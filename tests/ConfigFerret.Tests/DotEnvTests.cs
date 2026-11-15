using ConfigFerret.Compose;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tests;

/// <summary>A folder under the temp directory that is deleted when disposed.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Join(System.IO.Path.GetTempPath(), "configferret-tests-" + Guid.NewGuid().ToString("N"));

    public string Write(string relative, string text)
    {
        var full = System.IO.Path.Join(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp folder does no harm.
        }
    }
}

public class DotEnvFileTests
{
    private static DotEnvFile Parse(string text, Dictionary<string, string?>? variables = null) => DotEnvFile.Parse(text, ".env", variables);

    private static string Value(string text, string key = "A", Dictionary<string, string?>? variables = null) => Parse(text, variables).Variables[key];

    [Theory]
    [InlineData("A=1", "1")]
    [InlineData("A = 1", "1")]
    [InlineData("A=  spaced out  ", "spaced out")]
    [InlineData("A=", "")]
    [InlineData("A=\t", "")]
    [InlineData("export A=1", "1")]
    [InlineData("export\tA=1", "1")]
    [InlineData("  export   A=1", "1")]
    [InlineData("A=x=y", "x=y")]
    [InlineData("A=foo#bar", "foo#bar")]
    [InlineData("A=foo #bar", "foo")]
    [InlineData("A=foo\t#bar", "foo")]
    [InlineData("A= #only a comment", "")]
    [InlineData("A=#not a comment", "#not a comment")]
    [InlineData("A=it's", "it's")]
    [InlineData("A=say \"hi\"", "say \"hi\"")]
    [InlineData("A='single $B \\n'", "single $B \\n")]
    [InlineData("A='a # b' # comment", "a # b")]
    [InlineData("A=`back $B 'tick'`", "back $B 'tick'")]
    [InlineData("A=\"double\"", "double")]
    [InlineData("A=\"a # b\" # comment", "a # b")]
    [InlineData("A=\"tab\\there\"", "tab\there")]
    [InlineData("A=\"cr\\rlf\\n\"", "cr\rlf\n")]
    [InlineData("A=\"quote \\\" and slash \\\\\"", "quote \" and slash \\")]
    [InlineData("A=\"keep \\q\"", "keep \\q")]
    [InlineData("A=\"dollar \\$B\"", "dollar $B")]
    [InlineData("A=\"\"", "")]
    [InlineData("A=''", "")]
    [InlineData("A=\"x\"   ", "x")]
    [InlineData("A=1\r\n", "1")]
    [InlineData("\uFEFFA=1", "1")]
    [InlineData("A.b-c/d[0]=1", "1", "A.b-c/d[0]")]
    [InlineData("A_1=1", "1", "A_1")]
    [InlineData("exported=1", "1", "exported")]
    [InlineData("export=1", "1", "export")]
    public void Parses_values(string text, string expected, string key = "A") => Assert.Equal(expected, Value(text, key));

    [Fact]
    public void Reads_multi_line_quoted_values_and_counts_lines()
    {
        var file = Parse("# header\n\nA=\"one\ntwo\"\nB='x\ny'\nC=`p\nq`\nD=4\n");

        Assert.Equal(["A", "B", "C", "D"], file.Entries.Select(e => e.Name));
        Assert.Equal([3, 5, 7, 9], file.Entries.Select(e => e.Line));
        Assert.Equal("one\ntwo", file.Variables["A"]);
        Assert.Equal("x\ny", file.Variables["B"]);
        Assert.Equal("p\nq", file.Variables["C"]);
    }

    [Fact]
    public void Later_lines_win_and_both_are_listed()
    {
        var file = Parse("A=1\nA=2\n");

        Assert.Equal("2", file.Variables["A"]);
        Assert.Equal(2, file.Entries.Count);
        Assert.Equal(2, file.Find("A")!.Line);
        Assert.Null(file.Find("B"));
        Assert.Null(file.Path);
        Assert.Equal(".env", file.Name);
    }

