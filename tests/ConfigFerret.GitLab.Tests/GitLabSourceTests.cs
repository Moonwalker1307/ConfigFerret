using ConfigFerret.Sources;
using ConfigFerret.Tests.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.GitLab.Tests;

public sealed class GitLabSourceTests : IDisposable
{
    private const string Token = "glpat-bound_never_printed";

    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["ConfigFerret:GitLab:Project"] = "contoso/platform/api",
        ["ConfigFerret:GitLab:BaseAddress"] = "https://gitlab.example.com/",
        ["ConfigFerret:GitLab:IncludeGroups"] = "false",
        ["ConfigFerret:GitLab:IncludeInstance"] = "true",
        ["ConfigFerret:GitLab:IncludeMaskedValues"] = "true",
        ["ConfigFerret:GitLab:Environment"] = "production",
        ["ConfigFerret:GitLab:MatchByName"] = "false",
        ["ConfigFerret:GitLab:CiFiles:0"] = "does-not-exist/.gitlab-ci.yml",
        ["ConfigFerret:GitLab:Links:Db__Timeout"] = "DB_TIMEOUT",
    };

    private readonly ApiHandler _handler = new();
    private readonly HttpClient _client;

    public GitLabSourceTests()
    {
        _client = _handler.Client();
        GitLabVariablesTests.Full(_handler);
    }

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }

    private static IConfigurationRoot Configuration(string? token = Token, Action<IConfigurationBuilder>? more = null)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(Settings);
        more?.Invoke(builder);
        if (token is not null)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?> { ["ConfigFerret:GitLab:Token"] = token });
        }

        return builder.Build();
    }

    private static ServiceProvider Services(IConfigurationRoot configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddConfigFerretGitLab(configuration.GetSection("ConfigFerret:GitLab"));
        return services.BuildServiceProvider();
    }

    // ValidateOnStart runs these same validators when the host starts; reading the options runs them directly.
    private static GitLabOptions Validate(ServiceProvider provider) => provider.GetRequiredService<IOptions<GitLabOptions>>().Value;

    private static OptionsValidationException Invalid(IConfigurationRoot configuration)
    {
        using var provider = Services(configuration);
        return Assert.Throws<OptionsValidationException>(() => Validate(provider));
    }

    [Fact]
    public void Binds_the_options_and_trace_options_from_the_section()
    {
        using var provider = Services(Configuration());

        var source = provider.GetRequiredService<GitLabSource>();

        Assert.Equal("contoso/platform/api", source.Options.Project);
        Assert.Equal(new Uri("https://gitlab.example.com/"), source.Options.BaseAddress);
        Assert.Equal(Token, source.Options.Token);
        Assert.False(source.Options.IncludeGroups);
        Assert.True(source.Options.IncludeInstance);
        Assert.True(source.Options.IncludeMaskedValues);
        Assert.Equal("production", source.TraceOptions.Environment);
        Assert.False(source.TraceOptions.MatchByName);
        Assert.Equal(["does-not-exist/.gitlab-ci.yml"], source.TraceOptions.CiFiles);
        Assert.Equal("DB_TIMEOUT", source.TraceOptions.Links["Db__Timeout"]);
        Assert.Equal("GitLab contoso/platform/api (production)", source.DisplayName);
        Assert.Same(source, provider.GetRequiredService<GitLabSource>());
    }

    [Fact]
    public async Task Loads_through_the_registered_source_and_warns_about_a_missing_ci_file()
    {
        using var provider = Services(Configuration(more: b => b.AddInMemoryCollection(new Dictionary<string, string?> { ["ConfigFerret:GitLab:BaseAddress"] = "https://gitlab.com/" })));
        provider.GetRequiredService<GitLabSource>().HttpClient = _client;
        var tracer = ConfigTracer.For(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Timeout"] = "30" }).Build());

        Assert.Same(tracer, await tracer.WithGitLabAsync(provider, TestContext.Current.CancellationToken));

        var report = tracer.Trace();
        Assert.Contains(report.Warnings, w => w.StartsWith("Could not read GitLab CI file does-not-exist/.gitlab-ci.yml", StringComparison.Ordinal));
        Assert.Contains(_handler.Paths, p => p.StartsWith("/api/v4/admin/ci/variables", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Says_how_to_register_the_source_when_it_is_missing()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tracer.WithGitLabAsync(provider, TestContext.Current.CancellationToken));

        Assert.Contains("AddConfigFerretGitLab", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentNullException>(() => tracer.WithGitLabAsync(null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => GitLabTracerExtensions.WithGitLabAsync(null!, provider, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Accepts_a_token_from_a_source_that_is_not_an_appsettings_file()
    {
        using var provider = Services(Configuration());

        Assert.Equal(Token, Validate(provider).Token);
    }

    [Fact]
    public void Fails_at_startup_without_a_token()
    {
        var ex = Invalid(Configuration(token: null));

        Assert.StartsWith("ConfigFerret:GitLab:Token is not set.", Assert.Single(ex.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Fails_at_startup_when_the_token_is_in_an_appsettings_file()
    {
        using var folder = new TempSettingsFolder();
        var file = folder.Write("appsettings.json", $$"""{ "ConfigFerret": { "GitLab": { "Token": "{{Token}}" } } }""");

        var ex = Invalid(Configuration(more: b => b.AddJsonFile(file)));

        var failure = Assert.Single(ex.Failures);
        Assert.StartsWith("ConfigFerret:GitLab:Token is set in appsettings.json.", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Fails_at_startup_without_a_project_or_with_an_http_address()
    {
        var ex = Invalid(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConfigFerret:GitLab:Token"] = Token,
            ["ConfigFerret:GitLab:BaseAddress"] = "http://gitlab.example.com/",
        }).Build());

        Assert.Equal(2, ex.Failures.Count());
        Assert.StartsWith("ConfigFerret:GitLab:Project is not set.", ex.Failures.First(), StringComparison.Ordinal);
        Assert.StartsWith("ConfigFerret:GitLab:BaseAddress must use https", ex.Failures.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void Validates_options_bound_at_the_root_and_checks_its_arguments()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Token"] = Token }).Build();
        var services = new ServiceCollection();
        services.AddConfigFerretGitLab(configuration);
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => Validate(provider));
        Assert.StartsWith("Project is not set.", Assert.Single(ex.Failures), StringComparison.Ordinal);

        Assert.Throws<ArgumentNullException>(() => GitLabServiceCollectionExtensions.AddConfigFerretGitLab(null!, configuration));
        Assert.Throws<ArgumentNullException>(() => services.AddConfigFerretGitLab(null!));
        Assert.Throws<ArgumentNullException>(() => new GitLabOptionsValidator("", null).Validate(null, null!));
        Assert.Throws<ArgumentNullException>(() => new GitLabSource(null!, Options.Create(new GitLabTraceOptions())));
        Assert.Throws<ArgumentNullException>(() => new GitLabSource(Options.Create(new GitLabOptions()), null!));
        Assert.Throws<ArgumentNullException>(() => GitLabSource.Create(null!));
    }

    [Fact]
    public void Describes_the_provider_settings()
    {
        var provider = new GitLabSourceProvider();

        Assert.Equal("gitlab-ci-variables", provider.Id);
        Assert.Equal("GitLab CI/CD variables", provider.DisplayName);
        Assert.Equal(SourceCategory.CiCd, provider.Category);
        Assert.Equal(
            ["BaseAddress", "Project", "Token", "Environment", "IncludeGroups", "IncludeInstance", "IncludeMaskedValues", "CiFiles", "MatchByName", "AllowUnencryptedHttp"],
            provider.Settings.Select(s => s.Name));
        var token = provider.Settings.Single(s => s.Name == "Token");
        Assert.Equal(SourceSettingKind.Secret, token.Kind);
        Assert.True(token.Required);
        Assert.Equal(["GITLAB_TOKEN"], token.EnvironmentVariables);
        Assert.Equal("https://gitlab.com/", provider.Settings[0].DefaultValue);
    }

    [Fact]
    public void Creates_a_source_from_settings()
    {
        var source = (GitLabSource)new GitLabSourceProvider().Create(new Dictionary<string, string?>
        {
            ["BaseAddress"] = "http://gitlab.internal/",
            ["Project"] = " 42 ",
            ["Token"] = Token,
            ["Environment"] = " review/x ",
            ["IncludeGroups"] = "false",
            ["IncludeInstance"] = "TRUE",
            ["IncludeMaskedValues"] = "true",
            ["CiFiles"] = ".gitlab-ci.yml\n ci/deploy.yml ",
            ["MatchByName"] = "false",
            ["AllowUnencryptedHttp"] = "true",
        });

        Assert.Equal(new Uri("http://gitlab.internal/"), source.Options.BaseAddress);
        Assert.Equal("42", source.Options.Project);
        Assert.Equal(Token, source.Options.Token);
        Assert.False(source.Options.IncludeGroups);
        Assert.True(source.Options.IncludeInstance);
        Assert.True(source.Options.IncludeMaskedValues);
        Assert.True(source.Options.AllowUnencryptedHttp);
        Assert.Equal("review/x", source.TraceOptions.Environment);
        Assert.False(source.TraceOptions.MatchByName);
        Assert.Equal([".gitlab-ci.yml", "ci/deploy.yml"], source.TraceOptions.CiFiles);

        var defaults = (GitLabSource)new GitLabSourceProvider().Create(new Dictionary<string, string?> { ["Project"] = "a/b", ["Token"] = Token });
        Assert.Equal(new Uri("https://gitlab.com/"), defaults.Options.BaseAddress);
        Assert.True(defaults.Options.IncludeGroups);
        Assert.False(defaults.Options.IncludeInstance);
        Assert.False(defaults.Options.IncludeMaskedValues);
        Assert.False(defaults.Options.AllowUnencryptedHttp);
        Assert.Null(defaults.TraceOptions.Environment);
        Assert.True(defaults.TraceOptions.MatchByName);
        Assert.Equal("GitLab a/b", defaults.DisplayName);
    }

    [Theory]
    [InlineData("https://gitlab.com/", null, Token, "Project is required.")]
    [InlineData("https://gitlab.com/", "a/b", " ", "Token is required.")]
    [InlineData("gitlab.com", "a/b", Token, "BaseAddress must be an absolute URL.")]
    [InlineData("http://gitlab.example.com/", "a/b", Token, "BaseAddress must use https")]
    public void Refuses_missing_or_invalid_settings(string? address, string? project, string? token, string message)
    {
        var ex = Assert.Throws<ArgumentException>(() => new GitLabSourceProvider().Create(new Dictionary<string, string?>
        {
            ["BaseAddress"] = address,
            ["Project"] = project,
            ["Token"] = token,
        }));

        Assert.StartsWith(message, ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => new GitLabSourceProvider().Create(null!));
    }

    [Fact]
    public async Task Loads_as_a_config_source_and_lists_unused_entries()
    {
        const string prefix = "CFGLSRC_";
        var source = GitLabSource.Create(GitLabVariablesTests.Options(), new GitLabTraceOptions { Environment = "production" }.Map(prefix + "DB_TIMEOUT", "DB_TIMEOUT"));
        source.HttpClient = _client;
        Environment.SetEnvironmentVariable(prefix + "DB_TIMEOUT", "32");
        try
        {
            var tracer = ConfigTracer.For(new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build())
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "DB_TIMEOUT"] = "32" });
            var loaded = await tracer.WithSourceAsync(source, TestContext.Current.CancellationToken);
            var report = tracer.Trace();

            Assert.Equal("GitLab contoso/platform/api (production)", loaded.DisplayName);
            Assert.Equal(9, loaded.Entries.Count);
            var review = loaded.Entries.Single(e => e.Reference is GitLabVariableRef { EnvironmentScope: "review/*" });
            Assert.Equal("contoso/platform/api, environment review/*", review.Location);
            Assert.Equal("project variable, environment scope review/*", review.Detail);
            var password = loaded.Entries.Single(e => e.Name == "DB_PASSWORD");
            Assert.True(password.IsSensitive);
            Assert.Equal("project variable, environment scope *, masked", password.Detail);
            Assert.Equal("project variable, environment scope *, masked, hidden", loaded.Entries.Single(e => e.Name == "API_KEY").Detail);
            Assert.Equal("project variable, environment scope *, protected, file", loaded.Entries.Single(e => e.Name == "KUBECONFIG").Detail);
            Assert.Equal("contoso", loaded.Entries[0].Location);

            var unused = loaded.FindUnused(report);
            Assert.Equal(["REGION", "DB_TIMEOUT", "DB_TIMEOUT", "DB_PASSWORD", "API_KEY", "KUBECONFIG"], unused.Select(e => e.Name));
            Assert.Equal(["*", "review/*"], unused.Where(e => e.Name == "DB_TIMEOUT").Select(e => ((GitLabVariableRef)e.Reference).EnvironmentScope));
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "DB_TIMEOUT", null);
        }
    }

    [Fact]
    public async Task Loads_with_its_own_client_when_none_is_set()
    {
        var options = GitLabVariablesTests.Options();
        options.BaseAddress = new Uri("https://localhost:1/");
        IConfigSource source = GitLabSource.Create(options);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.LoadAsync(TestContext.Current.CancellationToken));
    }
}
