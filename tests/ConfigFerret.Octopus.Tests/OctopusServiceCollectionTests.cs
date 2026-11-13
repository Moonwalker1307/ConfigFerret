using ConfigFerret.Tests.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Octopus.Tests;

public class OctopusServiceCollectionTests
{
    private const string ApiKey = "API-BOUNDKEYNEVERPRINTED";

    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["ConfigFerret:Octopus:ServerUri"] = "https://octopus.example.com/",
        ["ConfigFerret:Octopus:Space"] = "Spaces-1",
        ["ConfigFerret:Octopus:Project"] = "billing-api",
        ["ConfigFerret:Octopus:IncludeLibraryVariableSets"] = "false",
        ["ConfigFerret:Octopus:Environment"] = "Production",
        ["ConfigFerret:Octopus:Machine"] = "web-01",
        ["ConfigFerret:Octopus:Roles:0"] = "web",
        ["ConfigFerret:Octopus:Roles:1"] = "api",
        ["ConfigFerret:Octopus:Channel"] = "Default",
        ["ConfigFerret:Octopus:Step"] = "Deploy",
        ["ConfigFerret:Octopus:TenantTags:0"] = "Region/West",
        ["ConfigFerret:Octopus:MatchByName"] = "false",
        ["ConfigFerret:Octopus:Links:DB__TIMEOUT"] = "Db:Timeout",
    };

    private static IConfigurationRoot Configuration(string? apiKey = ApiKey, Action<IConfigurationBuilder>? more = null)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(Settings);
        more?.Invoke(builder);
        if (apiKey is not null)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?> { ["ConfigFerret:Octopus:ApiKey"] = apiKey });
        }

        return builder.Build();
    }

    private static ServiceProvider Services(IConfigurationRoot configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddConfigFerretOctopus(configuration.GetSection("ConfigFerret:Octopus"));
        return services.BuildServiceProvider();
    }

    private static OptionsValidationException StartupFailure(ServiceProvider provider) =>
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

    [Fact]
    public void Binds_the_connection_options_and_the_deployment_from_the_section()
    {
        using var provider = Services(Configuration());

        var source = provider.GetRequiredService<OctopusVariablesSource>();
        Assert.Equal(new Uri("https://octopus.example.com/"), source.Options.ServerUri);
        Assert.Equal("Spaces-1", source.Options.Space);
        Assert.Equal("billing-api", source.Options.Project);
        Assert.Equal(ApiKey, source.Options.ApiKey);
        Assert.False(source.Options.IncludeLibraryVariableSets);

        var trace = source.TraceOptions;
        Assert.Equal("Production", trace.Environment);
        Assert.Equal("web-01", trace.Machine);
        Assert.Equal(["web", "api"], trace.Roles.Order(StringComparer.Ordinal).Reverse());
        Assert.Equal("Default", trace.Channel);
        Assert.Equal("Deploy", trace.Step);
        Assert.Equal(["Region/West"], trace.TenantTags);
        Assert.False(trace.MatchByName);
        Assert.Equal("Db:Timeout", trace.FindMapping("db__timeout"));

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void Fails_at_startup_without_an_API_key_and_says_where_to_put_it()
    {
        using var provider = Services(Configuration(apiKey: null));

        var message = StartupFailure(provider).Message;

        Assert.Contains("ConfigFerret:Octopus:ApiKey is not set", message, StringComparison.Ordinal);
        Assert.Contains("ConfigFerret__Octopus__ApiKey", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fails_at_startup_when_the_API_key_is_in_appsettings_json_even_if_overridden()
    {
        using var folder = new TempSettingsFolder();
        var file = folder.Write("appsettings.json", $$"""{ "ConfigFerret": { "Octopus": { "ApiKey": "{{ApiKey}}" } } }""");
        using var provider = Services(Configuration(more: b => b.AddJsonFile(file)));

        var message = StartupFailure(provider).Message;

        Assert.Contains("ConfigFerret:Octopus:ApiKey is set in appsettings.json", message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Finds_an_API_key_in_an_appsettings_file_behind_a_chained_configuration()
    {
        using var folder = new TempSettingsFolder();
        var file = folder.Write("appsettings.Development.json", $$"""{ "ConfigFerret": { "Octopus": { "ApiKey": "{{ApiKey}}" } } }""");
        var inner = new ConfigurationBuilder().AddJsonFile(file).Build();
        using var provider = Services(Configuration(apiKey: null, more: b => b.AddConfiguration(inner)));

        Assert.Contains("is set in appsettings.Development.json", StartupFailure(provider).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fails_at_startup_for_a_missing_server_and_project()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConfigFerret:Octopus:ApiKey"] = ApiKey }).Build();
        using var provider = Services(configuration);

        var ex = StartupFailure(provider);

        Assert.Equal(2, ex.Failures.Count());
        Assert.Contains(ex.Failures, f => f.StartsWith("ConfigFerret:Octopus:ServerUri is not set", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, f => f.StartsWith("ConfigFerret:Octopus:Project is not set", StringComparison.Ordinal));
    }

    [Fact]
    public void Fails_at_startup_for_a_plain_http_server()
    {
        using var provider = Services(Configuration(more: b => b.AddInMemoryCollection(new Dictionary<string, string?> { ["ConfigFerret:Octopus:ServerUri"] = "http://octopus.example.com/" })));

        Assert.Contains("ConfigFerret:Octopus:ServerUri must use https", StartupFailure(provider).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Traces_with_the_bound_options_and_scope()
    {
        using var folder = new TempSettingsFolder();
        var settings = folder.Write("appsettings.json", """{ "Db": { "Timeout": "30" } }""");
        using var provider = Services(Configuration());
        var handler = new FakeHttpHandler()
            .Respond("/api/Spaces-1/projects/all", """[{"Id":"Projects-7","Name":"Billing API","Slug":"billing-api","VariableSetId":"vs-7","IncludedLibraryVariableSetIds":[]}]""")
            .Respond("/api/Spaces-1/variables/vs-7", """
                {"Variables":[
                  {"Name":"Db:Timeout","Value":"20","Type":"String","Scope":{"Environment":["Environments-1"]}},
                  {"Name":"Db:Timeout","Value":"30","Type":"String","Scope":{"Environment":["Environments-2"]}}
                ],"ScopeValues":{"Environments":[{"Id":"Environments-1","Name":"Staging"},{"Id":"Environments-2","Name":"Production"}]}}
                """);
        using var client = handler.Client();
        var source = provider.GetRequiredService<OctopusVariablesSource>();
        source.HttpClient = client;

        var tracer = ConfigTracer.For(new ConfigurationBuilder().AddJsonFile(settings).Build());
        await tracer.WithOctopusAsync(provider, TestContext.Current.CancellationToken);
        var origin = Assert.Single(tracer.Trace().Find("Db:Timeout")!.Steps[0].Origins);

        Assert.Equal("30", origin.Value);
        Assert.All(handler.Requests, r => Assert.Equal(ApiKey, Assert.Single(r.Headers.GetValues("X-Octopus-ApiKey"))));
    }
}
