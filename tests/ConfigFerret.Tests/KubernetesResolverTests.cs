using ConfigFerret.Kubernetes;

namespace ConfigFerret.Tests;

public class KubernetesResolverTests
{
    private const string Yaml = """
        apiVersion: v1
        kind: ConfigMap
        metadata: { name: cfg }
        data:
          key: k
          other: o
        ---
        apiVersion: v1
        kind: Secret
        metadata: { name: sec }
        stringData:
          PW: pw
        ---
        apiVersion: apps/v1
        kind: Deployment
        metadata: { name: api }
        spec:
          template:
            spec:
              containers:
                - name: main
                  envFrom:
                    - secretRef: { name: unloaded }
                      prefix: S_
                  env:
                    - name: LIT
                      value: "a $(B)"
                    - name: PLAIN
                      value: "p"
                    - name: CMK
                      valueFrom: { configMapKeyRef: { name: cfg, key: key } }
                    - name: CMMISS
                      valueFrom: { configMapKeyRef: { name: cfg, key: nope } }
                    - name: CMUNL
                      valueFrom: { configMapKeyRef: { name: unknown, key: k } }
                    - name: SK
                      valueFrom: { secretKeyRef: { name: sec, key: PW } }
                  volumeMounts:
                    - { name: cfgvol, mountPath: /etc/cfg/ }
                    - { name: cfgvol, mountPath: /etc/one/file.txt, subPath: key }
                    - { name: secvol, mountPath: /etc/sec }
                    - { name: novol, mountPath: /etc/none }
                - name: sidecar
              volumes:
                - name: cfgvol
                  configMap: { name: cfg }
                - name: secvol
                  secret:
                    secretName: sec
                    items: [{ key: PW, path: pw.txt }]
        ---
        apiVersion: v1
        kind: Pod
        metadata: { name: other }
        spec:
          containers: [{ name: c }]
        """;

    private static readonly KubernetesManifests Manifests = KubernetesManifests.Parse(Yaml, "m.yaml");

