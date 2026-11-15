using System.Text.Json;

namespace ConfigFerret.AzureDevOps.Tests;

public class EdgeCaseTests
{
    private static ProvenanceStep EnvStep(string name, string? value) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = $"environment variable {name}",
        Value = value,
        Reference = new EnvironmentVariableRef(name),
    };

    private static readonly AzureDevOpsVariableGroups Groups = new("contoso/api",
    [
        new(3, "shared", AzureDevOpsGroupType.Vsts, null, null, null, [new("Db.Timeout", "10", false, "shared", 3)]),
    ]);

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("  plain # comment", "plain")]
    [InlineData("a#b", "a#b")]
    [InlineData("'single ''quoted'' # not a comment' # comment", "single 'quoted' # not a comment")]
    [InlineData("'unterminated", "unterminated")]
    [InlineData("''", "")]
    [InlineData("\"double \\\"quoted\\\" \\\\ \\n\\t\" # comment", "double \"quoted\" \\ \n\t")]
    [InlineData("\"unterminated", "unterminated")]
    [InlineData("\"ends in a backslash\\", "ends in a backslash\\")]
    [InlineData("|", null)]
    [InlineData(">", null)]
    [InlineData("|-", null)]
    [InlineData(">-", null)]
    [InlineData("|+", null)]
    [InlineData(">+", null)]
    [InlineData("|x", "|x")]
    [InlineData("   ", null)]
    public void Reads_yaml_scalars(string text, string? expected)
    {
        Assert.Equal(expected, YamlLine.Scalar(text));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("  # comment", true)]
    [InlineData("  a: 1", false)]
    public void Spots_blank_lines(string line, bool blank)
    {
        Assert.Equal(blank, YamlLine.IsBlank(line));
    }

    [Fact]
    public void Finds_properties_past_a_name_that_is_not_valid_utf16()
    {
        using var doc = JsonDocument.Parse("""{"\udc00":1,"a":2}""");

        Assert.Equal(2, JsonRead.Number(JsonRead.Property(doc.RootElement, "a")));
        Assert.Null(JsonRead.Property(doc.RootElement, "b"));
        Assert.Equal(["a"], JsonRead.Properties(doc.RootElement).Select(p => p.Name));
        Assert.Null(JsonRead.Property(doc.RootElement.GetProperty("a"), "a"));
        Assert.Empty(JsonRead.Properties(doc.RootElement.GetProperty("a")));
    }

    [Fact]
    public void Reads_a_group_with_an_empty_type_and_a_blank_vault()
    {
        using var doc = JsonDocument.Parse("""
            [{"id":1,"name":"a","type":""},
             {"id":2,"name":"b","type":"azurekeyvault","providerData":{"vault":"  "}},
             {"id":3,"name":"c","type":"VSTS"},
             {"\udc00":1,"id":4}]
            """);
        var items = doc.RootElement.EnumerateArray().ToList();

        Assert.Equal(AzureDevOpsGroupType.Vsts, AzureDevOpsVariableGroups.ReadGroup(items[0])!.Type);
        var vault = AzureDevOpsVariableGroups.ReadGroup(items[1])!;
        Assert.Equal(AzureDevOpsGroupType.AzureKeyVault, vault.Type);
        Assert.Null(vault.KeyVaultName);
        Assert.Equal(AzureDevOpsGroupType.Vsts, AzureDevOpsVariableGroups.ReadGroup(items[2])!.Type);
        Assert.Null(AzureDevOpsVariableGroups.ReadGroup(items[3]));
    }

    [Fact]
    public void Matches_an_explicit_link_by_the_exact_variable_name_only()
    {
        var options = new AzureDevOpsTraceOptions().Map("X", "DB_TIMEOUT");

        Assert.Empty(new AzureDevOpsResolver(Groups, options).Resolve(EnvStep("X", "1"), new TraceDiagnostics()));
    }

    [Fact]
    public void Leaves_pipeline_variables_out_of_a_link_to_one_group()
    {
        var options = new AzureDevOpsTraceOptions()
            .AddPipelineText("variables:\n- name: Db.Timeout\n  value: 20\n", "p.yml")
            .Map("X", "Db.Timeout", "shared");

        var step = Assert.Single(new AzureDevOpsResolver(Groups, options).Resolve(EnvStep("X", "10"), new TraceDiagnostics()));

        Assert.Equal("10", step.Value);
    }

    [Fact]
    public void Treats_a_link_with_a_leading_slash_as_any_group()
    {
        var options = new AzureDevOpsTraceOptions().AddPipelineText("variables:\n- name: Db.Timeout\n  value: 20\n", "p.yml");
        options.Links["X"] = "/Db.Timeout";

        var chain = new AzureDevOpsResolver(Groups, options).Resolve(EnvStep("X", "20"), new TraceDiagnostics());

        Assert.Equal(["10", "20"], chain.Select(s => s.Value));
        Assert.Equal("linked explicitly", chain[1].Note);
        Assert.Equal("linked explicitly; the Azure DevOps value differs from the value seen here, so the deployment may be out of date or the value was transformed", chain[0].Note);
    }

    [Fact]
    public void Follows_a_pipeline_reference_only_when_file_line_and_name_all_match()
    {
        var options = new AzureDevOpsTraceOptions().AddPipelineText("variables:\n- name: A\n  value: $(Db.Timeout)\n", "p.yml");
        var resolver = new AzureDevOpsResolver(Groups, options);

        Assert.Single(resolver.Resolve(Ref("p.yml", 2, "A"), new TraceDiagnostics()));
        Assert.Empty(resolver.Resolve(Ref("q.yml", 2, "A"), new TraceDiagnostics()));
        Assert.Empty(resolver.Resolve(Ref("p.yml", 3, "A"), new TraceDiagnostics()));
        Assert.Empty(resolver.Resolve(Ref("p.yml", 2, "B"), new TraceDiagnostics()));

        static ProvenanceStep Ref(string file, int line, string name) =>
            new() { Kind = LayerKind.AzureDevOps, Source = "a", Reference = new AzurePipelinesVariableRef(file, line, name) };
    }

    [Fact]
    public void Skips_a_list_item_without_a_name_or_group()
    {
        var options = new AzureDevOpsTraceOptions().AddPipelineText("variables:\n- value: orphan\n- readonly: true\n", "p.yml");

        Assert.Empty(options.Variables);
        Assert.Empty(options.GroupLinks);
    }

    [Fact]
    public void Does_not_read_a_configured_pipeline_twice()
    {
        var path = Path.Join(Path.GetTempPath(), $"cf-ado-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, "variables:\n- name: A\n  value: 1\n");
        try
        {
            var options = new AzureDevOpsTraceOptions();
            options.Pipelines.Add(path);

            _ = new AzureDevOpsResolver(Groups, options);
            _ = new AzureDevOpsResolver(Groups, options);

            Assert.Single(options.Variables);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Describes_every_provider_setting()
    {
        Assert.All(new AzureDevOpsSourceProvider().Settings, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Label));
            Assert.False(string.IsNullOrWhiteSpace(s.Help));
        });
        Assert.Equal("true", new AzureDevOpsSourceProvider().Settings.Single(s => s.Name == "MatchByName").DefaultValue);
        Assert.Equal("false", new AzureDevOpsSourceProvider().Settings.Single(s => s.Name == "AllowUnencryptedHttp").DefaultValue);
    }

    [Fact]
    public void Notes_a_pipeline_variable_step()
    {
        var options = new AzureDevOpsTraceOptions().AddPipelineText("variables:\n  Db.Timeout: 20\n", "p.yml");

        var chain = new AzureDevOpsResolver(Groups, options).Resolve(EnvStep("DB_TIMEOUT", "20"), new TraceDiagnostics());

        Assert.Equal("matched by name: scripts see Db.Timeout as DB_TIMEOUT", chain[^1].Note);
        Assert.Equal("20", chain[^1].Value);
    }
}
