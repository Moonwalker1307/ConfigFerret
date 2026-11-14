using static ConfigFerret.KubernetesCluster.Tests.KubernetesClusterTests;

namespace ConfigFerret.KubernetesCluster.Tests;

public class KubeconfigTests
{
    private static readonly string Second = Path.Join(AppContext.BaseDirectory, "TestData", "second-kubeconfig.yaml");
    private static readonly string Broken = Path.Join(AppContext.BaseDirectory, "TestData", "broken-kubeconfig.yaml");

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Uses_the_current_context_of_the_given_file()
    {
        var (config, context) = KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions { Kubeconfig = Kubeconfig }, Env());

        Assert.Equal("prod", context);
        Assert.Equal("orders", config.Namespace);
        Assert.StartsWith("https://fake-cluster.example:6443", config.Host, StringComparison.Ordinal);
        Assert.Equal(Token, config.AccessToken);
    }

    [Fact]
    public void Merges_KUBECONFIG_files_with_the_first_one_winning()
    {
        var list = string.Join(Path.PathSeparator, Second, "/does/not/exist", Kubeconfig, Second);
        var (config, context) = KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions(), Env(("KUBECONFIG", list)));
        Assert.Equal("staging", context);
        Assert.StartsWith("https://staging-cluster.example", config.Host, StringComparison.Ordinal);

        // prod-cluster is defined in both; the first file's definition wins.
        var (prod, _) = KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions { Context = "prod" }, Env(("KUBECONFIG", list)));
        Assert.StartsWith("https://shadowed.example", prod.Host, StringComparison.Ordinal);
        Assert.Equal(Token, prod.AccessToken);
    }

    [Fact]
    public void A_single_KUBECONFIG_file_is_used_as_is()
    {
        var (_, context) = KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions(), Env(("KUBECONFIG", Kubeconfig)));
        Assert.Equal("prod", context);
    }

    [Fact]
    public void Falls_back_to_the_home_kube_config()
    {
        var home = Directory.CreateTempSubdirectory("configferret-home-");
        try
        {
            Directory.CreateDirectory(Path.Join(home.FullName, ".kube"));
            File.Copy(Kubeconfig, Path.Join(home.FullName, ".kube", "config"));

            Assert.Equal("prod", KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions(), Env(("HOME", home.FullName))).Context);
            Assert.Equal("prod", KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions(), Env(("USERPROFILE", home.FullName))).Context);
            Assert.Equal("prod", KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions(), Env(("KUBECONFIG", "/nope"), ("HOME", home.FullName))).Context);
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    [Fact]
    public void No_kubeconfig_outside_a_cluster_is_an_error()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions(), Env(("HOME", "/does/not/exist"))));
        Assert.Equal("No kubeconfig found: set Kubeconfig or KUBECONFIG, create ~/.kube/config, or run in a pod.", ex.Message);
        Assert.Throws<InvalidOperationException>(() => KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions(), Env()));
    }

    [Fact]
    public void InCluster_outside_a_cluster_fails_and_skips_kubeconfig_files()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions { InCluster = true }, Env(("KUBECONFIG", Kubeconfig))));
        Assert.StartsWith("Could not use the pod's service account: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_server_that_is_not_http_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions { Kubeconfig = Kubeconfig, Context = "ftp" }, Env()));
        Assert.Equal("The kubeconfig's cluster has no valid server address.", ex.Message);
    }

    [Fact]
    public void Merged_files_may_leave_out_lists_and_the_current_context()
    {
        var contextsOnly = Path.Join(AppContext.BaseDirectory, "TestData", "contexts-only-kubeconfig.yaml");
        var list = $" {contextsOnly} {Path.PathSeparator} {Kubeconfig} ";
        var (config, context) = KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions(), Env(("KUBECONFIG", list)));
        Assert.Equal("prod", context);
        Assert.Equal("orders", config.Namespace);

        var (extra, _) = KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions { Context = "extra" }, Env(("KUBECONFIG", list)));
        Assert.Equal("extra", extra.Namespace);
        Assert.Equal(Token, extra.AccessToken);
    }

    [Fact]
    public void HOME_wins_over_USERPROFILE()
    {
        var home = Directory.CreateTempSubdirectory("configferret-home-");
        try
        {
            Directory.CreateDirectory(Path.Join(home.FullName, ".kube"));
            File.Copy(Second, Path.Join(home.FullName, ".kube", "config"));
            var other = Directory.CreateTempSubdirectory("configferret-profile-");
            Directory.CreateDirectory(Path.Join(other.FullName, ".kube"));
            File.Copy(Kubeconfig, Path.Join(other.FullName, ".kube", "config"));

            Assert.Equal("staging", KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions(), Env(("HOME", home.FullName), ("USERPROFILE", other.FullName))).Context);
            other.Delete(recursive: true);
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_missing_explicit_file_is_FileNotFound()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions { Kubeconfig = "/no/such/kubeconfig" }, Env()));
        Assert.Equal("/no/such/kubeconfig", ex.FileName);
        Assert.Equal("The kubeconfig file /no/such/kubeconfig does not exist.", ex.Message);
    }

    [Fact]
    public void An_unknown_context_or_broken_file_is_an_InvalidOperationException()
    {
        var unknown = Assert.Throws<InvalidOperationException>(() => KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions { Kubeconfig = Kubeconfig, Context = "nope" }, Env()));
        Assert.StartsWith("Could not use the kubeconfig (kubeconfig.yaml): ", unknown.Message, StringComparison.Ordinal);
        Assert.True(unknown.Message.Length > "Could not use the kubeconfig (kubeconfig.yaml): ".Length);
        Assert.DoesNotContain(Token, unknown.Message, StringComparison.Ordinal);

        var broken = Assert.Throws<InvalidOperationException>(() => KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions { Kubeconfig = Broken }, Env()));
        Assert.StartsWith("Could not use the kubeconfig (broken-kubeconfig.yaml): ", broken.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_http_is_refused_unless_loopback_or_allowed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions { Kubeconfig = Kubeconfig, Context = "plain" }, Env()));
        Assert.Equal("The API server plain-cluster.example uses plain http, which would send the credential in clear text. Use https, or set AllowUnencryptedHttp.", ex.Message);

        Assert.Equal("plain", KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions { Kubeconfig = Kubeconfig, Context = "plain", AllowUnencryptedHttp = true }, Env()).Context);
        Assert.Equal("local", KubernetesClusterReader.BuildConfiguration(new KubernetesClusterOptions { Kubeconfig = Kubeconfig, Context = "local" }, Env()).Context);
    }

    [Fact]
    public async Task Loads_through_the_public_entry_point_with_the_kubeconfig_credential()
    {
        var server = Server();
        var source = new KubernetesClusterSource(Options()) { Handlers = [server], GetEnvironmentVariable = _ => null };
        var loaded = await source.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Kubernetes Deployment/api (prod, orders)", loaded.DisplayName);
        Assert.Equal(["ConfigMap missing does not exist in namespace orders."], loaded.Warnings);
        Assert.Contains("Bearer " + Token, server.Authorizations);
    }
}
