using System.Net;
using System.Text;
using Amazon.Runtime;

namespace ConfigFerret.Aws.Tests;

/// <summary>
/// The AWS SDK's JSON readers loop forever, using more and more memory, on a list or structure that is a plain value.
/// These inputs came from fuzzing (aws-secrets and aws-parameters timeouts) and must fail fast instead.
/// </summary>
public class AwsResponseGuardTests
{
    [Theory]
    [InlineData("""{"Parameters":"/orders/prroot"}],"NextToken":"x"}""")]
    [InlineData("""{"Parameters":"x"}""")]
    [InlineData("""{"Parameters":5}""")]
    [InlineData("""{"Parameters":{"a":1}}""")]
    [InlineData("""{"Parameters":["x"]}""")]
    [InlineData("""{"Parameters":[5]}""")]
    public async Task Refuses_parameter_responses_the_SDK_cannot_read(string body)
    {
        var aws = new FakeAws().On("GetParametersByPath", body);
        using var client = aws.ParameterStore();

        var ex = await Assert.ThrowsAnyAsync<AmazonServiceException>(() =>
            AwsParameters.LoadAsync(new AwsParameterStoreOptions { Path = "/p" }, client, TestContext.Current.CancellationToken));

        Assert.Equal("InvalidResponseException", ex.ErrorCode);
        Assert.Single(aws.Requests);
    }

    [Theory]
    [InlineData("""{"SecretList":[{},{"Name":"x"}],"NextToken":""}""", """{"SecretValues":null,"Errors":""}""")]
    [InlineData("""{"SecretList":[{"Name":"a","RotationRules":"x"}]}""", null)]
    [InlineData("""{"SecretList":[{"Name":"a","SecretVersionsToStages":"x"}]}""", null)]
    [InlineData("""{"SecretList":[{"Name":"a","Tags":[5]}]}""", null)]
    [InlineData("""{"SecretList":[{"Name":"a","ExternalSecretRotationMetadata":"x"}]}""", null)]
    [InlineData("""{"SecretList":[{"Name":"a"}]}""", """{"SecretValues":[],"Errors":["x"]}""")]
    [InlineData("""{"SecretList":[{"Name":"a"}]}""", """{"SecretValues":[{"Name":"a","VersionStages":"AWSCURRENT"}]}""")]
    [InlineData("""{"SecretList":[{"Name":"a"}]}""", """{"SecretValues":[{"Name":"a"}],""")]
    public async Task Refuses_secret_responses_the_SDK_cannot_read(string list, string? values)
    {
        var aws = new FakeAws().On("ListSecrets", list);
        if (values is not null)
        {
            aws.On("BatchGetSecretValue", values);
        }

        using var client = aws.SecretsManager();

        var ex = await Assert.ThrowsAnyAsync<AmazonServiceException>(() =>
            AwsSecrets.LoadAsync(new AwsSecretsManagerOptions { IncludeValues = true }, client, TestContext.Current.CancellationToken));

        Assert.Equal("InvalidResponseException", ex.ErrorCode);
    }

    [Theory]
    [InlineData("""{}""", true)]
    [InlineData("""{"SecretList":null,"RotationRules":null,"VersionStages":null}""", true)]
    [InlineData("""{"SecretList":[null,{}],"RotationRules":{},"VersionStages":["a"],"Other":"x","Deep":[[{"Tags":[]}]]}""", true)]
    [InlineData("""{"Deep":[[{"Tags":"x"}]]}""", false)]
    [InlineData("""{"Deep":{"Values":"x"}}""", false)]
    [InlineData("""{"RotationRules":[]}""", false)]
    [InlineData("""[]""", false)]
    [InlineData("""not json""", false)]
    public void Checks_the_shape_of_lists_and_structures(string body, bool readable)
    {
        Assert.Equal(readable, AwsResponseGuard.IsReadable(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public async Task Passes_good_and_error_responses_through_with_their_headers()
    {
        var inner = new FakeAws().On("ListSecrets", """{"SecretList":[]}""").Fail("GetSecretValue", "ResourceNotFoundException");
        using var invoker = new HttpMessageInvoker(new AwsResponseGuard(inner, disposeInner: false));
        var ct = TestContext.Current.CancellationToken;

        using var ok = await invoker.SendAsync(Request("secretsmanager.ListSecrets"), ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("""{"SecretList":[]}""", await ok.Content.ReadAsStringAsync(ct));
        Assert.Equal("application/x-amz-json-1.1", ok.Content.Headers.ContentType!.MediaType);

        using var error = await invoker.SendAsync(Request("secretsmanager.GetSecretValue"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("ResourceNotFoundException", await error.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passes_an_empty_success_through()
    {
        var inner = new FakeAws().On("ListSecrets", string.Empty);
        using var invoker = new HttpMessageInvoker(new AwsResponseGuard(inner, disposeInner: false));

        using var response = await invoker.SendAsync(Request("secretsmanager.ListSecrets"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Gives_the_SDK_clients_that_do_not_follow_redirects_and_keep_the_timeout()
    {
        var factory = new GuardedHttpClientFactory(null);
        var config = new Amazon.SecretsManager.AmazonSecretsManagerConfig { Timeout = TimeSpan.FromSeconds(7) };

        using var client = factory.CreateHttpClient(config);

        Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
        Assert.False(factory.UseSDKHttpClientCaching(config));
        Assert.True(factory.DisposeHttpClientsAfterUse(config));
        using var withHandler = new GuardedHttpClientFactory(new FakeAws()).CreateHttpClient(new Amazon.SecretsManager.AmazonSecretsManagerConfig());
        Assert.Equal(TimeSpan.FromSeconds(100), withHandler.Timeout);
        Assert.Throws<ArgumentNullException>(() => factory.CreateHttpClient(null!));
    }

    private static HttpRequestMessage Request(string target)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://secretsmanager.eu-west-1.amazonaws.com/") { Content = new StringContent("{}") };
        request.Headers.Add("X-Amz-Target", target);
        return request;
    }
}
