using System.Net;
using System.Text;
using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.Model;
using ConfigFerret.Desktop.Services;
using ConfigFerret.Tests.Shared;

namespace ConfigFerret.Desktop.Tests;

public class TraceRunnerSourcesTests : IDisposable
{
    private const string Token = "ghp" + "_testtoken0000000000000000000000000";
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("configferret-trace-");

    public void Dispose()
    {
        _temp.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Write(string relativePath, string text)
    {
        var path = Path.Join(_temp.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Dir(string relativePath) => Directory.CreateDirectory(Path.Join(_temp.FullName, relativePath)).FullName;

    private AppSettingsSource App(string json = """{"Db":{"Host":"base"}}""", string folder = "app", string environment = "Production", bool secrets = false)
    {
        Write($"{folder}/appsettings.json", json);
        return new AppSettingsSource { Name = "app", Folder = Path.Join(_temp.FullName, folder), EnvironmentName = environment, UseUserSecrets = secrets };
    }

    private static Profile ProfileOf(params SourceSettings[] sources)
    {
        var profile = new Profile();
        foreach (var source in sources)
        {
            profile.Sources.Add(source);
        }

        return profile;
    }

    private static Task<TraceResult> Run(Profile profile, Func<string, string?>? env = null, bool reveal = false, Func<HttpClient>? http = null, ICredentialStore? store = null) =>
        new TraceRunner(store ?? new InMemoryCredentialStore(), env ?? (_ => null), http).RunAsync(profile, reveal, TestContext.Current.CancellationToken);

    private static string? ProblemOf(TraceResult result, SourceSettings source) => result.Sources.Single(s => s.Source == source).Problem;

    // Profile and manifests

    [Fact]
    public async Task A_profile_is_needed()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => new TraceRunner(new InMemoryCredentialStore()).RunAsync(null!, false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_runner_needs_a_credential_store()
    {
        Assert.Throws<ArgumentNullException>(() => new TraceRunner(null!));
    }

    [Fact]
    public async Task Two_kubernetes_sources_are_refused_with_an_explanation_unless_one_is_disabled()
    {
        var one = new KubernetesSource { Paths = [Write("k8s/a.yaml", Pod("a"))] };
        var two = new KubernetesSource { Paths = [Write("k8s/b.yaml", Pod("b"))] };

        var ex = await Assert.ThrowsAsync<TraceSetupException>(() => Run(ProfileOf(App(), one, two)));
        Assert.Equal("A profile can use one Kubernetes source. Add all the manifest files to that one.", ex.Message);

        two.Enabled = false;
        var result = await Run(ProfileOf(App(), one, two));
        Assert.DoesNotContain(result.Sources, s => s.Source == two);
    }

    [Fact]
    public async Task A_kubernetes_source_needs_a_path_and_names_itself_in_the_error()
    {
        var ex = await Assert.ThrowsAsync<TraceSetupException>(() => Run(ProfileOf(App(), new KubernetesSource { Name = "my manifests" })));

        Assert.Equal("my manifests: add at least one manifest file or folder.", ex.Message);
    }

    [Fact]
    public async Task A_missing_manifest_path_is_named()
    {
        var missing = Path.Join(_temp.FullName, "nope.yaml");

        var ex = await Assert.ThrowsAsync<TraceSetupException>(() => Run(ProfileOf(App(), new KubernetesSource { Name = "k", Paths = [missing] })));

        Assert.Equal($"k: {missing} does not exist.", ex.Message);
    }

    [Fact]
    public async Task A_manifest_folder_is_accepted()
    {
        Write("k8s/a.yaml", Pod("a"));

        var result = await Run(ProfileOf(App(), new KubernetesSource { Paths = [Dir("k8s")] }));

        Assert.All(result.Sources, s => Assert.Null(s.Problem));
    }

    private static string Pod(string name, string env = "") =>
        $"apiVersion: v1\nkind: Pod\nmetadata:\n  name: {name}\nspec:\n  containers:\n    - name: {name}\n      image: {name}:1\n{env}";

    [Fact]
    public async Task A_container_trace_needs_a_kubernetes_source_and_says_what_for()
    {
        var ex = await Assert.ThrowsAsync<TraceSetupException>(() => Run(new Profile { Mode = TraceMode.Container }));

        Assert.Equal("Tracing a container needs the Kubernetes manifests with the workload and its ConfigMaps and Secrets. Turn them on in the Sources menu if they are off.", ex.Message);
    }

    [Fact]
    public async Task A_container_trace_leaves_out_app_and_environment_sources_and_says_so()
    {
        var k8s = new KubernetesSource { Paths = [Write("k8s/pod.yaml", Pod("web", "      env:\n        - name: A__B\n          value: one\n"))] };
        var app = App();
        var env = new EnvironmentVariablesSource { Variables = "X=1" };
        var helm = new HelmSource { ChartFolder = Path.Join(_temp.FullName, "missing") };
        var profile = ProfileOf(k8s, app, env, helm);
        profile.Mode = TraceMode.Container;

        var result = await Run(profile);

        const string reason = "Not used when tracing a container. Switch the profile to App to use it.";
        Assert.Equal(reason, ProblemOf(result, app));
        Assert.Equal(reason, ProblemOf(result, env));
        Assert.Null(ProblemOf(result, k8s));
        Assert.Contains("The chart folder", ProblemOf(result, helm), StringComparison.Ordinal);
        Assert.Equal("one", result.Report.Find("A__B")!.Value);
        Assert.Null(result.Report.Find("X"));
    }

    [Fact]
    public async Task A_container_trace_picks_the_named_workload_and_container_ignoring_blanks_around_the_names()
    {
        var yaml = Pod("one", "      env:\n        - name: FROM_ONE\n          value: x\n") + "---\n" + Pod("two", "      env:\n        - name: FROM_TWO\n          value: y\n");
        var k8s = new KubernetesSource { Paths = [Write("k8s/pods.yaml", yaml)], Workload = " two ", Container = "  " };
        var profile = ProfileOf(k8s);
        profile.Mode = TraceMode.Container;

        var result = await Run(profile);

        Assert.NotNull(result.Report.Find("FROM_TWO"));
        Assert.Null(result.Report.Find("FROM_ONE"));
    }

    // App settings

    [Fact]
    public async Task An_app_without_a_folder_or_with_a_missing_one_is_named_in_the_error()
    {
        var app = new AppSettingsSource { Name = "my app", Folder = "  " };
        var ex = await Assert.ThrowsAsync<TraceSetupException>(() => Run(ProfileOf(app)));
        Assert.Equal("my app: the app folder '  ' does not exist.", ex.Message);

        app.Folder = Path.Join(_temp.FullName, "nope");
        ex = await Assert.ThrowsAsync<TraceSetupException>(() => Run(ProfileOf(app)));
        Assert.Equal($"my app: the app folder '{app.Folder}' does not exist.", ex.Message);
    }

    [Fact]
    public async Task A_folder_without_settings_files_is_an_error_that_names_it()
    {
        var folder = Dir("empty");

        var ex = await Assert.ThrowsAsync<TraceSetupException>(() => Run(ProfileOf(new AppSettingsSource { Name = "app", Folder = folder })));

        Assert.Equal($"app: there is no appsettings.json in {folder}.", ex.Message);
    }

    [Fact]
    public async Task The_environment_file_wins_over_the_base_file()
    {
        var app = App("""{"Db":{"Host":"base","Port":"1"}}""", environment: "Staging");
        Write("app/appsettings.Staging.json", """{"Db":{"Host":"staging"}}""");
        Write("app/appsettings.Production.json", """{"Db":{"Host":"production"}}""");

        var result = await Run(ProfileOf(app));

        Assert.Equal("staging", result.Report.Find("Db:Host")!.Value);
        Assert.Equal("1", result.Report.Find("Db:Port")!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_environment_means_production(string environment)
    {
        var app = App("""{"Db":{"Host":"base"}}""", environment: environment);
        Write("app/appsettings.Production.json", """{"Db":{"Host":"production"}}""");

        var result = await Run(ProfileOf(app));

        Assert.Equal("production", result.Report.Find("Db:Host")!.Value);
    }

    [Fact]
    public async Task Blanks_around_the_environment_name_are_ignored()
    {
        var app = App(environment: "  Staging ");
        Write("app/appsettings.Staging.json", """{"Db":{"Host":"staging"}}""");

        Assert.Equal("staging", (await Run(ProfileOf(app))).Report.Find("Db:Host")!.Value);
    }

    [Fact]
    public async Task Only_an_environment_file_is_enough()
    {
        Dir("app");
        Write("app/appsettings.Production.json", """{"Db":{"Host":"production"}}""");

        var result = await Run(ProfileOf(new AppSettingsSource { Name = "app", Folder = Path.Join(_temp.FullName, "app") }));

        Assert.Equal("production", result.Report.Find("Db:Host")!.Value);
    }

    [Fact]
    public async Task Extra_settings_files_come_last_and_a_missing_one_is_named()
    {
        var app = App();
        app.ExtraFiles = [Write("extra/one.json", """{"Db":{"Host":"extra"}}""")];

        var result = await Run(ProfileOf(app));

        Assert.Equal("extra", result.Report.Find("Db:Host")!.Value);

        var missing = Path.Join(_temp.FullName, "extra", "gone.json");
        app.ExtraFiles = [missing];
        var ex = await Assert.ThrowsAsync<TraceSetupException>(() => Run(ProfileOf(app)));
        Assert.Equal($"app: the settings file '{missing}' does not exist.", ex.Message);
    }

    [Fact]
    public async Task A_broken_settings_file_stops_the_trace_with_a_message()
    {
        var app = App("{ not json");

        var ex = await Assert.ThrowsAsync<TraceSetupException>(() => Run(ProfileOf(app)));

        Assert.StartsWith("Could not read the app's settings: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Several_apps_are_layered_in_order()
    {
        var first = App("""{"Db":{"Host":"first"}}""", "one");
        var second = App("""{"Db":{"Host":"second"}}""", "two");

        var result = await Run(ProfileOf(first, second));

        Assert.Equal("second", result.Report.Find("Db:Host")!.Value);
        Assert.All(result.Sources, s => Assert.Null(s.Problem));
    }

    // User secrets

    private string UserSecretsHome(string id, string secretsJson)
    {
        var home = Dir("home");
        Write($"home/.microsoft/usersecrets/{id}/secrets.json", secretsJson);
        return home;
    }

    [Fact]
    public async Task Development_loads_the_apps_user_secrets_when_asked()
    {
        var app = App(environment: "Development", secrets: true);
        Write("app/App.csproj", "<Project><PropertyGroup><UserSecretsId>abc-123</UserSecretsId></PropertyGroup></Project>");
        var home = UserSecretsHome("abc-123", """{"Db":{"Host":"secret-host"}}""");

        var result = await Run(ProfileOf(app), name => name == "HOME" ? home : null, reveal: true);

        Assert.Equal("secret-host", result.Report.Find("Db:Host")!.Value);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public async Task User_secrets_are_left_out_unless_development_and_asked_for(string environment, bool useSecrets)
    {
        var app = App(environment: environment, secrets: useSecrets);
        Write("app/App.csproj", "<Project><PropertyGroup><UserSecretsId>abc-123</UserSecretsId></PropertyGroup></Project>");
        var home = UserSecretsHome("abc-123", """{"Db":{"Host":"secret-host"}}""");

        var result = await Run(ProfileOf(app), name => name == "HOME" ? home : null);

        Assert.Equal("base", result.Report.Find("Db:Host")!.Value);
    }

    [Fact]
    public async Task A_missing_user_secrets_file_is_not_an_error()
    {
        var app = App(environment: "Development", secrets: true);
        Write("app/App.csproj", "<Project><PropertyGroup><UserSecretsId>abc-123</UserSecretsId></PropertyGroup></Project>");

        var result = await Run(ProfileOf(app), name => name == "HOME" ? Dir("home") : null);

        Assert.Equal("base", result.Report.Find("Db:Host")!.Value);
    }

    [Fact]
    public void User_secrets_are_found_by_the_id_in_the_only_project_file()
    {
        var folder = Dir("proj");
        Write("proj/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><UserSecretsId> abc-123 </UserSecretsId></PropertyGroup></Project>");

        var path = UserSecrets.FindFile(folder, name => name == "HOME" ? "/home/me" : null);

        Assert.Equal(Path.Join("/home/me", ".microsoft", "usersecrets", "abc-123", "secrets.json"), path);
    }

    [Fact]
    public void User_secrets_use_the_home_folder_on_this_platform_even_when_appdata_is_set()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Dir("proj");
        Write("proj/App.csproj", "<Project><PropertyGroup><UserSecretsId>abc</UserSecretsId></PropertyGroup></Project>");

        var path = UserSecrets.FindFile(folder, name => name == "HOME" ? "/home/me" : "/appdata");

        Assert.StartsWith("/home/me", path, StringComparison.Ordinal);
    }

    [Fact]
    public void User_secrets_are_not_found_without_a_home_folder()
    {
        var folder = Dir("proj");
        Write("proj/App.csproj", "<Project><PropertyGroup><UserSecretsId>abc</UserSecretsId></PropertyGroup></Project>");

        Assert.Null(UserSecrets.FindFile(folder, _ => null));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(2, "<UserSecretsId>abc</UserSecretsId>")]
    public void User_secrets_need_exactly_one_project_file(int count, string content)
    {
        var folder = Dir("proj");
        for (var i = 0; i < count; i++)
        {
            Write($"proj/App{i}.csproj", $"<Project><PropertyGroup>{content}</PropertyGroup></Project>");
        }

        Assert.Null(UserSecrets.FindFile(folder, _ => "/home/me"));
    }

    [Theory]
    [InlineData("<Project><PropertyGroup></PropertyGroup></Project>")]
    [InlineData("<Project><PropertyGroup><UserSecretsId></UserSecretsId></PropertyGroup></Project>")]
    [InlineData("<Project><PropertyGroup><UserSecretsId>..</UserSecretsId></PropertyGroup></Project>")]
    [InlineData("<Project><PropertyGroup><UserSecretsId>.</UserSecretsId></PropertyGroup></Project>")]
    [InlineData("<Project><PropertyGroup><UserSecretsId>../../etc</UserSecretsId></PropertyGroup></Project>")]
    [InlineData("<Project><PropertyGroup><UserSecretsId>a b</UserSecretsId></PropertyGroup></Project>")]
    [InlineData("<Project><PropertyGroup><UserSecretsId>a/b</UserSecretsId></PropertyGroup></Project>")]
    [InlineData("<Project><PropertyGroup><UserSecretsId>a\\b</UserSecretsId></PropertyGroup></Project>")]
    [InlineData("<Project><not closed")]
    public void User_secrets_ids_that_are_missing_odd_or_could_leave_the_folder_are_refused(string project)
    {
        var folder = Dir("proj");
        Write("proj/App.csproj", project);

        Assert.Null(UserSecrets.FindFile(folder, _ => "/home/me"));
    }

    [Theory]
    [InlineData("0f3c1e5a-1111-2222-3333-444455556666")]
    [InlineData("my_app.secrets-1")]
    [InlineData("..a")]
    public void User_secrets_ids_may_hold_letters_digits_dots_underscores_and_dashes(string id)
    {
        var folder = Dir("proj");
        Write("proj/App.csproj", $"<Project><PropertyGroup><UserSecretsId>{id}</UserSecretsId></PropertyGroup></Project>");

        Assert.EndsWith(Path.Join(id, "secrets.json"), UserSecrets.FindFile(folder, _ => "/home/me"), StringComparison.Ordinal);
    }

    // Environment variables sources

    [Fact]
    public async Task Environment_variable_sources_add_a_file_and_then_typed_lines_and_later_sources_win()
    {
        var file = Write("env/.env", "export DB__HOST='from-file'\nDB__PORT=\"5\"\nDB__NAME=file\n");
        var first = new EnvironmentVariablesSource { Name = "first", File = file, Variables = "# comment\nDB__NAME=typed\n" };
        var second = new EnvironmentVariablesSource { Name = "second", Variables = "DB__PORT=6" };

        var result = await Run(ProfileOf(App("{}"), first, second));

        Assert.Equal("from-file", result.Report.Find("Db:Host")!.Value);
        Assert.Equal("typed", result.Report.Find("Db:Name")!.Value);
        Assert.Equal("6", result.Report.Find("Db:Port")!.Value);
        Assert.All(result.Sources, s => Assert.Null(s.Problem));
    }

    [Fact]
    public async Task A_blank_file_name_is_skipped()
    {
        var source = new EnvironmentVariablesSource { File = "  ", Variables = "A__B=1" };

        var result = await Run(ProfileOf(App("{}"), source));

        Assert.Null(ProblemOf(result, source));
        Assert.Equal("1", result.Report.Find("A:B")!.Value);
    }

    [Fact]
    public async Task A_missing_environment_file_is_a_problem_of_that_source()
    {
        var source = new EnvironmentVariablesSource { File = Path.Join(_temp.FullName, "missing.env") };

        var result = await Run(ProfileOf(App(), source));

        Assert.Contains("missing.env", ProblemOf(result, source), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_line_in_the_file_is_reported_with_the_file_name_and_line()
    {
        var source = new EnvironmentVariablesSource { File = Write("env/prod.env", "A=1\n\nNOPE\n") };

        var result = await Run(ProfileOf(App(), source));

        Assert.Equal("prod.env line 3: expected NAME=VALUE.", ProblemOf(result, source));
    }

    [Fact]
    public async Task Prefixes_of_all_apps_strip_the_variables_they_name_and_every_variable_still_counts()
    {
        var one = App("{}", "one");
        one.EnvironmentPrefixes = ["ORDERS_", "SHARED_"];
        var two = App("{}", "two");
        two.EnvironmentPrefixes = ["SHARED_", "BILLING_"];
        var env = new EnvironmentVariablesSource { Variables = "ORDERS_A=1\nSHARED_B=2\nBILLING_C=3\nOTHER_D=4\nPLAIN=5" };

        var result = await Run(ProfileOf(one, two, env));

        Assert.Equal("1", result.Report.Find("A")!.Value);
        Assert.Equal("2", result.Report.Find("B")!.Value);
        Assert.Equal("3", result.Report.Find("C")!.Value);
        Assert.Single(result.Report.Settings, s => s.Key == "B");

        // The host's unprefixed provider reads every variable under its whole name, as the .NET hosts do.
        Assert.Null(result.Report.Find("D"));
        Assert.Equal("4", result.Report.Find("OTHER_D")!.Value);
        Assert.Equal("5", result.Report.Find("PLAIN")!.Value);
        Assert.Equal("1", result.Report.Find("ORDERS_A")!.Value);
    }

    [Fact]
    public async Task A_prefix_the_app_strips_does_not_hide_the_containers_other_variables()
    {
        var app = App("""{"Db":{"Host":"localhost","Password":""}}""");
        app.EnvironmentPrefixes = ["CFG_"];
        var manifests = new KubernetesSource
        {
            Paths = [Write("k8s/orders.yaml", """
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: orders-config
                data:
                  Cache__Ttl: "60"
                ---
                apiVersion: v1
                kind: Secret
                metadata:
                  name: db-secret
                stringData:
                  password: fake-password-for-tests
                ---
                apiVersion: apps/v1
                kind: Deployment
                metadata:
                  name: orders
                spec:
                  template:
                    spec:
                      containers:
                        - name: orders
                          image: orders:1
                          env:
                            - name: Db__Host
                              value: prod-db.internal
                            - name: Db__Password
                              valueFrom:
                                secretKeyRef:
                                  name: db-secret
                                  key: password
                          envFrom:
                            - prefix: CFG_
                              configMapRef:
                                name: orders-config
                """)],
        };

        var result = await Run(ProfileOf(app, manifests));

        var host = result.Report.Find("Db:Host")!;
        Assert.Equal("prod-db.internal", host.Value);
        Assert.DoesNotContain("appsettings", host.EffectiveStep!.Source, StringComparison.Ordinal);
        Assert.Contains(host.EffectiveStep.Origins, o => o.Kind == LayerKind.KubernetesWorkload);
        Assert.Contains(result.Report.Find("Db:Password")!.EffectiveStep!.Origins, o => o.Kind == LayerKind.KubernetesSecret);
        Assert.Equal("60", result.Report.Find("Cache:Ttl")!.Value);
    }

    [Fact]
    public async Task Without_prefixes_every_variable_counts()
    {
        var env = new EnvironmentVariablesSource { Variables = "PLAIN=5\nA__B=6" };

        var result = await Run(ProfileOf(App("{}"), env));

        Assert.Equal("5", result.Report.Find("PLAIN")!.Value);
        Assert.Equal("6", result.Report.Find("A:B")!.Value);
    }

    [Fact]
    public async Task The_process_environment_does_not_leak_in()
    {
        var result = await Run(ProfileOf(App("{}")), name => "leaked");

        Assert.Null(result.Report.Find("PATH"));
        Assert.Null(result.Report.Find("HOME"));
    }

    // Manifests whose values are not known

    private const string UnknownEnv =
        "      env:\n"
        + "        - name: ZED\n          valueFrom:\n            secretKeyRef:\n              name: absent\n              key: k\n"
        + "        - name: ALPHA\n          valueFrom:\n            configMapKeyRef:\n              name: absent\n              key: k\n"
        + "        - name: KNOWN\n          value: yes\n";

    [Fact]
    public async Task Values_the_manifests_do_not_hold_are_listed_in_a_note_in_name_order_and_kept_with_a_stand_in()
    {
        var k8s = new KubernetesSource { Paths = [Write("k8s/pod.yaml", Pod("web", UnknownEnv))] };

        var result = await Run(ProfileOf(App("{}"), k8s));

        var note = Assert.Single(result.Notes);
        Assert.Equal(
            "The manifests don't hold the value of ALPHA, ZED (set at run time, or from a ConfigMap or Secret that isn't in the manifests), so they show (value not in the manifests) instead. Add them in an Environment variables source to trace their values.",
            note);
        Assert.Equal("yes", result.Report.Find("KNOWN")!.Value);
        Assert.Equal("(value not in the manifests)", result.Report.Find("ALPHA")!.Value);
        Assert.Contains(result.Report.Find("ALPHA")!.EffectiveStep!.Origins, o => o.Kind == LayerKind.KubernetesConfigMap);
    }

    [Theory]
    [InlineData("")]
    [InlineData("apiVersion: external-secrets.io/v1beta1\nkind: ExternalSecret\nmetadata:\n  name: db\nspec:\n  secretStoreRef:\n    name: vault\n    kind: ClusterSecretStore\n  target:\n    name: db-secret\n  data:\n    - secretKey: password\n      remoteRef:\n        key: orders/db\n        property: password\n---\n")]
    public async Task A_setting_from_a_secret_outside_the_repository_wins_over_appsettings_and_is_protected(string externalSecret)
    {
        var k8s = new KubernetesSource { Paths = [Write("k8s/orders.yaml", externalSecret + DeploymentUsingSecret("db-secret"))] };

        var result = await Run(ProfileOf(App("""{"Db":{"Password":""}}"""), k8s));

        var password = result.Report.Find("Db:Password")!;
        Assert.DoesNotContain("appsettings", password.EffectiveStep!.Source, StringComparison.Ordinal);
        Assert.Contains(AllSteps(password.Steps), s => s.Kind == LayerKind.KubernetesSecret && s.Source.Contains("db-secret", StringComparison.Ordinal));
        var finding = result.Secrets.Single(s => s.Key == "Db:Password");
        Assert.Equal(SecretRating.Protected, finding.Rating);
        Assert.Contains("db-secret", finding.Reason + finding.Where, StringComparison.Ordinal);
    }

    // The environment the container sets

    private AppSettingsSource ThreeEnvironments(bool isDefault)
    {
        var app = App("""{"Db":{"Timeout":"5"}}""", environment: "Production");
        Write("app/appsettings.Production.json", """{"Db":{"Timeout":"60"}}""");
        Write("app/appsettings.Staging.json", """{"Db":{"Timeout":"15"}}""");
        app.EnvironmentNameIsDefault = isDefault;
        return app;
    }

    private KubernetesSource ContainerSetting(string variables) =>
        new() { Paths = [Write("k8s/pod.yaml", Pod("web", "      env:\n" + variables))] };

    private static string Variable(string name, string value) => $"        - name: {name}\n          value: \"{value}\"\n";

    [Fact]
    public async Task The_environment_the_container_sets_wins_over_a_default_one_with_a_note()
    {
        var result = await Run(ProfileOf(ThreeEnvironments(isDefault: true), ContainerSetting(Variable("ASPNETCORE_ENVIRONMENT", "Staging"))));

        var timeout = result.Report.Find("Db:Timeout")!;
        Assert.Equal("15", timeout.Value);
        Assert.Contains("appsettings.Staging.json", timeout.EffectiveStep!.Source, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_ENVIRONMENT is Staging in the app's environment (DOTNET_ENVIRONMENT before ASPNETCORE_ENVIRONMENT, as WebApplication.CreateBuilder reads them on .NET 7 and later), so app is traced in Staging, not Production. Pick an environment to trace another one.", result.Notes);
    }

    [Fact]
    public async Task A_chosen_environment_is_kept_with_a_note_when_the_container_sets_another()
    {
        var result = await Run(ProfileOf(ThreeEnvironments(isDefault: false), ContainerSetting(Variable("ASPNETCORE_ENVIRONMENT", "Staging"))));

        Assert.Equal("60", result.Report.Find("Db:Timeout")!.Value);
        Assert.Contains("ASPNETCORE_ENVIRONMENT is Staging in the app's environment (DOTNET_ENVIRONMENT before ASPNETCORE_ENVIRONMENT, as WebApplication.CreateBuilder reads them on .NET 7 and later), but app is traced in Production, the environment picked. The app reads appsettings.Staging.json.", result.Notes);
    }

    [Fact]
    public async Task Dotnet_environment_wins_over_aspnetcore_environment_as_in_a_web_app()
    {
        var variables = Variable("ASPNETCORE_ENVIRONMENT", "Production") + Variable("DOTNET_ENVIRONMENT", "Staging");

        var result = await Run(ProfileOf(ThreeEnvironments(isDefault: true), ContainerSetting(variables)));

        Assert.Equal("15", result.Report.Find("Db:Timeout")!.Value);
    }

    [Fact]
    public async Task No_note_when_the_container_sets_the_same_environment()
    {
        var result = await Run(ProfileOf(ThreeEnvironments(isDefault: false), ContainerSetting(Variable("ASPNETCORE_ENVIRONMENT", "Production"))));

        Assert.Equal("60", result.Report.Find("Db:Timeout")!.Value);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task An_environment_that_differs_only_in_case_gets_a_note_about_the_file_name()
    {
        var result = await Run(ProfileOf(ThreeEnvironments(isDefault: true), ContainerSetting(Variable("ASPNETCORE_ENVIRONMENT", "production"))));

        Assert.Equal("60", result.Report.Find("Db:Timeout")!.Value);
        Assert.Equal(
            ["ASPNETCORE_ENVIRONMENT is production in the app's environment, and app is traced with appsettings.Production.json. A Linux container looks for appsettings.production.json, with the name as written."],
            result.Notes);
    }

    [Fact]
    public async Task An_environment_that_differs_only_in_case_gets_no_note_when_the_app_has_no_file_for_it()
    {
        var app = App("""{"Db":{"Timeout":"5"}}""", environment: "Production");
        Write("app/appsettings.Development.json", """{"Db":{"Timeout":"1"}}""");
        app.EnvironmentNameIsDefault = true;

        var result = await Run(ProfileOf(app, ContainerSetting(Variable("ASPNETCORE_ENVIRONMENT", "production"))));

        Assert.Equal("5", result.Report.Find("Db:Timeout")!.Value);
        Assert.Empty(result.Notes);
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("Staging/../../x")]
    [InlineData(" Staging")]
    [InlineData("Stag.ing")]
    public async Task An_environment_name_that_could_pick_another_file_is_not_used(string name)
    {
        var result = await Run(ProfileOf(ThreeEnvironments(isDefault: true), ContainerSetting(Variable("ASPNETCORE_ENVIRONMENT", name))));

        Assert.Equal("60", result.Report.Find("Db:Timeout")!.Value);
        Assert.Contains("ASPNETCORE_ENVIRONMENT in the app's environment is not a name that picks a settings file in the app's folder, so app is traced in Production.", result.Notes);
    }

    [Fact]
    public async Task An_environment_whose_value_is_not_in_the_manifests_is_not_used_and_named_in_a_note()
    {
        var variables = "        - name: ASPNETCORE_ENVIRONMENT\n          valueFrom:\n            configMapKeyRef:\n              name: absent\n              key: env\n";

        var result = await Run(ProfileOf(ThreeEnvironments(isDefault: true), ContainerSetting(variables)));

        Assert.Equal("60", result.Report.Find("Db:Timeout")!.Value);
        Assert.Equal("Production", result.Environment);
        Assert.Contains("ASPNETCORE_ENVIRONMENT in the app's environment has no value in the repository, so app is traced in Production, which may not be the environment the app runs in.", result.Notes);
    }

    [Fact]
    public async Task The_first_environment_variable_set_decides_even_when_its_value_is_not_known()
    {
        // DOTNET_ENVIRONMENT wins in the app, so its unknown value is what the app runs in, not ASPNETCORE_ENVIRONMENT's.
        var variables = "        - name: DOTNET_ENVIRONMENT\n          valueFrom:\n            configMapKeyRef:\n              name: absent\n              key: env\n"
            + Variable("ASPNETCORE_ENVIRONMENT", "Staging");

        var result = await Run(ProfileOf(ThreeEnvironments(isDefault: true), ContainerSetting(variables)));

        Assert.Equal("60", result.Report.Find("Db:Timeout")!.Value);
        Assert.Contains(result.Notes, n => n.StartsWith("DOTNET_ENVIRONMENT in the app's environment has no value in the repository", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_environment_name_with_a_line_break_from_a_block_scalar_is_not_used()
    {
        var variables = "        - name: ASPNETCORE_ENVIRONMENT\n          value: |\n            Staging\n";

        var result = await Run(ProfileOf(ThreeEnvironments(isDefault: true), ContainerSetting(variables)));

        Assert.Equal("60", result.Report.Find("Db:Timeout")!.Value);
        Assert.Equal("Production", result.Environment);
        Assert.Contains("ASPNETCORE_ENVIRONMENT in the app's environment is not a name that picks a settings file in the app's folder, so app is traced in Production.", result.Notes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_environment_from_a_secret_is_never_used_or_shown(bool reveal)
    {
        const string fromSecret = "Qa7FromTheSecret";
        Write($"app/appsettings.{fromSecret}.json", """{"Db":{"Timeout":"7"}}""");
        var yaml = $"apiVersion: v1\nkind: Secret\nmetadata:\n  name: env-secret\nstringData:\n  env: {fromSecret}\n---\n"
            + Pod("web", "      env:\n        - name: ASPNETCORE_ENVIRONMENT\n          valueFrom:\n            secretKeyRef:\n              name: env-secret\n              key: env\n");
        var k8s = new KubernetesSource { Paths = [Write("k8s/all.yaml", yaml)] };

        var result = await Run(ProfileOf(ThreeEnvironments(isDefault: true), k8s), reveal: reveal);

        Assert.Equal("60", result.Report.Find("Db:Timeout")!.Value);
        Assert.Equal("Production", result.Environment);
        Assert.Contains("ASPNETCORE_ENVIRONMENT in the app's environment comes from a Secret or is sensitive, so its value is not used or shown, and app is traced in Production.", result.Notes);
        Assert.All(result.Notes, n => Assert.DoesNotContain(fromSecret, n, StringComparison.Ordinal));
        var shown = result.Report.Settings.Where(s => !reveal || s.Key != "ASPNETCORE_ENVIRONMENT").SelectMany(s => AllSteps(s.Steps)).SelectMany(s => new[] { s.Source, s.Location, s.Note, s.Value });
        Assert.All(shown, text => Assert.DoesNotContain(fromSecret, text ?? string.Empty, StringComparison.Ordinal));
        Assert.All(result.Sources, s => Assert.DoesNotContain(fromSecret, s.Source.Name + s.Problem, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_environment_that_looks_like_a_credential_is_never_used_or_shown(bool reveal)
    {
        const string random = "Q7rT2vX9kL4mN8pZ3wY6bH1j";
        Write($"app/appsettings.{random}.json", """{"Db":{"Timeout":"7"}}""");

        var result = await Run(ProfileOf(ThreeEnvironments(isDefault: true), ContainerSetting(Variable("DOTNET_ENVIRONMENT", random))), reveal: reveal);

        Assert.Equal("60", result.Report.Find("Db:Timeout")!.Value);
        Assert.Equal("Production", result.Environment);
        Assert.Contains("DOTNET_ENVIRONMENT in the app's environment comes from a Secret or is sensitive, so its value is not used or shown, and app is traced in Production.", result.Notes);
        Assert.All(result.Notes, n => Assert.DoesNotContain(random, n, StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_note_when_every_value_is_known()
    {
        var k8s = new KubernetesSource { Paths = [Write("k8s/pod.yaml", Pod("web", "      env:\n        - name: KNOWN\n          value: yes\n"))] };

        var result = await Run(ProfileOf(App("{}"), k8s));

        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task An_environment_variables_source_can_supply_what_the_manifests_lack()
    {
        var k8s = new KubernetesSource { Paths = [Write("k8s/pod.yaml", Pod("web", UnknownEnv))] };
        var env = new EnvironmentVariablesSource { Variables = "ALPHA=a\nZED=z" };

        var result = await Run(ProfileOf(App("{}"), k8s, env));

        Assert.Empty(result.Notes);
        Assert.Equal("a", result.Report.Find("ALPHA")!.Value);
    }

    [Fact]
    public async Task Secret_values_in_the_manifests_reach_the_audit_even_when_hidden()
    {
        var yaml = "apiVersion: v1\nkind: Secret\nmetadata:\n  name: db\ndata:\n  password: " + Convert.ToBase64String(Encoding.UTF8.GetBytes("hunter2-prod")) + "\n---\n"
            + Pod("web", "      env:\n        - name: Db__Password\n          valueFrom:\n            secretKeyRef:\n              name: db\n              key: password\n");
        var k8s = new KubernetesSource { Paths = [Write("k8s/all.yaml", yaml)] };
        var app = App("{}");

        var hidden = await Run(ProfileOf(app, k8s));
        var revealed = await Run(ProfileOf(app, k8s), reveal: true);

        Assert.Equal("***", hidden.Report.Find("Db:Password")!.Value);
        Assert.Equal("hunter2-prod", revealed.Report.Find("Db:Password")!.Value);
        // Only the real value makes the committed Secret exposed; the hidden *** would count as a placeholder.
        Assert.Equal(SecretRating.Exposed, hidden.Secrets.Single(s => s.Key == "Db:Password").Rating);
    }

    [Fact]
    public async Task An_app_settings_file_that_links_out_of_the_repository_is_not_read()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs extra rights on Windows.");
        var outside = Write("outside/settings.json", """{"Leak": {"Value": "fake-outside-value"}}""");
        var app = App("""{"Db": {"Host": "base"}}""", folder: "repo/app");
        File.CreateSymbolicLink(Path.Join(app.Folder, "appsettings.Production.json"), outside);
        var profile = ProfileOf(app);
        profile.RepositoryRoot = Path.Join(_temp.FullName, "repo");

        var result = await Run(profile);

        Assert.Equal("base", result.Report.Find("Db:Host")!.Value);
        Assert.Null(result.Report.Find("Leak:Value"));
        Assert.Contains("app/appsettings.Production.json is a symbolic link that leads out of the repository, so it was not read.", result.Notes);
    }

    [Fact]
    public async Task An_app_whose_only_settings_file_links_out_of_the_repository_is_not_traced()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs extra rights on Windows.");
        var outside = Write("outside/settings.json", """{"Leak": {"Value": "fake-outside-value"}}""");
        var folder = Dir("repo/app");
        File.CreateSymbolicLink(Path.Join(folder, "appsettings.json"), outside);
        var profile = ProfileOf(new AppSettingsSource { Name = "app", Folder = folder, UseUserSecrets = false });
        profile.RepositoryRoot = Path.Join(_temp.FullName, "repo");

        var ex = await Assert.ThrowsAsync<TraceSetupException>(() => Run(profile));

        Assert.Equal("app: app/appsettings.json is a symbolic link that leads out of the repository, so it was not read.", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_committed_password_next_to_a_reference_to_a_secret_is_exposed(bool secretInRepository)
    {
        const string password = "hunter2-fake-committed";
        var fromSecret = "        - name: TENANT\n          valueFrom:\n            secretKeyRef:\n              name: tenant\n              key: tenant\n";
        var k8s = ContainerSetting(fromSecret + Variable("Db__ConnectionString", $"Server=db;Password={password};Tenant=$(TENANT)"));
        if (secretInRepository)
        {
            k8s.Paths.Add(Write("k8s/secret.yaml", "apiVersion: v1\nkind: Secret\nmetadata:\n  name: tenant\nstringData:\n  tenant: fake-tenant\n"));
        }

        var result = await Run(ProfileOf(App("{}"), k8s));

        var finding = result.Secrets.Single(s => s.Key == "Db:ConnectionString");
        Assert.Equal(SecretRating.Exposed, finding.Rating);
        Assert.DoesNotContain(password, finding.Reason + finding.Where, StringComparison.Ordinal);
        Assert.DoesNotContain(ValuesOf(result), v => v?.Contains(password, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result.Notes, n => n.Contains(password, StringComparison.Ordinal));
    }

    private const string FakeAccountKey = "hunter2-fake-account-key";

    private const string FakeGitHubToken = "ghp" + "_FakeTokenForTestsOnly000000000000";

    // Neither key looks secret, but both values look like credentials: a connection string with an AccountKey and a
    // GitHub token. Deploy:Header carries the token inside a longer value, which does not look like a credential.
    private Profile CredentialShapedValues()
    {
        var app = App($$$"""{"AzureWebJobsStorage": "DefaultEndpointsProtocol=https;AccountName=fake;AccountKey={{{FakeAccountKey}}};EndpointSuffix=core.windows.net", "Db": {"Host": "db.example.test"}}""");
        var k8s = ContainerSetting(Variable("Deploy__Ref", FakeGitHubToken) + Variable("Deploy__Header", "Basic $(Deploy__Ref)"));
        return ProfileOf(app, k8s);
    }

    private static IEnumerable<string?> ValuesOf(TraceResult result)
    {
        foreach (var setting in result.Report.Settings)
        {
            yield return setting.Value;
            foreach (var value in setting.Steps.SelectMany(Values))
            {
                yield return value;
            }
        }

        static IEnumerable<string?> Values(ProvenanceStep step) => step.Origins.SelectMany(Values).Prepend(step.Value);
    }

    [Fact]
    public async Task A_value_that_looks_like_a_credential_is_hidden_everywhere_until_secrets_are_revealed()
    {
        var hidden = await Run(CredentialShapedValues());

        Assert.Equal(SecretRating.Exposed, hidden.Secrets.Single(s => s.Key == "AzureWebJobsStorage").Rating);
        Assert.Equal(SecretRating.Exposed, hidden.Secrets.Single(s => s.Key == "Deploy:Ref").Rating);
        foreach (var key in new[] { "AzureWebJobsStorage", "Deploy:Ref", "Deploy:Header" })
        {
            var setting = hidden.Report.Find(key)!;
            Assert.Equal("***", setting.Value);
            Assert.True(setting.IsSensitive, key);
        }

        Assert.Equal("db.example.test", hidden.Report.Find("Db:Host")!.Value);
        foreach (var text in new[] { FakeAccountKey, FakeGitHubToken })
        {
            Assert.DoesNotContain(ValuesOf(hidden), v => v?.Contains(text, StringComparison.Ordinal) == true);
            Assert.DoesNotContain(text, hidden.Report.ToJson(), StringComparison.Ordinal);
            Assert.DoesNotContain(text, hidden.Report.ToText(), StringComparison.Ordinal);
            Assert.DoesNotContain(hidden.Notes.Concat(hidden.Report.Warnings), n => n.Contains(text, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_value_that_looks_like_a_credential_is_shown_when_secrets_are_revealed()
    {
        var revealed = await Run(CredentialShapedValues(), reveal: true);

        Assert.Contains(FakeAccountKey, revealed.Report.Find("AzureWebJobsStorage")!.Value, StringComparison.Ordinal);
        Assert.Equal(FakeGitHubToken, revealed.Report.Find("Deploy:Ref")!.Value);
        Assert.Equal("Basic " + FakeGitHubToken, revealed.Report.Find("Deploy:Header")!.Value);
        Assert.False(revealed.Report.Find("AzureWebJobsStorage")!.IsSensitive);
    }

    // The secrets audit of a scanned repository

    private const string CommittedPassword = "Fake-Committed-Passw0rd-42";

    private static readonly AnalysisConnections NoConnections = new(null, string.Empty, "github", string.Empty, string.Empty, "octopus");

    private static string Encoded(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string DeploymentUsingSecret(string secretName) =>
        "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: orders\nspec:\n  template:\n    spec:\n      containers:\n        - name: orders\n          image: orders:1\n"
        + $"          env:\n            - name: Db__Password\n              valueFrom:\n                secretKeyRef:\n                  name: {secretName}\n                  key: password\n";

    // Traces the folder repo the way the app does: scan it, pick the choices, build the profile and run it.
    private async Task<TraceResult> TraceRepository(Func<string, IReadOnlyList<string>, string?>? render = null, string? kustomization = null)
    {
        var scan = RepositoryScanner.Scan(Path.Join(_temp.FullName, "repo"));
        var choices = new AnalysisChoices { Kustomization = kustomization };
        RepositoryAnalyzer.SuggestChoices(scan, choices, []);
        var profile = new RepositoryAnalyzer(render ?? ((_, _) => null)).BuildProfile(scan, choices, NoConnections, []);
        return await Run(profile);
    }

    private static void AssertHoldsNoValue(SecretFinding finding)
    {
        foreach (var text in new[] { CommittedPassword, Encoded(CommittedPassword) })
        {
            Assert.DoesNotContain(text, finding.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(text, finding.Where, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("stringData", false)]
    [InlineData("data", true)]
    public async Task A_value_in_a_secret_manifest_in_the_repository_is_exposed(string field, bool encoded)
    {
        Write("repo/src/Orders/appsettings.json", """{"Db":{"Password":""}}""");
        Write("repo/k8s/deployment.yaml", DeploymentUsingSecret("db-secret"));
        Write("repo/k8s/secret.yaml", $"apiVersion: v1\nkind: Secret\nmetadata:\n  name: db-secret\n{field}:\n  password: {(encoded ? Encoded(CommittedPassword) : CommittedPassword)}\n");

        var result = await TraceRepository();

        var finding = result.Secrets.Single(s => s.Key == "Db:Password");
        Assert.Equal(SecretRating.Exposed, finding.Rating);
        Assert.Equal("Secret db-secret (key password, secret.yaml:6)", finding.Where);
        Assert.Equal("The value is kept in plain text or base64 in the manifest of Secret db-secret, so anyone who can read that file can decode it. Fill the Secret from a secret store instead (an ExternalSecret or the Secrets Store CSI driver), or encrypt it (a SealedSecret or SOPS).", finding.Reason);
        AssertHoldsNoValue(finding);
    }

    [Fact]
    public async Task A_secret_generator_literal_is_exposed_and_named_when_kubectl_builds_the_overlay()
    {
        Write("repo/src/Orders/appsettings.json", """{"Db":{"Password":""}}""");
        Write("repo/deploy/base/kustomization.yaml", "resources:\n  - deployment.yaml\n");
        Write("repo/deploy/base/deployment.yaml", DeploymentUsingSecret("db-secret"));
        Write("repo/deploy/overlays/production/kustomization.yaml", $"resources:\n  - ../../base\nsecretGenerator:\n  - name: db-secret\n    literals:\n      - password={CommittedPassword}\n");
        var built = $"apiVersion: v1\nkind: Secret\nmetadata:\n  name: db-secret-5f2k9m7c4t\ndata:\n  password: {Encoded(CommittedPassword)}\ntype: Opaque\n---\n" + DeploymentUsingSecret("db-secret-5f2k9m7c4t");

        var result = await TraceRepository((tool, _) => tool == "kubectl" ? built : null, "deploy/overlays/production");

        var finding = result.Secrets.Single(s => s.Key == "Db:Password");
        Assert.Equal(SecretRating.Exposed, finding.Rating);
        Assert.Equal("production/kustomization.yaml (line 6, secretGenerator db-secret)", finding.Where);
        Assert.Equal("The value is kept in plain text in the secretGenerator of production/kustomization.yaml, so anyone who can read that file can decode it. Fill the Secret from a secret store instead (an ExternalSecret or the Secrets Store CSI driver), or encrypt it (a SealedSecret or SOPS).", finding.Reason);
        AssertHoldsNoValue(finding);
    }

    [Fact]
    public async Task A_secret_generator_literal_is_exposed_when_the_overlay_is_not_built()
    {
        // Without an app the container is traced, so the env entry keeps its link to the generated Secret.
        Write("repo/deploy/base/kustomization.yaml", "resources:\n  - deployment.yaml\n");
        Write("repo/deploy/base/deployment.yaml", DeploymentUsingSecret("db-secret"));
        Write("repo/deploy/overlays/production/kustomization.yaml", $"resources:\n  - ../../base\nsecretGenerator:\n  - name: db-secret\n    literals:\n      - password={CommittedPassword}\n");

        var result = await TraceRepository(kustomization: "deploy/overlays/production");

        var finding = result.Secrets.Single(s => s.Key == "Db__Password");
        Assert.Equal(SecretRating.Exposed, finding.Rating);
        Assert.Equal("production/kustomization.yaml (line 6, secretGenerator db-secret)", finding.Where);
        AssertHoldsNoValue(finding);
    }

    [Fact]
    public async Task A_committed_manifest_named_like_rendered_output_is_rated_as_a_file_in_the_repository()
    {
        Write("repo/src/Orders/appsettings.json", """{"Db":{"Password":""}}""");
        Write("repo/deploy/helm-template.yaml", $"apiVersion: v1\nkind: Secret\nmetadata:\n  name: db-secret\nstringData:\n  password: {CommittedPassword}\n---\n" + DeploymentUsingSecret("db-secret"));

        var result = await TraceRepository();

        var finding = result.Secrets.Single(s => s.Key == "Db:Password");
        Assert.Equal(SecretRating.Exposed, finding.Rating);
        Assert.Equal("Secret db-secret (key password, helm-template.yaml:6)", finding.Where);
        Assert.StartsWith("The value is kept in plain text or base64 in the manifest of Secret db-secret, so anyone who can read that file can decode it.", finding.Reason, StringComparison.Ordinal);
        AssertHoldsNoValue(finding);
    }

    [Fact]
    public async Task A_manifest_file_is_rated_as_a_file_even_in_a_source_named_like_rendered_output()
    {
        // Only text the analyzer rendered is handed over in memory; a path is always a file, whatever the source is called.
        Write("repo/src/Orders/appsettings.json", """{"Db":{"Password":""}}""");
        Write("repo/deploy/helm-template.yaml", $"apiVersion: v1\nkind: Secret\nmetadata:\n  name: db-secret\nstringData:\n  password: {CommittedPassword}\n---\n" + DeploymentUsingSecret("db-secret"));
        var scan = RepositoryScanner.Scan(Path.Join(_temp.FullName, "repo"));
        var choices = new AnalysisChoices();
        RepositoryAnalyzer.SuggestChoices(scan, choices, []);
        var profile = new RepositoryAnalyzer((_, _) => null).BuildProfile(scan, choices, NoConnections, [], TestContext.Current.CancellationToken);
        profile.Sources.OfType<KubernetesSource>().Single().Name = "Rendered manifests";

        var result = await Run(profile);

        var finding = result.Secrets.Single(s => s.Key == "Db:Password");
        Assert.Equal(SecretRating.Exposed, finding.Rating);
        Assert.Equal("Secret db-secret (key password, helm-template.yaml:6)", finding.Where);
        AssertHoldsNoValue(finding);
    }

    [Theory]
    [InlineData("stringData")]
    [InlineData("data")]
    public async Task A_secret_encrypted_with_sops_is_not_exposed(string field)
    {
        Write("repo/src/Orders/appsettings.json", """{"Db":{"Password":""}}""");
        Write("repo/k8s/deployment.yaml", DeploymentUsingSecret("db-secret"));
        Write("repo/k8s/secret.yaml", $"apiVersion: v1\nkind: Secret\nmetadata:\n  name: db-secret\n{field}:\n  password: ENC[AES256_GCM,data:RmFrZUNpcGhlcg==,iv:RmFrZUl2RmFrZUl2RmFrZQ==,tag:RmFrZVRhZw==,type:str]\n"
            + "sops:\n  lastmodified: \"2026-01-01T00:00:00Z\"\n  mac: ENC[AES256_GCM,data:RmFrZU1hYw==,iv:RmFrZUl2RmFrZUl2RmFrZQ==,tag:RmFrZVRhZw==,type:str]\n  version: 3.9.0\n");

        var result = await TraceRepository();

        var finding = result.Secrets.Single(s => s.Key == "Db:Password");
        Assert.NotEqual(SecretRating.Exposed, finding.Rating);
        Assert.DoesNotContain("decode", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secret_generator_env_file_is_named_in_the_reason()
    {
        // Without an app the container is traced, so the env entry keeps its link to the generated Secret.
        Write("repo/deploy/base/kustomization.yaml", "resources:\n  - deployment.yaml\n");
        Write("repo/deploy/base/deployment.yaml", DeploymentUsingSecret("db-secret"));
        Write("repo/deploy/overlays/production/secrets.env", $"# database\npassword={CommittedPassword}\n");
        Write("repo/deploy/overlays/production/kustomization.yaml", "resources:\n  - ../../base\nsecretGenerator:\n  - name: db-secret\n    envs:\n      - secrets.env\n");

        var result = await TraceRepository(kustomization: "deploy/overlays/production");

        var finding = result.Secrets.Single(s => s.Key == "Db__Password");
        Assert.Equal(SecretRating.Exposed, finding.Rating);
        Assert.Equal("production/kustomization.yaml (line 6, secretGenerator db-secret)", finding.Where);
        Assert.StartsWith("The value is kept in plain text in line 2 of the env file secrets.env named by the secretGenerator of production/kustomization.yaml, so anyone who can read that file can decode it.", finding.Reason, StringComparison.Ordinal);
        AssertHoldsNoValue(finding);
    }

    [Fact]
    public async Task A_secret_from_the_resources_of_a_built_overlay_is_exposed_by_its_name_and_key()
    {
        Write("repo/src/Orders/appsettings.json", """{"Db":{"Password":""}}""");
        Write("repo/deploy/base/kustomization.yaml", "resources:\n  - deployment.yaml\n  - secret.yaml\n");
        Write("repo/deploy/base/deployment.yaml", DeploymentUsingSecret("db-secret"));
        Write("repo/deploy/base/secret.yaml", $"apiVersion: v1\nkind: Secret\nmetadata:\n  name: db-secret\nstringData:\n  password: {CommittedPassword}\n");
        Write("repo/deploy/overlays/production/kustomization.yaml", "resources:\n  - ../../base\n");
        var built = $"apiVersion: v1\nkind: Secret\nmetadata:\n  name: db-secret\ndata:\n  password: {Encoded(CommittedPassword)}\n---\n" + DeploymentUsingSecret("db-secret");

        var result = await TraceRepository((tool, _) => tool == "kubectl" ? built : null, "deploy/overlays/production");

        var finding = result.Secrets.Single(s => s.Key == "Db:Password");
        Assert.Equal(SecretRating.Exposed, finding.Rating);
        Assert.Equal("Secret db-secret (key password)", finding.Where);
        Assert.StartsWith("The value is kept in plain text or base64 in Secret db-secret from the overlay's resources, so anyone who can read the overlay's files can decode it.", finding.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("kustomize-build.yaml", finding.Reason + finding.Where, StringComparison.Ordinal);
        AssertHoldsNoValue(finding);
    }

    [Fact]
    public async Task A_secret_value_a_rendered_chart_sets_without_its_values_is_to_be_checked()
    {
        Write("repo/src/Orders/appsettings.json", """{"Db":{"Password":""}}""");
        Write("repo/chart/Chart.yaml", "name: orders\nversion: 0.1.0\n");
        Write("repo/chart/values.yaml", "replicas: 1\n");
        Write("repo/chart/templates/secret.yaml", "apiVersion: v1\nkind: Secret\nmetadata:\n  name: db-secret\ndata:\n  password: {{ randAlphaNum 16 | b64enc }}\n");
        Write("repo/chart/templates/deployment.yaml", DeploymentUsingSecret("db-secret"));
        var rendered = $"apiVersion: v1\nkind: Secret\nmetadata:\n  name: db-secret\ndata:\n  password: {Encoded(CommittedPassword)}\n---\n" + DeploymentUsingSecret("db-secret");

        var result = await TraceRepository((tool, _) => tool == "helm" ? rendered : null);

        var finding = result.Secrets.Single(s => s.Key == "Db:Password");
        Assert.Equal(SecretRating.Check, finding.Rating);
        Assert.Equal("Comes from Secret db-secret, which the chart renders without a values file that sets it. Check the chart's template: a value written there is kept in the repository, one it generates as it renders (such as with randAlphaNum or lookup) is not.", finding.Reason);
        Assert.Equal("Secret db-secret (key password)", finding.Where);
        AssertHoldsNoValue(finding);

        // The rendered text is read from memory under the output's file name, as a file of that name would be.
        static IEnumerable<ProvenanceStep> All(IEnumerable<ProvenanceStep> steps) => steps.SelectMany(s => All(s.Origins).Prepend(s));
        var secret = All(result.Report.Find("Db:Password")!.Steps).Single(s => s.Kind == LayerKind.KubernetesSecret);
        Assert.Equal("key password, helm-template.yaml:6", secret.Location);
    }

    [Fact]
    public async Task A_secret_rendered_from_committed_helm_values_is_exposed_at_the_template()
    {
        Write("repo/src/Orders/appsettings.json", """{"Db":{"Password":""}}""");
        Write("repo/chart/Chart.yaml", "name: orders\nversion: 0.1.0\n");
        Write("repo/chart/values.yaml", $"db:\n  password: {CommittedPassword}\n");
        Write("repo/chart/templates/secret.yaml", "apiVersion: v1\nkind: Secret\nmetadata:\n  name: db-secret\nstringData:\n  password: {{ .Values.db.password | quote }}\n");
        Write("repo/chart/templates/deployment.yaml", DeploymentUsingSecret("db-secret"));
        var rendered = $"apiVersion: v1\nkind: Secret\nmetadata:\n  name: db-secret\nstringData:\n  password: \"{CommittedPassword}\"\n---\n" + DeploymentUsingSecret("db-secret");

        var result = await TraceRepository((tool, _) => tool == "helm" ? rendered : null);

        var finding = result.Secrets.Single(s => s.Key == "Db:Password");
        Assert.Equal(SecretRating.Exposed, finding.Rating);
        Assert.StartsWith("Helm template ", finding.Where, StringComparison.Ordinal);
        AssertHoldsNoValue(finding);
    }

    [Theory]
    [InlineData("")]
    [InlineData("apiVersion: v1\nkind: Secret\nmetadata:\n  name: db-secret\nstringData:\n  password: \"${DB_PASSWORD}\"\n")]
    [InlineData("apiVersion: v1\nkind: Secret\nmetadata:\n  name: db-secret\nstringData:\n  password: \"\"\n")]
    [InlineData("apiVersion: external-secrets.io/v1beta1\nkind: ExternalSecret\nmetadata:\n  name: db\nspec:\n  secretStoreRef:\n    name: vault\n    kind: ClusterSecretStore\n  target:\n    name: db-secret\n  data:\n    - secretKey: password\n      remoteRef:\n        key: orders/db\n        property: password\n")]
    [InlineData("apiVersion: bitnami.com/v1alpha1\nkind: SealedSecret\nmetadata:\n  name: db-secret\nspec:\n  encryptedData:\n    password: AgBy3i4OJSWK+PiTySYZZA==\n")]
    public async Task A_secret_whose_value_is_not_in_the_repository_stays_protected(string manifest)
    {
        // Without an app the container is traced, so the env entry keeps its link to the Secret even when its value is
        // unknown. The scan picks files by the kinds of workloads, ConfigMaps and Secrets, so the other object shares a file.
        Write("repo/k8s/deployment.yaml", DeploymentUsingSecret("db-secret") + "---\n" + manifest);

        var result = await TraceRepository();

        var finding = result.Secrets.Single(s => s.Key == "Db__Password");
        Assert.Equal(SecretRating.Protected, finding.Rating);
        Assert.Equal("Kept in Secret db-secret.", finding.Reason);
    }

    // Helm and Kustomize

    [Fact]
    public async Task A_helm_source_names_a_missing_chart_folder()
    {
        var helm = new HelmSource { ChartFolder = Path.Join(_temp.FullName, "nochart") };

        var result = await Run(ProfileOf(App(), helm));

        Assert.Equal($"The chart folder '{helm.ChartFolder}' does not exist.", ProblemOf(result, helm));
    }

    [Fact]
    public async Task A_helm_source_names_a_missing_values_file()
    {
        Write("chart/Chart.yaml", "name: demo\nversion: 0.1.0\n");
        Write("chart/values.yaml", "a: 1\n");
        var missing = Path.Join(_temp.FullName, "values-none.yaml");
        var helm = new HelmSource { ChartFolder = Path.Join(_temp.FullName, "chart"), ValuesFiles = [missing] };

        var result = await Run(ProfileOf(App(), helm));

        Assert.Equal($"The values file '{missing}' does not exist.", ProblemOf(result, helm));
    }

    [Fact]
    public async Task A_helm_chart_links_its_values_to_the_settings_that_use_them()
    {
        Write("chart/Chart.yaml", "name: demo\nversion: 0.1.0\n");
        Write("chart/values.yaml", "db:\n  timeout: 30\n");
        Write("chart/templates/cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: cfg\ndata:\n  Db__Timeout: {{ .Values.db.timeout | quote }}\n");
        var k8s = new KubernetesSource { Paths = [Write("k8s/pod.yaml", Pod("web", "      envFrom:\n        - configMapRef:\n            name: cfg\n")), Write("k8s/cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: cfg\ndata:\n  Db__Timeout: \"30\"\n")] };
        var helm = new HelmSource { ChartFolder = Path.Join(_temp.FullName, "chart"), SetValues = ["db.timeout=45"] };

        var result = await Run(ProfileOf(App("{}"), k8s, helm));

        Assert.Null(ProblemOf(result, helm));
        var setting = result.Report.Find("Db:Timeout")!;
        Assert.Contains(AllSteps(setting.Steps), s => s.Kind == LayerKind.Helm);
    }

    [Fact]
    public async Task A_helm_chart_that_includes_a_template_it_does_not_have_says_why_no_value_is_dead()
    {
        Write("chart/Chart.yaml", "name: demo\nversion: 0.1.0\ndependencies:\n  - name: base\n    version: 1.0.0\n");
        Write("chart/values.yaml", "image: example/app\n");
        Write("chart/templates/all.yaml", "{{ include \"base.deployment\" . }}\n");
        var helm = new HelmSource { Name = "Helm chart deploy/demo", ChartFolder = Path.Join(_temp.FullName, "chart") };

        var result = await Run(ProfileOf(App("{}"), helm));

        Assert.Null(ProblemOf(result, helm));
        Assert.Empty(result.DeadHelmValues);
        Assert.Contains(result.Notes, n => n.StartsWith("The templates of Helm chart deploy/demo include \"base.deployment\"", StringComparison.Ordinal));
    }

    private static IEnumerable<ProvenanceStep> AllSteps(IEnumerable<ProvenanceStep> steps) =>
        steps.SelectMany(s => new[] { s }.Concat(AllSteps(s.Origins)));

    [Fact]
    public async Task A_kustomize_source_names_a_missing_folder()
    {
        var kustomize = new KustomizeSource { Folder = Path.Join(_temp.FullName, "nokustomize") };

        var result = await Run(ProfileOf(App(), kustomize));

        Assert.Equal($"The Kustomize folder '{kustomize.Folder}' does not exist.", ProblemOf(result, kustomize));
    }

    [Fact]
    public async Task A_kustomize_generator_links_to_the_settings_that_use_it()
    {
        Write("overlay/kustomization.yaml", "configMapGenerator:\n  - name: cfg\n    literals:\n      - Db__Timeout=90\n");
        var k8s = new KubernetesSource { Paths = [Write("k8s/pod.yaml", Pod("web", "      envFrom:\n        - configMapRef:\n            name: cfg\n")), Write("k8s/cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: cfg\ndata:\n  Db__Timeout: \"90\"\n")] };
        var kustomize = new KustomizeSource { Folder = Path.Join(_temp.FullName, "overlay") };

        var result = await Run(ProfileOf(App("{}"), k8s, kustomize));

        Assert.Null(ProblemOf(result, kustomize));
        Assert.Contains(AllSteps(result.Report.Find("Db:Timeout")!.Steps), s => s.Kind == LayerKind.Kustomize);
    }

    // GitHub

    private static FakeHttpHandler GitHub(string owner = "contoso", string name = "orders")
    {
        var repo = $"/repos/{owner}/{name}";
        return new FakeHttpHandler()
            .Respond($"{repo}/actions/organization-variables?per_page=30&page=1", """{"total_count":0,"variables":[]}""")
            .Respond($"{repo}/actions/organization-secrets?per_page=100&page=1", """{"total_count":0,"secrets":[]}""")
            .Respond($"{repo}/actions/variables?per_page=30&page=1", """{"total_count":1,"variables":[{"name":"Db__Host","value":"gh-host","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}]}""")
            .Respond($"{repo}/actions/secrets?per_page=100&page=1", """{"total_count":1,"secrets":[{"name":"OLD_KEY","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}]}""");
    }

    private static GitHubSource GitHubSourceFor(string repository = "contoso/orders") =>
        new() { Name = "gh", Repository = repository, CredentialSource = CredentialSource.EnvironmentVariable, CredentialVariable = "TOK" };

    [Theory]
    [InlineData("orders")]
    [InlineData("a/b/c")]
    [InlineData("contoso/")]
    [InlineData("/orders")]
    [InlineData("contoso/ ")]
    [InlineData("")]
    public async Task A_github_repository_must_be_owner_slash_name(string repository)
    {
        var github = GitHubSourceFor(repository);

        var result = await Run(ProfileOf(App(), github), name => Token);

        Assert.Equal($"The repository must be owner/name, got '{repository}'.", ProblemOf(result, github));
    }

    [Fact]
    public async Task A_missing_workflow_file_is_named()
    {
        var github = GitHubSourceFor();
        github.Workflows = [Path.Join(_temp.FullName, "ci.yml")];

        var result = await Run(ProfileOf(App(), github), name => Token);

        Assert.Equal($"The workflow file '{github.Workflows[0]}' does not exist.", ProblemOf(result, github));
    }

    [Fact]
    public async Task A_github_api_address_must_be_a_url()
    {
        var github = GitHubSourceFor();
        github.ApiUrl = "not a url";

        var result = await Run(ProfileOf(App(), github), name => Token);

        Assert.Equal("The GitHub API address must be an absolute URL, got 'not a url'.", ProblemOf(result, github));
    }

    [Theory]
    [InlineData("https://ghe.example.test/api/v3")]
    [InlineData("https://ghe.example.test/api/v3/")]
    public async Task A_github_enterprise_address_is_used_with_or_without_a_trailing_slash(string apiUrl)
    {
        var handler = new FakeHttpHandler()
            .Respond("/api/v3/repos/contoso/orders/actions/organization-variables?per_page=30&page=1", """{"total_count":0,"variables":[]}""")
            .Respond("/api/v3/repos/contoso/orders/actions/organization-secrets?per_page=100&page=1", """{"total_count":0,"secrets":[]}""")
            .Respond("/api/v3/repos/contoso/orders/actions/variables?per_page=30&page=1", """{"total_count":0,"variables":[]}""")
            .Respond("/api/v3/repos/contoso/orders/actions/secrets?per_page=100&page=1", """{"total_count":0,"secrets":[]}""");
        var github = GitHubSourceFor();
        github.ApiUrl = " " + apiUrl + " ";

        var result = await Run(ProfileOf(App(), github), name => Token, http: handler.Client);

        Assert.Null(ProblemOf(result, github));
        Assert.All(handler.Requests, r => Assert.Equal("ghe.example.test", r.RequestUri!.Host));
    }

    [Fact]
    public async Task Github_names_with_blanks_around_them_are_trimmed_and_the_environment_is_asked_for_when_set()
    {
        var handler = GitHub();
        handler.Respond("/repos/contoso/orders/environments/prod/variables?per_page=30&page=1", """{"total_count":0,"variables":[]}""")
            .Respond("/repos/contoso/orders/environments/prod/secrets?per_page=100&page=1", """{"total_count":0,"secrets":[]}""");
        var github = GitHubSourceFor(" contoso / orders ");
        github.Environment = " prod ";

        var result = await Run(ProfileOf(App("{}"), github), name => Token, http: handler.Client);

        Assert.Null(ProblemOf(result, github));
        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath == "/repos/contoso/orders/environments/prod/variables");
    }

    [Fact]
    public async Task A_workflow_variable_without_the_apps_prefix_still_reaches_the_setting()
    {
        var app = App("""{"Db":{"Host":"base"}}""");
        app.EnvironmentPrefixes = ["ORDERS_"];
        var github = GitHubSourceFor();
        github.Workflows = [Write("workflows/deploy.yml", "jobs:\n  deploy:\n    runs-on: ubuntu-latest\n    env:\n      Db__Host: ${{ vars.Db__Host }}\n    steps:\n      - run: ./deploy.sh\n")];

        var result = await Run(ProfileOf(app, github), name => Token, http: GitHub().Client);

        Assert.Null(ProblemOf(result, github));
        Assert.Equal("gh-host", result.Report.Find("Db:Host")!.Value);
    }

    [Fact]
    public async Task Github_variables_that_a_setting_uses_are_linked_and_the_others_are_listed_as_unused()
    {
        var github = GitHubSourceFor();

        var result = await Run(ProfileOf(App("""{"Db":{"Host":"base"}}"""), github), name => Token, http: GitHub().Client);

        Assert.Null(ProblemOf(result, github));
        Assert.Contains(new UnusedVariable("GitHub Actions", "OLD_KEY", "repository secret (contoso/orders)") { IsSecret = true, NameLooksSecret = true }, result.UnusedVariables);
        Assert.Contains(new UnusedVariable("GitHub Actions", "Db__Host", "repository variable (contoso/orders)") { Value = "gh-host" }, result.UnusedVariables);
    }

    [Fact]
    public async Task A_github_variable_that_is_not_a_secret_is_described_as_a_variable()
    {
        var github = GitHubSourceFor();
        var handler = GitHub().Respond("/repos/contoso/orders/actions/variables?per_page=30&page=1", """{"total_count":1,"variables":[{"name":"UNUSED_VAR","value":"x","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}]}""");

        var result = await Run(ProfileOf(App("{}"), github), name => Token, http: handler.Client);

        Assert.Contains(new UnusedVariable("GitHub Actions", "UNUSED_VAR", "repository variable (contoso/orders)") { Value = "x" }, result.UnusedVariables);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_github_variable_whose_value_looks_like_a_credential_has_no_value_unless_secrets_are_revealed(bool reveal)
    {
        const string storage = "DefaultEndpointsProtocol=https;AccountName=fake;AccountKey=hunter2-fake-key";
        var github = GitHubSourceFor();
        var handler = GitHub().Respond("/repos/contoso/orders/actions/variables?per_page=30&page=1", $$"""{"total_count":1,"variables":[{"name":"STORAGE","value":"{{storage}}","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}]}""");

        var result = await Run(ProfileOf(App("{}"), github), name => Token, reveal, handler.Client);

        Assert.Equal(reveal ? storage : null, result.UnusedVariables.Single(v => v.Name == "STORAGE").Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_github_variable_whose_name_looks_secret_has_no_value_unless_secrets_are_revealed(bool reveal)
    {
        var github = GitHubSourceFor();
        github.Workflows = [Write("workflows/deploy.yml", "jobs:\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: ./deploy.sh ${{ vars.DEPLOY_TOKEN }}\n")];
        var handler = GitHub().Respond("/repos/contoso/orders/actions/variables?per_page=30&page=1", """
            {"total_count":3,"variables":[
              {"name":"SMTP_PASSWORD","value":"fake-smtp-password"},
              {"name":"DEPLOY_TOKEN","value":"fake-deploy-token"},
              {"name":"MAIL_HOST","value":"mail.example.test"}]}
            """);

        var result = await Run(ProfileOf(App("{}"), github), name => Token, reveal, handler.Client);

        var unused = result.UnusedVariables.Single(v => v.Name == "SMTP_PASSWORD");
        Assert.Equal(reveal ? "fake-smtp-password" : null, unused.Value);
        Assert.False(unused.IsSecret);
        Assert.True(unused.NameLooksSecret);
        Assert.Equal("repository variable (contoso/orders)", unused.Detail);
        var workflow = result.WorkflowVariables.Single(v => v.Name == "DEPLOY_TOKEN");
        Assert.Equal(reveal ? "fake-deploy-token" : null, workflow.Value);
        Assert.False(workflow.IsSecret);
        Assert.True(workflow.NameLooksSecret);
        var plain = result.UnusedVariables.Single(v => v.Name == "MAIL_HOST");
        Assert.Equal("mail.example.test", plain.Value);
        Assert.False(plain.NameLooksSecret);
    }

    // Octopus

    private static FakeHttpHandler Octopus(bool sensitive = false) => new FakeHttpHandler()
        .Respond("/api/spaces/all", """[{"Id":"Spaces-1","Name":"Default","IsDefault":true},{"Id":"Spaces-2","Name":"Platform"}]""")
        .Respond("/api/Spaces-1/projects/all", """[{"Id":"Projects-1","Name":"Api","Slug":"api","VariableSetId":"vs-1","IncludedLibraryVariableSetIds":["Lib-1"]}]""")
        .Respond("/api/Spaces-1/variables/vs-1", """
            {"Variables":[
              {"Name":"Db:Host","Value":"octo-host","Type":"String","Scope":{"Environment":["Environments-1"]}},
              {"Name":"Db:Host","Value":"octo-other","Type":"String","Scope":{"Environment":["Environments-2"]}},
              {"Name":"Db:Pool","Value":"7","Type":"String","Scope":{"Role":["web"],"TenantTag":["Region/West"]}},
              {"Name":"Db:Mach","Value":"m","Type":"String","Scope":{"Machine":["Machines-1"]}},
              {"Name":"Db:Chan","Value":"c","Type":"String","Scope":{"Channel":["Channels-1"]}},
              {"Name":"Db:Step","Value":"s","Type":"String","Scope":{"Action":["Actions-1"]}},
              {"Name":"Unused:Var","Value":null,"Type":"@TYPE@","IsSensitive":@SENSITIVE@,"Scope":{}}
            ],"ScopeValues":{"Environments":[{"Id":"Environments-1","Name":"Production"},{"Id":"Environments-2","Name":"Staging"}],"Roles":[{"Id":"web","Name":"web"}],"TenantTags":[{"Id":"Region/West","Name":"Region/West"}],"Machines":[{"Id":"Machines-1","Name":"m1"}],"Channels":[{"Id":"Channels-1","Name":"Main"}],"Actions":[{"Id":"Actions-1","Name":"Deploy"}]}}
            """.Replace("@TYPE@", sensitive ? "Sensitive" : "String", StringComparison.Ordinal).Replace("@SENSITIVE@", sensitive ? "true" : "false", StringComparison.Ordinal))
        .Respond("/api/Spaces-1/libraryvariablesets/Lib-1", """{"Id":"Lib-1","Name":"Shared","VariableSetId":"vs-lib"}""")
        .Respond("/api/Spaces-1/variables/vs-lib", """{"Variables":[{"Name":"Lib:Var","Value":"x","Type":"String","Scope":{}}],"ScopeValues":{}}""");

    private static OctopusSource OctopusSourceFor() =>
        new() { Name = "octo", Server = "https://octopus.example.test", Project = "Api", CredentialSource = CredentialSource.EnvironmentVariable, CredentialVariable = "KEY" };

    [Fact]
    public async Task Octopus_needs_a_server_and_a_project_and_says_so()
    {
        var noServer = OctopusSourceFor();
        noServer.Server = " ";
        var noProject = OctopusSourceFor();
        noProject.Project = " ";

        var result = await Run(ProfileOf(App(), noServer, noProject), name => "API-KEY");

        Assert.Equal("Enter the Octopus server address.", ProblemOf(result, noServer));
        Assert.Equal("Enter the Octopus project.", ProblemOf(result, noProject));
    }

    [Fact]
    public async Task An_octopus_server_must_be_a_url()
    {
        var source = OctopusSourceFor();
        source.Server = "not a url";

        var result = await Run(ProfileOf(App(), source), name => "API-KEY");

        Assert.Equal("The Octopus server address must be an absolute URL, got 'not a url'.", ProblemOf(result, source));
    }

    [Fact]
    public async Task Octopus_variables_are_linked_for_the_chosen_environment()
    {
        var source = OctopusSourceFor();
        source.Environment = " Production ";
        var handler = Octopus();

        var result = await Run(ProfileOf(App("""{"Db":{"Host":"base"}}"""), source), name => "API-KEY", http: handler.Client);

        Assert.Null(ProblemOf(result, source));
        Assert.Contains(AllSteps(result.Report.Find("Db:Host")!.Steps), s => s.Kind == LayerKind.OctopusDeploy && s.Value == "octo-host");
        Assert.DoesNotContain(AllSteps(result.Report.Find("Db:Host")!.Steps), s => s.Kind == LayerKind.OctopusDeploy && s.Value == "octo-other");
        Assert.All(handler.Requests, r => Assert.Equal("API-KEY", Assert.Single(r.Headers.GetValues("X-Octopus-ApiKey"))));
    }

    [Fact]
    public async Task Octopus_scope_choices_select_the_variables_that_apply()
    {
        var source = OctopusSourceFor();
        source.Roles = ["web"];
        source.TenantTags = ["Region/West"];
        source.Machine = "m1";
        source.Channel = "Main";
        source.Step = "Deploy";

        var result = await Run(ProfileOf(App("""{"Db":{"Pool":"1","Mach":"1","Chan":"1","Step":"1"}}"""), source), name => "API-KEY", http: Octopus().Client);

        Assert.Null(ProblemOf(result, source));
        foreach (var key in new[] { "Db:Pool", "Db:Mach", "Db:Chan", "Db:Step" })
        {
            Assert.Contains(AllSteps(result.Report.Find(key)!.Steps), s => s.Kind == LayerKind.OctopusDeploy);
        }
    }

    [Theory]
    [InlineData(false, "Unused:Var", "Octopus Deploy", "Api variables")]
    public async Task Unused_octopus_variables_are_listed_with_their_set(bool sensitive, string name, string tool, string set)
    {
        var source = OctopusSourceFor();

        var result = await Run(ProfileOf(App("{}"), source), _ => "API-KEY", http: Octopus(sensitive).Client);

        var unused = result.UnusedVariables.Single(v => v.Name == name);
        Assert.Equal(tool, unused.Tool);
        Assert.StartsWith(set.Split(' ')[0], unused.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sensitive_unused_octopus_variable_says_so()
    {
        var result = await Run(ProfileOf(App("{}"), OctopusSourceFor()), _ => "API-KEY", http: Octopus(sensitive: true).Client);

        Assert.EndsWith(", sensitive", result.UnusedVariables.Single(v => v.Name == "Unused:Var").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unused_octopus_variable_that_is_not_sensitive_has_no_suffix()
    {
        var result = await Run(ProfileOf(App("{}"), OctopusSourceFor()), _ => "API-KEY", http: Octopus().Client);

        Assert.DoesNotContain("sensitive", result.UnusedVariables.Single(v => v.Name == "Unused:Var").Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_octopus_variable_whose_name_looks_secret_has_no_value_unless_secrets_are_revealed(bool reveal)
    {
        var handler = Octopus().Respond("/api/Spaces-1/variables/vs-lib", """
            {"Variables":[
              {"Name":"Legacy.ApiKey","Value":"fake-api-key","Type":"String","Scope":{}},
              {"Name":"Lib:Var","Value":"x","Type":"String","Scope":{}}
            ],"ScopeValues":{}}
            """);

        var result = await Run(ProfileOf(App("{}"), OctopusSourceFor()), _ => "API-KEY", reveal, handler.Client);

        var unused = result.UnusedVariables.Single(v => v.Name == "Legacy.ApiKey");
        Assert.Equal(reveal ? "fake-api-key" : null, unused.Value);
        Assert.False(unused.IsSecret);
        Assert.True(unused.NameLooksSecret);
        Assert.Equal("library variable set Shared", unused.Detail);
        Assert.Equal("x", result.UnusedVariables.Single(v => v.Name == "Lib:Var").Value);
    }

    [Fact]
    public async Task The_octopus_library_set_can_be_left_out()
    {
        var source = OctopusSourceFor();
        source.IncludeLibraryVariableSets = false;

        var result = await Run(ProfileOf(App("{}"), source), _ => "API-KEY", http: Octopus().Client);

        Assert.DoesNotContain(result.UnusedVariables, v => v.Name == "Lib:Var");
        var withLibrary = await Run(ProfileOf(App("{}"), OctopusSourceFor()), _ => "API-KEY", http: Octopus().Client);
        Assert.Contains(withLibrary.UnusedVariables, v => v.Name == "Lib:Var");
    }

    [Fact]
    public async Task A_named_octopus_space_is_used()
    {
        var handler = new FakeHttpHandler().Respond("/api/spaces/all", """[{"Id":"Spaces-1","Name":"Default","IsDefault":true},{"Id":"Spaces-2","Name":"Platform"}]""");
        var source = OctopusSourceFor();
        source.Space = " Platform ";

        var result = await Run(ProfileOf(App("{}"), source), _ => "API-KEY", http: handler.Client);

        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath == "/api/Spaces-2/projects/all");
        Assert.NotNull(ProblemOf(result, source));
    }

    // Failures of the services

    private sealed class ThrowingHandler(Func<Exception> make) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw make();
    }

    [Fact]
    public async Task A_timeout_is_a_problem_of_the_source_and_the_trace_goes_on()
    {
        var github = GitHubSourceFor();

        var result = await Run(ProfileOf(App(), github), name => Token, http: () => new HttpClient(new ThrowingHandler(() => new TaskCanceledException("timed out"))));

        Assert.Contains("timed out", ProblemOf(result, github), StringComparison.Ordinal);
        Assert.Equal("base", result.Report.Find("Db:Host")!.Value);
    }

    [Fact]
    public async Task A_cancelled_trace_is_not_a_problem_of_the_source()
    {
        using var cancel = new CancellationTokenSource();
        var github = GitHubSourceFor();
        var client = () => new HttpClient(new ThrowingHandler(() =>
        {
            cancel.Cancel();
            return new TaskCanceledException("cancelled");
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TraceRunner(new InMemoryCredentialStore(), _ => Token, client).RunAsync(ProfileOf(App(), github), false, cancel.Token));
    }

    [Fact]
    public async Task A_bug_in_a_source_is_not_swallowed()
    {
        var github = GitHubSourceFor();

        await Assert.ThrowsAsync<NotSupportedException>(() => Run(ProfileOf(App(), github), name => Token, http: () => new HttpClient(new ThrowingHandler(() => new NotSupportedException("bug")))));
    }

    [Fact]
    public async Task A_failing_credential_store_is_a_problem_of_the_source()
    {
        var github = new GitHubSource { Name = "gh", Repository = "contoso/orders" };

        var result = await Run(ProfileOf(App(), github), store: new BrokenStore());

        Assert.Equal("keyring is locked", ProblemOf(result, github));
    }

    private sealed class BrokenStore : ICredentialStore
    {
        public string DisplayName => "broken";

        public string? Read(string account) => throw new CredentialStoreException("keyring is locked");

        public void Write(string account, string secret) => throw new CredentialStoreException("keyring is locked");

        public void Delete(string account) => throw new CredentialStoreException("keyring is locked");
    }

    // Credentials

    [Fact]
    public void Reading_a_credential_needs_a_source()
    {
        Assert.Throws<ArgumentNullException>(() => new TraceRunner(new InMemoryCredentialStore()).ReadCredential(null!));
    }

    [Fact]
    public void The_credential_messages_name_the_credential_and_the_variable()
    {
        var runner = new TraceRunner(new InMemoryCredentialStore(), _ => null);
        var stored = new GitHubSource();
        var variable = new GitHubSource { CredentialSource = CredentialSource.EnvironmentVariable, CredentialVariable = "TOK" };
        var unnamed = new OctopusSource { CredentialSource = CredentialSource.EnvironmentVariable };

        Assert.Equal("No token is saved for this connection. Enter it and choose Save.", Assert.Throws<TraceSetupException>(() => runner.ReadCredential(stored)).Message);
        Assert.Equal("The environment variable TOK is not set. Set it before starting ConfigFerret, or save the token in the memory instead.", Assert.Throws<TraceSetupException>(() => runner.ReadCredential(variable)).Message);
        Assert.Equal("Name the environment variable that holds the API key.", Assert.Throws<TraceSetupException>(() => runner.ReadCredential(unnamed)).Message);
    }

    [Fact]
    public void A_credential_from_the_environment_is_returned_as_found()
    {
        var source = new GitHubSource { CredentialSource = CredentialSource.EnvironmentVariable, CredentialVariable = " TOK " };

        Assert.Equal(Token, new TraceRunner(new InMemoryCredentialStore(), name => name == "TOK" ? Token : null).ReadCredential(source));
    }

    [Fact]
    public void The_default_environment_is_this_processs()
    {
        const string name = "CONFIGFERRET_TEST_TRACE_TOKEN";
        var source = new GitHubSource { CredentialSource = CredentialSource.EnvironmentVariable, CredentialVariable = name };
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "from-process");

            Assert.Equal("from-process", new TraceRunner(new InMemoryCredentialStore()).ReadCredential(source));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    // Dotenv text

    [Theory]
    [InlineData("A=1", "A", "1")]
    [InlineData("  A = 1  ", "A", "1")]
    [InlineData("export A=1", "A", "1")]
    [InlineData("export    A=1", "A", "1")]
    [InlineData("exportA=1", "exportA", "1")]
    [InlineData("A=\"quoted value\"", "A", "quoted value")]
    [InlineData("A='quoted value'", "A", "quoted value")]
    [InlineData("A=\"\"", "A", "")]
    [InlineData("A=\"", "A", "\"")]
    [InlineData("A=\"mixed'", "A", "\"mixed'")]
    [InlineData("A=x=y", "A", "x=y")]
    [InlineData("A=", "A", "")]
    public void Dotenv_lines_are_split_at_the_first_equals_sign_and_unquoted(string line, string name, string value)
    {
        Assert.Equal((name, value), Assert.Single(DotEnv.Parse(line, "x")));
    }

    [Theory]
    [InlineData("=1")]
    [InlineData("NOPE")]
    [InlineData("export ")]
    public void Dotenv_lines_without_a_name_are_refused_with_the_line_number(string line)
    {
        var ex = Assert.Throws<FormatException>(() => DotEnv.Parse("A=1\n" + line, "my.env").ToList());

        Assert.Equal("my.env line 2: expected NAME=VALUE.", ex.Message);
    }

    [Fact]
    public void Dotenv_text_may_be_null_and_skips_blank_lines_and_comments()
    {
        Assert.Empty(DotEnv.Parse(null, "x"));
        Assert.Equal([("A", "1"), ("B", "2")], DotEnv.Parse("\n  \n# c\n  # d\nA=1\r\nB=2\r\n", "x"));
    }
}
