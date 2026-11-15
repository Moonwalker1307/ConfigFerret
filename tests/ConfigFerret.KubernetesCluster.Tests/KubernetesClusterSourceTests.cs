using ConfigFerret.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static ConfigFerret.KubernetesCluster.Tests.KubernetesClusterTests;

namespace ConfigFerret.KubernetesCluster.Tests;

public class KubernetesClusterSourceTests
{
    [Theory]
    [InlineData("deployment/api", "Deployment", "api")]
    [InlineData("Deployments/api", "Deployment", "api")]
    [InlineData("deploy/api", "Deployment", "api")]
    [InlineData("statefulset/db", "StatefulSet", "db")]
    [InlineData("sts/db", "StatefulSet", "db")]
    [InlineData("statefulsets/db", "StatefulSet", "db")]
    [InlineData("daemonset/a", "DaemonSet", "a")]
    [InlineData("daemonsets/a", "DaemonSet", "a")]
    [InlineData("ds/a", "DaemonSet", "a")]
    [InlineData("cronjob/r", "CronJob", "r")]
    [InlineData("cronjobs/r", "CronJob", "r")]
    [InlineData("cj/r", "CronJob", "r")]
    [InlineData("job/m", "Job", "m")]
    [InlineData("jobs/m", "Job", "m")]
    [InlineData("pod/p", "Pod", "p")]
    [InlineData("pods/p", "Pod", "p")]
    [InlineData("po/p", "Pod", "p")]
    [InlineData(" api-7d9f8c-x2kqp ", "Pod", "api-7d9f8c-x2kqp")]
    [InlineData("deployment.apps/a.b-1", "Deployment", "a.b-1")]
    public void Parses_workloads(string text, string kind, string name)
    {
        var workload = WorkloadReference.Parse(text);
        Assert.Equal(new WorkloadReference(kind, name), workload);
        Assert.Equal($"{kind}/{name}", workload.ToString());
    }

    [Theory]
    [InlineData("service/api", "Workload kind 'service' is not supported. Use deployment, statefulset, daemonset, cronjob, job or pod.")]
    [InlineData("deployment/", "'' is not a valid Kubernetes object name: use lowercase letters, digits, '-' and '.'.")]
    [InlineData("deployment/API", "'API' is not a valid Kubernetes object name: use lowercase letters, digits, '-' and '.'.")]
    [InlineData("pod/a/b", "'a/b' is not a valid Kubernetes object name: use lowercase letters, digits, '-' and '.'.")]
    [InlineData("a_b", "'a_b' is not a valid Kubernetes object name: use lowercase letters, digits, '-' and '.'.")]
    public void Rejects_bad_workloads(string text, string message)
    {
        var ex = Assert.Throws<ArgumentException>(() => WorkloadReference.Parse(text));
        Assert.StartsWith(message, ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => WorkloadReference.Parse(" "));
        Assert.Throws<ArgumentException>(() => WorkloadReference.Parse(new string('a', 254)));
        Assert.Equal("Pod", WorkloadReference.Parse(new string('a', 253)).Kind);
    }