    [Theory]
    [InlineData("A=1\nB=${A}", "1")]
    [InlineData("A=1\nB=$A", "1")]
    [InlineData("A=1\nB=\"${A}x\"", "1x")]
    [InlineData("A=1\nB='${A}'", "${A}")]
    [InlineData("A=1\nB=`${A}`", "${A}")]
    [InlineData("B=${A}", "")]
    [InlineData("B=${A:-dflt}", "dflt")]
    [InlineData("A=\nB=${A:-dflt}", "dflt")]
    [InlineData("A=\nB=${A-dflt}", "")]
    [InlineData("B=${A-dflt}", "dflt")]
    [InlineData("A=1\nB=${A:+alt}", "alt")]
    [InlineData("A=\nB=${A:+alt}", "")]
    [InlineData("A=\nB=${A+alt}", "alt")]
    [InlineData("B=${A+alt}", "")]
    [InlineData("A=1\nB=${A:?missing}", "1")]
    [InlineData("A=\nB=${A?missing}", "")]
    [InlineData("B=${A:-${C:-deep}}", "deep")]
    [InlineData("C=c\nB=${A:-x${C}y}", "xcy")]
    [InlineData("B=$$A", "$A")]
    [InlineData("B=5$", "5$")]
    [InlineData("B=$ 1", "$ 1")]
    [InlineData("B=$1", "$1")]
    [InlineData("B=\"a$\"", "a$")]
    [InlineData("A=1\nB=\"${A}\"", "1")]
    [InlineData("A=1\nB=x$A-y", "x1-y")]
    [InlineData("A_B=1\nB=$A_B", "1")]
    public void Interpolates_earlier_keys(string text, string expected) => Assert.Equal(expected, Value(text, "B"));

    [Fact]
    public void Variables_passed_in_win_over_earlier_keys()
    {
        var file = Parse("A=file\nB=${A}\nC=${D}", new() { ["A"] = "shell", ["D"] = "d", ["N"] = null });

        Assert.Equal("shell", file.Variables["B"]);
        Assert.Equal("d", file.Variables["C"]);
        var reference = Assert.Single(file.Entries[1].References);
        Assert.Equal("A", reference.Name);
        Assert.True(reference.IsSet);
        Assert.Null(reference.From);
        Assert.Equal("shell", reference.Value);
    }

    [Fact]
    public void Records_where_each_reference_came_from()
    {
        var file = Parse("A=1\nB=${A}${A}${C:-x}${D}");

        var references = file.Entries[1].References;
        Assert.Equal(["A", "C", "D"], references.Select(r => r.Name));
        Assert.Same(file.Entries[0], references[0].From);
        Assert.True(references[0].IsSet);
        Assert.False(references[0].UsedDefault);
        Assert.True(references[1].UsedDefault);
        Assert.False(references[1].IsSet);
        Assert.False(references[2].IsSet);
        Assert.False(references[2].UsedDefault);
        Assert.Equal(["${A} from A (.env line 1)", "${C} (not set)"], references.Take(2).Select(r => r.ToString()));
        Assert.Equal(".env line 2: D is not set, so an empty string is used.", Assert.Single(file.Warnings));
    }

    [Fact]
    public void A_bare_key_takes_its_value_from_the_variables_passed_in()
    {
        var file = Parse("A\nB # comment\nC\n", new() { ["A"] = "from shell", ["B"] = "b" });

        Assert.Equal(["A", "B"], file.Entries.Select(e => e.Name));
        Assert.True(file.Entries[0].IsInherited);
        Assert.Equal("from shell", file.Variables["A"]);
        Assert.Equal("A", Assert.Single(file.Entries[0].References).Name);
        Assert.False(Parse("A=1").Entries[0].IsInherited);
    }

