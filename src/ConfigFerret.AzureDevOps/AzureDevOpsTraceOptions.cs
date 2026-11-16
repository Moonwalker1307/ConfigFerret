using System.Globalization;
using System.Text.RegularExpressions;

namespace ConfigFerret.AzureDevOps;

/// <summary>How to link configuration layers to Azure Pipelines variables. Can be bound from a configuration section.</summary>
public sealed partial class AzureDevOpsTraceOptions
{
    private readonly Dictionary<string, AzurePipelinesMapping> _mappings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(PipelineScope Scope, string Name), AzurePipelinesMapping> _links = [];
    private readonly List<AzurePipelinesVariable> _variables = [];
    private readonly List<AzurePipelinesGroupLink> _groups = [];
    private readonly Dictionary<object, PipelineScope> _scopes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _readPipelines = new(StringComparer.Ordinal);
    private int _order;

    /// <summary>
    /// When true (the default), an environment variable, Kubernetes env entry or ConfigMap/Secret key is linked to a
    /// variable with the same name, or with the name Azure Pipelines turns into it in scripts (<c>Db.Timeout</c> becomes
    /// <c>DB_TIMEOUT</c>). These links are marked as inferred.
    /// </summary>
    public bool MatchByName { get; set; } = true;

    /// <summary>
    /// Explicit links, from layer name (environment variable, env entry or ConfigMap/Secret key) to variable name. Write
    /// <c>Group/Variable</c> to pick one group's variable. <see cref="Map"/> adds to it, and it can be bound from a
    /// <c>Links</c> configuration section. These win over links from pipeline files.
    /// </summary>
    public IDictionary<string, string> Links { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pipeline YAML files to read, relative to the current directory. Bindable from a <c>Pipelines</c> configuration
    /// section. A file that cannot be read becomes a trace warning instead of an error.
    /// </summary>
    public IList<string> Pipelines { get; } = [];

    /// <summary>
    /// The <c>NAME: $(Var)</c> links read from pipeline files: one link per name, ignoring case, a later line replacing
    /// an earlier one. The trace itself follows each stage's or job's own link.
    /// </summary>
    public IReadOnlyCollection<AzurePipelinesMapping> Mappings => _mappings.Values;

    /// <summary>The variables set in pipeline files (<c>- name: N</c> with <c>value: V</c>, or <c>N: V</c>), in file order.</summary>
    public IReadOnlyList<AzurePipelinesVariable> Variables => _variables;

    /// <summary>The variable groups pipeline files link with <c>- group: NAME</c>, in file order.</summary>
    public IReadOnlyList<AzurePipelinesGroupLink> GroupLinks => _groups;

    /// <summary>How many pipeline files were read, so a resolver knows when what it indexed is out of date.</summary>
    internal int Version { get; private set; }

    /// <summary>
    /// Links the environment variable, Kubernetes env entry or ConfigMap/Secret key <paramref name="name"/> to the variable
    /// <paramref name="variableName"/>, optionally in one <paramref name="group"/>. For example
    /// <c>Map("Db__Password", "Db.Password", "orders-prod")</c>.
    /// </summary>
    public AzureDevOpsTraceOptions Map(string name, string variableName, string? group = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);
        Links[name] = string.IsNullOrWhiteSpace(group) ? variableName : $"{group}/{variableName}";
        return this;
    }

    /// <summary>
    /// Reads a pipeline YAML file: its <c>variables:</c> blocks (<c>- group: NAME</c>, <c>- name: N</c> with
    /// <c>value: V</c>, and <c>N: V</c>), and every <c>NAME: $(Var)</c> line elsewhere, for example in a step's
    /// <c>env:</c> block, so <c>NAME</c> is traced to <c>Var</c>. <c>$[variables.Var]</c> and
    /// <c>${{ variables.Var }}</c> work too. The stage and job each one is in is read as well, so that a job only gets
    /// the pipeline's variables and groups, its stage's and its own.
    /// </summary>
    public AzureDevOpsTraceOptions AddPipeline(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return AddPipelineText(File.ReadAllText(path), Path.GetFileName(path));
    }

