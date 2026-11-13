using Microsoft.Extensions.Configuration;

namespace ConfigFerret.GitHub.Tests;

public class GitHubResolverTests
{
    private static readonly GitHubVariables Variables = new(
    [
        new("DB_TIMEOUT", "10", GitHubVariableKind.Variable, GitHubVariableScope.Organization, "contoso"),
        new("DB_TIMEOUT", "30", GitHubVariableKind.Variable, GitHubVariableScope.Environment, "production"),
        new("DB_PASSWORD", null, GitHubVariableKind.Secret, GitHubVariableScope.Repository, "contoso/api"),
        new("TIMEOUT_SECONDS", "45", GitHubVariableKind.Variable, GitHubVariableScope.Repository, "contoso/api"),
    ]);

    private static ProvenanceStep EnvStep(string name, string? value) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = $"environment variable {name}",
        Value = value,
        Reference = new EnvironmentVariableRef(name),
    };

    private static IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep step, Action<GitHubTraceOptions>? configure = null, TraceDiagnostics? diagnostics = null)
    {
        var options = new GitHubTraceOptions();
        configure?.Invoke(options);
        return new GitHubResolver(Variables, options).Resolve(step, diagnostics ?? new TraceDiagnostics());
    }

    [Fact]
    public void Matches_an_environment_variable_by_name_case_insensitively_lowest_scope_first()
    {
        var chain = Resolve(EnvStep("db_timeout", "30"));

        Assert.Equal(["10", "30"], chain.Select(s => s.Value));
        Assert.All(chain, s => Assert.Equal(LayerKind.GitHubActions, s.Kind));
        Assert.All(chain, s => Assert.True(s.IsInferred));
        Assert.Equal("GitHub environment variable DB_TIMEOUT", chain[^1].Source);
        Assert.Equal("production", chain[^1].Location);
        Assert.Equal(new GitHubVariableRef(GitHubVariableKind.Variable, GitHubVariableScope.Environment, "production", "DB_TIMEOUT"), chain[^1].Reference);
    }

    [Fact]
    public void Matches_a_secret_and_marks_it_sensitive_without_a_value()
    {
        var step = Assert.Single(Resolve(EnvStep("DB_PASSWORD", "hunter2")));

        Assert.True(step.IsSensitive);
        Assert.Null(step.Value);
        Assert.Contains("does not return secret values", step.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_Kubernetes_env_entries_and_ConfigMap_keys()
    {
        Assert.Equal(2, Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesWorkload, Source = "w", Reference = new WorkloadEnvRef("api", "app", "DB_TIMEOUT") }).Count);
        Assert.Equal(2, Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "c", Reference = new ConfigMapKeyRef("cfg", "DB_TIMEOUT") }).Count);
        Assert.Single(Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "s", Reference = new SecretKeyRef("sec", "DB_PASSWORD") }));
    }

    [Fact]
    public void Ignores_layers_without_a_name_and_names_it_does_not_know()
    {
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.Helm, Source = "h", Reference = new HelmValueRef("db.timeout") }));
        Assert.Empty(Resolve(EnvStep("UNKNOWN", "1")));
        Assert.Empty(Resolve(EnvStep("DB_TIMEOUT", "1"), o => o.MatchByName = false));
    }

    [Fact]
    public void Says_when_the_value_differs_without_printing_either_value()
    {
        var step = Resolve(EnvStep("DB_TIMEOUT", "99"))[^1];

        Assert.Contains("differs", step.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("99", step.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("30", step.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Follows_an_explicit_mapping_as_a_certain_link()
    {
        var step = Assert.Single(Resolve(EnvStep("Db__Timeout", "45"), o => o.Map("Db__Timeout", "TIMEOUT_SECONDS")));

        Assert.False(step.IsInferred);
        Assert.Equal("45", step.Value);
    }

    [Fact]
    public void Reads_vars_and_secrets_links_from_a_workflow()
    {
        const string workflow = """
            jobs:
              deploy:
                env:
                  Db__Timeout: ${{ vars.TIMEOUT_SECONDS }}
                  - "Db__Password": '${{ secrets.DB_PASSWORD }}' # list form
                  NOT_A_LINK: prefix-${{ vars.TIMEOUT_SECONDS }}
                steps:
                  - run: echo hi
            """;
        var options = new GitHubTraceOptions().AddWorkflowText(workflow, "deploy.yml");

        Assert.Equal(2, options.Mappings.Count);
        var timeout = Assert.Single(Resolve(EnvStep("Db__Timeout", "45"), o => o.AddWorkflowText(workflow, "deploy.yml")));
        Assert.Equal("deploy.yml line 4 sets Db__Timeout from ${{ vars.TIMEOUT_SECONDS }}", timeout.Note);
        Assert.False(timeout.IsInferred);
    }

    [Fact]
    public void Warns_when_a_mapped_variable_is_missing()
    {
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(Resolve(EnvStep("X", "1"), o => o.Map("X", "MISSING", GitHubVariableKind.Secret), diagnostics));

        Assert.Contains("secrets.MISSING", Assert.Single(diagnostics.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Passes_load_warnings_to_the_trace_once()
    {
        var variables = new GitHubVariables([], ["could not read organization variables"]);
        var resolver = new GitHubResolver(variables, new GitHubTraceOptions());
        var diagnostics = new TraceDiagnostics();

        resolver.Resolve(EnvStep("A", "1"), diagnostics);
        resolver.Resolve(EnvStep("B", "1"), diagnostics);

        Assert.Equal(["could not read organization variables"], diagnostics.Warnings);
    }

    [Fact]
    public void Traces_an_environment_variable_to_GitHub_and_redacts_the_secret_end_to_end()
    {
        const string prefix = "CFGHTEST_";
        Environment.SetEnvironmentVariable(prefix + "DB_TIMEOUT", "30");
        Environment.SetEnvironmentVariable(prefix + "DB_PASSWORD", "hunter2");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var variables = new GitHubVariables(
            [
                new(prefix + "DB_TIMEOUT", "30", GitHubVariableKind.Variable, GitHubVariableScope.Environment, "production"),
                new(prefix + "DB_PASSWORD", null, GitHubVariableKind.Secret, GitHubVariableScope.Environment, "production"),
            ]);

            var report = ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "DB_TIMEOUT"] = "30", [prefix + "DB_PASSWORD"] = "hunter2" })
                .WithGitHub(variables)
                .Trace();

            var timeout = report.Find("DB_TIMEOUT")!;
            Assert.Equal($"set in environment variable {prefix}DB_TIMEOUT from GitHub environment variable {prefix}DB_TIMEOUT", timeout.Story);

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
}
