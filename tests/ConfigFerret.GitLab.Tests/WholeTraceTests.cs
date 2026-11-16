using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace ConfigFerret.GitLab.Tests;

/// <summary>
/// Whole traces over CI files made to grow a trace, take it deep or reach its bounds. Whatever the file, the trace ends,
/// keeps to its bounds, hides what it did not follow, and never shows a masked value.
/// </summary>
public class WholeTraceTests
{
    // The tracer's bound on the origin steps of one setting.
    private const int MaxStepsPerSetting = 1_000;

    private const string Masked = "masked-fake-value-7c1";

    // Each file, with the start of a warning the trace must give for it.
    public static TheoryData<string, string?> CiFiles() => new()
    {
        { SelfReferences(5), null },
        { Chain(12), "Stopped following the origins of A0 after 8 levels upstream (MaxDepth)" },
        { BeyondTheBound(linked: false), "MAIL_AUTH is linked or set in more GitLab CI jobs and definitions than the trace follows for one step (1,000)" },
        { BeyondTheBound(linked: true), "MAIL_AUTH is linked or set in more GitLab CI jobs and definitions than the trace follows for one step (1,000)" },
        { "deploy:\n  script:\n    - export MAIL_AUTH=$SMTP_AUTH\n%%\nreview:\n  variables:\n    SMTP_AUTH: ${SMTP_PASSWORD}\n  script:\n    - export MAIL_AUTH=$SMTP_AUTH\n", null },
    };

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(CiFiles))]
    public async Task Keeps_a_whole_trace_within_its_bounds(string files, string? warning)
    {
        var report = await Task.Run(() => Trace(files.Split("%%\n")), TestContext.Current.CancellationToken);

        Assert.NotEmpty(report.Settings);
        foreach (var setting in report.Settings)
        {
            Assert.InRange(setting.Steps.Sum(CountOrigins), 0, MaxStepsPerSetting);
            Assert.InRange(setting.Steps.Max(Depth), 0, new TraceOptions().MaxDepth);
            if (report.Warnings.Any(w => w.StartsWith($"Stopped following the origins of {setting.Key} after ", StringComparison.Ordinal)))
            {
                Assert.True(setting.IsSensitive);
                Assert.Equal("***", setting.Value);
            }

            if (Winning(setting.Steps).Any(s => s.Reference is GitLabVariableRef))
            {
                Assert.True(setting.IsSensitive);
                Assert.Equal("***", setting.Value);
            }
        }

        if (warning is not null)
        {
            Assert.Contains(report.Warnings, w => w.StartsWith(warning, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(Masked, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Masked, report.ToJson(), StringComparison.Ordinal);
        Assert.All(report.Warnings, w => Assert.DoesNotContain(Masked, w, StringComparison.Ordinal));
    }

    // Every name the files link or set is an environment variable of the process, set to v, a value the files set too,
    // and every name they link or refer to and do not set is a masked project variable.
    private static ProvenanceReport Trace(string[] files)
    {
        var read = new GitLabTraceOptions();
        foreach (var (file, i) in files.Select((f, i) => (f, i)))
        {
            read.AddCiFileText(file, Name(i));
        }

        var environment = read.Mappings.Select(m => m.Name).Concat(read.Variables.Select(v => v.Name))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(n => n, _ => (string?)"v", StringComparer.Ordinal);
        var variables = new GitLabVariables(read.Mappings.Select(m => m.Key).Concat(read.Variables.Select(v => Referenced(v.Value)).OfType<string>())
            .Except(read.Variables.Select(v => v.Name), StringComparer.Ordinal)
            .Select(key => new GitLabVariable(key, Masked, GitLabVariableLevel.Project, "contoso/api", IsMasked: true)));

        return ConfigTracer.For(new ConfigurationBuilder().Add(new FakeEnvironmentVariables(environment)).Build())
            .WithOptions(o => o.Environment = environment)
            .WithGitLab(variables, o =>
            {
                foreach (var (file, i) in files.Select((f, i) => (f, i)))
                {
                    o.AddCiFileText(file, Name(i));
                }
            })
            .Trace();
    }

    // The name a whole-value reference such as $X or ${X} refers to.
    private static string? Referenced(string value) => value.StartsWith('$') && value.Trim('$', '{', '}') is { Length: > 0 } name ? name : null;

    private static string Name(int i) => i == 0 ? ".gitlab-ci.yml" : $"ci/include-{i}.yml";

    private static string SelfReferences(int jobs)
    {
        var yaml = new StringBuilder("variables:\n  X: base\n");
        for (var i = 0; i < jobs; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"job-{i}:\n  variables:\n    X: $X\n  script: [make]\n");
        }

        return yaml.ToString();
    }

    private static string Chain(int length)
    {
        var yaml = new StringBuilder("variables:\n");
        for (var i = 0; i < length; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"  A{i}: $A{i + 1}\n");
        }

        return yaml.Append(CultureInfo.InvariantCulture, $"  A{length}: end\n").ToString();
    }

    // job-a links MAIL_AUTH to X, which as many hidden jobs set as one step looks at, so job-b is cut.
    private static string BeyondTheBound(bool linked)
    {
        var yaml = new StringBuilder("job-a:\n  script:\n    - export MAIL_AUTH=$X\n");
        for (var i = 0; i < 1_000; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $".t{i}:\n  variables:\n    X: v\n");
        }

        return yaml.Append(linked
            ? "job-b:\n  variables:\n    Y: y\n  script:\n    - export MAIL_AUTH=$Y\n"
            : "job-b:\n  variables:\n    MAIL_AUTH: direct\n  script:\n    - ./run.sh\n").ToString();
    }

    private static int CountOrigins(ProvenanceStep step) => step.Origins.Sum(o => 1 + CountOrigins(o));

    private static int Depth(ProvenanceStep step) => step.Origins.Count == 0 ? 0 : 1 + step.Origins.Max(Depth);

    // The steps the value comes through: the effective step of each chain, and the effective steps upstream of it.
    private static IEnumerable<ProvenanceStep> Winning(IEnumerable<ProvenanceStep> steps) =>
        steps.Where(s => s.IsEffective).SelectMany(s => Winning(s.Origins).Prepend(s));

    /// <summary>Environment variables from a dictionary instead of the test process, described as environment variables.</summary>
    private sealed class FakeEnvironmentVariables(Dictionary<string, string?> variables) : EnvironmentVariablesConfigurationProvider, IConfigurationSource
    {
        public override void Load() => Data = new Dictionary<string, string?>(variables, StringComparer.OrdinalIgnoreCase);

        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
    }
}
