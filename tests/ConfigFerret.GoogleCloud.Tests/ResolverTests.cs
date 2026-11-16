using ConfigFerret.Sources;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.GoogleCloud.Tests;

public class ResolverTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 8, 45, 0, TimeSpan.Zero);

    private static readonly GcpSecret DbPassword = new()
    {
        Id = "Db__Password",
        Project = "123",
        CreateTime = Created,
        Replication = "automatic",
        Labels = new Dictionary<string, string> { ["env"] = "prod", ["app"] = "orders" },
        Versions = [new("3", GcpSecretVersionState.Enabled), new("2", GcpSecretVersionState.Disabled), new("1", GcpSecretVersionState.Destroyed)],
        ValueVersion = "3",
        Value = "hunter2",
    };

    private static readonly GcpSecret Db = new()
    {
        Id = "orders-db",
        Project = "123",
        Versions = [new("2", GcpSecretVersionState.Enabled)],
        ValueVersion = "2",
        Value = """{"password":"hunter2","port":5432,"nested":{"user":"app"},"a.b":"dotted"}""",
    };

    private static readonly GcpSecret NamesOnly = new() { Id = "Api__Key", Project = "123" };

    private static readonly GcpSecret Binary = new() { Id = "Cert", Project = "123", ValueVersion = "1", IsBinary = true };

    private static readonly GcpSecrets WithValues = new("contoso-prod", [DbPassword, Db, NamesOnly, Binary]);

    private static ProvenanceStep EnvStep(string name, string? value = null) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = $"environment variable {name}",
        Value = value,
        Reference = new EnvironmentVariableRef(name),
    };

    private static ProvenanceStep External(string? provider, string key, string? property = null, string? version = null, string? store = "gcp-store") => new()
    {
        Kind = LayerKind.KubernetesSecret,
        Source = "ExternalSecret db",
        Reference = new ExternalSecretRef(provider, store, key, property, version),
    };

    private static IReadOnlyList<ProvenanceStep> Resolve(GcpSecrets secrets, ProvenanceStep step, Action<GcpSecretManagerTraceOptions>? configure = null, TraceDiagnostics? diagnostics = null)
    {
        var options = new GcpSecretManagerTraceOptions();
        configure?.Invoke(options);
        return new GcpSecretManagerResolver(secrets, options).Resolve(step, diagnostics ?? new TraceDiagnostics());
    }

    [Fact]
    public void Matches_an_environment_variable_by_name_as_an_inferred_sensitive_step()
    {
        var step = Assert.Single(Resolve(WithValues, EnvStep("Db__Password", "hunter2")));

        Assert.Equal(LayerKind.GcpSecretManager, step.Kind);
        Assert.Equal("Google Cloud Secret Manager secret Db__Password (contoso-prod)", step.Source);
        Assert.Equal("projects/123/secrets/Db__Password", step.Location);
        Assert.Equal("hunter2", step.Value);
        Assert.True(step.IsSensitive);
        Assert.True(step.IsInferred);
        Assert.Equal("matched by name; latest enabled version 3, created 2026-10-01 08:45:00Z, replication automatic, labels app=orders, env=prod", step.Note);
        Assert.Equal(new GcpSecretRef("123", null, "Db__Password"), step.Reference);
    }

    [Theory]
    [InlineData("Db:Password")]
    [InlineData("DB__PASSWORD")]
    [InlineData("db__password")]
    public void Matches_names_the_dotnet_way(string name)
    {
        Assert.Single(Resolve(WithValues, EnvStep(name)));
    }

    [Fact]
    public void Matches_Kubernetes_env_entries_and_ConfigMap_and_Secret_keys()
    {
        Assert.Single(Resolve(WithValues, new ProvenanceStep { Kind = LayerKind.KubernetesWorkload, Source = "w", Reference = new WorkloadEnvRef("api", "app", "Db__Password") }));
        Assert.Single(Resolve(WithValues, new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "c", Reference = new ConfigMapKeyRef("cm", "Api__Key") }));
        Assert.Single(Resolve(WithValues, new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "s", Reference = new SecretKeyRef("s", "Db__Password") }));
        Assert.Empty(Resolve(WithValues, new ProvenanceStep { Kind = LayerKind.Helm, Source = "h", Reference = new HelmValueRef("db.password") }));
        Assert.Empty(Resolve(WithValues, EnvStep("Nope")));
    }

    [Fact]
    public void Matches_a_configuration_key_set_by_a_Secret_Manager_provider()
    {
        ProvenanceStep Provider(string source) => new() { Kind = LayerKind.DotNetProvider, Source = source, ConfigurationKey = "Db:Password" };

        Assert.Single(Resolve(WithValues, Provider("Google Secret Manager")));
        Assert.Single(Resolve(WithValues, Provider("gcp secrets")));
        Assert.Single(Resolve(WithValues, Provider("secret manager provider")));
        Assert.Single(Resolve(WithValues, Provider("SecretManagerConfigurationProvider")));
        Assert.Single(Resolve(WithValues, Provider("Contoso.Config.SecretManagerProvider")));
        Assert.Empty(Resolve(WithValues, Provider("SecretsManagerConfigurationProvider")));
        Assert.Empty(Resolve(WithValues, Provider("AWS Secrets Manager")));
        Assert.Empty(Resolve(WithValues, Provider("appsettings.json")));
        Assert.Empty(Resolve(WithValues, Provider("Google") with { ConfigurationKey = null }));
        Assert.Empty(Resolve(WithValues, Provider("Google") with { Reference = new FileRef("x") }));
    }

    [Fact]
    public void Says_when_the_value_differs_without_printing_either()
    {
        var step = Assert.Single(Resolve(WithValues, EnvStep("Db__Password", "old-value")));

        Assert.Contains("the Secret Manager value differs from the value seen here", step.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("old-value", step.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", step.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("differs", Assert.Single(Resolve(WithValues, EnvStep("Db__Password"))).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_the_value_unknown_for_names_only_and_binary_secrets()
    {
        var names = Assert.Single(Resolve(WithValues, EnvStep("Api__Key", "x")));
        Assert.Null(names.Value);
        Assert.Equal("matched by name", names.Note);

        var binary = Assert.Single(Resolve(WithValues, EnvStep("Cert", "x")));
        Assert.Null(binary.Value);
        Assert.Equal("matched by name; the value is not text, so it was not compared", binary.Note);
    }

    [Fact]
    public void Strips_the_secret_prefix_and_layer_prefixes_and_honours_a_separator()
    {
        var secrets = new GcpSecrets("p", [new GcpSecret { Id = "orders-Db--Timeout", Project = "p" }, new GcpSecret { Id = "orders-", Project = "p" }], prefix: "orders-");

        Assert.Empty(Resolve(secrets, EnvStep("Db__Timeout")));
        Assert.Single(Resolve(secrets, EnvStep("Db__Timeout"), o => o.Separator = "--"));
        Assert.Single(Resolve(secrets, EnvStep("ORDERS_Db__Timeout"), o =>
        {
            o.Separator = "--";
            o.StripPrefixes.Add("orders_");
            o.StripPrefixes.Add("");
        }));
        Assert.Single(Resolve(secrets, EnvStep("orders-Db__Timeout"), o =>
        {
            o.Separator = "--";
            o.StripSecretPrefix = false;
        }));
        Assert.Empty(Resolve(secrets, EnvStep("Db__Timeout"), o =>
        {
            o.Separator = "--";
            o.StripSecretPrefix = false;
        }));

        // A secret id that is only the prefix keeps its name.
        Assert.Single(Resolve(secrets, EnvStep("orders-")));
    }

    [Fact]
    public void Does_not_match_by_name_when_turned_off()
    {
        Assert.Empty(Resolve(WithValues, EnvStep("Db__Password"), o => o.MatchByName = false));
        Assert.Single(Resolve(WithValues, EnvStep("DB_PW"), o =>
        {
            o.MatchByName = false;
            o.Map("DB_PW", "Db__Password");
        }));
    }

    [Fact]
    public void Follows_explicit_links_with_a_JSON_property()
    {
        var step = Assert.Single(Resolve(WithValues, EnvStep("DB_PW", "hunter2"), o => o.Map("DB_PW", "orders-db", "password")));

        Assert.Equal("Google Cloud Secret Manager secret orders-db property password (contoso-prod)", step.Source);
        Assert.Equal("hunter2", step.Value);
        Assert.False(step.IsInferred);
        Assert.Equal("linked explicitly; latest enabled version 2", step.Note);
        Assert.Equal(new GcpSecretRef("123", null, "orders-db"), step.Reference);
    }

    [Fact]
    public void Warns_when_an_explicit_link_names_a_missing_secret()
    {
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(Resolve(WithValues, EnvStep("DB_PW"), o => o.Links["DB_PW"] = "missing", diagnostics));

        Assert.Equal("DB_PW is linked to Google Cloud secret missing, but no such secret was loaded from project contoso-prod.", Assert.Single(diagnostics.Warnings));
    }

    [Theory]
    [InlineData("gcpsm")]
    [InlineData("GCPSM")]
    [InlineData("gcp")]
    [InlineData(null)]
    public void Links_an_external_secret_to_the_secret_for_certain(string? provider)
    {
        var step = Assert.Single(Resolve(WithValues, External(provider, "Db__Password")));

        Assert.False(step.IsInferred);
        Assert.Equal("hunter2", step.Value);
        Assert.StartsWith("read through secret store gcp-store; latest enabled version 3", step.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Treats_an_empty_property_as_none()
    {
        var step = Assert.Single(Resolve(WithValues, External("gcpsm", "Db__Password", property: "")));

        Assert.Equal("hunter2", step.Value);
        Assert.Equal("Google Cloud Secret Manager secret Db__Password (contoso-prod)", step.Source);
    }

    [Theory]
    [InlineData("password", "hunter2")]
    [InlineData("port", "5432")]
    [InlineData("nested.user", "app")]
    [InlineData("nested", """{"user":"app"}""")]
    [InlineData("a.b", "dotted")]
    public void Reads_a_JSON_property_of_an_external_secret(string property, string expected)
    {
        var step = Assert.Single(Resolve(WithValues, External("gcpsm", "orders-db", property, store: null)));

        Assert.Equal(expected, step.Value);
        Assert.Equal($"Google Cloud Secret Manager secret orders-db property {property} (contoso-prod)", step.Source);
        Assert.StartsWith("read by an external secret reference", step.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("orders-db", "missing")]
    [InlineData("orders-db", "nested.missing")]
    [InlineData("orders-db", "password.x")]
    [InlineData("Db__Password", "password")]
    public void Warns_when_a_JSON_property_is_missing(string key, string property)
    {
        var diagnostics = new TraceDiagnostics();

        var step = Assert.Single(Resolve(WithValues, External("gcpsm", key, property), diagnostics: diagnostics));

        Assert.Null(step.Value);
        Assert.Contains($"the value has no property {property}", step.Note, StringComparison.Ordinal);
        Assert.Equal($"ExternalSecret db reads property {property} of Google Cloud secret {key}, but its value is not a JSON object with that property.", Assert.Single(diagnostics.Warnings));
    }

    [Fact]
    public void Explains_why_a_property_was_not_checked()
    {
        Assert.Equal(
            "read through secret store gcp-store; property password was not checked, because secret values were not read (IncludeValues is off)",
            Assert.Single(Resolve(WithValues, External("gcpsm", "Api__Key", "password"))).Note);
        Assert.Equal(
            "read through secret store gcp-store; property password was not checked, because the value is not text",
            Assert.Single(Resolve(WithValues, External("gcpsm", "Cert", "password"))).Note);
        var pinned = Assert.Single(Resolve(WithValues, External("gcpsm", "orders-db", "password", version: "1")));
        Assert.Null(pinned.Value);
        Assert.Equal(
            "read through secret store gcp-store; pinned to version 1, which was not among the versions read; property password was not checked, because only version 2 was read; latest enabled version 2",
            pinned.Note);
    }

    [Theory]
    [InlineData("projects/contoso-prod/secrets/Db__Password/versions/latest")]
    [InlineData("projects/123/secrets/Db__Password/versions/3")]
    [InlineData("projects/CONTOSO-PROD/secrets/Db__Password")]
    [InlineData(" projects/123/secrets/Db__Password ")]
    public void Links_CSI_resource_names(string resourceName)
    {
        var step = Assert.Single(Resolve(WithValues, External("gcpsm", resourceName, store: "spc")));

        Assert.Equal("hunter2", step.Value);
        Assert.StartsWith("read through secret store spc; ", step.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2", "pinned to version 2, which is disabled")]
    [InlineData("1", "pinned to version 1, which is destroyed")]
    [InlineData("3", "pinned to version 3, the latest enabled version")]
    [InlineData("9", "pinned to version 9, which was not among the versions read")]
    public void Describes_a_pinned_version(string version, string note)
    {
        var step = Assert.Single(Resolve(WithValues, External("gcpsm", $"projects/123/secrets/Db__Password/versions/{version}", store: null)));

        Assert.Equal($"read by an external secret reference; {note}; latest enabled version 3, created 2026-10-01 08:45:00Z, replication automatic, labels app=orders, env=prod", step.Note);
        Assert.Equal(version == "3" ? "hunter2" : null, step.Value);
    }

    [Fact]
    public void Describes_a_pin_behind_the_latest_enabled_version_and_prefers_the_references_version()
    {
        var secrets = new GcpSecrets("p", [new GcpSecret { Id = "S", Project = "p", Versions = [new("5", GcpSecretVersionState.Enabled), new("4", GcpSecretVersionState.Enabled)] }]);

        Assert.Equal(
            "read by an external secret reference; pinned to version 4, while the latest enabled version is 5; latest enabled version 5",
            Assert.Single(Resolve(secrets, External(null, "projects/p/secrets/S/versions/5", version: "4", store: null))).Note);
        Assert.Equal(
            "read by an external secret reference; pinned to version 7",
            Assert.Single(Resolve(new GcpSecrets("p", [new GcpSecret { Id = "S", Project = "p" }]), External(null, "S", version: "7", store: null))).Note);
        Assert.Equal(
            "read by an external secret reference; latest enabled version 5",
            Assert.Single(Resolve(secrets, External(null, "S", version: "LATEST", store: null))).Note);
        Assert.Equal(
            "read by an external secret reference; pinned to version 4, while the latest enabled version is 5; latest enabled version 5",
            Assert.Single(Resolve(secrets, External(null, "S", version: "", store: null) with { Reference = new ExternalSecretRef(null, null, "projects/p/secrets/S/versions/4", null, "") })).Note);
    }

    [Fact]
    public void Links_regional_resource_names_only_to_secrets_in_that_location()
    {
        var regional = new GcpSecrets("p", [new GcpSecret { Id = "S", Project = "123", Location = "europe-west1" }], location: "europe-west1");

        Assert.Single(Resolve(regional, External("gcpsm", "projects/p/locations/europe-west1/secrets/S")));
        Assert.Single(Resolve(regional, External("gcpsm", "projects/123/locations/EUROPE-WEST1/secrets/S/versions/2")));
        Assert.Single(Resolve(regional, External("gcpsm", "S")));
        Assert.Empty(Resolve(regional, External("gcpsm", "projects/p/secrets/S")));
        Assert.Empty(Resolve(regional, External("gcpsm", "projects/p/locations/us-east1/secrets/S")));
        Assert.Equal("Google Cloud Secret Manager secret S (p/europe-west1)", Assert.Single(Resolve(regional, External(null, "S"))).Source);
    }

    [Fact]
    public void Ignores_other_providers_and_warns_only_for_explicit_Google_references()
    {
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(Resolve(WithValues, External("vault", "Db__Password"), diagnostics: diagnostics));
        Assert.Empty(Resolve(WithValues, External(null, "missing"), diagnostics: diagnostics));
        Assert.Empty(Resolve(WithValues, External(null, "projects/other/secrets/Db__Password"), diagnostics: diagnostics));
        Assert.Empty(Resolve(WithValues, External(null, "a/b"), diagnostics: diagnostics));
        Assert.Empty(diagnostics.Warnings);

        Assert.Empty(Resolve(WithValues, External("gcpsm", "missing"), diagnostics: diagnostics));
        Assert.Empty(Resolve(WithValues, External("gcpsm", "projects/other/secrets/Db__Password"), diagnostics: diagnostics));
        Assert.Empty(Resolve(WithValues, External("gcp", "projects//secrets/x"), diagnostics: diagnostics));
        Assert.Empty(Resolve(WithValues, External("gcp", " "), diagnostics: diagnostics));
        Assert.Equal(
            [
                "ExternalSecret db reads Google Cloud secret missing, which was not loaded from project contoso-prod.",
                "ExternalSecret db reads Google Cloud secret projects/other/secrets/Db__Password, which was not loaded from project contoso-prod.",
                "ExternalSecret db reads Google Cloud secret projects//secrets/x, which is not a secret id or a projects/.../secrets/... name.",
                "ExternalSecret db reads Google Cloud secret  , which is not a secret id or a projects/.../secrets/... name.",
            ],
            diagnostics.Warnings);
    }

    [Theory]
    [InlineData("S", null, null, "S", null)]
    [InlineData("projects/p/secrets/S", "p", null, "S", null)]
    [InlineData("projects/p/secrets/S/versions/2", "p", null, "S", "2")]
    [InlineData("projects/p/locations/l/secrets/S", "p", "l", "S", null)]
    [InlineData("projects/p/locations/l/secrets/S/versions/latest", "p", "l", "S", "latest")]
    public void Parses_remote_keys(string key, string? project, string? location, string id, string? version)
    {
        Assert.True(GcpSecretManagerResolver.TryParseRemoteKey(key, out var resource));
        Assert.Equal((project, location, id, version), resource);
    }

    [Theory]
    [InlineData("")]
    [InlineData("projects/p")]
    [InlineData("projects/p/secrets")]
    [InlineData("projects/p/secrets/S/versions")]
    [InlineData("projects/p/secrets/S/other/2")]
    [InlineData("folders/p/secrets/S")]
    [InlineData("projects/p/secrets//")]
    [InlineData("projects/p/locations/l/secrets/S/versions/2/x")]
    public void Refuses_other_remote_keys(string key)
    {
        Assert.False(GcpSecretManagerResolver.TryParseRemoteKey(key, out _));
    }

    [Fact]
    public void Adds_load_warnings_to_the_trace_once()
    {
        var secrets = new GcpSecrets("p", [], warnings: ["w1", "w2"]);
        var resolver = new GcpSecretManagerResolver(secrets, new());
        var diagnostics = new TraceDiagnostics();

        resolver.Resolve(EnvStep("A"), diagnostics);
        resolver.Resolve(EnvStep("B"), diagnostics);

        Assert.Equal(["w1", "w2"], diagnostics.Warnings);
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!, diagnostics));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(EnvStep("A"), null!));
    }

    [Fact]
    public void Lists_one_sensitive_entry_per_secret()
    {
        var resolver = new GcpSecretManagerResolver(new GcpSecrets("contoso-prod", [DbPassword, NamesOnly, new GcpSecret { Id = "R", Project = "1", Location = "l", Versions = [] }], location: null), new());

        var entries = resolver.Entries().ToList();

        Assert.Equal(["Db__Password", "Api__Key", "R"], entries.Select(e => e.Name));
        Assert.All(entries, e => Assert.True(e.IsSensitive));
        Assert.All(entries, e => Assert.Equal("project contoso-prod", e.Location));
        Assert.Equal("latest enabled version 3, created 2026-10-01 08:45:00Z, replication automatic, labels app=orders, env=prod", entries[0].Detail);
        Assert.Null(entries[1].Detail);
        Assert.Equal("no enabled version", entries[2].Detail);
        Assert.Equal(new GcpSecretRef("1", "l", "R"), entries[2].Reference);
    }

    [Fact]
    public void Lists_every_secret_with_the_same_key()
    {
        var secrets = new GcpSecrets("p", [new GcpSecret { Id = "Db__Timeout", Project = "p" }, new GcpSecret { Id = "Db--Timeout", Project = "p" }, new GcpSecret { Id = "__", Project = "p" }]);

        Assert.Equal(2, Resolve(secrets, EnvStep("Db:Timeout"), o => o.Separator = "--").Count);
    }

    [Fact]
    public void Parses_explicit_links()
    {
        var options = new GcpSecretManagerTraceOptions();
        options.Links["a"] = " s ";
        options.Links["b"] = "s#p#q";
        options.Links["c"] = "s#";
        options.Links["d"] = " ";
        options.Links["f"] = "#p";

        Assert.Equal(("s", null), options.FindMapping("A"));
        Assert.Equal(("s", "p#q"), options.FindMapping("b"));
        Assert.Equal(("s", null), options.FindMapping("c"));
        Assert.Null(options.FindMapping("d"));
        Assert.Equal(("", "p"), options.FindMapping("f"));
        Assert.Null(options.FindMapping("e"));
        Assert.Same(options, options.Map("x", "s", "p"));
        Assert.Equal("s#p", options.Links["x"]);
        Assert.Equal("s", options.Map("y", "s").Links["y"]);
        Assert.Throws<ArgumentException>(() => options.Map(" ", "s"));
        Assert.Throws<ArgumentException>(() => options.Map("n", " "));
    }

    [Fact]
    public void Traces_an_environment_variable_and_redacts_the_value_end_to_end()
    {
        var prefix = $"CFGCP{Guid.NewGuid():N}_";
        Environment.SetEnvironmentVariable(prefix + "Db__Password", "hunter2");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var secrets = new GcpSecrets("contoso-prod", [DbPassword, NamesOnly]);
            var report = ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "Db__Password"] = "hunter2" })
                .WithGcpSecretManager(secrets, o => o.StripPrefixes.Add(prefix))
                .Trace();

            var password = report.Find("Db:Password")!;
            Assert.Equal($"set in environment variable {prefix}Db__Password from Google Cloud Secret Manager secret Db__Password (contoso-prod)", password.Story);
            Assert.True(password.IsSensitive);
            Assert.DoesNotContain("hunter2", report.ToJson(), StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", report.ToText(), StringComparison.Ordinal);
            Assert.Contains("Google Cloud Secret Manager secret Db__Password", report.ToText(), StringComparison.Ordinal);

            var resolver = new GcpSecretManagerResolver(secrets, new GcpSecretManagerTraceOptions { StripPrefixes = { prefix } });
            var loaded = new LoadedConfigSource("gcp", resolver, resolver.Entries());
            var report2 = ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "Db__Password"] = "hunter2" })
                .WithSource(loaded)
                .Trace();
            Assert.Equal(["Api__Key"], loaded.FindUnused(report2).Select(e => e.Name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Db__Password", null);
        }
    }

    [Fact]
    public void WithGcpSecretManager_checks_its_arguments()
    {
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());

        Assert.Throws<ArgumentNullException>(() => GoogleCloudTracerExtensions.WithGcpSecretManager(null!, WithValues));
        Assert.Throws<ArgumentNullException>(() => tracer.WithGcpSecretManager(null!));
        Assert.Same(tracer, tracer.WithGcpSecretManager(WithValues));
    }
}
