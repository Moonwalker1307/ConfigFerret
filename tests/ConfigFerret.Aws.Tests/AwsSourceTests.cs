using Amazon.Runtime;
using ConfigFerret.Sources;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Aws.Tests;

// Looking up an unknown profile reads the profile files, and AwsProfileTests points that lookup at its own folder while it
// runs. Running in its collection keeps the two from reading and deleting the same files at the same time.
[Collection(nameof(AwsProfileTests))]
public class AwsSourceTests
{
    private static readonly Dictionary<string, string?> Keys = new()
    {
        ["Region"] = "eu-west-1",
        ["AccessKeyId"] = "AKIDEXAMPLE",
        ["SecretAccessKey"] = FakeAws.SecretKey,
    };

    private static Dictionary<string, string?> With(params (string Key, string? Value)[] more)
    {
        var values = new Dictionary<string, string?>(Keys);
        foreach (var (key, value) in more)
        {
            values[key] = value;
        }

        return values;
    }

    private static readonly IConfigSourceProvider[] Providers = [new AwsSecretsManagerProvider(), new AwsParameterStoreProvider()];

    [Fact]
    public void Describes_both_providers()
    {
        var secrets = Providers[0];
        var parameters = Providers[1];

        Assert.Equal("aws-secrets-manager", secrets.Id);
        Assert.Equal("AWS Secrets Manager", secrets.DisplayName);
        Assert.Equal(SourceCategory.SecretStore, secrets.Category);
        Assert.Equal("aws-parameter-store", parameters.Id);
        Assert.Equal("AWS Systems Manager Parameter Store", parameters.DisplayName);
        Assert.Equal(SourceCategory.ConfigurationService, parameters.Category);
        Assert.Equal(
            ["Region", "Profile", "ServiceUrl", "NamePrefix", "KeyPrefix", "IncludeValues", "MatchByName", "AccessKeyId", "SecretAccessKey", "SessionToken"],
            secrets.Settings.Select(s => s.Name));
        Assert.Equal(
            ["Path", "Region", "Profile", "ServiceUrl", "Recursive", "IncludeValues", "MatchByName", "AccessKeyId", "SecretAccessKey", "SessionToken"],
            parameters.Settings.Select(s => s.Name));
        Assert.True(parameters.Settings[0].Required);
        Assert.Single(parameters.Settings, s => s.Required);
        Assert.DoesNotContain(secrets.Settings, s => s.Required);
        foreach (var provider in new[] { secrets, parameters })
        {
            Assert.Equal(["AccessKeyId", "SecretAccessKey", "SessionToken"], provider.Settings.Where(s => s.Kind == SourceSettingKind.Secret).Select(s => s.Name));
            Assert.Equal(["AWS_SECRET_ACCESS_KEY"], provider.Settings.Single(s => s.Name == "SecretAccessKey").EnvironmentVariables);
            Assert.Equal(["AWS_ACCESS_KEY_ID"], provider.Settings.Single(s => s.Name == "AccessKeyId").EnvironmentVariables);
            Assert.Equal(["AWS_SESSION_TOKEN"], provider.Settings.Single(s => s.Name == "SessionToken").EnvironmentVariables);
            Assert.Equal(SourceSettingKind.Url, provider.Settings.Single(s => s.Name == "ServiceUrl").Kind);
            Assert.Equal("false", provider.Settings.Single(s => s.Name == "IncludeValues").DefaultValue);
            Assert.Equal("true", provider.Settings.Single(s => s.Name == "MatchByName").DefaultValue);
            Assert.All(provider.Settings, s => Assert.False(string.IsNullOrWhiteSpace(s.Help)));
        }
    }

