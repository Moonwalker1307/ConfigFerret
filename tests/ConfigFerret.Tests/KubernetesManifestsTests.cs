using System.Text;
using ConfigFerret.Kubernetes;

namespace ConfigFerret.Tests;

public sealed class KubernetesManifestsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cf-manifests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("Deployment")]
    [InlineData("StatefulSet")]
    [InlineData("DaemonSet")]
    [InlineData("ReplicaSet")]
    [InlineData("Job")]
    [InlineData("ReplicationController")]
    public void Reads_every_pod_template_kind(string kind)
    {
        var manifests = KubernetesManifests.Parse($$"""
            kind: {{kind}}
            metadata: { name: w }
            spec: { template: { spec: { containers: [{ name: c }] } } }
            """);

        var workload = Assert.Single(manifests.Workloads);
        Assert.Equal($"{kind}/w", workload.DisplayName);
        Assert.Equal("c", Assert.Single(workload.Containers).Name);
    }

    [Theory]
    [InlineData("List")]
    [InlineData("PodList")]
    [InlineData("ConfigMapList")]
    [InlineData("SecretList")]
    [InlineData("DeploymentList")]
    public void Reads_items_of_list_kinds(string kind)
    {
        var manifests = KubernetesManifests.Parse($$$"""
            {"kind":"{{{kind}}}","items":[{"kind":"ConfigMap","metadata":{"name":"a"}}]}
            """);

        Assert.True(manifests.ConfigMaps.ContainsKey("a"));
    }

    [Fact]
    public void Skips_objects_without_kind_unknown_kinds_and_workloads_without_a_spec()
    {
        var manifests = KubernetesManifests.Parse("""
            metadata: { name: nokind }
            ---
            kind: Service
            metadata: { name: svc }
            spec: { template: { spec: { containers: [{ name: c }] } } }
            ---
            kind: Deployment
            metadata: { name: nospec }
            ---
            kind: ConfigMap
            """);

        Assert.Empty(manifests.Workloads);
        Assert.Equal(["(unnamed)"], manifests.ConfigMaps.Keys);
    }

    [Fact]
    public void Reads_containers_init_containers_and_every_env_source()
    {
        var manifests = KubernetesManifests.Parse("""
            kind: Pod
            metadata: { name: p }
            spec:
              initContainers: [{ name: init }]
              containers:
                - name: main
                  env:
                    - name: LIT
                      value: v
                    - name: EMPTY
                    - value: no-name
                    - name: CM
                      valueFrom: { configMapKeyRef: { name: cm, key: k } }
                    - name: CM_DEFAULT_KEY
                      valueFrom: { configMapKeyRef: { name: cm } }
                    - name: SK
                      valueFrom: { secretKeyRef: { name: s, key: k } }
                    - name: SK_DEFAULT_KEY
                      valueFrom: { secretKeyRef: { name: s } }
                    - name: FIELD
                      valueFrom: { fieldRef: { fieldPath: metadata.name } }
                    - name: RES
                      valueFrom: { resourceFieldRef: { resource: limits.cpu } }
                  envFrom:
                    - configMapRef: { name: cm }
                      prefix: P_
                    - secretRef: { name: s }
                    - {}
                  volumeMounts:
                    - { name: v, mountPath: /m, subPath: sp }
                    - { name: w, mountPath: /n }
                - {}
            """);

        var workload = Assert.Single(manifests.Workloads);
        Assert.Equal(["main", "(unnamed)", "init"], workload.Containers.Select(c => c.Name));
        var main = workload.Containers[0];

        Assert.Equal(
            [
                ("LIT", (EnvValueSource)new EnvValueSource.Literal("v")),
                ("EMPTY", new EnvValueSource.Literal("")),
                ("CM", new EnvValueSource.FromConfigMap("cm", "k")),
                ("CM_DEFAULT_KEY", new EnvValueSource.FromConfigMap("cm", "CM_DEFAULT_KEY")),
                ("SK", new EnvValueSource.FromSecret("s", "k")),
                ("SK_DEFAULT_KEY", new EnvValueSource.FromSecret("s", "SK_DEFAULT_KEY")),
                ("FIELD", new EnvValueSource.FromField("metadata.name")),
                ("RES", new EnvValueSource.FromField("limits.cpu")),
            ],
            main.Env.Select(e => (e.Name, e.Source)));
        Assert.Equal(8, main.Env[0].Line);
        Assert.Equal([new EnvFromEntry("cm", false, "P_"), new EnvFromEntry("s", true, "")], main.EnvFrom);
        Assert.Equal([new VolumeMount("v", "/m", "sp"), new VolumeMount("w", "/n", null)], main.VolumeMounts);
    }

    [Fact]
    public void Reads_configmap_secret_and_projected_volumes()
    {
        var manifests = KubernetesManifests.Parse("""
            kind: Pod
            metadata: { name: p }
            spec:
              containers: [{ name: c }]
              volumes:
                - name: a
                  configMap:
                    name: cm
                    items: [{ key: k1, path: p1 }, { key: k2 }, { path: nokey }]
                - name: b
                  secret: { secretName: s }
                - name: c
                  projected:
                    sources:
                      - configMap: { name: pcm }
                      - secret: { name: ps, items: [{ key: x, path: y }] }
                - emptyDir: {}
            """);

        var volumes = Assert.Single(manifests.Workloads).Volumes;
        Assert.Equal(["a", "b", "c", ""], volumes.Select(v => v.Name));

        var a = Assert.Single(volumes[0].Projections);
        Assert.Equal(("cm", false), (a.SourceName, a.IsSecret));
        Assert.Equal([new("k1", "p1"), new KeyValuePair<string, string>("k2", "k2")], a.Items);

        var b = Assert.Single(volumes[1].Projections);
        Assert.Equal(("s", true), (b.SourceName, b.IsSecret));
        Assert.Empty(b.Items);

        Assert.Equal([("pcm", false), ("ps", true)], volumes[2].Projections.Select(p => (p.SourceName, p.IsSecret)));
        Assert.Equal([new KeyValuePair<string, string>("x", "y")], volumes[2].Projections[1].Items);
        Assert.Empty(volumes[3].Projections);
    }

    [Fact]
    public void Reads_configmap_data_and_binary_keys()
    {
        var manifests = KubernetesManifests.Parse("""
            kind: ConfigMap
            metadata: { name: cm }
            data:
              a: "1"
              n: ~
            binaryData:
              bin: AAAA
              a: AAAA
            """, "cm.yaml");

        var cm = manifests.ConfigMaps["cm"];
        Assert.Equal("cm.yaml", cm.SourceName);
        Assert.Equal(new KubernetesValue("1", 4), cm.Data["a"]);
        Assert.Null(cm.Data["n"].Value);
        Assert.Equal(new KubernetesValue(null, 7), cm.Data["bin"]);
    }

    [Fact]
    public void Decodes_secret_data_and_lets_stringData_win()
    {
        var manifests = KubernetesManifests.Parse($$"""
            kind: Secret
            metadata: { name: s }
            data:
              a: {{Convert.ToBase64String(Encoding.UTF8.GetBytes("hello"))}}
              bad: "not base64!"
              empty: ~
              both: {{Convert.ToBase64String(Encoding.UTF8.GetBytes("from-data"))}}
            stringData:
              both: from-stringData
            """);

        var s = manifests.Secrets["s"];
        Assert.Equal("hello", s.Data["a"].Value);
        Assert.Equal(4, s.Data["a"].Line);
        Assert.Null(s.Data["bad"].Value);
        Assert.Null(s.Data["empty"].Value);
        Assert.Equal(new KubernetesValue("from-stringData", 9), s.Data["both"]);
        Assert.Empty(manifests.ConfigMaps);
    }

    [Fact]
    public void Loads_yaml_yml_and_json_files_from_a_folder_in_name_order()
    {
        Directory.CreateDirectory(Path.Join(_dir, "sub"));
        File.WriteAllText(Path.Join(_dir, "b.yml"), "kind: ConfigMap\nmetadata: { name: b }\n");
        File.WriteAllText(Path.Join(_dir, "a.YAML"), "kind: ConfigMap\nmetadata: { name: a }\n");
        File.WriteAllText(Path.Join(_dir, "sub", "c.json"), """{"kind":"ConfigMap","metadata":{"name":"c"}}""");
        File.WriteAllText(Path.Join(_dir, "d.txt"), "kind: ConfigMap\nmetadata: { name: d }\n");
        File.WriteAllText(Path.Join(_dir, "e.yaml"), "kind: ConfigMap\nmetadata: { name: b }\ndata: { later: x }\n");

        var manifests = KubernetesManifests.LoadFiles(_dir);

        Assert.Equal(["a", "b", "c"], manifests.ConfigMaps.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("e.yaml", manifests.ConfigMaps["b"].SourceName);
        Assert.Equal("c.json", manifests.ConfigMaps["c"].SourceName);
    }

    [Fact]
    public void Loads_a_single_file_of_any_extension()
    {
        var path = Path.Join(_dir, "x.txt");
        File.WriteAllText(path, "kind: ConfigMap\nmetadata: { name: x }\n");

        Assert.Equal("x.txt", KubernetesManifests.LoadFile(path).ConfigMaps["x"].SourceName);
    }

    [Fact]
    public void Reports_bad_input()
    {
        Assert.Throws<ArgumentNullException>(() => KubernetesManifests.LoadFiles(null!));
        Assert.Throws<ArgumentNullException>(() => KubernetesManifests.Parse(null!));
        var ex = Assert.Throws<FormatException>(() => KubernetesManifests.Parse("a: [\n", "broken.yaml"));
        Assert.StartsWith("Could not parse Kubernetes manifests in broken.yaml: ", ex.Message, StringComparison.Ordinal);
    }
}
