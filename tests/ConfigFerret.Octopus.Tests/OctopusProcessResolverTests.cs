namespace ConfigFerret.Octopus.Tests;

public class OctopusProcessResolverTests
{
    private static OctopusVariable Var(string name, string? value, bool sensitive = false, params (string Dimension, string Value)[] scope) =>
        new(name, value, sensitive, sensitive ? "Sensitive" : "String", "api", true,
            scope.GroupBy(s => s.Dimension).ToDictionary<IGrouping<string, (string Dimension, string Value)>, string, IReadOnlyList<string>>(g => g.Key, g => g.Select(s => s.Value).ToList()));

    private static readonly OctopusVariable[] Entries =
    [
        Var("Db:Timeout", "10"),
        Var("Db:Timeout", "15", scope: ("Action", "Deploy web")),
        Var("ConnectionString", "Server=db"),
        Var("Password", null, sensitive: true),
        Var("Name", "web"),
    ];

    private static OctopusDeploymentAction Deploy(string? structured = "appsettings.json", string? substitute = null) => new("Deploy web", "Deploy web", "Octopus.TentaclePackage")
    {
        StructuredConfigurationTargets = structured is null ? [] : [structured],
        SubstituteInFilesTargets = substitute is null ? [] : [substitute],
    };

    private static ProvenanceStep FileStep(string key, string? value, string path = "/app/appsettings.json") => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = Path.GetFileName(path),
        Value = value,
        ConfigurationKey = key,
        Reference = new FileRef(path),
    };

    private static IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, OctopusDeploymentProcess? process, Action<OctopusTraceOptions>? configure = null, TraceDiagnostics? diagnostics = null)
    {
        var options = new OctopusTraceOptions();
        configure?.Invoke(options);
        var variables = new OctopusVariables(Entries) { Process = process };
        return new OctopusResolver(variables, options).Resolve(layer, diagnostics ?? new TraceDiagnostics());
    }

    [Fact]
    public void Names_the_step_whose_structured_configuration_replaces_the_key_and_uses_its_step_scope()
    {
        var chain = Resolve(FileStep("Db:Timeout", "15"), new OctopusDeploymentProcess([Deploy()]));

        // The value scoped to the one step that replaces the file applies, without the step being given.
        Assert.Equal(["10", "15"], chain.Select(s => s.Value));
        Assert.False(chain[^1].IsInferred);
        Assert.Contains("replaced by structured configuration variables in step Deploy web (target appsettings.json)", chain[^1].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Says_when_no_step_replaces_the_file()
    {
        var chain = Resolve(FileStep("Db:Timeout", "10"), new OctopusDeploymentProcess([Deploy("other.json")], "deployment process of release 1.4.0"));

        // No step changes the file, so the step-scoped value is kept but only may apply.
        Assert.Equal(["10", "15"], chain.Select(s => s.Value));
        Assert.Contains("applies only if the deployment matches step Deploy web, which was not given", chain[^1].Note, StringComparison.Ordinal);
        Assert.True(chain[^1].IsInferred);
        Assert.Contains("but no step in the deployment process of release 1.4.0 replaces values in this file", chain[^1].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_the_name_match_without_a_process()
    {
        var chain = Resolve(FileStep("Db:Timeout", "10"), null);

        Assert.Equal(["10", "15"], chain.Select(s => s.Value));
        Assert.True(chain[^1].IsInferred);
        Assert.Contains("structured configuration variable for this key, matched by name", chain[^1].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_target_built_from_variables_may_replace_the_file()
    {
        var chain = Resolve(FileStep("Db:Timeout", "15"), new OctopusDeploymentProcess([Deploy("#{ConfigFile}")]));

        Assert.True(chain[^1].IsInferred);
        Assert.Contains("may be replaced by structured configuration variables in step Deploy web (target #{ConfigFile})", chain[^1].Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/app/config/settings.yaml", true)]
    [InlineData("/app/settings.yml", true)]
    [InlineData("/app/settings.ini", false)]
    public void Matches_YAML_keys_too(string path, bool linked)
    {
        var chain = Resolve(FileStep("Db:Timeout", "10", path), new OctopusDeploymentProcess([Deploy("**/*.y*ml")]));

        Assert.Equal(linked, chain.Count > 0);
    }

    [Theory]
    [InlineData("appsettings.json", "/app/appsettings.json", true)]
    [InlineData("appsettings.json", "/app/myappsettings.json", false)]
    [InlineData("config/*.json", "/app/config/a.json", true)]
    [InlineData("config/*.json", "/app/config/sub/a.json", false)]
    [InlineData("**/a.json", "/app/config/sub/a.json", true)]
    [InlineData("**/a.json", "/a.json", true)]
    [InlineData(".\\config\\a.json", "C:\\app\\config\\a.json", true)]
    [InlineData("APPSETTINGS.?SON", "/app/appsettings.json", true)]
    [InlineData("a+b(c).json", "/app/a+b(c).json", true)]
    public void Matches_target_patterns_at_the_end_of_the_path(string pattern, string path, bool matches)
    {
        Assert.Equal(matches ? TargetMatch.Yes : TargetMatch.No, OctopusDeploymentAction.MatchTarget([pattern], path, out _));
    }

    [Fact]
    public void Leaves_out_steps_that_do_not_run_in_the_deployment()
    {
        OctopusDeploymentAction[] actions =
        [
            Deploy() with { IsDisabled = true },
            Deploy() with { Environments = ["Staging"] },
            Deploy() with { ExcludedEnvironments = ["Production"] },
            Deploy() with { Roles = ["worker"] },
            Deploy() with { Channels = ["Hotfix"] },
        ];
        foreach (var action in actions)
        {
            var chain = Resolve(FileStep("Db:Timeout", "10"), new OctopusDeploymentProcess([action]), o =>
            {
                o.Environment = "Production";
                o.Roles.Add("web");
                o.Channel = "Default";
            });

            Assert.Contains("no step in the deployment process replaces values in this file", chain[^1].Note, StringComparison.Ordinal);
        }

        var runs = Resolve(FileStep("Db:Timeout", "10"), new OctopusDeploymentProcess([Deploy() with { Environments = ["production"], Roles = ["web"] }]), o =>
        {
            o.Environment = "Production";
            o.Roles.Add("web");
        });
        Assert.False(runs[^1].IsInferred);
    }

    [Fact]
    public void Links_a_substitution_in_a_file_to_its_variable_and_step()
    {
        var chain = Resolve(
            FileStep("Db:Connection", "#{ConnectionString}"),
            new OctopusDeploymentProcess([Deploy(structured: null, substitute: "*.json")]));

        var step = Assert.Single(chain);
        Assert.Equal("Octopus variable ConnectionString", step.Source);
        Assert.Equal("Server=db", step.Value);
        Assert.False(step.IsInferred);
        Assert.Contains("#{ConnectionString} substituted into this file in step Deploy web (target *.json)", step.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Infers_a_substitution_without_a_process_or_a_step_that_substitutes_the_file()
    {
        var without = Assert.Single(Resolve(FileStep("Db:Connection", "#{ConnectionString}"), null));
        Assert.True(without.IsInferred);
        Assert.Contains("#{ConnectionString} written in this file, and Octopus substitutes it", without.Note, StringComparison.Ordinal);

        var none = Assert.Single(Resolve(FileStep("Db:Connection", "#{ConnectionString}"), new OctopusDeploymentProcess([Deploy()])));
        Assert.True(none.IsInferred);
        Assert.Contains("no step in the deployment process substitutes variables in it", none.Note, StringComparison.Ordinal);

        var env = Assert.Single(Resolve(new ProvenanceStep
        {
            Kind = LayerKind.DotNetProvider,
            Source = "environment variable DB",
            Value = "#{ConnectionString | ToUpper}",
            Reference = new EnvironmentVariableRef("DB"),
        }, null));
        Assert.Equal("Octopus variable ConnectionString", env.Source);
        Assert.True(env.IsInferred);
    }

    [Theory]
    [InlineData("##{ConnectionString}")]
    [InlineData("#{if ConnectionString}x#{/if}")]
    [InlineData("#{each n in ConnectionString}x#{/each}")]
    [InlineData("#{Octopus.Environment.Name}")]
    [InlineData("#{}")]
    public void Ignores_escapes_statements_and_system_variables(string value)
    {
        Assert.Empty(OctopusResolver.SubstitutionTokens(value));
    }

    [Fact]
    public void Finds_each_variable_once_up_to_the_limit()
    {
        Assert.Equal(["A", "b c", "Octo.x"], OctopusResolver.SubstitutionTokens("#{A}-#{ b c }-#{a}-#{Octo.x | Trim}"));
        Assert.Equal(20, OctopusResolver.SubstitutionTokens(string.Concat(Enumerable.Range(0, 50).Select(i => $"#{{V{i}}}"))).Count);
    }

    [Fact]
    public void Keeps_a_value_with_a_sensitive_variable_redacted_whatever_its_order()
    {
        var chain = Resolve(FileStep("Db:Connection", "pw=#{Password};name=#{Name}"), null);

        Assert.Equal(["Octopus variable Password", "Octopus variable Name"], chain.Select(s => s.Source));
        Assert.True(chain[^1].IsSensitive);
        Assert.Contains("another variable substituted into this value is sensitive", chain[^1].Note, StringComparison.Ordinal);
        Assert.Contains("one of 2 variables in this value", chain[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Can_turn_off_substitution_links()
    {
        var chain = Resolve(FileStep("Db:Timeout", "#{Name}"), null, o => o.MatchSubstitutions = false);

        Assert.Equal(["Octopus variable Db:Timeout"], chain.Select(s => s.Source).Distinct());
    }

    [Fact]
    public void Adds_a_step_template_parameter_last()
    {
        var action = Deploy(structured: null, substitute: "appsettings.json") with
        {
            StepTemplate = "Deploy .NET app",
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Name"] = "from-template", ["Secret"] = null, ["Ref"] = "#{Name}" },
        };
        var process = new OctopusDeploymentProcess([action]);

        var chain = Resolve(FileStep("App:Name", "#{Name}"), process);
        Assert.Equal(["Octopus variable Name", "Octopus step template parameter Name"], chain.Select(s => s.Source));
        Assert.Equal("from-template", chain[^1].Value);
        Assert.Equal(new OctopusStepParameterRef("Deploy web", "Name"), chain[^1].Reference);
        Assert.Contains("a parameter of step template Deploy .NET app, set on step Deploy web", chain[^1].Note, StringComparison.Ordinal);

        var secret = Assert.Single(Resolve(FileStep("App:Secret", "#{Secret}"), process));
        Assert.True(secret.IsSensitive);
        Assert.Null(secret.Value);

        var reference = Assert.Single(Resolve(FileStep("App:Ref", "#{Ref}"), process));
        Assert.Contains("references other Octopus variables", reference.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parameter_standing_in_for_a_sensitive_variable_stays_redacted()
    {
        var action = Deploy(structured: null, substitute: "appsettings.json") with
        {
            StepTemplate = "t",
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Password"] = "plain" },
        };

        var chain = Resolve(FileStep("App:Password", "#{Password}"), new OctopusDeploymentProcess([action]));

        Assert.True(chain[^1].IsSensitive);
    }
}
