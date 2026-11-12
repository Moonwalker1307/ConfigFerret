using System.Text;
using ConfigFerret.DotNet;

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
}
