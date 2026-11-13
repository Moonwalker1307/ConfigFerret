using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Octopus.Tests;

public class OctopusResolverTests
{
    private static OctopusVariable Var(string name, string? value, string set = "api", bool project = true, bool sensitive = false, params (string Dimension, string Value)[] scope) =>
        new(name, value, sensitive, sensitive ? "Sensitive" : "String", set, project,
            scope.GroupBy(s => s.Dimension).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(s => s.Value)]));

    private static readonly OctopusVariables Variables = new(
    [
        Var("Db:Timeout", "10"),
        Var("Db:Timeout", "30", scope: ("Environment", "Production")),
        Var("Db:Timeout", "20", scope: ("Environment", "Staging")),
        Var("Db:Timeout", "40", scope: [("Environment", "Production"), ("Role", "web")]),
        Var("Region", "eu", set: "library variable set Shared", project: false),
        Var("Region", "us"),
        Var("DB_PASSWORD", null, sensitive: true),
        Var("Greeting", "Hello #{Name}"),
    ]);

    private static ProvenanceStep FileStep(string key, string? value, string path = "/app/appsettings.json") => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = "appsettings.json",
        Value = value,
        ConfigurationKey = key,
        Reference = new FileRef(path),
    };

    private static ProvenanceStep EnvStep(string name, string? value) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = $"environment variable {name}",
        Value = value,
        Reference = new EnvironmentVariableRef(name),
    };

    private static IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep step, Action<OctopusTraceOptions>? configure = null, TraceDiagnostics? diagnostics = null)
    {
        var options = new OctopusTraceOptions();
        configure?.Invoke(options);
        return new OctopusResolver(Variables, options).Resolve(step, diagnostics ?? new TraceDiagnostics());
    }

    [Fact]
    public void Picks_the_variables_scoped_to_the_environment_most_specific_last()
    {
        var chain = Resolve(FileStep("db:timeout", "40"), o =>
        {
            o.Environment = "production";
            o.Roles.Add("web");
        });

        Assert.Equal(["10", "30", "40"], chain.Select(s => s.Value));
        Assert.All(chain, s => Assert.Equal(LayerKind.OctopusDeploy, s.Kind));
        Assert.Equal("Octopus variable Db:Timeout", chain[^1].Source);
        Assert.Equal("project api", chain[^1].Location);
        Assert.Contains("structured configuration variable", chain[^1].Note, StringComparison.Ordinal);
        Assert.Contains("scoped to environment Production; target tag web", chain[^1].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_a_variable_scoped_on_something_not_given_but_says_so()
    {
        var chain = Resolve(FileStep("Db:Timeout", "40"), o => o.Environment = "Production");

        Assert.Equal(["10", "30", "40"], chain.Select(s => s.Value));
        Assert.Contains("applies only if the deployment matches target tag web, which was not given", chain[^1].Note, StringComparison.Ordinal);
        Assert.True(chain[^1].IsInferred);
    }

    [Fact]
    public void Drops_a_variable_scoped_to_another_environment()
    {
        var chain = Resolve(FileStep("Db:Timeout", "20"), o =>
        {
            o.Environment = "Staging";
            o.Roles.Add("worker");
        });

        Assert.Equal(["10", "20"], chain.Select(s => s.Value));
    }

    [Fact]
    public void Lets_a_project_variable_win_over_a_library_one_of_equal_scope()
    {
        var chain = Resolve(EnvStep("Region", "us"));

        Assert.Equal(["eu", "us"], chain.Select(s => s.Value));
        Assert.Equal("library variable set Shared", chain[0].Location);
    }

    [Fact]
    public void Warns_when_two_values_are_scoped_equally()
    {
        var variables = new OctopusVariables([Var("A", "1", scope: ("Environment", "Production")), Var("A", "2", scope: ("Environment", "Production"))]);
        var diagnostics = new TraceDiagnostics();

        new OctopusResolver(variables, new OctopusTraceOptions { Environment = "Production" }).Resolve(EnvStep("A", "2"), diagnostics);

        Assert.Contains("scoped equally", Assert.Single(diagnostics.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Marks_a_sensitive_variable_without_a_value()
    {
        var step = Assert.Single(Resolve(EnvStep("DB_PASSWORD", "hunter2")));

        Assert.True(step.IsSensitive);
        Assert.Null(step.Value);
    }

    [Fact]
    public void Notes_variable_references_and_differences_without_printing_values()
    {
        Assert.Contains("references other Octopus variables", Resolve(EnvStep("Greeting", "Hello Ada"))[0].Note, StringComparison.Ordinal);

        var differs = Resolve(FileStep("Db:Timeout", "99"))[0];
        Assert.Contains("differs", differs.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("99", differs.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_only_JSON_files_by_configuration_key_and_honours_the_switches()
    {
        Assert.Empty(Resolve(FileStep("Db:Timeout", "10", "/app/settings.ini")));
        Assert.Empty(Resolve(FileStep("Db:Timeout", "10"), o => o.MatchConfigurationKeys = false));
        Assert.Empty(Resolve(EnvStep("Region", "us"), o => o.MatchByName = false));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "x", ConfigurationKey = "Db:Timeout" }));
    }

    [Fact]
    public void Follows_an_explicit_mapping_from_an_environment_variable_or_key()
    {
        var fromEnv = Resolve(EnvStep("DB__TIMEOUT", "30"), o =>
        {
            o.Map("DB__TIMEOUT", "Db:Timeout").Environment = "Production";
            o.Roles.Add("worker");
        });
        Assert.Equal(["10", "30"], fromEnv.Select(s => s.Value));
        Assert.False(fromEnv[0].IsInferred);

        var fromKey = Resolve(FileStep("Database:Timeout", "10"), o => o.Map("Database:Timeout", "Db:Timeout").Environment = "Development");
        Assert.Equal(["10"], fromKey.Select(s => s.Value));
    }

    [Fact]
    public void Traces_a_JSON_key_to_Octopus_and_redacts_a_secret_named_key_end_to_end()
    {
        var dir = Directory.CreateTempSubdirectory("configferret-octopus-");
        try
        {
            var file = Path.Combine(dir.FullName, "appsettings.json");
            File.WriteAllText(file, """{ "Db": { "Timeout": "30", "Password": "hunter2" } }""");
            var configuration = new ConfigurationBuilder().AddJsonFile(file).Build();

            // Octopus does not mark this variable sensitive, but its key matches the secret pattern, so it is still redacted.
            var variables = new OctopusVariables([Var("Db:Timeout", "30", scope: ("Environment", "Production")), Var("Db:Password", "hunter2")]);
            var report = ConfigTracer.For(configuration)
                .WithOctopus(variables, o => o.Environment = "Production")
                .Trace();

            var timeout = report.Find("Db:Timeout")!;
            Assert.Equal("set in appsettings.json from Octopus variable Db:Timeout", timeout.Story);
            Assert.Equal("30", timeout.Steps[0].Origins[0].Value);

            Assert.Equal("***", report.Find("Db:Password")!.Steps[0].Origins[0].Value);
            Assert.DoesNotContain("hunter2", report.ToJson(), StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", report.ToText(), StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
