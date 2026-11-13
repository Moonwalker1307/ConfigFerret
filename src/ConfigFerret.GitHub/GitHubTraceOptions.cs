using System.Globalization;
using System.Text.RegularExpressions;

namespace ConfigFerret.GitHub;

/// <summary>How to link configuration layers to GitHub Actions variables.</summary>
public sealed partial class GitHubTraceOptions
{
    private readonly Dictionary<string, GitHubMapping> _mappings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When true (the default), an environment variable, Kubernetes env entry or ConfigMap/Secret key is linked to a
    /// GitHub variable (or, failing that, a secret) with the same name. These links are marked as inferred.
    /// </summary>
    public bool MatchByName { get; set; } = true;

    /// <summary>The explicit links, from <see cref="Map"/> and the workflow files read with <see cref="AddWorkflow"/>.</summary>
    public IReadOnlyCollection<GitHubMapping> Mappings => _mappings.Values;

    /// <summary>
    /// Links the environment variable, Kubernetes env entry or ConfigMap/Secret key <paramref name="name"/> to the GitHub
    /// variable or secret <paramref name="variableName"/>, for example <c>Map("DB__Timeout", "DB_TIMEOUT")</c>.
    /// </summary>
    public GitHubTraceOptions Map(string name, string variableName, GitHubVariableKind kind = GitHubVariableKind.Variable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);
        _mappings[name] = new GitHubMapping(name, variableName, kind, null);
        return this;
    }

    /// <summary>
    /// Reads a workflow file and links every <c>NAME: ${{ vars.X }}</c> or <c>NAME: ${{ secrets.X }}</c> entry it finds,
    /// for example in an <c>env:</c> block, so <c>NAME</c> is traced to <c>X</c>.
    /// </summary>
    public GitHubTraceOptions AddWorkflow(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return AddWorkflowText(File.ReadAllText(path), Path.GetFileName(path));
    }

    /// <summary>Like <see cref="AddWorkflow"/>, for workflow YAML you already have in memory.</summary>
    /// <param name="yaml">The workflow text.</param>
    /// <param name="sourceName">A name for the workflow used in the report, for example <c>deploy.yml</c>.</param>
    public GitHubTraceOptions AddWorkflowText(string yaml, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        var lines = yaml.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = EnvFromExpression().Match(lines[i].TrimEnd('\r'));
            if (!match.Success)
            {
                continue;
            }

            var kind = match.Groups["kind"].Value == "secrets" ? GitHubVariableKind.Secret : GitHubVariableKind.Variable;
            var name = match.Groups["name"].Value;
            var where = string.Create(CultureInfo.InvariantCulture, $"{sourceName} line {i + 1}");

            // A later line (usually a narrower job or step env block) replaces an earlier one with the same name.
            _mappings[name] = new GitHubMapping(name, match.Groups["var"].Value, kind, where);
        }

        return this;
    }

    internal GitHubMapping? FindMapping(string name) => _mappings.GetValueOrDefault(name);

    // NAME: ${{ vars.X }}, name and value optionally quoted, optionally a "- " list item, optionally a trailing comment.
    [GeneratedRegex(
        """^\s*(?:-\s+)?(?<kq>['"]?)(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\k<kq>\s*:\s*(?<q>['"]?)\$\{\{\s*(?<kind>vars|secrets)\.(?<var>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}\k<q>\s*(?:#.*)?$""",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnvFromExpression();
}

/// <summary>An explicit link from a layer name to a GitHub variable or secret.</summary>
/// <param name="Name">The environment variable, env entry or ConfigMap/Secret key name.</param>
/// <param name="VariableName">The GitHub variable or secret name.</param>
/// <param name="Kind">Variable or secret.</param>
/// <param name="Where">The workflow file and line that made the link, or null when it was set with <see cref="GitHubTraceOptions.Map"/>.</param>
public sealed record GitHubMapping(string Name, string VariableName, GitHubVariableKind Kind, string? Where);
