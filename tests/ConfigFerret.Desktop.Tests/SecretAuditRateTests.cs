using ConfigFerret.Desktop.Services;
using ConfigFerret.Kubernetes;
using ConfigFerret.Tests.Shared;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Desktop.Tests;

public class SecretAuditRateTests : IDisposable
{
    private const string ApiKey = "Zx81kQp4LmT7vRw2Nc9YbH3s";
    private readonly TempSettingsFolder _folder = new();

    public void Dispose()
    {
        _folder.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Rates_a_committed_api_key_as_exposed()
    {
        var finding = Rate("Payments:ApiKey", Files(("appsettings.json", Json("""{"Payments":{"ApiKey":"@KEY@"}}"""))));

        Assert.Equal(SecretRating.Exposed, finding.Rating);
        Assert.Contains("appsettings.json", finding.Where, StringComparison.Ordinal);
        AssertNoValue(finding);
    }

    [Fact]
    public void Rates_a_committed_api_key_as_exposed_even_when_the_key_name_gives_nothing_away()
    {
        var finding = Rate("Payments:Blob", Files(("appsettings.json", Json("""{"Payments":{"Blob":"@KEY@"}}"""))));

        Assert.Equal(SecretRating.Exposed, finding.Rating);
        AssertNoValue(finding);
    }

    [Fact]
    public void Rates_a_key_that_is_not_secret_as_not_secret()
    {
        var finding = Rate("Logging:LogLevel:Default", Files(("appsettings.json", """{"Logging":{"LogLevel":{"Default":"Information"}}}""")));

        Assert.Equal(SecretRating.NotSecret, finding.Rating);
    }

    [Fact]
    public void Rates_a_password_only_in_the_development_file_as_check()
    {
        var finding = Rate(
            "Db:Password",
            Files(("appsettings.json", """{"Db":{"Host":"localhost"}}"""), ("appsettings.Development.json", """{"Db":{"Password":"localhost-dev-password"}}""")));

        Assert.Equal(SecretRating.Check, finding.Rating);
        Assert.Contains("appsettings.Development.json", finding.Where, StringComparison.Ordinal);
        AssertNoValue(finding, "localhost-dev-password");
    }

    [Fact]
    public void Rates_a_secret_key_with_a_placeholder_value_as_check()
    {
        var finding = Rate("Payments:ApiKey", Files(("appsettings.json", """{"Payments":{"ApiKey":"#{PaymentsApiKey}"}}""")));

        Assert.Equal(SecretRating.Check, finding.Rating);
    }

    [Fact]
    public void Still_flags_a_committed_value_that_a_later_layer_overrides()
    {
        var finding = Rate(
            "Payments:ApiKey",
            Files(("appsettings.json", Json("""{"Payments":{"ApiKey":"@KEY@"}}"""))),
            new Dictionary<string, string?> { ["Payments:ApiKey"] = "Qm27pLz9XcV4bN8kRt5WyH1j" });

        Assert.Equal(SecretRating.Exposed, finding.Rating);
        AssertNoValue(finding, "Qm27pLz9XcV4bN8kRt5WyH1j");
    }

    [Fact]
    public void Rates_a_value_from_a_kubernetes_secret_as_protected()
    {
        var finding = RateContainer("Payments__ApiKey", PodYaml(secret: true));

        Assert.Equal(SecretRating.Protected, finding.Rating);
        Assert.Contains("payments", finding.Reason, StringComparison.OrdinalIgnoreCase);
        AssertNoValue(finding);
    }

    [Fact]
    public void Rates_the_same_value_from_a_config_map_as_exposed()
    {
        var finding = RateContainer("Payments__ApiKey", PodYaml(secret: false));

        Assert.Equal(SecretRating.Exposed, finding.Rating);
        AssertNoValue(finding);
    }

    [Fact]
    public void Audit_rates_every_setting_and_the_findings_hold_no_values()
    {
        var root = Build(Files(("appsettings.json", Json("""{"Payments":{"ApiKey":"@KEY@"},"Cache":{"Ttl":60}}"""))), null);
        var report = Trace(root);

        var findings = SecretAudit.Audit(report);

        Assert.Equal(report.Settings.Count, findings.Count);
        Assert.Equal(SecretRating.Exposed, findings.Single(f => f.Key == "Payments:ApiKey").Rating);
        Assert.Equal(SecretRating.NotSecret, findings.Single(f => f.Key == "Cache:Ttl").Rating);
        Assert.All(findings, f => AssertNoValue(f));
    }

    private static string PodYaml(bool secret)
    {
        // The Secret holds the api key base64 encoded, the ConfigMap in plain text: the same real value either way.
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ApiKey));
        var store = secret
            ? """
                apiVersion: v1
                kind: Secret
                metadata: { name: payments }
                data:
                  api-key: @VALUE@
                """
            : """
                apiVersion: v1
                kind: ConfigMap
                metadata: { name: payments }
                data:
                  api-key: @VALUE@
                """;
        var reference = secret ? "secretKeyRef" : "configMapKeyRef";
        var pod = """
            apiVersion: v1
            kind: Pod
            metadata: { name: orders-1 }
            spec:
              containers:
                - name: orders
                  env:
                    - name: Payments__ApiKey
                      valueFrom:
                        @REF@: { name: payments, key: api-key }
            """;
        return store.Replace("@VALUE@", secret ? encoded : ApiKey, StringComparison.Ordinal) + "\n---\n" + pod.Replace("@REF@", reference, StringComparison.Ordinal);
    }

    private static string Json(string json) => json.Replace("@KEY@", ApiKey, StringComparison.Ordinal);

    private static SecretFinding RateContainer(string key, string yaml)
    {
        var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(yaml))
            .WithOptions(o => o.RevealSensitiveValues = true)
            .Trace();
        return SecretAudit.Rate(report.Find(key)!);
    }

    private static SecretFinding Rate(string key, string[] files, Dictionary<string, string?>? memory = null)
    {
        var report = Trace(Build(files, memory));
        return SecretAudit.Rate(report.Find(key)!);
    }

    private string[] Files(params (string Name, string Json)[] files) => [.. files.Select(f => _folder.Write(f.Name, f.Json))];

    private static IConfigurationRoot Build(string[] files, Dictionary<string, string?>? memory)
    {
        var builder = new ConfigurationBuilder();
        foreach (var file in files)
        {
            builder.AddJsonFile(file, optional: false);
        }

        if (memory is not null)
        {
            builder.AddInMemoryCollection(memory);
        }

        return builder.Build();
    }

    private static ProvenanceReport Trace(IConfigurationRoot root) =>
        ConfigTracer.For(root)
            .WithOptions(o =>
            {
                o.RevealSensitiveValues = true;
                o.Environment = new Dictionary<string, string?>();
            })
            .Trace();

    private static void AssertNoValue(SecretFinding finding, string? value = null)
    {
        foreach (var secret in new[] { ApiKey, "localhost-dev-password", value }.OfType<string>())
        {
            Assert.DoesNotContain(secret, finding.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, finding.Where, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, finding.Key, StringComparison.Ordinal);
        }
    }
}
