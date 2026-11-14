using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.Tests;

public class DotEnvTests
{
    [Fact]
    public void Reads_name_value_pairs()
    {
        var pairs = DotEnv.Parse("A=1\nB=two words\r\nC=\n", "test").ToList();

        Assert.Equal([("A", "1"), ("B", "two words"), ("C", string.Empty)], pairs);
    }

    [Fact]
    public void Skips_blank_lines_and_comments()
    {
        var pairs = DotEnv.Parse("# a comment\n\n   \n  # indented comment\nA=1\n", "test").ToList();

        Assert.Equal([("A", "1")], pairs);
    }

    [Fact]
    public void Removes_matching_quotes_only()
    {
        var pairs = DotEnv.Parse("A=\"double\"\nB='single'\nC=\"mixed'\nD=\"\nE=it's\n", "test").ToList();

        Assert.Equal([("A", "double"), ("B", "single"), ("C", "\"mixed'"), ("D", "\""), ("E", "it's")], pairs);
    }

    [Fact]
    public void Keeps_spaces_inside_quotes()
    {
        Assert.Equal([("A", "  padded  ")], DotEnv.Parse("A=\"  padded  \"", "test"));
    }

    [Fact]
    public void Accepts_an_export_prefix()
    {
        var pairs = DotEnv.Parse("export A=1\nexport   B=\"2\"\n", "test").ToList();

        Assert.Equal([("A", "1"), ("B", "2")], pairs);
    }

    [Fact]
    public void Splits_at_the_first_equals_sign()
    {
        Assert.Equal([("CONN", "Server=db;Password=x=y")], DotEnv.Parse("CONN=Server=db;Password=x=y", "test"));
    }

    [Fact]
    public void Trims_around_the_name_and_value()
    {
        Assert.Equal([("A", "1")], DotEnv.Parse("  A = 1  ", "test"));
    }

    [Fact]
    public void Reads_nothing_from_empty_text()
    {
        Assert.Empty(DotEnv.Parse(null, "test"));
        Assert.Empty(DotEnv.Parse(string.Empty, "test"));
    }

    [Theory]
    [InlineData("A=1\nnot a variable", 2)]
    [InlineData("=value", 1)]
    [InlineData("# ok\n\nexport ", 3)]
    public void Throws_a_format_exception_that_names_the_source_and_line(string text, int line)
    {
        var ex = Assert.Throws<FormatException>(() => DotEnv.Parse(text, "prod.env").ToList());

        Assert.Contains($"prod.env line {line}", ex.Message, StringComparison.Ordinal);
    }
}
