using System.Net;
using ConfigFerret.Tests.Shared;

namespace ConfigFerret.HashiCorp.Tests;

public class VaultSecretsTests
{
    private const string Token = "hvs.test_token_never_printed";
    private const string Mounts = "/v1/sys/mounts/secret";
    private const string UiMounts = "/v1/sys/internal/ui/mounts/secret";

    private static VaultOptions Options(Action<VaultOptions>? configure = null)
    {
        var options = new VaultOptions { Address = new Uri("https://vault.example.com:8200/"), Token = Token };
        configure?.Invoke(options);
        return options;
    }

    private static string Keys(params string[] keys) => """{"data":{"keys":[""" + string.Join(",", keys.Select(k => $"\"{k}\"")) + "]}}";

    private static string Metadata(int version, string updated = "2026-10-01T08:30:00.123456Z", bool deleted = false) =>
        $$$"""{"data":{"current_version":{{{version}}},"updated_time":"{{{updated}}}","versions":{"{{{version}}}":{"deletion_time":"{{{(deleted ? "2026-10-02T00:00:00Z" : "")}}}","destroyed":false""" + "}}}}";

    private static FakeHttpHandler V2() => new FakeHttpHandler()
        .Respond(Mounts, """{"type":"kv","options":{"version":"2"},"data":{"type":"kv","options":{"version":"2"}}}""")
        .Respond("/v1/secret/metadata/orders/?list=true", Keys("db", "Db/", "app"))
        .Respond("/v1/secret/metadata/orders/Db/?list=true", Keys("Timeout"))
        .Respond("/v1/secret/metadata/orders/db", Metadata(3))
        .Respond("/v1/secret/metadata/orders/app", Metadata(1, deleted: true))
        .Respond("/v1/secret/metadata/orders/Db/Timeout", Metadata(7))
        .Respond("/v1/secret/data/orders/db", """{"data":{"data":{"password":"hunter2","port":5432,"tls":true,"extra":{"a":1},"none":null},"metadata":{"version":3}}}""")
        .Respond("/v1/secret/data/orders/Db/Timeout", """{"data":{"data":{"value":"30"},"metadata":{"version":7}}}""");

    private static async Task<VaultSecrets> Load(FakeHttpHandler handler, Action<VaultOptions>? configure = null)
    {
        using var client = handler.Client();
        return await VaultSecrets.LoadAsync(Options(configure), client, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Lists_a_KV2_path_recursively_with_metadata_and_never_reads_values_by_default()
    {
        var handler = V2();

        var vault = await Load(handler, o => o.Paths.Add("orders"));

        Assert.Equal(["orders/db", "orders/app", "orders/Db/Timeout"], vault.Secrets.Select(s => s.Path));
        Assert.Equal(["db", "app", "Db/Timeout"], vault.Secrets.Select(s => s.RelativePath));
        var db = vault.Find("/orders/db/")!;
        Assert.Equal(3, db.Version);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 30, 0, 123, TimeSpan.Zero).AddTicks(4560), db.UpdatedTime);
        Assert.Null(db.Data);
        Assert.False(db.IsDeleted);
        Assert.True(vault.Find("orders/app")!.IsDeleted);
        Assert.Null(vault.Find("orders/missing"));
        Assert.Equal("secret", vault.Mount);
        Assert.Equal("vault.example.com:8200", vault.Location);
        Assert.Empty(vault.Warnings);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("/data/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sends_the_token_and_namespace_only_in_headers()
    {
        var handler = V2();

        await Load(handler, o =>
        {
            o.Paths.Add("orders");
            o.Namespace = " admin/orders ";
        });

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(Token, Assert.Single(r.Headers.GetValues("X-Vault-Token")));
            Assert.Equal("admin/orders", Assert.Single(r.Headers.GetValues("X-Vault-Namespace")));
            Assert.Equal("true", Assert.Single(r.Headers.GetValues("X-Vault-Request")));
            Assert.Contains(r.Headers.Accept, a => a.MediaType == "application/json");
            Assert.DoesNotContain(Token, r.RequestUri!.ToString(), StringComparison.Ordinal);
            Assert.Equal(HttpMethod.Get, r.Method);
        });
    }

