using System.Globalization;
using System.Text.RegularExpressions;

namespace ConfigFerret.GitLab;

/// <summary>How to link configuration layers to GitLab CI/CD variables. Can be bound from a configuration section.</summary>
public sealed partial class GitLabTraceOptions
{
    private readonly Dictionary<string, GitLabMapping> _mappings = new(StringComparer.Ordinal);
    private readonly Dictionary<(string? Job, string Name), GitLabMapping> _links = [];
    private readonly Dictionary<string, string> _environments = new(StringComparer.Ordinal);
    private readonly List<GitLabCiVariable> _variables = [];
    private readonly HashSet<string> _readFiles = new(StringComparer.Ordinal);

    /// <summary>
    /// The environment the deployment runs in, for example <c>production</c> or <c>review/feature-1</c>. It picks which
    /// environment-scoped variable applies, and leaves out the CI file jobs whose own <c>environment:</c> names another
    /// environment (never a hidden job, since a job that extends it can name another one). Leave null for a job with no
    /// environment, which gets only <c>*</c> variables; then every job counts.
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>
    /// When true (the default), an environment variable, Kubernetes env entry or ConfigMap/Secret key is linked to a
    /// GitLab variable with the same key. These links are marked as inferred.
    /// </summary>
    public bool MatchByName { get; set; } = true;

    /// <summary>
    /// Explicit links, from layer name (environment variable, env entry or ConfigMap/Secret key) to GitLab variable key.
    /// <see cref="Map"/> adds to it, and it can be bound from a <c>Links</c> configuration section. These win over links
    /// from <c>.gitlab-ci.yml</c>.
    /// </summary>
    public IDictionary<string, string> Links { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// CI files to read, such as <c>.gitlab-ci.yml</c>, relative to the current directory. Bindable from a <c>CiFiles</c>
    /// configuration section. A file that cannot be read becomes a trace warning instead of an error.
    /// </summary>
    public IList<string> CiFiles { get; } = [];

    /// <summary>
    /// The <c>NAME: $VAR</c> and <c>export NAME=$VAR</c> links read from CI files: one link per name, a later line
    /// replacing an earlier one. The trace itself follows each job's own link.
    /// </summary>
    public IReadOnlyCollection<GitLabMapping> Mappings => _mappings.Values;

    /// <summary>The variables set in CI files' <c>variables:</c> blocks, in file order.</summary>
    public IReadOnlyList<GitLabCiVariable> Variables => _variables;

    /// <summary>How many CI files were read, so a resolver knows when what it indexed is out of date.</summary>
    internal int Version { get; private set; }

    /// <summary>
    /// Links the environment variable, Kubernetes env entry or ConfigMap/Secret key <paramref name="name"/> to the GitLab
    /// variable <paramref name="key"/>, for example <c>Map("Db__Timeout", "DB_TIMEOUT")</c>.
    /// </summary>
    public GitLabTraceOptions Map(string name, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Links[name] = key;
        return this;
    }

    /// <summary>
    /// Reads a CI file such as <c>.gitlab-ci.yml</c>: its global and job <c>variables:</c> blocks (<c>NAME: value</c> or
    /// <c>NAME:</c> with <c>value: ...</c>), and every <c>NAME: $VAR</c> or script <c>export NAME=$VAR</c> line elsewhere,
    /// so <c>NAME</c> is traced to <c>VAR</c>. A variable whose whole value is <c>$VAR</c> is traced to <c>VAR</c> too.
    /// What a job sets counts for that job only. What is set outside jobs, in global keywords such as <c>default:</c>,
    /// and in hidden jobs (<c>.name</c>, since <c>extends:</c> is not read) counts for every job; variables from
    /// <c>workflow:</c> rules count only as alternatives, since the trace cannot tell whether a rule matched. Each job's
    /// own <c>environment:</c> is read too, so <see cref="Environment"/> can leave out the jobs for other environments.
    /// </summary>
    public GitLabTraceOptions AddCiFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return AddCiFileText(File.ReadAllText(path), Path.GetFileName(path));
    }

