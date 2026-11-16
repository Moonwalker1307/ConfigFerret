using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tests;

public class ConfigTracerTests
{
    private static IConfigurationRoot Memory(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value)).Build();

    private static ProvenanceStep Step(string source, string? value = "v", bool sensitive = false, bool removal = false) => new()
    {
        Kind = LayerKind.Helm,
        Source = source,
        Value = value,
        IsSensitive = sensitive,
        IsRemoval = removal,
    };

    /// <summary>Answers every step with the same upstream chain, whatever its depth.</summary>
    private sealed class Fixed(params ProvenanceStep[] chain) : IOriginResolver
    {
        public int Calls { get; private set; }

        public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
        {
            Calls++;
            diagnostics.Warn("resolver ran");
            return chain;
        }
    }

    /// <summary>
    /// Answers each layer of the given settings, and each step it returned for them, with that many upstream steps,
    /// so their traces grow by that factor at every level.
    /// </summary>
    private sealed class FanOut(params (string Key, int Width)[] widths) : IOriginResolver
    {
        public List<string> Resolved { get; } = [];

        public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
        {
            var key = layer.ConfigurationKey ?? layer.Location;
            var width = widths.FirstOrDefault(w => w.Key == key).Width;
            if (key is null || width == 0)
            {
                return [];
            }

            Resolved.Add(key);
            return [.. Enumerable.Range(1, width).Select(i => Step($"{layer.Source} > {i}", $"{key} upstream") with { Kind = LayerKind.Other, Location = key })];
        }
    }

    /// <summary>Answers only the .NET configuration layers of a setting, never the steps upstream of them.</summary>
    private sealed class TopLevel(params ProvenanceStep[] chain) : IOriginResolver
    {
        public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics) =>
            layer.ConfigurationKey is null ? [] : chain;
    }

    private static int Depth(ProvenanceStep step) => step.Origins.Count == 0 ? 0 : 1 + Depth(step.Origins[^1]);

    private static IEnumerable<ProvenanceStep> Upstream(ProvenanceStep step) => step.Origins.SelectMany(o => Upstream(o).Prepend(o));

    private static List<ProvenanceStep> Upstream(TracedSetting setting) => [.. setting.Steps.SelectMany(Upstream)];

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void Follows_origins_up_to_MaxDepth(int maxDepth, int depth)
    {
        var setting = ConfigTracer.For(Memory(("K", "v")))
            .WithResolver(new Fixed(Step("up")))
            .WithOptions(o => o.MaxDepth = maxDepth)
            .Trace().Find("K")!;

        Assert.Equal(depth, Depth(setting.Steps[0]));
    }

    [Fact]
    public void Does_not_resolve_at_all_with_a_max_depth_of_zero()
    {
        var resolver = new Fixed(Step("up"));
        var report = ConfigTracer.For(Memory(("K", "v"))).WithResolver(resolver).WithOptions(o => o.MaxDepth = 0).Trace();

        Assert.Equal(0, resolver.Calls);
        Assert.Empty(report.Find("K")!.Steps[0].Origins);
    }

    [Fact]
    public void Joins_origins_from_every_resolver_and_marks_the_last_of_each_chain_effective()
    {
        var setting = ConfigTracer.For(Memory(("K", "v")))
            .WithResolver(new Fixed())
            .WithResolver(new Fixed(Step("a1"), Step("a2")))
            .WithResolver(new Fixed(Step("b1"), Step("b2", removal: true)))
            .WithOptions(o => o.MaxDepth = 1)
            .Trace().Find("K")!;

        var origins = setting.Steps[0].Origins;
        Assert.Equal(["a1", "a2", "b1", "b2"], origins.Select(o => o.Source));
        Assert.Equal([false, true, false, false], origins.Select(o => o.IsEffective));
        Assert.Contains("resolver ran", ConfigTracer.For(Memory(("K", "v"))).WithResolver(new Fixed()).Trace().Warnings);
    }

    [Fact]
    public void Keeps_origins_a_resolver_already_returned()
    {
        var preset = Step("up") with { Origins = [Step("preset")] };
        var setting = ConfigTracer.For(Memory(("K", "v")))
            .WithResolver(new Fixed(preset))
            .WithOptions(o => o.MaxDepth = 2)
            .Trace().Find("K")!;

        var up = Assert.Single(setting.Steps[0].Origins);
        Assert.Equal(["preset", "up"], up.Origins.Select(o => o.Source));
    }

    [Fact]
    public void Stops_following_the_origins_of_a_setting_at_the_step_limit_and_hides_its_value()
    {
        // Three upstream steps for every step, eight levels deep, are 9,840 steps; two for every step are 510.
        var report = ConfigTracer.For(Memory(("Big", "big-value"), ("Small", "small-value")))
            .WithResolver(new FanOut(("Big", 3), ("Small", 2)))
            .Trace();

        var big = report.Find("Big")!;
        Assert.Equal(1_000, Upstream(big).Count);
        Assert.Equal(
            "Stopped following the origins of Big after 1,000 upstream steps, so its trace is incomplete and its value is hidden: the part not traced may hold a secret.",
            Assert.Single(report.Warnings));
        Assert.True(big.IsSensitive);
        Assert.Equal("***", big.Value);
        Assert.All(big.Steps.Concat(Upstream(big)), s => Assert.Equal("***", s.Value));

        var small = report.Find("Small")!;
        Assert.Equal(510, Upstream(small).Count);
        Assert.False(small.IsSensitive);
        Assert.Equal("small-value", small.Value);
        Assert.Equal("small-value", small.Steps[0].Value);
        Assert.All(Upstream(small), s => Assert.Equal("Small upstream", s.Value));
    }

    [Fact]
    public void Shows_a_cut_off_setting_with_RevealSensitiveValues_and_still_warns()
    {
        var report = ConfigTracer.For(Memory(("Big", "big-value")))
            .WithResolver(new FanOut(("Big", 3)))
            .WithOptions(o => o.RevealSensitiveValues = true)
            .Trace();

        var big = report.Find("Big")!;
        Assert.Equal(1_000, Upstream(big).Count);
        Assert.Equal("Stopped following the origins of Big after 1,000 upstream steps, so its trace is incomplete.", Assert.Single(report.Warnings));
        Assert.True(big.IsSensitive);
        Assert.Equal("big-value", big.Value);
        Assert.Equal("big-value", big.Steps[0].Value);
        Assert.All(Upstream(big), s => Assert.Equal("Big upstream", s.Value));
        Assert.DoesNotContain(big.Steps.Concat(Upstream(big)), s => s.IsSensitive);
    }

    [Fact]
    public void Stops_the_whole_trace_at_the_trace_step_limit_and_hides_the_settings_after_it()
    {
        // Two upstream steps for every step, two levels deep: six steps for each setting.
        var fanOut = new FanOut(("A", 2), ("B", 2), ("C", 2), ("D", 2));
        var tracer = ConfigTracer.For(Memory(("A", "a"), ("B", "b"), ("C", "c"), ("D", "d")))
            .WithResolver(fanOut)
            .WithOptions(o => o.MaxDepth = 2);
        tracer.MaxStepsPerSetting = 10;
        tracer.MaxStepsPerTrace = 15;

        var report = tracer.Trace();

        Assert.Equal(
            "Stopped following origins after 15 upstream steps in this trace, so the traces of C and every setting after it are incomplete and their values are hidden: the part not traced may hold a secret.",
            Assert.Single(report.Warnings));
        Assert.Equal([6, 6, 3, 0], report.Settings.Select(s => Upstream(s).Count));
        Assert.DoesNotContain("D", fanOut.Resolved);
        Assert.Equal(["a", "b", "***", "***"], report.Settings.Select(s => s.Value));
        Assert.Equal([false, false, true, true], report.Settings.Select(s => s.IsSensitive));
        Assert.All(report.Settings.Skip(2).SelectMany(s => s.Steps.Concat(Upstream(s))), s => Assert.Equal("***", s.Value));
    }

    [Fact]
    public void Leaves_a_trace_that_fits_the_step_limits_as_it_was()
    {
        // Two upstream steps for every step, three levels deep, are 14 steps: exactly both limits.
        var config = Memory(("K", "v"));
        var unlimited = ConfigTracer.For(config).WithResolver(new FanOut(("K", 2))).WithOptions(o => o.MaxDepth = 3).Trace();
        var fanOut = new FanOut(("K", 2));
        var tracer = ConfigTracer.For(config).WithResolver(fanOut).WithOptions(o => o.MaxDepth = 3);
        tracer.MaxStepsPerSetting = 14;
        tracer.MaxStepsPerTrace = 14;

        var report = tracer.Trace();

        Assert.Empty(report.Warnings);
        Assert.Equal(7, fanOut.Resolved.Count);
        var setting = report.Find("K")!;
        Assert.Equal(14, Upstream(setting).Count);
        Assert.False(setting.IsSensitive);
        Assert.Equal("v", setting.Value);
        Assert.Equal(unlimited.ToJson(), report.ToJson());
    }

    [Fact]
    public void Never_prints_a_secret_value_that_lies_beyond_the_step_limit()
    {
        // The secret store is linked only after the fan-out above it is traced, which takes more than 1,000 steps.
        const string secret = "fake-secret-value-not-real";
        var vault = Step("vault secret", secret, sensitive: true);
        ConfigTracer Tracer() => ConfigTracer.For(Memory(("Upstream", secret)))
            .WithResolver(new FanOut(("Upstream", 3)))
            .WithResolver(new TopLevel(vault));

        var full = Tracer();
        full.MaxStepsPerSetting = 10_000;
        Assert.True(full.Trace().Find("Upstream")!.IsSensitive);

        var report = Tracer().Trace();

        Assert.Equal("***", report.Find("Upstream")!.Value);
        Assert.DoesNotContain(secret, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, report.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, report.Explain("Upstream"), StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_values_whose_winning_origin_is_secret()
    {
        var report = ConfigTracer.For(Memory(("Plain", "v")))
            .WithResolver(new Fixed(Step("public", "p"), Step("secret", "s", sensitive: true)))
            .WithOptions(o => o.MaxDepth = 1)
            .Trace();

        var setting = report.Find("Plain")!;
        Assert.True(setting.IsSensitive);
        Assert.Equal("***", setting.Value);
        Assert.Equal("***", setting.Steps[0].Value);
        Assert.True(setting.Steps[0].IsSensitive);
        Assert.Equal(["p", "***"], setting.Steps[0].Origins.Select(o => o.Value));
    }

    [Fact]
    public void A_secret_that_lost_does_not_make_the_value_sensitive()
    {
        var setting = ConfigTracer.For(Memory(("Plain", "v")))
            .WithResolver(new Fixed(Step("secret", "s", sensitive: true), Step("public", "p")))
            .WithOptions(o => o.MaxDepth = 1)
            .Trace().Find("Plain")!;

        Assert.False(setting.IsSensitive);
        Assert.Equal("v", setting.Value);
        Assert.Equal(["***", "p"], setting.Steps[0].Origins.Select(o => o.Value));
    }

    private static ProvenanceStep Named(SourceRef reference, string? value = "fake-smtp-password") =>
        Step("upstream", value) with { Kind = LayerKind.Other, Reference = reference };

    [Fact]
    public void Hides_a_value_whose_winning_origin_is_named_like_a_secret()
    {
        const string secret = "fake-smtp-password";
        SourceRef[] references =
        [
            new ConfigMapKeyRef("api-config", "smtp-password"),
            new WorkloadEnvRef("api", "api", "SMTP_PASSWORD"),
            new EnvironmentVariableRef("SMTP_PASSWORD"),
            new HelmValueRef("smtp.password"),
            new Compose.DotEnvEntryRef(".env", "SMTP_PASSWORD"),
            new Compose.ComposeEnvironmentRef("api", "SMTP_PASSWORD"),
            new GitOps.GitOpsVariableRef("apps", "smtp_password"),
            new GitOps.GitOpsPatchRef("api", "api-config", "smtp-password"),
            new Terraform.TerraformOutputRef("smtp", "password"),
        ];

        Assert.All(references, reference =>
        {
            var report = ConfigTracer.For(Memory(("Smtp:Auth", secret)))
                .WithResolver(new Fixed(Named(reference) with { Origins = [Step("values file", secret)] }))
                .WithOptions(o => o.MaxDepth = 1)
                .Trace();

            var setting = report.Find("Smtp:Auth")!;
            Assert.True(setting.IsSensitive);
            Assert.Equal("***", setting.Value);
            Assert.Equal("***", setting.Steps[0].Value);
            var origin = Assert.Single(setting.Steps[0].Origins);
            Assert.True(origin.IsSensitive);
            Assert.Equal("***", origin.Value);
            Assert.Equal("***", Assert.Single(origin.Origins).Value);
            Assert.DoesNotContain(secret, report.ToText(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, report.ToJson(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, report.Explain("Smtp:Auth"), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Names_of_objects_files_and_folders_do_not_hide_a_value()
    {
        SourceRef[] references =
        [
            new ConfigMapKeyRef("secret-config", "smtp-host"),
            new WorkloadEnvRef("token-service", "password-sidecar", "SMTP_HOST"),
            new FileRef("/etc/secrets/appsettings.json"),
            new Compose.DotEnvEntryRef("/run/secrets/.env", "SMTP_HOST"),
            new Compose.ComposeEnvironmentRef("token-service", "SMTP_HOST"),
            new GitOps.GitOpsVariableRef("secrets", "smtp_host"),
            new GitOps.GitOpsPatchRef("secrets", "token-config", "smtp-host"),
            new Terraform.TerraformOutputRef("smtp", "host"),
        ];

        Assert.All(references, reference =>
        {
            var setting = ConfigTracer.For(Memory(("Smtp:Host", "mail.example.test")))
                .WithResolver(new Fixed(Named(reference, "mail.example.test")))
                .WithOptions(o => o.MaxDepth = 1)
                .Trace().Find("Smtp:Host")!;

            Assert.False(setting.IsSensitive);
            Assert.Equal("mail.example.test", setting.Value);
            Assert.Equal("mail.example.test", Assert.Single(setting.Steps[0].Origins).Value);
        });
    }

    [Fact]
    public void An_environment_variable_prefix_named_like_a_secret_does_not_hide_values()
    {
        const string prefix = "CFTOKENSVC_";
        Environment.SetEnvironmentVariable(prefix + "Db__Timeout", "30");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();

            // Upstream, the workload's env entry and a variable go by the same name, the second in other letter case.
            var setting = ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "Db__Timeout"] = "30" })
                .WithResolver(new TopLevel(
                    Named(new WorkloadEnvRef("api", "api", prefix + "Db__Timeout"), "30"),
                    Named(new EnvironmentVariableRef(prefix.ToUpperInvariant() + "DB__TIMEOUT"), "30")))
                .Trace().Find("Db:Timeout")!;

            Assert.Equal(new EnvironmentVariableRef(prefix + "Db__Timeout"), Assert.Single(setting.Steps).Reference);
            Assert.False(setting.IsSensitive);
            Assert.Equal("30", setting.Value);
            Assert.Equal("30", setting.Steps[0].Value);
            Assert.Equal(["30", "30"], setting.Steps[0].Origins.Select(o => o.Value));
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Db__Timeout", null);
        }
    }

    [Fact]
    public void The_configmap_key_an_appsettings_file_is_mounted_from_counts_as_a_name()
    {
        var file = Step("appsettings.Secrets.json", null) with
        {
            Kind = LayerKind.DotNetProvider,
            Reference = new FileRef("/app/appsettings.Secrets.json"),
            Origins = [Named(new ConfigMapKeyRef("api-files", "appsettings.Secrets.json"), value: null) with { IsEffective = true }],
        };

        var setting = ConfigTracer.For(Memory(("Smtp:Host", "mail.example.test")))
            .WithResolver(new Fixed(file))
            .WithOptions(o => o.MaxDepth = 1)
            .Trace().Find("Smtp:Host")!;

        // A file named like a secret says it holds secrets, so its values are hidden, as a container trace hides the file.
        Assert.True(setting.IsSensitive);
        Assert.Equal("***", setting.Value);
        Assert.All(setting.Steps.Concat(Upstream(setting)), s => Assert.True(s.IsSensitive));
    }

    [Fact]
    public void A_layer_named_like_a_secret_that_lost_hides_only_itself()
    {
        var setting = ConfigTracer.For(Memory(("Smtp:Auth", "v")))
            .WithResolver(new Fixed(Named(new ConfigMapKeyRef("api-config", "smtp-password"), "s") with { Origins = [Step("values file", "s")] }, Step("public", "p")))
            .WithOptions(o => o.MaxDepth = 1)
            .Trace().Find("Smtp:Auth")!;

        Assert.False(setting.IsSensitive);
        Assert.Equal("v", setting.Value);
        Assert.Equal("v", setting.Steps[0].Value);
        Assert.Equal(["***", "p"], setting.Steps[0].Origins.Select(o => o.Value));
        Assert.Equal("***", Assert.Single(setting.Steps[0].Origins[0].Origins).Value);
    }

    [Fact]
    public void A_layer_named_like_a_secret_follows_the_reveal_switch_and_the_trace_pattern()
    {
        var config = Memory(("Smtp:Auth", "fake-smtp-password"));
        ConfigTracer Tracer(string name) => ConfigTracer.For(config)
            .WithResolver(new Fixed(Named(new ConfigMapKeyRef("api-config", name))))
            .WithOptions(o => o.MaxDepth = 1);

        var revealed = Tracer("smtp-password").WithOptions(o => o.RevealSensitiveValues = true).Trace().Find("Smtp:Auth")!;
        var off = Tracer("smtp-password").WithOptions(o => o.SensitiveKeyPattern = null).Trace().Find("Smtp:Auth")!;
        var custom = Tracer("hidden-auth").WithOptions(o => o.SensitiveKeyPattern = new Regex("^hidden", RegexOptions.IgnoreCase)).Trace().Find("Smtp:Auth")!;

        Assert.True(revealed.IsSensitive);
        Assert.Equal("fake-smtp-password", revealed.Value);
        Assert.Equal("fake-smtp-password", Assert.Single(revealed.Steps[0].Origins).Value);
        Assert.DoesNotContain(revealed.Steps.Concat(Upstream(revealed)), s => s.IsSensitive);
        Assert.False(off.IsSensitive);
        Assert.Equal("fake-smtp-password", Assert.Single(off.Steps[0].Origins).Value);
        Assert.True(custom.IsSensitive);
        Assert.Equal("***", Assert.Single(custom.Steps[0].Origins).Value);
    }

    [Fact]
    public void Uses_a_custom_key_pattern_redacted_text_and_reveal_switch()
    {
        var config = Memory(("Hidden", "h"), ("Password", "pw"));

        var custom = ConfigTracer.For(config)
            .WithOptions(new TraceOptions { SensitiveKeyPattern = new Regex("^hid", RegexOptions.IgnoreCase), RedactedText = "[x]" })
            .Trace();
        var off = ConfigTracer.For(config).WithOptions(new TraceOptions { SensitiveKeyPattern = null }).Trace();
        var revealed = ConfigTracer.For(config).WithOptions(o => o.RevealSensitiveValues = true).Trace();

        Assert.Equal("[x]", custom.Find("Hidden")!.Value);
        Assert.Equal("[x]", custom.Find("Hidden")!.Steps[0].Value);
        Assert.Equal("pw", custom.Find("Password")!.Value);
        Assert.False(off.Find("Password")!.IsSensitive);
        Assert.Equal("pw", off.Find("Password")!.Value);
        Assert.True(revealed.Find("Password")!.IsSensitive);
        Assert.Equal("pw", revealed.Find("Password")!.Value);
        Assert.Equal("pw", revealed.Find("Password")!.Steps[0].Value);
    }

    [Fact]
    public void Matches_environment_variables_from_the_given_environment()
    {
        Environment.SetEnvironmentVariable("CFTR_A", "process");
        var config = new ConfigurationBuilder().AddEnvironmentVariables("CFTR_").Build();
        var options = new TraceOptions { Environment = new Dictionary<string, string?> { ["CFTR_a"] = "process" } };

        var given = ConfigTracer.For(config).WithOptions(options).Trace().Find("A")!;
        var process = ConfigTracer.For(config).Trace().Find("A")!;

        Assert.Equal("environment variable CFTR_a", given.Steps[0].Source);
        Assert.Equal("environment variable CFTR_A", process.Steps[0].Source);
    }

    [Fact]
    public void Leaves_out_keys_with_no_value_and_sorts_keys()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["b"] = "1", ["A:x"] = "2", ["c"] = null })
            .Build();

        var report = ConfigTracer.For(config).Trace();

        Assert.Equal(["A:x", "b"], report.Settings.Select(s => s.Key));
    }

    [Fact]
    public void Rejects_null_arguments()
    {
        var tracer = ConfigTracer.For(Memory());

        Assert.Throws<ArgumentNullException>(() => ConfigTracer.For(null!));
        Assert.Throws<ArgumentNullException>(() => tracer.WithOptions((TraceOptions)null!));
        Assert.Throws<ArgumentNullException>(() => tracer.WithOptions((Action<TraceOptions>)null!));
        Assert.Throws<ArgumentNullException>(() => tracer.WithKubernetes(null!));
        Assert.Throws<ArgumentNullException>(() => tracer.WithHelm(null!));
        Assert.Throws<ArgumentNullException>(() => tracer.WithKustomize(null!));
        Assert.Throws<ArgumentNullException>(() => tracer.WithResolver(null!));
        Assert.Throws<ArgumentNullException>(() => tracer.Trace().Find(null!));
    }

    [Fact]
    public void Story_says_not_set_without_steps_and_follows_at_most_seven_origins()
    {
        var chain = Step("s9");
        for (var i = 8; i >= 1; i--)
        {
            chain = Step($"s{i}") with { IsEffective = true, Kind = LayerKind.KubernetesConfigMap, Origins = [chain with { IsEffective = true }] };
        }

        var empty = new TracedSetting("K", null, [], false);
        var deep = new TracedSetting("K", "v", [Step("root") with { Kind = LayerKind.DotNetProvider, Origins = [chain] }], false);

        Assert.Equal("not set", empty.Story);
        Assert.Null(empty.EffectiveStep);
        Assert.Equal("root", deep.EffectiveStep!.Source);
        Assert.Equal("set in root from s1 from s2 from s3 from s4 from s5 from s6 from s7", deep.Story);
    }

    [Theory]
    [InlineData("CFT1_", "CFT1_CUSTOMCONNSTR_Db", "CUSTOMCONNSTR_Db")]
    [InlineData("CFT2_", "CFT2_POSTGRESQLCONNSTR_Db", "POSTGRESQLCONNSTR_Db")]
    [InlineData("", "CUSTOMCONNSTR_CfTest3", "ConnectionStrings:CfTest3")]
    public void Redacts_connection_string_variables_even_with_a_provider_prefix(string prefix, string variable, string key)
    {
        // With a prefix, .NET keeps the *CONNSTR_ part in the key, which the sensitive-key pattern doesn't catch.
        Environment.SetEnvironmentVariable(variable, "Server=db;Password=hunter2");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var report = ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [variable] = "Server=db;Password=hunter2" })
                .Trace();

            var setting = Assert.Single(report.Settings, s => s.Key == key);
            Assert.True(setting.IsSensitive);
            Assert.Equal("***", setting.Value);
            Assert.All(setting.Steps, step => Assert.NotEqual("Server=db;Password=hunter2", step.Value));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Theory]
    [InlineData("Db:Password", true)]
    [InlineData("Db:Pass", true)]
    [InlineData("DB_PASS", true)]
    [InlineData("Cert:Passphrase", true)]
    [InlineData("Jwt:Key", true)]
    [InlineData("Jwt:SigningKey", true)]
    [InlineData("DataProtection:EncryptionKey", true)]
    [InlineData("Storage:AccountKey", true)]
    [InlineData("ServiceBus:SharedAccessKey", true)]
    [InlineData("Blob:Sas", true)]
    [InlineData("Sentry:Dsn", true)]
    [InlineData("Webhook:HmacKey", true)]
    [InlineData("Api:Bearer", true)]
    [InlineData("CUSTOMCONNSTR_Db", true)]
    // A short word that ends a PascalCase or camelCase name, in each way .NET configuration writes keys.
    [InlineData("JwtKey", true)]
    [InlineData("Auth:JwtKey", true)]
    [InlineData("Auth__JwtKey", true)]
    [InlineData("AUTH_JWT_KEY", true)]
    [InlineData("auth-jwt-key", true)]
    [InlineData("auth:jwtKey", true)]
    [InlineData("Auth:JWTKey", true)]
    [InlineData("Cosmos:AuthKey", true)]
    [InlineData("Cosmos__PrimaryKey", true)]
    [InlineData("Cosmos:AuthKey2", true)]
    [InlineData("Storage:SasKey", true)]
    [InlineData("Storage:BlobSas", true)]
    [InlineData("Mail:SmtpPass", true)]
    [InlineData("MAIL_SMTP_PASS", true)]
    [InlineData("mail:smtpPass", true)]
    [InlineData("SentryDsn", true)]
    [InlineData("Logging__SentryDsn", true)]
    [InlineData("SENTRY_DSN", true)]
    [InlineData("logging:sentryDsn", true)]
    [InlineData("App:LicenseKey", true)]
    [InlineData("Deploy:SshKey", true)]
    [InlineData("Crypto:AesKey", true)]
    // A SAS URL carries the signature in its query string.
    [InlineData("Storage:SasUrl", true)]
    [InlineData("Storage:BlobSasUri", true)]
    [InlineData("storage:sasUrl", true)]
    [InlineData("STORAGE_SAS_URL", true)]
    // Multi-word names with _ or - between the words.
    [InlineData("DB_CONNECTION_STRING", true)]
    [InlineData("Db:Connection_String", true)]
    [InlineData("db-connection-string", true)]
    [InlineData("Redis__ConnectionString", true)]
    [InlineData("DB_CONN_STR", true)]
    [InlineData("OPENAI_API_KEY", true)]
    [InlineData("openai-api-key", true)]
    [InlineData("Jwt__SigningKey", true)]
    [InlineData("JWT_SIGNING_KEY", true)]
    [InlineData("TLS_PRIVATE_KEY", true)]
    [InlineData("tls-private-key", true)]
    [InlineData("AWS_SECRET_ACCESS_KEY", true)]
    [InlineData("SERVICEBUS_SHARED_ACCESS_SIGNATURE", true)]
    [InlineData("servicebus-shared-access-signature", true)]
    [InlineData("GITHUB_TOKEN", true)]
    [InlineData("Db:AdminPwd", true)]
    [InlineData("Cache:KeyPrefix", false)]
    [InlineData("Proxy:PassThrough", false)]
    [InlineData("Logging:LogLevel:Default", false)]
    [InlineData("Kestrel:Certificates:Default:Path", false)]
    [InlineData("Ui:Keyboard", false)]
    [InlineData("Db:Timeout", false)]
    // A short word that starts or sits inside a PascalCase name names something about a key, not the key.
    [InlineData("Azure:KeyVaultName", false)]
    [InlineData("Db:PrimaryKeyColumn", false)]
    [InlineData("Jwt:KeyId", false)]
    [InlineData("Aws:KmsKeyId", false)]
    [InlineData("Kestrel:Certificates:Default:KeyPath", false)]
    [InlineData("Auth:Keyless", false)]
    [InlineData("Storage:SasUrlExpiryDays", false)]
    [InlineData("Kafka:SaslMechanism", false)]
    [InlineData("SASL_MECHANISM", false)]
    // Keys that are not secret by what they are.
    [InlineData("Cosmos:PartitionKey", false)]
    [InlineData("Cosmos:partitionKey", false)]
    [InlineData("Cosmos:PartitionKeyPath", false)]
    [InlineData("RabbitMq:RoutingKey", false)]
    [InlineData("Jwt:PublicKey", false)]
    [InlineData("Jwt:RsaPublicKey", false)]
    // The short words inside ordinary words, which have no PascalCase boundary.
    [InlineData("Network:Passive", false)]
    [InlineData("Ui:Compass", false)]
    [InlineData("Proxy:Bypass", false)]
    [InlineData("Proxy:BypassOnLocal", false)]
    [InlineData("Auth:Passport", false)]
    [InlineData("Zoo:Monkey", false)]
    [InlineData("Sports:Hockey", false)]
    [InlineData("Menu:Turkey", false)]
    [InlineData("Region:Kansas", false)]
    public void Default_pattern_catches_common_secret_names_only(string key, bool sensitive)
    {
        Assert.Equal(sensitive, new TraceOptions().SensitiveKeyPattern!.IsMatch(key));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("A")]
    [InlineData("_")]
    [InlineData("Key")]
    [InlineData("aKe")]
    [InlineData("PartitionKey_")]
    [InlineData("SasUr")]
    [InlineData("connection_strin")]
    public void Default_pattern_rejects_long_keys_without_backtracking(string repeated)
    {
        // A key with no secret word in it, 10,000 characters long: a pattern that backtracks would hit the timeout.
        var key = string.Concat(Enumerable.Repeat(repeated, 10_000 / repeated.Length)) + "Prefix";
        var pattern = new Regex(TraceOptions.DefaultSensitiveKeyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        Assert.DoesNotMatch(pattern, key);
    }
}