    [Theory]
    [InlineData("A x", ".env line 1: expected = after A.", null)]
    [InlineData("=1", ".env line 1: expected a KEY=VALUE line.", null)]
    [InlineData("A: 1", ".env line 1: expected = after A.", null)]
    [InlineData("\nA=\"open", ".env line 2: the \" quote is not closed.", null)]
    [InlineData("A='open", ".env line 1: the ' quote is not closed.", null)]
    [InlineData("A=`open", ".env line 1: the ` quote is not closed.", null)]
    [InlineData("A=\"x\" y", ".env line 1: unexpected text after the closing quote.", null)]
    [InlineData("A='x'y", ".env line 1: unexpected text after the closing quote.", null)]
    [InlineData("A=${B", ".env line 1: a ${ is not closed with }.", null)]
    [InlineData("A=\"${B\"", ".env line 1: a ${ is not closed with }.", null)]
    [InlineData("A=${}", ".env line 1: '${' is not followed by a variable name.", null)]
    [InlineData("A=${1B}", ".env line 1: '1B' is not a valid variable name.", null)]
    [InlineData("A=${B*x}", ".env line 1: the reference to B is not valid: after the name, expect }, :-, -, :?, ?, :+ or +.", null)]
    [InlineData("A=${B:}", ".env line 1: the reference to B is not valid: after the name, expect }, :-, -, :?, ?, :+ or +.", null)]
    [InlineData("A=${B:?must be set}", ".env line 1: B: must be set.", null)]
    [InlineData("A=${B?}", ".env line 1: B is required but not set.", null)]
    [InlineData("C=\nA=${C:?}", ".env line 2: C is required but not set.", null)]
    public void Rejects_malformed_lines(string text, string message, string? alternative)
    {
        var ex = Assert.Throws<FormatException>(() => Parse(alternative ?? text));
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void Rejects_defaults_nested_too_deep_instead_of_overflowing()
    {
        var deep = string.Concat(Enumerable.Repeat("${A:-", 40)) + "x" + new string('}', 40);
        var ex = Assert.Throws<FormatException>(() => Parse("B=" + deep));
        Assert.Contains("nests more than 16", ex.Message, StringComparison.Ordinal);

        var ok = string.Concat(Enumerable.Repeat("${A:-", 15)) + "x" + new string('}', 15);
        Assert.Equal("x", Value("B=" + ok, "B"));
    }

    [Fact]
    public void Error_messages_never_hold_the_default_text()
    {
        var ex = Assert.Throws<FormatException>(() => Parse("A=${1:-hunter2}"));
        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_prints_without_its_value()
    {
        var entry = Parse("\nPASSWORD=hunter2").Entries[0];
        Assert.Equal("PASSWORD (.env line 2)", entry.ToString());
        Assert.Equal("${X} from the variables passed in", new DotEnvReference("X", null, IsSet: true) { Value = "hunter2" }.ToString());
    }

    [Fact]
    public void Load_reads_a_file_and_keeps_its_path()
    {
        using var folder = new TempFolder();
        var path = folder.Write("app.envfile", "A=1\nB=${C}\n");

        var file = DotEnvFile.Load(path, new Dictionary<string, string?> { ["C"] = "c" });

        Assert.Equal("app.envfile", file.Name);
        Assert.Equal(path, file.Path);
        Assert.Equal(path, file.Entries[0].Path);
        Assert.Equal("c", file.Variables["B"]);
        Assert.Throws<FileNotFoundException>(() => DotEnvFile.Load(Path.Join(folder.Path, "missing")));
    }

    [Fact]
    public void Validates_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => DotEnvFile.Parse(null!, "x"));
        Assert.Throws<ArgumentException>(() => DotEnvFile.Parse("", " "));
        Assert.Throws<ArgumentException>(() => DotEnvFile.Load(" "));
    }

    [Fact]
    public void Raw_format_keeps_values_as_written()
    {
        var file = DotEnvFile.Parse("A= '${B}' # x\nB=\"q\"", "raw", null, _ => null, raw: true);

        Assert.Equal(" '${B}' # x", file.Variables["A"]);
        Assert.Equal("\"q\"", file.Variables["B"]);
        Assert.Equal(2, file.Entries[1].Line);
    }
}

public class DotEnvLayersTests
{
    [Fact]
    public void Later_files_win_and_see_earlier_keys()
    {
        var layers = DotEnvLayers.Parse([(".env", "A=1\nB=base\n"), (".env.local", "B=${A}-local\nC=$B")]);

        Assert.Equal("1-local", layers.Variables["B"]);
        Assert.Equal("1-local", layers.Variables["C"]);
        Assert.Equal([".env", ".env.local"], layers.History("B").Select(e => e.File));
        Assert.Empty(layers.History("Z"));
        Assert.Same(layers.Files[0].Entries[0], layers.Files[1].Entries[0].References[0].From);
        Assert.Same(layers.Files[1].Entries[0], layers.Files[1].Entries[1].References[0].From);
    }

    [Fact]
    public void Variables_passed_in_win_over_every_file()
    {
        var layers = DotEnvLayers.Parse([(".env", "A=1"), (".env.local", "B=${A}")], new Dictionary<string, string?> { ["A"] = "shell" });

        Assert.Equal("shell", layers.Variables["B"]);
    }

    [Fact]
    public void Collects_every_file_warning()
    {
        var layers = DotEnvLayers.Parse([("one", "A=$X"), ("two", "B=$Y")]);

        Assert.Equal(["one line 1: X is not set, so an empty string is used.", "two line 1: Y is not set, so an empty string is used."], layers.Warnings);
    }

    [Fact]
    public void Load_reads_files_in_order_and_can_skip_missing_ones()
    {
        using var folder = new TempFolder();
        var first = folder.Write("base.envfile", "A=1");
        var second = folder.Write("local.envfile", "A=2");
        var missing = Path.Join(folder.Path, "missing.envfile");

        var layers = DotEnvLayers.Load([first, missing, second], skipMissing: true);

        Assert.Equal("2", layers.Variables["A"]);
        Assert.Equal(["base.envfile", "local.envfile"], layers.Files.Select(f => f.Name));
        Assert.Throws<FileNotFoundException>(() => DotEnvLayers.Load([first, missing]));
    }

