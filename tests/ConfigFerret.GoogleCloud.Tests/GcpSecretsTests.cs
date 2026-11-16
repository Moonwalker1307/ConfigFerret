using System.Net;
using System.Text;
using ConfigFerret.Tests.Shared;

namespace ConfigFerret.GoogleCloud.Tests;

public class GcpSecretsTests
{
    internal const string Token = "ya29.test_token_never_printed";
    internal const string List = "/v1/projects/contoso-prod/secrets?pageSize=250";

    internal static GcpSecretManagerOptions Options(Action<GcpSecretManagerOptions>? configure = null)
    {
        var options = new GcpSecretManagerOptions { ProjectId = "contoso-prod", AccessToken = Token };
        configure?.Invoke(options);
        return options;
    }

    internal static string Secret(string id, string extra = "") =>
        "{\"name\":\"projects/123/secrets/" + id + "\",\"createTime\":\"2026-10-01T08:30:00.5Z\",\"replication\":{\"automatic\":{}}" + extra + "}";

    internal static string Versions(params (int Id, string State)[] versions) =>
        """{"versions":[""" + string.Join(",", versions.Select(v => $$"""{"name":"projects/123/secrets/x/versions/{{v.Id}}","state":"{{v.State}}","createTime":"2026-10-0{{v.Id % 9 + 1}}T00:00:00Z"}""")) + "]}";

    internal static string Access(string value) =>
        "{\"name\":\"projects/123/secrets/x/versions/1\",\"payload\":{\"data\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "\"}}";

