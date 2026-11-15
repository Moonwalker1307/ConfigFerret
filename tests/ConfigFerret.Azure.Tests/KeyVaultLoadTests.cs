using System.Net;
using Azure;
using Azure.Security.KeyVault.Secrets;

namespace ConfigFerret.Azure.Tests;

public class KeyVaultLoadTests
{
    internal const string Vault = "https://contoso-prod.vault.azure.net/";
    internal const string SecretValue = "kv-secret-value-never-printed";

    internal static string Item(string name, bool enabled = true, string extra = "") =>
        $$"""{"id":"{{Vault}}secrets/{{name}}","attributes":{"enabled":{{(enabled ? "true" : "false")}},"updated":1767225600{{extra}}},"tags":{"team":"orders"},"contentType":"text/plain"}""";

    internal static string Page(string? next, params string[] items) =>
        $$"""{"value":[{{string.Join(",", items)}}],"nextLink":{{(next is null ? "null" : $"\"{Vault}secrets?api-version=7.6&$skiptoken={next}\"")}}}""";

    internal static string Value(string name, string value = SecretValue, string version = "v1") =>
        $$$"""{"value":"{{{value}}}","id":"{{{Vault}}}secrets/{{{name}}}/{{{version}}}","attributes":{"enabled":true}}""";