    [Fact]
    public void Provider_describes_its_settings()
    {
        var provider = new KubernetesClusterSourceProvider();

        Assert.Equal("kubernetes-cluster", provider.Id);
        Assert.Equal("Kubernetes cluster", provider.DisplayName);
        Assert.Equal(SourceCategory.Cluster, provider.Category);
        Assert.Equal(["Kubeconfig", "Context", "Namespace", "Workload", "Container", "IncludeSecrets"], provider.Settings.Select(s => s.Name));
        Assert.Equal(["Kubeconfig file", "Context", "Namespace", "Workload", "Container", "Read Secrets"], provider.Settings.Select(s => s.Label));
        Assert.All(provider.Settings, s => Assert.EndsWith(".", s.Help, StringComparison.Ordinal));
        Assert.StartsWith("The kubeconfig file.", provider.Settings[0].Help, StringComparison.Ordinal);
        Assert.StartsWith("The kubeconfig context.", provider.Settings[1].Help, StringComparison.Ordinal);
        Assert.StartsWith("The namespace.", provider.Settings[2].Help, StringComparison.Ordinal);
        Assert.StartsWith("The workload as kind/name", provider.Settings[3].Help, StringComparison.Ordinal);
        Assert.StartsWith("The container name.", provider.Settings[4].Help, StringComparison.Ordinal);
        Assert.StartsWith("Also read the Secrets", provider.Settings[5].Help, StringComparison.Ordinal);
        Assert.Equal(SourceSettingKind.Path, provider.Settings[0].Kind);
        Assert.Equal(["KUBECONFIG"], provider.Settings[0].EnvironmentVariables);
        Assert.Equal(SourceSettingKind.Boolean, provider.Settings[5].Kind);
        Assert.Equal("false", provider.Settings[5].DefaultValue);
        Assert.Equal(["Workload"], provider.Settings.Where(s => s.Required).Select(s => s.Name));
        Assert.DoesNotContain(provider.Settings, s => s.Kind == SourceSettingKind.Secret);
    }

    [Fact]
    public void Provider_creates_a_source_from_settings()
    {
        var source = Assert.IsType<KubernetesClusterSource>(new KubernetesClusterSourceProvider().Create(new Dictionary<string, string?>
        {
            ["Kubeconfig"] = " /k ",
            ["Context"] = "prod",
            ["Namespace"] = "orders",
            ["Workload"] = "deployment/api",
            ["Container"] = "api",
            ["IncludeSecrets"] = "True",
        }));

        Assert.Equal("/k", source.Options.Kubeconfig);
        Assert.Equal("prod", source.Options.Context);
        Assert.Equal("orders", source.Options.Namespace);
        Assert.Equal("deployment/api", source.Options.Workload);
        Assert.Equal("api", source.Options.Container);
        Assert.True(source.Options.IncludeSecrets);
        Assert.Equal("Kubernetes deployment/api (prod)", source.DisplayName);

        var minimal = (KubernetesClusterSource)new KubernetesClusterSourceProvider().Create(new Dictionary<string, string?> { ["Workload"] = "p", ["Context"] = " ", ["IncludeSecrets"] = "" });
        Assert.False(minimal.Options.IncludeSecrets);
        Assert.Null(minimal.Options.Context);
        Assert.Equal("Kubernetes p", minimal.DisplayName);
    }

