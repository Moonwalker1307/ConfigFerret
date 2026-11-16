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
    public void Merge_finds_a_base_generator_by_its_name_before_the_base_prefix()
    {
        Write("base/kustomization.yaml", """
            namePrefix: shop-
            configMapGenerator:
              - name: api-config
                literals: [Db__Host=localhost, A=1]
            """);
        Write("mid/kustomization.yaml", """
            resources: [../base]
            nameSuffix: -v2
            configMapGenerator:
              - name: api-config
                behavior: merge
                literals: [Db__Host=mid-db]
            """);
        Write("app/kustomization.yaml", """
            resources: [../mid]
            configMapGenerator:
              - name: shop-api-config
                behavior: replace
                literals: [Db__Host=prod-db.internal]
            """);
        var k = Load();

        var config = Assert.Single(k.ConfigMaps).Value;
        Assert.Equal("shop-api-config-v2", config.Name);
        Assert.Equal(["localhost", "mid-db", null, "prod-db.internal"], config.Keys["Db__Host"].Select(h => h.Value));
        Assert.Equal(["base/kustomization.yaml", "mid/kustomization.yaml", "app/kustomization.yaml", "app/kustomization.yaml"], config.Keys["Db__Host"].Select(h => h.Kustomization));
        Assert.Equal([false, true], config.Keys["A"].Select(h => h.IsRemoval));
    }

    [Fact]
    public void Merge_finds_a_generator_by_every_name_it_had_after_several_renames()
    {
        Write("base/kustomization.yaml", """
            namePrefix: a-
            nameSuffix: -1
            configMapGenerator:
              - name: cfg
                literals: [K=base]
            """);
        Write("mid/kustomization.yaml", """
            resources: [../base]
            namePrefix: b-
            nameSuffix: -2
            """);
        Write("top/kustomization.yaml", """
            resources: [../mid]
            nameSuffix: -3
            """);
        Write("app/kustomization.yaml", """
            resources: [../top]
            namePrefix: c-
            configMapGenerator:
              - name: cfg
                behavior: merge
                literals: [K=own]
              - name: a-cfg-1
                behavior: merge
                literals: [K=innermost]
              - name: b-a-cfg-1-2
                behavior: merge
                literals: [K=middle]
              - name: b-a-cfg-1-2-3
                behavior: merge
                literals: [K=outermost]
              - name: a-cfg-1-2
                behavior: merge
                literals: [K=never]
              - name: b-a-cfg-1
                behavior: merge
                literals: [K=never]
            """);
        var k = Load();

        // a-cfg-1-2 and b-a-cfg-1 mix the prefix of one level with the suffix of another, so they are new objects.
        Assert.Equal(["c-a-cfg-1-2", "c-b-a-cfg-1", "c-b-a-cfg-1-2-3"], k.ConfigMaps.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["base", "own", "innermost", "middle", "outermost"], k.ConfigMaps["c-b-a-cfg-1-2-3"].Keys["K"].Select(h => h.Value));
    }

    [Fact]
    public void Merge_that_fits_objects_from_two_bases_is_refused()
    {
        Write("frontend/kustomization.yaml", """
            namePrefix: frontend-
            configMapGenerator:
              - name: app-config
                literals: [Mode=fast]
            """);
        Write("backend/kustomization.yaml", """
            namePrefix: backend-
            configMapGenerator:
              - name: app-config
                literals: [Mode=slow]
            """);
        Write("app/kustomization.yaml", """
            resources: [../frontend, ../backend]
            configMapGenerator:
              - name: app-config
                behavior: merge
                literals: [Mode=hunter2]
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => Load());

        Assert.Contains("configMapGenerator app-config", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Join(_root, "app"), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("frontend-", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_with_its_own_namespace_that_fits_objects_from_two_bases_matches_the_current_name_only()
    {
        // kustomize merges into a-config, the only object in team-a. Namespaces are not traced, so the overlay's
        // generator stays an object of its own, as when earlier names were not matched at all.
        Write("team-a/kustomization.yaml", """
            namespace: team-a
            namePrefix: a-
            configMapGenerator:
              - name: config
                literals: [Mode=fast]
            """);
        Write("team-b/kustomization.yaml", """
            namespace: team-b
            namePrefix: b-
            configMapGenerator:
              - name: config
                literals: [Mode=slow]
            """);
        Write("app/kustomization.yaml", """
            resources: [../team-a, ../team-b]
            configMapGenerator:
              - name: config
                namespace: team-a
                behavior: merge
                literals: [Mode=steady]
            """);
        var k = Load();

        Assert.Equal(["a-config", "b-config", "config"], k.ConfigMaps.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["fast"], k.ConfigMaps["a-config"].Keys["Mode"].Select(h => h.Value));
        Assert.Equal(["steady"], k.ConfigMaps["config"].Keys["Mode"].Select(h => h.Value));
    }

    [Fact]
    public void Create_does_not_take_over_a_base_object_by_its_name_before_the_prefix()
    {
        Write("base/kustomization.yaml", """
            namePrefix: a-
            configMapGenerator:
              - name: config
                literals: [A=1]
            """);
        Write("app/kustomization.yaml", """
            resources: [../base]
            configMapGenerator:
              - name: config
                literals: [B=2]
            """);
        var k = Load();

        Assert.Equal(["a-config", "config"], k.ConfigMaps.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["1"], k.ConfigMaps["a-config"].Keys["A"].Select(h => h.Value));
        Assert.Equal(["B"], k.ConfigMaps["config"].Keys.Keys);
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
    public void A_component_merges_into_the_generators_of_the_kustomization_that_names_it()
    {
        Write("base/kustomization.yaml", """
            configMapGenerator:
              - name: app-config
                literals: [A=1, B=2]
            """);
        Write("components/feature/kustomization.yaml", """
            apiVersion: kustomize.config.k8s.io/v1alpha1
            kind: Component
            configMapGenerator:
              - name: app-config
                behavior: merge
                literals: [B=3]
            """);
        Write("app/kustomization.yaml", """
            resources: [../base]
            components: [../components/feature]
            """);

        var config = Assert.Single(Load().ConfigMaps).Value;

        Assert.Equal("app-config", config.Name);
        Assert.Equal(["1"], config.Keys["A"].Select(h => h.Value));
        Assert.Equal(["2", "3"], config.Keys["B"].Select(h => h.Value));
        Assert.Equal(["base/kustomization.yaml", "components/feature/kustomization.yaml"], config.Keys["B"].Select(h => h.Kustomization));
        Assert.Equal("merge (literal)", config.Keys["B"][1].How);
    }

    [Fact]
    public void A_component_runs_after_its_parents_generators_and_before_its_parents_prefix()
    {
        Write("base/kustomization.yaml", """
            configMapGenerator:
              - name: cfg
                literals: [A=1]
            """);
        Write("feature/kustomization.yaml", """
            kind: Component
            namePrefix: feature-
            configMapGenerator:
              - name: cfg
                behavior: merge
                literals: [A=3]
              - name: extra
                literals: [E=1]
            """);
        Write("app/kustomization.yaml", """
            resources: [../base]
            components: [../feature]
            namePrefix: prod-
            configMapGenerator:
              - name: cfg
                behavior: merge
                literals: [A=2]
            """);
        var k = Load();

        Assert.Equal(["prod-feature-cfg", "prod-feature-extra"], k.ConfigMaps.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["1", "2", "3"], k.ConfigMaps["prod-feature-cfg"].Keys["A"].Select(h => h.Value));
    }

    [Fact]
    public void Reports_a_cycle_of_components()
    {
        Write("app/kustomization.yaml", "resources: [../base]\ncomponents: [../one]\n");
        Write("base/kustomization.yaml", "configMapGenerator:\n  - name: c\n    literals: [A=1]\n");
        Write("one/kustomization.yaml", "kind: Component\ncomponents: [../two]\n");
        Write("two/kustomization.yaml", "kind: Component\ncomponents: [../one]\n");

        Assert.Contains("Kustomization cycle at", Assert.Throws<InvalidOperationException>(() => Load()).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("resources")]
    [InlineData("components")]
    public async Task Stops_when_bases_fan_out_so_far_that_loading_would_never_finish(string field)
    {
        // Each folder names both folders of the next level, so the last level is reached 2^24 times.
        const int depth = 24;
        var kind = field == "components" ? "Component" : "Kustomization";
        for (var i = 0; i < depth; i++)
        {
            foreach (var side in new[] { "a", "b" })
            {
                Write($"l{i}{side}/kustomization.yaml", $"kind: {kind}\n{field}: [../l{i + 1}a, ../l{i + 1}b]\n");
            }
        }

        foreach (var side in new[] { "a", "b" })
        {
            Write($"l{depth}{side}/kustomization.yaml", $"kind: {kind}\nconfigMapGenerator:\n  - name: c\n    behavior: merge\n    literals: [A=1]\n");
        }

        Write("app/kustomization.yaml", field == "components" ? "resources: [../base]\ncomponents: [../l0a]\n" : "resources: [../base, ../l0a]\n");
        Write("base/kustomization.yaml", "configMapGenerator:\n  - name: c\n    literals: [A=0]\n");

        var load = Task.Run(() => Load(), TestContext.Current.CancellationToken);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => load.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.Contains("more than 10000 kustomizations", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Stops_at_bases_nested_more_than_100_deep()
    {
        // d0 names d1, which names d2, down to d100: 101 kustomizations in one line of bases.
        for (var i = 0; i < 100; i++)
        {
            Write($"d{i}/kustomization.yaml", $"resources: [../d{i + 1}]\n");
        }

        Write("d100/kustomization.yaml", "configMapGenerator:\n  - name: c\n    literals: [A=1]\n");

        var ex = Assert.Throws<InvalidOperationException>(() => Load("d0"));
        Assert.Contains("more than 100 deep", ex.Message, StringComparison.Ordinal);
        Assert.Contains("d100", ex.Message, StringComparison.Ordinal);
        Assert.Equal("1", Load("d1").ConfigMaps["c"].Keys["A"][0].Value);
    }

    [Theory]
    [InlineData("env file", "envs", "big.env")]
    [InlineData("kustomization", "big", "kustomization.yaml")]
    public void Stops_when_the_files_it_reads_hold_more_than_256_MiB(string kind, string folder, string file)
    {
        // An env file of 2,097,155 characters, named 127 times, is read again for each name: with the two small
        // kustomizations that is 266,339,9xx characters, 2,095,5xx below 256 MiB, so the next read of a file that size
        // crosses it. That is a 128th name of the env file, or else the kustomization in big.
        var big = "# " + new string('x', 2 * 1024 * 1024) + "\n";
        Write("envs/big.env", big);
        Write("big/kustomization.yaml", big);
        var envs = string.Join(", ", Enumerable.Repeat("big.env", kind == "env file" ? 128 : 127));
        Write("envs/kustomization.yaml", $"configMapGenerator:\n  - name: c\n    envs: [{envs}]\n");
        Write("app/kustomization.yaml", kind == "env file" ? "resources: [../envs]\n" : "resources: [../envs, ../big]\n");

        var ex = Assert.Throws<InvalidOperationException>(() => Load());
        Assert.Contains("more than 256 MiB", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Join(_root, folder, file), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Stops_at_a_kustomization_that_holds_more_than_16_MiB()
    {
        Write("app/kustomization.yaml", "#" + new string('x', 16 * 1024 * 1024) + "\n");

        var ex = Assert.Throws<InvalidOperationException>(() => Load());
        Assert.Contains("more than 16 MiB", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Join(_root, "app", "kustomization.yaml"), ex.Message, StringComparison.Ordinal);
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
    public void Refuses_an_env_file_outside_the_kustomization_folder()
    {
        Write("secret.env", "TOKEN=hunter2\n");
        Write("app/kustomization.yaml", """
            configMapGenerator:
              - name: cfg
                envs: [../secret.env]
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => Load());
        Assert.Contains("../secret.env", ex.Message, StringComparison.Ordinal);
        Assert.Equal("hunter2", Kustomization.Load(Path.Join(_root, "app"), allowFilesOutsideFolder: true).ConfigMaps["cfg"].Keys["TOKEN"][0].Value);
    }

    [Fact]
    public void Counts_a_file_below_a_root_folder_as_inside_it()
    {
        var file = Write("app/app.env", "A=1\n");
        var root = Path.GetPathRoot(file)!;

        Assert.True(Kustomization.IsInFolder(file, root));
        Assert.True(Kustomization.IsInFolder(file, _root));
        Assert.False(Kustomization.IsInFolder(root, root));
        Assert.False(Kustomization.IsInFolder(_root, _root));
    }

    [Fact]
    public void Does_not_follow_a_link_to_another_root()
    {
        var root = Path.GetPathRoot(_root)!;
        Assert.True(Kustomization.IsOnRoot(Path.Join(_root, "app.env"), root));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(Kustomization.IsOnRoot(@"c:\repo\app.env", @"C:\"));
            Assert.False(Kustomization.IsOnRoot(@"\\attacker\share\app.env", @"C:\"));
            Assert.False(Kustomization.IsOnRoot(@"\\?\UNC\attacker\share\app.env", @"C:\"));
            Assert.False(Kustomization.IsOnRoot(@"D:\repo\app.env", @"C:\"));
            return; // Creating a symbolic link needs extra rights on Windows; the junction tests below cover Windows.
        }

        // On Linux and macOS every path starts from /, so no link can lead to another root, and this part can't show
        // that such a link is refused. It shows only that a fully qualified link on the same root is still followed.
        var target = Write("app/config/app.env", "A=1\n");
        var link = Path.Join(_root, "app", "linked.env");
        File.CreateSymbolicLink(link, target);

        // The temp folder itself can lie behind a link, as /var does on macOS, so both sides are resolved.
        Assert.Equal(Kustomization.RealPath(target), Kustomization.RealPath(link));
        Assert.True(Kustomization.IsInFolder(link, Path.Join(_root, "app")));
    }

    // A new folder on a second drive, or null: GitHub's Windows runners keep the temp folder on C: and the build output
    // on D:. Linux and macOS have one root, so there is none.
    internal static string? FolderOnSecondDrive()
    {
        var temp = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        if (!OperatingSystem.IsWindows() || string.Equals(temp, Path.GetPathRoot(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var folder = Path.Join(AppContext.BaseDirectory, "cf-other-drive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    // Makes a directory junction, which needs no extra rights on Windows. False when it can't be made.
    internal static bool TryCreateJunction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public void Does_not_follow_a_junction_to_another_drive_out_of_the_folder()
    {
        if (FolderOnSecondDrive() is not { } other)
        {
            return; // Needs Windows and a second drive, such as D: on GitHub's runners.
        }

        try
        {
            var outside = Path.Join(other, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Join(outside, "app.env"), "A=1\n");
            Write("app/kustomization.yaml", "configMapGenerator:\n  - name: cfg\n    envs: [ext/app.env]\n");
            var junction = Path.Join(_root, "app", "ext");
            if (!TryCreateJunction(junction, outside))
            {
                return; // The junction could not be made here.
            }

            var file = Path.Join(junction, "app.env");
            Assert.Null(Kustomization.RealPath(file));
            Assert.Equal(Path.Join(outside, "app.env"), Kustomization.RealPath(file, anyRoot: true), ignoreCase: true);
            Assert.False(Kustomization.IsInFolder(file, Path.Join(_root, "app")));
            Assert.Throws<InvalidOperationException>(() => Load());
            Directory.Delete(junction);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void Finds_the_files_of_a_folder_behind_a_junction_to_another_drive()
    {
        if (FolderOnSecondDrive() is not { } other)
        {
            return; // Needs Windows and a second drive, such as D: on GitHub's runners.
        }

        try
        {
            var src = Path.Join(other, "src");
            Directory.CreateDirectory(Path.Join(src, "app"));
            File.WriteAllText(Path.Join(src, "app", "app.env"), "A=1\n");
            File.WriteAllText(Path.Join(src, "app", "kustomization.yaml"), "configMapGenerator:\n  - name: cfg\n    envs: [app.env]\n");
            var junction = Path.Join(_root, "src");
            if (!TryCreateJunction(junction, src))
            {
                return; // The junction could not be made here.
            }

            var app = Path.Join(junction, "app");
            Assert.True(Kustomization.IsInFolder(Path.Join(app, "app.env"), app));
            Assert.Equal("1", Kustomization.Load(app).ConfigMaps["cfg"].Keys["A"][0].Value);
            Directory.Delete(junction);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void Reads_an_env_file_below_the_kustomization_folder()
    {
        Write("app/config/app.env", "A=1\n");
        Write("app/kustomization.yaml", """
            configMapGenerator:
              - name: cfg
                envs: [config/../config/app.env]
            """);

        Assert.Equal("1", Load().ConfigMaps["cfg"].Keys["A"][0].Value);
    }

    [Fact]
    public void Refuses_an_env_file_that_links_outside_the_kustomization_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        var outside = Write("secret.env", "TOKEN=hunter2\n");
        Write("app/kustomization.yaml", """
            configMapGenerator:
              - name: cfg
                envs: [linked.env]
            """);
        File.CreateSymbolicLink(Path.Join(_root, "app", "linked.env"), outside);

        Assert.Throws<InvalidOperationException>(() => Load());
    }

    [Fact]
    public void Refuses_an_env_file_whose_link_runs_through_a_second_link()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        // link -> sub/secrets and sub -> outside: the target names a folder inside, but sub leads out.
        Write("outside/secrets/prod.env", "TOKEN=hunter2\n");
        Write("app/kustomization.yaml", """
            configMapGenerator:
              - name: cfg
                envs: [link/prod.env]
            """);
        Directory.CreateSymbolicLink(Path.Join(_root, "app", "sub"), Path.Join(_root, "outside"));
        Directory.CreateSymbolicLink(Path.Join(_root, "app", "link"), Path.Join("sub", "secrets"));

        Assert.Throws<InvalidOperationException>(() => Load());
    }

    [Fact]
    public void Reads_an_env_file_through_links_that_stay_in_the_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        Write("app/real/prod.env", "A=1\n");
        Write("app/kustomization.yaml", """
            configMapGenerator:
              - name: cfg
                envs: [link/prod.env]
            """);
        Directory.CreateSymbolicLink(Path.Join(_root, "app", "sub"), "real");
        Directory.CreateSymbolicLink(Path.Join(_root, "app", "link"), Path.Join("sub", "..", "sub"));

        Assert.Equal("1", Load().ConfigMaps["cfg"].Keys["A"][0].Value);
    }

    [Fact]
    public void Refuses_an_env_file_link_that_steps_up_out_of_a_linked_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        // In a link's target, sub/.. is the folder above where sub leads, not app.
        Write("outside/prod.env", "TOKEN=hunter2\n");
        Directory.CreateDirectory(Path.Join(_root, "outside", "deep"));
        Write("app/kustomization.yaml", """
            configMapGenerator:
              - name: cfg
                envs: [linked.env]
            """);
        Directory.CreateSymbolicLink(Path.Join(_root, "app", "sub"), Path.Join(_root, "outside", "deep"));
        File.CreateSymbolicLink(Path.Join(_root, "app", "linked.env"), Path.Join("sub", "..", "prod.env"));

        Assert.Throws<InvalidOperationException>(() => Load());
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
