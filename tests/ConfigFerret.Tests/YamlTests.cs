using ConfigFerret.Internal;

namespace ConfigFerret.Tests;

public class YamlTests
{
    private static YamlDotNet.RepresentationModel.YamlNode Doc(string text) => Assert.Single(Yaml.LoadDocuments(text));

    [Theory]
    [InlineData("v:\n", true)]
    [InlineData("v: ~\n", true)]
    [InlineData("v: null\n", true)]
    [InlineData("v: Null\n", true)]
    [InlineData("v: NULL\n", true)]
    [InlineData("v: \"null\"\n", false)]
    [InlineData("v: 'null'\n", false)]
    [InlineData("v: \"\"\n", false)]
    [InlineData("v: nil\n", false)]
    [InlineData("v: 0\n", false)]
    public void Treats_only_unquoted_null_forms_as_null(string text, bool isNull)
    {
        var node = Doc(text).Get("v");

        Assert.Equal(isNull, node.IsNull());
        Assert.Equal(isNull, node.Scalar() is null);
    }

    [Fact]
    public void Missing_and_non_scalar_nodes_have_no_scalar_value()
    {
        var doc = Doc("m: { a: 1 }\ns: [1]\n");

        Assert.True(doc.Get("missing").IsNull());
        Assert.Null(doc.Get("m").Scalar());
        Assert.Null(doc.Get("s").Scalar());
        Assert.Null(doc.Get("s").Get("a"));
        Assert.Equal("1", doc.Get("m").Str("a"));
    }

    [Fact]
    public void Pairs_lists_scalar_keys_of_a_mapping_only()
    {
        var doc = Doc("a: 1\n? [x]\n: 2\nb: 3\n");

        Assert.Equal(["a", "b"], doc.Pairs().Select(p => p.Key));
        Assert.Empty(Doc("[1, 2]").Pairs());
        Assert.Empty(((YamlDotNet.RepresentationModel.YamlNode?)null).Pairs());
    }

    [Fact]
    public void Items_lists_sequence_children_only()
    {
        Assert.Equal(2, Doc("[1, 2]").Items().Count());
        Assert.Empty(Doc("a: 1").Items());
    }

    [Fact]
    public void Renders_nodes_in_flow_style()
    {
        var doc = Doc("a: [1, ~, x]\nb: { k: v, n: null }\nc: plain\nd: ~\n");

        Assert.Equal("[1, null, x]", Yaml.Render(doc.Get("a")));
        Assert.Equal("{k: v, n: null}", Yaml.Render(doc.Get("b")));
        Assert.Equal("plain", Yaml.Render(doc.Get("c")));
        Assert.Null(Yaml.Render(doc.Get("d")));
        Assert.Null(Yaml.Render(null));
    }

    [Fact]
    public void Location_joins_file_and_line()
    {
        var doc = Doc("a: 1\nb: 2\n");

        Assert.Equal("f.yaml:2", Yaml.Location("f.yaml", doc.Get("b")!));
    }

    [Fact]
    public void Rejects_aliases_that_expand_to_a_huge_document()
    {
        // 330 bytes that expand to 10^7 scalars if every alias is copied out.
        var text = "a0: &a0 [x,x,x,x,x,x,x,x,x,x]\n"
            + string.Concat(Enumerable.Range(1, 7).Select(i => $"a{i}: &a{i} [{string.Join(",", Enumerable.Repeat($"*a{i - 1}", 10))}]\n"));

        var ex = Assert.Throws<YamlDotNet.Core.YamlException>(() => Yaml.LoadDocuments(text));
        Assert.Contains("expand to more than", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_ordinary_anchor_reuse()
    {
        var doc = Doc("base: &base { image: api, replicas: 2 }\nprod: *base\nstaging: *base\n");

        Assert.Equal("{image: api, replicas: 2}", Yaml.Render(doc.Get("staging")));
    }
}
