using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.GitLab.Tests;

public class GitLabResolverTests
{
    private static readonly GitLabVariables Variables = new(
    [
        new("DB_TIMEOUT", "5", GitLabVariableLevel.Instance, "gitlab.com"),
        new("DB_TIMEOUT", "10", GitLabVariableLevel.Group, "contoso"),
        new("DB_TIMEOUT", "20", GitLabVariableLevel.Group, "contoso/platform", "production"),
        new("DB_TIMEOUT", "30", GitLabVariableLevel.Project, "contoso/platform/api"),
        new("DB_TIMEOUT", "32", GitLabVariableLevel.Project, "contoso/platform/api", "production"),
        new("DB_PASSWORD", null, GitLabVariableLevel.Project, "contoso/platform/api", IsMasked: true),
        new("API_KEY", null, GitLabVariableLevel.Project, "contoso/platform/api", IsMasked: true, IsHidden: true),
        new("SIGNING_KEY", "k", GitLabVariableLevel.Project, "contoso/platform/api", IsMasked: true),
        new("KUBECONFIG", "apiVersion: v1", GitLabVariableLevel.Group, "contoso", IsProtected: true, IsFile: true),
        new("REVIEW_ONLY", "r", GitLabVariableLevel.Project, "contoso/platform/api", "review/*"),
        new("REVIEW_ONLY", "s", GitLabVariableLevel.Project, "contoso/platform/api", "staging"),
    ]);

    private const string CiFile = """
        variables:
          DB_TIMEOUT: "15"   # global default
          GREETING:
            value: hello
            description: shown in the run form
          ALIAS: $DB_TIMEOUT
          EMPTY: ""

        deploy:
          stage: deploy
          variables:
            DB_TIMEOUT: 25
            BRACED: ${SIGNING_KEY}
          environment: production
          script:
            - export DB_PASSWORD="$DB_PASSWORD"
            - 'export TOKEN_ALIAS=${API_KEY}'
            - MISSING=$NOT_THERE
            - echo $DB_TIMEOUT
          services:
            - name: postgres
        """;

