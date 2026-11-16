using System.Globalization;
using System.Text.RegularExpressions;

namespace ConfigFerret.Compose;

/// <summary>A key in a dotenv file: a project <c>.env</c>, an <c>env_file</c> or a layered app dotenv file.</summary>
/// <param name="File">The file's full path, or its name when it was parsed from text.</param>
/// <param name="Name">The key.</param>
public sealed record DotEnvEntryRef(string File, string Name) : SourceRef
{
    /// <inheritdoc/>
    protected internal override string? ValueName() => Name;
}

/// <summary>An entry in a Docker Compose service's <c>environment</c>.</summary>
/// <param name="Service">The service name.</param>
/// <param name="Name">The variable name.</param>
public sealed record ComposeEnvironmentRef(string Service, string Name) : SourceRef
{
    /// <inheritdoc/>
    protected internal override string? ValueName() => Name;
}

/// <summary>A Docker Compose secret or config, mounted into a service's containers as a file.</summary>
/// <param name="Name">The top-level secret or config name.</param>
/// <param name="IsSecret">True for a secret, false for a config.</param>
public sealed record ComposeMountRef(string Name, bool IsSecret) : SourceRef;

/// <summary>Builds the provenance steps shared by the dotenv and Compose resolvers.</summary>
internal static class ComposeSteps
{
    // How many ${VAR} hops are followed below a step. Deeper chains are cut off rather than overflowing the stack.
    private const int MaxReferenceDepth = 8;

    private static readonly Regex Sensitive = new(TraceOptions.DefaultSensitiveKeyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>True when a variable's name looks like a secret, by the same rules a trace uses for keys.</summary>
    public static bool IsSensitiveName(string name) => Sensitive.IsMatch(name);

    /// <summary>A step for a dotenv entry, with its <c>${VAR}</c> references as origins.</summary>
    public static ProvenanceStep Entry(DotEnvEntry entry, string source, string? note, int depth = 0)
    {
        var origins = References(entry.References, entry.IsInherited ? null : $"{entry.File} line {entry.Line.ToString(CultureInfo.InvariantCulture)}", depth);
        return new ProvenanceStep
        {
            Kind = LayerKind.DotEnv,
            Source = source,
            Location = string.Create(CultureInfo.InvariantCulture, $"line {entry.Line}"),
            Value = entry.Value,
            IsSensitive = IsSensitiveName(entry.Name) || origins.Exists(o => o.IsSensitive),
            Note = Join(note, entry.IsInherited ? "a bare key, so the value comes from the shell" : null),
            Reference = new DotEnvEntryRef(entry.Path ?? entry.File, entry.Name),
            Origins = origins,
        };
    }

    /// <summary>One step per <c>${VAR}</c> reference: where the variable's value came from.</summary>
    /// <param name="references">The references.</param>
    /// <param name="usedAt">Where the reference is written, for the note, or null for a bare key that takes the variable itself.</param>
    /// <param name="depth">How many references deep this is.</param>
    public static List<ProvenanceStep> References(IReadOnlyList<DotEnvReference> references, string? usedAt, int depth)
    {
        if (depth >= MaxReferenceDepth)
        {
            return [];
        }

        return references.Select(r => Reference(r, usedAt, depth)).ToList();
    }

    private static ProvenanceStep Reference(DotEnvReference reference, string? usedAt, int depth)
    {
        var how = usedAt is null ? null : $"interpolated as ${{{reference.Name}}} in {usedAt}";
        if (reference.From is { } entry)
        {
            return Entry(entry, entry.File, how, depth + 1) with { IsEffective = true };
        }

        return new ProvenanceStep
        {
            Kind = LayerKind.DockerCompose,
            Source = reference.IsSet ? $"shell variable {reference.Name}" : $"variable {reference.Name}, not set",
            Value = reference.Value,
            IsSensitive = IsSensitiveName(reference.Name),
            IsEffective = true,
            Note = Join(how, reference.IsSet ? null : reference.UsedDefault ? "the reference's default is used" : "an empty string is used"),
            Reference = reference.IsSet ? new EnvironmentVariableRef(reference.Name) : null,
        };
    }

    public static string? Join(string? a, string? b) => a is null ? b : b is null ? a : a + "; " + b;
}
