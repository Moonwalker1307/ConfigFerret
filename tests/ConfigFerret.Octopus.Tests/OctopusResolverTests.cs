using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ConfigFerret.Octopus.Tests;

public class OctopusResolverTests
{
    private static OctopusVariable Var(string name, string? value, string set = "api", bool project = true, bool sensitive = false, params (string Dimension, string Value)[] scope) =>
        new(name, value, sensitive, sensitive ? "Sensitive" : "String", set, project,
            scope.GroupBy(s => s.Dimension).ToDictionary<IGrouping<string, (string Dimension, string Value)>, string, IReadOnlyList<string>>(g => g.Key, g => g.Select(s => s.Value).ToList()));

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
            var file = Path.Join(dir.FullName, "appsettings.json");
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

    [Fact]
    public void Redacts_a_value_from_a_variable_named_like_a_secret_that_is_not_sensitive_end_to_end()
    {
        const string secret = "fake-api-token";
        var dir = Directory.CreateTempSubdirectory("configferret-octopus-");
        try
        {
            var file = Path.Join(dir.FullName, "appsettings.json");
            File.WriteAllText(file, $$"""{ "Api": { "Auth": "{{secret}}" } }""");
            var configuration = new ConfigurationBuilder().AddJsonFile(file).Build();

            // Neither the key nor the Octopus flag says secret, but the variable's own name does.
            var report = ConfigTracer.For(configuration)
                .WithOctopus(new OctopusVariables([Var("Api.Token", secret)]), o => o.Map("Api:Auth", "Api.Token"))
                .Trace();

            var auth = report.Find("Api:Auth")!;
            var octopus = Assert.Single(auth.Steps[0].Origins);
            Assert.Equal("Octopus variable Api.Token", octopus.Source);
            Assert.True(octopus.IsSensitive);
            Assert.Equal("***", octopus.Value);
            Assert.True(auth.IsSensitive);
            Assert.Equal("***", auth.Value);
            Assert.DoesNotContain(secret, report.ToJson(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, report.ToText(), StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Redacts_the_value_when_a_sensitive_value_is_scoped_equally_whatever_the_order(bool sensitiveFirst)
    {
        OctopusVariable[] rows =
        [
            Var("Queue:Endpoint", null, sensitive: true, scope: ("Environment", "Production")),
            Var("Queue:Endpoint", "amqp://dev", scope: ("Environment", "Development")),
        ];

        AssertSecretHidden(TraceQueueEndpoint(new OctopusVariables(sensitiveFirst ? rows : [.. rows.Reverse()])));
    }

    [Fact]
    public void Redacts_the_value_when_a_sensitive_value_applies_unless_a_more_specific_one_does()
    {
        // The plain project value applies only on targets tagged worker, which was not given; elsewhere the sensitive one does.
        var variables = new OctopusVariables(
        [
            Var("Queue:Endpoint", null, set: "library variable set Shared", project: false, sensitive: true),
            Var("Queue:Endpoint", "amqp://worker", scope: ("Role", "worker")),
        ]);

        AssertSecretHidden(TraceQueueEndpoint(variables, o => o.Environment = "Production"));
    }

    [Fact]
    public void Marks_the_last_value_sensitive_only_when_a_sensitive_value_could_win()
    {
        var variables = new OctopusVariables(
        [
            Var("A", null, sensitive: true, scope: ("Environment", "Production")),
            Var("A", "tie", scope: ("Environment", "Development")),
            Var("B", null, sensitive: true),
            Var("B", "maybe", scope: ("Machine", "worker-01")),
            Var("C", null, sensitive: true),
            Var("C", "certain", scope: ("Environment", "Production")),
            Var("D", null, sensitive: true),
            Var("D", "certain", scope: ("Environment", "Production")),
            Var("D", "maybe", scope: [("Environment", "Production"), ("Machine", "worker-01")]),
            Var("E", null, sensitive: true, scope: ("Role", "worker")),
            Var("E", "certain", scope: [("Environment", "Production"), ("Role", "web")]),
        ]);
        var options = new OctopusTraceOptions();
        var resolver = new OctopusResolver(variables, options);
        var diagnostics = new TraceDiagnostics();

        var tie = resolver.Resolve(EnvStep("A", null), diagnostics)[^1];
        options.Environment = "Production";
        options.Roles.Add("web");
        var maybe = resolver.Resolve(EnvStep("B", null), diagnostics)[^1];
        var overridden = resolver.Resolve(EnvStep("C", null), diagnostics)[^1];
        var behindCertain = resolver.Resolve(EnvStep("D", null), diagnostics)[^1];
        var ruledOut = resolver.Resolve(EnvStep("E", null), diagnostics)[^1];

        Assert.True(tie.IsSensitive);
        Assert.Equal("matched by name; scoped to environment Development; applies only if the deployment matches environment Development, which was not given; another value that may apply instead is sensitive", tie.Note);
        Assert.True(maybe.IsSensitive);
        Assert.EndsWith("; another value that may apply instead is sensitive", maybe.Note, StringComparison.Ordinal);
        Assert.False(overridden.IsSensitive);
        Assert.False(behindCertain.IsSensitive);
        Assert.False(ruledOut.IsSensitive);
        Assert.DoesNotContain("sensitive", overridden.Note + behindCertain.Note + ruledOut.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_the_value_when_a_sensitive_value_scoped_only_to_a_runbook_is_left_out()
    {
        // The trace is taken to be of a deployment, which cannot be told otherwise, so the runbook value may still apply.
        var variables = new OctopusVariables(
        [
            Var("Queue:Endpoint", "amqp://relay"),
            Var("Queue:Endpoint", null, sensitive: true, scope: ("ProcessOwner", "Rebuild index")) with { Process = ProcessScope.RunbooksOnly },
        ]);

        var report = TraceQueueEndpoint(variables, o => o.Environment = "Production");

        AssertSecretHidden(report);
        var octopus = Assert.Single(report.Find("Queue:Endpoint")!.Steps[0].Origins);
        Assert.True(octopus.IsSensitive);
        Assert.EndsWith("; another value that may apply instead is sensitive", octopus.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Links_a_layer_to_a_sensitive_value_scoped_only_to_a_runbook_when_nothing_else_applies()
    {
        var variables = new OctopusVariables(
        [
            Var("Queue:Endpoint", null, sensitive: true, scope: ("ProcessOwner", "Rebuild index")) with { Process = ProcessScope.RunbooksOnly },
            Var("Queue:Endpoint", "amqp://other", scope: ("ProcessOwner", "Purge")) with { Process = ProcessScope.RunbooksOnly },
        ]);
        var diagnostics = new TraceDiagnostics();

        var chain = new OctopusResolver(variables, new OctopusTraceOptions()).Resolve(FileStep("Queue:Endpoint", "amqps://app:hunter2@mq.prod"), diagnostics);

        var step = Assert.Single(chain);
        Assert.True(step.IsSensitive);
        Assert.True(step.IsInferred);
        Assert.Contains("applies only if the deployment matches runbook Rebuild index, which was not given", step.Note, StringComparison.Ordinal);
        Assert.Empty(diagnostics.Warnings);
        AssertSecretHidden(TraceQueueEndpoint(variables));
    }

    [Theory]
    [InlineData("Environment", "Production")]
    [InlineData("Action", "Deploy")]
    public void Redacts_the_value_when_a_sensitive_runbook_value_is_left_out_behind_a_deployment_only_value(string dimension, string value)
    {
        // The plain value is certain in a deployment but does not apply when the runbook runs, so the runbook's secret may
        // be what the file holds.
        var variables = new OctopusVariables(
        [
            Var("Queue:Endpoint", "amqp://relay", scope: [(dimension, value), ("ProcessOwner", "Api")]) with { Process = ProcessScope.Deployment },
            Var("Queue:Endpoint", null, sensitive: true, scope: ("ProcessOwner", "Rebuild index")) with { Process = ProcessScope.RunbooksOnly },
        ]);

        AssertSecretHidden(TraceQueueEndpoint(variables, o =>
        {
            o.Environment = "Production";
            o.Step = "Deploy";
        }));
    }

    [Fact]
    public void Leaves_out_a_runbook_value_without_doubt_when_it_is_plain_or_ruled_out_on_another_scope()
    {
        var variables = new OctopusVariables(
        [
            Var("A", "plain"),
            Var("A", "runbook", scope: ("ProcessOwner", "Rebuild index")) with { Process = ProcessScope.RunbooksOnly },
            Var("A", null, sensitive: true, scope: [("ProcessOwner", "Rebuild index"), ("Environment", "Staging")]) with { Process = ProcessScope.RunbooksOnly },
            Var("A", null, sensitive: true, scope: [("ProcessOwner", "Rebuild index"), ("Machine", "worker-01")]) with { Process = ProcessScope.RunbooksOnly },
        ]);
        var resolver = new OctopusResolver(variables, new OctopusTraceOptions { Environment = "Production", Machine = "web-01" });

        var plain = Assert.Single(resolver.Resolve(EnvStep("A", null), new TraceDiagnostics()));

        Assert.Equal("plain", plain.Value);
        Assert.False(plain.IsSensitive);
        Assert.Equal("matched by name", plain.Note);
    }

    private static ProvenanceReport TraceQueueEndpoint(OctopusVariables variables, Action<OctopusTraceOptions>? configure = null)
    {
        var dir = Directory.CreateTempSubdirectory("configferret-octopus-");
        try
        {
            var file = Path.Join(dir.FullName, "appsettings.json");
            File.WriteAllText(file, """{ "Queue": { "Endpoint": "amqps://app:hunter2@mq.prod" } }""");
            var configuration = new ConfigurationBuilder().AddJsonFile(file).Build();
            return ConfigTracer.For(configuration).WithOctopus(variables, configure).Trace();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static void AssertSecretHidden(ProvenanceReport report)
    {
        Assert.DoesNotContain("hunter2", report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", report.ToJson(), StringComparison.Ordinal);
        var setting = report.Find("Queue:Endpoint")!;
        Assert.True(setting.IsSensitive);
        Assert.Equal("***", setting.Value);
        Assert.All(Flatten(setting.Steps), s =>
        {
            Assert.DoesNotContain("hunter2", s.Value ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", s.Note ?? string.Empty, StringComparison.Ordinal);
        });
        Assert.All(report.Warnings, w => Assert.DoesNotContain("hunter2", w, StringComparison.Ordinal));

        static IEnumerable<ProvenanceStep> Flatten(IEnumerable<ProvenanceStep> steps) =>
            steps.SelectMany(s => Flatten(s.Origins).Prepend(s));
    }

    [Fact]
    public void Checks_its_arguments()
    {
        var resolver = new OctopusResolver(Variables, new OctopusTraceOptions());

        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!, new TraceDiagnostics()));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(EnvStep("A", "1"), null!));
    }

    [Fact]
    public void Passes_load_warnings_to_the_trace_once()
    {
        var resolver = new OctopusResolver(new OctopusVariables([], ["no variables"]), new OctopusTraceOptions());
        var diagnostics = new TraceDiagnostics();

        resolver.Resolve(EnvStep("A", "1"), diagnostics);
        resolver.Resolve(EnvStep("B", "1"), diagnostics);

        Assert.Equal(["no variables"], diagnostics.Warnings);
    }

    [Fact]
    public void Warns_when_a_mapped_variable_does_not_apply()
    {
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(Resolve(EnvStep("X", "1"), o => o.Map("X", "Missing"), diagnostics));
        Assert.Empty(Resolve(EnvStep("Unmapped", "1"), diagnostics: diagnostics));

        Assert.Equal(["X is linked to Octopus variable Missing, but no such variable applies to this deployment."], diagnostics.Warnings);
    }

    [Fact]
    public void Follows_a_mapping_from_a_JSON_file_key_but_not_from_another_layer_key()
    {
        var json = Assert.Single(Resolve(FileStep("App:Region", "us"), o => o.Map("App:Region", "Region")), s => s.Location == "project api");
        Assert.False(json.IsInferred);

        var other = new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "x", ConfigurationKey = "App:Region", Reference = new ConfigMapKeyRef("cm", "OTHER") };
        Assert.Empty(Resolve(other, o => o.Map("App:Region", "Region")));
    }

    [Fact]
    public void Ranks_by_the_documented_weights()
    {
        var variables = new OctopusVariables(
        [
            Var("A", "action", scope: ("Action", "Deploy")),
            Var("A", "channel", scope: ("Channel", "Default")),
            Var("A", "machine", scope: ("Machine", "web-01")),
            Var("A", "environment", scope: ("Environment", "Production")),
            Var("A", "tenant", scope: ("TenantTag", "Region/West")),
            Var("A", "role", scope: ("Role", "web")),
            Var("A", "process", scope: ("ProcessOwner", "Api")),
            Var("A", "unscoped"),
        ]);
        var options = new OctopusTraceOptions { Environment = "Production", Machine = "web-01", Channel = "Default", Step = "Deploy" };
        options.Roles.Add("web");
        options.TenantTags.Add("West");
        var diagnostics = new TraceDiagnostics();

        var chain = new OctopusResolver(variables, options).Resolve(EnvStep("A", null), diagnostics);

        Assert.Equal(["unscoped", "process", "channel", "environment", "tenant", "role", "machine", "action"], chain.Select(s => s.Value));
        Assert.Empty(diagnostics.Warnings);
        Assert.Equal("matched by name; scoped to step Deploy", chain[^1].Note);
        Assert.Equal("matched by name; scoped to target web-01", chain[^2].Note);
        Assert.Equal("matched by name; scoped to target tag web", chain[^3].Note);
        Assert.Equal("matched by name; scoped to tenant tag Region/West", chain[^4].Note);
        Assert.Equal("matched by name; scoped to environment Production", chain[^5].Note);
        Assert.Equal("matched by name; scoped to channel Default", chain[^6].Note);
        Assert.StartsWith("matched by name; scoped to process Api", chain[^7].Note, StringComparison.Ordinal);
        Assert.Equal("matched by name", chain[0].Note);
    }

    [Fact]
    public void Needs_only_one_scope_value_to_match_any_context_value()
    {
        var variables = new OctopusVariables([Var("A", "1", scope: [("Role", "db"), ("Role", "web")])]);
        var options = new OctopusTraceOptions();
        options.Roles.Add("api");
        options.Roles.Add("web");

        var step = Assert.Single(new OctopusResolver(variables, options).Resolve(EnvStep("A", null), new TraceDiagnostics()));

        Assert.Equal("matched by name; scoped to target tag db, web", step.Note);
    }

    [Fact]
    public void Describes_unknown_dimensions_by_their_names()
    {
        var variables = new OctopusVariables(
        [
            Var("A", "1", scope: [("Machine", "web-01"), ("Action", "Deploy"), ("TenantTag", "Region/West"), ("ProcessOwner", "Runbook"), ("Channel", "Beta"), ("Custom", "x")]),
        ]);

        var step = Assert.Single(new OctopusResolver(variables, new OctopusTraceOptions { MatchByName = false }.Map("A", "A")).Resolve(EnvStep("A", null), new TraceDiagnostics()));

        Assert.True(step.IsInferred);
        Assert.Equal(
            "scoped to target web-01; step Deploy; tenant tag Region/West; process Runbook; channel Beta; Custom x; "
            + "applies only if the deployment matches target web-01 and step Deploy and tenant tag Region/West and process Runbook and channel Beta and Custom x, which was not given",
            step.Note);
    }

    [Fact]
    public void Leaves_the_note_empty_for_a_certain_unscoped_link_with_the_same_value()
    {
        var step = Assert.Single(Resolve(EnvStep("X", "us"), o => o.Map("X", "Region")), s => s.Location == "project api");

        Assert.Equal(string.Empty, step.Note);
        Assert.False(step.IsInferred);
        Assert.Equal("Octopus variable Region", step.Source);
        Assert.Equal(new OctopusVariableRef("api", "Region"), step.Reference);
    }

    [Fact]
    public void Names_the_type_of_a_variable_that_is_not_a_string()
    {
        var variables = new OctopusVariables(
        [
            new OctopusVariable("Cert", "Certificates-1", false, "Certificate", "api", true, new Dictionary<string, IReadOnlyList<string>>()),
            new OctopusVariable("Text", "x", false, "string", "api", true, new Dictionary<string, IReadOnlyList<string>>()),
            new OctopusVariable("Key", null, true, "Sensitive", "api", true, new Dictionary<string, IReadOnlyList<string>>()),
        ]);
        var resolver = new OctopusResolver(variables, new OctopusTraceOptions());

        Assert.Equal("matched by name; type Certificate", Assert.Single(resolver.Resolve(EnvStep("Cert", "Certificates-1"), new TraceDiagnostics())).Note);
        Assert.Equal("matched by name", Assert.Single(resolver.Resolve(EnvStep("Text", "x"), new TraceDiagnostics())).Note);
        Assert.Equal("matched by name; Octopus does not return sensitive values", Assert.Single(resolver.Resolve(EnvStep("Key", "x"), new TraceDiagnostics())).Note);
    }

    [Fact]
    public void Says_nothing_about_differences_when_the_layer_has_no_value()
    {
        var step = Resolve(EnvStep("Region", null))[^1];

        Assert.Equal("matched by name", step.Note);
    }

    [Fact]
    public void Does_not_warn_about_equal_scopes_from_a_project_and_a_library_set()
    {
        var diagnostics = new TraceDiagnostics();

        Resolve(EnvStep("Region", "us"), diagnostics: diagnostics);

        Assert.Empty(diagnostics.Warnings);
    }

    [Fact]
    public void Treats_blank_options_as_not_given()
    {
        var options = new OctopusTraceOptions { Environment = " ", Machine = "", Channel = null, Step = "\t" };

        Assert.Null(options.Context("Environment"));
        Assert.Null(options.Context("Machine"));
        Assert.Null(options.Context("Channel"));
        Assert.Null(options.Context("Action"));
        Assert.Null(options.Context("Role"));
        Assert.Null(options.Context("TenantTag"));
        Assert.Null(options.Context("Other"));

        options.Machine = "web-01";
        options.Channel = "Beta";
        options.Step = "Deploy";
        options.TenantTags.Add("West");
        Assert.Equal(["web-01"], options.Context("Machine"));
        Assert.Equal(["Beta"], options.Context("Channel"));
        Assert.Equal(["Deploy"], options.Context("Action"));
        Assert.Equal(["West"], options.Context("TenantTag"));
    }

    [Fact]
    public void Ignores_blank_links_and_checks_its_arguments()
    {
        var options = new OctopusTraceOptions();
        options.Links["A"] = " ";

        Assert.Null(options.FindMapping("A"));
        Assert.Null(options.FindMapping("B"));
        Assert.Throws<ArgumentException>(() => options.Map(" ", "X"));
        Assert.Throws<ArgumentException>(() => options.Map("X", ""));
        Assert.Single(options.Links);
    }

    [Fact]
    public void WithOctopus_checks_its_arguments_and_applies_the_options()
    {
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());

        Assert.Throws<ArgumentNullException>(() => OctopusTracerExtensions.WithOctopus(null!, Variables));
        Assert.Throws<ArgumentNullException>(() => tracer.WithOctopus(null!));

        var applied = false;
        Assert.Same(tracer, tracer.WithOctopus(Variables, o => applied = true));
        Assert.True(applied);
    }

    [Fact]
    public async Task WithOctopusAsync_checks_its_arguments_and_says_how_to_register()
    {
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());
        using var provider = new ServiceCollection().BuildServiceProvider();
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentNullException>(() => OctopusTracerExtensions.WithOctopusAsync(null!, provider, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => tracer.WithOctopusAsync(null!, ct));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tracer.WithOctopusAsync(provider, ct));
        Assert.Equal("OctopusVariablesSource is not registered. Call services.AddConfigFerretOctopus(configuration.GetSection(\"ConfigFerret:Octopus\")) first.", ex.Message);
    }
}
