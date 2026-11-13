using ConfigFerret.Tests.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.GitHub.Tests;

public class GitHubServiceCollectionTests
{
    private const string Token = "ghp_bound_token_never_printed";

    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["ConfigFerret:GitHub:Owner"] = "contoso",
        ["ConfigFerret:GitHub:Repository"] = "api",
        ["ConfigFerret:GitHub:Environment"] = "production",
        ["ConfigFerret:GitHub:IncludeOrganization"] = "false",
        ["ConfigFerret:GitHub:IncludeSecrets"] = "false",
        ["ConfigFerret:GitHub:ApiBaseAddress"] = "https://github.example.com/api/v3/",
        ["ConfigFerret:GitHub:MatchByName"] = "false",
        ["ConfigFerret:GitHub:Workflows:0"] = "does-not-exist/deploy.yml",
        ["ConfigFerret:GitHub:Links:Db__Timeout"] = "DB_TIMEOUT",
        ["ConfigFerret:GitHub:Links:Db__Password"] = "secrets.DB_PASSWORD",
    };

    // The token comes from a source that stands in for user secrets or an environment variable.
    private static IConfigurationRoot Configuration(string? token = Token, Action<IConfigurationBuilder>? more = null)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(Settings);
        more?.Invoke(builder);
        if (token is not null)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?> { ["ConfigFerret:GitHub:Token"] = token });
        }

        return builder.Build();
    }

    private static ServiceProvider Services(IConfigurationRoot configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddConfigFerretGitHub(configuration.GetSection("ConfigFerret:GitHub"));
        return services.BuildServiceProvider();
    }

    // ValidateOnStart runs these same validators when the host starts. Reading the options runs them directly, and
    // works on .NET 10 and 11 (IStartupValidator is obsolete on 11).
    private static GitHubVariablesOptions Validate(ServiceProvider provider) => provider.GetRequiredService<IOptions<GitHubVariablesOptions>>().Value;

    private static OptionsValidationException StartupFailure(ServiceProvider provider) =>
        Assert.Throws<OptionsValidationException>(() => Validate(provider));

    [Fact]
    public void Binds_the_connection_and_trace_options_from_the_section()
    {
        using var provider = Services(Configuration());

        var source = provider.GetRequiredService<GitHubVariablesSource>();
        Assert.Equal("contoso", source.Options.Owner);
        Assert.Equal("api", source.Options.Repository);
        Assert.Equal("production", source.Options.Environment);
        Assert.Equal(Token, source.Options.Token);
        Assert.False(source.Options.IncludeOrganization);
        Assert.False(source.Options.IncludeSecrets);
        Assert.Equal(new Uri("https://github.example.com/api/v3/"), source.Options.ApiBaseAddress);

        Assert.False(source.TraceOptions.MatchByName);
        Assert.Equal(["does-not-exist/deploy.yml"], source.TraceOptions.Workflows);
        Assert.Equal("DB_TIMEOUT", source.TraceOptions.Links["db__timeout"]);
        Assert.Equal(GitHubVariableKind.Secret, source.TraceOptions.FindMapping("Db__Password")!.Kind);
        Assert.Equal("DB_PASSWORD", source.TraceOptions.FindMapping("Db__Password")!.VariableName);

        Validate(provider);
    }

    [Fact]
    public void Fails_at_startup_without_a_token_and_says_where_to_put_it()
    {
        using var provider = Services(Configuration(token: null));

        var message = StartupFailure(provider).Message;

        Assert.Contains("ConfigFerret:GitHub:Token is not set", message, StringComparison.Ordinal);
        Assert.Contains("dotnet user-secrets set \"ConfigFerret:GitHub:Token\"", message, StringComparison.Ordinal);
        Assert.Contains("ConfigFerret__GitHub__Token", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Production.json")]
    public void Fails_at_startup_when_the_token_is_in_an_appsettings_file_even_if_overridden(string fileName)
    {
        using var folder = new TempSettingsFolder();
        var file = folder.Write(fileName, $$"""{ "ConfigFerret": { "GitHub": { "Token": "{{Token}}-committed" } } }""");
        using var provider = Services(Configuration(more: b => b.AddJsonFile(file)));

        var message = StartupFailure(provider).Message;

        Assert.Contains($"ConfigFerret:GitHub:Token is set in {fileName}", message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_a_token_from_a_user_secrets_file()
    {
        using var folder = new TempSettingsFolder();
        var file = folder.Write("secrets.json", $$"""{ "ConfigFerret": { "GitHub": { "Token": "{{Token}}" } } }""");
        using var provider = Services(Configuration(token: null, more: b => b.AddJsonFile(file)));

        Validate(provider);
    }

    [Fact]
    public void Fails_at_startup_for_missing_names_and_an_unsafe_address_without_printing_the_token()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConfigFerret:GitHub:ApiBaseAddress"] = "http://github.example.com/api/v3/",
            ["ConfigFerret:GitHub:Token"] = Token,
        }).Build();
        using var provider = Services(configuration);

        var ex = StartupFailure(provider);

        Assert.Equal(3, ex.Failures.Count());
        Assert.Contains(ex.Failures, f => f.StartsWith("ConfigFerret:GitHub:Owner is not set", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, f => f.StartsWith("ConfigFerret:GitHub:Repository is not set", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, f => f.StartsWith("ConfigFerret:GitHub:ApiBaseAddress must use https", StringComparison.Ordinal));
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Traces_with_the_bound_options_and_links()
    {
        const string prefix = "CFGHDI_";
        Environment.SetEnvironmentVariable(prefix + "Db__Timeout", "30");
        try
        {
            using var provider = Services(Configuration());
            var handler = new FakeHttpHandler()
                .Respond("/api/v3/repos/contoso/api/actions/variables?per_page=30&page=1", """{"total_count":0,"variables":[]}""")
                .Respond("/api/v3/repos/contoso/api/environments/production/variables?per_page=30&page=1", """{"total_count":1,"variables":[{"name":"DB_TIMEOUT","value":"30"}]}""");
            using var client = handler.Client();
            var source = provider.GetRequiredService<GitHubVariablesSource>();
            source.HttpClient = client;
            source.TraceOptions.Links[prefix + "Db__Timeout"] = "DB_TIMEOUT";

            var tracer = ConfigTracer.For(new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build())
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "Db__Timeout"] = "30" });
            await tracer.WithGitHubAsync(provider, TestContext.Current.CancellationToken);
            var report = tracer.Trace();

            Assert.Equal($"set in environment variable {prefix}Db__Timeout from GitHub environment variable DB_TIMEOUT", report.Find("Db:Timeout")!.Story);
            Assert.Contains(report.Warnings, w => w.StartsWith("Could not read GitHub workflow does-not-exist/deploy.yml", StringComparison.Ordinal));
            Assert.All(handler.Requests, r => Assert.Equal(Token, r.Headers.Authorization?.Parameter));
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Db__Timeout", null);
        }
    }

    [Fact]
    public async Task Says_how_to_register_when_the_source_is_missing()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ConfigTracer.For(new ConfigurationBuilder().Build()).WithGitHubAsync(provider, TestContext.Current.CancellationToken));

        Assert.Contains("AddConfigFerretGitHub", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_checks_its_argument_and_passes_valid_options()
    {
        var validator = new GitHubOptionsValidator("ConfigFerret:GitHub", null);

        Assert.Throws<ArgumentNullException>(() => validator.Validate(null, null!));
        Assert.Same(ValidateOptionsResult.Success, validator.Validate(null, new GitHubVariablesOptions { Owner = "contoso", Repository = "api", Token = "t" }));
    }

    [Fact]
    public void Registration_checks_its_arguments()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<ArgumentNullException>(() => GitHubServiceCollectionExtensions.AddConfigFerretGitHub(null!, configuration));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddConfigFerretGitHub(null!));
    }

    [Fact]
    public void Names_keys_without_a_prefix_when_bound_from_the_root()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddConfigFerretGitHub(configuration);
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GitHubVariablesOptions>>().Value);

        Assert.Contains(failure.Failures, f => f.StartsWith("Token is not set.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Source_checks_its_arguments_and_loads_without_a_test_client()
    {
        var options = new GitHubVariablesOptions { Owner = "contoso", Repository = "api", Token = "t" };
        var source = new GitHubVariablesSource(Microsoft.Extensions.Options.Options.Create(options), Microsoft.Extensions.Options.Options.Create(new GitHubTraceOptions()));

        Assert.Throws<ArgumentNullException>(() => new GitHubVariablesSource(null!, Microsoft.Extensions.Options.Options.Create(new GitHubTraceOptions())));
        Assert.Throws<ArgumentNullException>(() => new GitHubVariablesSource(Microsoft.Extensions.Options.Options.Create(options), null!));

        // Without a test client the source loads through its own, which refuses an http address before sending anything.
        var o = options;
        o.ApiBaseAddress = new Uri("http://github.example.com/");
        await Assert.ThrowsAsync<ArgumentException>(() => source.LoadAsync(TestContext.Current.CancellationToken));
    }
}
