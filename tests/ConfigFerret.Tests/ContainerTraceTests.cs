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

    // ConfigMap and Secret volumes: a whole ConfigMap, a Secret with items, a projected volume and a subPath mount.
    private const string PodWithVolumes = """
        apiVersion: v1
        kind: ConfigMap
        metadata: { name: app-files }
        data:
          appsettings.json: '{ "Db": { "Timeout": 42 } }'
          logging.json: '{}'
        ---
        apiVersion: v1
        kind: Secret
        metadata: { name: tls }
        data:
          tls.key: c2VjcmV0LWtleQ==
          tls.crt: Y2VydA==
        ---
        apiVersion: v1
        kind: Pod
        metadata: { name: api-7d9f }
        spec:
          containers:
            - name: api
              env:
                - name: A
                  value: "1"
              volumeMounts:
                - { name: files, mountPath: /app/config }
                - { name: tls, mountPath: /etc/tls/ }
                - { name: files, mountPath: /app/appsettings.json, subPath: appsettings.json }
                - { name: merged, mountPath: /etc/merged }
                - { name: scratch, mountPath: /tmp }
          volumes:
            - name: files
              configMap: { name: app-files }
            - name: tls
              secret:
                secretName: tls
                items:
                  - { key: tls.key, path: private/key.pem }
            - name: merged
              projected:
                sources:
                  - configMap: { name: unread }
            - name: scratch
              emptyDir: {}
        """;

    [Fact]
    public void Lists_mounted_files_after_the_variables()
    {
        var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(PodWithVolumes)).Trace();

        Assert.Equal(
            ["A", "/app/appsettings.json", "/app/config/appsettings.json", "/app/config/logging.json", "/etc/tls/private/key.pem"],
            report.Settings.Select(s => s.Key));
        Assert.Contains("Volume merged on Pod/api-7d9f, container api mounts ConfigMap unread at /etc/merged, which was not loaded", Assert.Single(report.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void A_mounted_file_has_its_content_as_value()
    {
        var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(PodWithVolumes)).Trace();

        var file = report.Find("/app/appsettings.json")!;
        var step = Assert.Single(file.Steps);

        Assert.Equal("""{ "Db": { "Timeout": 42 } }""", file.Value);
        Assert.Equal("ConfigMap app-files", step.Source);
        Assert.Equal(new ConfigMapKeyRef("app-files", "appsettings.json"), step.Reference);
        Assert.StartsWith("mounted at /app/appsettings.json by volume files", step.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mounted_secret_file_is_redacted()
    {
        var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(PodWithVolumes)).Trace();

        var key = report.Find("/etc/tls/private/key.pem")!;

        Assert.True(key.IsSensitive);
        Assert.Equal("***", key.Value);
        Assert.Equal(new SecretKeyRef("tls", "tls.key"), Assert.Single(key.Steps).Reference);
        Assert.DoesNotContain("secret-key", report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_mounted_file_from_a_source_that_was_not_loaded_has_no_value()
    {
        var manifests = KubernetesManifests.Parse("""
            kind: Pod
            metadata: { name: p }
            spec:
              containers:
                - name: c
                  volumeMounts:
                    - { name: cfg, mountPath: /cfg }
              volumes:
                - name: cfg
                  configMap:
                    name: unread
                    items:
                      - { key: app.json, path: app.json }
            """);

        var file = ConfigTracer.ForContainer(manifests).Trace().Find("/cfg/app.json")!;

        Assert.Null(file.Value);
        Assert.EndsWith("its contents were not loaded", Assert.Single(file.Steps).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_says_why_a_value_is_missing()
    {
        var manifests = KubernetesManifests.Parse("""
            kind: Pod
            metadata: { name: p }
            spec:
              containers:
                - name: c
                  env:
                    - name: POD_IP
                      valueFrom:
                        fieldRef: { fieldPath: status.podIP }
                    - name: TOKEN
                      valueFrom:
                        secretKeyRef: { name: unread, key: token }
            """);

        var text = ConfigTracer.ForContainer(manifests).Trace().ToText();

        Assert.Contains("POD_IP = (set at run time)", text, StringComparison.Ordinal);
        Assert.Contains("TOKEN = (unknown)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithKubernetes_is_rejected_on_a_container_trace()
    {
        var manifests = KubernetesManifests.Parse(Pod);

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigTracer.ForContainer(manifests).WithKubernetes(manifests));

        Assert.Contains("ForContainer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForContainer_rejects_null_manifests() =>
        Assert.Throws<ArgumentNullException>(() => ConfigTracer.ForContainer(null!));
}