    /// <summary>Like <see cref="AddCiFile"/>, for CI YAML you already have in memory.</summary>
    /// <param name="yaml">The CI file text.</param>
    /// <param name="sourceName">A name for the file used in the report, for example <c>.gitlab-ci.yml</c>.</param>
    public GitLabTraceOptions AddCiFileText(string yaml, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        var lines = yaml.Split('\n');
        string? top = null;
        string? job = null;
        var blockIndent = -1;
        var childIndent = -1;
        (string Key, int Line)? pending = null;
        var environmentIndent = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (YamlLine.IsBlank(line))
            {
                continue;
            }

            var indent = YamlLine.Indent(line);
            var text = line[indent..].TrimEnd();
            if (indent == 0 && TopKey().Match(text) is { Success: true } topKey)
            {
                top = topKey.Groups["quoted"].Success ? YamlLine.Scalar(topKey.Groups["quoted"].Value) : topKey.Groups["key"].Value;
            }

            if (environmentIndent >= 0 && indent <= environmentIndent)
            {
                environmentIndent = -1;
            }

            if (blockIndent >= 0 && indent <= blockIndent)
            {
                blockIndent = -1;
                pending = null;
            }

            if (blockIndent < 0)
            {
                if (VariablesKey().IsMatch(text))
                {
                    blockIndent = indent;
                    childIndent = -1;
                    job = indent == 0 ? null : top;
                }
                else if (indent > 0 && top is not null && KeyValue().Match(text) is { Success: true } setting
                    && (setting.Groups["key"].Value == "environment" || (environmentIndent >= 0 && setting.Groups["key"].Value == "name")))
                {
                    // environment: NAME, environment: { name: NAME }, or environment: with name: NAME below it. A comment
                    // alone leaves the name to the lines below; an alias, a tag such as !reference or an anchor is not
                    // read, so the environment stays unknown.
                    var written = setting.Groups["value"].Value.Trim();
                    if (written.StartsWith('#'))
                    {
                        written = string.Empty;
                    }

                    var environment = written.Length > 0 && written[0] is '*' or '!' or '&' ? null
                        : !written.StartsWith('{') ? YamlLine.Scalar(written)
                        : FlowName().Match(written) is { Success: true } flow ? YamlLine.Scalar(flow.Groups["name"].Value) : null;
                    if (environment is { Length: > 0 })
                    {
                        _environments[top] = environment;
                        environmentIndent = -1;
                    }
                    else if (setting.Groups["key"].Value == "environment" && written.Length == 0)
                    {
                        environmentIndent = indent;
                    }
                }
                else if (FindLink(text) is { } link)
                {
                    // A later line replaces an earlier one with the same name, in Mappings and in the same job.
                    var mapping = new GitLabMapping(link.Name, link.Key, Where(sourceName, i));
                    _mappings[link.Name] = mapping;
                    _links[(indent == 0 ? null : top, link.Name)] = mapping;
                }

                continue;
            }

            if (childIndent < 0)
            {
                childIndent = indent;
            }

            if (indent == childIndent)
            {
                pending = null;
            }

            var pair = KeyValue().Match(text);
            if (!pair.Success)
            {
                continue;
            }

            var key = pair.Groups["key"].Value;
            var value = YamlLine.Scalar(pair.Groups["value"].Value);
            if (indent == childIndent)
            {
                if (value is null)
                {
                    // NAME: with value: and description: below it.
                    pending = (key, i);
                }
                else
                {
                    _variables.Add(new GitLabCiVariable(key, value, sourceName, i + 1, job));
                }
            }
            else if (indent > childIndent && pending is { } open && key == "value")
            {
                _variables.Add(new GitLabCiVariable(open.Key, value ?? string.Empty, sourceName, open.Line + 1, job));
                pending = null;
            }
        }

