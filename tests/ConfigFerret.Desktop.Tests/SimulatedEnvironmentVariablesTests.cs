using ConfigFerret.Desktop.Services;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Desktop.Tests;

public class SimulatedEnvironmentVariablesTests
{
    private static IConfigurationRoot Build(string prefix, params (string Name, string? Value)[] variables)
    {
        var dictionary = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return new ConfigurationBuilder().Add(new SimulatedEnvironmentVariablesSource(dictionary, prefix)).Build();
    }

    [Fact]
    public void Turns_double_underscores_into_sections()
    {
        var configuration = Build(string.Empty, ("Db__Timeout", "30"), ("Plain", "x"));

        Assert.Equal("30", configuration["Db:Timeout"]);
        Assert.Equal("x", configuration["Plain"]);
    }

    [Fact]
    public void Keeps_only_variables_with_the_prefix_and_removes_it()
    {
        var configuration = Build("ORDERS_", ("ORDERS_Db__Timeout", "30"), ("OTHER_Db__Timeout", "99"), ("PATH", "/bin"));

        Assert.Equal("30", configuration["Db:Timeout"]);
        Assert.Null(configuration["OTHER_Db:Timeout"]);
        Assert.Single(configuration.AsEnumerable(), p => p.Value is not null);
    }

    [Fact]
    public void A_prefix_with_a_double_underscore_is_matched_as_a_section()
    {
        var configuration = Build("App__", ("App__Db__Timeout", "30"), ("Db__Timeout", "1"));

        Assert.Equal("30", configuration["Db:Timeout"]);
    }

    [Fact]
    public void Matches_names_and_prefixes_without_regard_to_case()
    {
        var configuration = Build("orders_", ("ORDERS_DB__TIMEOUT", "30"));

        Assert.Equal("30", configuration["db:timeout"]);
    }

    [Theory]
    [InlineData("MYSQLCONNSTR_Main", "MySql.Data.MySqlClient")]
    [InlineData("SQLAZURECONNSTR_Main", "System.Data.SqlClient")]
    [InlineData("SQLCONNSTR_Main", "System.Data.SqlClient")]
    [InlineData("POSTGRESQLCONNSTR_Main", "Npgsql")]
    public void Connection_string_variables_become_connection_strings_with_a_provider(string name, string provider)
    {
        var configuration = Build(string.Empty, (name, "Server=x"));

        Assert.Equal("Server=x", configuration["ConnectionStrings:Main"]);
        Assert.Equal(provider, configuration["ConnectionStrings:Main_ProviderName"]);
        Assert.Null(configuration[name]);
    }

    [Fact]
    public void A_custom_connection_string_has_no_provider()
    {
        var configuration = Build(string.Empty, ("CUSTOMCONNSTR_Main", "Server=x"));

        Assert.Equal("Server=x", configuration["ConnectionStrings:Main"]);
        Assert.Null(configuration["ConnectionStrings:Main_ProviderName"]);
    }

    [Fact]
    public void Connection_string_names_may_hold_sections_and_any_case()
    {
        var configuration = Build(string.Empty, ("sqlconnstr_Db__Main", "Server=x"));

        Assert.Equal("Server=x", configuration["ConnectionStrings:Db:Main"]);
    }

    [Fact]
    public void A_prefix_applies_to_connection_strings_too()
    {
        var configuration = Build("ConnectionStrings__", ("SQLCONNSTR_Main", "Server=x"));

        Assert.Equal("Server=x", configuration["Main"]);
    }

    [Fact]
    public void Never_reads_the_real_environment()
    {
        var configuration = Build(string.Empty, ("A", "1"));

        Assert.Null(configuration["PATH"]);
        Assert.Null(configuration["HOME"]);
    }

    [Fact]
    public void Keeps_a_variable_without_a_value()
    {
        var configuration = Build(string.Empty, ("A", null));

        Assert.Null(configuration["A"]);
        Assert.Contains(configuration.AsEnumerable(), p => p.Key == "A");
    }
}
