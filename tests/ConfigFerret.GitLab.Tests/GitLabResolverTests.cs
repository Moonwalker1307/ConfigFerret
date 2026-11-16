using System.Globalization;
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
