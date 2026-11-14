using ConfigFerret.Tests.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Aws.Tests;

public class AwsServiceCollectionTests
{
    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["ConfigFerret:AwsSecretsManager:Region"] = "eu-west-1",
        ["ConfigFerret:AwsSecretsManager:NamePrefix"] = "prod/orders/",
        ["ConfigFerret:AwsSecretsManager:IncludeValues"] = "true",
        ["ConfigFerret:AwsSecretsManager:KeyPrefix"] = "prod/",
        ["ConfigFerret:AwsSecretsManager:MatchByName"] = "false",
        ["ConfigFerret:AwsSecretsManager:MatchConfigurationKeys"] = "false",
        ["ConfigFerret:AwsSecretsManager:ServiceUrl"] = "https://vpce.example.com/",
        ["ConfigFerret:AwsSecretsManager:Links:PG_PASS"] = "prod/orders/db#password",
        ["ConfigFerret:AwsParameterStore:Region"] = "eu-west-1",
        ["ConfigFerret:AwsParameterStore:Path"] = "/orders/prod/",
        ["ConfigFerret:AwsParameterStore:Recursive"] = "false",
        ["ConfigFerret:AwsParameterStore:IncludeValues"] = "true",
        ["ConfigFerret:AwsParameterStore:MatchByName"] = "false",
        ["ConfigFerret:AwsParameterStore:Links:TIMEOUT"] = "/orders/prod/Db/Timeout",
    };

    // The keys come from a source that stands in for user secrets or an environment variable.
    private static IConfigurationRoot Configuration(bool keys = true, Action<IConfigurationBuilder>? more = null, Dictionary<string, string?>? settings = null)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(settings ?? Settings);
        more?.Invoke(builder);
        if (keys)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConfigFerret:AwsSecretsManager:AccessKeyId"] = "AKIDEXAMPLE",
                ["ConfigFerret:AwsSecretsManager:SecretAccessKey"] = FakeAws.SecretKey,
                ["ConfigFerret:AwsParameterStore:AccessKeyId"] = "AKIDEXAMPLE",
                ["ConfigFerret:AwsParameterStore:SecretAccessKey"] = FakeAws.SecretKey,
                ["ConfigFerret:AwsParameterStore:SessionToken"] = "session",
            });
        }

        return builder.Build();
    }

    private static ServiceProvider Services(IConfigurationRoot configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddConfigFerretAwsSecretsManager(configuration.GetSection("ConfigFerret:AwsSecretsManager"));
        services.AddConfigFerretAwsParameterStore(configuration.GetSection("ConfigFerret:AwsParameterStore"));
        return services.BuildServiceProvider();
    }

    // ValidateOnStart runs these same validators when the host starts. Reading the options runs them directly.
    private static string Failure<T>(ServiceProvider provider)
        where T : class =>
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<T>>().Value).Message;

    [Fact]
    public void Binds_the_connection_and_trace_options_from_the_sections()
    {
        using var provider = Services(Configuration());

        var secrets = provider.GetRequiredService<AwsSecretsManagerSource>();
        Assert.Equal("eu-west-1", secrets.Options.Region);
        Assert.Equal("prod/orders/", secrets.Options.NamePrefix);
        Assert.True(secrets.Options.IncludeValues);
        Assert.Equal(FakeAws.SecretKey, secrets.Options.SecretAccessKey);
        Assert.Equal(new Uri("https://vpce.example.com/"), secrets.Options.ServiceUrl);
        Assert.Equal("prod/", secrets.TraceOptions.KeyPrefix);
        Assert.False(secrets.TraceOptions.MatchByName);
        Assert.False(secrets.TraceOptions.MatchConfigurationKeys);
        Assert.Equal("prod/orders/db#password", secrets.TraceOptions.Links["pg_pass"]);

        var parameters = provider.GetRequiredService<AwsParameterStoreSource>();
        Assert.Equal("/orders/prod/", parameters.Options.Path);
        Assert.False(parameters.Options.Recursive);
        Assert.True(parameters.Options.IncludeValues);
        Assert.Equal("session", parameters.Options.SessionToken);
        Assert.False(parameters.TraceOptions.MatchByName);
        Assert.Equal("/orders/prod/Db/Timeout", parameters.TraceOptions.Links["TIMEOUT"]);
        Assert.Same(secrets, provider.GetRequiredService<AwsSecretsManagerSource>());
    }

    [Fact]
    public void Starts_without_keys_to_use_the_default_credential_chain()
    {
        using var provider = Services(Configuration(keys: false));

        Assert.Null(provider.GetRequiredService<IOptions<AwsSecretsManagerOptions>>().Value.AccessKeyId);
        Assert.Equal("/orders/prod/", provider.GetRequiredService<IOptions<AwsParameterStoreOptions>>().Value.Path);
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Production.json")]
    public void Fails_at_startup_when_a_key_is_in_an_appsettings_file_even_if_overridden(string fileName)
    {
        using var folder = new TempSettingsFolder();
        var file = folder.Write(fileName, $$"""
            { "ConfigFerret": {
                "AwsSecretsManager": { "SecretAccessKey": "{{FakeAws.SecretKey}}-committed" },
                "AwsParameterStore": { "AccessKeyId": "AKIDCOMMITTED", "SessionToken": "committed-token" } } }
            """);
        using var provider = Services(Configuration(more: b => b.AddJsonFile(file)));

        var secrets = Failure<AwsSecretsManagerOptions>(provider);
        var parameters = Failure<AwsParameterStoreOptions>(provider);

        Assert.Contains($"ConfigFerret:AwsSecretsManager:SecretAccessKey is set in {fileName}", secrets, StringComparison.Ordinal);
        Assert.Contains("ConfigFerret__AwsSecretsManager__SecretAccessKey", secrets, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessKeyId is set", secrets, StringComparison.Ordinal);
        Assert.Contains($"ConfigFerret:AwsParameterStore:AccessKeyId is set in {fileName}", parameters, StringComparison.Ordinal);
        Assert.Contains($"ConfigFerret:AwsParameterStore:SessionToken is set in {fileName}", parameters, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeAws.SecretKey, secrets, StringComparison.Ordinal);
        Assert.DoesNotContain("committed-token", parameters, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_keys_from_a_user_secrets_file()
    {
        using var folder = new TempSettingsFolder();
        var file = folder.Write("secrets.json", $$"""{ "ConfigFerret": { "AwsSecretsManager": { "AccessKeyId": "A", "SecretAccessKey": "{{FakeAws.SecretKey}}" } } }""");
        using var provider = Services(Configuration(keys: false, more: b => b.AddJsonFile(file)));

        Assert.Equal("A", provider.GetRequiredService<IOptions<AwsSecretsManagerOptions>>().Value.AccessKeyId);
    }

    [Fact]
    public void Fails_at_startup_for_a_missing_path_half_a_key_pair_and_an_unsafe_URL()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConfigFerret:AwsSecretsManager:AccessKeyId"] = "AKIDEXAMPLE",
            ["ConfigFerret:AwsSecretsManager:ServiceUrl"] = "http://secrets.example.com/",
            ["ConfigFerret:AwsParameterStore:Path"] = "orders/prod",
            ["ConfigFerret:AwsParameterStore:ServiceUrl"] = "http://localhost:4566/",
        }).Build();
        using var provider = Services(configuration);

        var secrets = Failure<AwsSecretsManagerOptions>(provider);
        Assert.Contains("ConfigFerret:AwsSecretsManager:AccessKeyId and ConfigFerret:AwsSecretsManager:SecretAccessKey must be set together", secrets, StringComparison.Ordinal);
        Assert.Contains("ConfigFerret:AwsSecretsManager:ServiceUrl must use https", secrets, StringComparison.Ordinal);
        Assert.DoesNotContain("Path", secrets, StringComparison.Ordinal);

        var parameters = Failure<AwsParameterStoreOptions>(provider);
        Assert.Contains("ConfigFerret:AwsParameterStore:Path must be set to a path that starts with /", parameters, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceUrl", parameters, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessKeyId", parameters, StringComparison.Ordinal);

        using var empty = Services(new ConfigurationBuilder().Build());
        Assert.Contains("ConfigFerret:AwsParameterStore:Path must be set", Failure<AwsParameterStoreOptions>(empty), StringComparison.Ordinal);
    }

    [Fact]
    public void Names_keys_without_a_section_path()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ServiceUrl"] = "http://x.example/" }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddConfigFerretAwsParameterStore(configuration);
        using var provider = services.BuildServiceProvider();

        var message = Failure<AwsParameterStoreOptions>(provider);

        Assert.Contains("Path must be set", message, StringComparison.Ordinal);
        Assert.Contains("ServiceUrl must use https", message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => new AwsOptionsValidator<AwsParameterStoreOptions>(string.Empty, null).Validate(null, null!));
    }

    [Fact]
    public async Task Loads_and_links_through_the_registered_sources()
    {
        using var provider = Services(Configuration());
        var aws = new FakeAws()
            .On("ListSecrets", """{"SecretList":[{"Name":"prod/orders/db"}]}""")
            .On("BatchGetSecretValue", """{"SecretValues":[{"Name":"prod/orders/db","SecretString":"{\"password\":\"p\"}"}]}""")
            .On("GetParametersByPath", """{"Parameters":[{"Name":"/orders/prod/Db/Timeout","Type":"String","Value":"30"}]}""");
        provider.GetRequiredService<AwsSecretsManagerSource>().ConfigureClient = aws.Configure;
        provider.GetRequiredService<AwsParameterStoreSource>().ConfigureClient = aws.Configure;
        const string prefix = "CFAWSDI_";

        // Layers are named by the whole variable name, prefix included.
        provider.GetRequiredService<AwsSecretsManagerSource>().TraceOptions.Map(prefix + "PG_PASS", "prod/orders/db", "password");
        provider.GetRequiredService<AwsParameterStoreSource>().TraceOptions.Map(prefix + "TIMEOUT", "/orders/prod/Db/Timeout");
        Environment.SetEnvironmentVariable(prefix + "PG_PASS", "p");
        Environment.SetEnvironmentVariable(prefix + "TIMEOUT", "30");
        try
        {
            var tracer = ConfigTracer.For(new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build())
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "PG_PASS"] = "p", [prefix + "TIMEOUT"] = "30" });
            await tracer.WithAwsSecretsManagerAsync(provider, TestContext.Current.CancellationToken);
            await tracer.WithAwsParameterStoreAsync(provider, TestContext.Current.CancellationToken);
            var report = tracer.Trace();

            Assert.Equal("AWS Secrets Manager secret prod/orders/db, property password", Assert.Single(report.Find("PG_PASS")!.EffectiveStep!.Origins).Source);
            Assert.Equal("AWS Parameter Store parameter /orders/prod/Db/Timeout", Assert.Single(report.Find("TIMEOUT")!.EffectiveStep!.Origins).Source);
            Assert.Equal("https://vpce.example.com/", aws.Requests[0].Uri.AbsoluteUri);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "PG_PASS", null);
            Environment.SetEnvironmentVariable(prefix + "TIMEOUT", null);
        }
    }

    [Fact]
    public async Task Says_how_to_register_the_sources_and_checks_arguments()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());
        var ct = TestContext.Current.CancellationToken;

        var secrets = await Assert.ThrowsAsync<InvalidOperationException>(() => tracer.WithAwsSecretsManagerAsync(provider, ct));
        var parameters = await Assert.ThrowsAsync<InvalidOperationException>(() => tracer.WithAwsParameterStoreAsync(provider, ct));

        Assert.Contains("AddConfigFerretAwsSecretsManager", secrets.Message, StringComparison.Ordinal);
        Assert.Contains("AddConfigFerretAwsParameterStore", parameters.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentNullException>(() => AwsTracerExtensions.WithAwsSecretsManagerAsync(null!, provider, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => tracer.WithAwsSecretsManagerAsync(null!, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AwsTracerExtensions.WithAwsParameterStoreAsync(null!, provider, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => tracer.WithAwsParameterStoreAsync(null!, ct));
        var configuration = new ConfigurationBuilder().Build();
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddConfigFerretAwsSecretsManager(configuration));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddConfigFerretAwsSecretsManager(null!));
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddConfigFerretAwsParameterStore(configuration));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddConfigFerretAwsParameterStore(null!));
    }
}
