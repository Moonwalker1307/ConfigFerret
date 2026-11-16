using ConfigFerret.Tests.Shared;

namespace ConfigFerret.Aws.Tests;

/// <summary>Reads profiles from temporary shared config and credentials files.</summary>
[Collection(nameof(AwsProfileTests))]
public sealed class AwsProfileTests : IDisposable
{
    private readonly TempSettingsFolder _dir = new();

    public AwsProfileTests()
    {
        var credentials = _dir.Write("credentials", "[cf-keys]\naws_access_key_id = AKIDPROFILE\naws_secret_access_key = profile-secret\n");
        _dir.Write("config", "[profile cf-keys]\nregion = ap-southeast-2\n\n[profile cf-empty]\nregion = eu-central-1\n");
        AwsClients.ProfilesLocation = credentials;
    }

    public void Dispose()
    {
        AwsClients.ProfilesLocation = null;
        _dir.Dispose();
    }

    [Fact]
    public async Task Uses_a_profiles_keys_and_region()
    {
        var aws = new FakeAws().On("ListSecrets", """{"SecretList":[]}""");
        using var client = aws.SecretsManager(new AwsSecretsManagerOptions { Profile = "cf-keys" });

        await AwsSecrets.LoadAsync(new AwsSecretsManagerOptions(), client, TestContext.Current.CancellationToken);

        Assert.Equal("ap-southeast-2", client.Config.RegionEndpoint.SystemName);
        Assert.Contains("Credential=AKIDPROFILE/", aws.SigningHeaders[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Runs_with_the_tests_that_look_up_a_profile_that_is_not_there()
    {
        // The profile files come from a setting that every test sees, so a lookup that runs at the same time reads them.
        var attribute = Assert.IsType<CollectionAttribute>(Assert.Single(typeof(AwsSourceTests).GetCustomAttributes(typeof(CollectionAttribute), inherit: false)));

        Assert.Equal(nameof(AwsProfileTests), attribute.Name);
    }

    [Fact]
    public void Keeps_an_explicit_region_and_explicit_keys_over_the_profile()
    {
        using var client = AwsClients.CreateSecretsManager(new AwsSecretsManagerOptions { Profile = "cf-empty", Region = "us-west-2", AccessKeyId = "A", SecretAccessKey = "S" });

        Assert.Equal("us-west-2", client.Config.RegionEndpoint.SystemName);
    }

    [Fact]
    public void Fails_for_a_profile_without_credentials()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AwsClients.CreateParameterStore(new AwsParameterStoreOptions { Profile = "cf-empty" }));

        Assert.Equal("AWS profile cf-empty has no usable credentials.", ex.Message);
    }
}
