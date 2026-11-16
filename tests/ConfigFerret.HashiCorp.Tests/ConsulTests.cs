using System.Net;
using System.Text;
using ConfigFerret.Sources;
using ConfigFerret.Tests.Shared;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.HashiCorp.Tests;

public class ConsulTests
{
    private const string Token = "consul-test-token-never-printed";
    private const string Path = "/v1/kv/config/orders/?recurse=true";

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string Kv(params (string Key, string? Value)[] items) =>
        "[" + string.Join(",", items.Select((i, n) => $$"""{"Key":"{{i.Key}}","Value":{{(i.Value is null ? "null" : $"\"{B64(i.Value)}\"")}},"Flags":0,"CreateIndex":1,"ModifyIndex":{{n + 10}},"LockIndex":0}""")) + "]";

    private static ConsulOptions Options(Action<ConsulOptions>? configure = null)
    {
        var options = new ConsulOptions { Address = new Uri("https://consul.example.com:8501/"), Token = Token, Prefix = "config/orders/" };
        configure?.Invoke(options);
        return options;
    }

    private static async Task<ConsulKeyValues> Load(FakeHttpHandler handler, Action<ConsulOptions>? configure = null)
    {
        using var client = handler.Client();
        return await ConsulKeyValues.LoadAsync(Options(configure), client, TestContext.Current.CancellationToken);
    }

    private static FakeHttpHandler Orders(string path = Path) => new FakeHttpHandler().Respond(path, Kv(
        ("config/orders/", null),
        ("config/orders/Db/", null),
        ("config/orders/Db/Timeout", "30"),
        ("config/orders/Feature", "{\"Flags\":{\"New\":true,\"Limit\":5,\"Name\":\"x\",\"Off\":null},\"List\":[\"a\",{}],\"Empty\":[]}"),
        ("config/orders/Broken", "{not json"),
        ("config/orders/NullValue", null)));

