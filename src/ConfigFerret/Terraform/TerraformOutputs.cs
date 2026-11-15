using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConfigFerret.Terraform;

/// <summary>
/// Terraform outputs, read from <c>terraform output -json</c> or from the <c>outputs</c> of a state file (version 4).
/// Object and list outputs are also split into their nested values, for example <c>db.host</c> and <c>subnets.0</c>.
/// </summary>
/// <remarks>
/// Only outputs are read. From a state file every other part, including resource attributes, which hold secrets, is
/// skipped without being parsed into memory. Sensitive outputs keep their values (a trace redacts them unless secrets
/// are revealed), and <see cref="TerraformOutput.ToString"/> leaves them out.
/// </remarks>
public sealed partial class TerraformOutputs
{
    private readonly List<TerraformOutput> _values = [];
    private readonly Dictionary<string, TerraformDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];

    private TerraformOutputs()
    {
    }

    /// <summary>Every output and every value nested in an object or list output, in file order (each output before its nested values).</summary>
    public IReadOnlyList<TerraformOutput> Values => _values;

    /// <summary>Where each output is defined in the <c>.tf</c> files, by output name, when a Terraform folder was read.</summary>
    public IReadOnlyDictionary<string, TerraformDefinition> Definitions => _definitions;

    /// <summary>Problems found while reading, such as a <c>.tf</c> file that could not be read.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Reads the output of <c>terraform output -json</c>: an object of <c>name: { value, type, sensitive }</c>.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="sourceName">A name for the text, used in output (for example a file name).</param>
    /// <exception cref="FormatException">The text is not valid JSON or not an object of outputs.</exception>
    public static TerraformOutputs Parse(string json, string sourceName = "terraform output -json")
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        var result = new TerraformOutputs();
        try
        {
            using var doc = JsonDocument.Parse(json);
            result.AddOutputs(doc.RootElement, sourceName);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Could not parse Terraform outputs in {sourceName}: {ex.Message}", ex);
        }

        return result;
    }

    /// <summary>Reads the <c>outputs</c> of a Terraform state file (format version 4), and nothing else in it.</summary>
    /// <param name="json">The state file text.</param>
    /// <param name="sourceName">A name for the text, used in output (for example a file name).</param>
    /// <exception cref="FormatException">The text is not valid JSON, or not a version 4 state file.</exception>
    public static TerraformOutputs ParseState(string json, string sourceName = "terraform.tfstate")
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        var result = new TerraformOutputs();
        result.AddState(Encoding.UTF8.GetBytes(json), sourceName);
        return result;
    }

    /// <summary>
    /// Reads outputs from the files given. Outputs from <paramref name="outputsFile"/> win over those in
    /// <paramref name="stateFile"/>. With only a folder, its <c>terraform.tfstate</c> is read. The folder's <c>.tf</c>
    /// files are scanned for <c>output "name" { ... }</c> blocks, to say where each output is defined.
    /// </summary>
    /// <param name="outputsFile">A file holding the output of <c>terraform output -json</c>.</param>
    /// <param name="stateFile">A state file (version 4), for example <c>terraform.tfstate</c>. Only its outputs are read.</param>
    /// <param name="terraformFolder">The Terraform root module folder.</param>
    /// <exception cref="ArgumentException">No file was given, and the folder has no <c>terraform.tfstate</c>.</exception>
    /// <exception cref="FormatException">A file is not valid.</exception>
    public static TerraformOutputs Load(string? outputsFile = null, string? stateFile = null, string? terraformFolder = null)
    {
        if (string.IsNullOrWhiteSpace(stateFile) && string.IsNullOrWhiteSpace(outputsFile) && !string.IsNullOrWhiteSpace(terraformFolder))
        {
            var local = Path.Combine(terraformFolder, "terraform.tfstate");
            stateFile = File.Exists(local)
                ? local
                : throw new ArgumentException($"{terraformFolder} has no terraform.tfstate; pass the output of terraform output -json or a state file.", nameof(terraformFolder));
        }

        if (string.IsNullOrWhiteSpace(stateFile) && string.IsNullOrWhiteSpace(outputsFile))
        {
            throw new ArgumentException("Pass the output of terraform output -json, a state file, or a Terraform folder.", nameof(outputsFile));
        }

        var result = new TerraformOutputs();
        if (!string.IsNullOrWhiteSpace(outputsFile))
        {
            var outputs = Parse(File.ReadAllText(outputsFile), Path.GetFileName(outputsFile));
            result._values.AddRange(outputs._values);
            result._warnings.AddRange(outputs._warnings);
        }

        if (!string.IsNullOrWhiteSpace(stateFile))
        {
            var state = new TerraformOutputs();
            state.AddState(File.ReadAllBytes(stateFile), Path.GetFileName(stateFile));
            var known = result._values.Select(v => v.Output).ToHashSet(StringComparer.Ordinal);
            result._values.AddRange(state._values.Where(v => !known.Contains(v.Output)));
            result._warnings.AddRange(state._warnings);
        }

        if (!string.IsNullOrWhiteSpace(terraformFolder))
        {
            result.AddDefinitions(terraformFolder);
        }

        return result;
    }

    /// <summary>Finds an output or nested value by name, for example <c>db_timeout</c>, <c>db.host</c> or <c>db:host</c>.</summary>
    public TerraformOutput? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var wanted = name.Replace(':', '.');
        return _values.FirstOrDefault(v => string.Equals(v.Name, wanted, StringComparison.Ordinal))
            ?? _values.FirstOrDefault(v => string.Equals(v.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Scans the <c>.tf</c> files in a folder (not its subfolders, which are other modules) for output blocks.</summary>
    private void AddDefinitions(string folder)
    {
        if (!Directory.Exists(folder))
        {
            _warnings.Add($"The Terraform folder {folder} does not exist.");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.tf").Order(StringComparer.Ordinal))
        {
            try
            {
                AddDefinitions(File.ReadAllText(file), Path.GetFileName(file));
            }
            catch (IOException ex)
            {
                _warnings.Add($"Could not read {file}: {ex.Message}");
            }
        }
    }

    /// <summary>Finds the <c>output "name" { value = ... }</c> blocks in one <c>.tf</c> file.</summary>
    internal void AddDefinitions(string text, string fileName)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = OutputBlock().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["name"].Value;
            string? expression = null;
            var depth = Depth(lines[i]);
            for (var j = i + 1; j < lines.Length && depth > 0 && expression is null; j++)
            {
                var value = ValueLine().Match(lines[j]);
                if (value.Success && depth == 1)
                {
                    // A multi-line object or list is left out: its first line says nothing.
                    var first = value.Groups["expr"].Value;
                    expression = Balanced(first) ? first : "";
                }

                depth += Depth(lines[j]);
            }

            _definitions.TryAdd(name, new TerraformDefinition(name, fileName, i + 1, string.IsNullOrEmpty(expression) ? null : expression));
        }
    }

    private static int Depth(string line) => line.Count(c => c == '{') - line.Count(c => c == '}');

    private static bool Balanced(string text) =>
        text.Count(c => c is '{' or '[' or '(') == text.Count(c => c is '}' or ']' or ')');

    private void AddOutputs(JsonElement root, string sourceName)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"Terraform outputs in {sourceName} must be a JSON object of outputs.");
        }

        foreach (var output in root.EnumerateObject())
        {
            if (output.Value.ValueKind != JsonValueKind.Object || !output.Value.TryGetProperty("value", out var value))
            {
                _warnings.Add($"Terraform output {output.Name} in {sourceName} has no value.");
                continue;
            }

            var sensitive = output.Value.TryGetProperty("sensitive", out var s) && s.ValueKind == JsonValueKind.True;
            var type = output.Value.TryGetProperty("type", out var t) ? Render(t) : null;
            Flatten(output.Name, null, value, type, sensitive, sourceName);
        }
    }

    private void Flatten(string output, string? path, JsonElement value, string? type, bool sensitive, string sourceName)
    {
        var children = value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Select(p => (Key: p.Name, Value: p.Value)).ToList(),
            JsonValueKind.Array => value.EnumerateArray().Select((v, i) => (Key: i.ToString(CultureInfo.InvariantCulture), Value: v)).ToList(),
            _ => [],
        };
        _values.Add(new TerraformOutput(output, path, Render(value), path is null ? type : null, sensitive, sourceName) { HasNestedValues = children.Count > 0 });
        foreach (var (key, child) in children)
        {
            Flatten(output, path is null ? key : path + "." + key, child, type, sensitive, sourceName);
        }
    }

    private static string? Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        _ => JsonSerializer.Serialize(value),
    };

    private void AddState(byte[] json, string sourceName)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new FormatException($"The Terraform state {sourceName} is not a JSON object.");
            }

            int? version = null;
            JsonDocument? outputs = null;
            try
            {
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var property = reader.GetString();
                    reader.Read();
                    if (property == "version" && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v))
                    {
                        version = v;
                    }
                    else if (property == "outputs" && reader.TokenType == JsonTokenType.StartObject && outputs is null)
                    {
                        outputs = JsonDocument.ParseValue(ref reader);
                    }
                    else
                    {
                        // Resources, check results and everything else: skipped token by token, never parsed into values.
                        reader.Skip();
                    }
                }

                if (version != 4)
                {
                    throw new FormatException($"The Terraform state {sourceName} has format version {version?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}; only version 4 is read.");
                }

                if (outputs is not null)
                {
                    AddOutputs(outputs.RootElement, sourceName);
                }
            }
            finally
            {
                outputs?.Dispose();
            }
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Could not parse the Terraform state {sourceName}: {ex.Message}", ex);
        }
    }

    [GeneratedRegex("""^\s*output\s+"(?<name>[^"]+)"\s*\{""", RegexOptions.CultureInvariant)]
    private static partial Regex OutputBlock();

    [GeneratedRegex(@"^\s*value\s*=\s*(?<expr>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValueLine();
}

/// <summary>One Terraform output, or a value nested in an object or list output.</summary>
/// <param name="Output">The output name, for example <c>db</c>.</param>
/// <param name="Path">The path inside the output, for example <c>host</c> or <c>subnets.0</c>; null for the output itself.</param>
/// <param name="Value">The value: strings as they are, numbers and booleans as JSON, objects and lists as compact JSON. Null for <c>null</c>.</param>
/// <param name="Type">The Terraform type as JSON, for example <c>"string"</c> or <c>["object",{...}]</c>, on the output itself.</param>
/// <param name="IsSensitive">True when the output is marked <c>sensitive</c>. Nested values of a sensitive output are sensitive too.</param>
/// <param name="Source">The file it was read from.</param>
public sealed record TerraformOutput(string Output, string? Path, string? Value, string? Type, bool IsSensitive, string Source)
{
    /// <summary>The name, for example <c>db_timeout</c> or <c>db.host</c>.</summary>
    public string Name => Path is null ? Output : Output + "." + Path;

    /// <summary>True for an object or list with values nested in it, which are listed separately.</summary>
    public bool HasNestedValues { get; init; }

    /// <summary>Describes the output without its value when it is sensitive.</summary>
    public override string ToString() =>
        $"Terraform output {Name} = {(IsSensitive ? "(sensitive)" : Value ?? "null")}";
}

/// <summary>Where an output is defined in the Terraform code.</summary>
/// <param name="Name">The output name.</param>
/// <param name="File">The <c>.tf</c> file name.</param>
/// <param name="Line">The line of the <c>output</c> block.</param>
/// <param name="Expression">The first line of its <c>value</c> expression, for example <c>aws_db_instance.orders.address</c>.</param>
public sealed record TerraformDefinition(string Name, string File, int Line, string? Expression);

/// <summary>A pointer to a Terraform output or a value nested in one.</summary>
/// <param name="Output">The output name.</param>
/// <param name="Path">The path inside the output, or null for the output itself.</param>
public sealed record TerraformOutputRef(string Output, string? Path = null) : SourceRef;
