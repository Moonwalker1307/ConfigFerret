using System.Text;
using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;
using ConfigFerret.Kustomize;

namespace ConfigFerret.Tests;

/// <summary>
/// Inputs the fuzzer (fuzz/) found that once crashed a parser. Malformed YAML must fail with
/// <see cref="FormatException"/>, never with whatever YamlDotNet throws inside.
/// </summary>
public class FuzzRegressionTests
{
    private static string Read(string name) => File.ReadAllText(TestPaths.Data("fuzz", name), Encoding.UTF8);

    [Fact]
    public void Malformed_helm_values_throw_FormatException()
    {
        var yaml = Read("helm-values-unsupported-event.yaml");

        Assert.Throws<FormatException>(() => HelmChart.FromText([("values.yaml", yaml)]));
    }

    [Fact]
    public void Malformed_kubernetes_manifests_throw_FormatException()
    {
        var yaml = Read("kubernetes-plain-scalar.yaml");

        Assert.Throws<FormatException>(() => KubernetesManifests.Parse(yaml));
    }

    [Fact]
    public void Malformed_kustomization_throws_FormatException()
    {
        // The fuzz input holds several files separated by %% lines; the first is the kustomization.
        var kustomization = Read("kustomize-plain-scalar.txt").Split("\n%%\n")[0];
        var dir = Path.Join(Path.GetTempPath(), $"configferret-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Join(dir, "kustomization.yaml"), kustomization);

            Assert.Throws<FormatException>(() => Kustomization.Load(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void An_alias_used_as_a_key_before_its_anchor_throws_FormatException()
    {
        // Found by fuzzing: YamlDotNet 18.1.0 overflows the stack on this, which ends the process.
        const string yaml = "m:\n  *a : 1\nx: &a foo\n";

        Assert.Throws<FormatException>(() => HelmChart.FromText([("values.yaml", yaml)]));
        Assert.Throws<FormatException>(() => KubernetesManifests.Parse(yaml));
    }

    [Fact]
    public void Anchors_aliases_and_merge_keys_still_load()
    {
        const string yaml = """
            defaults: &defaults
              timeout: 30
              host: db.internal
            db:
              <<: *defaults
              timeout: 42
            copy: *defaults
            """;

        var chart = HelmChart.FromText([("values.yaml", yaml)]);

        Assert.Equal("30", chart.GetValue("defaults.timeout"));
        Assert.Equal("db.internal", chart.GetValue("copy.host"));
    }

    [Theory]
    [InlineData("a: &a\n  b: *a\n")]
    [InlineData("a: &a\n  <<: *a\n")]
    [InlineData("a: &a [1, *a]\n")]
    [InlineData("a: &a\n  *a : 1\n")]
    public void An_alias_inside_its_own_anchor_throws_FormatException(string yaml)
    {
        // Found by fuzzing: a node that contains itself overflowed the stack, which ends the process.
        Assert.Throws<FormatException>(() => HelmChart.FromText([("values.yaml", yaml)]));
        Assert.Throws<FormatException>(() => KubernetesManifests.Parse(yaml));
    }

    [Fact]
    public void An_anchor_redefined_inside_its_node_can_still_be_used()
    {
        // The alias refers to the inner scalar, which is complete, so the node does not contain itself.
        const string yaml = "a: &x\n  b: &x 5\n  c: *x\n";

        Assert.Equal("5", HelmChart.FromText([("values.yaml", yaml)]).GetValue("a.c"));
    }
}
