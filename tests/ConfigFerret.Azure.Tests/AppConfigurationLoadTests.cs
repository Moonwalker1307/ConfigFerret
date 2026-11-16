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
    public async Task Trims_the_longest_matching_prefix_and_ignores_case_like_the_provider()
    {
        var handler = Handler().Respond(KeysPath(), Page(null, Kv("Orders:Db:Timeout", "30"), Kv("Shop:Db:Timeout", "5")));

        var settings = await Load(handler, o =>
        {
            o.TrimKeyPrefixes.Add("orders:");
            o.TrimKeyPrefixes.Add("Shop:");
            o.TrimKeyPrefixes.Add("Shop:Db:");
            o.IncludeFeatureFlags = false;
        });

        Assert.Equal("Orders:Db:Timeout", Assert.Single(settings.FindByConfigurationKey("Db:Timeout")).Key);
        Assert.Equal("Shop:Db:Timeout", Assert.Single(settings.FindByConfigurationKey("Timeout")).Key);
    }

    [Fact]
    public async Task Flattens_JSON_key_values_like_the_provider()
    {
        const string retry = """{"Count":3,"Backoff":{"Seconds":1.5},"Codes":[500,503],"On":true,"None":null,"Empty":{},"List":[]}""";
        var handler = Handler().Respond(KeysPath(), Page(
            null,
            Kv("Orders:Retry", retry, contentType: "application/json"),
            Kv("Orders:Name", "\"abc\"", contentType: "Application/JSON; charset=utf-8"),
            Kv("Orders:Custom", """{"A":1}""", contentType: "application/vnd.contoso+json"),
            Kv("Orders:Suffixed", """{"A":1}""", contentType: "application/json+x"),
            Kv("Orders:Middle", """{"A":1}""", contentType: "application/x+json+y"),
            Kv("Orders:Commented", "{ // a comment\n \"A\": 1 /* another */ }", contentType: "application/json"),
            Kv("Orders:Unnamed", """{"":7}""", contentType: "application/json"),
            Kv("Orders:Nothing", "{}", contentType: "application/json"),
            Kv("Orders:Bad", "{not json", contentType: "application/json"),
            Kv("Orders:TextJson", """{"A":1}""", contentType: "text/json"),
            Kv("Orders:Jsonx", """{"A":1}""", contentType: "application/jsonx"),
            Kv("Orders:Plain", """{"A":1}""", contentType: "text/plain")));

        var settings = await Load(handler, o =>
        {
            o.TrimKeyPrefixes.Add("Orders:Retry:");
            o.IncludeFeatureFlags = false;
        });

        string? Value(string key) => Assert.Single(settings.FindByConfigurationKey(key)).Value;
        Assert.Equal("3", Value("Count"));
        Assert.Equal("1.5", Value("Backoff:Seconds"));
        Assert.Equal("500", Value("Codes:0"));
        Assert.Equal("503", Value("Codes:1"));
        Assert.Equal("True", Value("On"));
        Assert.Equal(string.Empty, Value("None"));
        Assert.Empty(settings.FindByConfigurationKey("Empty"));
        Assert.Empty(settings.FindByConfigurationKey("List"));
        Assert.Empty(settings.FindByConfigurationKey("Orders:Retry"));
        Assert.Equal("abc", Value("Orders:Name"));
        Assert.Equal("1", Value("Orders:Custom:A"));
        Assert.Equal("1", Value("Orders:Suffixed:A"));
        Assert.Equal("1", Value("Orders:Middle:A"));
        Assert.Equal("1", Value("Orders:Commented:A"));
        Assert.Equal("7", Value("Orders:Unnamed:"));
        Assert.Empty(settings.FindByConfigurationKey("Orders:Nothing"));
        Assert.Equal("{not json", Value("Orders:Bad"));
        Assert.Equal("""{"A":1}""", Value("Orders:TextJson"));
        Assert.Equal("""{"A":1}""", Value("Orders:Jsonx"));
        Assert.Equal("""{"A":1}""", Value("Orders:Plain"));
        Assert.Empty(settings.Warnings);

        // Find, Settings and the loaded source keep one setting per stored key-value, with its stored value.
        Assert.Equal(retry, Assert.Single(settings.Find("Orders:Retry")).Value);
        Assert.Equal(12, settings.Settings.Count);
        Assert.Equal(settings.Settings.Select(s => s.Key), settings.ToLoadedSource().Entries.Select(e => e.Name));

        var step = Assert.Single(new AzureAppConfigurationResolver(settings, new()).Resolve(TestLayers.Provider("AzureAppConfigurationProvider", "Count", "3"), new TraceDiagnostics()));
        Assert.Equal("Azure App Configuration key Orders:Retry (contoso)", step.Source);
        Assert.Equal("read by the App Configuration provider (matched by key)", step.Note);
    }

    [Fact]
    public async Task Links_a_name_to_a_JSON_key_value_with_one_step_holding_its_stored_value()
    {
        const string retry = """{"Count":3,"Delay":5}""";
        var handler = Handler().Respond(KeysPath(), Page(null, Kv("Orders:Retry", retry, contentType: "application/json")));
        var settings = await Load(handler, o => o.IncludeFeatureFlags = false);

        var linked = Assert.Single(new AzureAppConfigurationResolver(settings, new AzureAppConfigurationTraceOptions().Map("RETRY", "Orders:Retry")).Resolve(TestLayers.Env("RETRY", retry), new TraceDiagnostics()));
        var named = Assert.Single(new AzureAppConfigurationResolver(settings, new()).Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "c", Value = retry, Reference = new ConfigMapKeyRef("cm", "Orders:Retry") }, new TraceDiagnostics()));
        var leaf = Assert.Single(new AzureAppConfigurationResolver(settings, new()).Resolve(TestLayers.Env("Orders__Retry__Count", "3"), new TraceDiagnostics()));

        Assert.Equal(retry, linked.Value);
        Assert.Null(linked.Note);
        Assert.Equal(retry, named.Value);
        Assert.Equal("matched by name", named.Note);
        Assert.Equal("3", leaf.Value);
        Assert.Equal("matched by configuration key", leaf.Note);
    }

    [Fact]
    public async Task Matches_a_name_to_a_JSON_key_value_by_its_own_configuration_key()
    {
        const string retry = """{"Count":3}""";
        var handler = Handler()
            .Respond(KeysPath("base"), Page(
                null,
                Kv("App:Retry", retry, "base", "application/json"),
                Kv("Orders:Retry", retry, "base", "application/json"),
                Kv("Orders:Name", "\"abc\"", "base", "application/json"),
                Kv("Orders:Mode", """{"A":1}""", "base", "application/json")))
            .Respond(KeysPath("prod"), Page(null, Kv("Orders:Mode", "x", "prod")));
        var settings = await Load(handler, o =>
        {
            o.Labels.Add("base");
            o.Labels.Add("prod");
            o.TrimKeyPrefixes.Add("App:");
            o.IncludeFeatureFlags = false;
        });
        var resolver = new AzureAppConfigurationResolver(settings, new());
        IReadOnlyList<ProvenanceStep> Resolve(string name, string value) => resolver.Resolve(TestLayers.Env(name, value), new TraceDiagnostics());

        var trimmed = Assert.Single(Resolve("Retry", retry));
        Assert.Equal("Azure App Configuration key App:Retry (contoso)", trimmed.Source);
        Assert.Equal(retry, trimmed.Value);
        Assert.Equal("matched by configuration key", trimmed.Note);
        Assert.Equal(retry, Assert.Single(Resolve("Orders__Retry", retry)).Value);

        // A JSON string sets its own key once; a key set by a JSON key-value in one label and a plain one in another
        // lists both, lowest priority first.
        Assert.Equal("abc", Assert.Single(Resolve("Orders__Name", "abc")).Value);
        Assert.Equal(["""{"A":1}""", "x"], Resolve("Orders__Mode", "x").Select(s => s.Value));

        // FindByConfigurationKey keeps giving only the keys the provider sets.
        Assert.Empty(settings.FindByConfigurationKey("Retry"));
        Assert.Single(settings.FindByConfigurationKey("Retry:Count"));
    }

    [Fact]
    public async Task Links_a_name_to_a_large_JSON_key_value_in_two_labels_without_cutting_the_trace()
    {
        var retry = "[" + string.Join(",", Enumerable.Repeat("1", 600)) + "]";
        var handler = Handler()
            .Respond(KeysPath("base"), Page(null, Kv("Orders:Retry", retry, "base", "application/json")))
            .Respond(KeysPath("prod"), Page(null, Kv("Orders:Retry", retry, "prod", "application/json")));
        var settings = await Load(handler, o =>
        {
            o.Labels.Add("base");
            o.Labels.Add("prod");
            o.IncludeFeatureFlags = false;
        });
        var variables = new Dictionary<string, string?> { ["RETRY"] = retry };
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Add(new FakeEnvironmentVariables(variables)).Build();

        var report = ConfigTracer.For(configuration)
            .WithOptions(o => o.Environment = variables)
            .WithAzureAppConfiguration(settings, o => o.Map("RETRY", "Orders:Retry"))
            .Trace();

        Assert.Equal(["label base", "label prod"], report.Find("RETRY")!.EffectiveStep!.Origins.Select(o => o.Location));
        Assert.DoesNotContain(report.Warnings, w => w.StartsWith("Stopped following", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bounds_the_elements_read_from_one_JSON_key_value_and_its_depth()
    {
        var many = "[" + string.Join(",", Enumerable.Range(0, AzureAppConfigurationSettings.MaxJsonValues + 1)) + "]";
        var nested = "[" + string.Join(",", Enumerable.Repeat("[]", AzureAppConfigurationSettings.MaxJsonValues + 1)) + "]";
        var deepest = new string('[', 63) + "1" + new string(']', 63);
        var tooDeep = new string('[', 64) + "1" + new string(']', 64);
        var handler = Handler().Respond(KeysPath(), Page(
            null,
            Kv("Many", many, contentType: "application/json"),
            Kv("Nested", nested, contentType: "application/json"),
            Kv("Deepest", deepest, contentType: "application/json"),
            Kv("TooDeep", tooDeep, contentType: "application/json")));

        var settings = await Load(handler, o => o.IncludeFeatureFlags = false);

        Assert.Single(settings.FindByConfigurationKey($"Many:{AzureAppConfigurationSettings.MaxJsonValues - 1}"));
        Assert.Empty(settings.FindByConfigurationKey($"Many:{AzureAppConfigurationSettings.MaxJsonValues}"));
        Assert.Empty(settings.FindByConfigurationKey("Nested"));
        Assert.Equal(nested, Assert.Single(settings.Find("Nested")).Value);
        Assert.Equal("1", Assert.Single(settings.FindByConfigurationKey("Deepest" + string.Concat(Enumerable.Repeat(":0", 63)))).Value);
        Assert.Equal(tooDeep, Assert.Single(settings.FindByConfigurationKey("TooDeep")).Value);
        Assert.Equal(
        [
            "Azure App Configuration key Many (no label) in contoso holds more than 1,000 JSON elements; only the first 1,000 are traced.",
            "Azure App Configuration key Nested (no label) in contoso holds more than 1,000 JSON elements; only the first 1,000 are traced.",
        ], settings.Warnings);
    }

    [Fact]
    public async Task Stops_flattening_JSON_key_values_after_the_load_limit()
    {
        static string Array(int count) => "[" + string.Join(",", Enumerable.Repeat("1", count)) + "]";
        var half = AzureAppConfigurationSettings.MaxJsonValues / 2;
        var full = AzureAppConfigurationSettings.MaxJsonValuesPerLoad / AzureAppConfigurationSettings.MaxJsonValues - 1;
        var items = Enumerable.Range(0, full).Select(i => Kv($"K{i}", Array(AzureAppConfigurationSettings.MaxJsonValues), contentType: "application/json"))
            .Prepend(Kv("Half", Array(half), contentType: "application/json"))
            .Append(Kv("Cut", Array(AzureAppConfigurationSettings.MaxJsonValues), contentType: "application/json"))
            .Append(Kv("Last", """{"A":1}""", contentType: "application/json"));
        var handler = Handler().Respond(KeysPath(), Page(null, [.. items]));

        var settings = await Load(handler, o => o.IncludeFeatureFlags = false);

        Assert.Single(settings.FindByConfigurationKey($"K{full - 1}:0"));
        Assert.Single(settings.FindByConfigurationKey($"Cut:{half - 1}"));
        Assert.Empty(settings.FindByConfigurationKey($"Cut:{half}"));
        Assert.Equal("""{"A":1}""", Assert.Single(settings.FindByConfigurationKey("Last")).Value);
        Assert.Equal(["Stopped flattening JSON key-values from Azure App Configuration contoso after 100,000 values; the rest are traced as they are stored."], settings.Warnings);
    }

    [Fact]
    public async Task Stops_flattening_JSON_key_values_whose_keys_take_too_many_characters()
    {
        var big = $$"""{"{{new string('a', 100_000)}}":[{{string.Join(",", Enumerable.Repeat("0", 2_000))}}]}""";
        var handler = Handler().Respond(KeysPath(), Page(null, Kv("Big", big, contentType: "application/json"), Kv("After", """{"A":1}""", contentType: "application/json")));

        var settings = await Load(handler, o => o.IncludeFeatureFlags = false);

        Assert.Equal(big, Assert.Single(settings.FindByConfigurationKey("Big")).Value);
        Assert.Equal("""{"A":1}""", Assert.Single(settings.FindByConfigurationKey("After")).Value);
        Assert.Equal(["Stopped flattening JSON key-values from Azure App Configuration contoso at key Big (no label): the flattened keys would pass 10,000,000 characters, so it and the rest are traced as they are stored."], settings.Warnings);
    }

    [Fact]
    public void Flattening_a_long_name_over_many_items_allocates_a_bounded_amount()
    {
        var big = $$"""{"{{new string('a', 100_000)}}":[{{string.Join(",", Enumerable.Repeat("0", 2_000))}}]}""";
        var whole = new AzureAppConfigurationSetting("Big", null, "contoso") { Value = big, ContentType = "application/json" };
        var warnings = new List<string>();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var values = AzureAppConfigurationSettings.Flatten(whole, big, [], new AzureAppConfigurationSettings.JsonBudget(), warnings);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(values);
        Assert.Single(warnings);
        // The key budget is 10 million characters (20 MB); building every key would take 400 MB.
        Assert.InRange(allocated, 0, 40_000_000);
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
    public async Task Reads_feature_flags_with_no_label_whatever_the_labels_of_the_keys()
    {
        var handler = Handler()
            .Respond(KeysPath("production"), Page(null, Kv("A", "1", "production")))
            .Respond(FlagsPath(), Page(null, Kv(".appconfig.featureflag/Beta", """{"id":"Beta","enabled":true}""", contentType: FeatureFlagType)));

        var settings = await Load(handler, o => o.Labels.Add("production"));

        Assert.Equal([KeysPath("production"), FlagsPath()], handler.Paths);
        Assert.Equal("True", Assert.Single(settings.FindByConfigurationKey("FeatureManagement:Beta")).Value);
    }

    [Fact]
    public async Task Reads_feature_flags_with_the_labels_given_for_them()
    {
        var handler = Handler()
            .Respond(KeysPath(), Page(null))
            .Respond(FlagsPath("base"), Page(null, Kv(".appconfig.featureflag/Beta", """{"id":"Beta","enabled":false}""", "base", FeatureFlagType)))
            .Respond(FlagsPath("production"), Page(null, Kv(".appconfig.featureflag/Beta", """{"id":"Beta","enabled":true}""", "production", FeatureFlagType)));

        var settings = await Load(handler, o =>
        {
            o.FeatureFlagLabels.Add("base");
            o.FeatureFlagLabels.Add("production");
        });

        Assert.Equal([KeysPath(), FlagsPath("base"), FlagsPath("production")], handler.Paths);
        Assert.Equal(["False", "True"], settings.FindByConfigurationKey("FeatureManagement:Beta").Select(s => s.Value));
    }

    [Fact]
    public async Task Trims_the_configuration_keys_of_feature_flags_like_those_of_key_values()
    {
        var handler = Handler()
            .Respond(KeysPath(), Page(null))
            .Respond(FlagsPath(), Page(
                null,
                Kv(".appconfig.featureflag/Beta", """{"id":"Beta","enabled":true}""", contentType: FeatureFlagType),
                Kv(".appconfig.featureflag/Gamma", """{"id":"Gamma","enabled":true,"conditions":{"client_filters":[{"name":"Microsoft.Percentage"}]}}""", contentType: FeatureFlagType)));

        var settings = await Load(handler, o => o.TrimKeyPrefixes.Add("FeatureManagement:"));
        var resolver = new AzureAppConfigurationResolver(settings, new());
        IReadOnlyList<ProvenanceStep> Resolve(string key, string value) =>
            resolver.Resolve(TestLayers.Provider("AzureAppConfigurationProvider", key, value), new TraceDiagnostics());

        var beta = Assert.Single(settings.FindByConfigurationKey("Beta"));
        Assert.Equal(AzureAppConfigurationSettingKind.FeatureFlag, beta.Kind);
        Assert.Empty(settings.FindByConfigurationKey("FeatureManagement:Beta"));
        Assert.Equal("Azure App Configuration feature flag Beta (contoso)", Assert.Single(Resolve("Beta", "True")).Source);

        // A flag with filters sets keys under its own, and they still lead to the flag; other keys under a flag's key do not.
        Assert.Equal("Azure App Configuration feature flag Gamma (contoso)", Assert.Single(Resolve("Gamma:EnabledFor:0:Name", "Microsoft.Percentage")).Source);
        Assert.Equal("Azure App Configuration feature flag Gamma (contoso)", Assert.Single(Resolve("gamma:enabledfor:0:Parameters:Value", "50")).Source);
        Assert.Equal("Azure App Configuration feature flag Gamma (contoso)", Assert.Single(Resolve("Gamma:RequirementType", "All")).Source);
        Assert.Empty(Resolve("Delta:EnabledFor:0:Name", "Microsoft.Percentage"));
        Assert.Empty(Resolve("Beta:Timeout", "30"));
        Assert.Empty(Resolve("Beta:Timeout:EnabledFor", "30"));
        Assert.Empty(Resolve("Beta:EnabledForever", "30"));
    }

    [Fact]
    public async Task Leads_only_the_keys_the_provider_writes_under_a_flag_to_that_flag()
    {
        static string Flag(string id, bool enabled, bool filters) =>
            "{\"id\":\"" + id + "\",\"enabled\":" + (enabled ? "true" : "false") + ",\"conditions\":{\"client_filters\":["
            + (filters ? """{"name":"Microsoft.Percentage"}""" : string.Empty) + "]}}";
        var handler = Handler()
            .Respond(KeysPath(), Page(null))
            .Respond(FlagsPath(), Page(
                null,
                Kv(".appconfig.featureflag/X", Flag("X", true, true), contentType: FeatureFlagType),
                Kv(".appconfig.featureflag/X:EnabledFor", Flag("X:EnabledFor", true, true), contentType: FeatureFlagType),
                Kv(".appconfig.featureflag/Beta", Flag("Beta", true, true), contentType: FeatureFlagType),
                Kv(".appconfig.featureflag/Off", Flag("Off", false, true), contentType: FeatureFlagType),
                Kv(".appconfig.featureflag/On", Flag("On", true, false), contentType: FeatureFlagType)));
        var settings = await Load(handler, o => o.TrimKeyPrefixes.Add("FeatureManagement:"));
        var resolver = new AzureAppConfigurationResolver(settings, new());
        string[] Flags(string key) =>
            [.. resolver.Resolve(TestLayers.Provider("AzureAppConfigurationProvider", key, "v"), new TraceDiagnostics()).Select(s => s.Source)];
        static string Named(string name) => $"Azure App Configuration feature flag {name} (contoso)";

        Assert.Equal([Named("X:EnabledFor")], Flags("X:EnabledFor:EnabledFor:0:Name"));
        Assert.Equal([Named("Beta")], Flags("Beta:EnabledFor:0:Name"));
        Assert.Equal([Named("Beta")], Flags("Beta:EnabledFor:12:Parameters:Value"));
        Assert.Equal([Named("Beta")], Flags("Beta:requirementtype"));
        Assert.Empty(Flags("Beta:EnabledFor"));
        Assert.Empty(Flags("Beta:EnabledFor:x"));
        Assert.Empty(Flags("Beta:EnabledFor:0"));
        Assert.Empty(Flags("Beta:EnabledFor:0:Other"));
        Assert.Empty(Flags("Beta:EnabledFor:0:Name:More"));
        Assert.Empty(Flags("Beta:RequirementType:x"));

        // Only a flag that is on and has a filter has such keys.
        Assert.Empty(Flags("Off:EnabledFor:0:Name"));
        Assert.Empty(Flags("On:EnabledFor:0:Name"));
        Assert.Empty(Flags("On:RequirementType"));

        // A prefix without its colon leaves flag keys that start with one.
        handler = Handler()
            .Respond(KeysPath(), Page(null))
            .Respond(FlagsPath(), Page(null, Kv(".appconfig.featureflag/Beta", Flag("Beta", true, true), contentType: FeatureFlagType)));
        settings = await Load(handler, o => o.TrimKeyPrefixes.Add("FeatureManagement"));
        resolver = new AzureAppConfigurationResolver(settings, new());
        Assert.Equal([Named("Beta")], Flags(":Beta:EnabledFor:0:Name"));
        Assert.Empty(Flags(":"));
        Assert.Empty(Flags(string.Empty));
    }

    [Fact]
    public void Walks_up_a_long_key_to_its_flag_without_copying_the_key()
    {
        var flag = new AzureAppConfigurationSetting(".appconfig.featureflag/Z", null, "s")
        {
            Kind = AzureAppConfigurationSettingKind.FeatureFlag,
            ConfigurationKey = new string('Z', 100_000),
            Enabled = true,
            Filters = ["Microsoft.Percentage"],
        };
        var resolver = new AzureAppConfigurationResolver(new AzureAppConfigurationSettings("s", [flag]), new());
        // Every colon here is followed by EnabledFor:0:Parameters:, so each one could end a flag's key.
        var key = "F" + string.Concat(Enumerable.Repeat(":EnabledFor:0:Parameters", 2_000));
        resolver.Resolve(TestLayers.Provider("AzureAppConfigurationProvider", "Warm", "v"), new TraceDiagnostics());

        var before = GC.GetAllocatedBytesForCurrentThread();
        var steps = resolver.Resolve(TestLayers.Provider("AzureAppConfigurationProvider", key, "v"), new TraceDiagnostics());
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(steps);
        // Copying the key up to each of those colons would take about 96 MB.
        Assert.InRange(allocated, 0, 1_000_000);
        Assert.Equal(
            "Azure App Configuration feature flag Z (s)",
            Assert.Single(resolver.Resolve(TestLayers.Provider("AzureAppConfigurationProvider", flag.ConfigurationKey + ":EnabledFor:0:Name", "v"), new TraceDiagnostics())).Source);
    }

    [Fact]
    public async Task Names_a_feature_flag_by_its_stored_key_whatever_prefix_is_trimmed()
    {
        var handler = Handler()
            .Respond(KeysPath(), Page(null, Kv("Plain", "1")))
            .Respond(FlagsPath(), Page(null, Kv(".appconfig.featureflag/Beta", """{"id":"Beta","enabled":true}""", contentType: FeatureFlagType)));

        var settings = await Load(handler, o => o.TrimKeyPrefixes.Add("Feature"));

        var step = Assert.Single(new AzureAppConfigurationResolver(settings, new()).Resolve(TestLayers.Provider("AzureAppConfigurationProvider", "Management:Beta", "True"), new TraceDiagnostics()));
        Assert.Equal("Azure App Configuration feature flag Beta (contoso)", step.Source);
        // Beta has no filters, so no key under it leads to it, and there is no flag key length to try.
        Assert.Empty(settings.FlagKeyLengths);
        Assert.Empty(new AzureAppConfigurationSettings("s", [new("Plain", null, "s")]).FlagKeyLengths);
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