    private static ProvenanceStep Env(string name) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = "environment variable " + name,
        Reference = new EnvironmentVariableRef(name),
    };

    private static ProvenanceStep File(string path) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = path,
        Reference = new FileRef(path),
    };

    private static IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep step, string? workload = "api", string? container = "main") =>
        new KubernetesResolver(Manifests, workload, container).Resolve(step, new TraceDiagnostics());

    [Fact]
    public void Describes_a_literal_env_entry_and_flags_variable_references()
    {
        var lit = Assert.Single(Resolve(Env("LIT")));
        var plain = Assert.Single(Resolve(Env("PLAIN")));

        Assert.Equal(LayerKind.KubernetesWorkload, lit.Kind);
        Assert.Equal("Deployment/api env LIT", lit.Source);
        Assert.Equal("a $(B)", lit.Value);
        Assert.Equal("container main; Kubernetes expands $(VAR) references in this value", lit.Note);
        Assert.Equal(new WorkloadEnvRef("api", "main", "LIT"), lit.Reference);
        Assert.Matches(@"^m\.yaml:\d+$", lit.Location);
        Assert.Equal("container main", plain.Note);
    }

    [Fact]
    public void Follows_configmap_key_refs_and_says_why_a_value_is_missing()
    {
        var found = Assert.Single(Resolve(Env("CMK")));
        var missing = Assert.Single(Resolve(Env("CMMISS")));
        var unloaded = Assert.Single(Resolve(Env("CMUNL")));

        Assert.Equal(LayerKind.KubernetesConfigMap, found.Kind);
        Assert.Equal("ConfigMap cfg", found.Source);
        Assert.Equal("k", found.Value);
        Assert.False(found.IsSensitive);
        Assert.Matches(@"^key key, m\.yaml:\d+$", found.Location);
        Assert.Equal("configMapKeyRef on Deployment/api, container main", found.Note);
        Assert.Equal(new ConfigMapKeyRef("cfg", "key"), found.Reference);

        Assert.Equal("ConfigMap cfg", missing.Source);
        Assert.Equal("key nope", missing.Location);
        Assert.Null(missing.Value);
        Assert.Equal("configMapKeyRef on Deployment/api, container main; the key is missing from it", missing.Note);
        Assert.Equal(new ConfigMapKeyRef("cfg", "nope"), missing.Reference);

        Assert.Equal(LayerKind.KubernetesConfigMap, unloaded.Kind);
        Assert.Equal("ConfigMap unknown", unloaded.Source);
        Assert.False(unloaded.IsSensitive);
        Assert.Equal("configMapKeyRef on Deployment/api, container main; its contents were not loaded", unloaded.Note);
    }

    [Fact]
    public void Follows_secret_key_refs()
    {
        var step = Assert.Single(Resolve(Env("SK")));

        Assert.Equal(LayerKind.KubernetesSecret, step.Kind);
        Assert.Equal("Secret sec", step.Source);
        Assert.True(step.IsSensitive);
        Assert.Equal("secretKeyRef on Deployment/api, container main", step.Note);
        Assert.Equal(new SecretKeyRef("sec", "PW"), step.Reference);
    }

    [Fact]
    public void Guesses_an_unloaded_secret_envFrom_with_its_prefix()
    {
        var step = Assert.Single(Resolve(Env("S_TOKEN")));

        Assert.Equal(LayerKind.KubernetesSecret, step.Kind);
        Assert.Equal("Secret unloaded", step.Source);
        Assert.Equal("key TOKEN", step.Location);
        Assert.True(step.IsInferred);
        Assert.True(step.IsSensitive);
        Assert.Equal("envFrom on Deployment/api, container main; its contents were not loaded, so this is a guess", step.Note);
        Assert.Equal(new SecretKeyRef("unloaded", "TOKEN"), step.Reference);
        Assert.Empty(Resolve(Env("NOT_PREFIXED")));
    }

    [Fact]
    public void Links_files_in_configmap_secret_and_subPath_mounts()
    {
        var dir = Assert.Single(Resolve(File("/etc/cfg/other")));
        var sub = Assert.Single(Resolve(File("/etc/one/file.txt")));
        var secret = Assert.Single(Resolve(File(@"\etc\sec\pw.txt")));

        Assert.Equal("ConfigMap cfg", dir.Source);
        Assert.Equal("key other", dir.Location);
        Assert.Equal(LayerKind.KubernetesConfigMap, dir.Kind);
        Assert.Equal("mounted at /etc/cfg/ by volume cfgvol on Deployment/api, container main", dir.Note);
        Assert.Equal(new ConfigMapKeyRef("cfg", "other"), dir.Reference);
        Assert.False(dir.IsSensitive);

        Assert.Equal("key key", sub.Location);

        Assert.Equal(LayerKind.KubernetesSecret, secret.Kind);
        Assert.Equal("Secret sec", secret.Source);
        Assert.Equal("key PW", secret.Location);
        Assert.True(secret.IsSensitive);
        Assert.Equal(new SecretKeyRef("sec", "PW"), secret.Reference);
    }

    [Theory]
    [InlineData("/etc/cfg/nested/other")]
    [InlineData("/etc/cfgother")]
    [InlineData("/etc/one/other.txt")]
    [InlineData("/etc/sec/unlisted.txt")]
    [InlineData("/etc/none/x")]
    [InlineData("/elsewhere/x")]
    public void Ignores_files_no_mount_provides(string path) =>
        Assert.Empty(Resolve(File(path)));

    [Fact]
    public void Ignores_layers_that_are_not_dotnet_providers_or_have_other_references()
    {
        Assert.Empty(Resolve(Env("LIT") with { Kind = LayerKind.Helm }));
        Assert.Empty(Resolve(Env("LIT") with { Reference = new HelmValueRef("x") }));
    }

    [Fact]
    public void Warns_when_it_has_to_pick_a_workload_or_container()
    {
        var diagnostics = new TraceDiagnostics();
        var resolver = new KubernetesResolver(Manifests, null, null);

        Assert.Single(resolver.Resolve(Env("LIT"), diagnostics));
        Assert.Single(resolver.Resolve(Env("LIT"), diagnostics));

        Assert.Equal(
            [
                "The manifests hold 2 workloads; using Deployment/api. Pass a workload name to pick another.",
                "Deployment/api has 2 containers; using 'main'. Pass a container name to pick another.",
            ],
            diagnostics.Warnings);
    }

    [Fact]
    public void Picks_the_app_container_over_init_containers_without_a_warning()
    {
        var manifests = KubernetesManifests.Parse("""
            kind: Deployment
            metadata: { name: api }
            spec:
              template:
                spec:
                  initContainers:
                    - name: migrate
                      env: [{ name: X, value: init }]
                  containers:
                    - name: app
                      env: [{ name: X, value: app }]
            """);
        var diagnostics = new TraceDiagnostics();

        var step = Assert.Single(new KubernetesResolver(manifests, null, null).Resolve(Env("X"), diagnostics));

        Assert.Equal("app", step.Value);
        Assert.Empty(diagnostics.Warnings);
        Assert.Equal([false, true], manifests.Workloads[0].Containers.Select(c => c.IsInitContainer));
    }

    [Fact]
    public void Uses_an_init_container_when_named_or_when_it_is_the_only_one()
    {
        var manifests = KubernetesManifests.Parse("""
            kind: Job
            metadata: { name: once }
            spec:
              template:
                spec:
                  initContainers:
                    - name: setup
                      env: [{ name: X, value: init }]
            """);

        Assert.Equal("init", Assert.Single(new KubernetesResolver(manifests, null, null).Resolve(Env("X"), new TraceDiagnostics())).Value);
        Assert.Equal("init", Assert.Single(new KubernetesResolver(manifests, null, "setup").Resolve(Env("X"), new TraceDiagnostics())).Value);
    }

    [Fact]
    public void Warns_when_the_workload_has_no_containers()
    {
        var manifests = KubernetesManifests.Parse("kind: Pod\nmetadata: { name: empty }\nspec: {}\n");
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(new KubernetesResolver(manifests, null, null).Resolve(Env("X"), diagnostics));
        Assert.Equal(["Pod/empty has no containers."], diagnostics.Warnings);
    }

    [Theory]
    [InlineData("nope", null, "No Kubernetes workload named 'nope' found in the manifests.")]
    [InlineData("api", "nope", "No container named 'nope' in Deployment/api.")]
    public void Warns_and_resolves_nothing_when_the_target_is_missing(string workload, string? container, string warning)
    {
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(new KubernetesResolver(Manifests, workload, container).Resolve(Env("LIT"), diagnostics));
        Assert.Equal([warning], diagnostics.Warnings);
    }

    [Fact]
    public void Warns_when_the_manifests_hold_no_workload()
    {
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(new KubernetesResolver(KubernetesManifests.Parse("kind: ConfigMap\nmetadata: { name: x }\n"), null, null).Resolve(Env("A"), diagnostics));
        Assert.Equal(["No Kubernetes workload found in the manifests."], diagnostics.Warnings);
    }
}
