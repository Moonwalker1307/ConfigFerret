using System.Text.Json;
using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;
using ConfigFerret.Kustomize;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tests;

public class EndToEndTests
{
    private static readonly Dictionary<string, string?> PodEnvironment = new()
    {
        ["CFT_Db__Timeout"] = "42",
        ["CFT_Db__Host"] = "db.internal",
        ["CFT_Db__Password"] = "prod-password",
        ["CFT_Logging__Level"] = "Information",
    };

    private static IConfigurationRoot BuildPodConfiguration() => new ConfigurationBuilder()
        .AddJsonFile(TestPaths.Data("app", "appsettings.json"))
        .AddJsonFile(TestPaths.Data("app", "appsettings.Production.json"))
        .AddInMemoryCollection(PodEnvironment.ToDictionary(p => p.Key["CFT_".Length..].Replace("__", ":", StringComparison.Ordinal), p => p.Value))
        .Build();

    // The in-memory collection stands in for the pod's environment so tests do not depend on process state.
    private static ProvenanceReport TraceWithEnvironmentProvider(bool reveal = false)
    {
        foreach (var (k, v) in PodEnvironment)
        {
            Environment.SetEnvironmentVariable(k, v);
        }

        var config = new ConfigurationBuilder()
            .AddJsonFile(TestPaths.Data("app", "appsettings.json"))
            .AddJsonFile(TestPaths.Data("app", "appsettings.Production.json"))
            .AddEnvironmentVariables("CFT_")
            .Build();

        return ConfigTracer.For(config)
            .WithOptions(o =>
            {
                o.Environment = PodEnvironment;
                o.RevealSensitiveValues = reveal;
            })
            .WithKubernetes(KubernetesManifests.LoadFile(TestPaths.Data("rendered.yaml")), container: "api")
            .WithHelm(HelmChart.Load(TestPaths.Data("chart"), [TestPaths.Data("values-prod.yaml")]))
            .Trace();
    }

    [Fact]
    public void Traces_a_value_from_appsettings_through_configmap_to_helm_values()
    {
        var report = TraceWithEnvironmentProvider();
        var setting = report.Find("Db:Timeout")!;

        Assert.Equal("42", setting.Value);
        var env = setting.EffectiveStep!;
        Assert.Equal("environment variable CFT_Db__Timeout", env.Source);

        var configMap = Assert.Single(env.Origins);
        Assert.Equal(LayerKind.KubernetesConfigMap, configMap.Kind);
        Assert.Equal("ConfigMap api-config", configMap.Source);
        Assert.Equal("42", configMap.Value);
        Assert.Contains("envFrom on Deployment/api, container api", configMap.Note, StringComparison.Ordinal);

        var template = Assert.Single(configMap.Origins);
        Assert.Equal("Helm template templates/configmap.yaml", template.Source);
        Assert.Equal("line 6", template.Location);
        Assert.True(template.IsInferred);

        Assert.Collection(template.Origins,
            v =>
            {
                Assert.Equal("values.yaml (chart default)", v.Source);
                Assert.Equal("30", v.Value);
                Assert.False(v.IsEffective);
            },
            v =>
            {
                Assert.Equal("values-prod.yaml", v.Source);
                Assert.Equal("line 2, db.timeout", v.Location);
                Assert.Equal("42", v.Value);
                Assert.True(v.IsEffective);
            });

        Assert.Equal(
            "set in appsettings.json, overridden by appsettings.Production.json, overridden again by environment variable CFT_Db__Timeout from ConfigMap api-config, rendered by Helm template templates/configmap.yaml from .Values.db.timeout, set in values-prod.yaml over values.yaml (chart default)",
            setting.Story);
    }

    [Fact]
    public void Traces_a_literal_env_entry_to_helm_values()
    {
        var setting = TraceWithEnvironmentProvider().Find("Db:Host")!;

        var literal = Assert.Single(setting.EffectiveStep!.Origins);
        Assert.Equal(LayerKind.KubernetesWorkload, literal.Kind);
        Assert.Equal("Deployment/api env CFT_Db__Host", literal.Source);
        var template = Assert.Single(literal.Origins);
        Assert.Equal("Helm template templates/deployment.yaml", template.Source);
        Assert.Equal("db.internal", template.Origins[^1].Value);
    }