    internal static async Task<GcpSecrets> Load(FakeHttpHandler handler, Action<GcpSecretManagerOptions>? configure = null)
    {
        using var client = handler.Client();
        return await GcpSecrets.LoadAsync(Options(configure), client, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Lists_secrets_with_metadata_and_never_reads_versions_or_values_by_default()
    {
        var handler = new FakeHttpHandler().Respond(List, $$$"""
            {"secrets":[
              {{{Secret("Db__Timeout", ""","labels":{"app":"orders","env":"prod","n":1},"annotations":{"owner":"team-a"}""")}}},
              {"name":"projects/123/secrets/Api__Key","replication":{"userManaged":{"replicas":[{"location":"us-east1"},{"location":"europe-west1"},{"x":1}]} } },
              {"name":"projects/123/secrets/Plain","replication":{"userManaged":{} } },
              {"name":"projects/123/secrets/Other","replication":{"future":{} } }
            ],"totalSize":4}
            """);

        var gcp = await Load(handler);

        Assert.Equal(["Db__Timeout", "Api__Key", "Plain", "Other"], gcp.Secrets.Select(s => s.Id));
        var db = gcp.Find("Db__Timeout")!;
        Assert.Equal("123", db.Project);
        Assert.Null(db.Location);
        Assert.Equal("projects/123/secrets/Db__Timeout", db.Name);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 30, 0, 500, TimeSpan.Zero), db.CreateTime);
        Assert.Equal("automatic", db.Replication);
        Assert.Equal(new Dictionary<string, string> { ["app"] = "orders", ["env"] = "prod" }, db.Labels);
        Assert.Equal("team-a", db.Annotations["owner"]);
        Assert.Null(db.Versions);
        Assert.Null(db.LatestEnabledVersion);
        Assert.Null(db.Value);
        Assert.Null(db.ValueVersion);
        Assert.Equal("user-managed (us-east1, europe-west1)", gcp.Find("Api__Key")!.Replication);
        Assert.Empty(gcp.Find("Api__Key")!.Labels);
        Assert.Equal("user-managed", gcp.Find("Plain")!.Replication);
        Assert.Null(gcp.Find("Other")!.Replication);
        Assert.Null(gcp.Find("db__timeout"));
        Assert.Equal("contoso-prod", gcp.ProjectId);
        Assert.Equal("project contoso-prod", gcp.Where);
        Assert.Empty(gcp.Warnings);
        Assert.Equal(List, Assert.Single(handler.Requests).RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Sends_the_token_only_in_the_Authorization_header()
    {
        var handler = new FakeHttpHandler().Respond(List, "{}");

        await Load(handler);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/json");
        Assert.Equal("https", request.RequestUri!.Scheme);
        Assert.Equal("secretmanager.googleapis.com", request.RequestUri.Host);
        Assert.DoesNotContain(Token, request.RequestUri.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("ConfigFerret/", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Follows_page_tokens_and_sends_the_filter()
    {
        var first = "/v1/projects/contoso-prod/secrets?pageSize=250&filter=labels.app%3Dorders";
        var handler = new FakeHttpHandler()
            .Respond(first, $$"""{"secrets":[{{Secret("A")}}],"nextPageToken":"tok/en+1"}""")
            .Respond(first + "&pageToken=tok%2Fen%2B1", $$"""{"secrets":[{{Secret("B")}}],"nextPageToken":""}""");

        var gcp = await Load(handler, o => o.Filter = " labels.app=orders ");

        Assert.Equal(["A", "B"], gcp.Secrets.Select(s => s.Id));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Stops_when_the_server_repeats_a_page_token()
    {
        var handler = new FakeHttpHandler()
            .Respond(List, $$"""{"secrets":[{{Secret("A")}}],"nextPageToken":"t"}""")
            .Respond(List + "&pageToken=t", $$"""{"secrets":[{{Secret("A")}},{{Secret("B")}}],"nextPageToken":"t"}""");

        var gcp = await Load(handler);

        Assert.Equal(["A", "B"], gcp.Secrets.Select(s => s.Id));
        Assert.Equal("Google Cloud Secret Manager sent the same page token twice, so listing stopped there.", Assert.Single(gcp.Warnings));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Keeps_only_secrets_with_the_prefix_and_stops_at_MaxSecrets()
    {
        var handler = new FakeHttpHandler()
            .Respond(List, $$"""{"secrets":[{{Secret("orders-A")}},{{Secret("billing-B")}},{{Secret("ORDERS-C")}},{{Secret("orders-D")}}],"nextPageToken":"more"}""");

        var gcp = await Load(handler, o =>
        {
            o.Prefix = "orders-";
            o.MaxSecrets = 2;
        });

        Assert.Equal(["orders-A", "ORDERS-C"], gcp.Secrets.Select(s => s.Id));
        Assert.Equal("orders-", gcp.Prefix);
        Assert.Equal("Stopped after 2 Google Cloud secrets (MaxSecrets); the rest were not read.", Assert.Single(gcp.Warnings));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Reads_exactly_MaxSecrets_without_a_warning()
    {
        var handler = new FakeHttpHandler().Respond(List, $$"""{"secrets":[{{Secret("A")}},{{Secret("B")}}]}""");

        var gcp = await Load(handler, o => o.MaxSecrets = 2);

        Assert.Equal(2, gcp.Secrets.Count);
        Assert.Empty(gcp.Warnings);
    }

    [Fact]
    public async Task Reads_regional_secrets_from_the_regional_endpoint()
    {
        var handler = new FakeHttpHandler()
            .Respond("/v1/projects/contoso-prod/locations/europe-west1/secrets?pageSize=250", """{"secrets":[{"name":"projects/123/locations/europe-west1/secrets/Db"}]}""")
            .Respond("/v1/projects/123/locations/europe-west1/secrets/Db/versions?pageSize=25", Versions((1, "ENABLED")));

        var gcp = await Load(handler, o =>
        {
            o.Location = " europe-west1 ";
            o.IncludeVersions = true;
        });

        var db = Assert.Single(gcp.Secrets);
        Assert.Equal("europe-west1", db.Location);
        Assert.Equal("projects/123/locations/europe-west1/secrets/Db", db.Name);
        Assert.Equal("europe-west1", gcp.Location);
        Assert.Equal("project contoso-prod, location europe-west1", gcp.Where);
        Assert.All(handler.Requests, r => Assert.Equal("secretmanager.europe-west1.rep.googleapis.com", r.RequestUri!.Host));
        Assert.Equal("1", db.LatestEnabledVersion);
    }

    [Fact]
    public async Task Uses_ApiBaseAddress_when_set()
    {
        var handler = new FakeHttpHandler().Respond("/sm/v1/projects/contoso-prod/secrets?pageSize=250", "{}");

        await Load(handler, o => o.ApiBaseAddress = new Uri("https://private.example.com/sm/v1"));

        Assert.Equal("private.example.com", Assert.Single(handler.Requests).RequestUri!.Host);
    }

    [Fact]
    public async Task Reads_version_states_newest_first_when_asked()
    {
        var handler = new FakeHttpHandler()
            .Respond(List, $$"""{"secrets":[{{Secret("A")}},{{Secret("B")}}]}""")
            .Respond("/v1/projects/123/secrets/A/versions?pageSize=25", Versions((2, "DISABLED"), (10, "ENABLED"), (3, "DESTROYED"), (4, "ENABLED"), (5, "PENDING")))
            .Respond("/v1/projects/123/secrets/B/versions?pageSize=25", """{"versions":[{"name":"bad"},{"name":"projects/123/secrets/B/versions/"},{"name":"projects/123/secrets/B/versions/.."},{"state":"ENABLED"},7,{"name":"projects/123/secrets/B/versions/1","state":"ENABLED"}]}""");

        var gcp = await Load(handler, o => o.IncludeVersions = true);

        var a = gcp.Find("A")!;
        Assert.Equal(["10", "5", "4", "3", "2"], a.Versions!.Select(v => v.Id));
        Assert.Equal(
            [GcpSecretVersionState.Enabled, GcpSecretVersionState.Unknown, GcpSecretVersionState.Enabled, GcpSecretVersionState.Destroyed, GcpSecretVersionState.Disabled],
            a.Versions!.Select(v => v.State));
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), a.Versions![0].CreateTime);
        Assert.Equal("10", a.LatestEnabledVersion);
        Assert.Equal("1", Assert.Single(gcp.Find("B")!.Versions!).Id);
        Assert.Null(a.Value);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith(":access", StringComparison.Ordinal));
        Assert.Empty(gcp.Warnings);
    }

    [Fact]
    public async Task Asks_for_the_newest_enabled_version_when_the_first_page_has_none()
    {
        var handler = new FakeHttpHandler()
            .Respond(List, $$"""{"secrets":[{{Secret("A")}}]}""")
            .Respond("/v1/projects/123/secrets/A/versions?pageSize=25", """{"versions":[{"name":"projects/123/secrets/A/versions/40","state":"DISABLED"}],"nextPageToken":"p2"}""")
            .Respond("/v1/projects/123/secrets/A/versions?pageSize=1&filter=state%3AENABLED", """{"versions":[{"name":"projects/123/secrets/A/versions/7","state":"ENABLED"}],"nextPageToken":"p3"}""");

        var gcp = await Load(handler, o => o.IncludeVersions = true);

        Assert.Equal(["40", "7"], gcp.Find("A")!.Versions!.Select(v => v.Id));
        Assert.Equal("7", gcp.Find("A")!.LatestEnabledVersion);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Does_not_ask_again_when_the_first_page_has_an_enabled_version_or_no_more_pages()
    {
        var handler = new FakeHttpHandler()
            .Respond(List, $$"""{"secrets":[{{Secret("A")}},{{Secret("B")}}]}""")
            .Respond("/v1/projects/123/secrets/A/versions?pageSize=25", """{"versions":[{"name":"projects/123/secrets/A/versions/3","state":"DISABLED"},{"name":"projects/123/secrets/A/versions/2","state":"ENABLED"}],"nextPageToken":"p2"}""")
            .Respond("/v1/projects/123/secrets/B/versions?pageSize=25", """{"versions":[{"name":"projects/123/secrets/B/versions/2","state":"DISABLED"}]}""");

        var gcp = await Load(handler, o =>
        {
            o.IncludeVersions = true;
            o.IncludeDisabled = true;
        });

        Assert.Equal(2, gcp.Secrets.Count);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Skips_secrets_with_no_enabled_version_unless_IncludeDisabled()
    {
        FakeHttpHandler Handler() => new FakeHttpHandler()
            .Respond(List, $$"""{"secrets":[{{Secret("A")}},{{Secret("Gone")}},{{Secret("Empty")}}]}""")
            .Respond("/v1/projects/123/secrets/A/versions?pageSize=25", Versions((1, "ENABLED")))
            .Respond("/v1/projects/123/secrets/Gone/versions?pageSize=25", Versions((1, "DISABLED"), (2, "DESTROYED")))
            .Respond("/v1/projects/123/secrets/Empty/versions?pageSize=25", "{}");

        var skipped = await Load(Handler(), o => o.IncludeVersions = true);
        var kept = await Load(Handler(), o =>
        {
            o.IncludeVersions = true;
            o.IncludeDisabled = true;
        });

        Assert.Equal(["A"], skipped.Secrets.Select(s => s.Id));
        Assert.Equal("Skipped 2 Google Cloud secret(s) with no enabled version. Turn on IncludeDisabled to keep them.", Assert.Single(skipped.Warnings));
        Assert.Equal(["A", "Gone", "Empty"], kept.Secrets.Select(s => s.Id));
        Assert.Empty(kept.Warnings);
        Assert.Empty(kept.Find("Empty")!.Versions!);
    }

    [Fact]
    public async Task Keeps_a_secret_whose_versions_could_not_be_read_with_a_warning()
    {
        var handler = new FakeHttpHandler()
            .Respond(List, $$"""{"secrets":[{{Secret("A")}},{{Secret("B")}}]}""")
            .Respond("/v1/projects/123/secrets/A/versions?pageSize=25", """{"error":{"code":403,"message":"Permission 'secretmanager.versions.list' denied.","status":"PERMISSION_DENIED"}}""", HttpStatusCode.Forbidden)
            .Respond("/v1/projects/123/secrets/B/versions?pageSize=25", """{"versions":{}}""");

        var gcp = await Load(handler, o => o.IncludeVersions = true);

        Assert.Equal(["A", "B"], gcp.Secrets.Select(s => s.Id));
        Assert.Null(gcp.Find("A")!.Versions);
        Assert.Equal(
            [
                "Could not read the versions of Google Cloud secret A: Google Cloud Secret Manager returned 403 Forbidden for /v1/projects/123/secrets/A/versions. Permission 'secretmanager.versions.list' denied.",
                "Could not read the versions of Google Cloud secret B: Google Cloud Secret Manager returned an unexpected response: no 'versions' array.",
            ],
            gcp.Warnings);
    }

    [Fact]
    public async Task Reads_the_latest_enabled_value_when_asked()
    {
        var handler = new FakeHttpHandler()
            .Respond(List, $$"""{"secrets":[{{Secret("Db")}},{{Secret("Bin")}},{{Secret("Empty")}}]}""")
            .Respond("/v1/projects/123/secrets/Db/versions?pageSize=25", Versions((4, "DISABLED"), (3, "ENABLED"), (2, "ENABLED")))
            .Respond("/v1/projects/123/secrets/Db/versions/3:access", Access("""{"password":"hunter2"}"""))
            .Respond("/v1/projects/123/secrets/Bin/versions?pageSize=25", Versions((1, "ENABLED")))
            .Respond("/v1/projects/123/secrets/Bin/versions/1:access", """{"payload":{"data":"//79"}}""")
            .Respond("/v1/projects/123/secrets/Empty/versions?pageSize=25", Versions((1, "ENABLED")))
            .Respond("/v1/projects/123/secrets/Empty/versions/1:access", """{"payload":{}}""");

        var gcp = await Load(handler, o => o.IncludeValues = true);

        var db = gcp.Find("Db")!;
        Assert.Equal("3", db.ValueVersion);
        Assert.Equal("""{"password":"hunter2"}""", db.Value);
        Assert.DoesNotContain("hunter2", db.ToString(), StringComparison.Ordinal);
        Assert.Equal("GcpSecret { Name = projects/123/secrets/Db, Versions = 3, Value = (redacted) }", db.ToString());
        var bin = gcp.Find("Bin")!;
        Assert.True(bin.IsBinary);
        Assert.Null(bin.Value);
        Assert.Equal("1", bin.ValueVersion);
        Assert.Equal(string.Empty, gcp.Find("Empty")!.Value);
        Assert.False(gcp.Find("Empty")!.IsBinary);
        Assert.Empty(gcp.Warnings);
        Assert.Equal("GcpSecret { Name = projects/123/secrets/X, Versions = not read, Value = not read }", new GcpSecret { Id = "X", Project = "123" }.ToString());
    }

    [Fact]
    public async Task Warns_when_a_value_cannot_be_read_and_keeps_the_secret()
    {
        var handler = new FakeHttpHandler()
            .Respond(List, $$"""{"secrets":[{{Secret("A")}},{{Secret("B")}},{{Secret("C")}},{{Secret("D")}}]}""")
            .Respond("/v1/projects/123/secrets/A/versions?pageSize=25", Versions((1, "ENABLED")))
            .Respond("/v1/projects/123/secrets/A/versions/1:access", """{"error":{"message":"denied"}}""", HttpStatusCode.Forbidden)
            .Respond("/v1/projects/123/secrets/B/versions?pageSize=25", Versions((1, "ENABLED")))
            .Respond("/v1/projects/123/secrets/B/versions/1:access", """{"payload":{"data":"not base64!"}}""")
            .Respond("/v1/projects/123/secrets/C/versions?pageSize=25", Versions((1, "ENABLED")))
            .Respond("/v1/projects/123/secrets/C/versions/1:access", """{"name":"x"}""")
            .Respond("/v1/projects/123/secrets/D/versions?pageSize=25", Versions((1, "DISABLED")));

        var gcp = await Load(handler, o =>
        {
            o.IncludeValues = true;
            o.IncludeDisabled = true;
        });

        Assert.Equal(4, gcp.Secrets.Count);
        Assert.All(gcp.Secrets, s => Assert.Null(s.Value));
        Assert.All(gcp.Secrets, s => Assert.Null(s.ValueVersion));
        Assert.Equal(
            [
                "Could not read the value of Google Cloud secret A version 1: Google Cloud Secret Manager returned 403 Forbidden for /v1/projects/123/secrets/A/versions/1:access. denied",
                "Could not read the value of Google Cloud secret B version 1: the payload is not base64.",
                "Could not read the value of Google Cloud secret C version 1: the response has no payload.",
            ],
            gcp.Warnings);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/v1/projects/123/secrets/D/versions/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Leaves_out_a_reason_that_repeats_the_token(bool fourCharacters)
    {
        // A proxy or server that echoes the request's headers back must not put the token into the message.
        var echoed = fourCharacters ? Token[^4..] : Token;
        var handler = new FakeHttpHandler().Respond(List, $$$"""{"error":{"code":401,"message":"Request had invalid credentials {{{echoed}}}."}}""", HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Load(handler));

        Assert.Equal("Google Cloud Secret Manager returned 401 Unauthorized for /v1/projects/contoso-prod/secrets.", ex.Message);
    }

    [Fact]
    public async Task Fails_with_the_servers_message_when_the_list_is_refused()
    {
        var handler = new FakeHttpHandler().Respond(List, """{"error":{"code":403,"message":"Permission 'secretmanager.secrets.list' denied for resource 'projects/contoso-prod'.","status":"PERMISSION_DENIED"}}""", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Load(handler));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Equal("Google Cloud Secret Manager returned 403 Forbidden for /v1/projects/contoso-prod/secrets. Permission 'secretmanager.secrets.list' denied for resource 'projects/contoso-prod'.", ex.Message);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"secrets":{}}""")]
    [InlineData("""{"secrets":"x"}""")]
    public async Task Fails_on_a_response_that_is_not_a_secret_list(string body)
    {
        var handler = new FakeHttpHandler().Respond(List, body);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Load(handler));

        Assert.Equal("Google Cloud Secret Manager returned an unexpected response for projects/contoso-prod/secrets: no 'secrets' array.", ex.Message);
    }

    [Fact]
    public async Task Skips_entries_it_cannot_read()
    {
        var handler = new FakeHttpHandler().Respond(List, """
            {"secrets":[1,null,{},{"name":5},{"name":"bad"},{"name":"projects/1/secrets/"},{"name":"projects//secrets/x"},
             {"name":"projects/1/secrets/.."},{"name":"projects/1/secrets/."},{"name":"projects/1/other/x"},{"name":"projects/1/locations/l/secrets/a/b"},
             {"name":"projects/1/secrets/Ok","createTime":"yesterday","labels":[],"annotations":"x","replication":"x"},
             {"name":"projects/1/secrets/Ok"},
             {"name":"projects/1/secrets/Odd","createTime":5,"labels":{"\ud800":"v","k":"\udc00","ok":"1"},"replication":{"userManaged":{"replicas":{}}}},
             {"name":"projects/1/secrets/Blank","replication":{"userManaged":{"replicas":[{"location":""},{"location":"us-east1"}]}}}]}
            """);

        var gcp = await Load(handler);

        Assert.Equal(["Ok", "Odd", "Blank"], gcp.Secrets.Select(s => s.Id));
        Assert.Equal("user-managed (us-east1)", gcp.Find("Blank")!.Replication);
        Assert.Null(gcp.Find("Ok")!.CreateTime);
        Assert.Empty(gcp.Find("Ok")!.Labels);
        Assert.Null(gcp.Find("Ok")!.Replication);
        Assert.Equal(new Dictionary<string, string> { ["ok"] = "1" }, gcp.Find("Odd")!.Labels);
        Assert.Equal("user-managed", gcp.Find("Odd")!.Replication);
    }

    [Theory]
    [InlineData("bad\\nid")]
    [InlineData("bad\\u0007id")]
    [InlineData("bad id")]
    [InlineData("bad.id")]
    public async Task Leaves_out_a_secret_whose_id_is_not_a_secret_manager_id_without_naming_it(string id)
    {
        var gcp = await Load(new FakeHttpHandler().Respond(List, $$"""{"secrets":[{{Secret(id)}},{{Secret("Ok_1-b")}}]}"""));

        Assert.Equal(["Ok_1-b"], gcp.Secrets.Select(s => s.Id));
        Assert.Equal("Left out 1 Google Cloud secret(s) whose id is not a valid Secret Manager secret id.", Assert.Single(gcp.Warnings));
    }

    [Fact]
    public async Task Leaves_out_a_secret_whose_id_is_longer_than_255_characters()
    {
        var gcp = await Load(new FakeHttpHandler().Respond(List, $$"""{"secrets":[{{Secret(new string('a', 10000))}},{{Secret(new string('b', 256))}},{{Secret(new string('c', 255))}}]}"""));

        Assert.Equal([new string('c', 255)], gcp.Secrets.Select(s => s.Id));
        Assert.Equal("Left out 2 Google Cloud secret(s) whose id is not a valid Secret Manager secret id.", Assert.Single(gcp.Warnings));
    }

    [Fact]
    public async Task Leaves_out_a_version_whose_id_is_not_a_number_without_naming_it()
    {
        var versions = string.Join(",", new[] { "1\\n2", "1\\u00072", "abc", new string('9', 10000), new string('0', 10000) + "1", "7" }
            .Select(id => $$"""{"name":"projects/123/secrets/A/versions/{{id}}","state":"ENABLED"}"""));
        var handler = new FakeHttpHandler()
            .Respond(List, $$"""{"secrets":[{{Secret("A")}}]}""")
            .Respond("/v1/projects/123/secrets/A/versions?pageSize=25", $$"""{"versions":[{{versions}}]}""");

        var gcp = await Load(handler, o => o.IncludeVersions = true);

        Assert.Equal("7", Assert.Single(gcp.Find("A")!.Versions!).Id);
        Assert.Equal("Left out 5 version(s) of Google Cloud secret A whose id is not a number.", Assert.Single(gcp.Warnings));
    }

    [Fact]
    public async Task Reads_past_property_names_that_are_not_valid_UTF16()
    {
        // TryGetProperty searches from the last property and throws on these names, so the guard must turn that into "not found".
        var items = await Load(new FakeHttpHandler().Respond(List, """{"secrets":[{"name":"projects/1/secrets/A","\ud800\u0041":1},{"name":"projects/1/secrets/B","labels":{"\ud800\u0041":"x","ok":"1"}}],"nextPageToken":"\ud800"}"""));
        var root = await Load(new FakeHttpHandler().Respond(List, """{"secrets":[{"name":"projects/1/secrets/A"}],"\ud800\u0041":1}"""));

        Assert.Equal(["B"], items.Secrets.Select(s => s.Id));
        Assert.Equal(new Dictionary<string, string> { ["ok"] = "1" }, items.Find("B")!.Labels);
        Assert.Empty(root.Secrets);
    }

    [Fact]
    public async Task Escapes_path_segments_and_never_follows_dot_segments()
    {
        var handler = new FakeHttpHandler()
            .Respond("/v1/projects/a%20b%3Fc/secrets?pageSize=250", """{"secrets":[{"name":"projects/p%2F/secrets/s"}]}""")
            .Respond("/v1/projects/p%252F/secrets/s/versions?pageSize=25", Versions((1, "ENABLED")));

        var gcp = await Load(handler, o =>
        {
            o.ProjectId = "a b?c";
            o.IncludeVersions = true;
        });

        Assert.Equal("1", Assert.Single(gcp.Secrets).LatestEnabledVersion);
    }

    [Fact]
    public async Task Checks_its_arguments()
    {
        using var client = new FakeHttpHandler().Client();
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentNullException>(() => GcpSecrets.LoadAsync(null!, client, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => GcpSecrets.LoadAsync(Options(), null!, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => GcpSecrets.LoadAsync(Options(o => o.ProjectId = " "), client, ct));
        var location = await Assert.ThrowsAsync<ArgumentException>(() => GcpSecrets.LoadAsync(Options(o => o.Location = "evil.com/x"), client, ct));
        Assert.StartsWith("Location must hold only lower-case letters, digits and dashes", location.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GcpSecrets.LoadAsync(Options(o => o.MaxSecrets = 0), client, ct));
        var http = await Assert.ThrowsAsync<ArgumentException>(() => GcpSecrets.LoadAsync(Options(o => o.ApiBaseAddress = new Uri("http://example.com/v1/")), client, ct));
        Assert.StartsWith("ApiBaseAddress must use https", http.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new GcpSecrets(" ", []));
        Assert.Throws<ArgumentNullException>(() => new GcpSecrets("p", null!));
        Assert.Throws<ArgumentNullException>(() => new GcpSecrets("p", []).Find(null!));
    }

    [Fact]
    public async Task Allows_plain_http_only_when_asked()
    {
        var handler = new FakeHttpHandler().Respond("/v1/projects/contoso-prod/secrets?pageSize=250", "{}");

        await Load(handler, o =>
        {
            o.ApiBaseAddress = new Uri("http://sm.internal/v1/");
            o.AllowUnencryptedHttp = true;
        });

        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Builds_a_snapshot_from_secrets_you_have_and_keeps_the_first_of_each_id()
    {
        var gcp = new GcpSecrets(" p ", [new GcpSecret { Id = "A", Project = "1" }, new GcpSecret { Id = "A", Project = "2" }], location: " ", prefix: "");

        Assert.Equal("1", Assert.Single(gcp.Secrets).Project);
        Assert.Equal("p", gcp.ProjectId);
        Assert.Null(gcp.Location);
        Assert.Null(gcp.Prefix);
        Assert.Empty(gcp.Warnings);
    }

    [Fact]
    public void Picks_the_newest_numbered_enabled_version()
    {
        var secret = new GcpSecret
        {
            Id = "S",
            Project = "p",
            Versions = [new("abc", GcpSecretVersionState.Enabled), new("1", GcpSecretVersionState.Enabled), new("9", GcpSecretVersionState.Disabled)],
        };

        Assert.Equal("1", secret.LatestEnabledVersion);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(" ", true)]
    [InlineData("europe-west1", true)]
    [InlineData("z09", true)]
    [InlineData(" us-central1 ", true)]
    [InlineData("Europe-West1", false)]
    [InlineData("a.b", false)]
    [InlineData("a/b", false)]
    [InlineData("a_b", false)]
    public void Accepts_only_locations_that_fit_in_a_host_name(string? location, bool valid)
    {
        Assert.Equal(valid, GcpSecretManagerOptions.IsValidLocation(location));
        Assert.False(GcpSecretManagerOptions.IsValidLocation(new string('a', 64)));
        Assert.True(GcpSecretManagerOptions.IsValidLocation(new string('a', 63)));
    }
}
