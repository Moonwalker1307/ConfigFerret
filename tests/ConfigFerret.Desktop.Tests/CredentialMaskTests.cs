using ConfigFerret.Desktop.Services;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Desktop.Tests;

public class CredentialMaskTests
{
    private static CredentialMask MaskOf(params string[] values)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select((v, i) => new KeyValuePair<string, string?>($"Value{i}", v)))
            .Build();
        return CredentialMask.From(ConfigTracer.For(config).WithOptions(o => o.RevealSensitiveValues = true).Trace(), "***")!;
    }

    [Fact]
    public void A_text_that_holds_two_overlapping_credentials_keeps_neither()
    {
        var mask = MaskOf("pwd=fake-alpha;beta", "beta;pwd=fake-gamma");

        var scrubbed = mask.Scrub("Seen: pwd=fake-alpha;beta;pwd=fake-gamma here.");

        Assert.DoesNotContain("fake-alpha", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-gamma", scrubbed, StringComparison.Ordinal);
        Assert.Equal(CredentialMask.HiddenText, scrubbed);
    }

    [Fact]
    public void A_text_without_a_credential_is_kept()
    {
        var mask = MaskOf("pwd=fake-alpha");

        Assert.Equal("Nothing secret here.", mask.Scrub("Nothing secret here."));
    }
}
