using ConfigFerret.Compose;
using ConfigFerret.Sources;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tests;

public class ComposeTracerTests
{
    private const string TwoServices = """
        services:
          api:
            environment: { DB_PASSWORD: hunter2, A: api }
            secrets: [db]
          worker:
            environment: { A: worker }
        secrets:
          db: { content: x }
        """;

    private static ComposeProject Parse(string yaml) => ComposeProject.Parse([("compose.yaml", yaml)]);

    [Fact]
    public void ForCompose_lists_variables_then_mounted_files_and_redacts_secrets()
    {
        var report = ConfigTracer.ForCompose(Parse(TwoServices), "api").Trace();

        Assert.Equal(["A", "DB_PASSWORD", "/run/secrets/db"], report.Settings.Select(s => s.Key));
        Assert.Equal("api", report.Find("A")!.Value);
        Assert.Equal("***", report.Find("DB_PASSWORD")!.Value);
        Assert.True(report.Find("DB_PASSWORD")!.IsSensitive);
        Assert.Equal("***", report.Find("/run/secrets/db")!.Value);
        Assert.Null(report.Find("a"));
        Assert.Empty(report.Warnings);
        Assert.DoesNotContain("hunter2", report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", report.ToJson(), StringComparison.Ordinal);
        Assert.Contains("hunter2", ConfigTracer.ForCompose(Parse(TwoServices), "api").WithOptions(o => o.RevealSensitiveValues = true).Trace().ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ForCompose_picks_the_first_service_and_says_so()
    {
        var report = ConfigTracer.ForCompose(Parse(TwoServices)).Trace();

        Assert.Equal("api", report.Find("A")!.Value);
        Assert.Equal("The compose project has 2 services; using api. Pass a service name to pick another.", Assert.Single(report.Warnings));
        Assert.Empty(ConfigTracer.ForCompose(Parse("services:\n  only: {environment: {A: 1}}\n")).Trace().Warnings);
    }

    [Fact]
    public void ForCompose_warns_about_an_unknown_service_or_an_empty_project()
    {
        var unknown = ConfigTracer.ForCompose(Parse(TwoServices), "web").Trace();
        Assert.Empty(unknown.Settings);
        Assert.Equal("No service named 'web' in the compose project. It has api, worker.", Assert.Single(unknown.Warnings));

        var empty = ConfigTracer.ForCompose(Parse("name: x\n")).Trace();
        Assert.Empty(empty.Settings);
        Assert.Equal("The compose project has no services.", Assert.Single(empty.Warnings));
    }

    [Fact]
    public void ForCompose_passes_on_project_and_service_warnings()
    {
        var report = ConfigTracer.ForCompose(Parse("services:\n  a:\n    environment: { A: $X }\n---\nx: 1\n")).Trace();

        Assert.Equal(["compose.yaml holds 2 YAML documents; only the first is read.", "compose.yaml:3: the X variable is not set, so an empty string is used."], report.Warnings);
    }

    [Fact]
    public void ForCompose_lists_an_unset_name_with_no_value()
    {
        var report = ConfigTracer.ForCompose(Parse("services:\n  a:\n    environment: [MISSING]\n")).Trace();

        var setting = Assert.Single(report.Settings);
        Assert.Null(setting.Value);
        Assert.True(setting.Steps[0].IsRemoval);
    }

    [Fact]
    public void WithCompose_links_environment_variables_and_secret_files()
    {
        var environment = new Dictionary<string, string?> { ["A"] = "api", ["DB_PASSWORD"] = "changed", ["NOT_IN_COMPOSE"] = "1" };
        var config = new ConfigurationBuilder()
            .Add(new ComposeGoldenTests.SimulatedEnvironmentSource(environment))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Memory"] = "1" })
            .Build();
        var project = Parse(TwoServices);

        var report = ConfigTracer.For(config).WithOptions(o => o.Environment = environment).WithCompose(project, "api").Trace();

        var a = Assert.Single(report.Find("A")!.EffectiveStep!.Origins);
        Assert.Equal("compose service api env A", a.Source);
        Assert.Null(a.Note);
        var password = Assert.Single(report.Find("DB_PASSWORD")!.EffectiveStep!.Origins);
        Assert.Equal("the compose value differs from the value seen here, so the container may be out of date", password.Note);
        Assert.Equal("***", password.Value);
        Assert.Empty(report.Find("NOT_IN_COMPOSE")!.EffectiveStep!.Origins);
        Assert.Empty(report.Find("Memory")!.EffectiveStep!.Origins);
        Assert.DoesNotContain("hunter2", report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain("changed", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void WithCompose_resolver_handles_files_removals_and_other_layers()
    {
        var project = Parse(TwoServices);
        var resolver = new ComposeResolver(Parse("""
            services:
              api:
                environment: [GONE, KEPT=1]
                secrets: [db]
            secrets:
              db: { content: x }
            """), null);
        var diagnostics = new TraceDiagnostics();
        ProvenanceStep Layer(SourceRef reference, string? value = null, LayerKind kind = LayerKind.DotNetProvider) =>
            new() { Kind = kind, Source = "test", Value = value, Reference = reference };

        Assert.Equal("compose secret db", Assert.Single(resolver.Resolve(Layer(new FileRef("/run/secrets/db"), "other"), diagnostics)).Source);
        Assert.EndsWith("; the compose value differs from the value seen here, so the container may be out of date", resolver.Resolve(Layer(new FileRef("/run/secrets/db"), "other"), diagnostics)[0].Note, StringComparison.Ordinal);
        Assert.Empty(resolver.Resolve(Layer(new FileRef("/run/secrets/nope")), diagnostics));
        Assert.True(Assert.Single(resolver.Resolve(Layer(new EnvironmentVariableRef("GONE"), "x"), diagnostics)).IsRemoval);
        Assert.Null(Assert.Single(resolver.Resolve(Layer(new EnvironmentVariableRef("KEPT"), "1"), diagnostics)).Note);
        Assert.Null(Assert.Single(resolver.Resolve(Layer(new EnvironmentVariableRef("KEPT")), diagnostics)).Note);
        Assert.Empty(resolver.Resolve(Layer(new EnvironmentVariableRef("KEPT"), kind: LayerKind.DockerCompose), diagnostics));
        Assert.Empty(resolver.Resolve(Layer(new HelmValueRef("x")), diagnostics));
        Assert.Empty(diagnostics.Warnings);
        Assert.Empty(new ComposeResolver(project, "nope").Resolve(Layer(new EnvironmentVariableRef("A")), diagnostics));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!, diagnostics));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(Layer(new EnvironmentVariableRef("A")), null!));
    }

    [Fact]
    public void Validates_arguments()
    {
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());
        Assert.Throws<ArgumentNullException>(() => ConfigTracer.ForCompose(null!));
        Assert.Throws<ArgumentNullException>(() => tracer.WithCompose(null!));
        Assert.Throws<ArgumentNullException>(() => ComposeTracerExtensions.WithCompose(null!, Parse("")));
    }

    [Fact]
    public async Task Dotenv_source_links_layers_and_finds_unused_entries()
    {
        using var folder = new TempFolder();
        var first = folder.Write("base.envfile", "A=1\nUNUSED=1\nAPI_TOKEN=t\n");
        var second = folder.Write("local.envfile", "A=2\nB=$MISSING\n");
        var provider = new DotEnvSourceProvider();
        var source = provider.Create(new Dictionary<string, string?> { ["Files"] = $" {first}\n\n{second} " });
        var environment = new Dictionary<string, string?> { ["A"] = "2", ["B"] = "" };
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Add(new ComposeGoldenTests.SimulatedEnvironmentSource(environment)).Build())
            .WithOptions(o => o.Environment = environment);

        var loaded = await tracer.WithSourceAsync(source, TestContext.Current.CancellationToken);
        var report = tracer.Trace();

        Assert.Equal("dotenv base.envfile, local.envfile", source.DisplayName);
        Assert.Equal("dotenv base.envfile, local.envfile", loaded.DisplayName);
        Assert.Equal(["A", "UNUSED", "API_TOKEN", "A", "B"], loaded.Entries.Select(e => e.Name));
        Assert.Equal(new SourceEntry("API_TOKEN", new DotEnvEntryRef(first, "API_TOKEN"), IsSensitive: true, "line 3", "base.envfile"), loaded.Entries[2]);
        Assert.False(loaded.Entries[0].IsSensitive);
        Assert.Equal(["UNUSED", "API_TOKEN"], loaded.FindUnused(report).Select(e => e.Name));
        Assert.Equal(["local.envfile line 2: MISSING is not set, so an empty string is used."], loaded.Warnings);
        Assert.Contains(loaded.Warnings[0], report.Warnings);
        Assert.Equal(["base.envfile", "local.envfile"], report.Find("A")!.EffectiveStep!.Origins.Select(o => o.Source));
    }

    [Fact]
    public async Task Dotenv_source_takes_the_last_line_of_a_key_set_twice_in_one_file()
    {
        using var folder = new TempFolder();
        var file = folder.Write("app.envfile", "A=1\nA=2\n");

        var loaded = await new DotEnvSource([file]).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("line 2", Assert.Single(loaded.Entries).Detail);
        await Assert.ThrowsAsync<OperationCanceledException>(() => new DotEnvSource([file]).LoadAsync(new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Compose_source_links_one_service_and_lists_its_entries()
    {
        using var folder = new TempFolder();
        var compose = folder.Write("compose.yaml", TwoServices);
        folder.Write("extra.envfile", "HOST=h\n");
        var provider = new ComposeSourceProvider();
        var source = provider.Create(new Dictionary<string, string?>
        {
            ["Files"] = compose,
            ["Service"] = " api ",
            ["EnvFiles"] = Path.Join(folder.Path, "extra.envfile"),
            ["ProjectDirectory"] = folder.Path,
        });

        var loaded = await source.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Docker Compose compose.yaml service api", source.DisplayName);
        Assert.Equal(["A", "DB_PASSWORD", "/run/secrets/db"], loaded.Entries.Select(e => e.Name));
        Assert.Equal(new SourceEntry("A", new ComposeEnvironmentRef("api", "A"), IsSensitive: false, "compose.yaml:3", "service api"), loaded.Entries[0]);
        Assert.True(loaded.Entries[1].IsSensitive);
        Assert.Equal(new ComposeMountRef("db", IsSecret: true), loaded.Entries[2].Reference);
        Assert.Empty(loaded.Warnings);

        var only = await new ComposeSource([compose]).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Docker Compose compose.yaml", new ComposeSource([compose]).DisplayName);
        Assert.Equal("service api", only.Entries[0].Location);
        Assert.Single(only.Warnings);
        await Assert.ThrowsAsync<OperationCanceledException>(() => source.LoadAsync(new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Compose_source_of_a_project_without_services_has_no_entries()
    {
        using var folder = new TempFolder();
        var compose = folder.Write("compose.yaml", "name: x\n");

        var loaded = await new ComposeSource([compose]).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(loaded.Entries);
        Assert.Equal(["The compose project has no services."], loaded.Warnings);
    }

    [Fact]
    public void Providers_describe_themselves_and_check_settings()
    {
        var dotenv = new DotEnvSourceProvider();
        var compose = new ComposeSourceProvider();

        Assert.Equal(("dotenv", "dotenv files", SourceCategory.File), (dotenv.Id, dotenv.DisplayName, dotenv.Category));
        Assert.Equal(("docker-compose", "Docker Compose", SourceCategory.File), (compose.Id, compose.DisplayName, compose.Category));
        Assert.Equal(["Files"], dotenv.Settings.Select(s => s.Name));
        Assert.Equal(["Files", "Service", "EnvFiles", "ProjectDirectory"], compose.Settings.Select(s => s.Name));
        Assert.True(dotenv.Settings[0].Required);
        Assert.Equal(SourceSettingKind.Lines, dotenv.Settings[0].Kind);
        Assert.Equal([SourceSettingKind.Lines, SourceSettingKind.Text, SourceSettingKind.Lines, SourceSettingKind.Path], compose.Settings.Select(s => s.Kind));
        Assert.Equal([true, false, false, false], compose.Settings.Select(s => s.Required));
        Assert.DoesNotContain(compose.Settings, s => s.Kind == SourceSettingKind.Secret);

        Assert.Equal("Files is required. (Parameter 'values')", Assert.Throws<ArgumentException>(() => dotenv.Create(new Dictionary<string, string?>())).Message);
        Assert.Throws<ArgumentException>(() => dotenv.Create(new Dictionary<string, string?> { ["Files"] = " \n " }));
        Assert.Throws<ArgumentException>(() => compose.Create(new Dictionary<string, string?> { ["Files"] = null }));
        Assert.Throws<ArgumentNullException>(() => dotenv.Create(null!));
        Assert.Throws<ArgumentNullException>(() => compose.Create(null!));
        var created = Assert.IsType<ComposeSource>(compose.Create(new Dictionary<string, string?> { ["Files"] = "a.yaml\nb.yaml", ["Service"] = " " }));
        Assert.Equal(["a.yaml", "b.yaml"], created.Files);
        Assert.Null(created.Service);
        Assert.Throws<ArgumentException>(() => new DotEnvSource([]));
        Assert.Throws<ArgumentException>(() => new ComposeSource([]));
        Assert.Throws<ArgumentNullException>(() => new DotEnvSource(null!));
        Assert.Throws<ArgumentNullException>(() => new ComposeSource(null!));
    }
}
