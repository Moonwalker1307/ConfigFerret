using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace ConfigFerret.AzureDevOps.Tests;

/// <summary>
/// Whole traces over pipeline files made to grow a trace, take it deep or reach its bounds. Whatever the file, the trace
/// ends, keeps to its bounds, and hides what it did not follow and what a secret set.
/// </summary>
public class WholeTraceTests
{
    // The tracer's bound on the origin steps of one setting.
    private const int MaxStepsPerSetting = 1_000;

    // Each file, with the start of a warning the trace must give for it.
    public static TheoryData<string, string?> Pipelines() => new()
    {
        { SelfReferences(5), null },
        { Chain(12), "Stopped following the origins of A0 after 8 levels upstream (MaxDepth)" },
        { BeyondTheBound(linked: false), "MAIL_AUTH is linked or set in more Azure Pipelines stages, jobs and variables than the trace follows for one step (1,000)" },
        { BeyondTheBound(linked: true), "MAIL_AUTH is linked or set in more Azure Pipelines stages, jobs and variables than the trace follows for one step (1,000)" },
        { "variables:\n- group: shared\nsteps:\n- script: ./run.sh\n  env:\n    MAIL_AUTH: $(smtp-auth)\n%%\nvariables:\n- name: smtp-auth\n  value: $(MAIL_AUTH)\n", null },
    };

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(Pipelines))]
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

            if (Winning(setting.Steps).Any(s => s.Reference is AzureDevOpsVariableRef))
            {
                Assert.True(setting.IsSensitive);
                Assert.Equal("***", setting.Value);
            }
        }

        if (warning is not null)
        {
            Assert.Contains(report.Warnings, w => w.StartsWith(warning, StringComparison.Ordinal));
        }
    }

    // Every name the files link or set is an environment variable of the process, and every name they link or refer to
    // and do not set is a secret in a variable group.
    private static ProvenanceReport Trace(string[] files)
    {
        var read = new AzureDevOpsTraceOptions();
        foreach (var (file, i) in files.Select((f, i) => (f, i)))
        {
            read.AddPipelineText(file, Name(i));
        }

        var environment = read.Mappings.Select(m => m.Name).Concat(read.Variables.Select(v => AzureDevOpsVariableGroups.ToEnvironmentName(v.Name)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(n => n, _ => (string?)"fake-value", StringComparer.Ordinal);
        var secrets = read.Mappings.Select(m => m.VariableName).Concat(read.Variables.Select(v => Referenced(v.Value)).OfType<string>())
            .Except(read.Variables.Select(v => v.Name), StringComparer.OrdinalIgnoreCase)
            .Select(name => new AzureDevOpsVariable(name, null, true, "shared", 1))
            .ToList();
        var groups = new AzureDevOpsVariableGroups("contoso/api", [new(1, "shared", AzureDevOpsGroupType.Vsts, null, null, null, secrets)]);

        return ConfigTracer.For(new ConfigurationBuilder().Add(new FakeEnvironmentVariables(environment)).Build())
            .WithOptions(o => o.Environment = environment)
            .WithAzureDevOps(groups, o =>
            {
                foreach (var (file, i) in files.Select((f, i) => (f, i)))
                {
                    o.AddPipelineText(file, Name(i));
                }
            })
            .Trace();
    }

    // The name a whole-value reference such as $(X) refers to.
    private static string? Referenced(string value) => value.StartsWith("$(", StringComparison.Ordinal) && value.EndsWith(')') ? value[2..^1] : null;

    private static string Name(int i) => i == 0 ? "azure-pipelines.yml" : $"templates/include-{i}.yml";

    private static string SelfReferences(int stages)
    {
        var yaml = new StringBuilder("variables:\n- name: X\n  value: base\nstages:\n");
        for (var i = 0; i < stages; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"- stage: s{i}\n  variables:\n  - name: X\n    value: $(X)\n");
        }

        return yaml.ToString();
    }

    private static string Chain(int length)
    {
        var yaml = new StringBuilder("variables:\n");
        for (var i = 0; i < length; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"  A{i}: $(A{i + 1})\n");
        }

        return yaml.Append(CultureInfo.InvariantCulture, $"  A{length}: end\n").ToString();
    }

    // Job A links MAIL_AUTH to X, which the pipeline sets as often as one step looks at, so job B is cut.
    private static string BeyondTheBound(bool linked)
    {
        var yaml = new StringBuilder("variables:\n");
        for (var i = 0; i < 1_000; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"- name: X\n  value: v{i}\n");
        }

        yaml.Append("jobs:\n- job: A\n  steps:\n  - script: ./run.sh\n    env:\n      MAIL_AUTH: $(X)\n");
        return yaml.Append(linked
            ? "- job: B\n  variables:\n    Y: y\n  steps:\n  - script: ./run.sh\n    env:\n      MAIL_AUTH: $(Y)\n"
            : "- job: B\n  variables:\n    MAIL_AUTH: direct\n  steps:\n  - script: ./run.sh\n").ToString();
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