    private static ProvenanceStep EnvStep(string name, string? value = null) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = $"environment variable {name}",
        Value = value,
        Reference = new EnvironmentVariableRef(name),
    };

    [Fact]
    public async Task Reads_keys_under_the_prefix_and_maps_them_to_configuration_keys()
    {
        var handler = Orders();

        var consul = await Load(handler);

        Assert.Equal(["Db:Timeout", "Feature", "Broken", "NullValue"], consul.Entries.Select(e => e.ConfigurationKey));
        var timeout = Assert.Single(consul.Find("db:timeout"));
        Assert.Equal(new ConsulEntry("config/orders/Db/Timeout", "Db:Timeout", "30", 12), timeout);
        Assert.Null(consul.Find("NullValue")[0].Value);
        Assert.Empty(consul.Find("Missing"));
        Assert.Equal("consul.example.com:8501", consul.Location);
        Assert.Equal("config/orders/", consul.Prefix);
        Assert.False(consul.IsSensitive);
        Assert.Empty(consul.Warnings);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(Token, Assert.Single(request.Headers.GetValues("X-Consul-Token")));
        Assert.DoesNotContain(Token, request.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expands_JSON_values_like_the_JSON_provider_when_asked()
    {
        var consul = await Load(Orders(), o => o.ExpandJson = true);

        Assert.Equal(
            ["Db:Timeout", "Feature:Flags:New", "Feature:Flags:Limit", "Feature:Flags:Name", "Feature:Flags:Off", "Feature:List:0", "Feature:List:1", "Feature:Empty", "Broken", "NullValue"],
            consul.Entries.Select(e => e.ConfigurationKey));
        Assert.Equal("True", consul.Find("Feature:Flags:New")[0].Value);
        Assert.Equal("5", consul.Find("Feature:Flags:Limit")[0].Value);
        Assert.Null(consul.Find("Feature:Flags:Off")[0].Value);
        Assert.Null(consul.Find("Feature:Empty")[0].Value);
        Assert.Equal("Flags:Name", consul.Find("Feature:Flags:Name")[0].JsonPath);
        Assert.Equal("config/orders/Feature", consul.Find("Feature:Flags:Name")[0].Key);
        Assert.Equal("{not json", consul.Find("Broken")[0].Value);
    }

    [Fact]
    public async Task Expands_a_JSON_value_at_the_root_when_the_prefix_is_the_key_itself()
    {
        var handler = new FakeHttpHandler().Respond("/v1/kv/config/orders/appsettings?recurse=true", Kv(("config/orders/appsettings", "[1]"), ("config/orders/appsettingsX", "{\"A\":{\"B\":\"c\"}}")))
            .Respond("/v1/kv/config/orders/plain?recurse=true", Kv(("config/orders/plain", "v"), ("config/orders/plain/", "x")));

        var consul = await Load(handler, o =>
        {
            o.Prefix = "config/orders/appsettings";
            o.ExpandJson = true;
        });
        Assert.Equal(["0", "X:A:B"], consul.Entries.Select(e => e.ConfigurationKey));

        var plain = await Load(handler, o => o.Prefix = "/config/orders/plain");
        Assert.Equal(["plain", "plain"], plain.Entries.Select(e => e.ConfigurationKey));
    }

    [Fact]
    public async Task Sends_the_datacenter_and_reads_the_whole_store_without_a_prefix()
    {
        var handler = new FakeHttpHandler().Respond("/v1/kv/?recurse=true&dc=eu%20west", Kv(("a/b", "1")));

        var consul = await Load(handler, o =>
        {
            o.Prefix = "";
            o.Datacenter = " eu west ";
            o.Token = null;
        });

        Assert.Equal("a:b", Assert.Single(consul.Entries).ConfigurationKey);
        Assert.Equal("eu west", consul.Datacenter);
        Assert.Equal("consul.example.com:8501 datacenter eu west", consul.Location);
        Assert.False(handler.Requests[0].Headers.Contains("X-Consul-Token"));
    }

    [Fact]
    public async Task Keeps_keys_outside_the_prefix_under_their_full_name()
    {
        var consul = await Load(new FakeHttpHandler().Respond(Path, Kv(("other/x", "1"))));

        Assert.Equal("other:x", Assert.Single(consul.Entries).ConfigurationKey);
    }

    [Fact]
    public async Task Warns_when_the_prefix_has_no_keys()
    {
        var consul = await Load(new FakeHttpHandler());

        Assert.Empty(consul.Entries);
        Assert.Equal(["Consul has no keys under prefix 'config/orders/'."], consul.Warnings);
    }

    [Fact]
    public async Task Fails_on_an_ACL_error_without_the_token_in_the_message()
    {
        var handler = new FakeHttpHandler().Respond(Path, "Permission denied: token with AccessorID '1' lacks permission", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Load(handler));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"Key":"a"}""")]
    [InlineData("""null""")]
    [InlineData("""42""")]
    public async Task Warns_about_a_response_that_is_not_an_array(string body)
    {
        var consul = await Load(new FakeHttpHandler().Respond(Path, body));

        Assert.Equal(["Consul returned an unexpected response for prefix 'config/orders/': not an array of keys."], consul.Warnings);
    }

    [Fact]
    public async Task Skips_odd_entries_with_warnings()
    {
        var body = """
            [
              {"Key":"config/orders/ok","Value":"MQ==","ModifyIndex":"x"},
              {"Key":""}, {"Value":"MQ=="}, 7, {"Key":null}, {"Key":"\ud800"}, {"Key":"//","Value":"MQ=="},
              {"Key":"config/orders/bad64","Value":"***"},
              {"Key":"config/orders/num","Value":5},
              {"Key":"config/orders/binary","Value":"/w=="}
            ]
            """;

        var consul = await Load(new FakeHttpHandler().Respond(Path, body));

        var ok = Assert.Single(consul.Entries);
        Assert.Equal("1", ok.Value);
        Assert.Null(ok.ModifyIndex);
        Assert.Equal(["Skipped 6 Consul entries that have no key, or no name once the prefix is stripped.", "Skipped 3 Consul values that are not base64-encoded UTF-8 text."], consul.Warnings);
    }

    [Fact]
    public async Task Reads_past_property_names_that_are_not_valid_UTF16()
    {
        var body = """[{"\ud800":1,"Key":"config/orders/a","Value":"e30="},{"Key":"config/orders/b","Value":"eyJcdWQ4MDAiOjEsIngiOjJ9"}]""";

        var consul = await Load(new FakeHttpHandler().Respond(Path, body), o => o.ExpandJson = true);

        Assert.Equal(["a", "b:x"], consul.Entries.Select(e => e.ConfigurationKey));
    }

    [Fact]
    public async Task Stops_at_the_key_cap_also_inside_a_JSON_value()
    {
        var many = Kv([.. Enumerable.Range(0, 5).Select(i => ($"config/orders/k{i}", "v"))]);
        var capped = await Load(new FakeHttpHandler().Respond(Path, many), o => o.MaxKeys = 3);
        Assert.Equal(3, capped.Entries.Count);
        Assert.Contains("Stopped reading Consul after 3 keys (MaxKeys).", capped.Warnings);

        var json = Kv(("config/orders/big", "[" + string.Join(",", Enumerable.Range(0, 10)) + "]"), ("config/orders/after", "x"));
        var expanded = await Load(new FakeHttpHandler().Respond(Path, json), o =>
        {
            o.MaxKeys = 4;
            o.ExpandJson = true;
        });
        Assert.Equal(4, expanded.Entries.Count);
        Assert.Contains("Stopped reading Consul after 4 keys (MaxKeys).", expanded.Warnings);
    }

    [Fact]
    public async Task Checks_its_arguments()
    {
        using var client = new FakeHttpHandler().Client();
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentNullException>(() => ConsulKeyValues.LoadAsync(null!, client, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ConsulKeyValues.LoadAsync(Options(), null!, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ConsulKeyValues.LoadAsync(Options(o => o.Address = new Uri("http://consul.example.com/")), client, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ConsulKeyValues.LoadAsync(Options(o => o.Address = new Uri("http://consul.example.com/")), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ConsulKeyValues.LoadAsync(Options(o => o.Prefix = "a/../b"), client, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ConsulKeyValues.LoadAsync(Options(o => o.Prefix = "a//b"), client, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ConsulKeyValues.LoadAsync(Options(o => o.Prefix = "a/.."), client, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ConsulKeyValues.LoadAsync(Options(o => o.MaxKeys = 0), client, ct));
        var token = await Assert.ThrowsAsync<ArgumentException>(() => ConsulKeyValues.LoadAsync(Options(o => o.Token = Token + "\n"), client, ct));
        Assert.DoesNotContain(Token, token.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentNullException>(() => new ConsulKeyValues(null!));
        Assert.Throws<ArgumentNullException>(() => new ConsulKeyValues([null!]));
        Assert.Throws<ArgumentNullException>(() => new ConsulKeyValues([]).Find(null!));
        Assert.Equal("Consul", new ConsulKeyValues([], prefix: null!).Location);
        Assert.Equal("Consul datacenter dc1", new ConsulKeyValues([], datacenter: "dc1").Location);
        Assert.Equal(string.Empty, new ConsulKeyValues([], prefix: null!).Prefix);
        Assert.Null(new ConsulKeyValues([], datacenter: " ").Datacenter);
    }

    private static readonly ConsulKeyValues Values = new(
    [
        new("config/orders/Db/Timeout", "Db:Timeout", "30", 12),
        new("config/orders/Feature", "Feature:Flags:New", "True", 13, "Flags:New"),
        new("config/orders/Db/Timeout2", "db:timeout", "31"),
    ],
    "config/orders/",
    "dc1",
    "consul.example.com:8501 datacenter dc1");

    private static IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, Action<ConsulTraceOptions>? configure = null, TraceDiagnostics? diagnostics = null, ConsulKeyValues? values = null)
    {
        var options = new ConsulTraceOptions();
        configure?.Invoke(options);
        return new ConsulResolver(values ?? Values, options).Resolve(layer, diagnostics ?? new TraceDiagnostics());
    }

    [Fact]
    public void Matches_by_configuration_key_in_Consul_order()
    {
        var steps = Resolve(EnvStep("DB__TIMEOUT", "30"));

        Assert.Equal(["30", "31"], steps.Select(s => s.Value));
        var step = steps[0];
        Assert.Equal(LayerKind.Consul, step.Kind);
        Assert.Equal("Consul key config/orders/Db/Timeout", step.Source);
        Assert.Equal("consul.example.com:8501 datacenter dc1", step.Location);
        Assert.False(step.IsSensitive);
        Assert.True(step.IsInferred);
        Assert.Equal("matched by name; modify index 12", step.Note);
        Assert.Equal(new ConsulKeyRef("dc1", "config/orders/Db/Timeout"), step.Reference);
        Assert.Contains("differs", steps[1].Note, StringComparison.Ordinal);
        Assert.DoesNotContain("31", steps[1].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_an_expanded_JSON_leaf_and_says_where_it_is()
    {
        var step = Assert.Single(Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "c", Reference = new ConfigMapKeyRef("cm", "Feature__Flags__New") }));

        Assert.Equal("matched by name; from Flags:New in the key's JSON value; modify index 13", step.Note);
        Assert.Equal(new ConsulKeyRef("dc1", "config/orders/Feature", "Flags:New"), step.Reference);
    }

    [Fact]
    public void Strips_prefixes_matches_Consul_provider_layers_and_ignores_others()
    {
        Assert.Equal(2, Resolve(EnvStep("APP_Db__Timeout"), o => o.StripPrefixes.Add("APP_")).Count);
        Assert.Empty(Resolve(EnvStep("APP_Db__Timeout")));
        Assert.Empty(Resolve(EnvStep("Db__Timeout"), o => o.MatchByName = false));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "x", Reference = new ExternalSecretRef(null, null, "Db/Timeout") }));
        Assert.Equal(2, Resolve(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "Winton.Extensions.Configuration.Consul.ConsulConfigurationProvider", ConfigurationKey = "Db:Timeout" }).Count);
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "in-memory collection", ConfigurationKey = "Db:Timeout" }));
    }

    [Fact]
    public void Follows_explicit_links_by_full_or_relative_key_and_JSON_path()
    {
        var diagnostics = new TraceDiagnostics();

        var full = Assert.Single(Resolve(EnvStep("T"), o => o.Map("T", "config/orders/Db/Timeout")));
        Assert.False(full.IsInferred);
        Assert.Equal("linked explicitly; modify index 12", full.Note);
        Assert.Single(Resolve(EnvStep("T"), o => o.Map("T", "Db/Timeout")));
        Assert.Single(Resolve(EnvStep("F"), o => o.Map("F", "Feature", "flags:new")));
        Assert.Single(Resolve(EnvStep("F"), o => o.Links["F"] = "Feature#"));
        Assert.Equal(2, Resolve(EnvStep("Db__Timeout"), o => o.Links["Db__Timeout"] = "").Count);

        Assert.Empty(Resolve(EnvStep("F"), o => o.Map("F", "Feature", "Other"), diagnostics));
        Assert.Empty(Resolve(EnvStep("M"), o => o.Map("M", "Missing"), diagnostics));
        Assert.Equal(["F is linked to Consul key Feature#Other, but no such key was loaded.", "M is linked to Consul key Missing, but no such key was loaded."], diagnostics.Warnings);
        Assert.Throws<ArgumentException>(() => new ConsulTraceOptions().Map(" ", "k"));
        Assert.Throws<ArgumentException>(() => new ConsulTraceOptions().Map("n", " "));
    }

    [Fact]
    public void Lists_entries_with_location_and_adds_load_warnings_once()
    {
        var values = new ConsulKeyValues([new("k", "k", "v"), new("j", "A:B", "v", null, "A:B")], isSensitive: true, warnings: ["w"]);
        var resolver = new ConsulResolver(values, new ConsulTraceOptions());
        var diagnostics = new TraceDiagnostics();

        var entries = resolver.Entries().ToList();
        Assert.Equal(["k", "j#A:B"], entries.Select(e => e.Name));
        Assert.All(entries, e => Assert.True(e.IsSensitive));
        Assert.Equal("configuration key A:B", entries[1].Detail);
        Assert.Equal("Consul", entries[0].Location);

        Assert.True(Assert.Single(resolver.Resolve(EnvStep("k"), diagnostics)).IsSensitive);
        resolver.Resolve(EnvStep("x"), diagnostics);
        Assert.Equal(["w"], diagnostics.Warnings);
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!, diagnostics));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(EnvStep("k"), null!));
        Assert.Throws<ArgumentNullException>(() => ConfigTracer.For(new ConfigurationBuilder().Build()).WithConsul(null!));
        Assert.Throws<ArgumentNullException>(() => HashiCorpTracerExtensions.WithConsul(null!, values));
    }

    [Fact]
    public async Task Traces_an_environment_variable_to_Consul_redacts_when_sensitive_and_finds_unused_keys()
    {
        var prefix = $"CFCONSUL{Guid.NewGuid():N}_";
        Environment.SetEnvironmentVariable(prefix + "Db__Password", "hunter2");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var handler = new FakeHttpHandler().Respond(Path, Kv(("config/orders/Db/Password", "hunter2"), ("config/orders/Unused", "1")));
            using var client = handler.Client();
            var source = ConsulSource.Create(Options(o => o.IsSensitive = true), new ConsulTraceOptions { StripPrefixes = { prefix } });
            source.HttpClient = client;

            var tracer = ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "Db__Password"] = "hunter2" });
            var loaded = await tracer.WithSourceAsync(source, TestContext.Current.CancellationToken);
            var report = tracer.Trace();

            var password = report.Find("Db:Password")!;
            Assert.Equal($"set in environment variable {prefix}Db__Password from Consul key config/orders/Db/Password", password.Story);
            Assert.DoesNotContain("hunter2", report.ToJson(), StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", report.ToText(), StringComparison.Ordinal);
            Assert.Equal(["config/orders/Unused"], loaded.FindUnused(report).Select(e => e.Name));
            Assert.Equal("Consul KV consul.example.com:8501 config/orders/", loaded.DisplayName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Db__Password", null);
        }
    }

    /// <summary>Links each .NET layer to one upstream step with the given reference.</summary>
    private sealed class LinkTo(SourceRef reference) : IOriginResolver
    {
        public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics) =>
            layer.ConfigurationKey is null ? [] : [new ProvenanceStep { Kind = LayerKind.Consul, Source = "Consul key", Value = layer.Value, Reference = reference }];
    }

    [Theory]
    [InlineData("config/token-service/Smtp/Host", null, false)]
    [InlineData("config/orders/smtp-password", null, true)]
    [InlineData("config/orders/appsettings", "Smtp:Host", false)]
    [InlineData("config/orders/appsettings", "Smtp:Password", true)]
    public void Only_the_last_part_of_a_key_and_its_JSON_path_can_hide_its_value(string key, string? jsonPath, bool hidden)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Smtp:Host"] = "mail.example.test" }).Build();

        var setting = ConfigTracer.For(configuration).WithResolver(new LinkTo(new ConsulKeyRef(null, key, jsonPath))).Trace().Find("Smtp:Host")!;

        Assert.Equal(hidden, setting.IsSensitive);
        Assert.Equal(hidden ? "***" : "mail.example.test", setting.Value);
    }
}
