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
}
