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
    public void Keeps_the_object_of_a_name_loaded_last_whatever_its_namespace()
    {
        var manifests = KubernetesManifests.Parse("""
            kind: ConfigMap
            metadata: { name: cfg, namespace: prod }
            data: { a: prod }
            ---
            kind: ConfigMap
            metadata: { name: cfg, namespace: staging }
            data: { a: staging }
            ---
            kind: ConfigMap
            metadata: { name: prod/cfg }
            data: { a: posing }
            ---
            kind: Secret
            metadata: { name: s, namespace: staging }
            ---
            kind: Secret
            metadata: { name: s, namespace: prod }
            ---
            apiVersion: secrets-store.csi.x-k8s.io/v1
            kind: SecretProviderClass
            metadata: { name: spc, namespace: prod }
            spec: { provider: aws }
            ---
            apiVersion: secrets-store.csi.x-k8s.io/v1
            kind: SecretProviderClass
            metadata: { name: spc, namespace: staging }
            spec: { provider: vault }
            """);

        Assert.Equal(["cfg"], manifests.ConfigMaps.Keys);
        Assert.Equal(("staging", "staging"), (manifests.ConfigMaps["cfg"].Namespace, manifests.ConfigMaps["cfg"].Data["a"].Value));
        Assert.Equal(["s"], manifests.Secrets.Keys);
        Assert.Equal("prod", manifests.Secrets["s"].Namespace);
        Assert.Equal("vault", Assert.Single(manifests.SecretProviderClasses).Value.Provider);
    }

    [Fact]
    public void Adds_and_finds_objects_of_one_name_in_many_namespaces_in_linear_work()
    {
        const int Count = 10_000;
        var calls = 0L;
        var objects = new KubernetesManifests.Namespaced<KubernetesData, string>(d => { calls++; return d.Namespace; }, d => { calls++; return d.Name; });

        for (var i = 0; i < Count; i++)
        {
            objects.Add(new KubernetesData("cfg", new Dictionary<string, KubernetesValue>(), "m.yaml") { Namespace = $"n{i}" });
        }

        for (var i = 0; i < Count; i++)
        {
            Assert.Equal($"n{i}", objects.Find($"n{i}", "cfg")?.Namespace);
        }

        Assert.Equal($"n{Count - 1}", objects.Find(null, "cfg")?.Namespace);
        Assert.InRange(calls, 0, 10L * Count);
    }

    [Fact]
    public void Reads_the_namespace_of_workloads_and_operator_objects()
    {
        var manifests = KubernetesManifests.Parse("""
            kind: Pod
            metadata: { name: p, namespace: prod }
            spec: { containers: [{ name: c }] }
            ---
            kind: Pod
            metadata: { name: q, namespace: "" }
            spec: { containers: [{ name: c }] }
            ---
            apiVersion: external-secrets.io/v1
            kind: ExternalSecret
            metadata: { name: es, namespace: prod }
            ---
            apiVersion: external-secrets.io/v1
            kind: SecretStore
            metadata: { name: store, namespace: prod }
            ---
            apiVersion: external-secrets.io/v1
            kind: ClusterSecretStore
            metadata: { name: store, namespace: prod }
            """);

        Assert.Equal<string?>(["prod", null], manifests.Workloads.Select(w => w.Namespace));
        Assert.Equal("prod", Assert.Single(manifests.ExternalSecrets).Namespace);
        Assert.Equal<(string, string?)>([("SecretStore", "prod"), ("ClusterSecretStore", null)], manifests.SecretStores.Select(s => (s.Kind, s.Namespace)));
    }

    [Fact]
    public void Keys_objects_by_name_alone_when_each_name_is_in_one_namespace()
    {
        var manifests = KubernetesManifests.Parse("""
            kind: ConfigMap
            metadata: { name: a, namespace: prod }
            ---
            kind: ConfigMap
            metadata: { name: b }
            ---
            kind: Secret
            metadata: { name: a, namespace: prod }
            ---
            kind: ConfigMap
            metadata: { name: a, namespace: prod }
            data: { later: x }
            """);

        Assert.Equal(["a", "b"], manifests.ConfigMaps.Keys);
        Assert.Equal(["later"], manifests.ConfigMaps["a"].Data.Keys);
        Assert.Equal(["a"], manifests.Secrets.Keys);
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
        Assert.Equal("sub/c.json", manifests.ConfigMaps["c"].SourceName);
    }

    [Fact]
    public void Names_files_in_a_folder_by_their_path_inside_it()
    {
        Directory.CreateDirectory(Path.Join(_dir, "api"));
        Directory.CreateDirectory(Path.Join(_dir, "worker", "base"));
        File.WriteAllText(Path.Join(_dir, "api", "cm.yaml"), "kind: ConfigMap\nmetadata: { name: api }\n");
        File.WriteAllText(Path.Join(_dir, "worker", "base", "cm.yaml"), "kind: ConfigMap\nmetadata: { name: worker }\n");

        var manifests = KubernetesManifests.LoadFiles(_dir);

        Assert.Equal("api/cm.yaml", manifests.ConfigMaps["api"].SourceName);
        Assert.Equal("worker/base/cm.yaml", manifests.ConfigMaps["worker"].SourceName);
        Assert.Equal("cm.yaml", KubernetesManifests.LoadFiles(Path.Join(_dir, "api", "cm.yaml")).ConfigMaps["api"].SourceName);
    }

    [Fact]
    public void Keeps_a_backslash_in_a_file_name_as_part_of_the_name()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "On Windows a backslash is the separator, so no file name can hold one.");
        Directory.CreateDirectory(Path.Join(_dir, "api"));
        File.WriteAllText(Path.Join(_dir, "api", "cm.yaml"), "kind: ConfigMap\nmetadata: { name: in-folder }\n");
        File.WriteAllText(Path.Join(_dir, "api\\cm.yaml"), "kind: ConfigMap\nmetadata: { name: in-name }\n");

        var manifests = KubernetesManifests.LoadFiles(_dir);

        Assert.Equal("api/cm.yaml", manifests.ConfigMaps["in-folder"].SourceName);
        Assert.Equal("api\\cm.yaml", manifests.ConfigMaps["in-name"].SourceName);
    }

    [Fact]
    public void Does_not_follow_links_below_a_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        var outside = Directory.CreateTempSubdirectory("cf-manifests-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Join(outside, "other.yaml"), "kind: ConfigMap\nmetadata: { name: outside }\n");
            File.WriteAllText(Path.Join(_dir, "cm.yaml"), "kind: ConfigMap\nmetadata: { name: inside }\n");
            Directory.CreateSymbolicLink(Path.Join(_dir, "loop"), ".");
            Directory.CreateSymbolicLink(Path.Join(_dir, "extra"), outside);
            File.CreateSymbolicLink(Path.Join(_dir, "linked.yaml"), Path.Join(outside, "other.yaml"));

            var manifests = KubernetesManifests.LoadFiles(_dir);

            Assert.Equal(["inside"], manifests.ConfigMaps.Keys);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
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
