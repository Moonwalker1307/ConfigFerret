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
    public void A_key_that_is_secret_by_name_never_shows_its_helm_values_in_a_note()
    {
        // Neither key comes from a Secret: only their names say they are secret, and both differ from the chart's values.
        var chart = HelmChart.FromText(
            [("values.yaml", "db:\n  host: orders-db\n  password: fake-values-pw\n")],
            [
                ("templates/configmap.yaml", "data:\n  ConnectionStrings__Default: \"Host={{ .Values.db.host }};Password={{ .Values.db.password }}\"\n"),
                ("templates/deployment.yaml", "env:\n  - name: Db__Password\n    value: {{ .Values.db.password | quote }}\n"),
            ]);
        var manifests = KubernetesManifests.Parse("""
            apiVersion: v1
            kind: ConfigMap
            metadata: { name: api-config }
            data:
              ConnectionStrings__Default: "Host=orders-db;Password=fake-rendered-pw"
            ---
            apiVersion: v1
            kind: Pod
            metadata: { name: api-7d9f }
            spec:
              containers:
                - name: api
                  envFrom:
                    - configMapRef: { name: api-config }
                  env:
                    - name: Db__Password
                      value: fake-deployed-pw
            """);

        var report = ConfigTracer.ForContainer(manifests).WithHelm(chart).Trace();
        var output = report.ToText() + report.ToJson() + string.Join("\n", report.Warnings);

        Assert.Equal(["ConnectionStrings__Default", "Db__Password"], report.Settings.Select(s => s.Key));
        Assert.All(report.Settings, s => Assert.True(s.IsSensitive));
        foreach (var secret in new[] { "fake-values-pw", "fake-rendered-pw", "fake-deployed-pw", "orders-db" })
        {
            Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        }

        Assert.All(report.Settings, s => Assert.EndsWith(
            "; the values differ from the manifest, so the release may be out of date or the template transforms it",
            Assert.Single(Assert.Single(s.Steps).Origins).Note,
            StringComparison.Ordinal));
    }

    [Fact]
    public void A_value_from_a_configmap_key_named_like_a_secret_is_redacted()
    {
        const string secret = "fake-smtp-password";
        var manifests = KubernetesManifests.Parse($$"""
            apiVersion: v1
            kind: ConfigMap
            metadata: { name: api-config }
            data:
              smtp-password: {{secret}}
              smtp-host: mail.example.test
            ---
            apiVersion: apps/v1
            kind: Deployment
            metadata: { name: api }
            spec:
              template:
                spec:
                  containers:
                    - name: api
                      env:
                        - name: Smtp__Auth
                          valueFrom:
                            configMapKeyRef: { name: api-config, key: smtp-password }
                        - name: Smtp__Host
                          valueFrom:
                            configMapKeyRef: { name: api-config, key: smtp-host }
            """);

        var report = ConfigTracer.ForContainer(manifests).Trace();

        var auth = report.Find("Smtp__Auth")!;
        Assert.True(auth.IsSensitive);
        Assert.Equal("***", auth.Value);
        Assert.All(auth.Steps, s => Assert.Equal("***", s.Value));
        Assert.Equal("mail.example.test", report.Find("Smtp__Host")!.Value);
        Assert.DoesNotContain(secret, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, report.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, report.Explain("Smtp__Auth"), StringComparison.Ordinal);
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

    // Env values that refer to other variables as $(VAR): a Secret, a ConfigMap key, envFrom, a literal, an entry set
    // later in the list, a name that is not set, the $$ escape and a chain of references.
    private const string PodWithReferences = """
        apiVersion: v1
        kind: ConfigMap
        metadata: { name: db-config }
        data:
          DB_HOST: db.internal
          DB_NAME: orders
        ---
        apiVersion: v1
        kind: Secret
        metadata: { name: db }
        stringData:
          password: not-a-real-password
        ---
        apiVersion: v1
        kind: Pod
        metadata: { name: api }
        spec:
          containers:
            - name: api
              envFrom:
                - configMapRef: { name: db-config }
                  prefix: CFG_
              env:
                - name: DB_PASSWORD
                  valueFrom: { secretKeyRef: { name: db, key: password } }
                - name: DB_HOST
                  valueFrom: { configMapKeyRef: { name: db-config, key: DB_HOST } }
                - name: DB_USER
                  value: app
                - name: Database__Url
                  value: "postgres://$(DB_USER):$(DB_PASSWORD)@$(DB_HOST)/$(CFG_DB_NAME)"
                - name: Database__Host
                  value: "$(DB_HOST):5432"
                - name: Database__Port
                  value: "$(DB_PORT)"
                - name: DB_PORT
                  value: "5432"
                - name: Database__Schema
                  value: "$(DB_SCHEMA)"
                - name: Escaped
                  value: "$$(DB_HOST) costs $$$(DB_USER)"
                - name: Database__Replica
                  value: "$(Database__Url)?target_session_attrs=read-only"
                - name: CFG_DB_NAME
                  value: "$(CFG_DB_NAME)_test"
        """;

    // Every value, note and warning in a report, upstream steps included, with the text and JSON reports.
    private static string Everything(ProvenanceReport report)
    {
        static IEnumerable<ProvenanceStep> All(ProvenanceStep step) => step.Origins.SelectMany(All).Prepend(step);

        var steps = report.Settings.SelectMany(s => s.Steps).SelectMany(All).ToList();
        return string.Join(
            "\n",
            report.Settings.Select(s => s.Value)
                .Concat(steps.Select(s => s.Value))
                .Concat(steps.Select(s => s.Note))
                .Concat(report.Warnings)
                .Append(report.ToText())
                .Append(report.ToJson()));
    }

    [Fact]
    public void A_value_that_refers_to_a_secret_is_redacted()
    {
        var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(PodWithReferences, "pod.yaml")).Trace();

        var url = report.Find("Database__Url")!;
        var origins = Assert.Single(url.Steps).Origins;

        Assert.True(url.IsSensitive);
        Assert.Equal("***", url.Value);
        Assert.Equal(["Pod/api env DB_USER", "Secret db", "ConfigMap db-config", "ConfigMap db-config"], origins.Select(o => o.Source));
        Assert.All(origins, o => Assert.True(o.IsEffective));
        Assert.Matches(@"^secretKeyRef on Pod/api, container api; referenced as \$\(DB_PASSWORD\) in pod\.yaml:\d+$", origins[1].Note);
        Assert.Matches(@"^envFrom on Pod/api, container api with prefix 'CFG_'; referenced as \$\(CFG_DB_NAME\) in pod\.yaml:\d+$", origins[3].Note);
        Assert.True(report.Find("Database__Replica")!.IsSensitive);
        Assert.Equal("Pod/api env Database__Url", Assert.Single(Assert.Single(report.Find("Database__Replica")!.Steps).Origins).Source);
        Assert.False(report.Find("Database__Host")!.IsSensitive);
        Assert.DoesNotContain("not-a-real-password", Everything(report), StringComparison.Ordinal);
    }

    [Fact]
    public void Expands_references_the_way_the_kubelet_does()
    {
        var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(PodWithReferences, "pod.yaml"))
            .WithOptions(o => o.RevealSensitiveValues = true)
            .Trace();

        string? Value(string name) => report.Find(name)!.Value;

        Assert.Equal("postgres://app:not-a-real-password@db.internal/orders", Value("Database__Url"));
        Assert.Equal("db.internal:5432", Value("Database__Host"));
        Assert.Equal("$(DB_PORT)", Value("Database__Port"));
        Assert.Equal("$(DB_SCHEMA)", Value("Database__Schema"));
        Assert.Equal("$(DB_HOST) costs $app", Value("Escaped"));
        Assert.Equal("postgres://app:not-a-real-password@db.internal/orders?target_session_attrs=read-only", Value("Database__Replica"));
        Assert.Equal("orders_test", Value("CFG_DB_NAME"));
        Assert.Equal(["orders", "orders_test"], report.Find("CFG_DB_NAME")!.Steps.Select(s => s.Value));
    }

    [Fact]
    public void Says_how_references_are_expanded()
    {
        var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(PodWithReferences, "pod.yaml")).Trace();

        string? Note(string name) => report.Find(name)!.Steps[^1].Note;

        Assert.Equal("db.internal:5432", report.Find("Database__Host")!.Value);
        Assert.Equal("$(DB_HOST) costs $app", report.Find("Escaped")!.Value);
        Assert.Equal("container api; Kubernetes replaces $(DB_USER), $(DB_PASSWORD), $(DB_HOST) and $(CFG_DB_NAME) with their values", Note("Database__Url"));
        Assert.Equal("container api; 1 reference is left as written, because no variable of that name is set before this entry", Note("Database__Port"));
        Assert.Equal("container api; 1 reference is left as written, because no variable of that name is set before this entry", Note("Database__Schema"));
        Assert.Equal("container api; Kubernetes replaces $(DB_USER) with its value; Kubernetes turns $$ into $", Note("Escaped"));
        Assert.Empty(Assert.Single(report.Find("Database__Port")!.Steps).Origins);
    }

    [Fact]
    public void A_reference_to_a_value_the_manifests_do_not_hold_leaves_the_value_unknown()
    {
        var manifests = KubernetesManifests.Parse("""
            kind: Pod
            metadata: { name: p }
            spec:
              containers:
                - name: c
                  env:
                    - name: POD_IP
                      valueFrom: { fieldRef: { fieldPath: status.podIP } }
                    - name: TOKEN
                      valueFrom: { secretKeyRef: { name: unread, key: token } }
                    - name: Api__Self
                      value: "http://$(POD_IP):8080"
                    - name: Api__Url
                      value: "https://api.example.test/?t=$(TOKEN)"
            """);

        var report = ConfigTracer.ForContainer(manifests).WithOptions(o => o.RevealSensitiveValues = true).Trace();

        Assert.Null(report.Find("Api__Self")!.Value);
        Assert.Null(report.Find("Api__Url")!.Value);
        Assert.Equal("container c; the value of $(POD_IP) is not known here, so this value is not known either", report.Find("Api__Self")!.Steps[^1].Note);
        Assert.Contains("Api__Self = (unknown)", report.ToText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("p", "***", "container c; 1 reference is left as written, because no variable of that name is set before this entry")]
    [InlineData("q", null, "container c; 1 reference may take its value from a source that was not loaded, so this value is not known")]
    public void A_redacted_value_shows_no_part_of_a_reference_nothing_is_known_to_set(string workload, string? value, string note)
    {
        const string password = "Pa$(xq7)w0rd";

        // A password that happens to hold $(...), redacted by its name. In q only an unloaded Secret may set xq7.
        var manifests = KubernetesManifests.Parse($$"""
            kind: Pod
            metadata: { name: p }
            spec:
              containers:
                - name: c
                  env:
                    - name: DB_PASSWORD
                      value: "{{password}}"
            ---
            kind: Pod
            metadata: { name: q }
            spec:
              containers:
                - name: c
                  envFrom:
                    - secretRef: { name: unread }
                  env:
                    - name: DB_PASSWORD
                      value: "{{password}}"
            """);

        var report = ConfigTracer.ForContainer(manifests, workload).Trace();

        var setting = report.Find("DB_PASSWORD")!;
        Assert.Equal(value, setting.Value);
        Assert.True(setting.IsSensitive);
        Assert.Equal(note, setting.Steps[^1].Note);
        var everything = Everything(report);
        Assert.DoesNotContain("xq7", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Pa$", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("w0rd", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void Stops_expanding_once_the_values_of_a_container_grow_too_long()
    {
        // Each value doubles the one before it: 40 of them would ask for 2^41 characters.
        var env = new System.Text.StringBuilder("        - name: V0\n          value: ab\n");
        for (var i = 1; i < 40; i++)
        {
            env.Append(System.Globalization.CultureInfo.InvariantCulture, $"        - name: V{i}\n          value: \"$(V{i - 1})$(V{i - 1})\"\n");
        }

        env.Append("        - name: Last\n          value: \"$(V39)$(TOKEN)\"\n");
        var manifests = KubernetesManifests.Parse($$"""
            kind: Pod
            metadata: { name: p }
            spec:
              containers:
                - name: c
                  env:
                    - name: TOKEN
                      valueFrom: { secretKeyRef: { name: unread, key: token } }
            {{env}}
            """);

        var report = ConfigTracer.ForContainer(manifests).WithOptions(o => o.RevealSensitiveValues = true).Trace();

        // V1 to V21 hold 2^23 - 4 characters together; V22 would add 2^23 more.
        Assert.Equal(1 << 22, report.Find("V21")!.Value!.Length);
        Assert.Null(report.Find("V22")!.Value);
        Assert.Equal(
            "container c; Kubernetes replaces $(V21) with its value; expanding it would take the values expanded for this container past 10,000,000 characters, so it is not worked out here",
            report.Find("V22")!.Steps[^1].Note);
        Assert.Null(report.Find("V23")!.Value);
        Assert.Equal("container c; the value of $(V22) is not known here, so this value is not known either", report.Find("V23")!.Steps[^1].Note);
        Assert.Null(report.Find("V39")!.Value);
        Assert.True(report.Find("Last")!.IsSensitive);
    }

    [Fact]
    public void Expands_values_up_to_the_limit()
    {
        // B and C repeat A until they hold exactly the limit of 10,000,000 characters together; D would add one more copy of A.
        const int Half = 5_000_000;
        var copies = Half / 1000;
        var half = string.Concat(Enumerable.Repeat("$(A)", copies));
        var manifests = KubernetesManifests.Parse($$"""
            kind: Pod
            metadata: { name: p }
            spec:
              containers:
                - name: c
                  env:
                    - name: A
                      value: {{new string('x', 1000)}}
                    - name: B
                      value: "{{half}}"
                    - name: C
                      value: "{{half}}"
                    - name: D
                      value: "$(A)"
                    - name: E
                      value: plain
            """);

        var report = ConfigTracer.ForContainer(manifests).Trace();

        Assert.Equal(Half, report.Find("B")!.Value!.Length);
        Assert.Equal(Half, report.Find("C")!.Value!.Length);
        Assert.Null(report.Find("D")!.Value);
        Assert.Equal("plain", report.Find("E")!.Value);
    }

    [Fact]
    public void A_value_that_refers_to_a_secret_that_was_not_loaded_is_redacted()
    {
        var manifests = KubernetesManifests.Parse("""
            kind: Pod
            metadata: { name: p }
            spec:
              containers:
                - name: c
                  envFrom:
                    - secretRef: { name: unread-env }
                  env:
                    - name: TOKEN
                      valueFrom: { secretKeyRef: { name: unread, key: token } }
                    - name: Api__Url
                      value: "https://api.example.test/?t=$(TOKEN)"
                    - name: Api__Other
                      value: "$(FROM_UNREAD)"
            """);

        var report = ConfigTracer.ForContainer(manifests).Trace();

        Assert.True(report.Find("Api__Url")!.IsSensitive);
        Assert.Equal("Secret unread", Assert.Single(Assert.Single(report.Find("Api__Url")!.Steps).Origins).Source);
        Assert.True(report.Find("Api__Other")!.IsSensitive);
        var guess = Assert.Single(Assert.Single(report.Find("Api__Other")!.Steps).Origins);
        Assert.Equal("Secret unread-env", guess.Source);
        Assert.True(guess.IsInferred);
        Assert.Null(guess.Location);

        // Only the unloaded Secret may set FROM_UNREAD, so the name may be part of a secret value: it is not repeated.
        Assert.DoesNotContain("FROM_UNREAD", Everything(report), StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_refers_to_a_variable_with_a_secret_name_is_redacted()
    {
        var manifests = KubernetesManifests.Parse("""
            kind: Pod
            metadata: { name: p }
            spec:
              containers:
                - name: c
                  env:
                    - name: DB_PASSWORD
                      value: not-a-real-password
                    - name: Database__Url
                      value: "postgres://app:$(DB_PASSWORD)@db"
            """);

        var report = ConfigTracer.ForContainer(manifests).Trace();

        Assert.True(report.Find("Database__Url")!.IsSensitive);
        Assert.DoesNotContain("not-a-real-password", Everything(report), StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_refers_to_a_secret_name_only_an_unloaded_configmap_may_set_is_redacted()
    {
        // In the pod DB_PASSWORD may hold a real password, so the value is treated as secret, without naming the reference.
        var manifests = KubernetesManifests.Parse("""
            kind: Pod
            metadata: { name: p }
            spec:
              containers:
                - name: c
                  envFrom:
                    - configMapRef: { name: unread }
                  env:
                    - name: CONN
                      value: "pw=$(DB_PASSWORD)"
            """);

        var report = ConfigTracer.ForContainer(manifests).Trace();

        var conn = report.Find("CONN")!;
        var guess = Assert.Single(Assert.Single(conn.Steps).Origins);
        Assert.True(conn.IsSensitive);
        Assert.Equal("ConfigMap unread", guess.Source);
        Assert.True(guess.IsSensitive);
        Assert.DoesNotContain("DB_PASSWORD", Everything(report), StringComparison.Ordinal);
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
