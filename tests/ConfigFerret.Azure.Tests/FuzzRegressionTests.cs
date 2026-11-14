using System.Net;
using Azure;

namespace ConfigFerret.Azure.Tests;

/// <summary>Inputs the fuzz targets found, reduced to what failed.</summary>
public class FuzzRegressionTests
{
    // Azure.Core's status code classifier throws IndexOutOfRangeException for some statuses above 599, such as 700.
    [Theory]
    [InlineData(700)]
    [InlineData(999)]
    public async Task A_status_far_above_599_on_a_list_is_a_request_failure(int status)
    {
        var keyVault = new FakeAzureHandler().Respond("/secrets", "{}", (HttpStatusCode)status);
        var kv = await Assert.ThrowsAsync<RequestFailedException>(() => KeyVaultLoadTests.Load(keyVault));
        Assert.Equal("Azure Key Vault contoso-prod sent a response that could not be read.", kv.Message);

        var appConfig = new FakeAzureHandler { Challenge = false }.Respond(AppConfigurationLoadTests.KeysPath(), "{}", (HttpStatusCode)status);
        var ac = await Assert.ThrowsAsync<RequestFailedException>(() => AppConfigurationLoadTests.Load(appConfig));
        Assert.Equal("Azure App Configuration contoso sent a response that could not be read.", ac.Message);
    }

    [Fact]
    public async Task A_status_far_above_599_on_a_value_is_a_warning()
    {
        var handler = new FakeAzureHandler()
            .Respond("/secrets", KeyVaultLoadTests.Page(null, KeyVaultLoadTests.Item("A")))
            .Respond("/secrets/A", "{}", (HttpStatusCode)700);

        var secrets = await KeyVaultLoadTests.Load(handler, o => o.IncludeValues = true);

        Assert.Equal(["Could not read the value of Azure Key Vault secret A (contoso-prod): the response could not be read."], secrets.Warnings);
    }

    // The SDK throws NullReferenceException for a body of JSON null.
    [Fact]
    public async Task A_null_body_is_a_request_failure_or_a_warning()
    {
        var keyVault = new FakeAzureHandler().Respond("/secrets", "null");
        Assert.Equal("Azure Key Vault contoso-prod sent a response that could not be read.", (await Assert.ThrowsAsync<RequestFailedException>(() => KeyVaultLoadTests.Load(keyVault))).Message);

        var appConfig = new FakeAzureHandler { Challenge = false }.Respond(AppConfigurationLoadTests.KeysPath(), "null");
        Assert.Equal("Azure App Configuration contoso sent a response that could not be read.", (await Assert.ThrowsAsync<RequestFailedException>(() => AppConfigurationLoadTests.Load(appConfig))).Message);

        var value = new FakeAzureHandler()
            .Respond("/secrets", KeyVaultLoadTests.Page(null, KeyVaultLoadTests.Item("A")))
            .Respond("/secrets/A", "null");
        var secrets = await KeyVaultLoadTests.Load(value, o => o.IncludeValues = true);
        Assert.Null(secrets.Find("A")!.Value);
    }
}
