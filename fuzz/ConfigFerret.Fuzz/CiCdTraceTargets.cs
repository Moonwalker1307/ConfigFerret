using System.Diagnostics;
using ConfigFerret.AzureDevOps;
using ConfigFerret.GitLab;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace ConfigFerret.Fuzz;

/// <summary>
/// Fuzz targets that run a whole trace over CI and pipeline text, the way an app does, so that what the tracer does with
/// the resolvers' answers, level after level up to <see cref="TraceOptions.MaxDepth"/>, is fuzzed too. The first byte
/// picks the options, and the rest holds up to three CI or pipeline files, split by <c>%%</c> lines. Every name the files
/// set or link is an environment variable of the traced process. Every name they refer to and do not set is a variable on
/// the server, some of them secret; with flag 16, so is every name they set.
/// </summary>
internal static partial class Targets
{
    // The value of the secret server variables. No fuzzed input is likely to hold it, so it shows up if it leaks.
    private const string ServerSecret = "server-secret-m3z7";

    // libFuzzer stops an input after 10 seconds. A trace that keeps to its bounds ends well within this.
    private static readonly TimeSpan TraceLimit = TimeSpan.FromSeconds(5);

    private static readonly string[] CiFileNames = [".gitlab-ci.yml", "ci/deploy.yml", "ci/review/.gitlab-ci.yml"];

    private static readonly string[] PipelineNames = ["azure-pipelines.yml", "templates/deploy.yml", "release/azure-pipelines.yml"];

    private static void GitLabTrace(ReadOnlySpan<byte> data)
    {
        var flags = data.Length > 0 ? data[0] : (byte)0;
        var files = Split(data.Length > 0 ? data[1..] : data).Take(CiFileNames.Length).ToList();
        var input = string.Join('\n', files);
        var ci = new GitLabTraceOptions();
        for (var i = 0; i < files.Count; i++)
        {
            ci.AddCiFileText(files[i], CiFileNames[i]);
        }

        var set = ci.Variables.Select(v => v.Name).ToList();
        var keys = ci.Mappings.Select(m => m.Key)
            .Concat(ci.Variables.Select(v => Referenced(v.Value)).OfType<string>())
            .Concat((flags & 16) != 0 ? set : [])
            .Except((flags & 16) != 0 ? [] : set, StringComparer.Ordinal)
            .Take(100)
            .ToList();
        var levels = new[] { GitLabVariableLevel.Project, GitLabVariableLevel.Group, GitLabVariableLevel.Instance };
        var variables = new GitLabVariables(keys.Select((key, i) => new GitLabVariable(
            key,
            i % 3 == 2 ? LayerValue : ServerSecret,
            levels[i / 3 % 3],
            "contoso/api",
            (flags & 8) != 0 && i % 2 == 1 ? "production" : "*",
            IsMasked: i % 3 == 0,
            IsFile: i % 3 == 1)));
        var secrets = variables.Entries.Where(v => v.Value == ServerSecret).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);

        var tracer = TracerFor(ci.Mappings.Select(m => m.Name).Concat(ci.Variables.Select(v => v.Name)));
        tracer.WithGitLab(variables, o =>
        {
            o.Environment = (flags & 3) switch { 1 => "production", 2 => "review/feature-1", 3 => string.Empty, _ => null };
            o.MatchByName = (flags & 4) == 0;
            for (var i = 0; i < files.Count; i++)
            {
                o.AddCiFileText(files[i], CiFileNames[i]);
            }
        });