    /// <summary>Like <see cref="AddPipeline"/>, for pipeline YAML you already have in memory.</summary>
    /// <param name="yaml">The pipeline text.</param>
    /// <param name="sourceName">A name for the file used in the report, for example <c>azure-pipelines.yml</c>.</param>
    public AzureDevOpsTraceOptions AddPipelineText(string yaml, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        var lines = yaml.Split('\n');
        var blockIndent = -1;
        Item? item = null;
        var parts = new List<(int Indent, bool Stage, string Name)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (YamlLine.IsBlank(line))
            {
                continue;
            }

            var indent = YamlLine.Indent(line);
            var text = line[indent..].TrimEnd();

            // The stage and job a line is in: - stage: NAME, - job: NAME and - deployment: NAME items.
            parts.RemoveAll(p => indent <= p.Indent);
            if (Part().Match(text) is { Success: true } part && YamlLine.Scalar(part.Groups["name"].Value) is { Length: > 0 } partName)
            {
                parts.Add((indent, part.Groups["kind"].Value == "stage", partName));
            }

            var scope = new PipelineScope(parts.FindLast(p => p.Stage).Name, parts.FindLast(p => !p.Stage).Name);
            if (blockIndent >= 0 && (indent < blockIndent || (indent == blockIndent && text[0] != '-')))
            {
                Flush(item, sourceName);
                item = null;
                blockIndent = -1;
            }

            if (blockIndent < 0)
            {
                if (VariablesKey().IsMatch(text))
                {
                    blockIndent = indent;
                }
                else if (Link().Match(text) is { Success: true } link)
                {
                    // A later line replaces an earlier one with the same name, in Mappings and in the same stage or job.
                    var name = link.Groups["name"].Value;
                    var mapping = new AzurePipelinesMapping(name, link.Groups["var"].Value, null, Where(sourceName, i));
                    _mappings[name] = mapping;
                    _links[(scope, name.ToUpperInvariant())] = mapping;
                }

                continue;
            }

            if (text[0] == '-')
            {
                Flush(item, sourceName);
                item = new Item(scope);
                text = text[1..].TrimStart();
            }

            var pair = KeyValue().Match(text);
            if (!pair.Success)
            {
                continue;
            }

            var key = pair.Groups["key"].Value;
            var value = YamlLine.Scalar(pair.Groups["value"].Value);
            if (item is not null)
            {
                item.Fields[key] = (value, i);
            }
            else if (value is not null)
            {
                _variables.Add(Scoped(new AzurePipelinesVariable(key, value, sourceName, i + 1, _order++), scope));
            }
        }

