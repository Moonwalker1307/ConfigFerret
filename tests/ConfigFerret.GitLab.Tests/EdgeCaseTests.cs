using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.GitLab.Tests;

public sealed class EdgeCaseTests : IDisposable
{
    private readonly ApiHandler _handler = new();
    private readonly HttpClient _client;

    public EdgeCaseTests() => _client = _handler.Client();

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }

    private static ProvenanceStep EnvStep(string name, string? value) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = $"environment variable {name}",
        Value = value,
        Reference = new EnvironmentVariableRef(name),
    };

    private static readonly GitLabVariables Variables = new([new("TIMEOUT", "30", GitLabVariableLevel.Project, "p")]);

    [Theory]
    [InlineData("a*b*c", "aXc", 0)]
    [InlineData("*b*", "bX", 4)]
    [InlineData("*ab*b", "ab", 0)]
    [InlineData("*ab*b", "abb", 6)]
    [InlineData("**", "x", 3)]
    public void Matches_every_part_of_a_wildcard_in_order(string scope, string environment, int expected)
    {
        Assert.Equal(expected, GitLabVariables.ScopeRank(scope, environment));
    }

    [Theory]
    [InlineData("plain # comment", "plain")]
    [InlineData("a#b", "a#b")]
    [InlineData("'it''s' # c", "it's")]
    [InlineData("'open", "open")]
    [InlineData("\"a \\\"b\\\" \\\\ \\n\\t\"", "a \"b\" \\ \n\t")]
    [InlineData("\"open", "open")]
    [InlineData("\"tail\\", "tail\\")]
    [InlineData("|", null)]
    [InlineData(">", null)]
    [InlineData("|-", null)]
    [InlineData(">-", null)]
    [InlineData("|+", null)]
    [InlineData(">+", null)]
    [InlineData(">x", ">x")]
    [InlineData("", null)]
    public void Reads_yaml_scalars(string text, string? expected)
    {
        Assert.Equal(expected, YamlLine.Scalar(text));
    }

    [Fact]
    public void Finds_properties_past_a_name_that_is_not_valid_utf16()
    {
        using var doc = JsonDocument.Parse("""{"\udc00":1,"a":"x"}""");

        Assert.Equal("x", JsonRead.String(JsonRead.Property(doc.RootElement, "a")));
        Assert.Null(JsonRead.Property(doc.RootElement, "b"));
        Assert.Equal(["a"], JsonRead.Properties(doc.RootElement).Select(p => p.Name));
        Assert.Null(GitLabVariables.ReadVariable(JsonElement.Parse("""{"\udc00":1}"""), GitLabVariableLevel.Project, "p", false));
    }

    [Fact]
    public void Reads_the_description()
    {
        using var doc = JsonDocument.Parse("""{"key":"A","value":"1","description":"the timeout"}""");

        Assert.Equal("the timeout", GitLabVariables.ReadVariable(doc.RootElement, GitLabVariableLevel.Project, "p", false)!.Description);
    }

    [Fact]
    public async Task Names_the_project_by_the_path_gitlab_returns()
    {
        _handler
            .Respond("/api/v4/projects/42", """{"id":42,"path_with_namespace":"contoso/api","namespace":{"kind":"user"}}""")
            .Respond("/api/v4/projects/42/variables?per_page=100&page=1", """[{"key":"A","value":"1"}]""");
        var options = GitLabVariablesTests.Options();
        options.Project = "42";

        var gitlab = await GitLabVariables.LoadAsync(options, _client, TestContext.Current.CancellationToken);

        Assert.Equal("contoso/api", Assert.Single(gitlab.Entries).Owner);
    }

    [Fact]
    public void Follows_a_ci_reference_only_when_file_line_and_name_all_match()
    {
        var options = new GitLabTraceOptions().AddCiFileText("variables:\n  A: $TIMEOUT\n", "ci.yml");
        var resolver = new GitLabResolver(Variables, options);

        Assert.Single(resolver.Resolve(Ref("ci.yml", 2, "A"), new TraceDiagnostics()));
        Assert.Empty(resolver.Resolve(Ref("other.yml", 2, "A"), new TraceDiagnostics()));
        Assert.Empty(resolver.Resolve(Ref("ci.yml", 3, "A"), new TraceDiagnostics()));
        Assert.Empty(resolver.Resolve(Ref("ci.yml", 2, "B"), new TraceDiagnostics()));

        static ProvenanceStep Ref(string file, int line, string name) =>
            new() { Kind = LayerKind.GitLabCi, Source = "g", Reference = new GitLabCiVariableRef(file, line, name) };
    }

    [Fact]
    public void Notes_a_ci_file_variable_step()
    {
        var options = new GitLabTraceOptions().AddCiFileText("variables:\n  TIMEOUT: 20\n", "ci.yml");

        var chain = new GitLabResolver(Variables, options).Resolve(EnvStep("TIMEOUT", "30"), new TraceDiagnostics());

        Assert.Equal("matched by name; the GitLab value differs from the value seen here, so the deployment may be out of date or the value was transformed", chain[0].Note);
        Assert.Equal("matched by name", chain[1].Note);
    }

    [Fact]
    public void Does_not_read_a_configured_ci_file_twice()
    {
        var path = Path.Join(Path.GetTempPath(), $"cf-gitlab-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, "variables:\n  A: 1\n");
        try
        {
            var options = new GitLabTraceOptions();
            options.CiFiles.Add(path);

            _ = new GitLabResolver(Variables, options);
            _ = new GitLabResolver(Variables, options);

            Assert.Single(options.Variables);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Applies_the_configure_callback()
    {
        var gitlab = new GitLabVariables([new("A", "1", GitLabVariableLevel.Project, "p", "production")]);
        const string prefix = "CFGLCFG_";
        Environment.SetEnvironmentVariable(prefix + "A", "1");
        try
        {
            var report = ConfigTracer.For(new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build())
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "A"] = "1" })
                .WithGitLab(gitlab, o => o.Map(prefix + "A", "A").Environment = "production")
                .Trace();

            Assert.Single(report.Find("A")!.Steps[^1].Origins);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "A", null);
        }
    }

    [Fact]
    public void Describes_every_provider_setting()
    {
        var settings = new GitLabSourceProvider().Settings;

        Assert.All(settings, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Label));
            Assert.False(string.IsNullOrWhiteSpace(s.Help));
        });
        Assert.Equal(["BaseAddress", "Project", "Token"], settings.Where(s => s.Required).Select(s => s.Name));
        Assert.Equal(
            ["https://gitlab.com/", null, null, null, "true", "false", "false", null, "true", "false"],
            settings.Select(s => s.DefaultValue));
    }

    [Fact]
    public void Reads_a_quoted_script_line_that_is_empty()
    {
        var options = new GitLabTraceOptions().AddCiFileText("job:\n  script:\n    - ''\n    - \"\"\n", "ci.yml");

        Assert.Empty(options.Mappings);
    }
}
