using ConfigFerret.Sources;
using ConfigFerret.Tests.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.HashiCorp.Tests;

public class ServiceCollectionAndProviderTests
{
    private const string Token = "hvs.bound_token_never_printed";

    private static IConfigurationRoot Configuration(Dictionary<string, string?> settings, string? token, string tokenKey, Action<IConfigurationBuilder>? more = null)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(settings);
        more?.Invoke(builder);
        if (token is not null)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?> { [tokenKey] = token });
        }

        return builder.Build();
    }

    private static readonly Dictionary<string, string?> VaultSettings = new()
    {
        ["ConfigFerret:Vault:Address"] = "https://vault.example.com:8200/",
        ["ConfigFerret:Vault:Namespace"] = "admin",
        ["ConfigFerret:Vault:Mount"] = "kv",
        ["ConfigFerret:Vault:KvVersion"] = "2",
        ["ConfigFerret:Vault:Paths:0"] = "orders/prod",
        ["ConfigFerret:Vault:Recursive"] = "false",
        ["ConfigFerret:Vault:IncludeValues"] = "true",
        ["ConfigFerret:Vault:MatchByName"] = "false",
        ["ConfigFerret:Vault:StripPrefixes:0"] = "ORDERS_",
        ["ConfigFerret:Vault:Links:Db__Password"] = "orders/prod#password",
    };

    private static ServiceProvider VaultServices(IConfigurationRoot configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddConfigFerretVault(configuration.GetSection("ConfigFerret:Vault"));
        return services.BuildServiceProvider();
    }

    private static ServiceProvider ConsulServices(IConfigurationRoot configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddConfigFerretConsul(configuration.GetSection("ConfigFerret:Consul"));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Binds_the_Vault_options_and_trace_options_from_the_section()
    {
        using var provider = VaultServices(Configuration(VaultSettings, Token, "ConfigFerret:Vault:Token"));

        var source = provider.GetRequiredService<VaultSource>();
        Assert.Equal(new Uri("https://vault.example.com:8200/"), source.Options.Address);
        Assert.Equal(Token, source.Options.Token);
        Assert.Equal("admin", source.Options.Namespace);
        Assert.Equal("kv", source.Options.Mount);
        Assert.Equal(2, source.Options.KvVersion);
        Assert.Equal(["orders/prod"], source.Options.Paths);
        Assert.False(source.Options.Recursive);
        Assert.True(source.Options.IncludeValues);
        Assert.False(source.TraceOptions.MatchByName);
        Assert.Equal(["ORDERS_"], source.TraceOptions.StripPrefixes);
        Assert.Equal(("orders/prod", "password"), source.TraceOptions.FindMapping("db__password"));
        Assert.Equal("HashiCorp Vault vault.example.com:8200 kv", source.DisplayName);
        _ = provider.GetRequiredService<IOptions<VaultOptions>>().Value;
    }

    [Fact]
    public void Fails_at_startup_without_a_Vault_token_or_address()
    {
        using var provider = VaultServices(Configuration([], null, "ConfigFerret:Vault:Token"));

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<VaultOptions>>().Value);

        Assert.Equal(2, ex.Failures.Count());
        Assert.Contains(ex.Failures, f => f.StartsWith("ConfigFerret:Vault:Address is not set", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, f => f.StartsWith("ConfigFerret:Vault:Token is not set", StringComparison.Ordinal) && f.Contains("ConfigFerret__Vault__Token", StringComparison.Ordinal));
    }

    [Fact]
    public void Fails_at_startup_for_bad_Vault_values_without_printing_the_token()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConfigFerret:Vault:Address"] = "http://vault.example.com/",
            ["ConfigFerret:Vault:Mount"] = " ",
            ["ConfigFerret:Vault:KvVersion"] = "3",
            ["ConfigFerret:Vault:MaxDepth"] = "-1",
        };
        using var provider = VaultServices(Configuration(settings, Token, "ConfigFerret:Vault:Token"));

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<VaultOptions>>().Value);

        Assert.Equal(4, ex.Failures.Count());
        Assert.Contains(ex.Failures, f => f.StartsWith("ConfigFerret:Vault:Address must use https", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, f => f.StartsWith("ConfigFerret:Vault:Mount is empty", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, f => f.StartsWith("ConfigFerret:Vault:KvVersion must be 1 or 2", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, f => f.StartsWith("ConfigFerret:Vault:MaxDepth must be at least 0", StringComparison.Ordinal));
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MaxSecrets")]
    [InlineData("MaxRequests")]
    public void Fails_at_startup_for_a_zero_Vault_cap(string cap)
    {
        var settings = new Dictionary<string, string?>(VaultSettings) { [$"ConfigFerret:Vault:{cap}"] = "0" };
        using var provider = VaultServices(Configuration(settings, Token, "ConfigFerret:Vault:Token"));

        Assert.Single(Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<VaultOptions>>().Value).Failures);
    }

    [Theory]
    [InlineData("Vault", "appsettings.json")]
    [InlineData("Consul", "appsettings.Production.json")]
    public void Fails_at_startup_when_the_token_is_in_an_appsettings_file(string source, string fileName)
    {
        using var folder = new TempSettingsFolder();
        var file = folder.Write(fileName, $$"""{ "ConfigFerret": { "{{source}}": { "Token": "{{Token}}-committed" } } }""");
        var configuration = Configuration(VaultSettings, Token, $"ConfigFerret:{source}:Token", b => b.AddJsonFile(file));
        using var provider = source == "Vault" ? VaultServices(configuration) : ConsulServices(configuration);

        var ex = Assert.Throws<OptionsValidationException>(() =>
            source == "Vault" ? (object)provider.GetRequiredService<IOptions<VaultOptions>>().Value : provider.GetRequiredService<IOptions<ConsulOptions>>().Value);

        Assert.Contains($"ConfigFerret:{source}:Token is set in {fileName}", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Traces_with_the_bound_Vault_options()
    {
        var prefix = $"CFVDI{Guid.NewGuid():N}_";
        Environment.SetEnvironmentVariable(prefix + "Db__Password", "hunter2");
        try
        {
            using var provider = VaultServices(Configuration(VaultSettings, Token, "ConfigFerret:Vault:Token"));
            var handler = new FakeHttpHandler()
                .Respond("/v1/kv/metadata/orders/prod", """{"data":{"current_version":1}}""")
                .Respond("/v1/kv/data/orders/prod", """{"data":{"data":{"password":"hunter2"}}}""");
            using var client = handler.Client();
            var source = provider.GetRequiredService<VaultSource>();
            source.HttpClient = client;
            source.TraceOptions.Links[prefix + "Db__Password"] = "orders/prod#password";

            var tracer = ConfigTracer.For(new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build())
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "Db__Password"] = "hunter2" });
            await tracer.WithVaultAsync(provider, TestContext.Current.CancellationToken);
            var report = tracer.Trace();

            Assert.Equal($"set in environment variable {prefix}Db__Password from HashiCorp Vault secret kv/orders/prod key password", report.Find("Db:Password")!.Story);
            Assert.DoesNotContain("hunter2", report.ToText(), StringComparison.Ordinal);
            Assert.All(handler.Requests, r => Assert.Equal("admin", Assert.Single(r.Headers.GetValues("X-Vault-Namespace"))));

            // The same source as an IConfigSource.
            var loaded = await ((IConfigSource)source).LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal("orders/prod#password", Assert.Single(loaded.Entries).Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Db__Password", null);
        }
    }

    [Fact]
    public async Task Says_how_to_register_when_a_source_is_missing()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());

        Assert.Contains("AddConfigFerretVault", (await Assert.ThrowsAsync<InvalidOperationException>(() => tracer.WithVaultAsync(provider, TestContext.Current.CancellationToken))).Message, StringComparison.Ordinal);
        Assert.Contains("AddConfigFerretConsul", (await Assert.ThrowsAsync<InvalidOperationException>(() => tracer.WithConsulAsync(provider, TestContext.Current.CancellationToken))).Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentNullException>(() => tracer.WithVaultAsync(null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => HashiCorpTracerExtensions.WithVaultAsync(null!, provider, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => tracer.WithConsulAsync(null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => HashiCorpTracerExtensions.WithConsulAsync(null!, provider, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Registration_and_sources_check_their_arguments()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<ArgumentNullException>(() => HashiCorpServiceCollectionExtensions.AddConfigFerretVault(null!, configuration));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddConfigFerretVault(null!));
        Assert.Throws<ArgumentNullException>(() => HashiCorpServiceCollectionExtensions.AddConfigFerretConsul(null!, configuration));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddConfigFerretConsul(null!));
        Assert.Throws<ArgumentNullException>(() => new VaultSource(null!, Options.Create(new VaultTraceOptions())));
        Assert.Throws<ArgumentNullException>(() => new VaultSource(Options.Create(new VaultOptions()), null!));
        Assert.Throws<ArgumentNullException>(() => VaultSource.Create(null!));
        Assert.Throws<ArgumentNullException>(() => new ConsulSource(null!, Options.Create(new ConsulTraceOptions())));
        Assert.Throws<ArgumentNullException>(() => new ConsulSource(Options.Create(new ConsulOptions()), null!));
        Assert.Throws<ArgumentNullException>(() => ConsulSource.Create(null!));
        Assert.Throws<ArgumentNullException>(() => new VaultOptionsValidator("", null).Validate(null, null!));
        Assert.Throws<ArgumentNullException>(() => new ConsulOptionsValidator("", null).Validate(null, null!));
        Assert.Same(ValidateOptionsResult.Success, new VaultOptionsValidator("", null).Validate(null, new VaultOptions { Address = new Uri("https://v/"), Token = "t" }));
        Assert.Equal("HashiCorp Vault (no address) secret", VaultSource.Create(new VaultOptions()).DisplayName);
        Assert.Equal("Consul KV (no address)", ConsulSource.Create(new ConsulOptions { Address = null }).DisplayName);
    }

    [Fact]
    public async Task Sources_load_through_their_own_client_which_refuses_an_unsafe_address()
    {
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentException>(() => VaultSource.Create(new VaultOptions { Address = new Uri("http://vault.example.com/"), Token = "t" }).LoadAsync(ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ConsulSource.Create(new ConsulOptions { Address = new Uri("http://consul.example.com/") }).LoadAsync(ct));
    }

    [Fact]
    public void Consul_validation_needs_a_token_unless_allowed_and_names_keys_from_the_root()
    {
        using var missing = ConsulServices(Configuration([], null, "ConfigFerret:Consul:Token"));
        var failure = Assert.Single(Assert.Throws<OptionsValidationException>(() => missing.GetRequiredService<IOptions<ConsulOptions>>().Value).Failures);
        Assert.StartsWith("ConfigFerret:Consul:Token is not set", failure, StringComparison.Ordinal);
        Assert.EndsWith("set ConfigFerret:Consul:AllowNoToken to true instead.", failure, StringComparison.Ordinal);

        var settings = new Dictionary<string, string?>
        {
            ["ConfigFerret:Consul:AllowNoToken"] = "true",
            ["ConfigFerret:Consul:Prefix"] = "config/orders/",
            ["ConfigFerret:Consul:Datacenter"] = "dc1",
            ["ConfigFerret:Consul:ExpandJson"] = "true",
            ["ConfigFerret:Consul:IsSensitive"] = "true",
            ["ConfigFerret:Consul:StripPrefixes:0"] = "APP_",
            ["ConfigFerret:Consul:Links:X"] = "Db/Timeout",
        };
        using var allowed = ConsulServices(Configuration(settings, null, "ConfigFerret:Consul:Token"));
        var source = allowed.GetRequiredService<ConsulSource>();
        Assert.Equal(new Uri("http://localhost:8500/"), source.Options.Address);
        Assert.Equal("config/orders/", source.Options.Prefix);
        Assert.Equal("dc1", source.Options.Datacenter);
        Assert.True(source.Options.ExpandJson);
        Assert.True(source.Options.IsSensitive);
        Assert.Equal(["APP_"], source.TraceOptions.StripPrefixes);
        Assert.Equal(("Db/Timeout", null), source.TraceOptions.FindMapping("x"));
        _ = allowed.GetRequiredService<IOptions<ConsulOptions>>().Value;

        var root = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Address"] = "http://consul.example.com/", ["MaxKeys"] = "0" }).Build();
        var services = new ServiceCollection();
        services.AddConfigFerretConsul(root);
        using var fromRoot = services.BuildServiceProvider();
        var failures = Assert.Throws<OptionsValidationException>(() => fromRoot.GetRequiredService<IOptions<ConsulOptions>>().Value).Failures.ToList();
        Assert.Equal(3, failures.Count);
        Assert.StartsWith("Token is not set.", failures[0], StringComparison.Ordinal);
        Assert.StartsWith("Address must use https", failures[1], StringComparison.Ordinal);
        Assert.StartsWith("MaxKeys must be at least 1", failures[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Traces_with_the_bound_Consul_options()
    {
        var prefix = $"CFCDI{Guid.NewGuid():N}_";
        Environment.SetEnvironmentVariable(prefix + "Db__Timeout", "30");
        try
        {
            var settings = new Dictionary<string, string?> { ["ConfigFerret:Consul:Address"] = "https://consul.example.com/", ["ConfigFerret:Consul:Prefix"] = "config/" };
            using var provider = ConsulServices(Configuration(settings, Token, "ConfigFerret:Consul:Token"));
            var handler = new FakeHttpHandler().Respond("/v1/kv/config/?recurse=true", """[{"Key":"config/Db/Timeout","Value":"MzA="}]""");
            using var client = handler.Client();
            var source = provider.GetRequiredService<ConsulSource>();
            source.HttpClient = client;
            source.TraceOptions.StripPrefixes.Add(prefix);

            var tracer = ConfigTracer.For(new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build())
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "Db__Timeout"] = "30" });
            await tracer.WithConsulAsync(provider, TestContext.Current.CancellationToken);

            Assert.Equal($"set in environment variable {prefix}Db__Timeout from Consul key config/Db/Timeout", tracer.Trace().Find("Db:Timeout")!.Story);
            Assert.Equal(Token, Assert.Single(Assert.Single(handler.Requests).Headers.GetValues("X-Consul-Token")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Db__Timeout", null);
        }
    }

    [Fact]
    public void Vault_provider_describes_its_settings_and_creates_a_source()
    {
        var provider = new VaultSourceProvider();
        Assert.Equal("hashicorp-vault", provider.Id);
        Assert.Equal("HashiCorp Vault", provider.DisplayName);
        Assert.Equal(SourceCategory.SecretStore, provider.Category);
        var token = Assert.Single(provider.Settings, s => s.Kind == SourceSettingKind.Secret);
        Assert.Equal("Token", token.Name);
        Assert.True(token.Required);
        Assert.Equal(["VAULT_TOKEN"], token.EnvironmentVariables);
        Assert.Equal(provider.Settings.Count, provider.Settings.Select(s => s.Name).Distinct().Count());

        var source = (VaultSource)provider.Create(new Dictionary<string, string?>
        {
            ["Address"] = " https://vault.example.com/ ",
            ["Token"] = "t",
            ["Namespace"] = "admin",
            ["Mount"] = "kv",
            ["KvVersion"] = "1",
            ["Paths"] = "orders/prod\r\n\r\n billing \n",
            ["Recursive"] = "False",
            ["IncludeValues"] = "true",
            ["MatchByName"] = "false",
            ["StripPrefixes"] = "A_\nB_",
            ["AllowUnencryptedHttp"] = "true",
        });
        Assert.Equal(new Uri("https://vault.example.com/"), source.Options.Address);
        Assert.Equal("admin", source.Options.Namespace);
        Assert.Equal("kv", source.Options.Mount);
        Assert.Equal(1, source.Options.KvVersion);
        Assert.Equal(["orders/prod", "billing"], source.Options.Paths);
        Assert.False(source.Options.Recursive);
        Assert.True(source.Options.IncludeValues);
        Assert.True(source.Options.AllowUnencryptedHttp);
        Assert.False(source.TraceOptions.MatchByName);
        Assert.Equal(["A_", "B_"], source.TraceOptions.StripPrefixes);

        var defaults = (VaultSource)provider.Create(new Dictionary<string, string?> { ["Address"] = "https://v/", ["Token"] = "t", ["KvVersion"] = " " });
        Assert.Equal("secret", defaults.Options.Mount);
        Assert.Null(defaults.Options.KvVersion);
        Assert.True(defaults.Options.Recursive);
        Assert.False(defaults.Options.IncludeValues);
        Assert.True(defaults.TraceOptions.MatchByName);
        Assert.Empty(defaults.Options.Paths);
    }

    [Theory]
    [InlineData(null, "t", null, null)]
    [InlineData("https://v/", null, null, null)]
    [InlineData("not a url", "t", null, null)]
    [InlineData("http://vault.example.com/", "t", null, null)]
    [InlineData("https://v/", "t", "KvVersion", "3")]
    [InlineData("https://v/", "t", "KvVersion", "two")]
    [InlineData("https://v/", "t", "Recursive", "yes")]
    public void Vault_provider_refuses_missing_or_invalid_settings(string? address, string? token, string? extra, string? value)
    {
        var values = new Dictionary<string, string?> { ["Address"] = address, ["Token"] = token };
        if (extra is not null)
        {
            values[extra] = value;
        }

        Assert.Throws<ArgumentException>(() => new VaultSourceProvider().Create(values));
        Assert.Throws<ArgumentNullException>(() => new VaultSourceProvider().Create(null!));
    }

    [Fact]
    public void Consul_provider_describes_its_settings_and_creates_a_source()
    {
        var provider = new ConsulSourceProvider();
        Assert.Equal("consul-kv", provider.Id);
        Assert.Equal("Consul KV", provider.DisplayName);
        Assert.Equal(SourceCategory.ConfigurationService, provider.Category);
        var token = Assert.Single(provider.Settings, s => s.Kind == SourceSettingKind.Secret);
        Assert.False(token.Required);
        Assert.Equal(["CONSUL_HTTP_TOKEN"], token.EnvironmentVariables);

        var source = (ConsulSource)provider.Create(new Dictionary<string, string?>
        {
            ["Address"] = "https://consul.example.com/",
            ["Token"] = "t",
            ["Datacenter"] = "dc1",
            ["Prefix"] = "config/orders/",
            ["ExpandJson"] = "true",
            ["IsSensitive"] = "true",
            ["MatchByName"] = "false",
            ["StripPrefixes"] = "APP_",
            ["AllowUnencryptedHttp"] = "true",
        });
        Assert.Equal("t", source.Options.Token);
        Assert.False(source.Options.AllowNoToken);
        Assert.Equal("dc1", source.Options.Datacenter);
        Assert.Equal("config/orders/", source.Options.Prefix);
        Assert.True(source.Options.ExpandJson);
        Assert.True(source.Options.IsSensitive);
        Assert.True(source.Options.AllowUnencryptedHttp);
        Assert.False(source.TraceOptions.MatchByName);
        Assert.Equal(["APP_"], source.TraceOptions.StripPrefixes);

        var defaults = (ConsulSource)provider.Create(new Dictionary<string, string?>());
        Assert.Equal(new Uri("http://localhost:8500/"), defaults.Options.Address);
        Assert.Null(defaults.Options.Token);
        Assert.True(defaults.Options.AllowNoToken);
        Assert.Equal(string.Empty, defaults.Options.Prefix);
        Assert.False(defaults.Options.ExpandJson);
        Assert.False(defaults.Options.IsSensitive);
        Assert.True(defaults.TraceOptions.MatchByName);

        Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?> { ["Address"] = "http://consul.example.com/" }));
        Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?> { ["ExpandJson"] = "1" }));
        Assert.Throws<ArgumentNullException>(() => provider.Create(null!));
    }
}
