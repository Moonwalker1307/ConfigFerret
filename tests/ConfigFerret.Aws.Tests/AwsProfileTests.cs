namespace ConfigFerret.Aws.Tests;

/// <summary>Reads profiles from temporary shared config and credentials files.</summary>
[Collection(nameof(AwsProfileTests))]
public sealed class AwsProfileTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("configferret-aws-");

    public AwsProfileTests()
    {
        var credentials = Path.Join(_dir.FullName, "credentials");
        var config = Path.Join(_dir.FullName, "config");
        File.WriteAllText(credentials, "[cf-keys]\naws_access_key_id = AKIDPROFILE\naws_secret_access_key = profile-secret\n");
        File.WriteAllText(config, "[profile cf-keys]\nregion = ap-southeast-2\n\n[profile cf-empty]\nregion = eu-central-1\n");
        AwsClients.ProfilesLocation = credentials;
    }

    public void Dispose()
    {
        AwsClients.ProfilesLocation = null;
        _dir.Delete(recursive: true);
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
