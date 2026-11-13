using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;
using ConfigFerret.Kustomize;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tests;

/// <summary>
/// Golden tests: trace a realistic setup end to end and compare the full text and JSON output with the
/// approved snapshots in <c>Snapshots/</c>. When a change to the output is intended, run the tests, check the
/// <c>.received.</c> files and rename them to <c>.verified.</c> (or accept them in your diff tool).
/// </summary>
public class GoldenTests
{
    // An orders service deployed with Helm: two values files and a --set over the chart defaults, a ConfigMap
    // loaded with envFrom and a prefix, a literal env entry, a secretKeyRef, a configMapKeyRef and a fieldRef,
    // with appsettings files and a command-line argument on the .NET side.
    private static readonly Dictionary<string, string?> OrdersPod = new()
    {
        ["ORDERS_Db__Port"] = "5432",
        ["ORDERS_Db__Timeout"] = "60",
        ["ORDERS_Cache__Ttl"] = "900",
        ["ORDERS_Logging__LogLevel__Default"] = "Warning",
        ["ORDERS_Features__Search"] = "true",
        ["ORDERS_Db__Host"] = "orders-db.prod.svc",
        ["ORDERS_Db__Password"] = "prod-s3cr3t",
        ["ORDERS_Api__BaseUrl"] = "https://eu.api.example.test",
        ["ORDERS_Pod__Name"] = "orders-7c9d8f6b5-x2kqp",
    };

    // A billing service deployed with Kustomize: a staging overlay that renames everything, merges into the base
    // ConfigMap generator and replaces the base Secret generator.
    private static readonly Dictionary<string, string?> BillingPod = new()
    {
        ["BILLING_Billing__Currency"] = "EUR",
        ["BILLING_Billing__InvoiceDueDays"] = "14",
        ["BILLING_Billing__RetryCount"] = "10",
        ["BILLING_Logging__LogLevel__Default"] = "Debug",
        ["BILLING_Smtp__Host"] = "smtp.staging.internal",
        ["BILLING_Smtp__Port"] = "25",
        ["BILLING_Stripe__ApiKey"] = "sk_test_staging",
    };

    // A developer machine: appsettings files, user secrets, a connection string in an environment variable and a
    // command-line argument, with no Kubernetes at all.
    private static readonly Dictionary<string, string?> DeveloperShell = new()
    {
        ["DEVAPP_ConnectionStrings__Default"] = "Host=db.dev.internal;Database=app;Username=app;Password=hunter2",
        ["DEVAPP_Paging__PageSize"] = "10",
    };

    [Fact]
    public Task Helm_release_text() => VerifyText(TraceHelmRelease().ToText());

    [Fact]
    public Task Helm_release_json() => VerifyJson(TraceHelmRelease().ToJson());

    [Fact]
    public Task Helm_release_text_with_secrets_revealed() => VerifyText(TraceHelmRelease(reveal: true).ToText());

    [Fact]
    public Task Kustomize_overlay_text() => VerifyText(TraceKustomizeOverlay().ToText());

    [Fact]
    public Task Kustomize_overlay_json() => VerifyJson(TraceKustomizeOverlay().ToJson());

    [Fact]
    public Task Local_dev_text() => VerifyText(TraceLocalDev().ToText());

    [Fact]
    public Task Local_dev_json() => VerifyJson(TraceLocalDev().ToJson());

    // What the orders service was seen reading: everything except Api:BaseUrl and Pod:Name.
    private static readonly string[] OrdersReads =
    [
        "Cache:Ttl", "Db:Host", "Db:Password", "Db:Port", "Db:Timeout", "Features:Search",
        "Logging:LogLevel:Default", "Logging:LogLevel:Microsoft.AspNetCore", "Orders:NotConfigured",
    ];

    [Fact]
    public Task Helm_release_unused_text() => VerifyText(TraceHelmRelease().FindUnused(OrdersReads).ToText());

    [Fact]
    public Task Helm_release_unused_json() => VerifyJson(TraceHelmRelease().FindUnused(OrdersReads).ToJson());

    [Fact]
    public Task Kustomize_overlay_unused_text() => VerifyText(TraceKustomizeOverlay().FindUnused().ToText());

    [Fact]
    public Task Local_dev_unused_text() => VerifyText(TraceLocalDev().FindUnused(["Paging:PageSize", "ConnectionStrings:Default"]).ToText());

    private static ProvenanceReport TraceHelmRelease(bool reveal = false)
    {
        var dir = Fixture("helm-release");
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Join(dir, "app", "appsettings.json"))
            .AddJsonFile(Path.Join(dir, "app", "appsettings.Production.json"))
            .AddEnvironmentVariables(SetProcessEnvironment(OrdersPod, "ORDERS_"))
            .AddCommandLine(["--Features:Search=false"])
            .Build();

        return ConfigTracer.For(config)
            .WithOptions(o =>
            {
                o.Environment = OrdersPod;
                o.RevealSensitiveValues = reveal;
            })
            .WithKubernetes(KubernetesManifests.LoadFile(Path.Join(dir, "rendered.yaml")), workload: "orders", container: "orders")
            .WithHelm(HelmChart.Load(
                Path.Join(dir, "chart"),
                [Path.Join(dir, "values-prod.yaml"), Path.Join(dir, "values-eu.yaml")],
                ["cache.ttl=900"]))
            .Trace();
    }

    private static ProvenanceReport TraceKustomizeOverlay()
    {
        var dir = Fixture("kustomize-overlay");
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Join(dir, "app", "appsettings.json"))
            .AddEnvironmentVariables(SetProcessEnvironment(BillingPod, "BILLING_"))
            .Build();

        return ConfigTracer.For(config)
            .WithOptions(o => o.Environment = BillingPod)
            .WithKubernetes(KubernetesManifests.LoadFile(Path.Join(dir, "kustomize-build.yaml")))
            .WithKustomize(Kustomization.Load(Path.Join(dir, "kustomize", "overlays", "staging")))
            .Trace();
    }

    private static ProvenanceReport TraceLocalDev()
    {
        var dir = Fixture("local-dev");
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Join(dir, "appsettings.json"))
            .AddJsonFile(Path.Join(dir, "appsettings.Development.json"))
            .AddJsonFile(Path.Join(dir, "usersecrets", "orders-api-6f1c2e9a", "secrets.json"))
            .AddEnvironmentVariables(SetProcessEnvironment(DeveloperShell, "DEVAPP_"))
            .AddCommandLine(["--Paging:MaxPageSize=50"])
            .Build();

        return ConfigTracer.For(config)
            .WithOptions(o => o.Environment = DeveloperShell)
            .Trace();
    }

    private static string Fixture(string name) => TestPaths.Data("golden", name);

    // The .NET environment variable provider reads the process environment, so the fixture's variables are set
    // there too. Each fixture uses its own prefix so tests running in parallel do not see each other's values.
    private static string SetProcessEnvironment(Dictionary<string, string?> variables, string prefix)
    {
        foreach (var (name, value) in variables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        return prefix;
    }

    private static Task VerifyText(string text) => Verify(text, Settings());

    private static Task VerifyJson(string json) => Verify(json, "json", Settings());

    private static VerifySettings Settings()
    {
        var settings = new VerifySettings();
        settings.UseDirectory("Snapshots");
        // Fixture paths differ per machine and per OS; show them the same way everywhere.
        var root = TestPaths.Data();
        settings.AddScrubber(sb =>
        {
            sb.Replace(root, "{TestData}");
            sb.Replace(root.Replace('\\', '/'), "{TestData}");
        });
        return settings;
    }
}
