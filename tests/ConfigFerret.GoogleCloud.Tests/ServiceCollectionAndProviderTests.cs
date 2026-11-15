using ConfigFerret.Sources;
using ConfigFerret.Tests.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.GoogleCloud.Tests;

public class ServiceCollectionAndProviderTests
{
    private const string Section = "ConfigFerret:GcpSecretManager";

    private static readonly Dictionary<string, string?> Settings = new()
    {
        [$"{Section}:ProjectId"] = "contoso-prod",
        [$"{Section}:Prefix"] = "orders-",
        [$"{Section}:Filter"] = "labels.app=orders",
        [$"{Section}:IncludeValues"] = "true",
        [$"{Section}:IncludeDisabled"] = "true",
        [$"{Section}:MaxSecrets"] = "10",
        [$"{Section}:MatchByName"] = "false",
        [$"{Section}:Separator"] = "--",
        [$"{Section}:StripSecretPrefix"] = "false",
        [$"{Section}:StripPrefixes:0"] = "ORDERS_",
        [$"{Section}:Links:Db__Password"] = "orders-db#password",
    };

    private static IConfigurationRoot Configuration(Dictionary<string, string?> settings, Dictionary<string, string?>? secrets = null)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(settings);
        if (secrets is not null)
        {
            builder.AddInMemoryCollection(secrets);
        }