    [Fact]
    public void Provider_rejects_missing_or_invalid_settings()
    {
        var provider = new KubernetesClusterSourceProvider();
        Assert.Throws<ArgumentNullException>(() => provider.Create(null!));
        Assert.Equal("Workload is required, for example deployment/api. (Parameter 'values')",
            Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?>())).Message);
        Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?> { ["Workload"] = "service/x" }));
        Assert.Equal("IncludeSecrets must be true or false. (Parameter 'values')",
            Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?> { ["Workload"] = "p", ["IncludeSecrets"] = "yes" })).Message);
    }

    [Fact]
    public void Source_rejects_null_options()
    {
        Assert.Throws<ArgumentNullException>(() => new KubernetesClusterSource((KubernetesClusterOptions)null!));
        Assert.Throws<ArgumentNullException>(() => new KubernetesClusterSource((IOptions<KubernetesClusterOptions>)null!));
    }

    private static ServiceProvider Services(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddConfigFerretKubernetesCluster(configuration.GetSection("ConfigFerret:KubernetesCluster"))
            .BuildServiceProvider();
    }

    [Fact]
    public void DI_binds_the_options()
    {
        using var services = Services(new()
        {
            ["ConfigFerret:KubernetesCluster:Kubeconfig"] = Kubeconfig,
            ["ConfigFerret:KubernetesCluster:Workload"] = "deployment/api",
            ["ConfigFerret:KubernetesCluster:IncludeSecrets"] = "true",
            ["ConfigFerret:KubernetesCluster:Context"] = "prod",
        });

        var options = services.GetRequiredService<IOptions<KubernetesClusterOptions>>().Value;
        Assert.Equal("deployment/api", options.Workload);
        Assert.True(options.IncludeSecrets);
        Assert.Same(options, services.GetRequiredService<KubernetesClusterSource>().Options);
    }

    [Theory]
    [InlineData(null, null, false, "ConfigFerret:KubernetesCluster:Workload is not set. Set it to the workload as kind/name, for example deployment/api.")]
    [InlineData("service/x", null, false, "ConfigFerret:KubernetesCluster:Workload: Workload kind 'service' is not supported. Use deployment, statefulset, daemonset, cronjob, job or pod.")]
    [InlineData("p", "/no/such/file", false, "ConfigFerret:KubernetesCluster:Kubeconfig names /no/such/file, which does not exist.")]
    [InlineData("p", "KUBECONFIG_FIXTURE", true, "ConfigFerret:KubernetesCluster:InCluster and ConfigFerret:KubernetesCluster:Kubeconfig are both set. Use one.")]
    public void DI_validation_fails_with_a_clear_message(string? workload, string? kubeconfig, bool inCluster, string message)
    {
        using var services = Services(new()
        {
            ["ConfigFerret:KubernetesCluster:Workload"] = workload,
            ["ConfigFerret:KubernetesCluster:Kubeconfig"] = kubeconfig == "KUBECONFIG_FIXTURE" ? Kubeconfig : kubeconfig,
            ["ConfigFerret:KubernetesCluster:InCluster"] = inCluster ? "true" : null,
        });

        var ex = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<KubernetesClusterOptions>>().Value);
        Assert.Equal([message], ex.Failures);
    }

    [Fact]
    public void Validator_names_keys_without_a_section()
    {
        var result = new KubernetesClusterOptionsValidator("").Validate(null, new KubernetesClusterOptions());
        Assert.Equal(["Workload is not set. Set it to the workload as kind/name, for example deployment/api."], result.Failures);
        Assert.True(new KubernetesClusterOptionsValidator("x").Validate(null, new KubernetesClusterOptions { Workload = "p" }).Succeeded);
        Assert.Throws<ArgumentNullException>(() => new KubernetesClusterOptionsValidator("").Validate(null, null!));
    }

    [Fact]
    public void DI_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => KubernetesClusterServiceCollectionExtensions.AddConfigFerretKubernetesCluster(null!, new ConfigurationBuilder().Build()));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddConfigFerretKubernetesCluster(null!));
    }

    [Fact]
    public async Task WithKubernetesClusterAsync_loads_through_the_registered_source()
    {
        using var services = Services(new()
        {
            ["ConfigFerret:KubernetesCluster:Kubeconfig"] = Kubeconfig,
            ["ConfigFerret:KubernetesCluster:Workload"] = "deployment/api",
        });
        var server = Server();
        var source = services.GetRequiredService<KubernetesClusterSource>();
        source.Handlers = [server];

        var tracer = ConfigTracer.For(new ConfigurationBuilder().AddInMemoryCollection([new("A", "1")]).Build());
        Assert.Same(tracer, await tracer.WithKubernetesClusterAsync(services, TestContext.Current.CancellationToken));
        Assert.Contains("ConfigMap missing does not exist in namespace orders.", tracer.Trace().Warnings);
        Assert.NotEmpty(server.Paths);
    }

    [Fact]
    public async Task WithKubernetesClusterAsync_needs_the_registration()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tracer.WithKubernetesClusterAsync(services, TestContext.Current.CancellationToken));
        Assert.StartsWith("KubernetesClusterSource is not registered.", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentNullException>(() => tracer.WithKubernetesClusterAsync(null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => KubernetesClusterTracerExtensions.WithKubernetesClusterAsync(null!, services, TestContext.Current.CancellationToken));
    }
}
