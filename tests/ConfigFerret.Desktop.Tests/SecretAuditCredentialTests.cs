using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.Tests;

public class SecretAuditCredentialTests
{
    [Theory]
    [InlineData("ghp" + "_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("github" + "_pat_11ABCDEFG0abcdefghijkl_mnopqrstuvwxyz")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("Server=db.example.test;Database=orders;User Id=sa;Password=Sup3rS3cret!")]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=x;AccountKey=abc123==")]
    [InlineData("Zx81kQp4LmT7vRw2Nc9YbH3s")]
    public void Recognizes_a_credential(string value)
    {
        Assert.True(SecretAudit.LooksLikeCredential(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://api.example.test/v1/orders")]
    [InlineData("localhost")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("1234567890123456789012345")]
    [InlineData("Information")]
    [InlineData("the quick brown fox jumps over the lazy dog")]
    [InlineData("the-quick-brown-fox-jumps-over-the-lazy-dog")]
    [InlineData("Server=db;Database=orders;Integrated Security=true")]
    [InlineData("Server=db;Password=#{DbPassword}")]
    [InlineData("#{ApiKey}")]
    [InlineData("${API_KEY}")]
    [InlineData("***")]
    public void Does_not_take_an_ordinary_value_for_a_credential(string? value)
    {
        Assert.False(SecretAudit.LooksLikeCredential(value));
    }

    [Fact]
    public void Does_not_take_a_very_long_mixed_string_for_a_credential()
    {
        Assert.False(SecretAudit.LooksLikeCredential(string.Concat(Enumerable.Repeat("aB3dE6gH9jK", 60))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#{Var}")]
    [InlineData("#{Db.Password}")]
    [InlineData("${VAR}")]
    [InlineData("${{ secrets.X }}")]
    [InlineData("{{ .Values.x }}")]
    [InlineData("__TOKEN__")]
    [InlineData("%DB_PASSWORD%")]
    [InlineData("$(DB_PASSWORD)")]
    [InlineData("<secret>")]
    [InlineData("***")]
    [InlineData("  ${VAR}  ")]
    public void Recognizes_a_placeholder(string? value)
    {
        Assert.True(SecretAudit.IsPlaceholder(value));
    }

    [Theory]
    [InlineData("Zx81kQp4LmT7vRw2Nc9YbH3s")]
    [InlineData("hunter2")]
    [InlineData("prefix-${VAR}")]
    [InlineData("https://example.test")]
    public void Does_not_take_a_real_value_for_a_placeholder(string value)
    {
        Assert.False(SecretAudit.IsPlaceholder(value));
    }
}