    [Fact]
    public async Task Leaves_out_the_namespace_header_when_none_is_set_and_shows_the_namespace_in_the_location()
    {
        var handler = V2();
        var vault = await Load(handler, o => o.Paths.Add("orders"));
        Assert.All(handler.Requests, r => Assert.False(r.Headers.Contains("X-Vault-Namespace")));

        var withNamespace = await Load(V2(), o => o.Namespace = "admin");
        Assert.Equal("vault.example.com:8200 namespace admin", withNamespace.Location);
        Assert.Equal("vault.example.com:8200", vault.Location);
    }

    [Fact]
    public async Task Reads_values_when_asked_as_text_like_the_JSON_provider()
    {
        var vault = await Load(V2(), o =>
        {
            o.Paths.Add("orders");
            o.IncludeValues = true;
        });

        var data = vault.Find("orders/db")!.Data!;
        Assert.Equal("hunter2", data["password"]);
        Assert.Equal("5432", data["port"]);
        Assert.Equal("True", data["tls"]);
        Assert.Equal("""{"a":1}""", data["extra"]);
        Assert.Null(data["none"]);
        Assert.Equal("30", vault.Find("orders/Db/Timeout")!.Data!["value"]);

        // The deleted secret has metadata but no data.
        Assert.Null(vault.Find("orders/app")!.Data);
    }

    [Fact]
    public async Task Reads_a_configured_path_that_is_a_secret_itself()
    {
        var handler = V2().Respond("/v1/secret/metadata/orders/db/?list=true", """{"errors":[]}""", HttpStatusCode.NotFound);

        var vault = await Load(handler, o => o.Paths.Add("orders/db"));

        var db = Assert.Single(vault.Secrets);
        Assert.Equal("orders/db", db.Path);
        Assert.Equal(string.Empty, db.RelativePath);
    }

    [Fact]
    public async Task Reads_the_whole_mount_when_no_path_is_given()
    {
        var handler = V2().Respond("/v1/secret/metadata/?list=true", Keys("orders/", "top"))
            .Respond("/v1/secret/metadata/top", Metadata(1));

        var vault = await Load(handler);

        Assert.Equal(["top", "orders/db", "orders/app", "orders/Db/Timeout"], vault.Secrets.Select(s => s.Path));
        Assert.Equal("orders/Db/Timeout", vault.Find("orders/Db/Timeout")!.RelativePath);
    }

