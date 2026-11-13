using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace ConfigFerret.Tool.Tests;

public class EnvironmentTests
{
    [Fact]
    public void Maps_variables_to_keys_like_the_real_environment_variables_provider()
    {
        // Compare against the real provider over this process's own environment, with a few tricky names added.
        Environment.SetEnvironmentVariable("CFTOOLTEST_Db__Timeout", "42");
        Environment.SetEnvironmentVariable("SQLCONNSTR_CfToolTest", "Server=sql");
        Environment.SetEnvironmentVariable("CUSTOMCONNSTR_CfToolTestCustom", "custom");
        Environment.SetEnvironmentVariable("MYSQLCONNSTR_CfToolTestMy", "my");
        var process = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            process[(string)entry.Key] = entry.Value as string;
        }

        foreach (var prefix in new[] { string.Empty, "CFTOOLTEST_", "CfToolTest__" })
        {
            var real = new EnvironmentVariablesConfigurationProvider(prefix);
            real.Load();
            var simulated = new SimulatedEnvironmentVariablesProvider(process, prefix);
            simulated.Load();

            Assert.Equal(Keys(real), Keys(simulated));
            foreach (var key in Keys(real))
            {
                real.TryGet(key, out var expected);
                simulated.TryGet(key, out var actual);
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void Described_as_environment_variables_with_their_prefix()
    {
        var configuration = new ConfigurationBuilder()
            .Add(new SimulatedEnvironmentVariablesSource(new Dictionary<string, string?> { ["APP_Db__Timeout"] = "5", ["OTHER"] = "x" }, "APP_"))
            .Build();

        var report = configuration.TraceProvenance(new TraceOptions { Environment = new Dictionary<string, string?> { ["APP_Db__Timeout"] = "5" } });

        var setting = Assert.Single(report.Settings);
        Assert.Equal("Db:Timeout", setting.Key);
        Assert.Equal("environment variable APP_Db__Timeout", setting.EffectiveStep!.Source);
    }

    [Fact]
    public void Parses_env_files()
    {
        var pairs = EnvironmentInput.ParseFile("# comment\n\nA=1\r\nexport B = two words \nC=\"quoted = value\"\nD='single'\nE=\n", "test").ToList();

        Assert.Equal([("A", "1"), ("B", "two words"), ("C", "quoted = value"), ("D", "single"), ("E", "")], pairs);
    }

    [Theory]
    [InlineData("NOVALUE")]
    [InlineData("=value")]
    public void Rejects_env_file_lines_without_a_name(string line)
    {
        var error = Assert.Throws<UsageException>(() => EnvironmentInput.ParseFile(line, "test.properties").ToList());
        Assert.Contains("test.properties line 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Splits_pairs_at_the_first_equals_sign()
    {
        Assert.Equal(("ConnectionStrings__Db", "Host=x;Password=y"), EnvironmentInput.ParsePair("ConnectionStrings__Db=Host=x;Password=y", "--env"));
    }

    private static List<string> Keys(EnvironmentVariablesConfigurationProvider provider) =>
        [.. provider.GetChildKeys([], null).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
}
