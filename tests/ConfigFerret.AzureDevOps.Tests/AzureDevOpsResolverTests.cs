using System.Globalization;
using System.Text;
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
    public void Ranks_a_stage_variable_over_a_pipeline_variable_written_below_the_stages()
    {
        // The pipeline's variables block comes after the stages, but the stage's own variable is the one its job sees.
        const string pipeline = "stages:\n- stage: deploy\n  variables:\n    Db.Timeout: 60\n  jobs:\n  - job: run\n    steps:\n    - script: ./run.sh\n"
            + "variables:\n  Db.Timeout: 30\n";
        var diagnostics = new TraceDiagnostics();

        var steps = Resolver(o => o.AddPipelineText(pipeline, "azure-pipelines.yml"), new AzureDevOpsVariableGroups("contoso/api", []))
            .Resolve(EnvStep("DB_TIMEOUT", "60"), diagnostics);

        Assert.Equal(["azure-pipelines.yml line 10", "azure-pipelines.yml line 4"], steps.Select(s => s.Location));
        Assert.Equal("60", steps[^1].Value);
        Assert.DoesNotContain("differs", steps[^1].Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_variable_names_with_a_hyphen_or_a_space()
    {
        // Key Vault secret names hold hyphens, and Azure Pipelines variable names may hold spaces.
        const string pipeline = "variables:\n- name: app-name\n  value: orders\n- name: Alias.Timeout\n  value: $(shared timeout)\n"
            + "steps:\n- script: ./run.sh\n  env:\n    ConnectionStrings__Default: $(sql-connection-string)\n    Db__Password: $(Db--Password)\n"
            + "    Cache__Mode: $[variables['cache mode']]\n    Db__Host: $(Db.Host)\n";
        var options = new AzureDevOpsTraceOptions().AddPipelineText(pipeline, "azure-pipelines.yml");
        var jobs = new AzureDevOpsTraceOptions().AddPipelineText("jobs:\n- job: run\n  variables:\n    feature-flag: on\n", "azure-pipelines.yml");

        Assert.Equal(
            ["ConnectionStrings__Default>sql-connection-string", "Db__Password>Db--Password", "Cache__Mode>cache mode", "Db__Host>Db.Host"],
            options.Mappings.Select(m => $"{m.Name}>{m.VariableName}"));
        Assert.Equal(["app-name=orders", "Alias.Timeout=$(shared timeout)"], options.Variables.Select(v => $"{v.Name}={v.Value}"));
        Assert.Equal("shared timeout", AzureDevOpsTraceOptions.MacroTarget(options.Variables[1].Value));
        var flag = Assert.Single(jobs.Variables);
        Assert.Equal("feature-flag=on", $"{flag.Name}={flag.Value}");
    }

    [Fact]
    public void Traces_an_env_link_to_a_key_vault_secret_with_hyphens()
    {
        var groups = new AzureDevOpsVariableGroups("contoso/api",
        [
            new(9, "api-kv", AzureDevOpsGroupType.AzureKeyVault, null, "contoso-prod", null,
            [
                new("Db--Password", null, false, "api-kv", 9, AzureDevOpsGroupType.AzureKeyVault, "contoso-prod"),
            ]),
        ]);
        const string pipeline = "variables:\n- group: api-kv\nsteps:\n- script: ./run.sh\n  env:\n    Db__Password: $(Db--Password)\n";

        var step = Assert.Single(Resolver(o => o.AddPipelineText(pipeline, "azure-pipelines.yml"), groups).Resolve(EnvStep("Db__Password", "p"), new TraceDiagnostics()));

        Assert.Equal("Azure DevOps variable group api-kv secret Db--Password", step.Source);
        Assert.True(step.IsSensitive);
        Assert.Contains("Azure Key Vault contoso-prod", step.Note, StringComparison.Ordinal);
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
    public void Follows_a_variable_that_refers_to_itself_to_the_other_levels_once()
    {
        const string prefix = "CFADOSELF_";
        var yaml = new StringBuilder($"variables:\n- name: {prefix}VERSION\n  value: '1.0'\nstages:\n");
        for (var i = 0; i < 5; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"- stage: service_{i}\n  variables:\n  - name: {prefix}VERSION\n    value: $({prefix}VERSION)\n");
        }

        var groups = new AzureDevOpsVariableGroups("contoso/api", [new(1, "release", AzureDevOpsGroupType.Vsts, null, null, null, [new(prefix + "VERSION", "1.2.3", false, "release", 1)])]);

        var setting = TraceOne(prefix, "VERSION", "1.2.3", groups, yaml.ToString()).Find("VERSION")!;

        var chain = Assert.Single(setting.Steps).Origins;
        Assert.Equal(["1.2.3", "1.0", .. Enumerable.Repeat($"$({prefix}VERSION)", 5)], chain.Select(s => s.Value));
        Assert.All(chain.Skip(2), stage =>
        {
            Assert.Equal(["1.2.3", "1.0"], stage.Origins.Select(s => s.Value));
            Assert.Equal([false, true], stage.Origins.Select(s => s.IsInferred));
        });
        Assert.Equal(18, Count(setting.Steps));
    }

    [Fact(Timeout = 30_000)]
    public async Task Keeps_the_trace_small_when_many_stages_refer_to_the_variable_itself()
    {
        // Without leaving the self references out, each of the 40 stages leads to the 39 others, for each level followed.
        const string prefix = "CFADOLOOP_";
        var yaml = new StringBuilder($"variables:\n  {prefix}PATH: /usr/bin\nstages:\n");
        for (var i = 0; i < 40; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"- stage: s{i}\n  variables:\n  - name: {prefix}PATH\n    value: $({prefix}PATH)\n");
        }

        var report = await Task.Run(() => TraceOne(prefix, "PATH", "/usr/bin", new AzureDevOpsVariableGroups("contoso/api", []), yaml.ToString()), TestContext.Current.CancellationToken);

        var steps = report.Find("PATH")!.Steps;
        Assert.Equal(41, Assert.Single(steps).Origins.Count);
        Assert.Equal(82, Count(steps));
    }

    private static ProvenanceReport TraceOne(string prefix, string key, string value, AzureDevOpsVariableGroups groups, string pipeline)
    {
        Environment.SetEnvironmentVariable(prefix + key, value);
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            return ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + key] = value })
                .WithAzureDevOps(groups, o => o.AddPipelineText(pipeline, "azure-pipelines.yml"))
                .Trace();
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + key, null);
        }
    }

    private static int Count(IEnumerable<ProvenanceStep> steps) => steps.Sum(s => 1 + Count(s.Origins));

    private static ProvenanceReport TraceMany(string prefix, Dictionary<string, string> values, AzureDevOpsVariableGroups groups, string pipeline)
    {
        var environment = values.ToDictionary(v => prefix + v.Key, v => (string?)v.Value);
        foreach (var (name, value) in environment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            return ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = environment)
                .WithAzureDevOps(groups, o => o.AddPipelineText(pipeline.Replace("PFX_", prefix, StringComparison.Ordinal), "azure-pipelines.yml"))
                .Trace();
        }
        finally
        {
            foreach (var name in environment.Keys)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    private static IEnumerable<ProvenanceStep> Flatten(IEnumerable<ProvenanceStep> steps) =>
        steps.SelectMany(s => Flatten(s.Origins).Prepend(s));

    [Fact]
    public void Warns_and_infers_when_sibling_stages_disagree()
    {
        const string prefix = "CFADOSTAGES_";
        const string pipeline = """
            variables:
            - group: shared

            stages:
            - stage: Staging
              variables:
                PFX_App.Mode: Staging
              jobs:
              - deployment: DeployStaging
                environment: staging
                strategy:
                  runOnce:
                    deploy:
                      steps:
                      - script: ./deploy.sh
                        env:
                          PFX_Db__Host: $(Staging.Db.Host)
            - stage: Production
              variables:
                PFX_App.Mode: Production
              jobs:
              - deployment: DeployProduction
                environment: production
                strategy:
                  runOnce:
                    deploy:
                      steps:
                      - script: ./deploy.sh
                        env:
                          PFX_Db__Host: $(Prod.Db.Host)
            """;
        var groups = new AzureDevOpsVariableGroups("contoso/api",
        [
            new(1, "shared", AzureDevOpsGroupType.Vsts, null, null, null,
            [
                new("Staging.Db.Host", "db-staging.example", false, "shared", 1),
                new("Prod.Db.Host", "db-prod.example", false, "shared", 1),
            ]),
        ]);

        var report = TraceMany(prefix, new() { ["APP_MODE"] = "Staging", ["Db__Host"] = "db-staging.example" }, groups, pipeline);

        // The tracer marks the last step effective, so the alternative with the value seen here goes last.
        var host = Assert.Single(report.Find("Db:Host")!.Steps).Origins;
        Assert.Equal(["Azure DevOps variable group shared variable Prod.Db.Host", "Azure DevOps variable group shared variable Staging.Db.Host"], host.Select(s => s.Source));
        Assert.All(host, s => Assert.True(s.IsInferred));
        Assert.All(host, s => Assert.DoesNotContain("differs", s.Note, StringComparison.Ordinal));
        var mode = Assert.Single(report.Find("APP_MODE")!.Steps).Origins;
        Assert.Equal([$"Azure Pipelines variable {prefix}App.Mode (stage Production)", $"Azure Pipelines variable {prefix}App.Mode (stage Staging)"], mode.Select(s => s.Source));
        Assert.All(mode, s => Assert.True(s.IsInferred));
        Assert.All(mode, s => Assert.Equal("matched by name: scripts see " + prefix + "App.Mode as " + prefix + "APP_MODE", s.Note));

        const string why = ", and the trace cannot tell which one ran this, so their steps are marked inferred.";
        Assert.Contains($"Azure Pipelines job DeployStaging in stage Staging, job DeployProduction in stage Production link {prefix}Db__Host to different values{why}", report.Warnings);
        Assert.Contains($"Azure Pipelines stage Staging, stage Production set {prefix}App.Mode differently{why}", report.Warnings);
        Assert.All(report.Warnings, w => Assert.DoesNotContain(".example", w, StringComparison.Ordinal));
    }

    [Fact]
    public void Gives_a_group_only_to_the_stage_that_links_it()
    {
        const string prefix = "CFADOGROUP_";
        const string pipeline = """
            stages:
            - stage: Staging
              variables:
              - group: api-staging
              - name: Db.Alias
                value: $(Db.Host)
              jobs:
              - job: Deploy
                steps:
                - script: ./deploy.sh
                  env:
                    PFX_Db__Host: $(Db.Host)
                    PFX_MAIL_AUTH: $(Mail.Auth)
            - stage: Production
              variables:
              - group: api-production
              jobs:
              - job: Deploy
                steps:
                - script: ./deploy.sh
                  env:
                    PFX_Db__Host: $(Db.Host)
                    PFX_MAIL_AUTH: $(Mail.Auth)
            """;
        var groups = new AzureDevOpsVariableGroups("contoso/api",
        [
            new(1, "api-staging", AzureDevOpsGroupType.Vsts, null, null, null,
            [
                new("Db.Host", "db-staging.example", false, "api-staging", 1),
                new("Mail.Auth", null, true, "api-staging", 1),
            ]),
            new(2, "api-production", AzureDevOpsGroupType.Vsts, null, null, null,
            [
                new("Db.Host", "db-prod.example", false, "api-production", 2),
                new("Mail.Auth", null, true, "api-production", 2),
            ]),
        ]);

        var report = TraceMany(prefix, new() { ["Db__Host"] = "db-staging.example", ["MAIL_AUTH"] = "fake-mail-auth" }, groups, pipeline);

        var host = Assert.Single(report.Find("Db:Host")!.Steps).Origins;
        Assert.Equal(["Azure DevOps variable group api-production variable Db.Host", "Azure DevOps variable group api-staging variable Db.Host"], host.Select(s => s.Source));
        Assert.All(host, s => Assert.True(s.IsInferred));
        Assert.Contains(report.Warnings, w => w.StartsWith($"Azure Pipelines job Deploy in stage Staging, job Deploy in stage Production link {prefix}Db__Host to different values", StringComparison.Ordinal));

        // Links that disagree are not matches by name, so the note about env: blocks does not apply. The key does not
        // look secret, so only the secret group variables make it sensitive.
        var auth = report.Find("MAIL_AUTH")!;
        Assert.True(auth.IsSensitive);
        var secrets = Assert.Single(auth.Steps).Origins;
        Assert.Equal(["Azure DevOps variable group api-staging secret Mail.Auth", "Azure DevOps variable group api-production secret Mail.Auth"], secrets.Select(s => s.Source));
        Assert.All(secrets, s => Assert.True(s.IsInferred));
        Assert.All(secrets, s => Assert.DoesNotContain("reach scripts", s.Note, StringComparison.Ordinal));
        Assert.DoesNotContain("fake-mail-auth", report.ToText(), StringComparison.Ordinal);

        var resolver = Resolver(o => o.AddPipelineText(pipeline, "azure-pipelines.yml"), groups);
        var alias = resolver.Resolve(new ProvenanceStep { Kind = LayerKind.AzureDevOps, Source = "a", Reference = new AzurePipelinesVariableRef("azure-pipelines.yml", 5, "Db.Alias") }, new TraceDiagnostics());
        var only = Assert.Single(alias);
        Assert.Equal("Azure DevOps variable group api-staging variable Db.Host", only.Source);
        Assert.False(only.IsInferred);
    }

    [Fact]
    public void Follows_a_stage_variable_to_its_own_stage_only()
    {
        const string pipeline = """
            variables:
              Db.Name: default-db
              Pipeline.Alias: $(Db.Name)
            stages:
            - stage: Staging
              variables:
                Db.Name: staging-db
                Db.Alias: $(Db.Name)
            - stage: Production
              variables:
                Db.Name: prod-db
            """;
        var diagnostics = new TraceDiagnostics();
        var resolver = Resolver(o => o.AddPipelineText(pipeline, "azure-pipelines.yml"), new AzureDevOpsVariableGroups("contoso/api", []));

        var staging = resolver.Resolve(new ProvenanceStep { Kind = LayerKind.AzureDevOps, Source = "a", Reference = new AzurePipelinesVariableRef("azure-pipelines.yml", 8, "Db.Alias") }, diagnostics);

        Assert.Equal(["default-db", "staging-db"], staging.Select(s => s.Value));
        Assert.Equal(["Azure Pipelines variable Db.Name", "Azure Pipelines variable Db.Name (stage Staging)"], staging.Select(s => s.Source));
        Assert.All(staging, s => Assert.False(s.IsInferred));
        Assert.Empty(diagnostics.Warnings);

        var shared = resolver.Resolve(new ProvenanceStep { Kind = LayerKind.AzureDevOps, Source = "a", Reference = new AzurePipelinesVariableRef("azure-pipelines.yml", 3, "Pipeline.Alias") }, diagnostics);

        Assert.Equal(["default-db", "staging-db", "prod-db"], shared.Select(s => s.Value));
        Assert.Equal([false, true, true], shared.Select(s => s.IsInferred));
        Assert.Equal("Azure Pipelines stage Staging, stage Production set Db.Name differently, and the trace cannot tell which one ran this, so their steps are marked inferred.", Assert.Single(diagnostics.Warnings));
    }

    [Fact]
    public void Counts_a_stage_variable_for_the_stage_jobs_that_do_not_set_it_again()
    {
        const string pipeline = """
            stages:
            - stage: Deploy
              variables:
                Db.Name: stage-db
                Stage.Alias: $(Db.Name)
              jobs:
              - job: Migrate
                variables:
                  Db.Name: migrate-db
                  Job.Alias: $(Db.Name)
              - job: Web
                steps:
                - script: ./run.sh
            """;
        var diagnostics = new TraceDiagnostics();
        var resolver = Resolver(o => o.AddPipelineText(pipeline, "azure-pipelines.yml"), new AzureDevOpsVariableGroups("contoso/api", []));

        var job = resolver.Resolve(new ProvenanceStep { Kind = LayerKind.AzureDevOps, Source = "a", Reference = new AzurePipelinesVariableRef("azure-pipelines.yml", 10, "Job.Alias") }, diagnostics);

        Assert.Equal(["stage-db", "migrate-db"], job.Select(s => s.Value));
        Assert.All(job, s => Assert.False(s.IsInferred));
        Assert.Empty(diagnostics.Warnings);

        var stage = resolver.Resolve(new ProvenanceStep { Kind = LayerKind.AzureDevOps, Source = "a", Reference = new AzurePipelinesVariableRef("azure-pipelines.yml", 5, "Stage.Alias") }, diagnostics);

        Assert.Equal(["stage-db", "migrate-db"], stage.Select(s => s.Value));
        Assert.All(stage, s => Assert.True(s.IsInferred));
        Assert.Equal("Azure Pipelines stage Deploy, job Migrate in stage Deploy set Db.Name differently, and the trace cannot tell which one ran this, so their steps are marked inferred.", Assert.Single(diagnostics.Warnings));
    }

    [Theory]
    [InlineData("env", "Password,User,User")]
    [InlineData("env", "User,Password,User")]
    [InlineData("env", "Password,User,Password")]
    [InlineData("env", "User,User,Password")]
    [InlineData("variables", "Password,User")]
    [InlineData("variables", "User,Password")]
    public void Redacts_every_alternative_when_one_may_be_secret(string form, string order)
    {
        const string prefix = "CFADOALT_";
        const string secret = "fake-smtp-pass";
        var pipeline = new StringBuilder("stages:\n");
        var stage = 0;
        foreach (var source in order.Split(','))
        {
            pipeline.Append(CultureInfo.InvariantCulture, $"- stage: s{stage++}\n");
            pipeline.Append(form == "env"
                ? $"  jobs:\n  - job: run\n    steps:\n    - script: ./run.sh\n      env:\n        PFX_MAIL_AUTH: $(Smtp.{source})\n"
                : $"  variables:\n    PFX_Mail.Auth: $(Smtp.{source})\n");
        }

        // Neither is secret in Azure DevOps; one is named like a secret. The value seen here is neither of them.
        var groups = new AzureDevOpsVariableGroups("contoso/api",
        [
            new(1, "mail", AzureDevOpsGroupType.Vsts, null, null, null,
            [
                new("Smtp.Password", secret, false, "mail", 1),
                new("Smtp.User", "fake-user", false, "mail", 1),
            ]),
        ]);

        var report = TraceMany(prefix, new() { ["MAIL_AUTH"] = $"smtp://fake-user:{secret}@mail" }, groups, pipeline.ToString());

        Assert.DoesNotContain(secret, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, report.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, report.Explain("MAIL_AUTH"), StringComparison.Ordinal);
        var setting = report.Find("MAIL_AUTH")!;
        Assert.True(setting.IsSensitive);
        Assert.Equal("***", setting.Value);
        Assert.All(Flatten(setting.Steps), s => Assert.True(s.IsSensitive));
        Assert.All(report.Warnings, w => Assert.DoesNotContain(secret, w, StringComparison.Ordinal));
    }

    [Fact(Timeout = 30_000)]
    public async Task Does_work_in_proportion_to_the_stages_that_link_a_name()
    {
        // Each stage links the shared group and sets Db.Host, and its job links Db__Host to it. Following each link may
        // look at its own stage and job only. There are fewer than the bound on one step, so every link is followed.
        const int stages = 400;
        var pipeline = new StringBuilder("stages:\n");
        for (var i = 0; i < stages; i++)
        {
            pipeline.Append(CultureInfo.InvariantCulture, $"- stage: s{i}\n  variables:\n  - group: shared\n  - name: Db.Host\n    value: h{i}\n  jobs:\n  - job: run\n    steps:\n    - script: ./run.sh\n      env:\n        Db__Host: $(Db.Host)\n");
        }

        var groups = new AzureDevOpsVariableGroups("contoso/api",
        [
            new(1, "shared", AzureDevOpsGroupType.Vsts, null, null, null, [new("Db.Host", "g", false, "shared", 1)]),
        ]);
        var resolver = Resolver(o => o.AddPipelineText(pipeline.ToString(), "azure-pipelines.yml"), groups);
        var diagnostics = new TraceDiagnostics();

        var steps = await Task.Run(
            () =>
            {
                var found = resolver.Resolve(EnvStep("Db__Host", "h7"), diagnostics);
                foreach (var step in found)
                {
                    resolver.Resolve(step, diagnostics);
                }

                return found;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2 * stages, steps.Count);
        Assert.Equal("h7", steps[^1].Value);
        Assert.InRange(resolver.Examined, stages, 30L * stages);
    }

    [Fact(Timeout = 120_000)]
    public async Task Does_not_look_at_self_references_it_leaves_out()
    {
        // Each stage sets X from Y and its job links MAIL_AUTH to X; the pipeline sets Y: $(Y) many times. Following
        // each stage's X to Y leaves every one of them out, so looking at them is wasted.
        const int stages = 999;
        const int items = 999;
        var pipeline = new StringBuilder("variables:\n");
        for (var i = 0; i < items; i++)
        {
            pipeline.Append("- name: Y\n  value: $(Y)\n");
        }

        pipeline.Append("stages:\n");
        for (var i = 0; i < stages; i++)
        {
            pipeline.Append(CultureInfo.InvariantCulture, $"- stage: s{i}\n  variables:\n    X: $(Y)\n  jobs:\n  - job: run\n    steps:\n    - script: ./run.sh\n      env:\n        MAIL_AUTH: $(X)\n");
        }

        var resolver = Resolver(o => o.AddPipelineText(pipeline.ToString(), "azure-pipelines.yml"), new AzureDevOpsVariableGroups("contoso/api", []));
        var diagnostics = new TraceDiagnostics();

        var steps = await Task.Run(
            () =>
            {
                var found = resolver.Resolve(EnvStep("MAIL_AUTH", "v"), diagnostics);
                foreach (var step in found)
                {
                    resolver.Resolve(step, diagnostics);
                }

                return found;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(stages, steps.Count);
        Assert.InRange(resolver.Examined, stages, 20L * (stages + items));
    }

    [Fact]
    public void Still_calls_an_earlier_link_missing_when_the_bound_cuts_a_later_job()
    {
        // Job A links MAIL_AUTH to a variable that is nowhere, job B to X, which the pipeline sets exactly as often as one
        // step looks at, and job C, cut on its first entry, to its own Y.
        var pipeline = new StringBuilder("variables:\n");
        for (var i = 0; i < 1_000; i++)
        {
            pipeline.Append(CultureInfo.InvariantCulture, $"- name: X\n  value: v{i}\n");
        }

        pipeline.Append("jobs:\n- job: A\n  steps:\n  - script: ./run.sh\n    env:\n      MAIL_AUTH: $(Not.Set)\n");
        pipeline.Append("- job: B\n  steps:\n  - script: ./run.sh\n    env:\n      MAIL_AUTH: $(X)\n");
        pipeline.Append("- job: C\n  variables:\n    Y: y\n  steps:\n  - script: ./run.sh\n    env:\n      MAIL_AUTH: $(Y)\n");
        var resolver = Resolver(o => o.AddPipelineText(pipeline.ToString(), "azure-pipelines.yml"));
        var diagnostics = new TraceDiagnostics();

        var steps = resolver.Resolve(EnvStep("MAIL_AUTH", "v999"), diagnostics);

        Assert.InRange(steps.Count, 1, 1_000);
        Assert.Contains(diagnostics.Warnings, w => w.Contains("than the trace follows for one step", StringComparison.Ordinal));
        var missing = Assert.Single(diagnostics.Warnings, w => w.Contains("no such variable", StringComparison.Ordinal));
        Assert.StartsWith("MAIL_AUTH is linked to Azure Pipelines variable Not.Set ", missing, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Leaves_out_a_job_the_bound_cuts_without_calling_it_missing(bool linked)
    {
        // Job A links MAIL_AUTH to X, which the pipeline sets exactly as often as one step looks at, so following A
        // uses up the bound and job B, which sets MAIL_AUTH itself or links it to its own Y, is cut on its first entry.
        var pipeline = new StringBuilder("variables:\n");
        for (var i = 0; i < 1_000; i++)
        {
            pipeline.Append(CultureInfo.InvariantCulture, $"- name: X\n  value: v{i}\n");
        }

        pipeline.Append("jobs:\n- job: A\n  steps:\n  - script: ./run.sh\n    env:\n      MAIL_AUTH: $(X)\n");
        pipeline.Append(linked
            ? "- job: B\n  variables:\n    Y: y\n  steps:\n  - script: ./run.sh\n    env:\n      MAIL_AUTH: $(Y)\n"
            : "- job: B\n  variables:\n    MAIL_AUTH: direct\n  steps:\n  - script: ./run.sh\n");
        var resolver = Resolver(o => o.AddPipelineText(pipeline.ToString(), "azure-pipelines.yml"));
        var diagnostics = new TraceDiagnostics();

        var steps = resolver.Resolve(EnvStep("MAIL_AUTH", "v999"), diagnostics);

        Assert.InRange(steps.Count, 1, 1_000);
        Assert.All(steps, s => Assert.True(s.IsSensitive));
        Assert.Contains(diagnostics.Warnings, w => w.Contains("than the trace follows for one step", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics.Warnings, w => w.Contains("no such variable", StringComparison.Ordinal));
    }

    [Fact]
    public void Keeps_the_job_own_variable_when_jobs_outside_stages_set_it_more_often_than_the_bound()
    {
        // Jobs outside stages: the first one sets X and links MAIL_AUTH to it, and more jobs after it than one step
        // looks at set their own X, which that job does not see.
        var pipeline = new StringBuilder("variables:\n  X: real\njobs:\n- job: J\n  variables:\n    X: real\n  steps:\n  - script: ./run.sh\n    env:\n      MAIL_AUTH: $(X)\n");
        for (var i = 0; i < 1_001; i++)
        {
            pipeline.Append(CultureInfo.InvariantCulture, $"- job: j{i}\n  variables:\n    X: other\n  steps:\n  - script: ./run.sh\n");
        }

        var resolver = Resolver(o => o.AddPipelineText(pipeline.ToString(), "azure-pipelines.yml"));
        var diagnostics = new TraceDiagnostics();

        var steps = resolver.Resolve(EnvStep("MAIL_AUTH", "real"), diagnostics);

        Assert.Equal(["Azure Pipelines variable X", "Azure Pipelines variable X (job J)"], steps.Select(s => s.Source));
        Assert.Equal("real", steps[^1].Value);
        Assert.Empty(diagnostics.Warnings);
    }

    [Fact]
    public void Keeps_the_linked_group_when_the_bound_cuts_the_unlinked_ones()
    {
        // More groups with Db.X than one step looks at, and the pipeline links only the last one.
        var groups = new AzureDevOpsVariableGroups("contoso/api", Enumerable.Range(0, 1_500)
            .Select(i => new AzureDevOpsVariableGroup(i, $"g{i}", AzureDevOpsGroupType.Vsts, null, null, null, [new("Db.X", $"v{i}", false, $"g{i}", i)]))
            .ToList());
        const string pipeline = "variables:\n- group: g1499\nsteps:\n- script: ./run.sh\n  env:\n    DB_X: $(Db.X)\n";
        var resolver = Resolver(o => o.AddPipelineText(pipeline, "azure-pipelines.yml"), groups);

        var steps = resolver.Resolve(EnvStep("DB_X", "v1499"), new TraceDiagnostics());

        // The tracer marks the last step effective, so the bound keeps the end, as the tracer's own bound does.
        Assert.Equal("Azure DevOps variable group g1499 variable Db.X", steps[^1].Source);
        Assert.DoesNotContain("no pipeline file read links this group", steps[^1].Note, StringComparison.Ordinal);
        Assert.InRange(steps.Count, 1, 1_000);
        Assert.All(steps, s => Assert.True(s.IsSensitive));
    }

    [Fact(Timeout = 120_000)]
    public async Task Bounds_the_work_when_every_link_sees_many_pipeline_variables()
    {
        // Each stage's job links Db__Host to Db.X, and every job sees each of the pipeline's Db.X items.
        const int stages = 1_000;
        const int items = 1_000;
        var pipeline = new StringBuilder("variables:\n");
        for (var i = 0; i < items; i++)
        {
            pipeline.Append(CultureInfo.InvariantCulture, $"- name: Db.X\n  value: v{i}\n");
        }

        pipeline.Append("stages:\n");
        for (var i = 0; i < stages; i++)
        {
            pipeline.Append(CultureInfo.InvariantCulture, $"- stage: s{i}\n  jobs:\n  - job: run\n    steps:\n    - script: ./run.sh\n      env:\n        Db__Host: $(Db.X)\n");
        }

        var resolver = Resolver(o => o.AddPipelineText(pipeline.ToString(), "azure-pipelines.yml"), new AzureDevOpsVariableGroups("contoso/api", []));
        var diagnostics = new TraceDiagnostics();

        var steps = await Task.Run(() => resolver.Resolve(EnvStep("Db__Host", "v7"), diagnostics), TestContext.Current.CancellationToken);

        Assert.InRange(resolver.Examined, stages, 20L * (stages + items));
        Assert.InRange(steps.Count, 1, 1_000);
        Assert.All(steps, s =>
        {
            Assert.True(s.IsSensitive);
            Assert.True(s.IsInferred);
        });
        Assert.Contains("Db__Host is linked or set in more Azure Pipelines stages, jobs and variables than the trace follows for one step (1,000), so not all of them were followed; the steps listed are marked inferred and sensitive.", diagnostics.Warnings);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Lets_a_stage_variable_win_over_the_pipeline_one_wherever_it_is_written(bool rootFirst)
    {
        const string root = "variables:\n  H: root\n";
        const string stages = "stages:\n"
            + "- stage: s1\n  variables:\n    H: a\n  jobs:\n  - job: run\n    steps:\n    - script: ./run.sh\n      env:\n        MAIL_AUTH: $(H)\n"
            + "- stage: s2\n  variables:\n    H: b\n  jobs:\n  - job: run\n    steps:\n    - script: ./run.sh\n      env:\n        MAIL_AUTH: $(H)\n";
        var resolver = Resolver(o => o.AddPipelineText(rootFirst ? root + stages : stages + root, "azure-pipelines.yml"), new AzureDevOpsVariableGroups("contoso/api", []));
        var diagnostics = new TraceDiagnostics();

        var steps = resolver.Resolve(EnvStep("MAIL_AUTH", "a"), diagnostics);

        // Each stage's own H wins over the pipeline's, so the two stages disagree.
        Assert.Equal(["root", "b", "root", "a"], steps.Select(s => s.Value));
        Assert.All(steps, s => Assert.True(s.IsInferred));
        Assert.Contains("Azure Pipelines job run in stage s1, job run in stage s2 link MAIL_AUTH to different values, and the trace cannot tell which one ran this, so their steps are marked inferred.", diagnostics.Warnings);
    }

    [Fact]
    public void Lets_a_job_variable_win_over_a_stage_one_written_after_it()
    {
        const string pipeline = "stages:\n- stage: s1\n  jobs:\n  - job: run\n    variables:\n      H: job\n    steps:\n    - script: ./run.sh\n      env:\n        MAIL_AUTH: $(H)\n  variables:\n    H: stage\n";
        var resolver = Resolver(o => o.AddPipelineText(pipeline, "azure-pipelines.yml"), new AzureDevOpsVariableGroups("contoso/api", []));

        var steps = resolver.Resolve(EnvStep("MAIL_AUTH", "job"), new TraceDiagnostics());

        Assert.Equal(["stage", "job"], steps.Select(s => s.Value));
        Assert.All(steps, s => Assert.False(s.IsInferred));
    }

    [Fact]
    public void Compares_what_a_reference_comes_to_in_each_stage()
    {
        const string prefix = "CFADOREF_";
        const string pipeline = """
            stages:
            - stage: Staging
              variables:
                Host.Var: db-staging.example
                PFX_Db.Host: $(Host.Var)
              jobs:
              - job: Deploy
                steps:
                - script: ./deploy.sh
                  env:
                    PFX_Db__Host: $(PFX_Db.Host)
            - stage: Production
              variables:
                Host.Var: db-production.example
                PFX_Db.Host: $(Host.Var)
              jobs:
              - job: Deploy
                steps:
                - script: ./deploy.sh
                  env:
                    PFX_Db__Host: $(PFX_Db.Host)
            """;
        var groups = new AzureDevOpsVariableGroups("contoso/api", []);

        var report = TraceMany(prefix, new() { ["Db__Host"] = "db-staging.example", ["DB_HOST"] = "db-staging.example" }, groups, pipeline);

        var host = Assert.Single(report.Find("Db:Host")!.Steps).Origins;
        Assert.Equal([$"Azure Pipelines variable {prefix}Db.Host (stage Production)", $"Azure Pipelines variable {prefix}Db.Host (stage Staging)"], host.Select(s => s.Source));
        Assert.All(host, s => Assert.True(s.IsInferred));
        Assert.Contains(report.Warnings, w => w.StartsWith($"Azure Pipelines job Deploy in stage Staging, job Deploy in stage Production link {prefix}Db__Host to different values", StringComparison.Ordinal));
        var byName = Assert.Single(report.Find("DB_HOST")!.Steps).Origins;
        Assert.Equal(2, byName.Count);
        Assert.All(byName, s => Assert.True(s.IsInferred));
        Assert.Contains(report.Warnings, w => w.StartsWith($"Azure Pipelines stage Staging, stage Production set {prefix}Db.Host differently", StringComparison.Ordinal));

        // When the reference comes to the same variable in each stage, it is one certain link.
        const string agreeing = "variables:\n  Host.Var: db.example\nstages:\n"
            + "- stage: Staging\n  variables:\n    PFX_Db.Host: $(Host.Var)\n  jobs:\n  - job: Deploy\n    steps:\n    - script: ./deploy.sh\n      env:\n        PFX_Db__Host: $(PFX_Db.Host)\n"
            + "- stage: Production\n  variables:\n    PFX_Db.Host: $(Host.Var)\n  jobs:\n  - job: Deploy\n    steps:\n    - script: ./deploy.sh\n      env:\n        PFX_Db__Host: $(PFX_Db.Host)\n";
        var shared = TraceMany(prefix, new() { ["Db__Host"] = "db.example" }, groups, agreeing);
        Assert.False(Assert.Single(Assert.Single(shared.Find("Db:Host")!.Steps).Origins).IsInferred);
    }

    [Theory]
    [InlineData("steps:\n- script: ./run.sh\n  env:\n    PFX_MAIL_AUTH: $(Smtp.Password)\nstages:\n- stage: Test\n  jobs:\n  - job: run\n    steps:\n    - script: ./test.sh\n      env:\n        PFX_MAIL_AUTH: $(Smtp.User)\n", "the pipeline, job run in stage Test link")]
    [InlineData("stages:\n- stage: A\n  variables:\n    PFX_Mail.Auth: $(Smtp.Password)\n- stage: B\n  jobs:\n  - job: run\n    steps:\n    - script: ./run.sh\n      env:\n        PFX_MAIL_AUTH: $(Smtp.User)\n", "job run in stage B, stage A link or set")]
    public void Keeps_every_stage_or_job_that_may_set_a_linked_name_as_an_alternative(string pipeline, string parts)
    {
        const string prefix = "CFADOMIXED_";
        const string secret = "fake-smtp-pass";
        var groups = new AzureDevOpsVariableGroups("contoso/api",
        [
            new(1, "mail", AzureDevOpsGroupType.Vsts, null, null, null,
            [
                new("Smtp.Password", secret, false, "mail", 1),
                new("Smtp.User", "fake-user", false, "mail", 1),
            ]),
        ]);

        var report = TraceMany(prefix, new() { ["MAIL_AUTH"] = "fake-user" }, groups, pipeline);

        Assert.DoesNotContain(secret, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, report.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, report.Explain("MAIL_AUTH"), StringComparison.Ordinal);
        var setting = report.Find("MAIL_AUTH")!;
        Assert.True(setting.IsSensitive);
        Assert.Equal("***", setting.Value);
        var origins = Assert.Single(setting.Steps).Origins;
        Assert.Equal(2, origins.Count);
        Assert.All(origins, s => Assert.True(s.IsInferred));
        Assert.All(Flatten(setting.Steps), s => Assert.True(s.IsSensitive));
        Assert.Contains(report.Warnings, w => w.StartsWith($"Azure Pipelines {parts} {prefix}MAIL_AUTH to values that cannot be compared", StringComparison.Ordinal));
        Assert.All(report.Warnings, w => Assert.DoesNotContain(secret, w, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Marks_a_link_to_a_variable_set_from_a_secret_sensitive_whatever_is_traced_first(bool otherFirst)
    {
        // B leads to C, C to A, and A to B in one stage and to Smtp.Password in another.
        const string pipeline = """
            variables:
              B: $(C)
              C: $(A)
            stages:
            - stage: a
              variables:
                A: $(B)
            - stage: b
              variables:
                A: $(Smtp.Password)
            - stage: other_a
              jobs:
              - job: run
                steps:
                - script: ./run.sh
                  env:
                    OTHER: $(A)
            - stage: other_b
              jobs:
              - job: run
                steps:
                - script: ./run.sh
                  env:
                    OTHER: $(Plain)
            - stage: mail_a
              jobs:
              - job: run
                steps:
                - script: ./run.sh
                  env:
                    MAIL_AUTH: $(B)
            - stage: mail_b
              jobs:
              - job: run
                steps:
                - script: ./run.sh
                  env:
                    MAIL_AUTH: $(User.Var)
            """;
        var groups = new AzureDevOpsVariableGroups("contoso/api",
        [
            new(1, "plain", AzureDevOpsGroupType.Vsts, null, null, null,
            [
                new("Plain", "p", false, "plain", 1),
                new("User.Var", "u", false, "plain", 1),
            ]),
        ]);
        var resolver = Resolver(o => o.AddPipelineText(pipeline, "azure-pipelines.yml"), groups);
        var diagnostics = new TraceDiagnostics();

        if (otherFirst)
        {
            resolver.Resolve(EnvStep("OTHER", "p"), diagnostics);
        }

        var mail = resolver.Resolve(EnvStep("MAIL_AUTH", "u"), diagnostics);

        Assert.Equal(2, mail.Count);
        Assert.All(mail, s => Assert.True(s.IsSensitive));
    }

    [Fact]
    public void Orders_alternatives_by_the_value_seen_through_a_reference()
    {
        const string prefix = "CFADOSEEN_";
        const string pipeline = "variables:\n  PFX_Alias: $(Timeout)\nstages:\n- stage: Staging\n  variables:\n    Timeout: 5\n- stage: Production\n  variables:\n    Timeout: 9\n";

        var report = TraceMany(prefix, new() { ["ALIAS"] = "5" }, new AzureDevOpsVariableGroups("contoso/api", []), pipeline);

        // The tracer marks the last step effective, so the alternative with the value seen at the start goes last.
        var alias = Assert.Single(Assert.Single(report.Find("ALIAS")!.Steps).Origins);
        Assert.Equal(["9", "5"], alias.Origins.Select(s => s.Value));
        Assert.True(alias.Origins[^1].IsEffective);
    }

    [Fact]
    public void Compares_group_variables_by_value_unless_one_may_be_secret()
    {
        const string prefix = "CFADOVALUE_";
        const string pipeline = """
            stages:
            - stage: Staging
              variables:
              - group: api-staging
              jobs:
              - job: Deploy
                steps:
                - script: ./deploy.sh
                  env:
                    PFX_Db__Host: $(Db.Host)
                    PFX_MAIL_AUTH: $(Mail.Auth)
            - stage: Production
              variables:
              - group: api-production
              jobs:
              - job: Deploy
                steps:
                - script: ./deploy.sh
                  env:
                    PFX_Db__Host: $(Db.Host)
                    PFX_MAIL_AUTH: $(Mail.Auth)
            """;
        var groups = new AzureDevOpsVariableGroups("contoso/api",
        [
            new(1, "api-staging", AzureDevOpsGroupType.Vsts, null, null, null,
            [
                new("Db.Host", "db.example", false, "api-staging", 1),
                new("Mail.Auth", null, true, "api-staging", 1),
            ]),
            new(2, "api-production", AzureDevOpsGroupType.Vsts, null, null, null,
            [
                new("Db.Host", "db.example", false, "api-production", 2),
                new("Mail.Auth", null, true, "api-production", 2),
            ]),
        ]);

        var report = TraceMany(prefix, new() { ["Db__Host"] = "db.example", ["MAIL_AUTH"] = "fake-mail-auth" }, groups, pipeline);

        // The same value in both groups is one certain link.
        Assert.False(Assert.Single(Assert.Single(report.Find("Db:Host")!.Steps).Origins).IsInferred);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("Db__Host", StringComparison.Ordinal));

        // Secret values are not returned, so the two stay alternatives, and the warning does not say they differ.
        var auth = report.Find("MAIL_AUTH")!;
        Assert.True(auth.IsSensitive);
        Assert.All(Assert.Single(auth.Steps).Origins, s => Assert.True(s.IsInferred));
        Assert.Contains(report.Warnings, w => w.StartsWith($"Azure Pipelines job Deploy in stage Staging, job Deploy in stage Production link {prefix}MAIL_AUTH to values that cannot be compared", StringComparison.Ordinal));
        Assert.DoesNotContain("fake-mail-auth", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Lists_one_link_per_name_with_the_later_line()
    {
        const string yaml = "stages:\n- stage: A\n  jobs:\n  - job: run\n    steps:\n    - script: run\n      env:\n        Db__Host: $(A.Host)\n- stage: B\n  jobs:\n  - job: run\n    steps:\n    - script: run\n      env:\n        DB__HOST: $(B.Host)\n";

        var options = new AzureDevOpsTraceOptions().AddPipelineText(yaml, "azure-pipelines.yml");

        var mapping = Assert.Single(options.Mappings);
        Assert.Equal("DB__HOST>B.Host@azure-pipelines.yml line 15", $"{mapping.Name}>{mapping.VariableName}@{mapping.Where}");
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
    public void Tells_pipeline_files_of_one_name_in_different_folders_apart()
    {
        // Two environments with a variables file of the same name, each setting Db.Timeout on line 2 from its own variable.
        var folder = $"cf-ado-{Guid.NewGuid():N}";
        var first = Path.Join(folder, "dev", "variables.yml");
        var second = Path.Join(folder, "prod", "variables.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllText(first, "variables:\n  Db.Timeout: $(Dev.Timeout)\n");
        File.WriteAllText(second, "variables:\n  Db.Timeout: $(Prod.Timeout)\n");
        try
        {
            var groups = new AzureDevOpsVariableGroups("contoso/api",
            [
                new AzureDevOpsVariableGroup(1, "timeouts", AzureDevOpsGroupType.Vsts, null, null, null, [new("Dev.Timeout", "5", false, "timeouts", 1), new("Prod.Timeout", "60", false, "timeouts", 1)]),
            ]);
            var resolver = Resolver(o => o.AddPipeline(first).AddPipeline(second), groups);

            var chain = resolver.Resolve(EnvStep("DB_TIMEOUT", "60"), new TraceDiagnostics());
            var origin = Assert.Single(resolver.Resolve(chain[^1], new TraceDiagnostics()));

            Assert.Equal("Azure DevOps variable group timeouts variable Prod.Timeout", origin.Source);
            // A file in the current directory is named by its path from there, with "/" on every platform.
            Assert.Equal([$"{folder}/dev/variables.yml line 2", $"{folder}/prod/variables.yml line 2"], chain.Select(s => s.Location));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Keeps_a_backslash_in_a_pipeline_file_path_as_part_of_the_name()
    {
        // On Windows a backslash is the separator, so no name can hold one.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A Windows file name cannot hold a backslash.");
        var folder = $"cf-ado-{Guid.NewGuid():N}";
        Directory.CreateDirectory(Path.Join(folder, "a\\b"));
        File.WriteAllText(Path.Join(folder, "a\\b", "variables.yml"), "variables:\n  A: 1\n");
        try
        {
            var options = new AzureDevOpsTraceOptions().AddPipeline(Path.Join(folder, "a\\b", "variables.yml"));

            Assert.Equal($"{folder}/a\\b/variables.yml", Assert.Single(options.Variables).File);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
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
            Assert.Contains($"{Path.GetFileName(path)} line 4 sets", step.Note, StringComparison.Ordinal);
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
    public void Gives_each_warning_to_every_trace_step_it_applies_to()
    {
        // The tracer may ask with diagnostics it then drops, so a warning given once could be lost.
        var groups = new AzureDevOpsVariableGroups("contoso/api", [], ["group x missing"]);
        const string pipeline = "stages:\n- stage: Staging\n  variables:\n    Db.Name: staging-db\n- stage: Production\n  variables:\n    Db.Name: prod-db\n";
        var resolver = Resolver(o => o.AddPipelineText(pipeline, "azure-pipelines.yml"), groups);
        var first = new TraceDiagnostics();
        var second = new TraceDiagnostics();

        foreach (var diagnostics in new[] { first, second })
        {
            resolver.Resolve(EnvStep("DB_NAME", "staging-db"), diagnostics);
        }

        Assert.Equal(2, first.Warnings.Count);
        Assert.Equal(first.Warnings, second.Warnings);
        Assert.Contains("group x missing", second.Warnings);
        Assert.Contains(second.Warnings, w => w.StartsWith("Azure Pipelines stage Staging, stage Production set Db.Name differently", StringComparison.Ordinal));
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
    public void Marks_variables_named_like_secrets_sensitive_even_when_not_secret()
    {
        var groups = new AzureDevOpsVariableGroups("contoso/api",
        [
            new(1, "mail", AzureDevOpsGroupType.Vsts, null, null, null,
            [
                new("Smtp.Password", "fake-smtp-pass", false, "mail", 1),
                new("Smtp.Host", "smtp.example.com", false, "mail", 1),
            ]),
        ]);
        var resolver = new AzureDevOpsResolver(groups, new AzureDevOpsTraceOptions().AddPipelineText("variables:\n  Smtp.Password: fake-smtp-pass\n", "azure-pipelines.yml"));

        Assert.Equal([true, false], resolver.Entries().Select(e => e.IsSensitive));
        var chain = resolver.Resolve(EnvStep("SMTP_PASSWORD", "fake-smtp-pass"), new TraceDiagnostics());
        Assert.Equal(["Azure DevOps variable group mail variable Smtp.Password", "Azure Pipelines variable Smtp.Password"], chain.Select(s => s.Source));
        Assert.All(chain, s => Assert.True(s.IsSensitive));
        Assert.DoesNotContain("secret variables reach scripts", chain[0].Note, StringComparison.Ordinal);
        Assert.False(Assert.Single(resolver.Resolve(EnvStep("SMTP_HOST", "smtp.example.com"), new TraceDiagnostics())).IsSensitive);
    }

    [Theory]
    [InlineData("steps:\n- script: dotnet run\n  env:\n    {0}Mail__Auth: $(Smtp.Password)\n", "{0}Mail__Auth", "Mail:Auth", "Azure DevOps variable group mail variable Smtp.Password")]
    [InlineData("variables:\n  {0}Mail.Auth: $(Smtp.Password)\n", "{0}MAIL_AUTH", "MAIL_AUTH", "Azure DevOps variable group mail variable Smtp.Password")]
    [InlineData("variables:\n  Smtp.Password: fake-smtp-pass\nsteps:\n- script: dotnet run\n  env:\n    {0}Mail__Auth: $(Smtp.Password)\n", "{0}Mail__Auth", "Mail:Auth", "Azure Pipelines variable Smtp.Password")]
    public void Redacts_a_value_from_a_variable_named_like_a_secret_end_to_end(string pipeline, string variable, string key, string source)
    {
        const string prefix = "CFADONAMED_";
        const string secret = "fake-smtp-pass";
        variable = string.Format(CultureInfo.InvariantCulture, variable, prefix);

        // Not marked secret in Azure DevOps. The last case sets it only in the pipeline file.
        var groups = new AzureDevOpsVariableGroups("contoso/api", source.StartsWith("Azure DevOps", StringComparison.Ordinal)
            ? [new(1, "mail", AzureDevOpsGroupType.Vsts, null, null, null, [new("Smtp.Password", secret, false, "mail", 1)])]
            : []);
        Environment.SetEnvironmentVariable(variable, secret);
        ProvenanceReport report;
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            report = ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [variable] = secret })
                .WithAzureDevOps(groups, o => o.AddPipelineText(string.Format(CultureInfo.InvariantCulture, pipeline, prefix), "azure-pipelines.yml"))
                .Trace();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        Assert.DoesNotContain(secret, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, report.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, report.Explain(key), StringComparison.Ordinal);
        var setting = report.Find(key)!;
        Assert.All(Flatten(setting.Steps), s =>
        {
            Assert.DoesNotContain(secret, s.Value ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, s.Note ?? string.Empty, StringComparison.Ordinal);
        });
        Assert.All(report.Warnings, w => Assert.DoesNotContain(secret, w, StringComparison.Ordinal));
        Assert.True(setting.IsSensitive);
        Assert.Equal("***", setting.Value);
        var effective = Flatten(setting.Steps).Last(s => s.IsEffective && s.Origins.Count == 0);
        Assert.Equal(source, effective.Source);
        Assert.True(effective.IsSensitive);

        static IEnumerable<ProvenanceStep> Flatten(IEnumerable<ProvenanceStep> steps) =>
            steps.SelectMany(s => Flatten(s.Origins).Prepend(s));
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