        return builder.Build();
    }

    private static ServiceProvider Services(IConfigurationRoot configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddConfigFerretGcpSecretManager(configuration.GetSection(Section));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Binds_the_options_and_trace_options_from_the_section()
    {
        using var provider = Services(Configuration(Settings, new() { [$"{Section}:AccessToken"] = GcpSecretsTests.Token }));

        var source = provider.GetRequiredService<GcpSecretManagerSource>();

        Assert.Equal("contoso-prod", source.Options.ProjectId);
        Assert.Equal("orders-", source.Options.Prefix);
        Assert.Equal("labels.app=orders", source.Options.Filter);
        Assert.True(source.Options.IncludeValues);
        Assert.True(source.Options.IncludeDisabled);
        Assert.Equal(10, source.Options.MaxSecrets);
        Assert.Equal(GcpSecretsTests.Token, source.Options.AccessToken);
        Assert.False(source.TraceOptions.MatchByName);
        Assert.False(source.TraceOptions.StripSecretPrefix);
        Assert.Equal("--", source.TraceOptions.Separator);
        Assert.Equal(["ORDERS_"], source.TraceOptions.StripPrefixes);
        Assert.Equal("orders-db#password", source.TraceOptions.Links["Db__Password"]);
        Assert.Equal("Google Cloud Secret Manager contoso-prod", source.DisplayName);
        Assert.Same(source, provider.GetRequiredService<GcpSecretManagerSource>());
    }

    [Fact]
    public void Accepts_no_credential_because_Application_Default_Credentials_are_used()
    {
        using var provider = Services(Configuration(Settings));

        Assert.Null(provider.GetRequiredService<IOptions<GcpSecretManagerOptions>>().Value.AccessToken);
    }

    [Theory]
    [InlineData("AccessToken")]
    [InlineData("CredentialsJson")]
    public void Fails_at_startup_when_a_credential_is_in_an_appsettings_file(string name)
    {
        using var folder = new TempSettingsFolder();
        var path = folder.Write("appsettings.Production.json", "{\"ConfigFerret\":{\"GcpSecretManager\":{\"ProjectId\":\"p\",\"" + name + "\":\"leaked-credential\"}}}");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path)
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"{Section}:{name}"] = "override" })
            .Build();
        using var provider = Services(configuration);

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GcpSecretManagerOptions>>().Value);

        Assert.Contains($"{Section}:{name} is set in appsettings.Production.json.", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"ConfigFerret__GcpSecretManager__{name}", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("leaked-credential", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("override", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_every_problem_at_startup()
    {
        using var provider = Services(Configuration(new()
        {
            [$"{Section}:ProjectId"] = " ",
            [$"{Section}:Location"] = "evil.example.com/",
            [$"{Section}:CredentialsFile"] = "/no/such/key.json",
            [$"{Section}:MaxSecrets"] = "0",
        }));

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GcpSecretManagerOptions>>().Value);

        Assert.Equal(
            [
                $"{Section}:ProjectId is not set. Set it to the Google Cloud project id, for example in appsettings.json.",
                $"{Section}:Location must hold only lower-case letters, digits and dashes, for example europe-west1.",
                $"{Section}:CredentialsFile names a file that does not exist: /no/such/key.json.",
                $"{Section}:MaxSecrets must be at least 1.",
            ],
            ex.Failures);
    }

    [Fact]
    public void Refuses_an_address_that_is_not_https()
    {
        var result = new GcpSecretManagerOptionsValidator("", null).Validate(null, new GcpSecretManagerOptions { ProjectId = "p", ApiBaseAddress = new Uri("http://sm.example.com/v1/") });

        Assert.Equal("ApiBaseAddress must use https so the credential is not sent in clear text. Turn on AllowUnencryptedHttp to use http anyway.", Assert.Single(result.Failures!));
        Assert.Same(ValidateOptionsResult.Success, new GcpSecretManagerOptionsValidator("", null).Validate(null, new GcpSecretManagerOptions { ProjectId = "p", MaxSecrets = 1, Location = "europe-west1" }));
        Assert.Throws<ArgumentNullException>(() => new GcpSecretManagerOptionsValidator("", null).Validate(null, null!));
    }

    [Fact]
    public void Accepts_an_existing_key_file()
    {
        using var folder = new TempSettingsFolder();
        var path = folder.Write("key.json", "{}");

        Assert.Same(ValidateOptionsResult.Success, new GcpSecretManagerOptionsValidator("", null).Validate(null, new GcpSecretManagerOptions { ProjectId = "p", CredentialsFile = $" {path} " }));
    }

    [Fact]
    public async Task Loads_through_dependency_injection_and_as_an_IConfigSource()
    {
        var prefix = $"CFGCPDI{Guid.NewGuid():N}_";
        Environment.SetEnvironmentVariable(prefix + "Db__Password", "hunter2");
        try
        {
            using var provider = Services(Configuration(Settings, new() { [$"{Section}:AccessToken"] = GcpSecretsTests.Token }));
            var handler = new FakeHttpHandler()
                .Respond("/v1/projects/contoso-prod/secrets?pageSize=250&filter=labels.app%3Dorders", $$"""{"secrets":[{{GcpSecretsTests.Secret("orders-db")}},{{GcpSecretsTests.Secret("orders-unused")}}]}""")
                .Respond("/v1/projects/123/secrets/orders-db/versions?pageSize=25", GcpSecretsTests.Versions((1, "ENABLED")))
                .Respond("/v1/projects/123/secrets/orders-db/versions/1:access", GcpSecretsTests.Access("""{"password":"hunter2"}"""))
                .Respond("/v1/projects/123/secrets/orders-unused/versions?pageSize=25", GcpSecretsTests.Versions((1, "DISABLED")));
            using var client = handler.Client();
            var source = provider.GetRequiredService<GcpSecretManagerSource>();
            source.HttpClient = client;
            source.TraceOptions.Links[prefix + "Db__Password"] = "orders-db#password";

            var tracer = ConfigTracer.For(new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build())
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "Db__Password"] = "hunter2" });
            await tracer.WithGcpSecretManagerAsync(provider, TestContext.Current.CancellationToken);
            var report = tracer.Trace();

            Assert.Equal($"set in environment variable {prefix}Db__Password from Google Cloud Secret Manager secret orders-db property password (contoso-prod)", report.Find("Db:Password")!.Story);
            Assert.DoesNotContain("hunter2", report.ToText(), StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", report.ToJson(), StringComparison.Ordinal);

            var loaded = await ((IConfigSource)source).LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal("Google Cloud Secret Manager contoso-prod", loaded.DisplayName);
            Assert.Equal(["orders-db", "orders-unused"], loaded.Entries.Select(e => e.Name));
            Assert.Empty(loaded.Warnings);
            var report2 = ConfigTracer.For(new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build())
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "Db__Password"] = "hunter2" })
                .WithSource(loaded)
                .Trace();
            Assert.Equal(["orders-unused"], loaded.FindUnused(report2).Select(e => e.Name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Db__Password", null);
        }
    }

    [Fact]
    public async Task Says_how_to_register_when_the_source_is_missing()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tracer.WithGcpSecretManagerAsync(provider, TestContext.Current.CancellationToken));

        Assert.Contains("AddConfigFerretGcpSecretManager", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentNullException>(() => GoogleCloudTracerExtensions.WithGcpSecretManagerAsync(null!, provider, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => tracer.WithGcpSecretManagerAsync(null!, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddConfigFerretGcpSecretManager(null!));
        Assert.Throws<ArgumentNullException>(() => GoogleCloudServiceCollectionExtensions.AddConfigFerretGcpSecretManager(null!, new ConfigurationBuilder().Build()));
        Assert.Throws<ArgumentNullException>(() => new GcpSecretManagerSource(null!, Options.Create(new GcpSecretManagerTraceOptions())));
        Assert.Throws<ArgumentNullException>(() => new GcpSecretManagerSource(Options.Create(new GcpSecretManagerOptions()), null!));
        Assert.Throws<ArgumentNullException>(() => GcpSecretManagerSource.Create(null!));
    }

    [Fact]
    public void Names_the_source_after_the_project_and_location()
    {
        Assert.Equal("Google Cloud Secret Manager (no project)", GcpSecretManagerSource.Create(new GcpSecretManagerOptions()).DisplayName);
        Assert.Equal("Google Cloud Secret Manager p europe-west1", GcpSecretManagerSource.Create(new GcpSecretManagerOptions { ProjectId = " p ", Location = " europe-west1 " }).DisplayName);
    }

    [Fact]
    public void Describes_the_provider()
    {
        var provider = new GcpSecretManagerSourceProvider();

        Assert.Equal("gcp-secret-manager", provider.Id);
        Assert.Equal("Google Cloud Secret Manager", provider.DisplayName);
        Assert.Equal(SourceCategory.SecretStore, provider.Category);
        Assert.Equal(
            ["ProjectId|Text|True|", "Location|Text|False|", "Prefix|Text|False|", "Filter|Text|False|", "CredentialsFile|Path|False|", "CredentialsJson|Secret|False|", "AccessToken|Secret|False|", "IncludeVersions|Boolean|False|false", "IncludeValues|Boolean|False|false", "IncludeDisabled|Boolean|False|false", "MatchByName|Boolean|False|true", "StripPrefixes|Lines|False|", "Separator|Text|False|", "ApiBaseAddress|Url|False|"],
            provider.Settings.Select(s => $"{s.Name}|{s.Kind}|{s.Required}|{s.DefaultValue}"));
        Assert.All(provider.Settings, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Label));
            Assert.EndsWith(".", s.Help, StringComparison.Ordinal);
        });
        Assert.Equal(["GOOGLE_APPLICATION_CREDENTIALS"], provider.Settings.Single(s => s.Name == "CredentialsFile").EnvironmentVariables);
        Assert.Equal(["GOOGLE_OAUTH_ACCESS_TOKEN"], provider.Settings.Single(s => s.Name == "AccessToken").EnvironmentVariables);
        Assert.Null(provider.Settings.Single(s => s.Name == "CredentialsJson").EnvironmentVariables);
    }

    [Fact]
    public void Creates_a_source_from_plain_settings()
    {
        var source = (GcpSecretManagerSource)new GcpSecretManagerSourceProvider().Create(new Dictionary<string, string?>
        {
            ["ProjectId"] = " contoso-prod ",
            ["Location"] = "europe-west1",
            ["Prefix"] = "orders-",
            ["Filter"] = "labels.app=orders",
            ["CredentialsFile"] = "/keys/key.json",
            ["CredentialsJson"] = "{}",
            ["AccessToken"] = "ya29.x",
            ["IncludeVersions"] = "true",
            ["IncludeValues"] = "True",
            ["IncludeDisabled"] = "true",
            ["MatchByName"] = "false",
            ["StripPrefixes"] = "A_\n\n B_ ",
            ["Separator"] = "--",
            ["ApiBaseAddress"] = "https://private.example.com/v1/",
        });

        Assert.Equal("contoso-prod", source.Options.ProjectId);
        Assert.Equal("europe-west1", source.Options.Location);
        Assert.Equal("orders-", source.Options.Prefix);
        Assert.Equal("labels.app=orders", source.Options.Filter);
        Assert.Equal("/keys/key.json", source.Options.CredentialsFile);
        Assert.Equal("{}", source.Options.CredentialsJson);
        Assert.Equal("ya29.x", source.Options.AccessToken);
        Assert.True(source.Options.IncludeVersions);
        Assert.True(source.Options.IncludeValues);
        Assert.True(source.Options.IncludeDisabled);
        Assert.Equal(new Uri("https://private.example.com/v1/"), source.Options.ApiBaseAddress);
        Assert.False(source.TraceOptions.MatchByName);
        Assert.Equal(["A_", "B_"], source.TraceOptions.StripPrefixes);
        Assert.Equal("--", source.TraceOptions.Separator);
    }

    [Fact]
    public void Creates_a_source_with_defaults()
    {
        var source = (GcpSecretManagerSource)new GcpSecretManagerSourceProvider().Create(new Dictionary<string, string?> { ["ProjectId"] = "p", ["Location"] = " " });

        Assert.Null(source.Options.Location);
        Assert.Null(source.Options.AccessToken);
        Assert.False(source.Options.IncludeVersions);
        Assert.False(source.Options.IncludeValues);
        Assert.False(source.Options.IncludeDisabled);
        Assert.Null(source.Options.ApiBaseAddress);
        Assert.True(source.TraceOptions.MatchByName);
        Assert.Empty(source.TraceOptions.StripPrefixes);
        Assert.Null(source.TraceOptions.Separator);
    }

    [Theory]
    [InlineData("ProjectId", "", "ProjectId is required.")]
    [InlineData("Location", "Europe/West", "Location must hold only lower-case letters, digits and dashes, for example europe-west1.")]
    [InlineData("IncludeValues", "yes", "IncludeValues must be true or false.")]
    [InlineData("ApiBaseAddress", "not a url", "ApiBaseAddress must be an absolute URL.")]
    [InlineData("ApiBaseAddress", "http://example.com/v1/", "ApiBaseAddress must use https so the credential is not sent in clear text. Turn on AllowUnencryptedHttp to use http anyway.")]
    public void Refuses_missing_or_invalid_settings(string name, string value, string message)
    {
        var values = new Dictionary<string, string?> { ["ProjectId"] = "p", [name] = value };

        var ex = Assert.Throws<ArgumentException>(() => new GcpSecretManagerSourceProvider().Create(values));

        Assert.StartsWith(message, ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => new GcpSecretManagerSourceProvider().Create(null!));
    }
}
