using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public void Checks_its_arguments()
    {
        var resolver = new GitHubResolver(Variables, new GitHubTraceOptions());

        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!, new TraceDiagnostics()));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(EnvStep("A", "1"), null!));
    }

    [Fact]
    public void Says_which_workflow_line_links_to_a_variable_that_was_not_loaded()
    {
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(Resolve(EnvStep("X", "1"), o => o.AddWorkflowText("env:\n  X: ${{ vars.MISSING }}\n", "deploy.yml"), diagnostics));

        Assert.Equal(["X is linked to vars.MISSING (deploy.yml line 2), but no such GitHub variable was loaded."], diagnostics.Warnings);
    }

    [Fact]
    public void Says_when_an_explicit_link_to_a_secret_finds_nothing()
    {
        var diagnostics = new TraceDiagnostics();

        Resolve(EnvStep("X", "1"), o => o.Map("X", "MISSING", GitHubVariableKind.Secret), diagnostics);

        Assert.Equal(["X is linked to secrets.MISSING, but no such GitHub secret was loaded."], diagnostics.Warnings);
    }

    [Fact]
    public void Notes_a_name_match_and_a_workflow_link_to_a_secret()
    {
        var byName = Resolve(EnvStep("DB_TIMEOUT", "30"))[^1];
        Assert.Equal("matched by name", byName.Note);

        var secret = Assert.Single(Resolve(EnvStep("Db__Password", "x"), o => o.AddWorkflowText("Db__Password: ${{ secrets.DB_PASSWORD }}", "deploy.yml")));
        Assert.Equal("deploy.yml line 1 sets Db__Password from ${{ secrets.DB_PASSWORD }}; GitHub does not return secret values", secret.Note);
        Assert.Equal("GitHub repository secret DB_PASSWORD", secret.Source);
        Assert.False(secret.IsInferred);

        var explicitLink = Assert.Single(Resolve(EnvStep("Db__Timeout", "45"), o => o.Map("Db__Timeout", "TIMEOUT_SECONDS")));
        Assert.Equal(string.Empty, explicitLink.Note);
        Assert.Equal("GitHub repository variable TIMEOUT_SECONDS", explicitLink.Source);
    }

    [Fact]
    public void Says_nothing_about_differences_when_either_value_is_missing()
    {
        Assert.Equal("matched by name", Resolve(EnvStep("TIMEOUT_SECONDS", null))[^1].Note);

        var variables = new GitHubVariables([new("A", null, GitHubVariableKind.Variable, GitHubVariableScope.Repository, "contoso/api")]);
        var step = Assert.Single(new GitHubResolver(variables, new GitHubTraceOptions()).Resolve(EnvStep("A", "1"), new TraceDiagnostics()));
        Assert.Equal("matched by name", step.Note);
    }

    [Fact]
    public void Falls_back_to_a_secret_with_the_name_when_there_is_no_variable()
    {
        var step = Assert.Single(Resolve(EnvStep("db_password", "x")));

        Assert.Equal("GitHub repository secret DB_PASSWORD", step.Source);
        Assert.Equal("contoso/api", step.Location);
    }

    [Fact]
    public void Names_the_organization_scope_in_the_source()
    {
        Assert.Equal("GitHub organization variable DB_TIMEOUT", Resolve(EnvStep("DB_TIMEOUT", "30"))[0].Source);
    }

    [Fact]
    public void Reads_workflow_files_once_and_skips_blank_paths()
    {
        var dir = Directory.CreateTempSubdirectory("configferret-workflows-");
        try
        {
            var path = Path.Join(dir.FullName, "deploy.yml");
            File.WriteAllText(path, "env:\n  DB__Timeout: ${{ vars.TIMEOUT_SECONDS }}\n");
            var options = new GitHubTraceOptions();
            options.Workflows.Add(path);
            options.Workflows.Add(" ");
            options.Workflows.Add(Path.Join(dir.FullName, "missing.yml"));

            var first = options.ReadConfiguredWorkflows();
            File.WriteAllText(path, "env:\n  OTHER: ${{ vars.X }}\n");
            var second = options.ReadConfiguredWorkflows();

            Assert.StartsWith("Could not read GitHub workflow ", Assert.Single(first), StringComparison.Ordinal);
            Assert.Single(second);
            Assert.Equal("DB__Timeout", Assert.Single(options.Mappings).Name);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Reads_explicit_links_with_and_without_a_prefix_and_ignores_blank_ones()
    {
        var options = new GitHubTraceOptions().AddWorkflowText("A: ${{ secrets.FROM_WORKFLOW }}", "deploy.yml");
        options.Links["A"] = "  ";
        options.Links["B"] = " vars.B_VAR ";
        options.Links["C"] = "Secrets.C_SECRET";
        options.Links["D"] = "D_VAR";

        Assert.Equal(new GitHubMapping("A", "FROM_WORKFLOW", GitHubVariableKind.Secret, "deploy.yml line 1"), options.FindMapping("A"));
        Assert.Equal(new GitHubMapping("B", "B_VAR", GitHubVariableKind.Variable, null), options.FindMapping("B"));
        Assert.Equal(new GitHubMapping("C", "C_SECRET", GitHubVariableKind.Secret, null), options.FindMapping("C"));
        Assert.Equal(new GitHubMapping("D", "D_VAR", GitHubVariableKind.Variable, null), options.FindMapping("D"));
        Assert.Null(options.FindMapping("E"));
    }

    [Fact]
    public void Links_a_variable_from_a_workflow_as_a_variable()
    {
        var options = new GitHubTraceOptions().AddWorkflowText("A: ${{ vars.X }}\r\nB: \"${{ secrets.Y }}\"\r\n", "deploy.yml");

        Assert.Equal(GitHubVariableKind.Variable, options.FindMapping("A")!.Kind);
        Assert.Equal(GitHubVariableKind.Secret, options.FindMapping("B")!.Kind);
    }

    [Fact]
    public void Checks_the_option_arguments()
    {
        var options = new GitHubTraceOptions();

        Assert.Throws<ArgumentException>(() => options.Map(" ", "X"));
        Assert.Throws<ArgumentException>(() => options.Map("X", ""));
        Assert.Throws<ArgumentException>(() => options.AddWorkflow(" "));
        Assert.Throws<ArgumentNullException>(() => options.AddWorkflowText(null!, "deploy.yml"));
        Assert.Throws<ArgumentException>(() => options.AddWorkflowText("A: ${{ vars.X }}", " "));
        Assert.Empty(options.Links);
        Assert.Empty(options.Mappings);
    }

    [Fact]
    public void WithGitHub_checks_its_arguments_and_applies_the_options()
    {
        var configuration = new ConfigurationBuilder().Build();
        var tracer = ConfigTracer.For(configuration);

        Assert.Throws<ArgumentNullException>(() => GitHubTracerExtensions.WithGitHub(null!, Variables));
        Assert.Throws<ArgumentNullException>(() => tracer.WithGitHub(null!));

        var applied = false;
        Assert.Same(tracer, tracer.WithGitHub(Variables, o => applied = true));
        Assert.True(applied);
    }

    [Fact]
    public async Task WithGitHubAsync_checks_its_arguments()
    {
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());
        using var provider = new ServiceCollection().BuildServiceProvider();

        await Assert.ThrowsAsync<ArgumentNullException>(() => GitHubTracerExtensions.WithGitHubAsync(null!, provider, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => tracer.WithGitHubAsync(null!, TestContext.Current.CancellationToken));
    }
}
