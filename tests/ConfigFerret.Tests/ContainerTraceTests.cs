using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;

namespace ConfigFerret.Tests;

public class ContainerTraceTests
{
    // A pod as a cluster browser sees it: the spec, one ConfigMap and one Secret, with no .NET configuration around it.
    private const string Pod = """
        apiVersion: v1
        kind: ConfigMap
        metadata: { name: api-config }
        data:
          DB__Timeout: "42"
          Logging__Level: Information
        ---
        apiVersion: v1
        kind: Secret
        metadata: { name: api-secret }
        data:
          Db__Password: aHVudGVyMi1tYWRlLXVw
        ---
        apiVersion: v1
        kind: Pod
        metadata: { name: api-7d9f }
        spec:
          containers:
            - name: api
              envFrom:
                - configMapRef: { name: api-config }
                - secretRef: { name: api-secret }
                  prefix: APP_
              env:
                - name: Logging__Level
                  value: Warning
                - name: POD_IP
                  valueFrom:
                    fieldRef: { fieldPath: status.podIP }
        """;

    [Fact]
    public void Lists_every_variable_the_container_gets_sorted_by_name()
    {
        var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(Pod)).Trace();

        Assert.Equal(["APP_Db__Password", "DB__Timeout", "Logging__Level", "POD_IP"], report.Settings.Select(s => s.Key));
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void An_env_entry_overrides_the_same_name_from_envFrom()
    {
        var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(Pod)).Trace();

        var level = report.Find("Logging__Level")!;

        Assert.Equal("Warning", level.Value);
        Assert.Equal(["ConfigMap api-config", "Pod/api-7d9f env Logging__Level"], level.Steps.Select(s => s.Source));
        Assert.Equal([false, true], level.Steps.Select(s => s.IsEffective));
        Assert.Equal("set in ConfigMap api-config, overridden by Pod/api-7d9f env Logging__Level", level.Story);
    }

    [Fact]
    public void Secret_values_are_redacted()
    {
        var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(Pod)).Trace();

        var password = report.Find("APP_Db__Password")!;

        Assert.True(password.IsSensitive);
        Assert.Equal("***", password.Value);
        Assert.Equal("***", Assert.Single(password.Steps).Value);
        Assert.DoesNotContain("hunter2", report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_variable_filled_in_at_run_time_is_listed_without_a_value()
    {
        var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(Pod)).Trace();

        var ip = report.Find("POD_IP")!;

        Assert.Null(ip.Value);
        Assert.Equal("downward API status.podIP", Assert.Single(ip.Steps).Source);
    }

    [Fact]
    public void Names_are_matched_case_sensitively()
    {
        var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(Pod)).Trace();

        Assert.NotNull(report.Find("DB__Timeout"));
        Assert.Null(report.Find("Db__Timeout"));
    }

    [Fact]
    public void Follows_a_configmap_key_to_the_helm_values_that_rendered_it()
    {
        // What a cluster browser can read from a Helm release Secret: the chart's values, the release's values and the templates.
        var chart = HelmChart.FromText(
            [("values.yaml (chart default)", "db:\n  timeout: 30\n"), ("release values", "db:\n  timeout: 42\n")],
            [("templates/configmap.yaml", """
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: api-config
                data:
                  DB__Timeout: {{ .Values.db.timeout | quote }}
                """)]);

        var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(Pod), "api-7d9f", "api").WithHelm(chart).Trace();

        var timeout = report.Find("DB__Timeout")!;
        var template = Assert.Single(Assert.Single(timeout.Steps).Origins);

        Assert.Equal("42", timeout.Value);
        Assert.Equal("Helm template templates/configmap.yaml", template.Source);
        Assert.Equal(["values.yaml (chart default)", "release values"], template.Origins.Select(o => o.Source));
        Assert.Equal(["30", "42"], template.Origins.Select(o => o.Value));
        Assert.Contains("rendered by Helm template templates/configmap.yaml from .Values.db.timeout", timeout.Story, StringComparison.Ordinal);
    }

    [Fact]
    public void Warns_when_an_envFrom_source_was_not_loaded()
    {
        var manifests = KubernetesManifests.Parse("""
            kind: Pod
            metadata: { name: p }
            spec:
              containers:
                - name: c
                  envFrom:
                    - secretRef: { name: unread }
                  env:
                    - name: A
                      value: "1"
            """);

        var report = ConfigTracer.ForContainer(manifests).Trace();

        Assert.Equal(["A"], report.Settings.Select(s => s.Key));
        Assert.Contains("envFrom Secret unread on Pod/p, container c was not loaded", Assert.Single(report.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_a_missing_workload_as_a_warning()
    {
        var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(Pod), workload: "other").Trace();

        Assert.Empty(report.Settings);
        Assert.Equal("No Kubernetes workload named 'other' found in the manifests.", Assert.Single(report.Warnings));
    }

    [Fact]
    public void ForContainer_rejects_null_manifests() =>
        Assert.Throws<ArgumentNullException>(() => ConfigTracer.ForContainer(null!));
}
