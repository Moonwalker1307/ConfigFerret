using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.Model;
using ConfigFerret.Desktop.Services;
using ConfigFerret.GitHub;
using ConfigFerret.Tests.Shared;

namespace ConfigFerret.Desktop.Tests;

public class TraceRunnerTests
{
    private const string Token = "ghp" + "_testtoken0000000000000000000000000";

    [Fact]
    public void The_note_on_skipped_code_files_names_the_first_five()
    {
        Assert.Equal(
            "The search for the keys the app reads skipped A.cs, because it is over 1 MB, doesn't fit in the 64 MB of code the scan holds, comes past the first 5000 files, or took too long to read. A setting marked dead may still be read there.",
            TraceRunner.SkippedCodeNote(["A.cs"]));
        Assert.Equal(
            "The search for the keys the app reads skipped A.cs, because it is over 1 MB, doesn't fit in the 64 MB of code the scan holds, comes past the first 5000 files, or took too long to read. A setting marked dead may still be read there. The scan didn't look for the classes it declares, so every class counts as having any property.",
            TraceRunner.SkippedCodeNote(["A.cs"], allClassesOpen: true));
        Assert.Equal(
            "The search for the keys the app reads skipped F0.cs, F1.cs, F2.cs, F3.cs, F4.cs and 4995 more, because they are over 1 MB, don't fit in the 64 MB of code the scan holds, come past the first 5000 files, or took too long to read. A setting marked dead may still be read there. The scan didn't look for the classes they declare, so every class counts as having any property.",
            TraceRunner.SkippedCodeNote(Enumerable.Range(0, 5000).Select(i => $"F{i}.cs").ToList(), allClassesOpen: true));
        Assert.StartsWith(
            "The search for the keys the app reads skipped F0.cs, F1.cs, F2.cs, F3.cs, F4.cs and 99995 more, because they are",
            TraceRunner.SkippedCodeNote(Enumerable.Range(0, 1000).Select(i => $"F{i}.cs").ToList(), count: 100_000),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_note_on_project_references_not_followed_names_the_first_five()
    {
        Assert.Equal(
            "The search for the keys the app reads did not follow all project references: \"../P50/P50.csproj\" (past the first 50 projects). A setting marked dead may still be read there.",
            TraceRunner.SkippedReferencesNote(["\"../P50/P50.csproj\" (past the first 50 projects)"]));
        Assert.Equal(
            "The search for the keys the app reads did not follow all project references: \"P0\" (x), \"P1\" (x), \"P2\" (x), \"P3\" (x), \"P4\" (x) and 2 more. A setting marked dead may still be read there.",
            TraceRunner.SkippedReferencesNote(Enumerable.Range(0, 7).Select(i => $"\"P{i}\" (x)").ToList()));
    }

    [Fact]
    public async Task Traces_the_app_with_its_rendered_manifests()
    {
        var result = await new TraceRunner(new InMemoryCredentialStore()).RunAsync(OrdersProfile(), revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.All(result.Sources, s => Assert.Null(s.Problem));
        // The container's env wins over appsettings.json (localhost) and is linked back to the manifests.
        var host = result.Report.Find("Db:Host")!;
        Assert.Equal("orders-db.prod.svc", host.Value);
        // The production file wins over the base file, and the ConfigMap wins over both.
        Assert.Equal("60", result.Report.Find("Db:Timeout")!.Value);
        Assert.Equal("Warning", result.Report.Find("Logging:LogLevel:Default")!.Value);
        Assert.Equal("https://eu.api.example.test", result.Report.Find("Api:BaseUrl")!.Value);
    }

    [Fact]
    public async Task Hides_secret_values_unless_asked_but_audits_with_them()
    {
        var runner = new TraceRunner(new InMemoryCredentialStore());

        var hidden = await runner.RunAsync(OrdersProfile(), revealSecrets: false, TestContext.Current.CancellationToken);
        var revealed = await runner.RunAsync(OrdersProfile(), revealSecrets: true, TestContext.Current.CancellationToken);

        Assert.Equal("***", hidden.Report.Find("Payments:ApiKey")!.Value);
        Assert.Equal("Zx81kQp4LmT7vRw2Nc9YbH3s", revealed.Report.Find("Payments:ApiKey")!.Value);
        Assert.Equal(SecretRating.Exposed, hidden.Secrets.Single(s => s.Key == "Payments:ApiKey").Rating);
        // The Secret manifest in deploy/rendered.yaml holds the password, so it is exposed; with hidden values it would be a placeholder.
        Assert.Equal(SecretRating.Exposed, hidden.Secrets.Single(s => s.Key == "Db:Password").Rating);
        Assert.All(hidden.Secrets, s => Assert.DoesNotContain("Zx81kQp4LmT7vRw2Nc9YbH3s", s.Reason + s.Where, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reads_the_github_token_from_the_credential_store_and_sends_it_only_as_a_header()
    {
        var store = new InMemoryCredentialStore();
        var profile = OrdersProfile();
        var github = AddGitHub(profile);
        store.Write(github.CredentialAccount, Token);
        var handler = GitHubHandler();

        var result = await new TraceRunner(store, httpClientFactory: handler.Client).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.All(result.Sources, s => Assert.Null(s.Problem));
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(Token, r.Headers.Authorization?.Parameter);
            Assert.DoesNotContain(Token, r.RequestUri!.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Reads_the_github_token_from_an_environment_variable()
    {
        var profile = OrdersProfile();
        var github = AddGitHub(profile);
        github.CredentialSource = CredentialSource.EnvironmentVariable;
        github.CredentialVariable = " MY_GITHUB_TOKEN ";
        var asked = new List<string>();
        var handler = GitHubHandler();

        var runner = new TraceRunner(
            new InMemoryCredentialStore(),
            name =>
            {
                asked.Add(name);
                return name == "MY_GITHUB_TOKEN" ? Token : null;
            },
            handler.Client);
        var result = await runner.RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.Null(result.Sources.Single(s => s.Source == github).Problem);
        Assert.Equal(["MY_GITHUB_TOKEN"], asked.Distinct());
        Assert.All(handler.Requests, r => Assert.Equal(Token, r.Headers.Authorization?.Parameter));
    }

    [Fact]
    public async Task A_missing_stored_credential_is_a_problem_of_that_source_and_the_trace_still_succeeds()
    {
        var profile = OrdersProfile();
        var github = AddGitHub(profile);
        var handler = GitHubHandler();

        var result = await new TraceRunner(new InMemoryCredentialStore(), httpClientFactory: handler.Client).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.Contains("No token is saved", result.Sources.Single(s => s.Source == github).Problem, StringComparison.Ordinal);
        Assert.All(result.Sources.Where(s => s.Source != github), s => Assert.Null(s.Problem));
        Assert.Empty(handler.Requests);
        Assert.Equal("orders-db.prod.svc", result.Report.Find("Db:Host")!.Value);
    }

    [Fact]
    public async Task A_missing_environment_variable_is_a_problem_of_that_source()
    {
        var profile = OrdersProfile();
        var github = AddGitHub(profile);
        github.CredentialSource = CredentialSource.EnvironmentVariable;
        github.CredentialVariable = "MY_GITHUB_TOKEN";

        var result = await new TraceRunner(new InMemoryCredentialStore(), _ => null).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        var problem = result.Sources.Single(s => s.Source == github).Problem;
        Assert.Contains("MY_GITHUB_TOKEN is not set", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unnamed_environment_variable_is_a_problem_of_that_source()
    {
        var profile = OrdersProfile();
        var github = AddGitHub(profile);
        github.CredentialSource = CredentialSource.EnvironmentVariable;
        github.CredentialVariable = "  ";

        var result = await new TraceRunner(new InMemoryCredentialStore(), _ => Token).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.Contains("Name the environment variable", result.Sources.Single(s => s.Source == github).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_token_is_a_problem_of_that_source_and_the_message_has_no_token()
    {
        var profile = OrdersProfile();
        var github = AddGitHub(profile);
        var store = new InMemoryCredentialStore();
        store.Write(github.CredentialAccount, Token);
        var handler = new FakeHttpHandler();

        var result = await new TraceRunner(store, httpClientFactory: handler.Client).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        var problem = result.Sources.Single(s => s.Source == github).Problem;
        Assert.NotNull(problem);
        Assert.DoesNotContain(Token, problem, StringComparison.Ordinal);
        Assert.NotEmpty(result.Report.Settings);
    }

    [Fact]
    public async Task Lists_github_variables_that_no_setting_uses()
    {
        var profile = OrdersProfile();
        var github = AddGitHub(profile);
        var store = new InMemoryCredentialStore();
        store.Write(github.CredentialAccount, Token);

        var result = await new TraceRunner(store, httpClientFactory: GitHubHandler().Client).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        var names = result.UnusedVariables.Select(v => v.Name).ToList();
        Assert.Contains("LEGACY_ENDPOINT", names);
        Assert.Contains("OLD_DEPLOY_KEY", names);
        Assert.DoesNotContain("FEATURES_SEARCH", names);
        Assert.All(result.UnusedVariables, v => Assert.Equal("GitHub Actions", v.Tool));
        Assert.Contains("secret", result.UnusedVariables.Single(v => v.Name == "OLD_DEPLOY_KEY").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Traces_the_settings_a_workflow_sets_from_github_variables_when_nothing_else_sets_them()
    {
        using var repo = new TempFolder();
        repo.Write("src/Api/appsettings.json", """{ "Db": { "Host": "localhost", "Password": "" }, "Mode": "dev" }""");
        repo.Write(".github/workflows/deploy.yml", """
            jobs:
              deploy:
                env:
                  Db__Host: ${{ vars.DB_HOST }}
                  Db__Password: ${{ secrets.DB_PASSWORD }}
                  Mode: ${{ vars.MODE }}
                  NODE_VERSION: ${{ vars.NODE_VERSION }}
                steps:
                  - run: echo deploying
            """);
        var profile = new Profile { Name = "api", Mode = TraceMode.App };
        profile.Sources.Add(new AppSettingsSource { Name = "app", Folder = repo.Full("src/Api"), UseUserSecrets = false });
        var github = new GitHubSource { Name = "github", Repository = "contoso/api", Workflows = [repo.Full(".github/workflows/deploy.yml")] };
        profile.Sources.Add(github);
        var store = new InMemoryCredentialStore();
        store.Write(github.CredentialAccount, Token);
        const string api = "/repos/contoso/api";
        var handler = new FakeHttpHandler()
            .Respond($"{api}/actions/organization-variables?per_page=30&page=1", """{"total_count":0,"variables":[]}""")
            .Respond($"{api}/actions/organization-secrets?per_page=100&page=1", """{"total_count":0,"secrets":[]}""")
            .Respond($"{api}/actions/variables?per_page=30&page=1", """
                {"total_count":3,"variables":[
                  {"name":"DB_HOST","value":"db.prod.internal"},
                  {"name":"MODE","value":"prod"},
                  {"name":"NODE_VERSION","value":"22"}]}
                """)
            .Respond($"{api}/actions/secrets?per_page=100&page=1", """{"total_count":1,"secrets":[{"name":"DB_PASSWORD"}]}""");

        var result = await new TraceRunner(store, httpClientFactory: handler.Client).RunAsync(profile, revealSecrets: true, TestContext.Current.CancellationToken);

        Assert.All(result.Sources, s => Assert.Null(s.Problem));
        var host = result.Report.Find("Db:Host")!;
        Assert.Equal("db.prod.internal", host.Value);
        Assert.Contains(host.Steps.SelectMany(s => s.Origins), o => o.Reference is GitHubVariableRef { Name: "DB_HOST" });
        Assert.Equal("prod", result.Report.Find("Mode")!.Value);
        var password = result.Report.Find("Db:Password")!;
        Assert.Equal(TraceRunner.SecretStandIn, password.Value);
        Assert.Contains(password.Steps.SelectMany(s => s.Origins), o => o.Reference is GitHubVariableRef { Name: "DB_PASSWORD", Kind: GitHubVariableKind.Secret });
        Assert.Null(result.Report.Find("NODE_VERSION"));
        var unused = result.UnusedVariables.Select(v => v.Name).ToList();
        Assert.DoesNotContain("DB_HOST", unused);
        Assert.DoesNotContain("DB_PASSWORD", unused);
        // NODE_VERSION is not an app setting, but the workflow still uses it, so it is not dead.
        Assert.DoesNotContain("NODE_VERSION", unused);
        Assert.DoesNotContain(result.Secrets, s => s.Key == "Db:Password" && s.Rating == SecretRating.Exposed);
    }

    [Theory]
    [InlineData(true, false, "db.repo.internal")]
    [InlineData(false, true, "db.env.internal")]
    [InlineData(true, true, "db.env.internal")]
    public async Task Sets_a_workflow_setting_to_the_github_variable_that_wins(bool organization, bool environment, string expected)
    {
        using var repo = new TempFolder();
        repo.Write("src/Api/appsettings.json", """{ "Db": { "Host": "localhost" } }""");
        repo.Write(".github/workflows/deploy.yml", """
            jobs:
              deploy:
                environment: production
                env:
                  Db__Host: ${{ vars.DB_HOST }}
                steps:
                  - run: echo deploying
            """);
        var profile = new Profile { Name = "api", Mode = TraceMode.App };
        profile.Sources.Add(new AppSettingsSource { Name = "app", Folder = repo.Full("src/Api"), UseUserSecrets = false });
        var github = new GitHubSource
        {
            Name = "github",
            Repository = "contoso/api",
            Environment = environment ? "production" : string.Empty,
            Workflows = [repo.Full(".github/workflows/deploy.yml")],
        };
        profile.Sources.Add(github);
        var store = new InMemoryCredentialStore();
        store.Write(github.CredentialAccount, Token);
        const string api = "/repos/contoso/api";
        var handler = new FakeHttpHandler()
            .Respond($"{api}/actions/organization-variables?per_page=30&page=1", organization
                ? """{"total_count":1,"variables":[{"name":"DB_HOST","value":"db.org.internal"}]}"""
                : """{"total_count":0,"variables":[]}""")
            .Respond($"{api}/actions/organization-secrets?per_page=100&page=1", """{"total_count":0,"secrets":[]}""")
            .Respond($"{api}/actions/variables?per_page=30&page=1", """{"total_count":1,"variables":[{"name":"DB_HOST","value":"db.repo.internal"}]}""")
            .Respond($"{api}/actions/secrets?per_page=100&page=1", """{"total_count":0,"secrets":[]}""")
            .Respond($"{api}/environments/production/variables?per_page=30&page=1", """{"total_count":1,"variables":[{"name":"DB_HOST","value":"db.env.internal"}]}""")
            .Respond($"{api}/environments/production/secrets?per_page=100&page=1", """{"total_count":0,"secrets":[]}""");

        var result = await new TraceRunner(store, httpClientFactory: handler.Client).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.All(result.Sources, s => Assert.Null(s.Problem));
        var host = result.Report.Find("Db:Host")!;
        Assert.Equal(expected, host.Value);
        var winner = Assert.Single(host.Steps.SelectMany(s => s.Origins), o => o.IsEffective);
        Assert.Equal(expected, winner.Value);
        Assert.DoesNotContain("differs", winner.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Github_variables_a_workflow_uses_outside_app_settings_are_not_listed_as_unused()
    {
        using var repo = new TempFolder();
        repo.Write("src/Web/appsettings.json", """{ "ConnectionStrings": { "Db": "" } }""");
        repo.Write(".github/workflows/deploy.yml", """
            jobs:
              deploy:
                environment: production
                env:
                  ConnectionStrings__Db: ${{ secrets.ELWIZ_DB_CONNECTION_STRING }}
                steps:
                  - if: ${{ vars.DEPLOY_ENABLED == 'true' }}
                    uses: azure/webapps-deploy@v3
                    with:
                      app-name: ${{ vars.AZURE_WEBAPP_NAME }}
                      publish-profile: ${{ secrets.AzureAppService_PublishProfile_B980 }}
                  - run: ./notify.sh "${{ secrets['SLACK_WEBHOOK'] }}"
                  # - run: echo ${{ secrets.OLD_DEPLOY_KEY }}
            """);
        var profile = new Profile { Name = "web", Mode = TraceMode.App };
        profile.Sources.Add(new AppSettingsSource { Name = "app", Folder = repo.Full("src/Web"), UseUserSecrets = false });
        var github = new GitHubSource { Name = "github", Repository = "contoso/web", Environment = "production", Workflows = [repo.Full(".github/workflows/deploy.yml")] };
        profile.Sources.Add(github);
        var store = new InMemoryCredentialStore();
        store.Write(github.CredentialAccount, Token);
        const string api = "/repos/contoso/web";
        var handler = new FakeHttpHandler()
            .Respond($"{api}/actions/organization-variables?per_page=30&page=1", """{"total_count":0,"variables":[]}""")
            .Respond($"{api}/actions/organization-secrets?per_page=100&page=1", """{"total_count":0,"secrets":[]}""")
            .Respond($"{api}/actions/variables?per_page=30&page=1", """{"total_count":1,"variables":[{"name":"DEPLOY_ENABLED","value":"true"}]}""")
            .Respond($"{api}/actions/secrets?per_page=100&page=1", """
                {"total_count":3,"secrets":[
                  {"name":"AZUREAPPSERVICE_PUBLISHPROFILE_B980"},
                  {"name":"ELWIZ_DB_CONNECTION_STRING"},
                  {"name":"OLD_DEPLOY_KEY"}]}
                """)
            .Respond($"{api}/environments/production/variables?per_page=30&page=1", """
                {"total_count":2,"variables":[{"name":"AZURE_WEBAPP_NAME","value":"elwiz"},{"name":"UNUSED_NAME","value":"x"}]}
                """)
            .Respond($"{api}/environments/production/secrets?per_page=100&page=1", """
                {"total_count":3,"secrets":[{"name":"ELWIZ_DB_CONNECTION_STRING"},{"name":"SLACK_WEBHOOK"},{"name":"AZURE_WEBAPP_NAME"}]}
                """);

        var result = await new TraceRunner(store, httpClientFactory: handler.Client).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.All(result.Sources, s => Assert.Null(s.Problem));
        var unused = result.UnusedVariables.Select(v => (v.Name, v.Detail)).ToList();
        Assert.DoesNotContain(unused, u => u.Name is "AZUREAPPSERVICE_PUBLISHPROFILE_B980" or "ELWIZ_DB_CONNECTION_STRING" or "DEPLOY_ENABLED" or "SLACK_WEBHOOK");
        Assert.DoesNotContain(("AZURE_WEBAPP_NAME", "environment variable (production)"), unused);
        // The workflow reads vars.AZURE_WEBAPP_NAME, not the secret of the same name, and the commented-out line is not a use.
        Assert.Contains(("AZURE_WEBAPP_NAME", "environment secret (production)"), unused);
        Assert.Contains(("OLD_DEPLOY_KEY", "repository secret (contoso/web)"), unused);
        Assert.Contains(("UNUSED_NAME", "environment variable (production)"), unused);

        // The ones only the workflow uses are still listed, with where the workflow uses them.
        var workflow = result.WorkflowVariables.ToDictionary(v => (v.Name, v.Detail), v => v.UsedBy);
        Assert.Equal("deploy.yml line 10", workflow[("AZURE_WEBAPP_NAME", "environment variable (production)")]);
        Assert.Equal("deploy.yml line 11", workflow[("AZUREAPPSERVICE_PUBLISHPROFILE_B980", "repository secret (contoso/web)")]);
        Assert.Equal("deploy.yml line 7", workflow[("DEPLOY_ENABLED", "repository variable (contoso/web)")]);
        Assert.Equal("deploy.yml line 12", workflow[("SLACK_WEBHOOK", "environment secret (production)")]);
        // ConnectionStrings__Db is an app setting, so the trace links the connection string secret and it is not a workflow-only row.
        Assert.DoesNotContain(result.WorkflowVariables, v => v.Name == "ELWIZ_DB_CONNECTION_STRING" && v.Detail.StartsWith("environment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_workflow_entry_does_not_replace_a_value_the_manifests_set()
    {
        var profile = OrdersProfile();
        var github = AddGitHub(profile);
        var store = new InMemoryCredentialStore();
        store.Write(github.CredentialAccount, Token);

        var result = await new TraceRunner(store, httpClientFactory: GitHubHandler().Client).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.Equal("orders-db.prod.svc", result.Report.Find("Db:Host")!.Value);
        Assert.Equal("true", result.Report.Find("Features:Search")!.Value);
    }

    [Fact]
    public async Task Leaves_out_a_disabled_source()
    {
        var profile = OrdersProfile();
        var github = AddGitHub(profile);
        github.Enabled = false;
        var handler = GitHubHandler();

        var result = await new TraceRunner(new InMemoryCredentialStore(), httpClientFactory: handler.Client).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.Sources, s => s.Source == github);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_missing_values_file_is_a_problem_of_the_helm_source()
    {
        var profile = OrdersProfile();
        var helm = profile.Sources.OfType<HelmSource>().Single();
        helm.ValuesFiles = [OrdersRepo.Full("deploy/values-missing.yaml")];

        var result = await new TraceRunner(new InMemoryCredentialStore()).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.Contains("values-missing.yaml", result.Sources.Single(s => s.Source == helm).Problem, StringComparison.Ordinal);
        Assert.Equal("orders-db.prod.svc", result.Report.Find("Db:Host")!.Value);
    }

    [Fact]
    public async Task Adds_environment_variables_from_a_source_and_reports_a_bad_line()
    {
        var profile = OrdersProfile();
        var good = new EnvironmentVariablesSource { Name = "env", Variables = "ORDERS_Cache__Ttl=5\n" };
        var bad = new EnvironmentVariablesSource { Name = "bad env", Variables = "NOT A VARIABLE" };
        profile.Sources.Add(good);
        profile.Sources.Add(bad);

        var result = await new TraceRunner(new InMemoryCredentialStore()).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.Equal("5", result.Report.Find("Cache:Ttl")!.Value);
        Assert.Null(result.Sources.Single(s => s.Source == good).Problem);
        Assert.Contains("bad env line 1", result.Sources.Single(s => s.Source == bad).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Traces_a_container_from_the_manifests_alone()
    {
        var profile = new Profile { Mode = TraceMode.Container };
        profile.Sources.Add(new KubernetesSource { Paths = [OrdersRepo.Full("deploy/rendered.yaml")], Container = "orders" });

        var result = await new TraceRunner(new InMemoryCredentialStore()).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.Equal("orders-db.prod.svc", result.Report.Find("ORDERS_Db__Host")!.Value);
        Assert.Equal("***", result.Report.Find("ORDERS_Db__Password")!.Value);
    }

    [Fact]
    public async Task Rejects_a_container_trace_without_manifests()
    {
        var profile = new Profile { Mode = TraceMode.Container };

        await Assert.ThrowsAsync<TraceSetupException>(() => new TraceRunner(new InMemoryCredentialStore()).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_an_app_trace_without_app_settings()
    {
        var profile = new Profile();
        profile.Sources.Add(new KubernetesSource { Paths = [OrdersRepo.Full("deploy/rendered.yaml")] });

        var ex = await Assert.ThrowsAsync<TraceSetupException>(() => new TraceRunner(new InMemoryCredentialStore()).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken));

        Assert.Contains("App settings", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_two_kubernetes_sources()
    {
        var profile = OrdersProfile();
        profile.Sources.Add(new KubernetesSource { Paths = [OrdersRepo.Full("deploy/rendered.yaml")] });

        await Assert.ThrowsAsync<TraceSetupException>(() => new TraceRunner(new InMemoryCredentialStore()).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_kubernetes_source_with_a_missing_file()
    {
        var profile = OrdersProfile();
        profile.Sources.OfType<KubernetesSource>().Single().Paths = [OrdersRepo.Full("deploy/missing.yaml")];

        var ex = await Assert.ThrowsAsync<TraceSetupException>(() => new TraceRunner(new InMemoryCredentialStore()).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken));

        Assert.Contains("missing.yaml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stops_when_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TraceRunner(new InMemoryCredentialStore()).RunAsync(OrdersProfile(), revealSecrets: false, cancelled.Token));
    }

    [Fact]
    public void Reads_a_stored_credential_by_the_source_account()
    {
        var store = new InMemoryCredentialStore();
        var source = new GitHubSource { CredentialAccountName = "github" };
        store.Write("github", Token);

        Assert.Equal(Token, new TraceRunner(store).ReadCredential(source));
    }

    [Fact]
    public void Does_not_take_an_empty_stored_credential_for_set()
    {
        var store = new InMemoryCredentialStore();
        store.Write("github", string.Empty);

        Assert.Throws<TraceSetupException>(() => new TraceRunner(store).ReadCredential(new GitHubSource { CredentialAccountName = "github" }));
    }

    [Fact]
    public void Names_the_credential_store_when_an_environment_variable_is_missing()
    {
        var source = new OctopusSource { CredentialSource = CredentialSource.EnvironmentVariable, CredentialVariable = "OCTOPUS_API_KEY" };

        var ex = Assert.Throws<TraceSetupException>(() => new TraceRunner(new InMemoryCredentialStore(), _ => " ").ReadCredential(source));

        Assert.Contains("API key", ex.Message, StringComparison.Ordinal);
        Assert.Contains("memory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finds_dead_settings_and_helm_values()
    {
        var result = await new TraceRunner(new InMemoryCredentialStore()).RunAsync(OrdersProfile(), revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.CodeFilesScanned);
        Assert.Empty(result.DynamicReads);
        var dead = result.DeadSettings.ToDictionary(d => d.Key, d => d.Reason);
        // Payments:ApiKey is only read in commented-out code, and DbOptions has no Timeout property.
        Assert.Equal("No code reads it", dead["Payments:ApiKey"]);
        Assert.Equal("Db is bound to DbOptions, which has no property Timeout", dead["Db:Timeout"]);
        Assert.Contains(result.DeadSettings.Single(d => d.Key == "Db:Timeout").SetBy, s => s.Contains("appsettings.json", StringComparison.Ordinal));
        // Read by code, by binding or by the host.
        foreach (var key in new[] { "Db:Host", "Db:Port", "Db:Password", "Smtp:Password", "Cache:Ttl", "Features:Search", "Api:BaseUrl", "Logging:LogLevel:Default", "AllowedHosts" })
        {
            Assert.False(dead.ContainsKey(key), key);
        }

        Assert.Equal(dead.Keys.Order(), result.Unused.NotRead!.Select(s => s.Key).Order());
        Assert.Contains(result.DeadHelmValues, v => v.Path == "features.recommendations");
    }

    [Fact]
    public async Task Without_code_no_setting_is_called_dead()
    {
        using var temp = new TempFolder();
        temp.Write("app/appsettings.json", "{\"A\": {\"B\": 1}}");
        var profile = new Profile { Name = "no code" };
        profile.Sources.Add(new AppSettingsSource { Name = "app", Folder = temp.Full("app"), UseUserSecrets = false });

        var result = await new TraceRunner(new InMemoryCredentialStore()).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.CodeFilesScanned);
        Assert.Empty(result.DeadSettings);
        Assert.Null(result.Unused.NotRead);
    }

    [Fact]
    public async Task A_file_key_that_only_starts_with_the_prefix_is_dead_when_the_name_without_it_is_read()
    {
        using var temp = new TempFolder();
        temp.Write("app/appsettings.json", """{"ORDERS_Db": {"Timeout": 5}, "Db": {"Timeout": 5}}""");
        temp.Write("app/Program.cs", """
            builder.Configuration.AddEnvironmentVariables("ORDERS_");
            var timeout = builder.Configuration["Db:Timeout"];
            """);
        var profile = new Profile { Name = "leftover" };
        profile.Sources.Add(new AppSettingsSource { Name = "app", Folder = temp.Full("app"), UseUserSecrets = false, EnvironmentPrefixes = ["ORDERS_"] });

        var result = await new TraceRunner(new InMemoryCredentialStore()).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.CodeFilesScanned);
        Assert.Equal(["ORDERS_Db:Timeout"], result.DeadSettings.Select(d => d.Key));
    }

    private static Profile OrdersProfile()
    {
        var profile = new Profile { Name = "orders" };
        profile.Sources.Add(new AppSettingsSource
        {
            Name = "app",
            Folder = OrdersRepo.Full("src/Orders.Api"),
            EnvironmentName = "Production",
            UseUserSecrets = false,
            EnvironmentPrefixes = ["ORDERS_"],
        });
        profile.Sources.Add(new KubernetesSource { Name = "manifests", Paths = [OrdersRepo.Full("deploy/rendered.yaml")], Container = "orders" });
        profile.Sources.Add(new HelmSource
        {
            Name = "chart",
            ChartFolder = OrdersRepo.Full("deploy/chart"),
            ValuesFiles = [OrdersRepo.Full("deploy/values-prod.yaml")],
        });
        return profile;
    }

    private static GitHubSource AddGitHub(Profile profile)
    {
        var github = new GitHubSource
        {
            Name = "github",
            Repository = "contoso/orders",
            Workflows = [OrdersRepo.Full(".github/workflows/deploy.yml")],
        };
        profile.Sources.Add(github);
        return github;
    }

    private static FakeHttpHandler GitHubHandler()
    {
        const string repo = "/repos/contoso/orders";
        return new FakeHttpHandler()
            .Respond($"{repo}/actions/organization-variables?per_page=30&page=1", """{"total_count":0,"variables":[]}""")
            .Respond($"{repo}/actions/organization-secrets?per_page=100&page=1", """{"total_count":0,"secrets":[]}""")
            .Respond($"{repo}/actions/variables?per_page=30&page=1", """
                {"total_count":2,"variables":[
                  {"name":"FEATURES_SEARCH","value":"true","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"},
                  {"name":"LEGACY_ENDPOINT","value":"https://old.example.test","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}]}
                """)
            .Respond($"{repo}/actions/secrets?per_page=100&page=1", """{"total_count":1,"secrets":[{"name":"OLD_DEPLOY_KEY","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}]}""");
    }
}