    [Fact]
    public void Creates_a_Secrets_Manager_source_from_settings()
    {
        var source = Assert.IsType<AwsSecretsManagerSource>(new AwsSecretsManagerProvider().Create(With(
            ("Profile", " "), ("NamePrefix", " prod/orders/ "), ("KeyPrefix", "prod/"), ("IncludeValues", "True"), ("MatchByName", "false"),
            ("ServiceUrl", "http://localhost:4566"), ("SessionToken", "token"))));

        Assert.Equal("eu-west-1", source.Options.Region);
        Assert.Null(source.Options.Profile);
        Assert.Equal("prod/orders/", source.Options.NamePrefix);
        Assert.True(source.Options.IncludeValues);
        Assert.Equal("AKIDEXAMPLE", source.Options.AccessKeyId);
        Assert.Equal(FakeAws.SecretKey, source.Options.SecretAccessKey);
        Assert.Equal("token", source.Options.SessionToken);
        Assert.Equal(new Uri("http://localhost:4566"), source.Options.ServiceUrl);
        Assert.Equal("prod/", source.TraceOptions.KeyPrefix);
        Assert.False(source.TraceOptions.MatchByName);
        Assert.Equal("AWS Secrets Manager eu-west-1 prod/orders/*", source.DisplayName);

        var defaults = Assert.IsType<AwsSecretsManagerSource>(new AwsSecretsManagerProvider().Create(new Dictionary<string, string?>()));
        Assert.False(defaults.Options.IncludeValues);
        Assert.True(defaults.TraceOptions.MatchByName);
        Assert.Null(defaults.TraceOptions.KeyPrefix);
        Assert.Null(defaults.Options.ServiceUrl);
        Assert.Equal("AWS Secrets Manager (default region)", defaults.DisplayName);
    }

    [Fact]
    public void Creates_a_Parameter_Store_source_from_settings()
    {
        var source = Assert.IsType<AwsParameterStoreSource>(new AwsParameterStoreProvider().Create(With(("Path", " /orders/prod/ "), ("Recursive", "false"), ("IncludeValues", "true"), ("MatchByName", "false"))));

        Assert.Equal("/orders/prod/", source.Options.Path);
        Assert.False(source.Options.Recursive);
        Assert.True(source.Options.IncludeValues);
        Assert.False(source.TraceOptions.MatchByName);
        Assert.Equal("AWS Parameter Store eu-west-1 /orders/prod/", source.DisplayName);

        var defaults = Assert.IsType<AwsParameterStoreSource>(new AwsParameterStoreProvider().Create(new Dictionary<string, string?> { ["Path"] = "/p" }));
        Assert.True(defaults.Options.Recursive);
        Assert.False(defaults.Options.IncludeValues);
        Assert.True(defaults.TraceOptions.MatchByName);
        Assert.Equal("AWS Parameter Store (default region) /p", defaults.DisplayName);
    }

    [Theory]
    [InlineData("IncludeValues", "yes", "IncludeValues must be true or false.")]
    [InlineData("ServiceUrl", "not a url", "ServiceUrl must be an absolute URL.")]
    [InlineData("ServiceUrl", "http://secrets.example.com", "ServiceUrl must use https")]
    [InlineData("ServiceUrl", "https://user:pw@secrets.example.com", "ServiceUrl must not hold credentials.")]
    [InlineData("SecretAccessKey", null, "AccessKeyId and SecretAccessKey must be set together")]
    public void Refuses_invalid_settings(string key, string? value, string message)
    {
        var values = With((key, value));
        values["Path"] = "/p";

        var secrets = Assert.Throws<ArgumentException>(() => new AwsSecretsManagerProvider().Create(values));
        var parameters = Assert.Throws<ArgumentException>(() => new AwsParameterStoreProvider().Create(values));

        Assert.StartsWith(message, secrets.Message, StringComparison.Ordinal);
        Assert.StartsWith(message, parameters.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeAws.SecretKey, secrets.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "Path is required")]
    [InlineData(" ", "Path is required")]
    [InlineData("orders/prod", "Path must start with /")]
    public void Refuses_a_missing_or_relative_path(string? path, string message)
    {
        var ex = Assert.Throws<ArgumentException>(() => new AwsParameterStoreProvider().Create(With(("Path", path))));
        Assert.StartsWith(message, ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => new AwsParameterStoreProvider().Create(null!));
        Assert.Throws<ArgumentNullException>(() => new AwsSecretsManagerProvider().Create(null!));
    }