    [Fact]
    public void Redacts_values_that_come_from_secrets()
    {
        var hidden = TraceWithEnvironmentProvider().Find("Db:Password")!;
        var shown = TraceWithEnvironmentProvider(reveal: true).Find("Db:Password")!;

        Assert.Equal("***", hidden.Value);
        var secret = Assert.Single(hidden.EffectiveStep!.Origins);
        Assert.Equal("Secret api-secrets", secret.Source);
        Assert.Equal("***", secret.Value);
        Assert.Equal("prod-password", shown.EffectiveStep!.Origins[0].Value);
        Assert.DoesNotContain("prod-password", TraceWithEnvironmentProvider().ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain("prod-password", TraceWithEnvironmentProvider().ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Warns_when_the_workload_has_several_containers_and_none_is_named()
    {
        Environment.SetEnvironmentVariable("CFW_Db__Timeout", "42");
        var config = new ConfigurationBuilder().AddEnvironmentVariables("CFW_").Build();

        var report = ConfigTracer.For(config)
            .WithOptions(o => o.Environment = new Dictionary<string, string?> { ["CFW_Db__Timeout"] = "42" })
            .WithKubernetes(KubernetesManifests.LoadFile(TestPaths.Data("rendered.yaml")))
            .Trace();

        var warning = Assert.Single(report.Warnings);
        Assert.Equal("Deployment/api has 2 containers; using 'api'. Pass a container name to pick another.", warning);
    }

    [Fact]
    public void Text_and_json_output_contain_the_whole_chain()
    {
        var report = TraceWithEnvironmentProvider();

        var text = report.Explain("Db:Timeout");
        Assert.Contains("Db:Timeout = \"42\"", text, StringComparison.Ordinal);
        Assert.Contains("[overridden] appsettings.json (line 4) = \"30\"", text, StringComparison.Ordinal);
        Assert.Contains("<- [effective]  values-prod.yaml (line 2, db.timeout) = \"42\"", text, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(report.ToJson());
        var timeout = json.RootElement.GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "Db:Timeout");
        var layers = timeout.GetProperty("layers");
        Assert.Equal(3, layers.GetArrayLength());
        Assert.Equal("KubernetesConfigMap", layers[2].GetProperty("origins")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public void Traces_configmap_keys_through_kustomize_overlays()
    {
        var manifests = KubernetesManifests.Parse("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: prod-api-config-7g2h9k4b5t
            data:
              KS_Db__Timeout: "42"
            ---
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: prod-api
            spec:
              template:
                spec:
                  containers:
                    - name: api
                      envFrom:
                        - configMapRef:
                            name: prod-api-config-7g2h9k4b5t
            """, "kustomize-build.yaml");
        var config = new ConfigurationBuilder().AddEnvironmentVariables("KS_").Build();
        Environment.SetEnvironmentVariable("KS_Db__Timeout", "42");
        config.Reload();

        var setting = ConfigTracer.For(config)
            .WithOptions(o => o.Environment = new Dictionary<string, string?> { ["KS_Db__Timeout"] = "42" })
            .WithKubernetes(manifests)
            .WithKustomize(Kustomization.Load(TestPaths.Data("kustomize", "overlays", "prod")))
            .Trace()
            .Find("Db:Timeout")!;

        var configMap = Assert.Single(setting.EffectiveStep!.Origins);
        Assert.Equal("ConfigMap prod-api-config-7g2h9k4b5t", configMap.Source);
        Assert.Collection(configMap.Origins,
            b =>
            {
                Assert.Equal("base/kustomization.yaml", b.Source);
                Assert.Equal("30", b.Value);
                Assert.Equal("create (literal)", b.Note);
            },
            o =>
            {
                Assert.Equal("overlays/prod/kustomization.yaml", o.Source);
                Assert.Equal("42", o.Value);
                Assert.Equal("merge (env file prod.properties:2)", o.Note);
                Assert.True(o.IsEffective);
            });
    }

    [Fact]
    public void Redacts_helm_values_that_render_a_secret()
    {
        var environment = new Dictionary<string, string?> { ["CFT_Redact__Db"] = "s3cret" };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Redact:Db"] = "s3cret" })
            .Build();
        var chart = HelmChart.FromText(
            [("values.yaml", "db:\n  key: s3cret\n")],
            [("templates/secret.yaml", "stringData:\n  dbkey: {{ .Values.db.key }}\n")]);
        var secretStep = new ProvenanceStep
        {
            Kind = LayerKind.KubernetesSecret,
            Source = "Secret db",
            Value = "s3cret",
            IsSensitive = true,
            Reference = new SecretKeyRef("db", "dbkey"),
        };

        // A custom resolver links the in-memory value to the Secret, which Helm then links to its values.
        var report = ConfigTracer.For(config)
            .WithOptions(o => o.Environment = environment)
            .WithResolver(new FixedResolver(secretStep))
            .WithHelm(chart)
            .Trace();

        var text = report.ToText() + report.ToJson();
        Assert.DoesNotContain("s3cret", text, StringComparison.Ordinal);
        var secret = Assert.Single(report.Find("Redact:Db")!.EffectiveStep!.Origins);
        var template = Assert.Single(secret.Origins);
        Assert.True(template.IsSensitive);
        Assert.True(Assert.Single(template.Origins).IsSensitive);
    }

    private sealed class FixedResolver(ProvenanceStep step) : IOriginResolver
    {
        public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics) =>
            layer.Kind == LayerKind.DotNetProvider ? [step] : [];
    }
}
