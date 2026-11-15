using ConfigFerret.Sources;
using ConfigFerret.Tests.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.AzureDevOps.Tests;

public sealed class AzureDevOpsSourceTests : IDisposable
{
    private const string Token = "ado_bound_pat_never_printed";

    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["ConfigFerret:AzureDevOps:OrganizationUrl"] = "https://dev.azure.com/contoso/",
        ["ConfigFerret:AzureDevOps:Project"] = "api",
        ["ConfigFerret:AzureDevOps:GroupNames:0"] = "shared",
        ["ConfigFerret:AzureDevOps:GroupNames:1"] = "api-production",
        ["ConfigFerret:AzureDevOps:MatchByName"] = "false",
        ["ConfigFerret:AzureDevOps:Pipelines:0"] = "does-not-exist/azure-pipelines.yml",
        ["ConfigFerret:AzureDevOps:Links:Db__Timeout"] = "Db.Timeout",
    };

    private readonly ApiHandler _handler = new();
    private readonly HttpClient _client;

    public AzureDevOpsSourceTests()
    {
        _client = _handler.Client();
        _handler
            .Respond(AzureDevOpsVariableGroupsTests.Query("shared"), AzureDevOpsVariableGroupsTests.Page(AzureDevOpsVariableGroupsTests.Group(3, "shared", new Dictionary<string, object>
            {
                ["Db.Timeout"] = new { value = "10" },
                ["Unused.Flag"] = new { value = "on" },
            })))
            .Respond(AzureDevOpsVariableGroupsTests.Query("api-production"), AzureDevOpsVariableGroupsTests.Page(AzureDevOpsVariableGroupsTests.Group(7, "api-production", new Dictionary<string, object>
            {
                ["Db.Timeout"] = new { value = "30" },
                ["Db.Password"] = new { isSecret = true },
            })))
            .Respond(AzureDevOpsVariableGroupsTests.Query("kv"), AzureDevOpsVariableGroupsTests.Page(AzureDevOpsVariableGroupsTests.Group(9, "kv", new Dictionary<string, object>
            {
                ["ApiKey"] = new { },
            }, "AzureKeyVault", "contoso-prod")));
    }

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }

    private static IConfigurationRoot Configuration(string? token = Token, string tokenKey = "ConfigFerret:AzureDevOps:PersonalAccessToken", Action<IConfigurationBuilder>? more = null)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(Settings);
        more?.Invoke(builder);
        if (token is not null)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?> { [tokenKey] = token });
        }

        return builder.Build();
    }

    private static ServiceProvider Services(IConfigurationRoot configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddConfigFerretAzureDevOps(configuration.GetSection("ConfigFerret:AzureDevOps"));
        return services.BuildServiceProvider();
    }

    // ValidateOnStart runs these same validators when the host starts; reading the options runs them directly.
    private static AzureDevOpsOptions Validate(ServiceProvider provider) => provider.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value;

    private static OptionsValidationException Invalid(IConfigurationRoot configuration)
    {
        using var provider = Services(configuration);
        return Assert.Throws<OptionsValidationException>(() => Validate(provider));
    }

    [Fact]
    public void Binds_the_options_and_trace_options_from_the_section()
    {
        using var provider = Services(Configuration());

        var source = provider.GetRequiredService<AzureDevOpsSource>();

        Assert.Equal(new Uri("https://dev.azure.com/contoso/"), source.Options.OrganizationUrl);
        Assert.Equal("api", source.Options.Project);
        Assert.Equal(["shared", "api-production"], source.Options.GroupNames);
        Assert.Equal(Token, source.Options.PersonalAccessToken);
        Assert.False(source.TraceOptions.MatchByName);
        Assert.Equal(["does-not-exist/azure-pipelines.yml"], source.TraceOptions.Pipelines);
        Assert.Equal("Db.Timeout", source.TraceOptions.Links["Db__Timeout"]);
        Assert.Equal("Azure DevOps contoso/api", source.DisplayName);
        Assert.Same(source, provider.GetRequiredService<AzureDevOpsSource>());
    }

    [Fact]
    public async Task Loads_through_the_registered_source_and_links_layers()
    {
        using var provider = Services(Configuration());
        provider.GetRequiredService<AzureDevOpsSource>().HttpClient = _client;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Timeout"] = "30" }).Build();
        var tracer = ConfigTracer.For(configuration);

        Assert.Same(tracer, await tracer.WithAzureDevOpsAsync(provider, TestContext.Current.CancellationToken));

        var report = tracer.Trace();
        Assert.Contains(report.Warnings, w => w.StartsWith("Could not read Azure Pipelines file does-not-exist/azure-pipelines.yml", StringComparison.Ordinal));
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Says_how_to_register_the_source_when_it_is_missing()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tracer.WithAzureDevOpsAsync(provider, TestContext.Current.CancellationToken));

        Assert.Contains("AddConfigFerretAzureDevOps", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentNullException>(() => tracer.WithAzureDevOpsAsync(null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AzureDevOpsTracerExtensions.WithAzureDevOpsAsync(null!, provider, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Accepts_a_token_from_a_source_that_is_not_an_appsettings_file()
    {
        using var provider = Services(Configuration());
        Assert.Equal(Token, Validate(provider).PersonalAccessToken);

        using var bearer = Services(Configuration(tokenKey: "ConfigFerret:AzureDevOps:AccessToken"));
        Assert.Equal(Token, Validate(bearer).AccessToken);
    }

    [Fact]
    public void Fails_at_startup_without_a_token_and_never_prints_one()
    {
        var ex = Invalid(Configuration(token: null));

        var failure = Assert.Single(ex.Failures);
        Assert.StartsWith("ConfigFerret:AzureDevOps:PersonalAccessToken is not set.", failure, StringComparison.Ordinal);
        Assert.EndsWith("Or set ConfigFerret:AzureDevOps:AccessToken to a Microsoft Entra ID access token.", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Fails_at_startup_with_both_tokens()
    {
        var ex = Invalid(Configuration(more: b => b.AddInMemoryCollection(new Dictionary<string, string?> { ["ConfigFerret:AzureDevOps:AccessToken"] = "entra" })));

        Assert.Equal("ConfigFerret:AzureDevOps:PersonalAccessToken and ConfigFerret:AzureDevOps:AccessToken are both set. Set only one.", Assert.Single(ex.Failures));
    }

    [Theory]
    [InlineData("PersonalAccessToken")]
    [InlineData("AccessToken")]
    public void Fails_at_startup_when_a_token_is_in_an_appsettings_file(string name)
    {
        using var folder = new TempSettingsFolder();
        var file = folder.Write("appsettings.Production.json", $$"""{ "ConfigFerret": { "AzureDevOps": { "{{name}}": "{{Token}}" } } }""");

        var ex = Invalid(Configuration(token: name == "AccessToken" ? null : Token, more: b => b.AddJsonFile(file)));

        var failure = Assert.Single(ex.Failures);
        Assert.StartsWith($"ConfigFerret:AzureDevOps:{name} is set in appsettings.Production.json.", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Fails_at_startup_without_an_organization_url_or_project()
    {
        var ex = Invalid(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConfigFerret:AzureDevOps:PersonalAccessToken"] = Token }).Build());

        Assert.Equal(2, ex.Failures.Count());
        Assert.StartsWith("ConfigFerret:AzureDevOps:OrganizationUrl is not set.", ex.Failures.First(), StringComparison.Ordinal);
        Assert.StartsWith("ConfigFerret:AzureDevOps:Project is not set.", ex.Failures.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void Fails_at_startup_with_an_http_organization_url()
    {
        var ex = Invalid(Configuration(more: b => b.AddInMemoryCollection(new Dictionary<string, string?> { ["ConfigFerret:AzureDevOps:OrganizationUrl"] = "http://tfs.example.com/tfs/" })));

        Assert.StartsWith("ConfigFerret:AzureDevOps:OrganizationUrl must use https", Assert.Single(ex.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Validates_options_bound_at_the_root_and_checks_its_arguments()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Project"] = "api", ["PersonalAccessToken"] = Token }).Build();
        var services = new ServiceCollection();
        services.AddConfigFerretAzureDevOps(configuration);
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => Validate(provider));
        Assert.StartsWith("OrganizationUrl is not set.", Assert.Single(ex.Failures), StringComparison.Ordinal);

        Assert.Throws<ArgumentNullException>(() => AzureDevOpsServiceCollectionExtensions.AddConfigFerretAzureDevOps(null!, configuration));
        Assert.Throws<ArgumentNullException>(() => services.AddConfigFerretAzureDevOps(null!));
        Assert.Throws<ArgumentNullException>(() => new AzureDevOpsOptionsValidator("", null).Validate(null, null!));
        Assert.Throws<ArgumentNullException>(() => new AzureDevOpsSource(null!, Options.Create(new AzureDevOpsTraceOptions())));
        Assert.Throws<ArgumentNullException>(() => new AzureDevOpsSource(Options.Create(new AzureDevOpsOptions()), null!));
        Assert.Throws<ArgumentNullException>(() => AzureDevOpsSource.Create(null!));
    }

    [Fact]
    public void Describes_the_provider_settings()
    {
        var provider = new AzureDevOpsSourceProvider();

        Assert.Equal("azure-devops-variable-groups", provider.Id);
        Assert.Equal("Azure DevOps variable groups", provider.DisplayName);
        Assert.Equal(SourceCategory.CiCd, provider.Category);
        Assert.Equal(
            ["OrganizationUrl", "Project", "GroupNames", "PersonalAccessToken", "AccessToken", "Pipelines", "MatchByName", "AllowUnencryptedHttp"],
            provider.Settings.Select(s => s.Name));
        var pat = provider.Settings.Single(s => s.Name == "PersonalAccessToken");
        Assert.Equal(SourceSettingKind.Secret, pat.Kind);
        Assert.Equal(["AZURE_DEVOPS_EXT_PAT", "SYSTEM_ACCESSTOKEN"], pat.EnvironmentVariables);
        Assert.Equal(SourceSettingKind.Secret, provider.Settings.Single(s => s.Name == "AccessToken").Kind);
        Assert.All(provider.Settings.Where(s => s.Name is "OrganizationUrl" or "Project"), s => Assert.True(s.Required));
    }

    [Fact]
    public void Creates_a_source_from_settings()
    {
        var source = (AzureDevOpsSource)new AzureDevOpsSourceProvider().Create(new Dictionary<string, string?>
        {
            ["OrganizationUrl"] = " https://dev.azure.com/contoso ",
            ["Project"] = " api ",
            ["GroupNames"] = "shared\n\n api-production \n",
            ["PersonalAccessToken"] = Token,
            ["Pipelines"] = "azure-pipelines.yml\ndeploy.yml",
            ["MatchByName"] = "false",
            ["AllowUnencryptedHttp"] = "true",
        });

        Assert.Equal(new Uri("https://dev.azure.com/contoso"), source.Options.OrganizationUrl);
        Assert.Equal("api", source.Options.Project);
        Assert.Equal(["shared", "api-production"], source.Options.GroupNames);
        Assert.Equal(Token, source.Options.PersonalAccessToken);
        Assert.Null(source.Options.AccessToken);
        Assert.True(source.Options.AllowUnencryptedHttp);
        Assert.False(source.TraceOptions.MatchByName);
        Assert.Equal(["azure-pipelines.yml", "deploy.yml"], source.TraceOptions.Pipelines);

        var defaults = (AzureDevOpsSource)new AzureDevOpsSourceProvider().Create(new Dictionary<string, string?>
        {
            ["OrganizationUrl"] = "https://dev.azure.com/contoso/",
            ["Project"] = "api",
            ["AccessToken"] = "entra",
        });
        Assert.Empty(defaults.Options.GroupNames);
        Assert.True(defaults.TraceOptions.MatchByName);
        Assert.False(defaults.Options.AllowUnencryptedHttp);
        Assert.Equal("entra", defaults.Options.AccessToken);
    }

    [Theory]
    [InlineData(null, "api", Token, null, "OrganizationUrl is required.")]
    [InlineData("dev.azure.com/contoso", "api", Token, null, "OrganizationUrl must be an absolute URL.")]
    [InlineData("http://tfs.example.com/", "api", Token, null, "OrganizationUrl must use https")]
    [InlineData("https://dev.azure.com/contoso/", " ", Token, null, "Project is required.")]
    [InlineData("https://dev.azure.com/contoso/", "api", null, null, "PersonalAccessToken or AccessToken is required.")]
    [InlineData("https://dev.azure.com/contoso/", "api", Token, "entra", "Set PersonalAccessToken or AccessToken, not both.")]
    public void Refuses_missing_or_invalid_settings(string? url, string? project, string? pat, string? bearer, string message)
    {
        var ex = Assert.Throws<ArgumentException>(() => new AzureDevOpsSourceProvider().Create(new Dictionary<string, string?>
        {
            ["OrganizationUrl"] = url,
            ["Project"] = project,
            ["PersonalAccessToken"] = pat,
            ["AccessToken"] = bearer,
        }));

        Assert.StartsWith(message, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_boolean_that_is_not_true_or_false()
    {
        var ex = Assert.Throws<ArgumentException>(() => new AzureDevOpsSourceProvider().Create(new Dictionary<string, string?>
        {
            ["OrganizationUrl"] = "https://dev.azure.com/contoso/",
            ["Project"] = "api",
            ["PersonalAccessToken"] = Token,
            ["MatchByName"] = "yes",
        }));

        Assert.StartsWith("MatchByName must be true or false.", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => new AzureDevOpsSourceProvider().Create(null!));
    }

    [Fact]
    public async Task Loads_as_a_config_source_and_lists_unused_entries()
    {
        var options = AzureDevOpsVariableGroupsTests.Options("shared", "api-production", "kv");
        const string prefix = "CFADOSRC_";
        var source = AzureDevOpsSource.Create(options, new AzureDevOpsTraceOptions().Map(prefix + "Db__Timeout", "Db.Timeout"));
        source.HttpClient = _client;
        Environment.SetEnvironmentVariable(prefix + "Db__Timeout", "30");
        try
        {
            var tracer = ConfigTracer.For(new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build())
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "Db__Timeout"] = "30" });
            var loaded = await tracer.WithSourceAsync(source, TestContext.Current.CancellationToken);
            var report = tracer.Trace();

            Assert.Equal("Azure DevOps contoso/api", loaded.DisplayName);
            Assert.Equal(5, loaded.Entries.Count);
            var kv = loaded.Entries.Single(e => e.Name == "ApiKey");
            Assert.True(kv.IsSensitive);
            Assert.Equal("contoso-prod", kv.Location);
            Assert.Equal("variable group kv, linked to Azure Key Vault contoso-prod", kv.Detail);
            var password = loaded.Entries.Single(e => e.Name == "Db.Password");
            Assert.Equal("variable group api-production, secret", password.Detail);
            Assert.Equal("api-production", password.Location);
            Assert.Equal("variable group shared", loaded.Entries.Single(e => e.Name == "Unused.Flag").Detail);

            Assert.Equal(["Unused.Flag", "Db.Password", "ApiKey"], loaded.FindUnused(report).Select(e => e.Name));
            Assert.Empty(loaded.Warnings);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Db__Timeout", null);
        }
    }

    [Fact]
    public void Names_a_source_without_an_organization_url_by_its_project()
    {
        Assert.Equal("Azure DevOps api", AzureDevOpsSource.Create(new AzureDevOpsOptions { Project = "api" }).DisplayName);
    }

    [Fact]
    public async Task Loads_with_its_own_client_when_none_is_set()
    {
        var options = AzureDevOpsVariableGroupsTests.Options("a");
        options.OrganizationUrl = new Uri("https://localhost:1/contoso/");
        IConfigSource source = AzureDevOpsSource.Create(options);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.LoadAsync(TestContext.Current.CancellationToken));
    }
}
