using ConfigFerret.Kubernetes;

namespace ConfigFerret.Tests;

public class ExternalSecretsTests
{
    private const string Pod = """
        apiVersion: v1
        kind: Pod
        metadata: { name: api }
        spec:
          containers:
            - name: api
              envFrom:
                - secretRef: { name: from-all }
              env:
                - name: PW
                  valueFrom: { secretKeyRef: { name: db, key: password } }
                - name: MISSING
                  valueFrom: { secretKeyRef: { name: db, key: nope } }
              volumeMounts:
                - { name: kv, mountPath: /mnt/kv }
                - { name: sec, mountPath: /etc/sec }
                - { name: other, mountPath: /mnt/other }
          volumes:
            - name: kv
              csi:
                driver: secrets-store.csi.k8s.io
                volumeAttributes: { secretProviderClass: azure-kv }
            - name: sec
              secret: { secretName: db }
            - name: other
              csi:
                driver: secrets-store.csi.k8s.io
                volumeAttributes: { secretProviderClass: not-loaded }
        """;

    private const string Eso = """
        apiVersion: external-secrets.io/v1
        kind: SecretStore
        metadata: { name: kv }
        spec:
          provider:
            azurekv: { vaultUrl: "https://contoso.vault.azure.net" }
        ---
        apiVersion: external-secrets.io/v1beta1
        kind: ExternalSecret
        metadata: { name: db }
        spec:
          secretStoreRef: { name: kv }
          data:
            - secretKey: password
              remoteRef: { key: db-password, version: v2 }
        ---
        apiVersion: external-secrets.io/v1
        kind: ExternalSecret
        metadata: { name: all }
        spec:
          secretStoreRef: { name: kv, kind: ClusterSecretStore }
          target: { name: from-all }
          dataFrom:
            - extract: { key: app-config }
            - find: { name: { regexp: "^app-" } }
        """;

    private const string Csi = """
        apiVersion: secrets-store.csi.x-k8s.io/v1
        kind: SecretProviderClass
        metadata: { name: azure-kv }
        spec:
          provider: azure
          parameters:
            keyvaultName: contoso
            objects: |
              array:
                - |
                  objectName: api-key
                  objectType: secret
                  objectVersion: abc123
                - |
                  objectName: tls
                  objectAlias: tls.pem
                  objectType: cert
          secretObjects:
            - secretName: synced
              type: Opaque
              data:
                - { objectName: api-key, key: API_KEY }
                - { objectName: unknown, key: OTHER }
        """;

    private static KubernetesManifests Manifests(params string[] docs) => KubernetesManifests.Parse(string.Join("\n---\n", docs), "m.yaml");

    private static ProvenanceStep SecretLayer(string name, string key) => new()
    {
        Kind = LayerKind.KubernetesSecret,
        Source = "Secret " + name,
        Reference = new SecretKeyRef(name, key),
    };

    private static ProvenanceStep Env(string name) => new() { Kind = LayerKind.DotNetProvider, Source = name, Reference = new EnvironmentVariableRef(name) };

    private static ProvenanceStep File(string path) => new() { Kind = LayerKind.DotNetProvider, Source = path, Reference = new FileRef(path) };

    [Fact]
    public void Parses_ExternalSecrets_SecretStores_and_SecretProviderClasses()
    {
        var m = Manifests(Eso, Csi);

        Assert.Equal(2, m.ExternalSecrets.Count);
        var db = m.ExternalSecrets[0];
        Assert.Equal("db", db.TargetSecret);
        Assert.Equal(new SecretStoreReference("SecretStore", "kv"), db.Store);
        Assert.Equal(new ExternalSecretData("password", "db-password", null, "v2", null, 14), db.Data.Single());
        Assert.False(db.HasTemplate);
        var all = m.ExternalSecrets[1];
        Assert.Equal("from-all", all.TargetSecret);
        Assert.Equal(["app-config", null], all.DataFrom.Select(d => d.RemoteKey));

        Assert.Equal(new SecretStore("SecretStore", "kv", "azurekv", "m.yaml"), Assert.Single(m.SecretStores));

        var spc = m.SecretProviderClasses["azure-kv"];
        Assert.Equal("azurekv", spc.Provider);
        Assert.Equal(
            [new SecretProviderObject("api-key", "api-key", null, "abc123"), new SecretProviderObject("tls.pem", "tls", null, null)],
            spc.Objects);
        var synced = Assert.Single(spc.SecretObjects);
        Assert.Equal("synced", synced.SecretName);
        Assert.Equal(["API_KEY", "OTHER"], synced.Keys.Select(k => k.Key));
    }

