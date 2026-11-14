using System.Net;
using ConfigFerret.Desktop.Services;
using ConfigFerret.Tests.Shared;

namespace ConfigFerret.Desktop.Tests;

public class OctopusAccountTests
{
    private const string ApiKey = "API-ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Spaces = """
        [
          {"Id":"Spaces-1","Name":"Default","IsDefault":true},
          {"Id":"Spaces-2","Name":"Platform","IsDefault":false}
        ]
        """;

    [Fact]
    public async Task Lists_the_projects_of_the_default_space_sorted_by_name()
    {
        var handler = Handler()
            .Respond("/api/Spaces-1/projects/all", """
                [
                  {"Name":"payments","Slug":"payments"},
                  {"Name":"Orders","Slug":"orders-api"},
                  {"Name":"Billing","Slug":"billing"},
                  {"Slug":"nameless"}
                ]
                """);

        var projects = await Account(handler).ListProjectsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Billing", "Orders", "payments"], projects.Select(p => p.Name));
        Assert.Equal("orders-api", projects[1].Slug);
        Assert.Equal("Orders", projects[1].ToString());
    }

    [Theory]
    [InlineData("Platform")]
    [InlineData("platform")]
    [InlineData("Spaces-2")]
    [InlineData("  Platform  ")]
    public async Task Picks_a_named_space_by_name_or_id(string space)
    {
        var handler = Handler().Respond("/api/Spaces-2/projects/all", """[{"Name":"Edge","Slug":"edge"}]""");

        var projects = await Account(handler, space).ListProjectsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Edge", Assert.Single(projects).Name);
    }

    [Fact]
    public async Task Fails_when_the_named_space_does_not_exist()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Account(Handler(), "Nowhere").ListProjectsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Nowhere", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_when_there_is_no_default_space()
    {
        var handler = new FakeHttpHandler().Respond("/api/spaces/all", """[{"Id":"Spaces-2","Name":"Platform","IsDefault":false}]""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Account(handler).ListProjectsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("default space", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sends_the_api_key_in_the_header_and_never_in_the_address()
    {
        var handler = Handler().Respond("/api/Spaces-1/projects/all", "[]");

        await Account(handler).ListProjectsAsync(TestContext.Current.CancellationToken);

        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(ApiKey, Assert.Single(r.Headers.GetValues("X-Octopus-ApiKey")));
            Assert.DoesNotContain(ApiKey, r.RequestUri!.ToString(), StringComparison.Ordinal);
            Assert.Null(r.Headers.Authorization);
        });
    }

    [Fact]
    public async Task Looks_the_space_up_once()
    {
        var handler = Handler()
            .Respond("/api/Spaces-1/projects/all", "[]")
            .Respond("/api/Spaces-1/environments/all", """[{"Name":"Development"},{"Name":"Production"}]""");
        var account = Account(handler);

        await account.ListProjectsAsync(TestContext.Current.CancellationToken);
        var environments = await account.ListEnvironmentsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Development", "Production"], environments);
        Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath == "/api/spaces/all");
    }

    [Theory]
    [InlineData("http://octopus.example.test/")]
    [InlineData("ftp://octopus.example.test/")]
    public void Refuses_a_server_address_that_is_not_https(string address)
    {
        var ex = Assert.Throws<ArgumentException>(() => new OctopusAccount(new HttpClient(), new Uri(address), ApiKey, null));

        Assert.DoesNotContain(ApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost:8065")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("https://octopus.example.test")]
    public void Accepts_https_and_loopback_addresses(string address)
    {
        _ = new OctopusAccount(new HttpClient(), new Uri(address), ApiKey, null);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "refused")]
    [InlineData(HttpStatusCode.Forbidden, "refused")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Error_messages_never_contain_the_api_key(HttpStatusCode status, string expected)
    {
        var handler = new FakeHttpHandler().Respond("/api/spaces/all", $"{{\"ErrorMessage\":\"bad {ApiKey}\"}}", status);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Account(handler).ListProjectsAsync(TestContext.Current.CancellationToken));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, ex.Message, StringComparison.Ordinal);
    }

    private static FakeHttpHandler Handler() => new FakeHttpHandler().Respond("/api/spaces/all", Spaces);

    private static OctopusAccount Account(FakeHttpHandler handler, string? space = null) =>
        new(handler.Client(), new Uri("https://octopus.example.test/"), ApiKey, space);
}