        Flush(item, sourceName);
        Version++;
        return this;
    }

    private void Flush(Item? item, string sourceName)
    {
        if (item is null)
        {
            return;
        }

        if (item.Fields.TryGetValue("group", out var group) && !string.IsNullOrWhiteSpace(group.Value))
        {
            _groups.Add(Scoped(new AzurePipelinesGroupLink(group.Value, Where(sourceName, group.Line), _order++), item.Scope));
        }
        else if (item.Fields.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name.Value) && VariableName().IsMatch(name.Value))
        {
            var value = item.Fields.TryGetValue("value", out var v) ? v.Value : null;
            _variables.Add(Scoped(new AzurePipelinesVariable(name.Value, value ?? string.Empty, sourceName, name.Line + 1, _order++), item.Scope));
        }
    }

    private T Scoped<T>(T read, PipelineScope scope)
        where T : notnull
    {
        _scopes[read] = scope;
        return read;
    }

    private static string Where(string sourceName, int index) => string.Create(CultureInfo.InvariantCulture, $"{sourceName} line {index + 1}");

    internal AzurePipelinesMapping? ExplicitLink(string name)
    {
        if (!Links.TryGetValue(name, out var link) || string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        link = link.Trim();
        var slash = link.LastIndexOf('/');
        return slash > 0 && slash < link.Length - 1
            ? new AzurePipelinesMapping(name, link[(slash + 1)..].Trim(), link[..slash].Trim(), null)
            : new AzurePipelinesMapping(name, link.Trim('/'), null, null);
    }

    /// <summary>The links from pipeline files, the last one for each stage or job and name, with the stage or job each one is in.</summary>
    internal IEnumerable<(AzurePipelinesMapping Mapping, PipelineScope Scope)> FileLinks() => _links.Select(l => (l.Value, l.Key.Scope));

    /// <summary>The stage and job a pipeline variable or <c>- group:</c> line is in.</summary>
    internal PipelineScope ScopeOf(object read) => _scopes.GetValueOrDefault(read);

    /// <summary>Reads the files in <see cref="Pipelines"/> once, and returns a warning for each one that cannot be read.</summary>
    internal List<string> ReadConfiguredPipelines()
    {
        var warnings = new List<string>();
        lock (_readPipelines)
        {
            foreach (var path in Pipelines.Where(p => !string.IsNullOrWhiteSpace(p) && !_readPipelines.Contains(p)))
            {
                try
                {
                    AddPipeline(path);
                    _readPipelines.Add(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    warnings.Add($"Could not read Azure Pipelines file {path}: {ex.Message}");
                }
            }
        }

        return warnings;
    }

    /// <summary>The variable a whole value refers to, such as <c>Shared.Timeout</c> for <c>$(Shared.Timeout)</c>, or null.</summary>
    internal static string? MacroTarget(string? value) =>
        value is not null && WholeMacro().Match(value) is { Success: true } match ? match.Groups["var"].Value : null;

    private sealed class Item(PipelineScope scope)
    {
        public PipelineScope Scope { get; } = scope;

        public Dictionary<string, (string? Value, int Line)> Fields { get; } = new(StringComparer.Ordinal);
    }

    [GeneratedRegex("""^-\s+(?<kind>stage|job|deployment)\s*:(?:\s+(?<name>.*))?$""", RegexOptions.CultureInvariant)]
    private static partial Regex Part();

    [GeneratedRegex("""^variables\s*:\s*(?:#.*)?$""", RegexOptions.CultureInvariant)]
    private static partial Regex VariablesKey();

    // KEY: VALUE inside a variables block. Keys that are template expressions (${{ if ... }}) do not match.
    [GeneratedRegex("""^(?<kq>['"]?)(?<key>[A-Za-z_][A-Za-z0-9_.]*)\k<kq>\s*:(?:\s+(?<value>.*))?$""", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();

    [GeneratedRegex("""^[A-Za-z_][A-Za-z0-9_.]*$""", RegexOptions.CultureInvariant)]
    private static partial Regex VariableName();

    // NAME: $(Var), NAME: $[variables.Var], NAME: ${{ variables.Var }}, or variables['Var'] in either expression,
    // name and value optionally quoted, optionally a "- " list item, optionally a trailing comment.
    [GeneratedRegex(
        """^(?:-\s+)?(?<kq>['"]?)(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\k<kq>\s*:\s*(?<q>['"]?)(?:\$\((?<var>[A-Za-z_][A-Za-z0-9_.]*)\)|\$\[\s*variables(?:\.(?<var>[A-Za-z_][A-Za-z0-9_.]*)|\[\s*'(?<var>[A-Za-z_][A-Za-z0-9_.]*)'\s*\])\s*\]|\$\{\{\s*variables(?:\.(?<var>[A-Za-z_][A-Za-z0-9_.]*)|\[\s*'(?<var>[A-Za-z_][A-Za-z0-9_.]*)'\s*\])\s*\}\})\k<q>\s*(?:#.*)?$""",
        RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex("""^\s*\$\((?<var>[A-Za-z_][A-Za-z0-9_.]*)\)\s*$""", RegexOptions.CultureInvariant)]
    private static partial Regex WholeMacro();
}

/// <summary>A link from a layer name to an Azure Pipelines variable.</summary>
/// <param name="Name">The environment variable, env entry or ConfigMap/Secret key name.</param>
/// <param name="VariableName">The Azure Pipelines variable name.</param>
/// <param name="Group">The variable group to look in, or null for any group or pipeline variable.</param>
/// <param name="Where">The pipeline file and line that made the link, or null for an explicit link (<see cref="AzureDevOpsTraceOptions.Links"/>).</param>
public sealed record AzurePipelinesMapping(string Name, string VariableName, string? Group, string? Where);

/// <summary>A variable set in pipeline YAML.</summary>
/// <param name="Name">The variable name.</param>
/// <param name="Value">The value as written, which may refer to another variable, for example <c>$(Shared.Timeout)</c>.</param>
/// <param name="File">The file name.</param>
/// <param name="Line">The 1-based line.</param>
/// <param name="Order">Its position among the variables and groups read, used for precedence: a later one wins.</param>
public sealed record AzurePipelinesVariable(string Name, string Value, string File, int Line, int Order);

/// <summary>A <c>- group: NAME</c> line in pipeline YAML.</summary>
/// <param name="Group">The group name.</param>
/// <param name="Where">The file and line.</param>
/// <param name="Order">Its position among the variables and groups read, used for precedence: a later one wins.</param>
public sealed record AzurePipelinesGroupLink(string Group, string Where, int Order);

/// <summary>The stage and job a pipeline file line is in; both null at the pipeline's top level.</summary>
internal readonly record struct PipelineScope(string? Stage, string? Job)
{
    public bool IsPipeline => Stage is null && Job is null;

    /// <summary>Whether a job in <paramref name="other"/> gets what is set here: this is the pipeline, its stage or itself.</summary>
    public bool Covers(PipelineScope other) =>
        (Stage is null || string.Equals(Stage, other.Stage, StringComparison.OrdinalIgnoreCase))
        && (Job is null || string.Equals(Job, other.Job, StringComparison.OrdinalIgnoreCase));

    public override string ToString() =>
        IsPipeline ? "the pipeline" : Job is null ? $"stage {Stage}" : Stage is null ? $"job {Job}" : $"job {Job} in stage {Stage}";
}
