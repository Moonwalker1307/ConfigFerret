using System.Text.RegularExpressions;

namespace ConfigFerret.Octopus;

/// <summary>
/// The steps of an Octopus Deploy deployment process that change configuration files: structured configuration
/// variables, substitute variables in templates, .NET configuration variables and configuration transforms. Read with
/// <see cref="OctopusVariables.LoadAsync(OctopusVariablesOptions, CancellationToken)"/> when
/// <see cref="OctopusVariablesOptions.ReadDeploymentProcess"/> or <see cref="OctopusVariablesOptions.Release"/> is set,
/// or built from actions you already have.
/// </summary>
public sealed class OctopusDeploymentProcess
{
    /// <summary>Creates a process from actions you already have.</summary>
    /// <param name="actions">The actions (step parts) of the process, in order.</param>
    /// <param name="description">What was read, for example <c>deployment process of release 1.4.0</c>.</param>
    public OctopusDeploymentProcess(IEnumerable<OctopusDeploymentAction> actions, string description = "deployment process")
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Actions = [.. actions];
        Description = description;
    }

    /// <summary>The actions of the process, in order. A step with one action has one entry.</summary>
    public IReadOnlyList<OctopusDeploymentAction> Actions { get; }

    /// <summary>What was read: the current process, the one at a Git reference, or the snapshot of a release.</summary>
    public string Description { get; }
}

/// <summary>One action of an Octopus deployment process, with the configuration features it runs.</summary>
/// <param name="StepName">The name of the step that holds the action.</param>
/// <param name="Name">The action name, which variables scoped to a step name.</param>
/// <param name="ActionType">The action type, for example <c>Octopus.TentaclePackage</c>.</param>
public sealed record OctopusDeploymentAction(string StepName, string Name, string ActionType)
{
    /// <summary>True when the action is turned off, so it does not run.</summary>
    public bool IsDisabled { get; init; }

    /// <summary>The target tags (roles) the step runs on. Empty when it does not name any.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>The environments the action runs in, by name. Empty for all of them.</summary>
    public IReadOnlyList<string> Environments { get; init; } = [];

    /// <summary>The environments the action skips, by name.</summary>
    public IReadOnlyList<string> ExcludedEnvironments { get; init; } = [];

    /// <summary>The channels the action runs for, by name. Empty for all of them.</summary>
    public IReadOnlyList<string> Channels { get; init; } = [];

    /// <summary>
    /// The files structured configuration variables replace values in, as Octopus target patterns such as
    /// <c>appsettings.json</c> or <c>config/*.yaml</c>. Empty when the feature is off.
    /// </summary>
    public IReadOnlyList<string> StructuredConfigurationTargets { get; init; } = [];

    /// <summary>
    /// The files substitute variables in templates replaces <c>#{Variable}</c> in, as Octopus target patterns. Empty
    /// when the feature is off.
    /// </summary>
    public IReadOnlyList<string> SubstituteInFilesTargets { get; init; } = [];

    /// <summary>True when the action replaces appSettings and connectionStrings in .NET XML <c>.config</c> files.</summary>
    public bool ConfigurationVariables { get; init; }

    /// <summary>True when the action runs .NET XML configuration transforms.</summary>
    public bool ConfigurationTransforms { get; init; }

    /// <summary>The step template the action comes from, by name, or null when it is not from one.</summary>
    public string? StepTemplate { get; init; }

    /// <summary>
    /// The parameter values set on a step template action, by parameter name. A null value is sensitive: Octopus does
    /// not return it.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a target pattern in <paramref name="patterns"/> names the file at <paramref name="path"/>.</summary>
    internal static TargetMatch MatchTarget(IReadOnlyList<string> patterns, string path, out string? pattern)
    {
        pattern = null;
        var normalized = path.Replace('\\', '/');
        var result = TargetMatch.No;
        foreach (var candidate in patterns)
        {
            if (candidate.Contains("#{", StringComparison.Ordinal))
            {
                // The pattern is built from variables when the step runs, so it may name any file.
                if (result == TargetMatch.No)
                {
                    result = TargetMatch.Maybe;
                    pattern = candidate;
                }

                continue;
            }

            if (Glob(candidate).IsMatch(normalized))
            {
                pattern = candidate;
                return TargetMatch.Yes;
            }
        }

        return result;
    }

    // A pattern is relative to the extracted package, so it matches the end of the path, at a folder boundary.
    private static Regex Glob(string pattern)
    {
        var text = pattern.Trim().Replace('\\', '/').TrimStart('.', '/');
        var regex = new System.Text.StringBuilder("(?:^|/)");
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i++;
                if (i + 1 < text.Length && text[i + 1] == '/')
                {
                    i++;
                    regex.Append("(?:[^/]*/)*");
                }
                else
                {
                    regex.Append(".*");
                }
            }
            else
            {
                regex.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString()),
                });
            }
        }

        regex.Append('$');
        return new Regex(regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}

/// <summary>Whether a step's target patterns name a file.</summary>
internal enum TargetMatch
{
    No,
    Maybe,
    Yes,
}

/// <summary>A pointer to a parameter of a step template action.</summary>
/// <param name="StepName">The step that sets the parameter.</param>
/// <param name="Name">The parameter name.</param>
public sealed record OctopusStepParameterRef(string StepName, string Name) : SourceRef
{
    /// <inheritdoc/>
    protected override string? ValueName() => Name;
}