        Version++;
        return this;
    }

    private static (string Name, string Key)? FindLink(string text)
    {
        if (YamlLink().Match(text) is { Success: true } yaml)
        {
            return (yaml.Groups["name"].Value, yaml.Groups["var"].Value);
        }

        // A script line, possibly a quoted list item: - export NAME="$VAR"
        if (text.StartsWith('-'))
        {
            text = text[1..].Trim();
            if (text.Length > 0 && text[0] is '"' or '\'')
            {
                text = YamlLine.Scalar(text) ?? string.Empty;
            }
        }

        return ScriptLink().Match(text) is { Success: true } script ? (script.Groups["name"].Value, script.Groups["var"].Value) : null;
    }

    private static string Where(string sourceName, int index) => string.Create(CultureInfo.InvariantCulture, $"{sourceName} line {index + 1}");

    internal GitLabMapping? ExplicitLink(string name) =>
        Links.TryGetValue(name, out var link) && !string.IsNullOrWhiteSpace(link)
            ? new GitLabMapping(name, link.Trim().TrimStart('$').Trim('{', '}'), null)
            : null;

    /// <summary>The links from CI files, the last one for each job and name, with the job each one is in (null outside a job).</summary>
    internal IEnumerable<(GitLabMapping Mapping, string? Job)> FileLinks() => _links.Select(l => (l.Value, l.Key.Job));

    /// <summary>
    /// The jobs whose own <c>environment:</c> is a name with no variable in it other than <paramref name="environment"/>.
    /// A job that gets its environment through <c>extends:</c>, or whose name has a variable, is not one of them, and
    /// neither is a hidden job, since a job that extends it can name another environment.
    /// </summary>
    internal IEnumerable<string> JobsForOtherEnvironments(string? environment) =>
        environment is null
            ? []
            : _environments
                .Where(e => !IsHidden(e.Key) && !e.Value.Contains('$', StringComparison.Ordinal) && !string.Equals(e.Value, environment, StringComparison.Ordinal))
                .Select(e => e.Key);

    /// <summary>
    /// Whether what a top-level key of a CI file sets reaches every job: outside any key, and in global keywords such as
    /// <c>default:</c> and <c>workflow:</c>. Jobs and hidden jobs (<c>.name</c>) are not shared.
    /// </summary>
    internal static bool IsShared(string? key) => key is null || NotJobs.Contains(key);

    /// <summary>Whether a top-level key is a hidden job (<c>.name</c>), a template that <c>extends:</c> can use.</summary>
    internal static bool IsHidden(string? key) => key is not null && key.StartsWith('.');

    /// <summary>Reads the files in <see cref="CiFiles"/> once, and returns a warning for each one that cannot be read.</summary>
    internal List<string> ReadConfiguredCiFiles()
    {
        var warnings = new List<string>();
        lock (_readFiles)
        {
            foreach (var path in CiFiles.Where(p => !string.IsNullOrWhiteSpace(p) && !_readFiles.Contains(p)))
            {
                try
                {
                    AddCiFile(path);
                    _readFiles.Add(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    warnings.Add($"Could not read GitLab CI file {path}: {ex.Message}");
                }
            }
        }

        return warnings;
    }

    private static readonly HashSet<string> NotJobs = new(
        ["default", "workflow", "include", "stages", "variables", "image", "services", "cache", "before_script", "after_script", "spec"],
        StringComparer.Ordinal);

    /// <summary>The variable a whole value refers to, such as <c>TIMEOUT</c> for <c>$TIMEOUT</c> or <c>${TIMEOUT}</c>, or null.</summary>
    internal static string? ReferenceTarget(string? value) =>
        value is not null && WholeReference().Match(value) is { Success: true } match ? match.Groups["var"].Value : null;

    // A top-level key ends at the first colon followed by white space or the end of the line, so deploy:staging: is one
    // key; a quoted key is read as a quoted scalar.
    [GeneratedRegex("""^(?:(?<quoted>"(?:[^"\\]|\\.)*"|'(?:[^']|'')*')|(?<key>[^\s:#'"-].*?))\s*:(?=\s|$)""", RegexOptions.CultureInvariant)]
    private static partial Regex TopKey();

    // variables:, optionally with an anchor or a tag such as &defaults.
    [GeneratedRegex("""^variables\s*:(?:\s+[&!]\S+)*\s*(?:#.*)?$""", RegexOptions.CultureInvariant)]
    private static partial Regex VariablesKey();

    [GeneratedRegex("""^(?<kq>['"]?)(?<key>[A-Za-z_][A-Za-z0-9_]*)\k<kq>\s*:(?:\s+(?<value>.*))?$""", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();

    // The name in a flow mapping such as { name: production, url: https://example.com }.
    [GeneratedRegex("""[{,]\s*name\s*:\s*(?<name>"[^"]*"|'[^']*'|[^,}]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex FlowName();

    // NAME: $VAR or NAME: ${VAR}, name and value optionally quoted, optionally a "- " list item, optionally a comment.
    [GeneratedRegex(
        """^(?:-\s+)?(?<kq>['"]?)(?<name>[A-Za-z_][A-Za-z0-9_]*)\k<kq>\s*:\s*(?<q>['"]?)\$(?:\{(?<var>[A-Za-z_][A-Za-z0-9_]*)\}|(?<var>[A-Za-z_][A-Za-z0-9_]*))\k<q>\s*(?:#.*)?$""",
        RegexOptions.CultureInvariant)]
    private static partial Regex YamlLink();

    // export NAME=$VAR, NAME="${VAR}" and so on, as a whole script line.
    [GeneratedRegex(
        """^(?:export\s+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)=(?<q>['"]?)\$(?:\{(?<var>[A-Za-z_][A-Za-z0-9_]*)\}|(?<var>[A-Za-z_][A-Za-z0-9_]*))\k<q>\s*$""",
        RegexOptions.CultureInvariant)]
    private static partial Regex ScriptLink();

    [GeneratedRegex("""^\s*\$(?:\{(?<var>[A-Za-z_][A-Za-z0-9_]*)\}|(?<var>[A-Za-z_][A-Za-z0-9_]*))\s*$""", RegexOptions.CultureInvariant)]
    private static partial Regex WholeReference();
}

/// <summary>A link from a layer name to a GitLab CI/CD variable.</summary>
/// <param name="Name">The environment variable, env entry or ConfigMap/Secret key name.</param>
/// <param name="Key">The GitLab variable key.</param>
/// <param name="Where">The CI file and line that made the link, or null for an explicit link (<see cref="GitLabTraceOptions.Links"/>).</param>
public sealed record GitLabMapping(string Name, string Key, string? Where);

/// <summary>A variable set in a CI file's <c>variables:</c> block.</summary>
/// <param name="Name">The variable name.</param>
/// <param name="Value">The value as written, which may refer to another variable, for example <c>$TIMEOUT</c>.</param>
/// <param name="File">The file name.</param>
/// <param name="Line">The 1-based line.</param>
/// <param name="Job">The job it is set on, or null for a global (default) variable.</param>
public sealed record GitLabCiVariable(string Name, string Value, string File, int Line, string? Job);