    [Fact]
    public void Ignores_objects_from_other_api_groups_and_reads_lists()
    {
        var m = Manifests("""
            apiVersion: kubernetes-client.io/v1
            kind: ExternalSecret
            metadata: { name: old }
            spec: { data: [{ secretKey: a, remoteRef: { key: b } }] }
            ---
            apiVersion: external-secrets.io/v1
            kind: PushSecret
            metadata: { name: push }
            ---
            apiVersion: secrets-store.csi.x-k8s.io/v1
            kind: Other
            metadata: { name: other }
            ---
            apiVersion: v1
            kind: List
            items:
              - apiVersion: external-secrets.io/v1
                kind: ExternalSecretList
                items:
                  - apiVersion: external-secrets.io/v1
                    kind: ExternalSecret
                    metadata: { name: listed }
                    spec: { data: [{ secretKey: k, remoteRef: { key: r } }, { remoteRef: { key: no-secret-key } }] }
              - apiVersion: secrets-store.csi.x-k8s.io/v1
                kind: SecretProviderClassList
                items:
                  - apiVersion: secrets-store.csi.x-k8s.io/v1
                    kind: SecretProviderClass
                    metadata: { name: spc }
                    spec: { provider: gcp }
            """);

        var es = Assert.Single(m.ExternalSecrets);
        Assert.Equal("listed", es.Name);
        Assert.Equal(["k"], es.Data.Select(d => d.SecretKey));
        Assert.Equal("gcpsm", Assert.Single(m.SecretProviderClasses).Value.Provider);
    }

    [Fact]
    public void A_SecretStore_loaded_twice_keeps_the_last_one()
    {
        var m = Manifests(Eso, """
            apiVersion: external-secrets.io/v1
            kind: SecretStore
            metadata: { name: kv }
            spec: { provider: { vault: {} } }
            ---
            apiVersion: external-secrets.io/v1
            kind: ClusterSecretStore
            metadata: { name: kv }
            spec: { provider: { gcpsm: {} } }
            """);

        Assert.Equal(["SecretStore/vault", "ClusterSecretStore/gcpsm"], m.SecretStores.Select(s => $"{s.Kind}/{s.Provider}"));
    }

    [Fact]
    public void Secret_key_links_to_its_ExternalSecret_with_the_store_provider()
    {
        var resolver = new KubernetesResolver(Manifests(Eso), null, null);
        var origin = Assert.Single(resolver.Resolve(SecretLayer("db", "password"), new TraceDiagnostics()));

        Assert.Equal(LayerKind.Other, origin.Kind);
        Assert.Equal("ExternalSecret db", origin.Source);
        Assert.Equal("remoteRef db-password, version v2, m.yaml:14", origin.Location);
        Assert.True(origin.IsSensitive);
        Assert.False(origin.IsInferred);
        Assert.Null(origin.Value);
        Assert.Equal("External Secrets Operator, SecretStore kv (azurekv) writes Secret db key password", origin.Note);
        Assert.Equal(new ExternalSecretRef("azurekv", "kv", "db-password", null, "v2"), origin.Reference);
    }

