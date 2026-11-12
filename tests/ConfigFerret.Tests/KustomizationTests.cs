using ConfigFerret.Kustomize;

namespace ConfigFerret.Tests;

public class KustomizationTests
{
    [Fact]
    public void Applies_name_prefix_and_merges_overlay_keys()
    {
        var k = Kustomization.Load(TestPaths.Data("kustomize", "overlays", "prod"));

        var generated = k.Find("prod-api-config-abcdefghij", isSecret: false);

        Assert.NotNull(generated);
        Assert.Equal("prod-api-config", generated.Name);
        Assert.Equal(["30", "42"], generated.Keys["KS_Db__Timeout"].Select(h => h.Value));
        Assert.Single(generated.Keys["KS_Db__Host"]);
        Assert.Null(k.Find("prod-api-config", isSecret: true));
    }

    [Fact]
    public void Replace_behavior_drops_keys_from_the_base()
    {
        var root = Directory.CreateTempSubdirectory("ferret-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "base"));
            Directory.CreateDirectory(Path.Combine(root.FullName, "prod"));
            File.WriteAllText(Path.Combine(root.FullName, "base", "kustomization.yaml"), """
                secretGenerator:
                  - name: creds
                    literals: [A=1, B=2]
                """);
            File.WriteAllText(Path.Combine(root.FullName, "prod", "kustomization.yaml"), """
                resources: [../base]
                secretGenerator:
                  - name: creds
                    behavior: replace
                    literals: ["A='9'"]
                """);

            var creds = Kustomization.Load(Path.Combine(root.FullName, "prod")).Find("creds", isSecret: true)!;

            Assert.Equal([false, true, false], creds.Keys["A"].Select(h => h.IsRemoval));
            Assert.Equal("9", creds.Keys["A"][^1].Value);
            Assert.True(creds.Keys["B"][^1].IsRemoval);
            Assert.Equal("base/kustomization.yaml", creds.Keys["B"][0].Kustomization);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_configmap_and_a_secret_can_share_a_name()
    {
        // Found by fuzzing: this threw ArgumentException from Load.
        var root = Directory.CreateTempSubdirectory("ferret-");
        try
        {
            File.WriteAllText(Path.Join(root.FullName, "kustomization.yaml"), """
                configMapGenerator:
                  - name: api
                    literals: [MODE=fast]
                secretGenerator:
                  - name: api
                    literals: [PASSWORD=hunter2]
                """);

            var kustomization = Kustomization.Load(root.FullName);

            Assert.Equal("fast", kustomization.Find("api", isSecret: false)!.Keys["MODE"][^1].Value);
            Assert.Equal("hunter2", kustomization.Find("api-0123456789", isSecret: true)!.Keys["PASSWORD"][^1].Value);
            Assert.Single(kustomization.ConfigMaps);
            Assert.Single(kustomization.Secrets);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_generator_with_an_empty_name_is_skipped()
    {
        // Found by fuzzing: name: '' produced a generated ConfigMap that Find could never match.
        var root = Directory.CreateTempSubdirectory("ferret-");
        try
        {
            File.WriteAllText(Path.Join(root.FullName, "kustomization.yaml"), "configMapGenerator:\n  - name: ''\n    literals: [A=1]\n");

            var kustomization = Kustomization.Load(root.FullName);

            Assert.Empty(kustomization.ConfigMaps);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