    [Fact]
    public void Conventional_lists_the_files_that_exist_in_priority_order()
    {
        using var folder = new TempFolder();
        foreach (var name in new[] { ".env", ".env.local", ".env.production", ".env.production.local", ".env.staging" })
        {
            folder.Write(name, "A=" + name);
        }

        Assert.Equal([".env", ".env.local", ".env.production", ".env.production.local"], DotEnvLayers.Conventional(folder.Path, " production ").Select(Path.GetFileName));
        Assert.Equal([".env", ".env.local"], DotEnvLayers.Conventional(folder.Path).Select(Path.GetFileName));
        Assert.Equal([".env", ".env.local"], DotEnvLayers.Conventional(folder.Path, " ").Select(Path.GetFileName));
        File.Delete(Path.Join(folder.Path, ".env.local"));
        Assert.Equal([".env", ".env.production", ".env.production.local"], DotEnvLayers.Conventional(folder.Path, "production").Select(Path.GetFileName));
    }

    [Fact]
    public void Validates_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => DotEnvLayers.Load(null!));
        Assert.Throws<ArgumentNullException>(() => DotEnvLayers.Parse(null!));
        Assert.Throws<ArgumentException>(() => DotEnvLayers.Conventional(""));
        Assert.Throws<ArgumentNullException>(() => DotEnvLayers.Parse([]).History(null!));
    }

    [Fact]
    public void WithDotEnv_links_environment_variables_to_dotenv_lines()
    {
        var environment = new Dictionary<string, string?>
        {
            ["Db__Host"] = "db.local",
            ["Db__Password"] = "hunter2",
            ["Other"] = "shell",
            ["Drift"] = "changed",
        };
        var layers = DotEnvLayers.Parse(
            [(".env", "Db__Host=db\nDb__Password=hunter2\nDrift=old\nHOST=db.local"), (".env.local", "Db__Host=${HOST}\nNotUsed=1\nBroken=$NOPE")]);
        var config = new ConfigurationBuilder().Add(new ComposeGoldenTests.SimulatedEnvironmentSource(environment)).Build();

        var report = ConfigTracer.For(config).WithOptions(o => o.Environment = environment).WithDotEnv(layers).Trace();

        var host = report.Find("Db:Host")!.EffectiveStep!.Origins;
        Assert.Equal([".env", ".env.local"], host.Select(o => o.Source));
        Assert.Equal(["line 1", "line 1"], host.Select(o => o.Location));
        Assert.Equal(LayerKind.DotEnv, host[1].Kind);
        Assert.Equal(new DotEnvEntryRef(".env.local", "Db__Host"), host[1].Reference);
        Assert.False(host[1].IsInferred);
        Assert.Null(host[1].Note);
        var hostOrigin = Assert.Single(host[1].Origins);
        Assert.Equal(".env", hostOrigin.Source);
        Assert.Equal("line 4", hostOrigin.Location);
        Assert.Equal("interpolated as ${HOST} in .env.local line 1", hostOrigin.Note);

        var password = Assert.Single(report.Find("Db:Password")!.EffectiveStep!.Origins);
        Assert.True(password.IsSensitive);
        Assert.Equal("***", password.Value);

        Assert.Empty(report.Find("Other")!.EffectiveStep!.Origins);

        var drift = Assert.Single(report.Find("Drift")!.EffectiveStep!.Origins);
        Assert.True(drift.IsInferred);
        Assert.Equal("the dotenv value differs from the value seen here, so the variable may be set somewhere else too", drift.Note);
        Assert.Contains(".env.local line 3: NOPE is not set, so an empty string is used.", report.Warnings);
    }

    [Fact]
    public void WithDotEnv_ignores_layers_that_are_not_environment_variables()
    {
        var resolver = new DotEnvResolver(DotEnvLayers.Parse([(".env", "A=1")]));
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "x", Reference = new FileRef("A") }, diagnostics));
        Assert.Empty(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.DotEnv, Source = "x", Reference = new EnvironmentVariableRef("A") }, diagnostics));
        Assert.Single(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "x", Reference = new EnvironmentVariableRef("A") }, diagnostics));
        Assert.Single(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "x", Value = "1", Reference = new EnvironmentVariableRef("A") }, diagnostics));
        Assert.False(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "x", Value = "1", Reference = new EnvironmentVariableRef("A") }, diagnostics)[0].IsInferred);
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!, diagnostics));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(new ProvenanceStep { Kind = LayerKind.DotEnv, Source = "x" }, null!));
        Assert.Throws<ArgumentNullException>(() => ConfigTracer.For(new ConfigurationBuilder().Build()).WithDotEnv(null!));
        Assert.Throws<ArgumentNullException>(() => ComposeTracerExtensions.WithDotEnv(null!, DotEnvLayers.Parse([])));
    }
}
