using Microsoft.Extensions.Configuration;

namespace ConfigFerret.AzureDevOps.Tests;

public class AzureDevOpsResolverTests
{
    private static readonly AzureDevOpsVariableGroups Groups = new("contoso/api",
    [
        new(3, "shared", AzureDevOpsGroupType.Vsts, null, null, null,
        [
            new("Db.Timeout", "10", false, "shared", 3),
            new("Region", "eu", false, "shared", 3),
        ]),
        new(7, "api-production", AzureDevOpsGroupType.Vsts, null, null, null,
        [
            new("Db.Timeout", "30", false, "api-production", 7),
            new("Db.Password", null, true, "api-production", 7),
        ]),
        new(9, "kv", AzureDevOpsGroupType.AzureKeyVault, null, "contoso-prod", null,
        [
            new("ApiKey", null, false, "kv", 9, AzureDevOpsGroupType.AzureKeyVault, "contoso-prod"),
        ]),
    ]);

    private const string Pipeline = """
        trigger: none

        variables:
        - group: shared
        - name: Db.Timeout   # set in YAML too
          value: '20'
        - group: api-production
        - name: Cache.Size
          value: "64"
        - name: Alias.Timeout
          value: $(Db.Timeout)
        - name: Empty

        stages:
        - stage: deploy
          variables:
            Feature.Flag: on
            Quoted: 'it''s'
            ${{ if eq(variables['Build.SourceBranchName'], 'main') }}:
              Conditional: yes
          jobs:
          - job: run
            steps:
            - script: dotnet run
              env:
                DB_PASSWORD: $(Db.Password)
                "API_KEY": "$[variables.ApiKey]"
                TIMEOUT_ALIAS: ${{ variables['Alias.Timeout'] }} # template expression
                MISSING: $(Not.There)
                Literal: plain
        """;

