using ConfigFerret.DotNet;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tests;

public sealed class ProviderDescriberTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cf-describer-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ProviderDescriber Describer(Dictionary<string, string?>? env = null) =>
        new(env ?? [], new TraceDiagnostics());

    private static IConfigurationProvider Single(IConfigurationBuilder builder) =>
        Assert.Single(builder.Build().Providers);

    [Fact]
    public void Prefers_the_environment_variable_whose_value_matches()
    {
        var describer = Describer(new() { ["X_Db__T"] = "1", ["X_DB__T"] = "2", ["Y_Db__T"] = "2", ["X_Db__Other"] = "2" });

        Assert.Equal("X_DB__T", describer.FindEnvironmentVariable("X_", "Db:T", "2"));
        Assert.Equal("X_Db__T", describer.FindEnvironmentVariable("X_", "Db:T", "3"));
        Assert.Null(describer.FindEnvironmentVariable("Z_", "Db:T", "2"));
    }

    [Fact]
    public void Names_the_environment_variable_as_it_is_spelled()
    {
        var provider = Single(new ConfigurationBuilder().AddEnvironmentVariables("CFSPELL_"));

        var step = Describer(new() { ["cfspell_db__HOST"] = "h" }).Describe(provider, "Db:Host", "h");

        Assert.Equal("environment variable cfspell_db__HOST", step.Source);
    }

    [Fact]
    public void Warns_once_and_leaves_out_lines_when_a_json_file_cannot_be_parsed()
    {
        var path = Path.Join(_dir, "broken.json");
        File.WriteAllText(path, "{ \"A\": ");
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(JsonLineLocator.TryLocate(path, diagnostics));
        Assert.Empty(JsonLineLocator.TryLocate(Path.Join(_dir, "missing.json"), diagnostics));
        Assert.StartsWith($"Could not read line numbers from {path}: ", Assert.Single(diagnostics.Warnings), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MYSQLCONNSTR_")]
    [InlineData("SQLAZURECONNSTR_")]
    [InlineData("SQLCONNSTR_")]
    [InlineData("CUSTOMCONNSTR_")]
    [InlineData("POSTGRESQLCONNSTR_")]
    public void Maps_every_connection_string_prefix(string csPrefix)
    {
        var name = "APP_" + csPrefix + "Main";
        var describer = Describer(new() { [name] = "Server=x" });

        Assert.Equal(name, describer.FindEnvironmentVariable("APP_", "ConnectionStrings:Main", "Server=x"));
    }

    [Fact]
    public void Builds_the_variable_name_from_prefix_and_key_when_none_is_seen()
    {
        var provider = Single(new ConfigurationBuilder().AddEnvironmentVariables("CFNOPE_"));

        var step = Describer().Describe(provider, "A:B", "v");

        Assert.Equal("environment variable CFNOPE_A__B", step.Source);
        Assert.Equal(new EnvironmentVariableRef("CFNOPE_A__B"), step.Reference);
        Assert.Equal("v", step.Value);
    }

    [Fact]
    public void Reads_the_environment_provider_prefix()
    {
        var provider = (Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider)
            Single(new ConfigurationBuilder().AddEnvironmentVariables("MY_PREFIX_"));

        Assert.Equal("MY_PREFIX_", ProviderDescriber.ReadPrefix(provider));
    }

    [Theory]
    [InlineData(true, "optional file")]
    [InlineData(false, null)]
    public void Notes_optional_files(bool optional, string? note)
    {
        var path = Path.Join(_dir, "settings.json");
        File.WriteAllText(path, """{ "A": "1" }""");
        var provider = Single(new ConfigurationBuilder().AddJsonFile(path, optional));

        var step = Describer().Describe(provider, "A", "1");

        Assert.Equal(note, step.Note);
        Assert.False(step.IsSensitive);
        Assert.Equal("line 1", step.Location);
        Assert.Equal(new FileRef(path), step.Reference);
    }

    [Fact]
    public void Marks_user_secrets_as_sensitive()
    {
        var dir = Directory.CreateDirectory(Path.Join(_dir, "usersecrets", "abc")).FullName;
        var path = Path.Join(dir, "secrets.json");
        File.WriteAllText(path, """{ "Pw": "x" }""");
        var provider = Single(new ConfigurationBuilder().AddJsonFile(path));

        var step = Describer().Describe(provider, "Pw", "x");

        Assert.Equal("user secrets (secrets.json)", step.Source);
        Assert.True(step.IsSensitive);
    }

    [Fact]
    public void Names_the_command_line_argument_in_either_form()
    {
        var spaced = Single(new ConfigurationBuilder().AddCommandLine(["--Port", "8080"]));
        var nested = Single(new ConfigurationBuilder().AddCommandLine(["/Db__Timeout=5"]));

        var spacedStep = Describer().Describe(spaced, "Port", "8080");
        var nestedStep = Describer().Describe(nested, "Db:Timeout", "5");

        Assert.Equal("command-line argument --Port", spacedStep.Source);
        Assert.Null(spacedStep.Note);
        Assert.Equal("command-line argument /Db__Timeout", nestedStep.Source);
    }

    [Fact]
    public void Says_a_switch_mapping_may_have_set_a_command_line_value()
    {
        var provider = Single(new ConfigurationBuilder().AddCommandLine(["-p", "8080"], new Dictionary<string, string> { ["-p"] = "Port" }));

        var step = Describer().Describe(provider, "Port", "8080");

        Assert.Equal("command line", step.Source);
        Assert.Equal("possibly through a switch mapping", step.Note);
    }

    [Fact]
    public void Describes_key_per_file_secrets()
    {
        File.WriteAllText(Path.Join(_dir, "Db__Password"), "pw");
        var provider = Single(new ConfigurationBuilder().AddKeyPerFile(_dir, optional: false));

        var step = Describer().Describe(provider, "Db:Password", "pw");
        var physical = Path.Join(_dir, "Db__Password");

        Assert.Equal($"key-per-file {physical}", step.Source);
        Assert.True(step.IsSensitive);
        Assert.Equal(new FileRef(physical), step.Reference);
    }

    [Fact]
    public void Uses_the_provider_name_for_unknown_providers()
    {
        var step = Describer().Describe(new NamedProvider(), "K", "v");

        Assert.Equal("my provider", step.Source);
        Assert.Equal("v", step.Value);
    }

    [Fact]
    public void Flattens_chained_configurations()
    {
        var inner = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["A"] = "1" })
            .AddCommandLine(["--A=2"])
            .Build();
        var outer = new ConfigurationBuilder().AddConfiguration(inner).Build();

        var flat = ProviderDescriber.Flatten(outer.Providers).ToList();

        Assert.Equal(inner.Providers, flat);
    }

    private sealed class NamedProvider : ConfigurationProvider
    {
        public override string ToString() => "my provider";
    }
}
