using System.Globalization;
using System.Text.RegularExpressions;
using ConfigFerret.GitHub;

namespace ConfigFerret.Desktop.Services;

/// <summary>
/// The GitHub variables and secrets that workflow files refer to anywhere: in <c>env:</c> blocks, <c>with:</c> inputs of
/// an action, <c>run:</c> scripts and <c>if:</c> conditions. A variable a workflow refers to is in use even when no app
/// setting picks it up, for example a publish profile passed to a deploy action.
/// </summary>
internal sealed partial class WorkflowReferences
{
    private readonly Dictionary<(GitHubVariableKind Kind, string Name), string> _names;

    private WorkflowReferences(Dictionary<(GitHubVariableKind Kind, string Name), string> names) => _names = names;

    /// <summary>Reads <paramref name="paths"/>. A file that cannot be read is skipped; its source reports the problem.</summary>
    public static WorkflowReferences Read(IEnumerable<string> paths)
    {
        var names = new Dictionary<(GitHubVariableKind, string), string>(NameComparer.Instance);
        foreach (var path in paths)
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                continue;
            }

            AddText(text, Path.GetFileName(path), names);
        }

        return new WorkflowReferences(names);
    }

    /// <summary>Like <see cref="Read"/>, for workflow text already in memory.</summary>
    public static WorkflowReferences FromText(params string[] workflows)
    {
        var names = new Dictionary<(GitHubVariableKind, string), string>(NameComparer.Instance);
        foreach (var text in workflows)
        {
            AddText(text, "workflow", names);
        }

        return new WorkflowReferences(names);
    }

    /// <summary>Whether a workflow refers to the variable or secret. GitHub names are not case sensitive.</summary>
    public bool Uses(GitHubVariableKind kind, string name) => _names.ContainsKey((kind, name));

    /// <summary>Where a workflow first refers to the variable or secret, such as <c>deploy.yml line 12</c>, or null.</summary>
    public string? FirstUse(GitHubVariableKind kind, string name) => _names.GetValueOrDefault((kind, name));

    private static void AddText(string text, string sourceName, Dictionary<(GitHubVariableKind, string), string> names)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith('#'))
            {
                continue;
            }

            foreach (Match match in Reference().Matches(line))
            {
                var kind = match.Groups["kind"].Value.Equals("secrets", StringComparison.OrdinalIgnoreCase) ? GitHubVariableKind.Secret : GitHubVariableKind.Variable;
                var name = match.Groups["dot"].Success ? match.Groups["dot"].Value : match.Groups["index"].Value;
                names.TryAdd((kind, name), string.Create(CultureInfo.InvariantCulture, $"{sourceName} line {i + 1}"));
            }
        }
    }

    // secrets.NAME, vars.NAME, secrets['NAME'] or vars["NAME"]. The lookbehind keeps out names such as inputs.my_secrets.X.
    [GeneratedRegex("""(?<![\w.-])(?<kind>secrets|vars)\s*(?:\.\s*(?<dot>[A-Za-z_][A-Za-z0-9_]*)|\[\s*['"](?<index>[A-Za-z_][A-Za-z0-9_]*)['"]\s*\])""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Reference();

    private sealed class NameComparer : IEqualityComparer<(GitHubVariableKind Kind, string Name)>
    {
        public static readonly NameComparer Instance = new();

        public bool Equals((GitHubVariableKind Kind, string Name) x, (GitHubVariableKind Kind, string Name) y) =>
            x.Kind == y.Kind && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((GitHubVariableKind Kind, string Name) obj) =>
            HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}