    private static ProvenanceStep EnvStep(string name, string? value) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = $"environment variable {name}",
        Value = value,
        Reference = new EnvironmentVariableRef(name),
    };

    private static AzureDevOpsResolver Resolver(Action<AzureDevOpsTraceOptions>? configure = null, AzureDevOpsVariableGroups? groups = null)
    {
        var options = new AzureDevOpsTraceOptions();
        configure?.Invoke(options);
        return new AzureDevOpsResolver(groups ?? Groups, options);
    }

    private static IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep step, Action<AzureDevOpsTraceOptions>? configure = null, TraceDiagnostics? diagnostics = null) =>
        Resolver(configure).Resolve(step, diagnostics ?? new TraceDiagnostics());

    [Fact]
    public void Matches_the_environment_name_scripts_see_lowest_priority_first()
    {
        var chain = Resolve(EnvStep("DB_TIMEOUT", "30"));

        Assert.Equal(["10", "30"], chain.Select(s => s.Value));
        Assert.All(chain, s => Assert.Equal(LayerKind.AzureDevOps, s.Kind));
        Assert.All(chain, s => Assert.True(s.IsInferred));
        Assert.Equal("Azure DevOps variable group api-production variable Db.Timeout", chain[^1].Source);
        Assert.Equal("api-production", chain[^1].Location);
        Assert.Equal("matched by name: scripts see Db.Timeout as DB_TIMEOUT", chain[^1].Note);
        Assert.Equal(new AzureDevOpsVariableRef("contoso/api", "api-production", "Db.Timeout"), chain[^1].Reference);
        Assert.Contains("differs", chain[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_the_exact_name_case_insensitively()
    {
        var step = Assert.Single(Resolve(EnvStep("region", "eu")));

        Assert.Equal("matched by name", step.Note);
        Assert.Equal("eu", step.Value);
    }

    [Fact]
    public void Marks_secrets_sensitive_without_a_value_and_says_how_they_reach_scripts()
    {
        var step = Assert.Single(Resolve(EnvStep("DB_PASSWORD", "hunter2")));

        Assert.True(step.IsSensitive);
        Assert.Null(step.Value);
        Assert.Equal("Azure DevOps variable group api-production secret Db.Password", step.Source);
        Assert.Equal("matched by name: scripts see Db.Password as DB_PASSWORD; Azure DevOps does not return secret values; secret variables reach scripts only when mapped in an env: block", step.Note);
        Assert.DoesNotContain("hunter2", step.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Notes_a_key_vault_linked_variable_with_the_vault_as_its_location()
    {
        var step = Assert.Single(Resolve(EnvStep("APIKEY", "k")));

        Assert.True(step.IsSensitive);
        Assert.Equal("contoso-prod", step.Location);
        Assert.Contains("the value comes from secret ApiKey in Azure Key Vault contoso-prod", step.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Notes_a_key_vault_group_whose_vault_is_unknown()
    {
        var groups = new AzureDevOpsVariableGroups("p", [new(1, "kv", AzureDevOpsGroupType.AzureKeyVault, null, null, null, [new("S", null, true, "kv", 1, AzureDevOpsGroupType.AzureKeyVault)])]);

        var step = Assert.Single(Resolver(groups: groups).Resolve(EnvStep("S", null), new TraceDiagnostics()));

        Assert.Equal("kv", step.Location);
        Assert.Contains("Azure Key Vault (unknown vault)", step.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("does not return secret values", step.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_Kubernetes_env_entries_and_ConfigMap_and_Secret_keys()
    {
        Assert.Equal(2, Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesWorkload, Source = "w", Reference = new WorkloadEnvRef("api", "app", "DB_TIMEOUT") }).Count);
        Assert.Equal(2, Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "c", Reference = new ConfigMapKeyRef("cfg", "Db.Timeout") }).Count);
        Assert.Single(Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "s", Reference = new SecretKeyRef("sec", "DB_PASSWORD") }));
    }

    [Fact]
    public void Ignores_layers_it_cannot_name_and_its_own_steps()
    {
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.Helm, Source = "h", Reference = new HelmValueRef("Region") }));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "e", Reference = new ExternalSecretRef(null, null, "Region") }));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.AzureDevOps, Source = "a", Reference = new AzureDevOpsVariableRef("contoso/api", "shared", "Region") }));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.AzureDevOps, Source = "a", Reference = new AzurePipelinesVariableRef("x.yml", 1, "Region") }));
        Assert.Empty(Resolve(EnvStep("UNKNOWN", "1")));
        Assert.Empty(Resolve(EnvStep("REGION", "1"), o => o.MatchByName = false));
    }

    [Fact]
    public void Follows_an_explicit_link_to_any_group_or_one_group()
    {
        var any = Resolve(EnvStep("Db__Timeout", "30"), o => o.Map("Db__Timeout", "Db.Timeout"));
        Assert.Equal(["10", "30"], any.Select(s => s.Value));
        Assert.All(any, s => Assert.False(s.IsInferred));
        Assert.All(any, s => Assert.Equal("linked explicitly", s.Note?.Split(';')[0]));

        var one = Assert.Single(Resolve(EnvStep("Db__Timeout", "10"), o => o.Map("Db__Timeout", "Db.Timeout", "SHARED")));
        Assert.Equal("10", one.Value);
        Assert.Equal("linked explicitly", one.Note);

        var bound = Assert.Single(Resolve(EnvStep("Db__Timeout", "30"), o => o.Links["db__timeout"] = " api-production/Db.Timeout "));
        Assert.Equal("30", bound.Value);

        Assert.Equal(2, Resolve(EnvStep("X", "1"), o => o.Links["X"] = "/Db.Timeout/").Count);
    }

    [Fact]
    public void Ignores_an_empty_link_and_checks_its_arguments()
    {
        Assert.Equal(2, Resolve(EnvStep("DB_TIMEOUT", "30"), o => o.Links["DB_TIMEOUT"] = " ").Count);

        var options = new AzureDevOpsTraceOptions();
        Assert.Throws<ArgumentException>(() => options.Map(" ", "a"));
        Assert.Throws<ArgumentException>(() => options.Map("a", ""));
        Assert.Same(options, options.Map("a", "b", " "));
        Assert.Equal("b", options.Links["a"]);

        var resolver = Resolver();
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!, new TraceDiagnostics()));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(EnvStep("A", "1"), null!));
    }

    [Fact]
    public void Warns_once_about_a_link_to_a_variable_that_was_not_loaded()
    {
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(Resolve(EnvStep("Db__Timeout", "1"), o => o.Map("Db__Timeout", "Nope", "shared"), diagnostics));

        Assert.Equal("Db__Timeout is linked to Azure Pipelines variable shared/Nope, but no such variable was loaded.", Assert.Single(diagnostics.Warnings));
    }

    [Fact]
    public void Reads_pipeline_variables_groups_and_env_links()
    {
        var options = new AzureDevOpsTraceOptions().AddPipelineText(Pipeline, "azure-pipelines.yml");

        Assert.Equal(
            ["Db.Timeout=20", "Cache.Size=64", "Alias.Timeout=$(Db.Timeout)", "Empty=", "Feature.Flag=on", "Quoted=it's", "Conditional=yes"],
            options.Variables.Select(v => $"{v.Name}={v.Value}"));
        Assert.Equal(5, options.Variables[0].Line);
        Assert.Equal("azure-pipelines.yml", options.Variables[0].File);
        Assert.Equal(["shared", "api-production"], options.GroupLinks.Select(g => g.Group));
        Assert.Equal("azure-pipelines.yml line 4", options.GroupLinks[0].Where);
        Assert.True(options.GroupLinks[0].Order < options.Variables[0].Order);
        Assert.True(options.Variables[0].Order < options.GroupLinks[1].Order);

        Assert.Equal(
            ["DB_PASSWORD>Db.Password", "API_KEY>ApiKey", "TIMEOUT_ALIAS>Alias.Timeout", "MISSING>Not.There"],
            options.Mappings.Select(m => $"{m.Name}>{m.VariableName}"));
        Assert.Equal("azure-pipelines.yml line 26", options.Mappings.First().Where);
    }

    [Fact]
    public void Orders_groups_and_pipeline_variables_the_way_the_pipeline_lists_them()
    {
        var chain = Resolve(EnvStep("DB_TIMEOUT", "30"), o => o.AddPipelineText(Pipeline, "azure-pipelines.yml"));

        Assert.Equal(["10", "20", "30"], chain.Select(s => s.Value));
        Assert.Equal("Azure Pipelines variable Db.Timeout", chain[1].Source);
        Assert.Equal("azure-pipelines.yml line 5", chain[1].Location);
        Assert.Equal(new AzurePipelinesVariableRef("azure-pipelines.yml", 5, "Db.Timeout"), chain[1].Reference);
        Assert.False(chain[1].IsSensitive);
        Assert.True(chain[1].IsInferred);
    }

    [Fact]
    public void Puts_groups_no_pipeline_links_first_and_notes_them()
    {
        const string yaml = """
            variables:
            - group: api-production
            - group: kv
            """;

        var chain = Resolve(EnvStep("DB_TIMEOUT", "30"), o => o.AddPipelineText(yaml, "p.yml"));

        Assert.Equal(["shared", "api-production"], chain.Select(s => ((AzureDevOpsVariableRef)s.Reference!).Group));
        Assert.Contains("no pipeline file read links this group", chain[0].Note, StringComparison.Ordinal);
        Assert.DoesNotContain("no pipeline file", chain[1].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Links_env_entries_from_the_pipeline_and_lets_an_explicit_link_win()
    {
        var password = Assert.Single(Resolve(EnvStep("DB_PASSWORD", null), o => o.AddPipelineText(Pipeline, "azure-pipelines.yml")));
        Assert.False(password.IsInferred);
        Assert.Equal("azure-pipelines.yml line 26 sets DB_PASSWORD from $(Db.Password); Azure DevOps does not return secret values", password.Note);

        var region = Assert.Single(Resolve(EnvStep("DB_PASSWORD", "eu"), o => o.AddPipelineText(Pipeline, "azure-pipelines.yml").Map("DB_PASSWORD", "Region")));
        Assert.Equal("eu", region.Value);
    }

    [Fact]
    public void Warns_about_a_pipeline_link_to_a_missing_variable()
    {
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(Resolve(EnvStep("MISSING", "1"), o => o.AddPipelineText(Pipeline, "azure-pipelines.yml"), diagnostics));

        Assert.Equal("MISSING is linked to Azure Pipelines variable Not.There (azure-pipelines.yml line 29), but no such variable was loaded.", Assert.Single(diagnostics.Warnings));
    }

    [Fact]
    public void Follows_a_pipeline_variable_that_refers_to_another_variable()
    {
        var resolver = Resolver(o => o.AddPipelineText(Pipeline, "azure-pipelines.yml"));
        var alias = Assert.Single(resolver.Resolve(EnvStep("TIMEOUT_ALIAS", "30"), new TraceDiagnostics()));
        Assert.Equal("$(Db.Timeout)", alias.Value);
        Assert.DoesNotContain("differs", alias.Note, StringComparison.Ordinal);

        var origins = resolver.Resolve(alias, new TraceDiagnostics());

        Assert.Equal(["10", "20", "30"], origins.Select(s => s.Value));
        Assert.All(origins, s => Assert.False(s.IsInferred));
        Assert.Equal("azure-pipelines.yml line 10 sets Alias.Timeout from $(Db.Timeout)", origins[0].Note);
        Assert.DoesNotContain("differs", origins[0].Note, StringComparison.Ordinal);

        var plain = resolver.Resolve(new ProvenanceStep { Kind = LayerKind.AzureDevOps, Source = "a", Reference = new AzurePipelinesVariableRef("azure-pipelines.yml", 5, "Db.Timeout") }, new TraceDiagnostics());
        Assert.Empty(plain);
    }

    [Fact]
    public void Traces_through_a_pipeline_alias_end_to_end()
    {
        const string prefix = "CFADOALIAS_";
        Environment.SetEnvironmentVariable(prefix + "TIMEOUT_ALIAS", "30");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var report = ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "TIMEOUT_ALIAS"] = "30" })
                .WithAzureDevOps(Groups, o => o.AddPipelineText(Pipeline.Replace("TIMEOUT_ALIAS", prefix + "TIMEOUT_ALIAS", StringComparison.Ordinal), "azure-pipelines.yml"))
                .Trace();

            var setting = report.Find("TIMEOUT_ALIAS")!;
            var alias = Assert.Single(setting.Steps[^1].Origins);
            Assert.Equal("Azure Pipelines variable Alias.Timeout", alias.Source);
            Assert.Equal(3, alias.Origins.Count);
            Assert.True(alias.Origins[^1].IsEffective);
            Assert.Equal("30", alias.Origins[^1].Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "TIMEOUT_ALIAS", null);
        }
    }

    [Fact]
    public void Ends_a_variables_block_at_the_next_key_and_reads_inline_and_template_forms()
    {
        const string yaml = "variables:\r\n  A.B: 1\r\n  C: ''\r\n  D:\r\n  - not a var\r\npool: x\r\n  Z: 9\r\nX: $(A.B)\r\n- name: Y\r\nvalue: $(C)\r\nW: \"$( C )\"\r\nvariables: {}\r\n";

        var options = new AzureDevOpsTraceOptions().AddPipelineText(yaml, "p.yml");

        Assert.Equal(["A.B=1", "C="], options.Variables.Select(v => $"{v.Name}={v.Value}"));
        Assert.Equal(["X>A.B", "value>C"], options.Mappings.Select(m => $"{m.Name}>{m.VariableName}"));
        Assert.Empty(options.GroupLinks);
    }

    [Fact]
    public void Reads_list_items_in_any_field_order_and_skips_odd_ones()
    {
        const string yaml = """
            variables:
              - value: v1
                name: First
              - name: "bad name!"
                value: x
              - template: vars.yml
              - group: ''
              -
                name: Second
              - ${{ if true }}:
                - name: Third
                  value: 3
            """;

        var options = new AzureDevOpsTraceOptions().AddPipelineText(yaml, "p.yml");

        Assert.Equal(["First=v1", "Second=", "Third=3"], options.Variables.Select(v => $"{v.Name}={v.Value}"));
        Assert.Equal(3, options.Variables[0].Line);
        Assert.Empty(options.GroupLinks);
    }

    [Fact]
    public void Reads_configured_pipeline_files_once_and_warns_about_missing_ones()
    {
        var path = Path.Join(Path.GetTempPath(), $"cf-ado-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, "steps:\n- script: run\n  env:\n    DB_PASSWORD: $(Db.Password)\n");
        try
        {
            var options = new AzureDevOpsTraceOptions();
            options.Pipelines.Add(path);
            options.Pipelines.Add(" ");
            options.Pipelines.Add("does-not-exist/azure-pipelines.yml");
            var diagnostics = new TraceDiagnostics();

            var resolver = new AzureDevOpsResolver(Groups, options);
            var step = Assert.Single(resolver.Resolve(EnvStep("DB_PASSWORD", null), diagnostics));
            resolver.Resolve(EnvStep("DB_PASSWORD", null), diagnostics);
            _ = new AzureDevOpsResolver(Groups, options);

            Assert.False(step.IsInferred);
            Assert.StartsWith($"{Path.GetFileName(path)} line 4 sets", step.Note, StringComparison.Ordinal);
            var warning = Assert.Single(diagnostics.Warnings);
            Assert.StartsWith("Could not read Azure Pipelines file does-not-exist/azure-pipelines.yml: ", warning, StringComparison.Ordinal);
            Assert.Single(options.Mappings);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Passes_load_warnings_to_the_trace_once()
    {
        var groups = new AzureDevOpsVariableGroups("p", [], ["group x missing"]);
        var resolver = new AzureDevOpsResolver(groups, new AzureDevOpsTraceOptions());
        var diagnostics = new TraceDiagnostics();

        resolver.Resolve(EnvStep("A", "1"), diagnostics);
        resolver.Resolve(EnvStep("B", "1"), diagnostics);

        Assert.Equal(["group x missing"], diagnostics.Warnings);
    }

    [Fact]
    public void Checks_pipeline_arguments()
    {
        var options = new AzureDevOpsTraceOptions();

        Assert.Throws<ArgumentNullException>(() => options.AddPipelineText(null!, "p.yml"));
        Assert.Throws<ArgumentException>(() => options.AddPipelineText("", " "));
        Assert.Throws<ArgumentException>(() => options.AddPipeline(" "));
    }

    [Fact]
    public void Redacts_secrets_end_to_end()
    {
        const string prefix = "CFADOTEST_";
        Environment.SetEnvironmentVariable(prefix + "DB_TIMEOUT", "30");
        Environment.SetEnvironmentVariable(prefix + "DB_PASSWORD", "hunter2");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var groups = new AzureDevOpsVariableGroups("contoso/api",
            [
                new(1, "prod", AzureDevOpsGroupType.Vsts, null, null, null,
                [
                    new(prefix + "Db.Timeout", "30", false, "prod", 1),
                    new(prefix + "Db.Password", null, true, "prod", 1),
                ]),
            ]);

            var report = ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "DB_TIMEOUT"] = "30", [prefix + "DB_PASSWORD"] = "hunter2" })
                .WithAzureDevOps(groups)
                .Trace();

            var timeout = report.Find("DB_TIMEOUT")!;
            Assert.Equal($"set in environment variable {prefix}DB_TIMEOUT from Azure DevOps variable group prod variable {prefix}Db.Timeout", timeout.Story);

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
    public void Checks_the_tracer_extension_arguments()
    {
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());

        Assert.Throws<ArgumentNullException>(() => AzureDevOpsTracerExtensions.WithAzureDevOps(null!, Groups));
        Assert.Throws<ArgumentNullException>(() => tracer.WithAzureDevOps(null!));
        Assert.Same(tracer, tracer.WithAzureDevOps(Groups));
    }
}