    [Fact]
    public async Task Loads_secrets_through_the_source_interface_and_finds_unused_entries()
    {
        var aws = new FakeAws()
            .On("ListSecrets", """{"SecretList":[{"Name":"prod/orders/db","ARN":"arn:aws:secretsmanager:eu-west-1:1:secret:prod/orders/db-AbCdEf"},{"Name":"prod/orders/Unused","Description":"old"}]}""")
            .On("BatchGetSecretValue", """{"SecretValues":[{"Name":"prod/orders/db","SecretString":"{\"password\":\"p\",\"user\":\"u\"}"},{"Name":"prod/orders/Unused","SecretString":"x"}]}""");
        var source = (AwsSecretsManagerSource)new AwsSecretsManagerProvider().Create(With(("NamePrefix", "prod/orders/"), ("IncludeValues", "true")));
        aws.Attach(source);
        var configuration = new ConfigurationBuilder().Add(new FakeSecretsManagerSource(new() { ["Db:Password"] = "p" })).Build();
        var tracer = ConfigTracer.For(configuration);

        var loaded = await tracer.WithSourceAsync(source, TestContext.Current.CancellationToken);
        var report = tracer.Trace();

        Assert.Equal("AWS Secrets Manager eu-west-1 prod/orders/*", loaded.DisplayName);
        Assert.Equal(
            [
                new SourceEntry("prod/orders/db#password", new AwsSecretRef("prod/orders/db", "password"), true, "JSON property", "eu-west-1, account 1"),
                new SourceEntry("prod/orders/db#user", new AwsSecretRef("prod/orders/db", "user"), true, "JSON property", "eu-west-1, account 1"),
                new SourceEntry("prod/orders/Unused", new AwsSecretRef("prod/orders/Unused"), true, "old", "eu-west-1"),
            ],
            loaded.Entries);
        Assert.Equal(["prod/orders/db#user", "prod/orders/Unused"], loaded.FindUnused(report).Select(e => e.Name));
        Assert.Contains("\"Values\":[\"prod/orders/\"]", aws.Requests[0].Body, StringComparison.Ordinal);
        Assert.Equal("https://secretsmanager.eu-west-1.amazonaws.com/", aws.Requests[0].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Loads_parameters_through_the_source_interface_and_finds_unused_entries()
    {
        var aws = new FakeAws().On("GetParametersByPath", """
            {"Parameters":[{"Name":"/orders/prod/Db/Timeout","Type":"String","Value":"30"},{"Name":"/orders/prod/Db/Password","Type":"SecureString","Value":"cipher"}]}
            """);
        var source = (AwsParameterStoreSource)new AwsParameterStoreProvider().Create(With(("Path", "/orders/prod/"), ("ServiceUrl", "http://127.0.0.1:4566/")));
        aws.Attach(source);
        var configuration = new ConfigurationBuilder().Add(new FakeSystemsManagerSource(new() { ["Db:Timeout"] = "30" })).Build();
        var tracer = ConfigTracer.For(configuration);

        var loaded = await tracer.WithSourceAsync(source, TestContext.Current.CancellationToken);
        var report = tracer.Trace();

        Assert.Equal(
            [
                new SourceEntry("/orders/prod/Db/Timeout", new AwsParameterRef("/orders/prod/Db/Timeout"), false, "String", "http://127.0.0.1:4566/"),
                new SourceEntry("/orders/prod/Db/Password", new AwsParameterRef("/orders/prod/Db/Password"), true, "SecureString", "http://127.0.0.1:4566/"),
            ],
            loaded.Entries);
        Assert.Equal(["/orders/prod/Db/Password"], loaded.FindUnused(report).Select(e => e.Name));
        Assert.Equal("http://127.0.0.1:4566/", aws.Requests[0].Uri.AbsoluteUri);
        Assert.DoesNotContain("cipher", report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checks_source_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new AwsSecretsManagerSource(null!));
        Assert.Throws<ArgumentNullException>(() => new AwsParameterStoreSource(null!));
        Assert.NotNull(new AwsSecretsManagerSource(new()).TraceOptions);
        Assert.NotNull(new AwsParameterStoreSource(new()).TraceOptions);
        await Assert.ThrowsAsync<ArgumentException>(() => new AwsParameterStoreSource(new()).LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Builds_clients_with_the_region_keys_and_service_URL()
    {
        using var regional = AwsClients.CreateSecretsManager(new AwsSecretsManagerOptions { Region = " us-east-2 ", AccessKeyId = "A", SecretAccessKey = "S" });
        Assert.Equal("us-east-2", regional.Config.RegionEndpoint.SystemName);
        Assert.False(regional.Config.AllowAutoRedirect);
        Assert.Equal("us-east-2", AwsClients.Describe(regional.Config));

        using var local = AwsClients.CreateParameterStore(new AwsParameterStoreOptions { ServiceUrl = new Uri("http://localhost:4566"), Region = "eu-north-1", Credentials = new AnonymousAWSCredentials() });
        Assert.Equal("http://localhost:4566/", local.Config.ServiceURL);
        Assert.Equal("eu-north-1", local.Config.AuthenticationRegion);

        using var defaultRegion = AwsClients.CreateParameterStore(new AwsParameterStoreOptions { ServiceUrl = new Uri("https://ssm.example.com"), AccessKeyId = "A", SecretAccessKey = "S", SessionToken = "T" });
        Assert.Equal("us-east-1", defaultRegion.Config.AuthenticationRegion);

        var configured = false;
        using var custom = AwsClients.CreateSecretsManager(new AwsSecretsManagerOptions { Region = "eu-west-1" }, configure: _ => configured = true);
        Assert.True(configured);
    }

    [Fact]
    public void Refuses_bad_connection_options_and_unknown_profiles()
    {
        Assert.Throws<ArgumentException>(() => AwsClients.CreateSecretsManager(new AwsSecretsManagerOptions { AccessKeyId = "A" }));
        Assert.Throws<ArgumentException>(() => AwsClients.CreateSecretsManager(new AwsSecretsManagerOptions { SecretAccessKey = "S" }));
        Assert.Throws<ArgumentException>(() => AwsClients.CreateSecretsManager(new AwsSecretsManagerOptions { SessionToken = "T" }));
        Assert.Throws<ArgumentException>(() => AwsClients.CreateParameterStore(new AwsParameterStoreOptions { ServiceUrl = new Uri("http://ssm.example.com") }));
        Assert.Throws<ArgumentNullException>(() => AwsClients.CreateParameterStore(null!));

        var ex = Assert.Throws<InvalidOperationException>(() => AwsClients.CreateSecretsManager(new AwsSecretsManagerOptions { Profile = $"configferret-missing-{Guid.NewGuid():N}" }));
        Assert.StartsWith("AWS profile configferret-missing-", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Checks_service_URLs_and_access_keys()
    {
        Assert.Null(AwsClients.CheckServiceUrl(null, "K"));
        Assert.Null(AwsClients.CheckServiceUrl(new Uri("https://x.example"), "K"));
        Assert.Null(AwsClients.CheckServiceUrl(new Uri("http://[::1]:4566"), "K"));
        Assert.Equal("K must be an absolute URL.", AwsClients.CheckServiceUrl(new Uri("/relative", UriKind.Relative), "K"));
        Assert.StartsWith("K must use https.", AwsClients.CheckServiceUrl(new Uri("ftp://localhost"), "K"), StringComparison.Ordinal);
        Assert.Null(AwsClients.CheckAccessKeys(new AwsSecretsManagerOptions(), "S"));
        Assert.Null(AwsClients.CheckAccessKeys(new AwsSecretsManagerOptions { AccessKeyId = "A", SecretAccessKey = "B", SessionToken = "C" }, "S"));
        Assert.StartsWith("S:AccessKeyId and S:SecretAccessKey must be set together", AwsClients.CheckAccessKeys(new AwsSecretsManagerOptions { AccessKeyId = "A" }, "S"), StringComparison.Ordinal);
        Assert.StartsWith("AccessKeyId and SecretAccessKey", AwsClients.CheckAccessKeys(new AwsSecretsManagerOptions { SessionToken = "C" }, string.Empty), StringComparison.Ordinal);
    }
}
