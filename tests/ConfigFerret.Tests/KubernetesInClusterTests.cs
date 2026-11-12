using ConfigFerret.Kubernetes;

namespace ConfigFerret.Tests;

public sealed class KubernetesInClusterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cf-sa-").FullName;

    public KubernetesInClusterTests()
    {
        File.WriteAllText(Path.Join(_dir, "token"), " tok\n");
        File.WriteAllText(Path.Join(_dir, "namespace"), "from-file\n");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Task<InClusterTarget> Resolve(Dictionary<string, string> env, string? podName = null, string? @namespace = null) =>
        KubernetesInCluster.ResolveTargetAsync(_dir, n => env.GetValueOrDefault(n), podName, @namespace, CancellationToken.None);

    [Fact]
    public async Task Reads_the_service_account_and_defaults()
    {
        var target = await Resolve(new() { ["KUBERNETES_SERVICE_HOST"] = "10.0.0.1", ["HOSTNAME"] = "host-pod" });

        Assert.Equal(new Uri("https://10.0.0.1:443/"), target.BaseAddress);
        Assert.Equal("tok", target.Token);
        Assert.Equal("from-file", target.Namespace);
        Assert.Equal("host-pod", target.PodName);
    }

    [Fact]
    public async Task Environment_variables_override_the_defaults()
    {
        var target = await Resolve(new()
        {
            ["KUBERNETES_SERVICE_HOST"] = "fd00::1",
            ["KUBERNETES_SERVICE_PORT"] = "6443",
            ["POD_NAMESPACE"] = "env-ns",
            ["POD_NAME"] = "env-pod",
            ["HOSTNAME"] = "host-pod",
        });

        Assert.Equal(new Uri("https://[fd00::1]:6443/"), target.BaseAddress);
        Assert.Equal("env-ns", target.Namespace);
        Assert.Equal("env-pod", target.PodName);
    }

    [Fact]
    public async Task Arguments_override_the_environment()
    {
        var target = await Resolve(new() { ["KUBERNETES_SERVICE_HOST"] = "h", ["POD_NAMESPACE"] = "env-ns", ["POD_NAME"] = "env-pod" }, "arg-pod", "arg-ns");

        Assert.Equal(("arg-ns", "arg-pod"), (target.Namespace, target.PodName));
    }

    [Fact]
    public async Task Falls_back_to_the_machine_name()
    {
        var target = await Resolve(new() { ["KUBERNETES_SERVICE_HOST"] = "h" });

        Assert.Equal(Environment.MachineName, target.PodName);
    }

    [Fact]
    public async Task Says_when_it_is_not_in_a_pod()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Resolve([]));

        Assert.Equal("KUBERNETES_SERVICE_HOST is not set; this process does not seem to run in a pod.", ex.Message);
    }
}
