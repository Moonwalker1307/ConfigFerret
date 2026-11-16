using System.Net;
using Azure;
using Azure.Data.AppConfiguration;

namespace ConfigFerret.Azure.Tests;

public class AppConfigurationLoadTests
{
    internal const string Endpoint = "https://contoso.azconfig.io";
    internal const string AccessKey = "YWNjZXNzLWtleS1uZXZlci1wcmludGVk";
    internal const string ConnectionString = $"Endpoint={Endpoint};Id=key-id;Secret={AccessKey}";
    internal const string KeyVaultRefType = "application/vnd.microsoft.appconfig.keyvaultref+json;charset=utf-8";
    internal const string FeatureFlagType = "application/vnd.microsoft.appconfig.ff+json;charset=utf-8";

    internal static string Kv(string key, string? value, string? label = null, string? contentType = null) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["etag"] = "e",
            ["key"] = key,
            ["label"] = label,
            ["content_type"] = contentType,
            ["value"] = value,
            ["tags"] = new Dictionary<string, string> { ["owner"] = "orders" },
            ["locked"] = label == "locked",
            ["last_modified"] = "2026-01-02T03:04:05+00:00",
        });

    internal static string Page(string? after, params string[] items) =>
        $$"""{"items":[{{string.Join(",", items)}}]{{(after is null ? string.Empty : $",\"@nextLink\":\"/kv?api-version=2023-11-01&key=%2A&label=%00&after={after}\"")}}}""";

    internal static string KeysPath(string label = "%00", string key = "%2A") => $"/kv?key={key}&label={label}";

    internal static string FlagsPath(string label = "%00") => $"/kv?key=.appconfig.featureflag%2F%2A&label={label}";

    internal static Task<AzureAppConfigurationSettings> Load(FakeAzureHandler handler, Action<AzureAppConfigurationOptions>? configure = null, bool connectionString = false, bool defaultRetries = false)
    {
        var options = connectionString ? new AzureAppConfigurationOptions { ConnectionString = ConnectionString } : new AzureAppConfigurationOptions { Endpoint = new(Endpoint) };
        configure?.Invoke(options);
        return AzureAppConfigurationSettings.LoadAsync(options, connectionString ? null : new FakeCredential(), handler.AppConfigurationOptions(defaultRetries), TestContext.Current.CancellationToken);
    }

    private static FakeAzureHandler Handler() => new() { Challenge = false };

    [Fact]
    public void Constants_match_the_service()
    {
        Assert.Equal("application/vnd.microsoft.appconfig.keyvaultref+json", AzureAppConfigurationSettings.KeyVaultReferenceContentType);
        Assert.Equal(".appconfig.featureflag/", AzureAppConfigurationSettings.FeatureFlagPrefix);
    }

    [Fact]
    public async Task Reads_keys_with_no_label_by_default_across_pages()
    {
        var handler = Handler()
            .Respond(KeysPath(), Page("p2", Kv("Db:Timeout", "30", contentType: "text/plain"), Kv(".appconfig.featureflag/Skip", "{}")))
            .Respond("/kv?after=p2&key=%2A&label=%00", Page(null, Kv("Db:Pool", "5")));

        var settings = await Load(handler, o => o.IncludeFeatureFlags = false);

        Assert.Equal("contoso", settings.Store);
        Assert.Equal(["Db:Timeout", "Db:Pool"], settings.Settings.Select(s => s.Key));
        var timeout = settings.Settings[0];
        Assert.Null(timeout.Label);
        Assert.Equal("no label", timeout.LabelText);
        Assert.Equal("30", timeout.Value);
        Assert.Equal("Db:Timeout", timeout.ConfigurationKey);
        Assert.Equal(AzureAppConfigurationSettingKind.KeyValue, timeout.Kind);
        Assert.Equal("text/plain", timeout.ContentType);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), timeout.LastModified);
        Assert.Equal("orders", timeout.Tags["owner"]);
        Assert.False(timeout.IsReadOnly);
        Assert.Null(timeout.SecretId);
        Assert.Null(timeout.Enabled);
        Assert.Empty(timeout.Filters);
        Assert.Empty(settings.Warnings);
        Assert.Equal([KeysPath(), "/kv?after=p2&key=%2A&label=%00"], handler.Paths);
        Assert.All(handler.Requests, r => Assert.Equal("Bearer", r.Headers.Authorization!.Scheme));
    }

    [Fact]
    public async Task Reads_each_label_in_order_with_the_key_filter_and_trims_prefixes()
    {
        var handler = Handler()
            .Respond(KeysPath(key: "Orders%3A%2A"), Page(null, Kv("Orders:Db:Timeout", "30")))
            .Respond(KeysPath("prod", "Orders%3A%2A"), Page(null, Kv("Orders:Db:Timeout", "60", "prod"), Kv("Orders:", "x", "prod"), Kv("Other:Key", "y", "locked")));

        var settings = await Load(handler, o =>
        {
            o.KeyFilter = " Orders:* ";
            o.Labels.Add("\0");
            o.Labels.Add("prod");
            o.Labels.Add("");
            o.TrimKeyPrefixes.Add("");
            o.TrimKeyPrefixes.Add("Orders:");
            o.IncludeFeatureFlags = false;
        });

        Assert.Equal([KeysPath(key: "Orders%3A%2A"), KeysPath("prod", "Orders%3A%2A")], handler.Paths);
        Assert.Equal(["30", "60"], settings.FindByConfigurationKey("db:timeout").Select(s => s.Value));
        Assert.Equal("label prod", settings.FindByConfigurationKey("Db:Timeout")[1].LabelText);
        Assert.Equal(["Orders:Db:Timeout", "Orders:Db:Timeout"], settings.Find("orders:db:timeout").Select(s => s.Key));
        Assert.Single(settings.FindByConfigurationKey("Orders:"));
        Assert.True(settings.Find("Other:Key")[0].IsReadOnly);
        Assert.Empty(settings.Find("nope"));
    }

    [Fact]
    public async Task Reads_Key_Vault_references_as_secret_URIs()
    {
        var handler = Handler().Respond(KeysPath(), Page(
            null,
            Kv("Db:Password", """{"uri":"https://contoso-prod.vault.azure.net/secrets/Db--Password"}""", contentType: KeyVaultRefType),
            Kv("Plain:Ref", """{"uri":"https://contoso-prod.vault.azure.net/secrets/Other"}""", contentType: " application/vnd.microsoft.appconfig.keyvaultref+json"),
            Kv("Bad:Json", "{not json", contentType: KeyVaultRefType),
            Kv("Bad:Uri", """{"uri":"http://contoso-prod.vault.azure.net/secrets/x"}""", contentType: KeyVaultRefType),
            Kv("Bad:Shape", """["https://x"]""", contentType: KeyVaultRefType),
            Kv("Other:Type", """{"uri":"https://x"}""", contentType: "application/vnd.microsoft.appconfig.keyvaultrefXjson")));

        var settings = await Load(handler, o => o.IncludeFeatureFlags = false);

        var reference = settings.Find("Db:Password")[0];
        Assert.Equal(AzureAppConfigurationSettingKind.KeyVaultReference, reference.Kind);
        Assert.Equal(new Uri("https://contoso-prod.vault.azure.net/secrets/Db--Password"), reference.SecretId);
        Assert.Null(reference.Value);
        Assert.Equal(AzureAppConfigurationSettingKind.KeyVaultReference, settings.Find("Plain:Ref")[0].Kind);
        Assert.Null(settings.Find("Bad:Json")[0].SecretId);
        Assert.Null(settings.Find("Bad:Uri")[0].SecretId);
        Assert.Equal(AzureAppConfigurationSettingKind.KeyValue, settings.Find("Other:Type")[0].Kind);
        Assert.Equal(
        [
            "Azure App Configuration Key Vault reference Bad:Json (no label) in contoso has no valid secret URI.",
            "Azure App Configuration Key Vault reference Bad:Uri (no label) in contoso has no valid secret URI.",
            "Azure App Configuration Key Vault reference Bad:Shape (no label) in contoso has no valid secret URI.",
        ], settings.Warnings);

        var without = await Load(handler, o =>
        {
            o.IncludeFeatureFlags = false;
            o.IncludeKeyVaultReferences = false;
        });
        Assert.Equal(["Other:Type"], without.Settings.Select(s => s.Key));
    }

    [Fact]
    public async Task Reads_feature_flags_as_FeatureManagement_keys()
    {
        var handler = Handler()
            .Respond(KeysPath(), Page(null, Kv(".appconfig.featureflag/Skip", "{}"), Kv("A", "1")))
            .Respond(FlagsPath(), Page(
                null,
                Kv(".appconfig.featureflag/Beta", """{"id":"Beta","enabled":true,"conditions":{"client_filters":[{"name":"Microsoft.Percentage"}]}}""", contentType: FeatureFlagType),
                Kv(".appconfig.featureflag/On", """{"id":" ","enabled":true,"conditions":{"client_filters":[]}}""", contentType: FeatureFlagType),
                Kv(".appconfig.featureflag/Off", """{"enabled":false,"conditions":[]}""", contentType: FeatureFlagType),
                Kv(".appconfig.featureflag/Renamed", """{"id":"NewName","enabled":"yes"}""", contentType: FeatureFlagType),
                Kv(".appconfig.featureflag/Broken", "[1,", contentType: FeatureFlagType),
                Kv(".appconfig.featureflag/Array", "[]", contentType: FeatureFlagType),
                Kv("NotAFlag", "x")));

        var settings = await Load(handler);

        Assert.Equal(["A", ".appconfig.featureflag/Beta", ".appconfig.featureflag/On", ".appconfig.featureflag/Off", ".appconfig.featureflag/Renamed", ".appconfig.featureflag/Broken", ".appconfig.featureflag/Array"], settings.Settings.Select(s => s.Key));
        var beta = settings.FindByConfigurationKey("FeatureManagement:Beta")[0];
        Assert.Equal(AzureAppConfigurationSettingKind.FeatureFlag, beta.Kind);
        Assert.True(beta.Enabled);
        Assert.Equal(["Microsoft.Percentage"], beta.Filters);
        Assert.Null(beta.Value);
        Assert.Equal("True", settings.FindByConfigurationKey("FeatureManagement:On")[0].Value);
        Assert.Equal("False", settings.FindByConfigurationKey("FeatureManagement:Off")[0].Value);
        Assert.False(settings.FindByConfigurationKey("FeatureManagement:Off")[0].Enabled);
        var renamed = settings.FindByConfigurationKey("FeatureManagement:NewName")[0];
        Assert.Null(renamed.Enabled);
        Assert.Null(renamed.Value);
        Assert.Single(settings.FindByConfigurationKey("FeatureManagement:Broken"));
        Assert.Equal(
        [
            "Azure App Configuration feature flag .appconfig.featureflag/Renamed (no label) in contoso could not be read.",
            "Azure App Configuration feature flag .appconfig.featureflag/Broken (no label) in contoso could not be read.",
            "Azure App Configuration feature flag .appconfig.featureflag/Array (no label) in contoso could not be read.",
        ], settings.Warnings);
    }

    [Fact]
    public async Task Signs_requests_with_the_connection_string_and_never_puts_the_key_in_a_url()
    {
        var handler = Handler().Respond(KeysPath(), Page(null, Kv("A", "1")));

        var settings = await Load(handler, o => o.IncludeFeatureFlags = false, connectionString: true);

        Assert.Equal("contoso", settings.Store);
        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("HMAC-SHA256", request.Headers.Authorization!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(AccessKey, request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(AccessKey, request.Headers.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Id=x;Secret=" + AccessKey, "ConnectionString has no Endpoint=... part.")]
    [InlineData("Endpoint=http://contoso.azconfig.io;Id=x;Secret=" + AccessKey, "The Endpoint in ConnectionString must be an absolute https URL.")]
    [InlineData("Endpoint=https://contoso.azconfig.io;Secret=" + AccessKey, "ConnectionString is not a valid App Configuration connection string (Endpoint=...;Id=...;Secret=...).")]
    public async Task Rejects_a_bad_connection_string_without_quoting_it(string connectionString, string message)
    {
        var options = new AzureAppConfigurationOptions { ConnectionString = connectionString };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => AzureAppConfigurationSettings.LoadAsync(options, TestContext.Current.CancellationToken));

        Assert.StartsWith(message, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://contoso.azconfig.io")]
    [InlineData("https://u:p@contoso.azconfig.io")]
    public async Task Rejects_an_endpoint_that_is_missing_or_not_https(string? endpoint)
    {
        var options = new AzureAppConfigurationOptions { Endpoint = endpoint is null ? null : new Uri(endpoint) };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => AzureAppConfigurationSettings.LoadAsync(options, new FakeCredential(), TestContext.Current.CancellationToken));

        Assert.StartsWith("Endpoint must be an absolute https URL", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => AzureAppConfigurationSettings.LoadAsync(new AzureAppConfigurationOptions { Endpoint = new Uri("x", UriKind.Relative) }, new FakeCredential(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_credential_wins_over_a_connection_string()
    {
        var handler = Handler().Respond(KeysPath(), Page(null));
        var options = new AzureAppConfigurationOptions { Endpoint = new(Endpoint), ConnectionString = ConnectionString, IncludeFeatureFlags = false };

        await AzureAppConfigurationSettings.LoadAsync(options, new FakeCredential(), handler.AppConfigurationOptions(), TestContext.Current.CancellationToken);

        Assert.Equal("Bearer", Assert.Single(handler.Requests).Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task A_refused_request_throws_the_service_error()
    {
        var handler = Handler().Respond(KeysPath(), """{"type":"https://azconfig.io/errors/forbidden","title":"Forbidden"}""", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => Load(handler));

        Assert.Equal(403, ex.Status);
        Assert.DoesNotContain(FakeCredential.Token, ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"items":{"a":1}}""")]
    public async Task A_response_that_cannot_be_read_throws_a_request_failure_that_names_the_store(string body)
    {
        var handler = Handler().Respond(KeysPath(), body);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => Load(handler));

        Assert.Equal("Azure App Configuration contoso sent a response that could not be read.", ex.Message);
    }

    [Fact]
    public async Task A_store_that_cannot_be_reached_throws_a_request_failure_that_names_the_store()
    {
        var handler = Handler().Unreachable();

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => Load(handler, defaultRetries: true));

        Assert.Equal("Could not reach Azure App Configuration contoso.", ex.Message);
        Assert.Equal(0, ex.Status);
        Assert.IsType<AggregateException>(ex.InnerException);
        Assert.Equal(new ConfigurationClientOptions().Retry.MaxRetries + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task A_store_that_never_answers_throws_a_request_failure_that_names_the_store()
    {
        var handler = Handler().Unreachable(failure: () => new TaskCanceledException("fake network timeout"));

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => Load(handler, defaultRetries: true));

        Assert.Equal("Could not reach Azure App Configuration contoso.", ex.Message);
        Assert.IsType<AggregateException>(ex.InnerException);
    }

    [Fact]
    public async Task Skips_items_without_a_key()
    {
        var handler = Handler().Respond(KeysPath(), """{"items":[{"value":"x"},{"key":"","value":"y"},{"key":"Ok","value":null}]}""");

        var settings = await Load(handler, o => o.IncludeFeatureFlags = false);

        var setting = Assert.Single(settings.Settings);
        Assert.Null(setting.Value);
    }

    [Fact]
    public async Task Stops_after_the_page_limit_with_a_warning()
    {
        var handler = Handler().Respond(KeysPath(), Page("loop", Kv("A", "1"))).Respond("/kv?after=loop&key=%2A&label=%00", Page("loop", Kv("B", "2")));

        var settings = await Load(handler, o => o.IncludeFeatureFlags = false);

        Assert.Equal(AzureAppConfigurationSettings.MaxPages, handler.Paths.Count);
        Assert.Equal([$"Stopped listing Azure App Configuration contoso keys * after {AzureAppConfigurationSettings.MaxPages} pages; some key-values were not read."], settings.Warnings);
    }

    [Fact]
    public async Task Checks_arguments()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new AzureAppConfigurationOptions { Endpoint = new(Endpoint) };
        await Assert.ThrowsAsync<ArgumentNullException>(() => AzureAppConfigurationSettings.LoadAsync(null!, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AzureAppConfigurationSettings.LoadAsync(options, (global::Azure.Core.TokenCredential)null!, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AzureAppConfigurationSettings.LoadAsync(options, null, clientOptions: null!, ct));
        Assert.Throws<ArgumentException>(() => new AzureAppConfigurationSettings(" ", []));
        Assert.Throws<ArgumentNullException>(() => new AzureAppConfigurationSettings("s", null!));
        Assert.Throws<ArgumentNullException>(() => new AzureAppConfigurationSettings("s", [null!]));
        Assert.Throws<ArgumentNullException>(() => new AzureAppConfigurationSettings("s", []).Find(null!));
        Assert.Throws<ArgumentNullException>(() => new AzureAppConfigurationSettings("s", []).FindByConfigurationKey(null!));
    }

    [Fact]
    public void Detail_describes_each_kind()
    {
        Assert.Null(AzureAppConfigurationSettings.Detail(new("k", null, "s")));
        Assert.Equal("content type text/plain", AzureAppConfigurationSettings.Detail(new("k", null, "s") { ContentType = "text/plain" }));
        Assert.Equal("Key Vault reference", AzureAppConfigurationSettings.Detail(new("k", null, "s") { Kind = AzureAppConfigurationSettingKind.KeyVaultReference }));
        Assert.Equal("Key Vault reference to https://v.vault.azure.net/secrets/a", AzureAppConfigurationSettings.Detail(new("k", null, "s") { Kind = AzureAppConfigurationSettingKind.KeyVaultReference, SecretId = new("https://v.vault.azure.net/secrets/a") }));
        Assert.Equal("feature flag, on", AzureAppConfigurationSettings.Detail(new("k", null, "s") { Kind = AzureAppConfigurationSettingKind.FeatureFlag, Enabled = true }));
        Assert.Equal("feature flag, off", AzureAppConfigurationSettings.Detail(new("k", null, "s") { Kind = AzureAppConfigurationSettingKind.FeatureFlag }));
    }
}