        CheckTrace(tracer, input, step => step.Reference is GitLabVariableRef r && secrets.Contains(r.Key));
    }

    private static void AzureDevOpsTrace(ReadOnlySpan<byte> data)
    {
        var flags = data.Length > 0 ? data[0] : (byte)0;
        var files = Split(data.Length > 0 ? data[1..] : data).Take(PipelineNames.Length).ToList();
        var input = string.Join('\n', files);
        var pipelines = new AzureDevOpsTraceOptions();
        for (var i = 0; i < files.Count; i++)
        {
            pipelines.AddPipelineText(files[i], PipelineNames[i]);
        }

        var set = pipelines.Variables.Select(v => v.Name).ToList();
        var names = pipelines.Mappings.Select(m => m.VariableName)
            .Concat(pipelines.Variables.Select(v => Referenced(v.Value)).OfType<string>())
            .Concat((flags & 16) != 0 ? set : [])
            .Except((flags & 16) != 0 ? [] : set, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();
        var groupNames = pipelines.GroupLinks.Select(g => g.Group).Append("shared").Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        var keyVault = (flags & 8) != 0;
        var groups = new AzureDevOpsVariableGroups("contoso/api", groupNames.Select((group, g) =>
        {
            var type = keyVault && g == 0 ? AzureDevOpsGroupType.AzureKeyVault : AzureDevOpsGroupType.Vsts;
            var vault = type == AzureDevOpsGroupType.AzureKeyVault ? "contoso-kv" : null;
            var inGroup = names.Where((_, i) => i % groupNames.Count == g)
                .Select((name, i) => new AzureDevOpsVariable(name, i % 2 == 0 || vault is not null ? null : LayerValue, i % 2 == 0, group, g + 1, type, vault))
                .ToList();
            return new AzureDevOpsVariableGroup(g + 1, group, type, null, vault, null, inGroup);
        }));
        var secrets = groups.Variables.Where(v => v.IsSensitive).Select(v => (v.Group, v.Name)).ToHashSet();

        var environment = pipelines.Mappings.Select(m => m.Name)
            .Concat(pipelines.Variables.SelectMany(v => new[] { v.Name, AzureDevOpsVariableGroups.ToEnvironmentName(v.Name) }));
        var tracer = TracerFor(environment);
        tracer.WithAzureDevOps(groups, o =>
        {
            o.MatchByName = (flags & 4) == 0;
            for (var i = 0; i < files.Count; i++)
            {
                o.AddPipelineText(files[i], PipelineNames[i]);
            }
        });

        CheckTrace(tracer, input, step => step.Reference is AzureDevOpsVariableRef r && secrets.Contains((r.Group, r.Name)));
    }

    // The name a whole-value reference such as $X, ${X} or $(X) refers to, so that the server has a variable for it.
    private static string? Referenced(string value)
    {
        var name = value.Trim().TrimStart('$').Trim('{', '}', '(', ')');
        return value.TrimStart().StartsWith('$') && name.Length > 0 ? name : null;
    }

    // Traces a configuration of environment variables only, each set to LayerValue, which are also the trace's view of
    // the process environment, so that every layer is described as the variable it is.
    private static ConfigTracer TracerFor(IEnumerable<string> names)
    {
        var variables = names.Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToDictionary(n => n, _ => (string?)LayerValue, StringComparer.Ordinal);
        var root = new ConfigurationBuilder().Add(new FuzzEnvironmentVariables(variables)).Build();
        return ConfigTracer.For(root).WithOptions(o => o.Environment = variables);
    }

    /// <summary>
    /// Traces, renders and checks what the trace promises whatever the text: it ends in time and without an exception,
    /// no setting has more origin steps than the bound or a chain deeper than MaxDepth, a setting whose trace was cut or
    /// whose winning chain reaches a secret is redacted, no sensitive step shows a value, and a secret's value appears
    /// nowhere.
    /// </summary>
    private static void CheckTrace(ConfigTracer tracer, string input, Func<ProvenanceStep, bool> isSecret)
    {
        var watch = Stopwatch.StartNew();
        var report = tracer.Trace();
        var text = report.ToText();
        var json = report.ToJson();
        var explained = report.Settings.Select(s => report.Explain(s.Key)).ToList();
        if (watch.Elapsed > TraceLimit)
        {
            throw new InvariantException($"The trace took {watch.Elapsed.TotalSeconds:0.0}s for {report.Settings.Count} settings.");
        }

        var redacted = tracer.Options.RedactedText;
        var total = 0;
        var settingsCut = report.Settings.Count;
        for (var i = 0; i < report.Settings.Count; i++)
        {
            var setting = report.Settings[i];
            var origins = setting.Steps.Sum(CountOrigins);
            total += origins;
            if (origins > tracer.MaxStepsPerSetting)
            {
                throw new InvariantException($"'{setting.Key}' has {origins} origin steps, more than the bound of {tracer.MaxStepsPerSetting}.");
            }

            var depth = setting.Steps.Select(Depth).DefaultIfEmpty().Max();
            if (depth > tracer.Options.MaxDepth)
            {
                throw new InvariantException($"'{setting.Key}' has a chain {depth} levels deep, more than MaxDepth.");
            }

            if (report.Warnings.Any(w => w.Contains($"so the traces of {setting.Key} and every setting after it", StringComparison.Ordinal)))
            {
                settingsCut = Math.Min(settingsCut, i);
            }

            var cut = i >= settingsCut || report.Warnings.Any(w => w.StartsWith($"Stopped following the origins of {setting.Key} after ", StringComparison.Ordinal));
            if ((cut || Winning(setting.Steps).Any(isSecret)) && (!setting.IsSensitive || (setting.Value is not null && setting.Value != redacted)))
            {
                throw new InvariantException($"'{setting.Key}' was cut off or wins through a secret, but its value is not redacted.");
            }

            if (setting.IsSensitive && setting.Value is not null && setting.Value != redacted)
            {
                throw new InvariantException($"'{setting.Key}' is sensitive but its value is not redacted.");
            }

            foreach (var step in EveryStep(setting.Steps))
            {
                if (step.IsSensitive && step.Value is not null && step.Value != redacted)
                {
                    throw new InvariantException($"A sensitive step of '{setting.Key}', {step.Source}, shows its value.");
                }
            }
        }

        if (total > tracer.MaxStepsPerTrace)
        {
            throw new InvariantException($"The trace has {total} origin steps, more than the bound of {tracer.MaxStepsPerTrace}.");
        }

        // Notes, sources and warnings are not redacted, so the secret must not be in them or in what the report renders.
        if (!input.Contains(ServerSecret, StringComparison.Ordinal))
        {
            var shown = new[] { text, json }.Concat(explained).Concat(report.Warnings)
                .Concat(report.Settings.SelectMany(s => EveryStep(s.Steps)).SelectMany(s => new[] { s.Source, s.Note ?? string.Empty }));
            if (shown.Any(s => s.Contains(ServerSecret, StringComparison.Ordinal)))
            {
                throw new InvariantException("The value of a secret server variable appears in the trace.");
            }
        }
    }

    private static int CountOrigins(ProvenanceStep step) => step.Origins.Sum(o => 1 + CountOrigins(o));

    private static int Depth(ProvenanceStep step) => step.Origins.Count == 0 ? 0 : 1 + step.Origins.Max(Depth);

    private static IEnumerable<ProvenanceStep> EveryStep(IEnumerable<ProvenanceStep> steps) => steps.SelectMany(s => EveryStep(s.Origins).Prepend(s));

    // The steps the setting's value comes through: the effective step of each chain, and the effective steps upstream of it.
    private static IEnumerable<ProvenanceStep> Winning(IEnumerable<ProvenanceStep> steps) =>
        steps.Where(s => s.IsEffective).SelectMany(s => Winning(s.Origins).Prepend(s));

    /// <summary>Environment variables from a dictionary instead of the fuzzing process, described as environment variables.</summary>
    private sealed class FuzzEnvironmentVariables(Dictionary<string, string?> variables) : EnvironmentVariablesConfigurationProvider, IConfigurationSource
    {
        public override void Load()
        {
            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in variables)
            {
                Data[name.Replace("__", ":", StringComparison.Ordinal)] = value;
            }
        }

        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
    }
}
