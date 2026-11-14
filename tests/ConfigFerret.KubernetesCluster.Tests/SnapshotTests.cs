using ConfigFerret.Kubernetes;

namespace ConfigFerret.KubernetesCluster.Tests;

public class SnapshotTests
{
    private const string Yaml = """
        apiVersion: v1
        kind: Pod
        metadata: { name: p }
        spec: { containers: [{ name: c }] }
        ---
        apiVersion: v1
        kind: ConfigMap
        metadata: { name: b }
        data: { z: "1", a: "2" }
        ---
        apiVersion: v1
        kind: ConfigMap
        metadata: { name: a }
        data: { k: "3" }
        ---
        apiVersion: v1
        kind: Secret
        metadata: { name: s2 }
        stringData: { y: "x", b: "x" }
        ---
        apiVersion: v1
        kind: Secret
        metadata: { name: s1 }
        stringData: { k: "x" }
        ---
        apiVersion: secrets-store.csi.x-k8s.io/v1
        kind: SecretProviderClass
        metadata: { name: z }
        spec: { secretObjects: [{ secretName: sz, data: [{ objectName: o, key: k }] }] }
        ---
        apiVersion: secrets-store.csi.x-k8s.io/v1
        kind: SecretProviderClass
        metadata: { name: y }
        spec: { secretObjects: [{ secretName: sy, data: [{ objectName: o, key: k }] }] }
        """;

    private static KubernetesClusterSnapshot Snapshot(string? context = null, params string[] warnings)
    {
        var manifests = KubernetesManifests.Parse(Yaml, "x");
        return new KubernetesClusterSnapshot(manifests, "ns", context, manifests.Workloads[0], null, includesSecrets: true, warnings);
    }

    [Fact]
    public void In_cluster_snapshots_name_no_context()
    {
        var snapshot = Snapshot();

        Assert.Equal("Kubernetes Pod/p (in-cluster, ns)", snapshot.DisplayName);
        Assert.Null(snapshot.Context);
        Assert.All(snapshot.Entries, e => Assert.Equal("ns", e.Location));
    }

    [Fact]
    public void Entries_are_sorted_by_object_then_key()
    {
        Assert.Equal(
            ["a/k", "b/a", "b/z", "s1/k", "s2/b", "s2/y", "sy/k", "sz/k"],
            Snapshot("c").Entries.Select(e => e.Reference switch
            {
                ConfigMapKeyRef c => $"{c.Name}/{c.Key}",
                SecretKeyRef s => $"{s.Name}/{s.Key}",
                _ => "?",
            }));
    }

    [Fact]
    public void The_resolver_adds_the_load_warnings_once_and_needs_diagnostics()
    {
        var resolver = Snapshot("c", "first", "second").CreateResolver();
        var diagnostics = new TraceDiagnostics();
        var layer = new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "x", Reference = new EnvironmentVariableRef("NOPE") };

        Assert.Empty(resolver.Resolve(layer, diagnostics));
        Assert.Empty(resolver.Resolve(layer, diagnostics));
        Assert.Equal(["first", "second"], diagnostics.Warnings);
        Assert.Throws<ArgumentNullException>(() => Snapshot().CreateResolver().Resolve(layer, null!));
    }
}
