using System.Text;
using ConfigFerret.DotNet;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tests;

public class JsonLineLocatorTests
{
    [Fact]
    public void Finds_lines_of_nested_values_and_array_items()
    {
        var json = """
            {
              // comment
              "Db": {
                "Timeout": 30,
                "Hosts": [ "a",
                  "b" ]
              },
              "Top": true,
            }
            """;

        var lines = JsonLineLocator.Locate(Encoding.UTF8.GetBytes(json));

        Assert.Equal(4, lines["Db:Timeout"]);
        Assert.Equal(5, lines["Db:Hosts:0"]);
        Assert.Equal(6, lines["db:hosts:1"]);
        Assert.Equal(8, lines["Top"]);
    }

    [Fact]
    public void Locate_finds_empty_arrays_and_objects()
    {
        // Found by fuzzing: .NET configuration keeps "Tags": [] as the key Tags with an empty value.
        var json = """
            {
              "Tags": [],
              "Db": {
                "Options": {},
                "Hosts": [[], ["a"]]
              }
            }
            """;

        var lines = JsonLineLocator.Locate(Encoding.UTF8.GetBytes(json));

        Assert.Equal(2, lines["Tags"]);
        Assert.Equal(4, lines["Db:Options"]);
        Assert.Equal(5, lines["Db:Hosts:0"]);
        Assert.Equal(5, lines["Db:Hosts:1:0"]);
        Assert.False(lines.ContainsKey("Db"));
    }

    [Fact]
    public void Locate_reads_invalid_utf8_property_names_like_the_json_provider()
    {
        // Found by fuzzing: this used to throw InvalidOperationException out of the tracer,
        // although the .NET JSON provider loads the same file.
        byte[] json = [.. "{\n  \"Db\": {\n    \"T"u8, 0xD4, .. "t\": 60\n  }\n}\n"u8];
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(json)).Build();
        var key = Assert.Single(config.AsEnumerable(), p => p.Value is not null).Key;

        var lines = JsonLineLocator.Locate(json);

        Assert.Equal(3, lines[key]);
    }

    [Fact]
    public void Locate_skips_a_byte_order_mark()
    {
        // Found by fuzzing: a BOM made every appsettings file lose its line numbers.
        byte[] json = [0xEF, 0xBB, 0xBF, .. "{\n  \"Db\": {\n    \"Timeout\": 60\n  }\n}\n"u8];

        var lines = JsonLineLocator.Locate(json);

        Assert.Equal(3, lines["Db:Timeout"]);
    }
}
