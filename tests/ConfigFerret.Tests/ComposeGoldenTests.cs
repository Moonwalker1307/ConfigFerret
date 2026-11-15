using ConfigFerret.Compose;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace ConfigFerret.Tests;

/// <summary>
/// Golden tests for a Docker Compose project: a shop API in compose.yaml with an override file, an extends file,
/// two env_files (one optional and missing), YAML merge keys, ${VAR} interpolation from an env file and the shell,
/// a file-based secret and an inline config. See <see cref="GoldenTests"/> for how to approve changes.
/// </summary>
public class ComposeGoldenTests
{
    [Fact]
    public Task Compose_service_text() => VerifyText(ConfigTracer.ForCompose(Project(), "api").Trace().ToText());

    [Fact]
    public Task Compose_service_json() => VerifyJson(ConfigTracer.ForCompose(Project(), "api").Trace().ToJson());

    [Fact]
    public Task Compose_worker_text() => VerifyText(ConfigTracer.ForCompose(Project(), "worker").Trace().ToText());

    [Fact]
    public Task Compose_app_text() => VerifyText(TraceApp().ToText());

    [Fact]
    public void Compose_secrets_never_reach_the_output()
    {
        var reports = new[] { ConfigTracer.ForCompose(Project(), "api").Trace(), TraceApp() };
        foreach (var output in reports.SelectMany(r => new[] { r.ToText(), r.ToJson() }))
        {
            Assert.DoesNotContain("pr0d-s3cret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("r3dis-pa", output, StringComparison.Ordinal);
            Assert.DoesNotContain("db-file-s3cret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("signing-key", output, StringComparison.Ordinal);
        }
    }

    internal static ComposeProject Project()
    {
        var dir = TestPaths.Data("golden", "compose");
        var options = new ComposeOptions { ReadSecretFiles = true };
        options.EnvFiles.Add(Path.Join(dir, "project.envfile"));
        options.Variables["DOMAIN"] = "shop.example.test";
        return ComposeProject.Load([Path.Join(dir, "compose.yaml"), Path.Join(dir, "compose.override.yaml")], options);
    }

    // The app inside the api container: its appsettings.json, then the environment compose gives it.
    private static ProvenanceReport TraceApp()
    {
        var project = Project();
        var environment = project.FindService("api")!.Environment;
        var config = new ConfigurationBuilder()
            .AddJsonFile(TestPaths.Data("golden", "compose", "app", "appsettings.json"))
            .Add(new SimulatedEnvironmentSource(environment))
            .Build();
        return ConfigTracer.For(config)
            .WithOptions(o => o.Environment = environment)
            .WithCompose(project, "api")
            .Trace();
    }

    private static Task VerifyText(string text) => Verify(text, Settings());

    private static Task VerifyJson(string json) => Verify(json, "json", Settings());

    private static VerifySettings Settings()
    {
        var settings = new VerifySettings();
        settings.UseDirectory("Snapshots");
        var root = TestPaths.Data();
        settings.AddScrubber(sb =>
        {
            sb.Replace(root, "{TestData}");
            sb.Replace(root.Replace('\\', '/'), "{TestData}");
        });
        return settings;
    }

    /// <summary>Environment variables from a dictionary instead of the test process, described as environment variables.</summary>
    internal sealed class SimulatedEnvironmentSource(IReadOnlyDictionary<string, string?> variables) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider(variables);

        private sealed class Provider(IReadOnlyDictionary<string, string?> variables) : EnvironmentVariablesConfigurationProvider
        {
            public override void Load() =>
                Data = variables.Where(v => v.Value is not null)
                    .ToDictionary(v => v.Key.Replace("__", ":", StringComparison.Ordinal), v => v.Value, StringComparer.OrdinalIgnoreCase);
        }
    }
}
