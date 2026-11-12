using ConfigFerret.Kustomize;

namespace ConfigFerret.Tests;

public sealed class KustomizationGeneratorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cf-kustomize-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Join(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private Kustomization Load(string relative = "app") => Kustomization.Load(Path.Join(_root, relative));

    [Fact]
    public void Finds_generated_names_with_or_without_the_hash_suffix()
    {
        Write("app/kustomization.yaml", """
            configMapGenerator:
              - name: cfg
                literals: [A=1]
            secretGenerator:
              - name: sec
                literals: [B=2]
            """);
        var k = Load();

        Assert.Equal("cfg", k.Find("cfg", isSecret: false)?.Name);
        Assert.Equal("cfg", k.Find("cfg-abcdefghij", isSecret: false)?.Name);
        Assert.Equal("sec", k.Find("sec-0123456789", isSecret: true)?.Name);
        Assert.Null(k.Find("cfg", isSecret: true));
        Assert.Null(k.Find("cfg-abcdefghij", isSecret: true));
        Assert.Null(k.Find("cfg-abcdefghi", isSecret: false));
        Assert.Null(k.Find("cfg-abcdefghijk", isSecret: false));
        Assert.Null(k.Find("other-abcdefghij", isSecret: false));
        Assert.Throws<ArgumentNullException>(() => k.Find(null!, isSecret: false));
    }

    [Fact]
    public void Reads_env_files_skipping_comments_blank_and_malformed_lines()
    {
        Write("app/kustomization.yaml", """
            configMapGenerator:
              - name: cfg
                envs: [a.properties, missing.properties]
              - name: single
                env: b.properties
            """);
        Write("app/a.properties", "# comment\n\nA=1\nB=\"quoted\"\nC='single'\nnoequals\n=novalue\n  D = 4\n");
        Write("app/b.properties", "E=5\n");
        var k = Load();

        var cfg = k.ConfigMaps["cfg"];
        Assert.Equal(["A", "B", "C", "D "], cfg.Keys.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("1", cfg.Keys["A"][0].Value);
        Assert.Equal(3, cfg.Keys["A"][0].Line);
        Assert.Equal("create (env file a.properties:3)", cfg.Keys["A"][0].How);
        Assert.Equal("quoted", cfg.Keys["B"][0].Value);
        Assert.Equal("single", cfg.Keys["C"][0].Value);
        Assert.Equal(" 4", cfg.Keys["D "][0].Value);
        Assert.Equal("5", k.ConfigMaps["single"].Keys["E"][0].Value);
    }

    [Fact]
    public void Reads_file_entries_with_and_without_a_key()
    {
        Write("app/kustomization.yaml", """
            configMapGenerator:
              - name: cfg
                files:
                  - conf/app.json
                  - alias=conf/other.txt
            """);
        var cfg = Load().ConfigMaps["cfg"];

        Assert.Null(cfg.Keys["app.json"][0].Value);
        Assert.Equal("create (file conf/app.json)", cfg.Keys["app.json"][0].How);
        Assert.Equal(4, cfg.Keys["app.json"][0].Line);
        Assert.Equal("create (file conf/other.txt)", cfg.Keys["alias"][0].How);
    }

    [Fact]
    public void Literals_keep_their_line_and_unquote_values()
    {
        Write("app/kustomization.yaml", """
            configMapGenerator:
              - name: cfg
                literals:
                  - A="x"
                  - B=y
                  - noequals
                  - =z
                  - C='
            """);
        var cfg = Load().ConfigMaps["cfg"];

        Assert.Equal(["A", "B", "C"], cfg.Keys.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("x", cfg.Keys["A"][0].Value);
        Assert.Equal(4, cfg.Keys["A"][0].Line);
        Assert.Equal("create (literal)", cfg.Keys["A"][0].How);
        Assert.Equal("'", cfg.Keys["C"][0].Value);
    }

    [Fact]
    public void Merge_keeps_base_keys_and_create_starts_over()
    {
        Write("base/kustomization.yaml", """
            configMapGenerator:
              - name: m
                literals: [A=1, B=2]
              - name: c
                literals: [A=1]
            """);
        Write("app/kustomization.yaml", """
            resources: [../base, deployment.yaml]
            configMapGenerator:
              - name: m
                behavior: merge
                literals: [B=3, C=4]
              - name: c
                literals: [Z=9]
            """);
        Write("app/deployment.yaml", "kind: Deployment\n");
        var k = Load();

        var m = k.ConfigMaps["m"];
        Assert.Equal(["1"], m.Keys["A"].Select(h => h.Value));
        Assert.Equal(["2", "3"], m.Keys["B"].Select(h => h.Value));
        Assert.Equal("merge (literal)", m.Keys["B"][1].How);
        Assert.Equal(["base/kustomization.yaml", "app/kustomization.yaml"], m.Keys["B"].Select(h => h.Kustomization));
        Assert.Equal(["Z"], k.ConfigMaps["c"].Keys.Keys);
    }

    [Fact]
    public void Replace_does_not_remove_a_key_twice()
    {
        Write("base/kustomization.yaml", """
            secretGenerator:
              - name: s
                literals: [A=1]
            """);
        Write("mid/kustomization.yaml", """
            resources: [../base]
            secretGenerator:
              - name: s
                behavior: replace
                literals: [B=2]
            """);
        Write("app/kustomization.yaml", """
            resources: [../mid]
            secretGenerator:
              - name: s
                behavior: replace
                literals: [C=3]
            """);
        var s = Load().Secrets["s"];

        Assert.Equal([false, true], s.Keys["A"].Select(h => h.IsRemoval));
        Assert.Equal("replace", s.Keys["A"][1].How);
        Assert.Equal(3, s.Keys["A"][1].Line);
        Assert.Equal("mid/kustomization.yaml", s.Keys["A"][1].Kustomization);
        Assert.Equal([false, true], s.Keys["B"].Select(h => h.IsRemoval));
        Assert.True(s.IsSecret);
    }

    [Fact]
    public void Applies_prefix_and_suffix_to_base_and_own_generators()
    {
        Write("base/kustomization.yaml", """
            configMapGenerator:
              - name: b
                literals: [A=1]
            """);
        Write("app/kustomization.yaml", """
            bases: [../base]
            nameSuffix: -v2
            configMapGenerator:
              - name: own
                literals: [A=1]
              - literals: [NO_NAME=1]
            """);
        var k = Load();

        Assert.Equal(["b-v2", "own-v2"], k.ConfigMaps.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Shows_a_lone_kustomization_with_its_folder_name()
    {
        Write("app/kustomization.yml", """
            configMapGenerator:
              - name: cfg
                literals: [A=1]
            """);

        Assert.Equal("app/kustomization.yml", Load().ConfigMaps["cfg"].Keys["A"][0].Kustomization);
    }

    [Fact]
    public void Accepts_a_capitalised_file_name_and_an_empty_file()
    {
        Write("app/Kustomization", "");

        var empty = Load();
        Assert.Empty(empty.ConfigMaps);
        Assert.Empty(empty.Secrets);
    }

    [Fact]
    public void Reports_missing_files_cycles_and_bad_yaml()
    {
        Directory.CreateDirectory(Path.Join(_root, "empty"));
        Write("a/kustomization.yaml", "resources: [../b]\n");
        Write("b/kustomization.yaml", "resources: [../a]\n");
        Write("bad/kustomization.yaml", "a: [\n");

        Assert.Throws<ArgumentNullException>(() => Kustomization.Load(null!));
        Assert.Contains("No kustomization file in", Assert.Throws<FileNotFoundException>(() => Load("empty")).Message, StringComparison.Ordinal);
        Assert.Contains("Kustomization cycle at", Assert.Throws<InvalidOperationException>(() => Load("a")).Message, StringComparison.Ordinal);
        Assert.StartsWith("Could not parse ", Assert.Throws<FormatException>(() => Load("bad")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_base_used_twice_is_not_a_cycle()
    {
        Write("base/kustomization.yaml", "configMapGenerator:\n  - name: c\n    literals: [A=1]\n");
        Write("x/kustomization.yaml", "resources: [../base]\n");
        Write("app/kustomization.yaml", "resources: [../base, ../x]\n");

        Assert.Single(Load().ConfigMaps);
    }

    [Fact]
    public void Resolver_lists_each_kustomization_step_for_a_key()
    {
        Write("app/kustomization.yaml", """
            secretGenerator:
              - name: s
                literals: [A=1]
            configMapGenerator:
              - name: c
                literals: [A=2]
            """);
        var resolver = new KustomizeResolver(Load());
        var diagnostics = new TraceDiagnostics();

        var secret = Assert.Single(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "x", Reference = new SecretKeyRef("s-abcdefghij", "A") }, diagnostics));
        var config = Assert.Single(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "x", Reference = new ConfigMapKeyRef("c", "A") }, diagnostics));

        Assert.Equal(LayerKind.Kustomize, secret.Kind);
        Assert.Equal("app/kustomization.yaml", secret.Source);
        Assert.Equal("line 3, secretGenerator s", secret.Location);
        Assert.Equal("1", secret.Value);
        Assert.True(secret.IsSensitive);
        Assert.False(secret.IsRemoval);
        Assert.Equal("create (literal)", secret.Note);
        Assert.Equal("line 6, configMapGenerator c", config.Location);
        Assert.False(config.IsSensitive);

        Assert.Empty(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "x", Reference = new ConfigMapKeyRef("c", "missing") }, diagnostics));
        Assert.Empty(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "x", Reference = new ConfigMapKeyRef("s", "A") }, diagnostics));
        Assert.Empty(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "x", Reference = new ConfigMapKeyRef("c", "A") }, diagnostics));
        Assert.Empty(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "x", Reference = new FileRef("/x") }, diagnostics));
    }

    [Fact]
    public void Env_file_entries_point_at_the_envs_line_in_the_kustomization()
    {
        Write("app/kustomization.yaml", """
            configMapGenerator:
              - name: cfg
                literals: [X=0]
                envs:
                  - first.properties
                  - second.properties
            """);
        Write("app/first.properties", "# comment\nA=1\n");
        Write("app/second.properties", "\n\n\nB=2\n");
        var cfg = Load().Find("cfg", isSecret: false)!;

        // The line is the envs entry in kustomization.yaml, not the line inside the env file.
        Assert.Equal(5, cfg.Keys["A"][0].Line);
        Assert.Equal("create (env file first.properties:2)", cfg.Keys["A"][0].How);
        Assert.Equal(6, cfg.Keys["B"][0].Line);
        Assert.Equal("create (env file second.properties:4)", cfg.Keys["B"][0].How);
    }
}