    private static ProvenanceStep EnvStep(string name, string? value) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = $"environment variable {name}",
        Value = value,
        Reference = new EnvironmentVariableRef(name),
    };

    private static GitLabResolver Resolver(Action<GitLabTraceOptions>? configure = null, GitLabVariables? variables = null)
    {
        var options = new GitLabTraceOptions();
        configure?.Invoke(options);
        return new GitLabResolver(variables ?? Variables, options);
    }

    private static IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep step, Action<GitLabTraceOptions>? configure = null, TraceDiagnostics? diagnostics = null) =>
        Resolver(configure).Resolve(step, diagnostics ?? new TraceDiagnostics());

    [Fact]
    public void Matches_by_key_instance_then_groups_then_project()
    {
        var chain = Resolve(EnvStep("DB_TIMEOUT", "30"));

        Assert.Equal(["5", "10", "30"], chain.Select(s => s.Value));
        Assert.All(chain, s => Assert.Equal(LayerKind.GitLabCi, s.Kind));
        Assert.All(chain, s => Assert.True(s.IsInferred));
        Assert.Equal("GitLab instance variable DB_TIMEOUT", chain[0].Source);
        Assert.Equal("GitLab group variable DB_TIMEOUT", chain[1].Source);
        Assert.Equal("GitLab project variable DB_TIMEOUT", chain[2].Source);
        Assert.Equal("contoso/platform/api", chain[2].Location);
        Assert.Equal("matched by name", chain[2].Note);
        Assert.Equal(new GitLabVariableRef(GitLabVariableLevel.Project, "contoso/platform/api", "DB_TIMEOUT", "*"), chain[2].Reference);
        Assert.Equal("matched by name; the GitLab value differs from the value seen here, so the deployment may be out of date or the value was transformed", chain[0].Note);
        Assert.Empty(Resolve(EnvStep("db_timeout", "30")));
    }

    [Fact]
    public void Picks_the_environment_scope_that_applies()
    {
        var chain = Resolve(EnvStep("DB_TIMEOUT", "32"), o => o.Environment = "production");

        Assert.Equal(["5", "10", "20", "32"], chain.Select(s => s.Value));
        Assert.Equal("contoso/platform/api, environment production", chain[^1].Location);
        Assert.Equal("matched by name; environment scope production", chain[^1].Note);
        Assert.Equal(new GitLabVariableRef(GitLabVariableLevel.Project, "contoso/platform/api", "DB_TIMEOUT", "production"), chain[^1].Reference);
    }

    [Fact]
    public void Warns_once_when_a_key_is_set_only_for_other_scopes()
    {
        var diagnostics = new TraceDiagnostics();
        var resolver = Resolver(o => o.Environment = "production");

        Assert.Empty(resolver.Resolve(EnvStep("REVIEW_ONLY", "r"), diagnostics));
        Assert.Empty(resolver.Resolve(EnvStep("REVIEW_ONLY", "r"), diagnostics));

        Assert.Equal("GitLab variable REVIEW_ONLY is set only for environment scopes review/*, staging, and none applies to environment production.", Assert.Single(diagnostics.Warnings));

        var none = new TraceDiagnostics();
        Assert.Empty(Resolve(EnvStep("REVIEW_ONLY", "r"), null, none));
        Assert.Equal("GitLab variable REVIEW_ONLY is set only for environment scopes review/*, staging, and none applies to a job with no environment.", Assert.Single(none.Warnings));

        Assert.Equal("r", Assert.Single(Resolve(EnvStep("REVIEW_ONLY", "r"), o => o.Environment = "review/x")).Value);
        Assert.Empty(Resolve(EnvStep("X", "r"), o => o.Map("X", "REVIEW_ONLY")));
    }

    [Fact]
    public void Marks_masked_and_hidden_variables_sensitive_without_printing_values()
    {
        var password = Assert.Single(Resolve(EnvStep("DB_PASSWORD", "hunter2")));
        Assert.True(password.IsSensitive);
        Assert.Null(password.Value);
        Assert.Equal("matched by name; masked: its value was not read", password.Note);

        var key = Assert.Single(Resolve(EnvStep("API_KEY", "hunter3")));
        Assert.True(key.IsSensitive);
        Assert.Equal("matched by name; hidden: GitLab does not return its value", key.Note);

        var signing = Assert.Single(Resolve(EnvStep("SIGNING_KEY", "other")));
        Assert.True(signing.IsSensitive);
        Assert.Equal("matched by name; masked; the GitLab value differs from the value seen here, so the deployment may be out of date or the value was transformed", signing.Note);
        Assert.DoesNotContain("other", signing.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Notes_file_and_protected_variables_and_does_not_compare_a_file_path()
    {
        var kube = Assert.Single(Resolve(EnvStep("KUBECONFIG", "/builds/tmp/KUBECONFIG")));

        Assert.Equal("matched by name; protected: only pipelines on protected branches and tags get it; a file variable: the job gets the path of a temporary file that holds the value", kube.Note);
        Assert.True(kube.IsSensitive);
    }

    [Fact]
    public void Matches_Kubernetes_layers_and_ignores_others()
    {
        Assert.Equal(3, Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesWorkload, Source = "w", Reference = new WorkloadEnvRef("api", "app", "DB_TIMEOUT") }).Count);
        Assert.Equal(3, Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "c", Reference = new ConfigMapKeyRef("cfg", "DB_TIMEOUT") }).Count);
        Assert.Single(Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "s", Reference = new SecretKeyRef("sec", "DB_PASSWORD") }));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.Helm, Source = "h", Reference = new HelmValueRef("DB_TIMEOUT") }));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "e", Reference = new ExternalSecretRef(null, null, "DB_TIMEOUT") }));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.GitLabCi, Source = "g", Reference = new GitLabVariableRef(GitLabVariableLevel.Project, "p", "DB_TIMEOUT", "*") }));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.GitLabCi, Source = "g", Reference = new GitLabCiVariableRef(".gitlab-ci.yml", 1, "DB_TIMEOUT") }));
        Assert.Empty(Resolve(EnvStep("UNKNOWN", "1")));
        Assert.Empty(Resolve(EnvStep("DB_TIMEOUT", "1"), o => o.MatchByName = false));
    }

    [Fact]
    public void Follows_explicit_links()
    {
        var chain = Resolve(EnvStep("Db__Timeout", "30"), o => o.Map("Db__Timeout", "DB_TIMEOUT"));
        Assert.Equal(["5", "10", "30"], chain.Select(s => s.Value));
        Assert.All(chain, s => Assert.False(s.IsInferred));
        Assert.Equal("linked explicitly", chain[^1].Note);

        Assert.Equal(3, Resolve(EnvStep("db__timeout", "30"), o => o.Links["Db__Timeout"] = " ${DB_TIMEOUT} ").Count);
        Assert.Equal(3, Resolve(EnvStep("DB_TIMEOUT", "30"), o => o.Links["DB_TIMEOUT"] = " ").Count);

        var diagnostics = new TraceDiagnostics();
        Assert.Empty(Resolve(EnvStep("Db__Timeout", "30"), o => o.Map("Db__Timeout", "NOPE"), diagnostics));
        Assert.Equal("Db__Timeout is linked to GitLab variable NOPE, but no such variable applies.", Assert.Single(diagnostics.Warnings));

        var options = new GitLabTraceOptions();
        Assert.Throws<ArgumentException>(() => options.Map(" ", "a"));
        Assert.Throws<ArgumentException>(() => options.Map("a", " "));
        Assert.Same(options, options.Map("a", "b"));
    }

    [Fact]
    public void Reads_global_and_job_variables_and_script_links_from_the_ci_file()
    {
        var options = new GitLabTraceOptions().AddCiFileText(CiFile, ".gitlab-ci.yml");

        Assert.Equal(
            ["DB_TIMEOUT=15@-", "GREETING=hello@-", "ALIAS=$DB_TIMEOUT@-", "EMPTY=@-", "DB_TIMEOUT=25@deploy", "BRACED=${SIGNING_KEY}@deploy"],
            options.Variables.Select(v => $"{v.Name}={v.Value}@{v.Job ?? "-"}"));
        Assert.Equal([2, 3, 6, 7, 12, 13], options.Variables.Select(v => v.Line));
        Assert.Equal(["DB_PASSWORD>DB_PASSWORD", "TOKEN_ALIAS>API_KEY", "MISSING>NOT_THERE"], options.Mappings.Select(m => $"{m.Name}>{m.Key}"));
        Assert.Equal(".gitlab-ci.yml line 16", options.Mappings.First().Where);
    }

    [Fact]
    public void Puts_ci_file_variables_below_settings_variables()
    {
        var chain = Resolve(EnvStep("DB_TIMEOUT", "30"), o => o.AddCiFileText(CiFile, ".gitlab-ci.yml"));

        Assert.Equal(["15", "25", "5", "10", "30"], chain.Select(s => s.Value));
        Assert.Equal("GitLab CI variable DB_TIMEOUT", chain[0].Source);
        Assert.Equal("GitLab CI variable DB_TIMEOUT (job deploy)", chain[1].Source);
        Assert.Equal(".gitlab-ci.yml line 12", chain[1].Location);
        Assert.Equal(new GitLabCiVariableRef(".gitlab-ci.yml", 12, "DB_TIMEOUT"), chain[1].Reference);
        Assert.False(chain[1].IsSensitive);

        var greeting = Assert.Single(Resolve(EnvStep("GREETING", "hello"), o => o.AddCiFileText(CiFile, ".gitlab-ci.yml")));
        Assert.Equal("matched by name", greeting.Note);
    }

    [Fact]
    public void Links_script_exports_from_the_ci_file_and_warns_about_missing_targets()
    {
        var password = Assert.Single(Resolve(EnvStep("DB_PASSWORD", null), o => o.AddCiFileText(CiFile, ".gitlab-ci.yml")));
        Assert.False(password.IsInferred);
        Assert.Equal(".gitlab-ci.yml line 16 sets DB_PASSWORD from $DB_PASSWORD; masked: its value was not read", password.Note);

        Assert.Single(Resolve(EnvStep("TOKEN_ALIAS", null), o => o.AddCiFileText(CiFile, ".gitlab-ci.yml")));

        var diagnostics = new TraceDiagnostics();
        Assert.Empty(Resolve(EnvStep("MISSING", "1"), o => o.AddCiFileText(CiFile, ".gitlab-ci.yml"), diagnostics));
        Assert.Equal("MISSING is linked to GitLab variable NOT_THERE (.gitlab-ci.yml line 18), but no such variable applies.", Assert.Single(diagnostics.Warnings));
    }

    [Fact]
    public void Follows_a_ci_variable_that_refers_to_another_variable()
    {
        var resolver = Resolver(o => o.AddCiFileText(CiFile, ".gitlab-ci.yml"));
        var alias = Assert.Single(resolver.Resolve(EnvStep("ALIAS", "30"), new TraceDiagnostics()));
        Assert.Equal("$DB_TIMEOUT", alias.Value);
        Assert.Equal("matched by name", alias.Note);

        var origins = resolver.Resolve(alias, new TraceDiagnostics());

        Assert.Equal(["15", "25", "5", "10", "30"], origins.Select(s => s.Value));
        Assert.All(origins, s => Assert.False(s.IsInferred));
        Assert.Equal(".gitlab-ci.yml line 6 sets ALIAS from $DB_TIMEOUT", origins[0].Note);

        var braced = Assert.Single(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.GitLabCi, Source = "g", Reference = new GitLabCiVariableRef(".gitlab-ci.yml", 13, "BRACED") }, new TraceDiagnostics()));
        Assert.Equal("SIGNING_KEY", ((GitLabVariableRef)braced.Reference!).Key);

        Assert.Empty(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.GitLabCi, Source = "g", Reference = new GitLabCiVariableRef(".gitlab-ci.yml", 2, "DB_TIMEOUT") }, new TraceDiagnostics()));
    }

    [Fact]
    public void Follows_a_variable_that_refers_to_itself_to_the_other_levels_once()
    {
        const string prefix = "CFGLSELF_";
        var yaml = new StringBuilder($"variables:\n  {prefix}VERSION: \"1.0\"\n");
        for (var i = 0; i < 5; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"trigger-service-{i}:\n  variables:\n    {prefix}VERSION: ${prefix}VERSION\n  trigger:\n    include: service-{i}/.gitlab-ci.yml\n");
        }

        var variables = new GitLabVariables([new(prefix + "VERSION", "1.2.3", GitLabVariableLevel.Project, "contoso/api")]);

        var setting = TraceOne(prefix, "VERSION", "1.2.3", variables, yaml.ToString()).Find("VERSION")!;

        var chain = Assert.Single(setting.Steps).Origins;
        Assert.Equal(["1.0", .. Enumerable.Repeat($"${prefix}VERSION", 5), "1.2.3"], chain.Select(s => s.Value));
        Assert.All(chain.Skip(1).SkipLast(1), job =>
        {
            Assert.Equal(["1.0", "1.2.3"], job.Origins.Select(s => s.Value));
            Assert.Equal([true, false], job.Origins.Select(s => s.IsInferred));
            Assert.True(job.Origins[^1].IsEffective);
        });
        Assert.Equal(18, Count(setting.Steps));
    }

    [Fact(Timeout = 30_000)]
    public async Task Keeps_the_trace_small_when_many_jobs_refer_to_the_variable_itself()
    {
        // Without leaving the self references out, each of the 40 jobs leads to the 39 others, for each level followed.
        const string prefix = "CFGLLOOP_";
        var yaml = new StringBuilder($"variables:\n  {prefix}PATH: /usr/bin\n");
        for (var i = 0; i < 40; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"job-{i}:\n  variables:\n    {prefix}PATH: ${prefix}PATH\n  script: [make]\n");
        }

        var report = await Task.Run(() => TraceOne(prefix, "PATH", "/usr/bin", new GitLabVariables([]), yaml.ToString()), TestContext.Current.CancellationToken);

        var steps = report.Find("PATH")!.Steps;
        Assert.Equal(41, Assert.Single(steps).Origins.Count);
        Assert.Equal(82, Count(steps));
    }

    private static int Count(IEnumerable<ProvenanceStep> steps) => steps.Sum(s => 1 + Count(s.Origins));

    private const string DeployJobs = """
        stages: [build, deploy]

        build:
          stage: build
          script:
            - dotnet publish

        deploy:staging:
          stage: deploy
          environment: staging
          variables:
            PFX_ASPNETCORE_ENVIRONMENT: Staging
            PFX_Db__Host: $PFX_STAGING_DB_HOST
          script:
            - export PFX_Db__Timeout=$PFX_STAGING_TIMEOUT

        deploy:production:
          stage: deploy
          environment:
            name: production
            url: https://example.com
          variables:
            PFX_ASPNETCORE_ENVIRONMENT: Production
            PFX_Db__Host: $PFX_PROD_DB_HOST
          script:
            - export PFX_Db__Timeout=$PFX_PROD_TIMEOUT

        qa:
          stage: deploy
          environment: { name: qa, url: https://qa.example.com }
          variables:
            PFX_ASPNETCORE_ENVIRONMENT: QA
        """;

    private static ProvenanceReport TraceMany(string prefix, Dictionary<string, string> values, GitLabVariables variables, string ciFile, string? environment)
    {
        var environmentVariables = values.ToDictionary(v => prefix + v.Key, v => (string?)v.Value);
        foreach (var (name, value) in environmentVariables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            return ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = environmentVariables)
                .WithGitLab(variables, o =>
                {
                    o.Environment = environment;
                    o.AddCiFileText(ciFile.Replace("PFX_", prefix, StringComparison.Ordinal), ".gitlab-ci.yml");
                })
                .Trace();
        }
        finally
        {
            foreach (var name in environmentVariables.Keys)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    private static GitLabVariables DeployVariables(string prefix) => new(
    [
        new(prefix + "STAGING_DB_HOST", "db-staging.example", GitLabVariableLevel.Project, "contoso/api"),
        new(prefix + "PROD_DB_HOST", "db-prod.example", GitLabVariableLevel.Project, "contoso/api"),
        new(prefix + "STAGING_TIMEOUT", "5", GitLabVariableLevel.Project, "contoso/api"),
        new(prefix + "PROD_TIMEOUT", "9", GitLabVariableLevel.Project, "contoso/api"),
    ]);

    [Theory]
    [InlineData("staging", "Staging", "deploy:staging", 12, "STAGING")]
    [InlineData("production", "Production", "deploy:production", 23, "PROD")]
    public void Uses_only_the_job_that_deploys_to_the_environment(string environment, string value, string job, int line, string target)
    {
        const string prefix = "CFGLJOB_";
        var values = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = value, ["Db__Host"] = $"db-{environment}.example", ["Db__Timeout"] = "5" };
        var report = TraceMany(prefix, values, DeployVariables(prefix), DeployJobs, environment);

        var ci = Assert.Single(Assert.Single(report.Find("ASPNETCORE_ENVIRONMENT")!.Steps).Origins);
        Assert.Equal($"GitLab CI variable {prefix}ASPNETCORE_ENVIRONMENT (job {job})", ci.Source);
        Assert.Equal($".gitlab-ci.yml line {line}", ci.Location);
        Assert.Equal("matched by name", ci.Note);
        Assert.True(ci.IsEffective);

        var host = Assert.Single(Assert.Single(report.Find("Db:Host")!.Steps).Origins);
        Assert.Equal($"GitLab CI variable {prefix}Db__Host (job {job})", host.Source);
        Assert.Equal($"GitLab project variable {prefix}{target}_DB_HOST", Assert.Single(host.Origins).Source);

        var timeout = Assert.Single(Assert.Single(report.Find("Db:Timeout")!.Steps).Origins);
        Assert.Equal($"GitLab project variable {prefix}{target}_TIMEOUT", timeout.Source);
        Assert.False(timeout.IsInferred);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("GitLab CI jobs", StringComparison.Ordinal));

        // The flow form, environment: { name: qa }, names the environment too.
        var qa = TraceMany(prefix, new() { ["ASPNETCORE_ENVIRONMENT"] = "QA" }, DeployVariables(prefix), DeployJobs, "qa");
        Assert.Equal($"GitLab CI variable {prefix}ASPNETCORE_ENVIRONMENT (job qa)", Assert.Single(Assert.Single(qa.Find("ASPNETCORE_ENVIRONMENT")!.Steps).Origins).Source);
    }

    [Fact]
    public void Warns_and_infers_when_jobs_disagree_and_nothing_picks_one()
    {
        const string prefix = "CFGLJOBS_";
        var report = TraceMany(prefix, new() { ["ASPNETCORE_ENVIRONMENT"] = "Staging", ["Db__Timeout"] = "5" }, DeployVariables(prefix), DeployJobs, null);

        // The tracer marks the last step effective, so the alternative with the value seen here goes last.
        var ci = Assert.Single(report.Find("ASPNETCORE_ENVIRONMENT")!.Steps).Origins;
        Assert.Equal(["Production", "QA", "Staging"], ci.Select(s => s.Value));
        Assert.All(ci, s => Assert.True(s.IsInferred));
        Assert.All(ci, s => Assert.Equal("matched by name", s.Note));
        var timeout = Assert.Single(report.Find("Db:Timeout")!.Steps).Origins;
        Assert.Equal([$"GitLab project variable {prefix}PROD_TIMEOUT", $"GitLab project variable {prefix}STAGING_TIMEOUT"], timeout.Select(s => s.Source));
        Assert.All(timeout, s => Assert.True(s.IsInferred));
        Assert.All(timeout, s => Assert.DoesNotContain("differs", s.Note, StringComparison.Ordinal));

        const string why = ", and the trace cannot tell which job deployed this, so their steps are marked inferred. Set Environment to leave out the jobs for other environments.";
        Assert.Contains($"GitLab CI jobs deploy:staging, deploy:production, qa set {prefix}ASPNETCORE_ENVIRONMENT differently{why}", report.Warnings);
        Assert.Contains($"GitLab CI jobs deploy:staging, deploy:production link {prefix}Db__Timeout to different values{why}", report.Warnings);
        Assert.All(report.Warnings, w =>
        {
            Assert.DoesNotContain("Staging", w, StringComparison.Ordinal);
            Assert.DoesNotContain(".example", w, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Follows_a_link_in_each_job_that_has_it()
    {
        const string prefix = "CFGLSAME_";
        const string yaml = """
            deploy-staging:
              variables:
                PFX_DB_HOST: db-staging.example
              script:
                - export PFX_Db__Host=$PFX_DB_HOST
            deploy-production:
              variables:
                PFX_DB_HOST: db-prod.example
              script:
                - export PFX_Db__Host=$PFX_DB_HOST
            """;

        var report = TraceMany(prefix, new() { ["Db__Host"] = "db-staging.example" }, new GitLabVariables([]), yaml, null);

        var host = Assert.Single(report.Find("Db:Host")!.Steps).Origins;
        Assert.Equal([$"GitLab CI variable {prefix}DB_HOST (job deploy-production)", $"GitLab CI variable {prefix}DB_HOST (job deploy-staging)"], host.Select(s => s.Source));
        Assert.All(host, s => Assert.True(s.IsInferred));
        Assert.Contains(report.Warnings, w => w.StartsWith($"GitLab CI jobs deploy-staging, deploy-production link {prefix}Db__Host to different values", StringComparison.Ordinal));

        // When the jobs' links end at the same variable, it is one certain link.
        const string agreeing = "deploy-staging:\n  script:\n    - export PFX_Db__Host=$PFX_DB_HOST\ndeploy-production:\n  script:\n    - export PFX_Db__Host=$PFX_DB_HOST\n";
        var shared = TraceMany(prefix, new() { ["Db__Host"] = "db.example" }, new GitLabVariables([new(prefix + "DB_HOST", "db.example", GitLabVariableLevel.Project, "contoso/api")]), agreeing, null);
        var link = Assert.Single(Assert.Single(shared.Find("Db:Host")!.Steps).Origins);
        Assert.Equal($"GitLab project variable {prefix}DB_HOST", link.Source);
        Assert.False(link.IsInferred);
    }

    [Theory]
    [InlineData("export", "PASSWORD,USER,USER")]
    [InlineData("export", "USER,PASSWORD,USER")]
    [InlineData("export", "PASSWORD,USER,PASSWORD")]
    [InlineData("export", "USER,USER,PASSWORD")]
    [InlineData("variables", "PASSWORD,USER")]
    [InlineData("variables", "USER,PASSWORD")]
    public void Redacts_every_alternative_when_one_may_be_secret(string form, string order)
    {
        const string prefix = "CFGLALT_";
        const string secret = "fake-smtp-pass";
        var yaml = new StringBuilder();
        var job = 0;
        foreach (var source in order.Split(','))
        {
            yaml.Append(CultureInfo.InvariantCulture, $"job-{job++}:\n");
            yaml.Append(form == "export" ? $"  script:\n    - export PFX_MAIL_AUTH=$PFX_SMTP_{source}\n" : $"  variables:\n    PFX_MAIL_AUTH: $PFX_SMTP_{source}\n");
        }

        // Neither is masked; one is named like a secret. The value seen here is neither of them.
        var variables = new GitLabVariables(
        [
            new(prefix + "SMTP_PASSWORD", secret, GitLabVariableLevel.Project, "contoso/api"),
            new(prefix + "SMTP_USER", "fake-user", GitLabVariableLevel.Project, "contoso/api"),
        ]);
        var report = TraceMany(prefix, new() { ["MAIL_AUTH"] = $"smtp://fake-user:{secret}@mail" }, variables, yaml.ToString(), null);

        AssertNowhere(report, "MAIL_AUTH", secret);
        var setting = report.Find("MAIL_AUTH")!;
        Assert.True(setting.IsSensitive);
        Assert.Equal("***", setting.Value);
        Assert.All(Flatten(setting.Steps), s => Assert.True(s.IsSensitive));
    }

    [Fact]
    public void Treats_hidden_jobs_that_disagree_as_alternatives()
    {
        const string prefix = "CFGLHIDDEN_";
        const string yaml = """
            .staging:
              environment: staging
              variables:
                PFX_DB_HOST: db-staging.example
            .prod:
              variables:
                PFX_DB_HOST: db-prod.example
            deploy-staging:
              extends: .staging
            deploy-production:
              extends: .prod
            """;

        var report = TraceMany(prefix, new() { ["DB_HOST"] = "db-staging.example" }, new GitLabVariables([]), yaml, null);

        var host = Assert.Single(report.Find("DB_HOST")!.Steps).Origins;
        Assert.Equal(["db-prod.example", "db-staging.example"], host.Select(s => s.Value));
        Assert.All(host, s => Assert.True(s.IsInferred));
        Assert.Contains(report.Warnings, w => w.StartsWith($"GitLab CI jobs .staging, .prod set {prefix}DB_HOST differently", StringComparison.Ordinal));

        // A hidden job counts whatever environment it names, since a job that extends it can name another one.
        var production = TraceMany(prefix, new() { ["DB_HOST"] = "db-prod.example" }, new GitLabVariables([]), yaml, "production");
        var both = Assert.Single(production.Find("DB_HOST")!.Steps).Origins;
        Assert.Equal(["db-staging.example", "db-prod.example"], both.Select(s => s.Value));
        Assert.All(both, s => Assert.True(s.IsInferred));
    }

    [Fact]
    public void Keeps_jobs_that_do_not_name_another_environment_themselves()
    {
        const string prefix = "CFGLEXT_";
        const string secret = "fake-smtp-pass";
        const string yaml = """
            .prod:
              environment: production
            deploy-api:
              extends: .prod
              script:
                - export PFX_MAIL_AUTH=$PFX_SMTP_PASSWORD
            review:
              environment: review/$CI_COMMIT_REF_SLUG
              script:
                - export PFX_REVIEW_AUTH=$PFX_SMTP_PASSWORD
            smoke:
              environment: production
            """;
        var variables = new GitLabVariables([new(prefix + "SMTP_PASSWORD", secret, GitLabVariableLevel.Project, "contoso/api")]);

        var report = TraceMany(prefix, new() { ["MAIL_AUTH"] = secret, ["REVIEW_AUTH"] = secret }, variables, yaml, "production");

        AssertNowhere(report, "MAIL_AUTH", secret);
        AssertNowhere(report, "REVIEW_AUTH", secret);
        Assert.False(Assert.Single(Assert.Single(report.Find("MAIL_AUTH")!.Steps).Origins).IsInferred);
        Assert.False(Assert.Single(Assert.Single(report.Find("REVIEW_AUTH")!.Steps).Origins).IsInferred);
    }

    [Fact]
    public void Follows_a_job_variable_to_its_own_job_only()
    {
        const string yaml = "variables:\n  TIMEOUT: \"1\"\nstaging:\n  variables:\n    TIMEOUT: \"5\"\n    ALIAS: $TIMEOUT\nproduction:\n  variables:\n    TIMEOUT: \"9\"\n";
        var diagnostics = new TraceDiagnostics();
        var resolver = Resolver(o => o.AddCiFileText(yaml, ".gitlab-ci.yml"), new GitLabVariables([]));

        var origins = resolver.Resolve(new ProvenanceStep { Kind = LayerKind.GitLabCi, Source = "g", Reference = new GitLabCiVariableRef(".gitlab-ci.yml", 6, "ALIAS") }, diagnostics);

        Assert.Equal(["1", "5"], origins.Select(s => s.Value));
        Assert.All(origins, s => Assert.False(s.IsInferred));
        Assert.Empty(diagnostics.Warnings);
    }

    [Fact(Timeout = 30_000)]
    public async Task Does_work_in_proportion_to_the_jobs_that_link_a_name()
    {
        // Each job sets X and links MAIL_AUTH to it. Following each link may look at its own job only. There are fewer
        // jobs than the bound on one step, so every link is followed.
        const int jobs = 900;
        var yaml = new StringBuilder();
        for (var i = 0; i < jobs; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"job-{i}:\n  variables:\n    X: v{i}\n  script:\n    - export MAIL_AUTH=$X\n");
        }

        var resolver = Resolver(o => o.AddCiFileText(yaml.ToString(), ".gitlab-ci.yml"), new GitLabVariables([]));
        var diagnostics = new TraceDiagnostics();

        var steps = await Task.Run(
            () =>
            {
                var found = resolver.Resolve(EnvStep("MAIL_AUTH", "v7"), diagnostics);
                foreach (var step in found)
                {
                    resolver.Resolve(step, diagnostics);
                }

                return found;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(jobs, steps.Count);
        Assert.Equal("v7", steps[^1].Value);
        Assert.InRange(resolver.Examined, jobs, 20L * jobs);
    }

    [Fact(Timeout = 120_000)]
    public async Task Bounds_the_work_when_every_link_sees_many_hidden_definitions()
    {
        // Each job links MAIL_AUTH to X, and every job sees each hidden job's X.
        const int jobs = 1_000;
        const int hidden = 1_000;
        var yaml = new StringBuilder();
        for (var i = 0; i < hidden; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $".h-{i}:\n  variables:\n    X: v{i}\n");
        }

        for (var i = 0; i < jobs; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"job-{i}:\n  script:\n    - export MAIL_AUTH=$X\n");
        }

        var resolver = Resolver(o => o.AddCiFileText(yaml.ToString(), ".gitlab-ci.yml"), new GitLabVariables([]));
        var diagnostics = new TraceDiagnostics();

        var steps = await Task.Run(() => resolver.Resolve(EnvStep("MAIL_AUTH", "v7"), diagnostics), TestContext.Current.CancellationToken);

        Assert.InRange(resolver.Examined, jobs, 20L * (jobs + hidden));
        Assert.InRange(steps.Count, 1, 1_000);
        Assert.All(steps, s =>
        {
            Assert.True(s.IsSensitive);
            Assert.True(s.IsInferred);
        });
        Assert.Contains("MAIL_AUTH is linked or set in more GitLab CI jobs and definitions than the trace follows for one step (1,000), so not all of them were followed; the steps listed are marked inferred and sensitive.", diagnostics.Warnings);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Redacts_links_that_come_to_the_same_value_when_one_is_named_like_a_secret(bool secretFirst)
    {
        const string prefix = "CFGLSAMEVAL_";
        const string secret = "fake-s3cr3t";
        const string secretJob = "job-a:\n  variables:\n    PFX_SMTP_PASSWORD: fake-s3cr3t\n  script:\n    - export PFX_MAIL_AUTH=$PFX_SMTP_PASSWORD\n";
        const string plainJob = "job-b:\n  variables:\n    PFX_PLAIN_AUTH: fake-s3cr3t\n  script:\n    - export PFX_MAIL_AUTH=$PFX_PLAIN_AUTH\n";

        var report = TraceMany(prefix, new() { ["MAIL_AUTH"] = secret }, new GitLabVariables([]), secretFirst ? secretJob + plainJob : plainJob + secretJob, null);

        AssertNowhere(report, "MAIL_AUTH", secret);
        var setting = report.Find("MAIL_AUTH")!;
        Assert.True(setting.IsSensitive);
        Assert.Equal("***", setting.Value);
        Assert.All(Flatten(setting.Steps), s => Assert.True(s.IsSensitive));
    }

    [Fact]
    public void Compares_what_a_reference_comes_to_in_each_job()
    {
        const string prefix = "CFGLREF_";
        const string yaml = """
            deploy-staging:
              variables:
                PFX_HOST_VAR: db-staging.example
                PFX_DB_HOST: $PFX_HOST_VAR
              script:
                - export PFX_Db__Host=$PFX_DB_HOST
            deploy-production:
              variables:
                PFX_HOST_VAR: db-production.example
                PFX_DB_HOST: $PFX_HOST_VAR
              script:
                - export PFX_Db__Host=$PFX_DB_HOST
            """;

        var report = TraceMany(prefix, new() { ["Db__Host"] = "db-staging.example", ["DB_HOST"] = "db-staging.example" }, new GitLabVariables([]), yaml, null);

        var host = Assert.Single(report.Find("Db:Host")!.Steps).Origins;
        Assert.Equal([$"GitLab CI variable {prefix}DB_HOST (job deploy-production)", $"GitLab CI variable {prefix}DB_HOST (job deploy-staging)"], host.Select(s => s.Source));
        Assert.All(host, s => Assert.True(s.IsInferred));
        Assert.Contains(report.Warnings, w => w.StartsWith($"GitLab CI jobs deploy-staging, deploy-production link {prefix}Db__Host to different values", StringComparison.Ordinal));
        var byName = Assert.Single(report.Find("DB_HOST")!.Steps).Origins;
        Assert.Equal(2, byName.Count);
        Assert.All(byName, s => Assert.True(s.IsInferred));
        Assert.Contains(report.Warnings, w => w.StartsWith($"GitLab CI jobs deploy-staging, deploy-production set {prefix}DB_HOST differently", StringComparison.Ordinal));

        // When the reference comes to the same variable in each job, it is one certain link.
        const string agreeing = "variables:\n  PFX_HOST_VAR: db.example\ndeploy-staging:\n  variables:\n    PFX_DB_HOST: $PFX_HOST_VAR\n  script:\n    - export PFX_Db__Host=$PFX_DB_HOST\ndeploy-production:\n  variables:\n    PFX_DB_HOST: $PFX_HOST_VAR\n  script:\n    - export PFX_Db__Host=$PFX_DB_HOST\n";
        var shared = TraceMany(prefix, new() { ["Db__Host"] = "db.example" }, new GitLabVariables([]), agreeing, null);
        Assert.False(Assert.Single(Assert.Single(shared.Find("Db:Host")!.Steps).Origins).IsInferred);
    }

    [Fact]
    public void Keeps_a_hidden_job_whatever_environment_it_names()
    {
        // A job that extends a hidden job can name another environment, so the hidden job's environment: decides nothing.
        const string prefix = "CFGLHIDENV_";
        const string secret = "fake-smtp-pass";
        const string yaml = """
            .deploy:
              environment: production
              variables:
                PFX_MAIL_AUTH: $PFX_SMTP_PASSWORD
            deploy-staging:
              extends: .deploy
              environment: staging
            """;
        var variables = new GitLabVariables([new(prefix + "SMTP_PASSWORD", secret, GitLabVariableLevel.Project, "contoso/api")]);

        var report = TraceMany(prefix, new() { ["MAIL_AUTH"] = secret }, variables, yaml, "staging");

        AssertNowhere(report, "MAIL_AUTH", secret);
        var setting = report.Find("MAIL_AUTH")!;
        Assert.True(setting.IsSensitive);
        Assert.Equal($"GitLab CI variable {prefix}MAIL_AUTH (job .deploy)", Assert.Single(Assert.Single(setting.Steps).Origins).Source);
    }

    [Theory]
    [InlineData(".tpl:\n  script:\n    - export PFX_MAIL_AUTH=$PFX_SMTP_PASSWORD\ndeploy:\n  extends: .tpl\ntest:\n  script:\n    - export PFX_MAIL_AUTH=$PFX_SMTP_USER\n", ".tpl, test link")]
    [InlineData("job-a:\n  variables:\n    PFX_MAIL_AUTH: $PFX_SMTP_PASSWORD\njob-b:\n  script:\n    - export PFX_MAIL_AUTH=$PFX_SMTP_USER\n", "job-b, job-a link or set")]
    public void Keeps_every_job_that_may_set_a_linked_name_as_an_alternative(string yaml, string jobs)
    {
        const string prefix = "CFGLMIXED_";
        const string secret = "fake-smtp-pass";
        var variables = new GitLabVariables(
        [
            new(prefix + "SMTP_PASSWORD", secret, GitLabVariableLevel.Project, "contoso/api"),
            new(prefix + "SMTP_USER", "fake-user", GitLabVariableLevel.Project, "contoso/api"),
        ]);

        var report = TraceMany(prefix, new() { ["MAIL_AUTH"] = "fake-user" }, variables, yaml, null);

        AssertNowhere(report, "MAIL_AUTH", secret);
        var setting = report.Find("MAIL_AUTH")!;
        Assert.True(setting.IsSensitive);
        Assert.Equal("***", setting.Value);
        var origins = Assert.Single(setting.Steps).Origins;
        Assert.Equal(2, origins.Count);
        Assert.All(origins, s => Assert.True(s.IsInferred));
        Assert.All(Flatten(setting.Steps), s => Assert.True(s.IsSensitive));
        Assert.Contains(report.Warnings, w => w.StartsWith($"GitLab CI jobs {jobs} {prefix}MAIL_AUTH to values that cannot be compared", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Marks_a_link_to_a_variable_set_from_a_secret_sensitive_whatever_is_traced_first(bool otherFirst)
    {
        // B leads to C, C to A, and A to B in one job and to SMTP_PASSWORD in another.
        const string yaml = """
            variables:
              B: $C
              C: $A
            job-a:
              variables:
                A: $B
            job-b:
              variables:
                A: $SMTP_PASSWORD
            other-a:
              script:
                - export OTHER=$A
            other-b:
              script:
                - export OTHER=$PLAIN
            mail-a:
              script:
                - export MAIL_AUTH=$B
            mail-b:
              script:
                - export MAIL_AUTH=$USER_VAR
            """;
        var variables = new GitLabVariables(
        [
            new("PLAIN", "p", GitLabVariableLevel.Project, "contoso/api"),
            new("USER_VAR", "u", GitLabVariableLevel.Project, "contoso/api"),
        ]);
        var resolver = Resolver(o => o.AddCiFileText(yaml, ".gitlab-ci.yml"), variables);
        var diagnostics = new TraceDiagnostics();

        if (otherFirst)
        {
            resolver.Resolve(EnvStep("OTHER", "p"), diagnostics);
        }

        var mail = resolver.Resolve(EnvStep("MAIL_AUTH", "u"), diagnostics);

        Assert.Equal(2, mail.Count);
        Assert.All(mail, s => Assert.True(s.IsSensitive));
    }

    [Fact]
    public void Orders_alternatives_by_the_value_seen_through_a_reference()
    {
        const string prefix = "CFGLSEEN_";
        const string yaml = "variables:\n  PFX_ALIAS: $PFX_TIMEOUT\nstaging:\n  variables:\n    PFX_TIMEOUT: \"5\"\nproduction:\n  variables:\n    PFX_TIMEOUT: \"9\"\n";

        var report = TraceMany(prefix, new() { ["ALIAS"] = "5" }, new GitLabVariables([]), yaml, null);

        // The tracer marks the last step effective, so the alternative with the value seen at the start goes last.
        var alias = Assert.Single(Assert.Single(report.Find("ALIAS")!.Steps).Origins);
        Assert.Equal(["9", "5"], alias.Origins.Select(s => s.Value));
        Assert.True(alias.Origins[^1].IsEffective);
    }

    [Fact]
    public void Does_not_take_an_alias_a_tag_or_a_comment_for_an_environment_name()
    {
        const string yaml = """
            .env: &prod
              name: production
            alias:
              environment: *prod
              variables:
                X: alias
            tag:
              environment: !reference [.env]
              variables:
                X: tag
            anchor:
              environment: &named production
              variables:
                X: anchor
            comment:
              environment:  # named below
                name: staging
              variables:
                X: comment
            production:
              environment: production
              variables:
                X: production
            """;
        var resolver = Resolver(
            o =>
            {
                o.Environment = "staging";
                o.AddCiFileText(yaml, ".gitlab-ci.yml");
            },
            new GitLabVariables([]));

        var origins = resolver.Resolve(EnvStep("X", "comment"), new TraceDiagnostics());

        Assert.Equal(["alias", "tag", "anchor", "comment"], origins.Select(s => s.Value));
    }

    [Fact]
    public void Counts_workflow_rule_variables_as_alternatives()
    {
        // A workflow rule's variables apply only when the rule matches, which the trace cannot tell.
        const string prefix = "CFGLFLOW_";
        const string secret = "fake-smtp-pass";
        const string yaml = """
            workflow:
              rules:
                - if: $CI_COMMIT_BRANCH == "main"
                  variables:
                    PFX_MAIL_AUTH: $PFX_SMTP_PASSWORD
                - when: always
            variables:
              PFX_MAIL_AUTH: $PFX_SMTP_USER
            """;
        var variables = new GitLabVariables(
        [
            new(prefix + "SMTP_PASSWORD", secret, GitLabVariableLevel.Project, "contoso/api"),
            new(prefix + "SMTP_USER", "fake-user", GitLabVariableLevel.Project, "contoso/api"),
        ]);

        var report = TraceMany(prefix, new() { ["MAIL_AUTH"] = "fake-user" }, variables, yaml, null);

        AssertNowhere(report, "MAIL_AUTH", secret);
        var setting = report.Find("MAIL_AUTH")!;
        Assert.True(setting.IsSensitive);
        var origins = Assert.Single(setting.Steps).Origins;
        Assert.Equal([$"GitLab CI variable {prefix}MAIL_AUTH (job workflow)", $"GitLab CI variable {prefix}MAIL_AUTH"], origins.Select(s => s.Source));
        Assert.All(origins, s => Assert.True(s.IsInferred));
    }

    [Fact]
    public void Lists_one_link_per_name_with_the_later_line()
    {
        const string yaml = "staging:\n  script:\n    - export Db__Host=$STAGING_DB_HOST\nproduction:\n  script:\n    - export Db__Host=$PROD_DB_HOST\n    - export db__host=$OTHER\n";

        var options = new GitLabTraceOptions().AddCiFileText(yaml, ".gitlab-ci.yml");

        Assert.Equal(["Db__Host>PROD_DB_HOST@.gitlab-ci.yml line 6", "db__host>OTHER@.gitlab-ci.yml line 7"], options.Mappings.Select(m => $"{m.Name}>{m.Key}@{m.Where}"));
    }

    [Fact]
    public void Reads_job_names_with_colons_and_quotes_and_a_variables_block_with_an_anchor()
    {
        const string yaml = """
            variables: &defaults
              GLOBAL_A: g
            "deploy production":
              variables:
                Q: q
            deploy:production:
              variables:
                A: 1
            deploy:staging:  # same stage, other environment
              variables:
                A: 2
            'quoted two':
              image: alpine:3.20
              variables:
                R: r
            """;

        var options = new GitLabTraceOptions().AddCiFileText(yaml, ".gitlab-ci.yml");

        Assert.Equal(
            ["GLOBAL_A=g@-", "Q=q@deploy production", "A=1@deploy:production", "A=2@deploy:staging", "R=r@quoted two"],
            options.Variables.Select(v => $"{v.Name}={v.Value}@{v.Job ?? "-"}"));
    }

    [Fact]
    public void Ends_a_variables_block_and_reads_odd_lines_safely()
    {
        const string yaml = "variables:\r\n  A: 1\r\n  B:\r\n    description: no value\r\n  - item\r\n    value: ignored\r\n  C:\r\nnext:\r\n  X: $A\r\n  - 'Y=${B}'\r\n  - \"export Z=$C\"\r\n  - W=$C trailing\r\n  - \r\nbuild:\r\n  variables:\r\n      D: 4\r\n      E:\r\n        value: 5\r\n";

        var options = new GitLabTraceOptions().AddCiFileText(yaml, "ci.yml");

        Assert.Equal(["A=1@-", "D=4@build", "E=5@build"], options.Variables.Select(v => $"{v.Name}={v.Value}@{v.Job ?? "-"}"));
        Assert.Equal(17, options.Variables[2].Line);
        Assert.Equal(["X>A", "Y>B", "Z>C"], options.Mappings.Select(m => $"{m.Name}>{m.Key}"));
    }

    [Fact]
    public void Reads_configured_ci_files_once_and_warns_about_missing_ones()
    {
        var path = Path.Join(Path.GetTempPath(), $"cf-gitlab-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, "deploy:\n  script:\n    - export DB_PASSWORD=$DB_PASSWORD\n");
        try
        {
            var options = new GitLabTraceOptions();
            options.CiFiles.Add(path);
            options.CiFiles.Add("");
            options.CiFiles.Add("does-not-exist/.gitlab-ci.yml");
            var diagnostics = new TraceDiagnostics();

            var resolver = new GitLabResolver(Variables, options);
            var step = Assert.Single(resolver.Resolve(EnvStep("DB_PASSWORD", null), diagnostics));
            resolver.Resolve(EnvStep("DB_PASSWORD", null), diagnostics);
            _ = new GitLabResolver(Variables, options);

            Assert.StartsWith($"{Path.GetFileName(path)} line 3 sets", step.Note, StringComparison.Ordinal);
            Assert.StartsWith("Could not read GitLab CI file does-not-exist/.gitlab-ci.yml: ", Assert.Single(diagnostics.Warnings), StringComparison.Ordinal);
            Assert.Single(options.Mappings);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Passes_load_warnings_to_the_trace_once_and_checks_arguments()
    {
        var resolver = new GitLabResolver(new GitLabVariables([], ["skipped group"]), new GitLabTraceOptions());
        var diagnostics = new TraceDiagnostics();

        resolver.Resolve(EnvStep("A", "1"), diagnostics);
        resolver.Resolve(EnvStep("B", "1"), diagnostics);

        Assert.Equal(["skipped group"], diagnostics.Warnings);
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!, diagnostics));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(EnvStep("A", "1"), null!));
        var options = new GitLabTraceOptions();
        Assert.Throws<ArgumentNullException>(() => options.AddCiFileText(null!, "x"));
        Assert.Throws<ArgumentException>(() => options.AddCiFileText("", " "));
        Assert.Throws<ArgumentException>(() => options.AddCiFile(" "));
    }

    [Fact]
    public void Redacts_masked_variables_end_to_end()
    {
        const string prefix = "CFGLTEST_";
        Environment.SetEnvironmentVariable(prefix + "DB_TIMEOUT", "30");
        Environment.SetEnvironmentVariable(prefix + "DB_PASSWORD", "hunter2");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var variables = new GitLabVariables(
            [
                new(prefix + "DB_TIMEOUT", "30", GitLabVariableLevel.Project, "contoso/api"),
                new(prefix + "DB_PASSWORD", "hunter2", GitLabVariableLevel.Project, "contoso/api", IsMasked: true),
            ]);

            var report = ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "DB_TIMEOUT"] = "30", [prefix + "DB_PASSWORD"] = "hunter2" })
                .WithGitLab(variables)
                .Trace();

            Assert.Equal($"set in environment variable {prefix}DB_TIMEOUT from GitLab project variable {prefix}DB_TIMEOUT", report.Find("DB_TIMEOUT")!.Story);
            var password = report.Find("DB_PASSWORD")!;
            Assert.True(password.IsSensitive);
            Assert.Equal("***", password.Value);
            Assert.DoesNotContain("hunter2", report.ToJson(), StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", report.ToText(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "DB_TIMEOUT", null);
            Environment.SetEnvironmentVariable(prefix + "DB_PASSWORD", null);
        }
    }

    [Theory]
    [InlineData("deploy:\n  script:\n    - export {0}MAIL_AUTH=\"${0}DB_PASSWORD\"\n", ".gitlab-ci.yml line 3 sets {0}MAIL_AUTH from ${0}DB_PASSWORD")]
    [InlineData("variables:\n  {0}MAIL_AUTH: ${0}DB_PASSWORD\n", ".gitlab-ci.yml line 2 sets {0}MAIL_AUTH from ${0}DB_PASSWORD")]
    [InlineData(null, "linked explicitly")]
    public void Redacts_a_value_linked_to_a_masked_variable_whose_scope_does_not_apply(string? ciFile, string how)
    {
        const string prefix = "CFGLSCOPED_";
        const string secret = "hunter2";
        var value = $"smtp://app:{secret}@mail";
        Environment.SetEnvironmentVariable(prefix + "MAIL_AUTH", value);
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var variables = new GitLabVariables([new(prefix + "DB_PASSWORD", null, GitLabVariableLevel.Project, "contoso/api", "production", IsMasked: true)]);

            // No Environment, so the production variable does not apply.
            var report = ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "MAIL_AUTH"] = value })
                .WithGitLab(variables, o =>
                {
                    if (ciFile is null)
                    {
                        o.Map(prefix + "MAIL_AUTH", prefix + "DB_PASSWORD");
                    }
                    else
                    {
                        o.AddCiFileText(string.Format(CultureInfo.InvariantCulture, ciFile, prefix), ".gitlab-ci.yml");
                    }
                })
                .Trace();

            Assert.DoesNotContain(secret, report.ToText(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, report.ToJson(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, report.Explain("MAIL_AUTH"), StringComparison.Ordinal);
            var setting = report.Find("MAIL_AUTH")!;
            Assert.True(setting.IsSensitive);
            Assert.Equal("***", setting.Value);
            var origin = Flatten(setting.Steps).Last();
            Assert.Equal($"GitLab variable {prefix}DB_PASSWORD", origin.Source);
            Assert.True(origin.IsSensitive);
            Assert.True(origin.IsEffective);
            Assert.Null(origin.Value);
            Assert.Null(origin.Reference);
            Assert.Equal(string.Format(CultureInfo.InvariantCulture, how, prefix) + "; masked, and set only for environment scopes that do not apply", origin.Note);
            Assert.All(Flatten(setting.Steps), s =>
            {
                Assert.DoesNotContain(secret, s.Value ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, s.Note ?? string.Empty, StringComparison.Ordinal);
            });
            Assert.Contains(report.Warnings, w => w.Contains("environment scopes production", StringComparison.Ordinal));
            Assert.All(report.Warnings, w => Assert.DoesNotContain(secret, w, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "MAIL_AUTH", null);
        }

        static IEnumerable<ProvenanceStep> Flatten(IEnumerable<ProvenanceStep> steps) =>
            steps.SelectMany(s => Flatten(s.Origins).Prepend(s));
    }

    [Fact]
    public void Redacts_the_content_of_a_file_variable_end_to_end()
    {
        const string prefix = "CFGLFILE_";
        const string secret = "fake-kube-token";
        var variables = new GitLabVariables(
        [
            new(prefix + "KUBECONFIG", $"apiVersion: v1\nusers:\n- user:\n    token: {secret}\n", GitLabVariableLevel.Project, "contoso/api", IsFile: true),
        ]);

        var report = TraceOne(prefix, "KUBECONFIG", "/builds/contoso/api.tmp/KUBECONFIG", variables, null);

        AssertNowhere(report, "KUBECONFIG", secret);
        var setting = report.Find("KUBECONFIG")!;
        Assert.True(setting.IsSensitive);
        var origin = Assert.Single(Assert.Single(setting.Steps).Origins);
        Assert.Equal($"GitLab project variable {prefix}KUBECONFIG", origin.Source);
        Assert.True(origin.IsSensitive);
        Assert.Equal("***", origin.Value);
    }

    [Theory]
    [InlineData("variables:\n  {0}MAIL_AUTH: ${0}SMTP_PASSWORD\n", "GitLab project variable {0}SMTP_PASSWORD")]
    [InlineData("deploy:\n  script:\n    - export {0}MAIL_AUTH=\"${0}SMTP_PASSWORD\"\n", "GitLab project variable {0}SMTP_PASSWORD")]
    [InlineData("variables:\n  {0}SMTP_PASSWORD: fake-smtp-pass\n  {0}MAIL_AUTH: ${0}SMTP_PASSWORD\n", "GitLab CI variable {0}SMTP_PASSWORD")]
    public void Redacts_a_value_from_a_variable_named_like_a_secret_end_to_end(string ciFile, string source)
    {
        const string prefix = "CFGLNAMED_";
        const string secret = "fake-smtp-pass";

        // Not masked in GitLab, as for a value with characters GitLab cannot mask. The last case sets it only in the CI file.
        var variables = new GitLabVariables(source.StartsWith("GitLab project", StringComparison.Ordinal)
            ? [new(prefix + "SMTP_PASSWORD", secret, GitLabVariableLevel.Project, "contoso/api")]
            : []);

        var report = TraceOne(prefix, "MAIL_AUTH", secret, variables, string.Format(CultureInfo.InvariantCulture, ciFile, prefix));

        AssertNowhere(report, "MAIL_AUTH", secret);
        var setting = report.Find("MAIL_AUTH")!;
        Assert.True(setting.IsSensitive);
        Assert.Equal("***", setting.Value);
        var effective = Flatten(setting.Steps).Last(s => s.IsEffective && s.Origins.Count == 0);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, source, prefix), effective.Source);
        Assert.True(effective.IsSensitive);
    }

    private static ProvenanceReport TraceOne(string prefix, string key, string value, GitLabVariables variables, string? ciFile)
    {
        Environment.SetEnvironmentVariable(prefix + key, value);
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            return ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + key] = value })
                .WithGitLab(variables, o =>
                {
                    if (ciFile is not null)
                    {
                        o.AddCiFileText(ciFile, ".gitlab-ci.yml");
                    }
                })
                .Trace();
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + key, null);
        }
    }

    private static void AssertNowhere(ProvenanceReport report, string key, string secret)
    {
        Assert.DoesNotContain(secret, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, report.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, report.Explain(key), StringComparison.Ordinal);
        Assert.All(Flatten(report.Find(key)!.Steps), s =>
        {
            Assert.DoesNotContain(secret, s.Value ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, s.Note ?? string.Empty, StringComparison.Ordinal);
        });
        Assert.All(report.Warnings, w => Assert.DoesNotContain(secret, w, StringComparison.Ordinal));
    }

    private static IEnumerable<ProvenanceStep> Flatten(IEnumerable<ProvenanceStep> steps) =>
        steps.SelectMany(s => Flatten(s.Origins).Prepend(s));

    [Fact]
    public void Checks_the_tracer_extension_arguments()
    {
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());

        Assert.Throws<ArgumentNullException>(() => GitLabTracerExtensions.WithGitLab(null!, Variables));
        Assert.Throws<ArgumentNullException>(() => tracer.WithGitLab(null!));
        Assert.Same(tracer, tracer.WithGitLab(Variables));
    }
}
