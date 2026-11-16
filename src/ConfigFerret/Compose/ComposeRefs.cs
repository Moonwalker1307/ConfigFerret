using System.Globalization;
using System.Runtime.CompilerServices;
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
    // How many ${VAR} hops are followed below a step. Deeper chains are cut off rather than overflowing the stack;
    // whether a step is sensitive still follows every hop.
    private const int MaxReferenceDepth = 8;

    /// <summary>The note on a step whose <c>${VAR}</c> references were not all followed. Its value is still marked secret when one it did not follow leads to a secret.</summary>
    public const string CutNote = "more ${VAR} references are not followed, to keep the trace small";

    private static readonly Regex Sensitive = new(TraceOptions.DefaultSensitiveKeyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // Whether each dotenv entry leads to a secret, worked out once: an entry and its references never change.
    private static readonly ConditionalWeakTable<DotEnvEntry, StrongBox<bool>> LeadsToSecret = new();

    /// <summary>True when a variable's name looks like a secret, by the same rules a trace uses for keys.</summary>
    public static bool IsSensitiveName(string name) => Sensitive.IsMatch(name);

    /// <summary>True when a <c>${VAR}</c> reference names a variable that looks like a secret, or the entry that set it takes its value from one through any number of <c>${VAR}</c> hops.</summary>
    public static bool IsSensitive(DotEnvReference reference) => IsSensitiveName(reference.Name) || (reference.From is { } entry && IsSensitive(entry));

    /// <summary>True when a dotenv entry's name looks like a secret, or its value takes one through any number of <c>${VAR}</c> hops.</summary>
    public static bool IsSensitive(DotEnvEntry entry)
    {
        if (LeadsToSecret.TryGetValue(entry, out var known))
        {
            return known.Value;
        }

        // Depth first without recursion, so a long chain of hops can't overflow the stack. Each entry is walked once.
        var path = new List<(DotEnvEntry Entry, int Next)> { (entry, 0) };
        var onPath = new HashSet<DotEnvEntry>(ReferenceEqualityComparer.Instance) { entry };
        while (path.Count > 0)
        {
            var (current, next) = path[^1];
            var sensitive = next == 0 && IsSensitiveName(current.Name);
            DotEnvEntry? deeper = null;
            while (!sensitive && deeper is null && next < current.References.Count)
            {
                var reference = current.References[next++];
                if (IsSensitiveName(reference.Name))
                {
                    sensitive = true;
                }
                else if (reference.From is not { } from)
                {
                    continue;
                }
                else if (onPath.Contains(from))
                {
                    // A cycle stops the walk, and what stops it counts as a secret.
                    sensitive = true;
                }
                else if (LeadsToSecret.TryGetValue(from, out var done))
                {
                    sensitive = done.Value;
                }
                else
                {
                    deeper = from;
                }
            }

            if (deeper is not null)
            {
                path[^1] = (current, next);
                path.Add((deeper, 0));
                onPath.Add(deeper);
                continue;
            }

            if (sensitive)
            {
                // Every entry on the path takes its value through this one.
                foreach (var (on, _) in path)
                {
                    LeadsToSecret.AddOrUpdate(on, new StrongBox<bool>(true));
                }

                return true;
            }

            LeadsToSecret.AddOrUpdate(current, new StrongBox<bool>(false));
            path.RemoveAt(path.Count - 1);
            onPath.Remove(current);
        }

        return false;
    }

    /// <summary>A step for a dotenv entry, with its <c>${VAR}</c> references as origins.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="source">The step's source.</param>
    /// <param name="note">The step's note.</param>
    /// <param name="budget">How many origin steps may be built, shared with other steps; null for this step alone.</param>
    public static ProvenanceStep Entry(DotEnvEntry entry, string source, string? note, OriginBudget? budget = null)
    {
        budget ??= new OriginBudget();
        budget.StartTree();
        return Entry(entry, source, note, 0, budget);
    }

    /// <summary>One step per <c>${VAR}</c> reference: where the variable's value came from.</summary>
    /// <param name="references">The references.</param>
    /// <param name="usedAt">Where the reference is written, for the note, or null for a bare key that takes the variable itself.</param>
    /// <param name="budget">How many origin steps may be built, shared with other steps.</param>
    /// <param name="cut">True when the budget ran out before every reference had a step.</param>
    public static List<ProvenanceStep> References(IReadOnlyList<DotEnvReference> references, string? usedAt, OriginBudget budget, out bool cut)
    {
        budget.StartTree();
        return References(references, usedAt, 0, budget, out cut);
    }

    private static ProvenanceStep Entry(DotEnvEntry entry, string source, string? note, int depth, OriginBudget budget)
    {
        var origins = References(entry.References, entry.IsInherited ? null : $"{entry.File} line {entry.Line.ToString(CultureInfo.InvariantCulture)}", depth, budget, out var cut);
        return new ProvenanceStep
        {
            Kind = LayerKind.DotEnv,
            Source = source,
            Location = string.Create(CultureInfo.InvariantCulture, $"line {entry.Line}"),
            Value = entry.Value,
            IsSensitive = IsSensitive(entry),
            Note = Join(Join(note, entry.IsInherited ? "a bare key, so the value comes from the shell" : null), cut ? CutNote : null),
            Reference = new DotEnvEntryRef(entry.Path ?? entry.File, entry.Name),
            Origins = origins,
        };
    }

    // Builds the steps depth first, one taken from the budget each, so a file whose values each take several others
    // can't make the tree grow by that factor at every level.
    private static List<ProvenanceStep> References(IReadOnlyList<DotEnvReference> references, string? usedAt, int depth, OriginBudget budget, out bool cut)
    {
        cut = false;
        if (depth >= MaxReferenceDepth)
        {
            return [];
        }

        var steps = new List<ProvenanceStep>(references.Count);
        foreach (var reference in references)
        {
            if (!budget.TryTake())
            {
                cut = true;
                break;
            }

            steps.Add(Reference(reference, usedAt, depth, budget));
        }

        return steps;
    }

    private static ProvenanceStep Reference(DotEnvReference reference, string? usedAt, int depth, OriginBudget budget)
    {
        var how = usedAt is null ? null : $"interpolated as ${{{reference.Name}}} in {usedAt}";
        if (reference.From is { } entry)
        {
            return Entry(entry, entry.File, how, depth + 1, budget) with { IsEffective = true };
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

/// <summary>
/// How many origin steps for <c>${VAR}</c> references may still be built: at most <see cref="MaxStepsPerTree"/> under
/// one step, and at most a total that the steps sharing the budget draw from.
/// </summary>
internal sealed class OriginBudget(int total = int.MaxValue)
{
    /// <summary>The most origin steps built under one step, like a trace's bound per setting.</summary>
    public const int MaxStepsPerTree = 1_000;

    private int _total = total;
    private int _tree;

    /// <summary>Starts the tree under a new step.</summary>
    public void StartTree() => _tree = MaxStepsPerTree;

    /// <summary>Takes one step, or returns false when the tree or the total is used up.</summary>
    public bool TryTake()
    {
        if (_tree == 0 || _total == 0)
        {
            return false;
        }

        _tree--;
        _total--;
        return true;
    }
}
