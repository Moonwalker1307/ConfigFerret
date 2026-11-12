using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tests;

public class DotNetLayerTests
{
    [Fact]
    public void Traces_json_files_and_environment_variables_in_priority_order()
    {
        Environment.SetEnvironmentVariable("CFDN1_Db__Timeout", "42");
        var config = new ConfigurationBuilder()
            .AddJsonFile(TestPaths.Data("app", "appsettings.json"))
            .AddJsonFile(TestPaths.Data("app", "appsettings.Production.json"))
            .AddEnvironmentVariables("CFDN1_")
            .Build();

        var setting = config.TraceProvenance().Find("Db:Timeout");

        Assert.NotNull(setting);
        Assert.Equal("42", setting.Value);
        Assert.Collection(setting.Steps,
            s =>
            {
                Assert.Equal(TestPaths.Data("app", "appsettings.json"), ((FileRef)s.Reference!).Path);
                Assert.Equal("line 4", s.Location);
                Assert.Equal("30", s.Value);
                Assert.False(s.IsEffective);
            },
            s =>
            {
                Assert.Equal("line 3", s.Location);
                Assert.Equal("60", s.Value);
            },
            s =>
            {
                Assert.Equal("environment variable CFDN1_Db__Timeout", s.Source);
                Assert.Equal(new EnvironmentVariableRef("CFDN1_Db__Timeout"), s.Reference);
                Assert.True(s.IsEffective);
            });
        Assert.StartsWith("set in ", setting.Story, StringComparison.Ordinal);
        Assert.Contains("overridden again by environment variable CFDN1_Db__Timeout", setting.Story, StringComparison.Ordinal);
    }

    [Fact]
    public void Finds_connection_string_environment_variables()
    {
        var env = new Dictionary<string, string?> { ["SQLCONNSTR_Main"] = "Server=x" };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Main"] = "Server=x" })
            .Build();
        var describer = new DotNet.ProviderDescriber(env, new TraceDiagnostics());

        Assert.Equal("SQLCONNSTR_Main", describer.FindEnvironmentVariable("", "ConnectionStrings:Main", "Server=x"));
        Assert.NotNull(config["ConnectionStrings:Main"]);
    }

    [Fact]
    public void Describes_command_line_and_memory_layers()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Port"] = "80" })
            .AddCommandLine(["--Port=8080"])
            .Build();

        var setting = config.TraceProvenance().Find("Port")!;

        Assert.Equal("in-memory collection", setting.Steps[0].Source);
        Assert.Equal("command-line argument --Port", setting.Steps[1].Source);
        Assert.Equal("8080", setting.Value);
    }

    [Fact]
    public void Redacts_sensitive_keys_unless_revealed()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(TestPaths.Data("app", "appsettings.json"))
            .Build();

        var hidden = config.TraceProvenance().Find("Db:Password")!;
        var shown = config.TraceProvenance(new TraceOptions { RevealSensitiveValues = true }).Find("Db:Password")!;

        Assert.Equal("***", hidden.Value);
        Assert.Equal("***", hidden.Steps[0].Value);
        Assert.True(hidden.IsSensitive);
        Assert.Equal("dev-password", shown.Value);
    }

    [Fact]
    public void Explain_says_when_a_key_is_not_set()
    {
        var config = new ConfigurationBuilder().Build();

        Assert.Equal("Nope:Key is not set by any configuration provider.", config.Explain("Nope:Key"));
    }

    [Fact]
    public void Find_accepts_double_underscore_separator()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(TestPaths.Data("app", "appsettings.json"))
            .Build();

        Assert.NotNull(config.TraceProvenance().Find("logging__level"));
    }
}
