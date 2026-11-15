using System.Net;
using Amazon.SecretsManager;

namespace ConfigFerret.Aws.Tests;

public class AwsSecretsLoadTests
{
    private const string DbArn = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:prod/orders/db-AbCdEf";

    private static readonly string TwoSecrets = $$"""
        {"SecretList":[
          {"Name":"prod/orders/db","ARN":"{{DbArn}}","Description":"orders database","LastChangedDate":1.523477145713E9,
           "Tags":[{"Key":"team","Value":"orders"},{"Key":"empty"}]},
          {"Name":"prod/orders/Api__Key"}
        ]}
        """;

    private static async Task<AwsSecrets> Load(FakeAws aws, bool includeValues = false, string? prefix = null)
    {
        using var client = aws.SecretsManager();
        return await AwsSecrets.LoadAsync(new AwsSecretsManagerOptions { IncludeValues = includeValues, NamePrefix = prefix }, client, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Lists_names_and_metadata_without_reading_values_by_default()
    {
        var aws = new FakeAws().On("ListSecrets", TwoSecrets);

        var secrets = await Load(aws);

        Assert.Equal(["prod/orders/db", "prod/orders/Api__Key"], secrets.Entries.Select(e => e.Name));
        var db = secrets.Entries[0];
        Assert.Equal(DbArn, db.Arn);
        Assert.Equal("orders database", db.Description);
        Assert.Equal(new DateTimeOffset(2018, 4, 11, 20, 5, 45, 713, TimeSpan.Zero), db.LastChanged);
        Assert.Equal("orders", db.Tags["team"]);
        Assert.Equal(string.Empty, db.Tags["empty"]);
        Assert.Null(db.Value);
        Assert.False(db.ValueRead);
        Assert.Null(db.Properties);
        Assert.Equal("eu-west-1, account 123456789012", db.Location);
        Assert.Null(secrets.Entries[1].Arn);
        Assert.Null(secrets.Entries[1].Location);
        Assert.Null(secrets.Entries[1].Description);
        Assert.Null(secrets.Entries[1].LastChanged);
        Assert.Equal("eu-west-1", secrets.Location);
        Assert.Empty(secrets.Warnings);
        Assert.Equal(["ListSecrets"], aws.Requests.Select(r => r.Operation));
        Assert.Equal("""{"MaxResults":100}""", aws.Requests[0].Body);
    }

    [Fact]
    public async Task Filters_by_name_prefix_and_follows_next_tokens()
    {
        var aws = new FakeAws()
            .On("ListSecrets", """{"SecretList":[{"Name":"prod/a"}],"NextToken":"t1"}""")
            .On("ListSecrets", """{"SecretList":[{"Name":"prod/b"}],"NextToken":"t2"}""")
            .On("ListSecrets", """{"SecretList":[{"Name":"prod/c"}]}""");

        var secrets = await Load(aws, prefix: "prod/");

        Assert.Equal(["prod/a", "prod/b", "prod/c"], secrets.Entries.Select(e => e.Name));
        Assert.Equal("prod/", secrets.NamePrefix);
        var bodies = aws.Operations("ListSecrets").ToList();
        Assert.Equal("""{"Filters":[{"Key":"name","Values":["prod/"]}],"MaxResults":100}""", bodies[0]);
        Assert.Contains("\"NextToken\":\"t1\"", bodies[1], StringComparison.Ordinal);
        Assert.Contains("\"NextToken\":\"t2\"", bodies[2], StringComparison.Ordinal);
        Assert.Equal(3, bodies.Count);
    }

    [Fact]
    public async Task Stops_when_a_next_token_repeats()
    {
        var aws = new FakeAws()
            .On("ListSecrets", """{"SecretList":[{"Name":"a"}],"NextToken":"t1"}""")
            .On("ListSecrets", """{"SecretList":[{"Name":"b"}],"NextToken":"t1"}""");

        var secrets = await Load(aws);

        Assert.Equal(["a", "b"], secrets.Entries.Select(e => e.Name));
        Assert.Equal(2, aws.Operations("ListSecrets").Count());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"SecretList":null}""")]
    [InlineData("""{"SecretList":[]}""")]
    [InlineData("""{"SecretList":[{},{"Name":""},{"Name":"x"},{"Name":"x","Description":"duplicate"}],"NextToken":""}""")]
    public async Task Skips_entries_without_a_name_and_duplicate_names(string body)
    {
        var secrets = await Load(new FakeAws().On("ListSecrets", body), includeValues: true);

        Assert.All(secrets.Entries, e => Assert.Equal("x", e.Name));
        Assert.True(secrets.Entries.Count <= 1);
        Assert.All(secrets.Entries, e => Assert.Null(e.Description));
    }

    [Fact]
    public async Task Reads_values_in_batches_and_flattens_JSON_secrets()
    {
        var list = string.Join(",", Enumerable.Range(0, 25).Select(i => $$"""{"Name":"s{{i}}"}"""));
        var aws = new FakeAws()
            .On("ListSecrets", $$"""{"SecretList":[{{list}}]}""")
            .On("BatchGetSecretValue", """
                {"SecretValues":[
                  {"Name":"s0","SecretString":"{\"password\":\"p@ss\",\"port\":5432,\"ssl\":true,\"none\":null,\"db\":{\"host\":\"h\",\"replicas\":[\"r1\",\"r2\"]}}"},
                  {"Name":"s1","SecretString":"plain"},
                  {"Name":"s2","SecretString":"{not json"},
                  {"Name":"s3","SecretString":"{}"},
                  {"Name":"s4","SecretString":"[1,2]"},
                  {"Name":"s5","SecretBinary":"AAEC"},
                  {"Name":"unknown","SecretString":"ignored"}
                ]}
                """)
            .On("BatchGetSecretValue", """{"SecretValues":[{"ARN":"arn-not-asked","SecretString":"x"}]}""");

        var secrets = await Load(aws, includeValues: true);

        var batches = aws.Operations("BatchGetSecretValue").ToList();
        Assert.Equal(2, batches.Count);
        Assert.Contains("\"s19\"", batches[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\"s20\"", batches[0], StringComparison.Ordinal);
        Assert.Contains("\"s24\"", batches[1], StringComparison.Ordinal);

        var s0 = secrets.Find("s0")!;
        Assert.True(s0.ValueRead);
        Assert.Equal("p@ss", s0.Properties!["password"]);
        Assert.Equal("p@ss", s0.Properties["PASSWORD"]);
        Assert.Equal("5432", s0.Properties["port"]);
        Assert.Equal("true", s0.Properties["ssl"]);
        Assert.Null(s0.Properties["none"]);
        Assert.Equal("h", s0.Properties["db:host"]);
        Assert.Equal("r2", s0.Properties["db:replicas:1"]);
        Assert.Equal(7, s0.Properties.Count);
        Assert.Equal("plain", secrets.Find("s1")!.Value);
        Assert.Null(secrets.Find("s1")!.Properties);
        Assert.Null(secrets.Find("s2")!.Properties);
        Assert.Null(secrets.Find("s3")!.Properties);
        Assert.Null(secrets.Find("s4")!.Properties);
        Assert.True(secrets.Find("s5")!.IsBinary);
        Assert.Null(secrets.Find("s5")!.Value);
        Assert.False(secrets.Find("s1")!.IsBinary);
        Assert.Null(secrets.Find("unknown"));

        // s6 to s24 were asked for but never answered.
        Assert.Contains("AWS did not return the value of secret s6.", secrets.Warnings);
        Assert.Contains("AWS did not return the value of secret s24.", secrets.Warnings);
        Assert.DoesNotContain(secrets.Warnings, w => w.Contains("s0.", StringComparison.Ordinal));
        Assert.Equal(19, secrets.Warnings.Count);
        Assert.False(secrets.Find("s6")!.ValueRead);
    }

    [Fact]
    public async Task Asks_for_values_by_ARN_and_matches_answers_by_ARN()
    {
        var aws = new FakeAws()
            .On("ListSecrets", TwoSecrets)
            .On("BatchGetSecretValue", $$"""
                {"SecretValues":[{"ARN":"{{DbArn}}","SecretString":"one"}],
                 "Errors":[{"SecretId":"prod/orders/Api__Key","ErrorCode":"DecryptionFailure","Message":"no kms access"}]}
                """);

        var secrets = await Load(aws, includeValues: true);

        Assert.Contains(DbArn, aws.Operations("BatchGetSecretValue").Single(), StringComparison.Ordinal);
        Assert.Equal("one", secrets.Find("prod/orders/db")!.Value);
        Assert.Equal(["Could not read the value of AWS secret prod/orders/Api__Key: DecryptionFailure."], secrets.Warnings);
    }

    [Fact]
    public async Task Follows_next_tokens_in_a_batch_and_names_errors_for_unknown_ids()
    {
        var aws = new FakeAws()
            .On("ListSecrets", """{"SecretList":[{"Name":"a"},{"Name":"b"}]}""")
            .On("BatchGetSecretValue", """{"SecretValues":[{"Name":"a","SecretString":"1"}],"NextToken":"n"}""")
            .On("BatchGetSecretValue", """{"SecretValues":[{"Name":"b","SecretString":"2"}],"Errors":[{"SecretId":"zzz"},{}],"NextToken":"n"}""");

        var secrets = await Load(aws, includeValues: true);

        Assert.Equal("1", secrets.Find("a")!.Value);
        Assert.Equal("2", secrets.Find("b")!.Value);
        Assert.Contains("\"NextToken\":\"n\"", aws.Operations("BatchGetSecretValue").Last(), StringComparison.Ordinal);
        Assert.Equal(
            ["Could not read the value of AWS secret zzz: unknown error.", "Could not read the value of AWS secret (unknown): unknown error."],
            secrets.Warnings);
    }

    [Theory]
    [InlineData("AccessDeniedException")]
    [InlineData("UnknownOperationException")]
    public async Task Falls_back_to_one_GetSecretValue_per_secret_when_batches_are_refused(string error)
    {
        var aws = new FakeAws()
            .On("ListSecrets", $$"""{"SecretList":[{{string.Join(",", Enumerable.Range(0, 21).Select(i => $"{{\"Name\":\"s{i}\"}}"))}}]}""")
            .Fail("BatchGetSecretValue", error)
            .On("GetSecretValue", """{"Name":"s0","SecretString":"v"}""")
            .On("GetSecretValue", """{"Name":"s1","SecretBinary":"AAEC"}""")
            .Fail("GetSecretValue", "ResourceNotFoundException");

        var secrets = await Load(aws, includeValues: true);

        // Refused once, then never tried again.
        Assert.Single(aws.Operations("BatchGetSecretValue"));
        Assert.Equal(21, aws.Operations("GetSecretValue").Count());
        Assert.Equal("v", secrets.Find("s0")!.Value);
        Assert.True(secrets.Find("s1")!.IsBinary);
        Assert.False(secrets.Find("s0")!.IsBinary);
        Assert.Contains("Could not read the value of AWS secret s2: ResourceNotFoundException.", secrets.Warnings);
        Assert.Equal(19, secrets.Warnings.Count);
    }

    [Fact]
    public async Task Fails_when_listing_is_refused_without_printing_the_credential()
    {
        var aws = new FakeAws().Fail("ListSecrets", "AccessDeniedException");

        var ex = await Assert.ThrowsAsync<AmazonSecretsManagerException>(() => Load(aws));

        Assert.Equal("AccessDeniedException", ex.ErrorCode);
        Assert.DoesNotContain(FakeAws.SecretKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_on_other_batch_errors()
    {
        var aws = new FakeAws().On("ListSecrets", """{"SecretList":[{"Name":"a"}]}""").Fail("BatchGetSecretValue", "InternalServiceError", HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAnyAsync<AmazonSecretsManagerException>(() => Load(aws, includeValues: true));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task Reads_no_values_when_nothing_is_listed()
    {
        var aws = new FakeAws().On("ListSecrets", """{"SecretList":[]}""");

        Assert.Empty((await Load(aws, includeValues: true)).Entries);
        Assert.Single(aws.Requests);
    }

    [Fact]
    public void Finds_by_name_full_ARN_and_partial_ARN()
    {
        var secrets = new AwsSecrets([new AwsSecret("prod/orders/db") { Arn = DbArn }, new AwsSecret("other")]);

        Assert.Same(secrets.Entries[0], secrets.Find("prod/orders/db"));
        Assert.Same(secrets.Entries[0], secrets.Find(DbArn));
        Assert.Same(secrets.Entries[0], secrets.Find("arn:aws:secretsmanager:eu-west-1:123456789012:secret:prod/orders/db"));
        Assert.Null(secrets.Find("arn:aws:secretsmanager:eu-west-1:123456789012:secret:prod/orders"));
        Assert.Null(secrets.Find("arn:aws:secretsmanager:eu-west-1:123456789012:secret:prod/orders/d"));
        Assert.Null(secrets.Find("arn:other"));
        Assert.Null(secrets.Find("PROD/ORDERS/DB"));
        Assert.Null(secrets.Find("prod/orders"));
    }

    [Fact]
    public void Describes_ARNs()
    {
        Assert.Equal("eu-west-1, account 1", AwsArn.Describe("arn:aws:secretsmanager:eu-west-1:1:secret:x"));
        Assert.Equal("eu-west-1", AwsArn.Describe("arn:aws:secretsmanager:eu-west-1::secret:x"));
        Assert.Null(AwsArn.Describe("arn:aws:secretsmanager::1:secret:x"));
        Assert.Null(AwsArn.Describe("nope:aws:secretsmanager:eu-west-1:1:secret:x"));
        Assert.Null(AwsArn.Describe("arn:aws:secretsmanager:eu-west-1"));
        Assert.Null(AwsArn.Describe(null));
    }

    [Fact]
    public async Task Checks_its_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new AwsSecrets(null!));
        Assert.Throws<ArgumentNullException>(() => new AwsSecrets([null!]));
        Assert.Throws<ArgumentException>(() => new AwsSecret(""));
        Assert.Throws<ArgumentNullException>(() => new AwsSecrets([]).Find(null!));
        Assert.Null(new AwsSecrets([], namePrefix: "").NamePrefix);
        using var client = new FakeAws().SecretsManager();
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentNullException>(() => AwsSecrets.LoadAsync(null!, client, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AwsSecrets.LoadAsync(new AwsSecretsManagerOptions(), client: null!, ct));
    }

    [Fact]
    public void Converts_dates_to_UTC()
    {
        Assert.Null(AwsSecrets.ToOffset(null));
        Assert.Equal(TimeSpan.Zero, AwsSecrets.ToOffset(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified))!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), AwsSecrets.ToOffset(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), AwsSecrets.ToOffset(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime()));
    }
}
