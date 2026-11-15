using ConfigFerret.Sources;

namespace ConfigFerret.Terraform;

/// <summary>Terraform outputs read from local files, as an <see cref="IConfigSource"/>. Its entries are the outputs and their nested values.</summary>
public sealed class TerraformSource : IConfigSource
{
    /// <summary>Creates the source. Nothing is read until <see cref="LoadAsync"/>; see <see cref="TerraformOutputs.Load"/> for the files.</summary>
    /// <param name="outputsFile">A file holding the output of <c>terraform output -json</c>.</param>
    /// <param name="stateFile">A state file (version 4). Only its outputs are read.</param>
    /// <param name="terraformFolder">The Terraform root module folder, scanned for <c>output</c> blocks.</param>
    /// <param name="traceOptions">How to link layers to outputs.</param>
    public TerraformSource(string? outputsFile = null, string? stateFile = null, string? terraformFolder = null, TerraformTraceOptions? traceOptions = null)
    {
        OutputsFile = string.IsNullOrWhiteSpace(outputsFile) ? null : outputsFile;
        StateFile = string.IsNullOrWhiteSpace(stateFile) ? null : stateFile;
        TerraformFolder = string.IsNullOrWhiteSpace(terraformFolder) ? null : terraformFolder;
        if (OutputsFile is null && StateFile is null && TerraformFolder is null)
        {
            throw new ArgumentException("Pass the output of terraform output -json, a state file, or a Terraform folder.", nameof(outputsFile));
        }

        TraceOptions = traceOptions ?? new TerraformTraceOptions();
    }

    /// <summary>The file holding the output of <c>terraform output -json</c>, when one was given.</summary>
    public string? OutputsFile { get; }

    /// <summary>The state file, when one was given.</summary>
    public string? StateFile { get; }

    /// <summary>The Terraform root module folder, when one was given.</summary>
    public string? TerraformFolder { get; }

    /// <summary>How to link layers to outputs.</summary>
    public TerraformTraceOptions TraceOptions { get; }

    /// <inheritdoc />
    public string DisplayName => $"Terraform outputs {Path.GetFileName(OutputsFile ?? StateFile ?? Path.TrimEndingDirectorySeparator(TerraformFolder!))}";

    /// <inheritdoc />
    /// <exception cref="FormatException">A file is not valid.</exception>
    public Task<LoadedConfigSource> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outputs = TerraformOutputs.Load(OutputsFile, StateFile, TerraformFolder);
        var entries = outputs.Values
            .Where(v => !v.HasNestedValues)
            .Select(v => new SourceEntry(v.Name, new TerraformOutputRef(v.Output, v.Path), v.IsSensitive, v.Type, v.Source));
        return Task.FromResult(new LoadedConfigSource(DisplayName, new TerraformResolver(outputs, TraceOptions), entries, outputs.Warnings));
    }
}

/// <summary>Creates a <see cref="TerraformSource"/>. Id <c>terraform-outputs</c>.</summary>
public sealed class TerraformSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Id => "terraform-outputs";

    /// <inheritdoc />
    public string DisplayName => "Terraform outputs";

    /// <inheritdoc />
    public SourceCategory Category => SourceCategory.GitOps;

    /// <inheritdoc />
    public IReadOnlyList<SourceSetting> Settings { get; } =
    [
        new("OutputsFile", "Outputs file", SourceSettingKind.Path, Help: "A file holding the output of terraform output -json."),
        new("StateFile", "State file", SourceSettingKind.Path, Help: "A Terraform state file (version 4). Only its outputs are read, never resource attributes."),
        new("TerraformFolder", "Terraform folder", SourceSettingKind.Path, Help: "The root module folder, to show where each output is defined. Its terraform.tfstate is read when no file is given."),
        new("Links", "Links", SourceSettingKind.Lines, Help: "Explicit links, one NAME=output per line, for example ORDERS_DB_HOST=db.host."),
    ];

    /// <inheritdoc />
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var outputsFile = Existing(values, "OutputsFile", File.Exists);
        var stateFile = Existing(values, "StateFile", File.Exists);
        var folder = Existing(values, "TerraformFolder", Directory.Exists);
        if (outputsFile is null && stateFile is null && folder is null)
        {
            throw new ArgumentException("Set OutputsFile, StateFile or TerraformFolder.", nameof(values));
        }

        var options = new TerraformTraceOptions();
        var links = values.GetValueOrDefault("Links") ?? "";
        foreach (var line in links.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || eq == line.Length - 1)
            {
                throw new ArgumentException($"The link '{line}' is not NAME=output.", nameof(values));
            }

            options.Map(line[..eq].Trim(), line[(eq + 1)..].Trim());
        }

        return new TerraformSource(outputsFile, stateFile, folder, options);
    }

    private static string? Existing(IReadOnlyDictionary<string, string?> values, string name, Func<string, bool> exists)
    {
        var value = values.GetValueOrDefault(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        return exists(value) ? value : throw new ArgumentException($"{name} {value} does not exist.", nameof(values));
    }
}