    [Fact]
    public void Store_kind_must_match_so_a_ClusterSecretStore_reference_has_no_provider()
    {
        var resolver = new KubernetesResolver(Manifests(Eso), null, null);
        var origin = Assert.Single(resolver.Resolve(SecretLayer("from-all", "anything"), new TraceDiagnostics()));

        Assert.Equal(new ExternalSecretRef(null, "kv", "app-config"), origin.Reference);
        Assert.True(origin.IsInferred);
        Assert.Contains("ClusterSecretStore kv (not loaded)", origin.Note, StringComparison.Ordinal);
        Assert.Contains("probably comes from it", origin.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Item_sourceRef_overrides_the_store_and_template_is_noted()
    {
        var m = Manifests(Eso, """
            apiVersion: external-secrets.io/v1
            kind: ExternalSecret
            metadata: { name: tpl }
            spec:
              secretStoreRef: { name: missing }
              target:
                name: tpl-secret
                template: { data: { url: "postgres://{{ .user }}" } }
              data:
                - secretKey: user
                  sourceRef: { storeRef: { name: kv } }
                  remoteRef: { key: db-user, property: name }
                - secretKey: nokey
                  remoteRef: {}
            """);
        var resolver = new KubernetesResolver(m, null, null);

        var origin = Assert.Single(resolver.Resolve(SecretLayer("tpl-secret", "user"), new TraceDiagnostics()));
        Assert.Equal(new ExternalSecretRef("azurekv", "kv", "db-user", "name"), origin.Reference);
        Assert.EndsWith("; its target template may change the key or the value", origin.Note, StringComparison.Ordinal);
        Assert.Empty(resolver.Resolve(SecretLayer("tpl-secret", "nokey"), new TraceDiagnostics()));
        Assert.True(m.ExternalSecrets[^1].HasTemplate);
    }

    [Fact]
    public void Only_secret_layers_with_a_secret_reference_get_origins()
    {
        var resolver = new KubernetesResolver(Manifests(Eso), null, null);
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(resolver.Resolve(SecretLayer("db", "other"), diagnostics));
        Assert.Empty(resolver.Resolve(SecretLayer("unknown", "password"), diagnostics));
        Assert.Empty(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "c", Reference = new SecretKeyRef("db", "password") }, diagnostics));
        Assert.Empty(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "c", Reference = new ConfigMapKeyRef("db", "password") }, diagnostics));
    }

    [Fact]
    public void A_synthesized_Secret_key_has_no_value_and_says_what_creates_it()
    {
        var resolver = new KubernetesResolver(Manifests(Pod, Eso, Csi), null, null);
        var diagnostics = new TraceDiagnostics();

        var step = Assert.Single(resolver.Resolve(Env("PW"), diagnostics));
        Assert.Equal(LayerKind.KubernetesSecret, step.Kind);
        Assert.Null(step.Value);
        Assert.True(step.IsSensitive);
        Assert.StartsWith("key password, m.yaml:", step.Location, StringComparison.Ordinal);
        Assert.EndsWith("; the Secret is not in the manifests but ExternalSecret db creates it, so its value is not known here", step.Note, StringComparison.Ordinal);

        var missing = Assert.Single(resolver.Resolve(Env("MISSING"), diagnostics));
        Assert.EndsWith("; the key is missing from it", missing.Note, StringComparison.Ordinal);

        // dataFrom adds unknown keys: any name could come from it, so it is a guess.
        var guess = Assert.Single(resolver.Resolve(Env("ANY"), diagnostics));
        Assert.True(guess.IsInferred);
        Assert.Equal(new SecretKeyRef("from-all", "ANY"), guess.Reference);
        Assert.Equal("envFrom on Pod/api, container api; ExternalSecret all creates the Secret and adds keys that are only known once the store is read, so this is a guess", guess.Note);
    }

    [Fact]
    public void A_real_Secret_keeps_its_value_and_gains_the_ExternalSecret_origin()
    {
        var report = ConfigTracer.ForContainer(Manifests(Pod, Eso, """
                apiVersion: v1
                kind: Secret
                metadata: { name: db }
                stringData: { password: hunter2 }
                """))
            .WithOptions(o => o.RevealSensitiveValues = true)
            .Trace();

        var step = report.Find("PW")!.EffectiveStep!;
        Assert.Equal("hunter2", step.Value);
        Assert.Equal("secretKeyRef on Pod/api, container api", step.Note);
        Assert.Equal(new ExternalSecretRef("azurekv", "kv", "db-password", null, "v2"), Assert.Single(step.Origins).Reference);
    }

    [Fact]
    public void Secret_values_stay_redacted_through_the_ExternalSecret_origin()
    {
        var report = ConfigTracer.ForContainer(Manifests(Pod, Eso, """
                apiVersion: v1
                kind: Secret
                metadata: { name: db }
                stringData: { password: hunter2 }
                """)).Trace();

        Assert.DoesNotContain("hunter2", report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", report.ToJson(), StringComparison.Ordinal);
        Assert.True(report.Find("PW")!.IsSensitive);
    }

    [Fact]
    public void Store_resolvers_attach_under_the_ExternalSecretRef_step()
    {
        var store = new FakeStore();
        var report = ConfigTracer.ForContainer(Manifests(Pod, Eso)).WithResolver(store).Trace();

        var origin = Assert.Single(report.Find("PW")!.EffectiveStep!.Origins);
        Assert.Equal("Azure Key Vault secret db-password", Assert.Single(origin.Origins).Source);

    }

    [Fact]
    public void Csi_files_link_to_the_SecretProviderClass_object()
    {
        var resolver = new KubernetesResolver(Manifests(Pod, Eso, Csi), null, null);
        var diagnostics = new TraceDiagnostics();

        var step = Assert.Single(resolver.Resolve(File("/mnt/kv/api-key"), diagnostics));
        Assert.Equal(LayerKind.Other, step.Kind);
        Assert.Equal("SecretProviderClass azure-kv", step.Source);
        Assert.True(step.IsSensitive);
        Assert.Equal(new ExternalSecretRef("azurekv", "azure-kv", "api-key", null, "abc123"), step.Reference);
        Assert.Equal("Secrets Store CSI driver, provider azurekv; mounted at /mnt/kv by CSI volume kv on Pod/api, container api", step.Note);

        Assert.Equal("tls", ((ExternalSecretRef)Assert.Single(resolver.Resolve(File("/mnt/kv/tls.pem"), diagnostics)).Reference!).RemoteKey);
        Assert.Empty(resolver.Resolve(File("/mnt/kv/nope"), diagnostics));

        var unloaded = Assert.Single(resolver.Resolve(File("/mnt/other/x"), diagnostics));
        Assert.True(unloaded.IsInferred);
        Assert.Null(unloaded.Reference);
        Assert.EndsWith("; the SecretProviderClass was not loaded", unloaded.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Synced_CSI_Secret_keys_link_to_their_objects()
    {
        var resolver = new KubernetesResolver(Manifests(Csi), null, null);
        var origin = Assert.Single(resolver.Resolve(SecretLayer("synced", "API_KEY"), new TraceDiagnostics()));

        Assert.Equal(new ExternalSecretRef("azurekv", "azure-kv", "api-key", null, "abc123"), origin.Reference);
        Assert.EndsWith("; synced into Secret synced key API_KEY while a pod mounts the volume", origin.Note, StringComparison.Ordinal);
        Assert.Empty(resolver.Resolve(SecretLayer("synced", "OTHER"), new TraceDiagnostics()));
    }

    // A pod, an ExternalSecret with its SecretStore and a SecretProviderClass, with the same names in each namespace.
    private static string InNamespace(string ns, string provider) => $$"""
        apiVersion: v1
        kind: Pod
        metadata: { name: api, namespace: {{ns}} }
        spec:
          containers:
            - name: api
              env:
                - name: PW
                  valueFrom: { secretKeyRef: { name: db, key: password } }
              volumeMounts:
                - { name: kv, mountPath: /mnt/kv }
          volumes:
            - name: kv
              csi:
                driver: secrets-store.csi.k8s.io
                volumeAttributes: { secretProviderClass: kv-files }
        ---
        apiVersion: external-secrets.io/v1
        kind: SecretStore
        metadata: { name: kv, namespace: {{ns}} }
        spec:
          provider:
            {{provider}}: {}
        ---
        apiVersion: external-secrets.io/v1
        kind: ExternalSecret
        metadata: { name: db, namespace: {{ns}} }
        spec:
          secretStoreRef: { name: kv }
          data:
            - secretKey: password
              remoteRef: { key: {{ns}}-db-password }
        ---
        apiVersion: secrets-store.csi.x-k8s.io/v1
        kind: SecretProviderClass
        metadata: { name: kv-files, namespace: {{ns}} }
        spec:
          provider: aws
          parameters:
            objects: |
              - objectName: {{ns}}-api-key
                objectAlias: api-key
        """;

    [Theory]
    [InlineData("api", "azurekv", "prod")]
    [InlineData("prod/api", "azurekv", "prod")]
    [InlineData("staging/api", "vault", "staging")]
    public void A_workload_reads_the_operator_objects_of_its_own_namespace(string workload, string provider, string ns)
    {
        var report = ConfigTracer.ForContainer(Manifests(InNamespace("prod", "azurekv"), InNamespace("staging", "vault")), workload).Trace();

        var origins = report.Find("PW")?.EffectiveStep?.Origins ?? [];
        Assert.Equal<SourceRef?>([new ExternalSecretRef(provider, "kv", $"{ns}-db-password")], origins.Select(o => o.Reference));
        Assert.Equal(new ExternalSecretRef("aws", "kv-files", $"{ns}-api-key", null, null), report.Find("/mnt/kv/api-key")?.EffectiveStep?.Reference);
    }

    [Fact]
    public void A_SecretProviderClass_named_like_another_namespace_s_does_not_take_its_place()
    {
        static string Spc(string name, string? ns, string remoteKey) => $$"""
            apiVersion: secrets-store.csi.x-k8s.io/v1
            kind: SecretProviderClass
            metadata: { name: "{{name}}"{{(ns is null ? "" : $", namespace: {ns}")}} }
            spec:
              provider: aws
              parameters:
                objects: |
                  - objectName: {{remoteKey}}
                    objectAlias: api-key
              secretObjects:
                - secretName: synced
                  data: [{ objectName: api-key, key: API_KEY }]
            """;
        var manifests = Manifests(
            """
            apiVersion: v1
            kind: Pod
            metadata: { name: api, namespace: prod }
            spec:
              containers:
                - name: api
                  env:
                    - name: K
                      valueFrom: { secretKeyRef: { name: synced, key: API_KEY } }
                  volumeMounts:
                    - { name: kv, mountPath: /mnt/kv }
              volumes:
                - name: kv
                  csi:
                    driver: secrets-store.csi.k8s.io
                    volumeAttributes: { secretProviderClass: kv }
            """,
            Spc("kv", "prod", "prod-api-key"),
            Spc("kv", "staging", "staging-api-key"),
            Spc("prod/kv", null, "posing-api-key"));

        var report = ConfigTracer.ForContainer(manifests, "prod/api").Trace();

        var key = report.Find("K")?.EffectiveStep;
        Assert.Contains("but SecretProviderClass kv creates it", key?.Note, StringComparison.Ordinal);
        Assert.Contains<SourceRef?>(new ExternalSecretRef("aws", "kv", "prod-api-key"), key?.Origins.Select(o => o.Reference) ?? []);
        Assert.DoesNotContain<SourceRef?>(new ExternalSecretRef("aws", "kv", "staging-api-key"), key?.Origins.Select(o => o.Reference) ?? []);
        Assert.Equal(new ExternalSecretRef("aws", "kv", "prod-api-key"), report.Find("/mnt/kv/api-key")?.EffectiveStep?.Reference);

        // A Kubernetes name cannot hold a slash, so the one named prod/kv is left out with a warning.
        Assert.DoesNotContain<SourceRef?>(new ExternalSecretRef("aws", "prod/kv", "posing-api-key"), key?.Origins.Select(o => o.Reference) ?? []);
        Assert.Equal(["kv"], manifests.SecretProviderClasses.Keys);
        Assert.Single(report.Warnings, w => w == "Skipped SecretProviderClass prod/kv in m.yaml: a Kubernetes name cannot hold a slash.");
    }

    [Fact]
    public void Container_trace_lists_CSI_files_and_synthesized_Secret_files_and_warns()
    {
        var report = ConfigTracer.ForContainer(Manifests(Pod, Eso, Csi)).Trace();

        var keys = report.Settings.Select(s => s.Key).ToList();
        Assert.Equal(["MISSING", "PW", "/etc/sec/password", "/mnt/kv/api-key", "/mnt/kv/tls.pem"], keys);

        var file = report.Find("/etc/sec/password")!.EffectiveStep!;
        Assert.Null(file.Value);
        Assert.Contains("ExternalSecret db creates it", file.Note, StringComparison.Ordinal);
        Assert.Single(file.Origins);

        var csi = report.Find("/mnt/kv/api-key")!.EffectiveStep!;
        Assert.Null(csi.Value);
        Assert.DoesNotContain("not loaded", csi.Note, StringComparison.Ordinal);

        Assert.Contains(report.Warnings, w => w.Contains("SecretProviderClass not-loaded", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.StartsWith("envFrom Secret from-all", StringComparison.Ordinal));
    }

    [Fact]
    public void Volume_of_a_Secret_with_unknown_keys_warns_and_links_any_file()
    {
        var m = Manifests("""
            apiVersion: v1
            kind: Pod
            metadata: { name: p }
            spec:
              containers: [{ name: c, volumeMounts: [{ name: v, mountPath: /s }] }]
              volumes:
                - name: v
                  projected:
                    sources:
                      - secret: { name: from-all }
                      - secret: { name: db }
            """, Eso);
        var diagnostics = new TraceDiagnostics();
        var resolver = new KubernetesResolver(m, null, null);

        Assert.Equal(["/s/password"], resolver.MountedFilePaths(diagnostics));
        Assert.Contains(diagnostics.Warnings, w => w.Contains("mounts Secret from-all, which ExternalSecret all fills", StringComparison.Ordinal));

        // A file not listed may still come from the dataFrom Secret, which is tried first.
        var step = Assert.Single(resolver.ResolveMountedFile("/s/anything", diagnostics));
        Assert.Equal(new SecretKeyRef("from-all", "anything"), step.Reference);
        Assert.EndsWith("; ExternalSecret all creates the Secret and adds keys that are only known once the store is read", step.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("aws", """
        - objectName: "prod/db"
          objectType: secretsmanager
          objectVersion: AWSCURRENT
          jmesPath:
            - { path: username, objectAlias: db-user }
            - { path: nothing }
        """, "prod/db|prod/db|-|AWSCURRENT;db-user|prod/db|username|AWSCURRENT")]
    [InlineData("vault", """
        - objectName: "pw"
          secretPath: "secret/data/db"
          secretKey: "password"
        """, "pw|secret/data/db|password|-")]
    [InlineData("azure", "array: [\"objectName: a\\nobjectAlias: b\", \"objectName: [\", \"{}\"]", "b|a|-|-")]
    [InlineData("azure", "not: [valid", "")]
    [InlineData("azure", "", "")]
    public void Reads_provider_objects(string provider, string objects, string expected)
    {
        var m = Manifests($$"""
            apiVersion: secrets-store.csi.x-k8s.io/v1
            kind: SecretProviderClass
            metadata: { name: s }
            spec:
              provider: {{provider}}
              parameters:
                objects: {{System.Text.Json.JsonSerializer.Serialize(objects)}}
            """);

        var actual = string.Join(";", m.SecretProviderClasses["s"].Objects.Select(o => $"{o.FileName}|{o.RemoteKey}|{o.Property ?? "-"}|{o.Version ?? "-"}"));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Reads_Google_Cloud_secrets_parameter()
    {
        var m = Manifests("""
            apiVersion: secrets-store.csi.x-k8s.io/v1
            kind: SecretProviderClass
            metadata: { name: g }
            spec:
              provider: gcp
              parameters:
                secrets: |
                  - resourceName: "projects/p/secrets/db/versions/latest"
                    fileName: "db.txt"
                  - resourceName: "projects/p/secrets/api/versions/2"
                    path: "api.txt"
            """);

        var spc = m.SecretProviderClasses["g"];
        Assert.Equal("gcpsm", spc.Provider);
        Assert.Equal(["db.txt", "api.txt"], spc.Objects.Select(o => o.FileName));
        Assert.Equal("projects/p/secrets/db/versions/latest", spc.Objects[0].RemoteKey);
        Assert.Null(KubernetesManifests.Parse("""
            apiVersion: secrets-store.csi.x-k8s.io/v1
            kind: SecretProviderClass
            metadata: { name: n }
            spec: {}
            """).SecretProviderClasses["n"].Provider);
    }

    [Fact]
    public void Adding_a_real_Secret_later_replaces_the_synthesized_one()
    {
        var m = Manifests(Pod, Eso);
        var resolver = new KubernetesResolver(m, null, null);
        Assert.Null(Assert.Single(resolver.Resolve(Env("PW"), new TraceDiagnostics())).Value);

        m.Add("""
            apiVersion: v1
            kind: Secret
            metadata: { name: db }
            stringData: { password: later }
            """, "later.yaml");

        Assert.Equal("later", Assert.Single(new KubernetesResolver(m, null, null).Resolve(Env("PW"), new TraceDiagnostics())).Value);
    }

    private sealed class FakeStore : IOriginResolver
    {
        public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics) =>
            layer.Reference is ExternalSecretRef { Provider: null or "azurekv" } e
                ? [new ProvenanceStep { Kind = LayerKind.AzureKeyVault, Source = "Azure Key Vault secret " + e.RemoteKey, IsSensitive = true }]
                : [];
    }
}