    internal static Task<AzureKeyVaultSecrets> Load(FakeAzureHandler handler, Action<AzureKeyVaultOptions>? configure = null, FakeCredential? credential = null)
    {
        var options = new AzureKeyVaultOptions { VaultUri = new(Vault) };
        configure?.Invoke(options);
        return AzureKeyVaultSecrets.LoadAsync(options, credential ?? new FakeCredential(), handler.KeyVaultOptions(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Lists_every_page_with_properties_and_signs_in_after_the_challenge()
    {
        var handler = new FakeAzureHandler()
            .Respond("/secrets", Page("p2", Item("Db--Password", extra: ""","exp":1893456000,"nbf":1700000000""")))
            .Respond("/secrets?$skiptoken=p2", Page(null, Item("Api--Key")));
        var credential = new FakeCredential();

        var secrets = await Load(handler, credential: credential);

        Assert.Equal("contoso-prod", secrets.Vault);
        Assert.Equal(["Api--Key", "Db--Password"], secrets.Secrets.Select(s => s.Name));
        var secret = secrets.Find("db--password")!;
        Assert.True(secret.Enabled);
        Assert.Equal("contoso-prod", secret.Vault);
        Assert.Equal("text/plain", secret.ContentType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767225600), secret.UpdatedOn);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), secret.ExpiresOn);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), secret.NotBefore);
        Assert.Equal("orders", secret.Tags["team"]);
        Assert.Null(secret.Value);
        Assert.Null(secret.Version);
        Assert.False(secret.Managed);
        Assert.Equal("Db:Password", secret.ConfigurationKey);
        Assert.Empty(secrets.Warnings);
        Assert.Equal(["/secrets", "/secrets?$skiptoken=p2"], handler.Paths);
        Assert.Contains(credential.Scopes, s => s.Contains("https://vault.azure.net/.default"));
    }

    [Fact]
    public async Task Skips_disabled_secrets_unless_asked_and_filters_by_name_and_prefix()
    {
        var handler = new FakeAzureHandler().Respond("/secrets", Page(null, Item("Db--Password"), Item("Db--Old", enabled: false), Item("Api--Key"), Item("Other")));

        var all = await Load(handler);
        Assert.Equal(["Api--Key", "Db--Password", "Other"], all.Secrets.Select(s => s.Name));

        var withDisabled = await Load(handler, o => o.IncludeDisabled = true);
        Assert.False(withDisabled.Find("Db--Old")!.Enabled);

        var filtered = await Load(handler, o =>
        {
            o.SecretNames.Add("db--*");
            o.SecretNames.Add(" other ");
            o.SecretNames.Add(" ");
        });
        Assert.Equal(["Db--Password", "Other"], filtered.Secrets.Select(s => s.Name));
    }

    [Fact]
    public async Task Reads_values_only_when_asked_and_warns_without_the_value_when_one_cannot_be_read()
    {
        var handler = new FakeAzureHandler()
            .Respond("/secrets", Page(null, Item("Db--Password"), Item("Api--Key"), Item("Old", enabled: false)))
            .Respond("/secrets/Db--Password", Value("Db--Password", version: "abc123"))
            .Respond("/secrets/Api--Key", """{"error":{"code":"Forbidden","message":"Caller is not authorized"}}""", HttpStatusCode.Forbidden);

        var secrets = await Load(handler, o =>
        {
            o.IncludeValues = true;
            o.IncludeDisabled = true;
        });

        Assert.Equal(SecretValue, secrets.Find("Db--Password")!.Value);
        Assert.Equal("abc123", secrets.Find("Db--Password")!.Version);
        Assert.Null(secrets.Find("Api--Key")!.Value);
        var warning = Assert.Single(secrets.Warnings);
        Assert.Equal("Could not read the value of Azure Key Vault secret Api--Key (contoso-prod): status 403 (Forbidden).", warning);
        Assert.DoesNotContain("/secrets/Old", handler.Paths);

        var metadataOnly = new FakeAzureHandler().Respond("/secrets", Page(null, Item("Db--Password")));
        await Load(metadataOnly);
        Assert.Equal(["/secrets"], metadataOnly.Paths);
    }

    [Fact]
    public async Task A_value_response_that_cannot_be_read_becomes_a_warning()
    {
        var handler = new FakeAzureHandler()
            .Respond("/secrets", Page(null, Item("Db--Password")))
            .Respond("/secrets/Db--Password", "{not json");

        var secrets = await Load(handler, o => o.IncludeValues = true);

        Assert.Null(secrets.Find("Db--Password")!.Value);
        Assert.Equal(["Could not read the value of Azure Key Vault secret Db--Password (contoso-prod): the response could not be read."], secrets.Warnings);
    }

    [Fact]
    public async Task A_refused_list_throws_the_service_error()
    {
        var handler = new FakeAzureHandler().Respond("/secrets", """{"error":{"code":"Forbidden","message":"no list permission"}}""", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => Load(handler));

        Assert.Equal(403, ex.Status);
        Assert.DoesNotContain(FakeCredential.Token, ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"value":[{"id":"not a url"}]}""")]
    [InlineData("""{"value":{"a":1}}""")]
    public async Task A_list_that_cannot_be_read_throws_a_request_failure_that_names_the_vault(string body)
    {
        var handler = new FakeAzureHandler().Respond("/secrets", body);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => Load(handler));

        Assert.Equal("Azure Key Vault contoso-prod sent a response that could not be read.", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Skips_items_without_a_name()
    {
        var handler = new FakeAzureHandler().Respond("/secrets", """{"value":[null,{"attributes":{}},{"id":"https://contoso-prod.vault.azure.net/secrets/Ok"}]}""");

        var secrets = await Load(handler);

        Assert.Equal(["Ok"], secrets.Secrets.Select(s => s.Name));
        Assert.True(secrets.Secrets[0].Enabled);
    }

    [Fact]
    public async Task Stops_after_the_page_limit_with_a_warning()
    {
        var handler = new FakeAzureHandler().Respond("/secrets", Page("loop", Item("A"))).Respond("/secrets?$skiptoken=loop", Page("loop", Item("B")));

        var secrets = await Load(handler);

        Assert.Equal(AzureKeyVaultSecrets.MaxPages, handler.Paths.Count);
        Assert.Equal([$"Stopped listing Azure Key Vault contoso-prod after {AzureKeyVaultSecrets.MaxPages} pages; some secrets were not read."], secrets.Warnings);
        Assert.Equal(["A", "B"], secrets.Secrets.Select(s => s.Name));
    }

    [Fact]
    public async Task A_last_page_at_the_limit_is_not_a_warning()
    {
        var handler = new FakeAzureHandler();
        for (var i = 1; i < AzureKeyVaultSecrets.MaxPages; i++)
        {
            handler.Respond(i == 1 ? "/secrets" : $"/secrets?$skiptoken=p{i}", Page($"p{i + 1}", Item($"S{i}")));
        }

        handler.Respond($"/secrets?$skiptoken=p{AzureKeyVaultSecrets.MaxPages}", Page(null, Item("Last")));

        var secrets = await Load(handler);

        Assert.Empty(secrets.Warnings);
        Assert.Equal(AzureKeyVaultSecrets.MaxPages, secrets.Secrets.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://contoso.vault.azure.net/")]
    [InlineData("https://user:pw@contoso.vault.azure.net/")]
    public async Task Rejects_a_vault_address_that_is_missing_or_not_https(string? uri)
    {
        var options = new AzureKeyVaultOptions { VaultUri = uri is null ? null : new Uri(uri) };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => AzureKeyVaultSecrets.LoadAsync(options, new FakeCredential(), TestContext.Current.CancellationToken));

        Assert.StartsWith("VaultUri must be an absolute https URL", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => AzureKeyVaultSecrets.LoadAsync(new AzureKeyVaultOptions { VaultUri = new Uri("relative", UriKind.Relative) }, new FakeCredential(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Checks_arguments()
    {
        var options = new AzureKeyVaultOptions { VaultUri = new(Vault) };
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentNullException>(() => AzureKeyVaultSecrets.LoadAsync(null!, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AzureKeyVaultSecrets.LoadAsync(null!, new FakeCredential(), ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AzureKeyVaultSecrets.LoadAsync(options, null!, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AzureKeyVaultSecrets.LoadAsync(options, new FakeCredential(), clientOptions: null!, ct));
        Assert.Throws<ArgumentException>(() => new AzureKeyVaultSecrets(" ", []));
        Assert.Throws<ArgumentNullException>(() => new AzureKeyVaultSecrets("v", null!));
        Assert.Throws<ArgumentNullException>(() => new AzureKeyVaultSecrets("v", [null!]));
        Assert.Throws<ArgumentNullException>(() => new AzureKeyVaultSecrets("v", []).Find(null!));
        Assert.Throws<ArgumentNullException>(() => new AzureKeyVaultSecrets("v", []).FindByConfigurationKey(null!));
    }

    [Fact]
    public async Task Load_with_options_only_builds_a_credential_from_them()
    {
        // No network: a client secret credential fails to sign in against a fake handler only when used, so check the
        // argument checks and the half-set service principal instead.
        var options = new AzureKeyVaultOptions { VaultUri = new(Vault), ClientSecret = "s" };
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => AzureKeyVaultSecrets.LoadAsync(options, TestContext.Current.CancellationToken));
        Assert.Equal("A client secret needs a tenant ID and a client ID too. (Parameter 'clientSecret')", ex.Message);
    }

    [Fact]
    public void Snapshot_keeps_the_last_secret_per_name_and_indexes_configuration_keys()
    {
        var secrets = new AzureKeyVaultSecrets("v", [new("Db--Password", "v"), new("db--password", "v", Enabled: false), new("Db--Timeout", "v")], ["w"]);

        Assert.Equal(2, secrets.Secrets.Count);
        Assert.False(secrets.Find("DB--PASSWORD")!.Enabled);
        Assert.Equal(["db--password"], secrets.FindByConfigurationKey("db:password").Select(s => s.Name));
        Assert.Empty(secrets.FindByConfigurationKey("Nope"));
        Assert.Null(secrets.Find("Nope"));
        Assert.Equal(["w"], secrets.Warnings);
    }

    [Fact]
    public void ToString_never_shows_the_value()
    {
        var secret = new AzureKeyVaultSecret("Db--Password", "contoso-prod") { Value = SecretValue };

        Assert.Equal("Azure Key Vault secret Db--Password (contoso-prod)", secret.ToString());
    }

    [Fact]
    public void Detail_describes_disabled_expiry_and_content_type()
    {
        Assert.Null(AzureKeyVaultSecrets.Detail(new AzureKeyVaultSecret("a", "v")));
        Assert.Equal(
            "disabled, expires 2030-01-02, content type application/json",
            AzureKeyVaultSecrets.Detail(new AzureKeyVaultSecret("a", "v", Enabled: false) { ExpiresOn = new DateTimeOffset(2030, 1, 2, 0, 0, 0, TimeSpan.Zero), ContentType = "application/json" }));
    }
}