    [Fact]
    public async Task Does_not_descend_when_not_recursive()
    {
        var handler = V2();

        var vault = await Load(handler, o =>
        {
            o.Paths.Add("orders");
            o.Recursive = false;
        });

        Assert.Equal(["orders/db", "orders/app"], vault.Secrets.Select(s => s.Path));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("/Db/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lists_KV1_without_reading_any_secret()
    {
        var handler = new FakeHttpHandler()
            .Respond("/v1/kv/orders/?list=true", Keys("db", "sub/"))
            .Respond("/v1/kv/orders/sub/?list=true", Keys("x"));

        var vault = await Load(handler, o =>
        {
            o.Mount = "/kv/";
            o.KvVersion = 1;
            o.Paths.Add("orders");
        });

        Assert.Equal(["orders/db", "orders/sub/x"], vault.Secrets.Select(s => s.Path));
        Assert.All(vault.Secrets, s => Assert.Null(s.Data));
        Assert.All(vault.Secrets, s => Assert.Null(s.Version));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("kv", vault.Mount);
    }

    [Fact]
    public async Task Reads_KV1_values_when_asked()
    {
        var handler = new FakeHttpHandler()
            .Respond(Mounts, """{"type":"kv","options":null}""")
            .Respond("/v1/secret/orders/?list=true", Keys("db"))
            .Respond("/v1/secret/orders", """{"data":{"Db__Timeout":"30"}}""")
            .Respond("/v1/secret/orders/db", """{"data":{"password":"hunter2"}}""");

        var vault = await Load(handler, o =>
        {
            o.Paths.Add("orders");
            o.IncludeValues = true;
        });

        Assert.Equal(["orders", "orders/db"], vault.Secrets.Select(s => s.Path));
        Assert.Equal("30", vault.Find("orders")!.Data!["Db__Timeout"]);
        Assert.Equal("hunter2", vault.Find("orders/db")!.Data!["password"]);
    }

    [Fact]
    public async Task Takes_a_KV1_path_that_is_not_a_folder_to_be_a_secret()
    {
        var vault = await Load(new FakeHttpHandler(), o =>
        {
            o.KvVersion = 1;
            o.Paths.Add("orders/db");
        });

        Assert.Equal("orders/db", Assert.Single(vault.Secrets).Path);
    }

    [Theory]
    [InlineData("""{"data":{"type":"kv","options":{"version":"1"}}}""", 1)]
    [InlineData("""{"type":"generic","options":{}}""", 1)]
    [InlineData("""{"data":{"type":"kv","options":{"version":"2"}}}""", 2)]
    public async Task Detects_the_KV_version_from_sys_mounts(string mount, int expected)
    {
        var handler = new FakeHttpHandler().Respond(Mounts, mount);

        await Load(handler, o => o.Paths.Add("orders"));

        var list = handler.Requests[^1].RequestUri!.PathAndQuery;
        Assert.Equal(expected == 2 ? "/v1/secret/metadata/orders/?list=true" : "/v1/secret/orders/?list=true", list);
    }

    [Fact]
    public async Task Falls_back_to_the_ui_mounts_endpoint_then_to_version_2_with_a_warning()
    {
        var fallback = new FakeHttpHandler()
            .Respond(Mounts, """{"errors":["permission denied"]}""", HttpStatusCode.Forbidden)
            .Respond(UiMounts, """{"data":{"type":"kv","options":{"version":"1"},"path":"secret/"}}""");
        Assert.Empty((await Load(fallback, o => o.Paths.Add("orders"))).Warnings);
        Assert.Equal("/v1/secret/orders/?list=true", fallback.Requests[2].RequestUri!.PathAndQuery);

        var neither = new FakeHttpHandler()
            .Respond(Mounts, """{"errors":["permission denied"]}""", HttpStatusCode.Forbidden)
            .Respond(UiMounts, """{"unexpected":true}""");
        var vault = await Load(neither, o => o.Paths.Add("orders"));
        Assert.Contains("version 2 is assumed", Assert.Single(vault.Warnings), StringComparison.Ordinal);
        Assert.Equal(["/v1/secret/metadata/orders", "/v1/secret/metadata/orders/?list=true"], neither.Requests.Skip(2).Select(r => r.RequestUri!.PathAndQuery));
    }

    [Theory]
    [InlineData("""{"type":"pki","options":null}""", "is a 'pki' secrets engine")]
    [InlineData("""{"type":"kv","options":{"version":"3"}}""", "reports KV version '3'")]
    public async Task Warns_about_a_mount_it_does_not_know(string mount, string warning)
    {
        var vault = await Load(new FakeHttpHandler().Respond(Mounts, mount));

        Assert.Contains(vault.Warnings, w => w.Contains(warning, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Skips_a_folder_the_token_cannot_list_with_a_warning()
    {
        var handler = V2().Respond("/v1/secret/metadata/orders/Db/?list=true", """{"errors":["permission denied"]}""", HttpStatusCode.Forbidden);

        var vault = await Load(handler, o => o.Paths.Add("orders"));

        Assert.Equal(["orders/db", "orders/app"], vault.Secrets.Select(s => s.Path));
        var warning = Assert.Single(vault.Warnings);
        Assert.Equal("Skipped listing orders/Db/ in HashiCorp Vault: HashiCorp Vault returned 403 Forbidden for /v1/secret/metadata/orders/Db/. permission denied", warning);
    }

    [Fact]
    public async Task Fails_on_a_server_error_without_the_token_in_the_message()
    {
        var handler = V2().Respond("/v1/secret/metadata/orders/?list=true", """{"errors":["internal error"]}""", HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Load(handler, o => o.Paths.Add("orders")));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stops_at_the_depth_cap_when_a_server_nests_folders_forever()
    {
        var handler = new LoopingHandler();
        using var client = new HttpClient(handler);

        var vault = await VaultSecrets.LoadAsync(Options(o => { o.KvVersion = 2; o.MaxDepth = 3; }), client, TestContext.Current.CancellationToken);

        Assert.Contains(vault.Warnings, w => w.StartsWith("Did not list HashiCorp Vault folders more than 3 levels below mount secret (MaxDepth), for example a/a/a/a/.", StringComparison.Ordinal));
        Assert.Equal(4, handler.Lists);
    }

    [Fact]
    public async Task Stops_at_the_request_and_secret_caps()
    {
        using var requestsClient = new HttpClient(new LoopingHandler());
        var byRequests = await VaultSecrets.LoadAsync(Options(o => { o.KvVersion = 2; o.MaxRequests = 5; }), requestsClient, TestContext.Current.CancellationToken);
        Assert.Contains("Stopped reading HashiCorp Vault after 5 requests (MaxRequests).", byRequests.Warnings);

        using var secretsClient = new HttpClient(new LoopingHandler());
        var bySecrets = await VaultSecrets.LoadAsync(Options(o => { o.KvVersion = 2; o.MaxSecrets = 2; }), secretsClient, TestContext.Current.CancellationToken);
        Assert.Equal(2, bySecrets.Secrets.Count);
        Assert.Contains("Stopped reading HashiCorp Vault after 2 secrets (MaxSecrets).", bySecrets.Warnings);
    }

    [Fact]
    public async Task Visits_a_folder_once_even_when_two_paths_overlap()
    {
        var handler = V2();

        var vault = await Load(handler, o =>
        {
            o.Paths.Add("orders");
            o.Paths.Add("orders/Db");
            o.Paths.Add("orders/");
        });

        Assert.Equal(3, vault.Secrets.Count);
        Assert.Single(handler.Requests, r => r.RequestUri!.PathAndQuery == "/v1/secret/metadata/orders/Db/?list=true");
    }

    [Fact]
    public async Task Skips_keys_that_are_not_single_path_segments_and_odd_shapes()
    {
        var longKey = new string('k', 1025);
        var handler = new FakeHttpHandler()
            .Respond("/v1/secret/metadata/orders/?list=true", $$$"""{"data":{"keys":["..","./","", "a/b", "{{{longKey}}}", 42, null, "\ud800x", "ok", "odd", "arr"]}}""")
            .Respond("/v1/secret/metadata/orders/ok", Metadata(1))
            .Respond("/v1/secret/metadata/orders/odd", """{"data":{"current_version":"x","updated_time":"not a date","versions":[]}}""")
            .Respond("/v1/secret/metadata/orders/arr", """{"data":[]}""");

        var vault = await Load(handler, o =>
        {
            o.KvVersion = 2;
            o.Paths.Add("orders");
        });

        Assert.Equal(["orders/ok", "orders/odd"], vault.Secrets.Select(s => s.Path));
        Assert.Null(vault.Find("orders/odd")!.Version);
        Assert.Null(vault.Find("orders/odd")!.UpdatedTime);
        Assert.Contains("Skipped 8 HashiCorp Vault keys whose names are empty, too long or not a single path segment.", vault.Warnings);
    }

    [Theory]
    [InlineData("""{"data":{"keys":"nope"}}""")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    public async Task Warns_about_a_listing_without_keys(string body)
    {
        var handler = new FakeHttpHandler().Respond("/v1/secret/metadata/orders/?list=true", body);

        var vault = await Load(handler, o =>
        {
            o.KvVersion = 2;
            o.Paths.Add("orders");
        });

        Assert.Empty(vault.Secrets);
        Assert.Contains("HashiCorp Vault returned an unexpected response listing orders/: no data.keys array.", vault.Warnings);
    }

    // Found by fuzzing: TryGetProperty throws when another property's name is a lone surrogate escape.
    [Fact]
    public async Task Reads_past_property_names_that_are_not_valid_UTF16()
    {
        var handler = new FakeHttpHandler()
            .Respond(Mounts, """{"type":"kv","options":{"n":"2"},"data":{"type":"nv","opt\ud800ions":{"version":"2"}}}""")
            .Respond("/v1/secret/metadata/orders/?list=true", """{"data":{"k\ud800":1,"keys":["s"]}}""")
            .Respond("/v1/secret/metadata/orders/s", """{"data":{"\ud800":1,"current_version":2}}""");

        var detected = await Load(handler, o => o.Paths.Add("orders"));
        var listed = await Load(handler, o =>
        {
            o.KvVersion = 2;
            o.Paths.Add("orders");
        });

        // The data object cannot be read, so the top-level fields decide: options without a version means KV 1.
        Assert.Equal("/v1/secret/orders/?list=true", handler.Requests[1].RequestUri!.PathAndQuery);
        Assert.Equal("orders", Assert.Single(detected.Secrets).Path);
        Assert.Equal(2, listed.Find("orders/s")!.Version);
    }

    [Fact]
    public async Task Wraps_a_response_that_is_not_JSON()
    {
        var handler = new FakeHttpHandler().Respond("/v1/secret/metadata/orders/?list=true", "<html>");

        await Assert.ThrowsAsync<HttpRequestException>(() => Load(handler, o =>
        {
            o.KvVersion = 2;
            o.Paths.Add("orders");
        }));
    }

    [Fact]
    public async Task Skips_a_configured_path_that_is_not_valid()
    {
        var handler = new FakeHttpHandler();

        var vault = await Load(handler, o =>
        {
            o.KvVersion = 2;
            o.Paths.Add("orders/../sys");
        });

        Assert.Equal(["Skipped HashiCorp Vault path 'orders/../sys': it is not a valid path."], vault.Warnings);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Escapes_each_path_segment()
    {
        var handler = new FakeHttpHandler().Respond("/v1/secret/metadata/my%20app/?list=true", Keys("a b"))
            .Respond("/v1/secret/metadata/my%20app/a%20b", Metadata(1));

        var vault = await Load(handler, o =>
        {
            o.KvVersion = 2;
            o.Paths.Add("my app");
        });

        Assert.Equal("my app/a b", Assert.Single(vault.Secrets).Path);
    }

    [Fact]
    public async Task Caps_the_keys_read_from_one_secret()
    {
        var keys = string.Join(",", Enumerable.Range(0, 1001).Select(i => $"\"k{i}\":\"v\""));
        var handler = new FakeHttpHandler()
            .Respond("/v1/secret/metadata/big", Metadata(1))
            .Respond("/v1/secret/data/big", """{"data":{"data":{""" + keys + """, "":"empty"}}}""");

        var vault = await Load(handler, o =>
        {
            o.KvVersion = 2;
            o.IncludeValues = true;
            o.Paths.Add("big");
        });

        Assert.Equal(1000, vault.Find("big")!.Data!.Count);
        Assert.Contains("HashiCorp Vault secret big has more than 1000 keys; the rest were skipped.", vault.Warnings);
    }

    [Fact]
    public async Task Uses_the_data_metadata_when_the_metadata_endpoint_is_not_readable()
    {
        var handler = new FakeHttpHandler()
            .Respond("/v1/secret/metadata/db", """{"errors":["permission denied"]}""", HttpStatusCode.Forbidden)
            .Respond("/v1/secret/data/db", """{"data":{"data":{"k":"v"},"metadata":{"version":4,"created_time":"2026-01-02T03:04:05Z"}}}""");

        var vault = await Load(handler, o =>
        {
            o.KvVersion = 2;
            o.IncludeValues = true;
            o.Paths.Add("db");
        });

        var db = vault.Find("db")!;
        Assert.Equal(4, db.Version);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), db.UpdatedTime);
        Assert.Single(vault.Warnings);
    }

    [Fact]
    public async Task Checks_its_arguments()
    {
        using var client = new FakeHttpHandler().Client();
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentNullException>(() => VaultSecrets.LoadAsync(null!, client, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => VaultSecrets.LoadAsync(Options(), null!, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => VaultSecrets.LoadAsync(new VaultOptions { Token = Token }, client, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => VaultSecrets.LoadAsync(Options(o => o.Address = new Uri("http://vault.example.com/")), client, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => VaultSecrets.LoadAsync(Options(o => o.Address = new Uri("http://vault.example.com/")), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => VaultSecrets.LoadAsync(Options(o => o.Mount = " / "), client, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => VaultSecrets.LoadAsync(Options(o => o.Mount = ".."), client, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => VaultSecrets.LoadAsync(Options(o => o.KvVersion = 3), client, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => VaultSecrets.LoadAsync(Options(o => o.MaxDepth = -1), client, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => VaultSecrets.LoadAsync(Options(o => o.MaxSecrets = 0), client, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => VaultSecrets.LoadAsync(Options(o => o.MaxRequests = 0), client, ct));
        var token = await Assert.ThrowsAsync<ArgumentException>(() => VaultSecrets.LoadAsync(Options(o => o.Token = Token + "\r\nX: y"), client, ct));
        Assert.DoesNotContain(Token, token.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => VaultSecrets.LoadAsync(Options(o => o.Namespace = "ä"), client, ct));
        Assert.Empty(new FakeHttpHandler().Requests);

        Assert.Throws<ArgumentNullException>(() => new VaultSecrets(null!));
        Assert.Throws<ArgumentException>(() => new VaultSecrets([], " "));
        Assert.Throws<ArgumentNullException>(() => new VaultSecrets([null!]));
        Assert.Throws<ArgumentNullException>(() => new VaultSecrets([]).Find(null!));
    }

    [Fact]
    public void Builds_a_snapshot_from_secrets_keeping_the_first_of_each_path()
    {
        var vault = new VaultSecrets([new("a/b", "b", Version: 1), new("/a/b/", "x", Version: 2), new("", "")], "/kv/", warnings: ["w"]);

        Assert.Equal(1, Assert.Single(vault.Secrets).Version);
        Assert.Equal("kv", vault.Mount);
        Assert.Equal("HashiCorp Vault", vault.Location);
        Assert.Equal(["w"], vault.Warnings);
    }

    [Fact]
    public async Task Works_without_a_token_header_when_none_is_set()
    {
        var handler = new FakeHttpHandler();

        await Load(handler, o =>
        {
            o.Token = null;
            o.KvVersion = 2;
        });

        Assert.All(handler.Requests, r => Assert.False(r.Headers.Contains("X-Vault-Token")));
    }

    /// <summary>A hostile server: every folder holds a folder named a/ and a secret named s.</summary>
    private sealed class LoopingHandler : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _sent = [];

        public int Lists { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var list = request.RequestUri!.Query.Contains("list=true", StringComparison.Ordinal);
            Lists += list ? 1 : 0;
            var body = list ? Keys("a/", "s") : Metadata(1);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            _sent.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _sent.ForEach(r => r.Dispose());
            }

            base.Dispose(disposing);
        }
    }
}
