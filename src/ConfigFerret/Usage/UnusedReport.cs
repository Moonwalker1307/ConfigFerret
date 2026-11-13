namespace ConfigFerret;

/// <summary>
/// A value that a layer sets but that never wins in this trace, because a later layer overrides or removes it.
/// It is possibly unused: it can still win in another environment, or with other values files.
/// </summary>
/// <param name="Key">The configuration key, for example <c>Db:Timeout</c>.</param>
/// <param name="Step">The layer whose value never wins.</param>
/// <param name="WonBy">The layer that overrides or removes it.</param>
/// <param name="Upstream">
/// The winning layers this one sits upstream of, from the .NET layer down, for example the environment variable and
/// ConfigMap that a Helm values file feeds. Empty for a .NET configuration layer.
/// </param>
/// <param name="SameValueAsWinner">
/// True when it sets the same value as <paramref name="WonBy"/>, so it changes nothing here. Always false for sensitive values.
/// </param>
public sealed record OverriddenValue(
    string Key,
    ProvenanceStep Step,
    ProvenanceStep WonBy,
    IReadOnlyList<ProvenanceStep> Upstream,
    bool SameValueAsWinner);

/// <summary>
/// Configuration that is possibly unused: values that never win, and, when reads were recorded, keys the app did not read.
/// Nothing here is proven safe to delete. A value can win in another environment, and a key can be read on a code
/// path that did not run while reads were recorded.
/// </summary>
public sealed class UnusedReport
{
    internal UnusedReport(
        IReadOnlyList<OverriddenValue> neverWinning,
        IReadOnlyList<TracedSetting>? notRead,
        int? recordedReads,
        int skippedEnvironmentKeys)
    {
        NeverWinning = neverWinning;
        NotRead = notRead;
        RecordedReads = recordedReads;
        SkippedEnvironmentKeys = skippedEnvironmentKeys;
    }

    /// <summary>Values that are always overridden or removed by a later layer in this trace, in the order of <see cref="ProvenanceReport.Settings"/>.</summary>
    public IReadOnlyList<OverriddenValue> NeverWinning { get; }

    /// <summary>
    /// Configured keys the app did not read, in the order of <see cref="ProvenanceReport.Settings"/>, each with every layer that sets it.
    /// Null when no reads were given.
    /// </summary>
    public IReadOnlyList<TracedSetting>? NotRead { get; }

    /// <summary>How many distinct keys were recorded as read. Null when no reads were given.</summary>
    public int? RecordedReads { get; }

    /// <summary>
    /// How many unread keys were left out of <see cref="NotRead"/> because they are plain process environment
    /// variables such as <c>PATH</c> or <c>HOME</c>: no <c>__</c> in the name, no prefix, and not set by Kubernetes.
    /// </summary>
    public int SkippedEnvironmentKeys { get; }

    /// <summary>Renders the report as text.</summary>
    public string ToText() => UnusedRenderer.RenderText(this);

    /// <summary>Renders the report as indented JSON.</summary>
    public string ToJson() => UnusedRenderer.RenderJson(this);

    internal static UnusedReport Create(ProvenanceReport report, IEnumerable<string>? readKeys)
    {
        var neverWinning = new List<OverriddenValue>();
        foreach (var setting in report.Settings)
        {
            CollectOverridden(setting.Key, setting.Steps, [], neverWinning);
        }

        if (readKeys is null)
        {
            return new UnusedReport(neverWinning, null, null, 0);
        }

        var reads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in readKeys)
        {
            reads.Add(key.Replace("__", ":", StringComparison.Ordinal));
        }

        var notRead = new List<TracedSetting>();
        var skipped = 0;
        foreach (var setting in report.Settings)
        {
            if (reads.Contains(setting.Key))
            {
                continue;
            }

            if (IsPlainEnvironmentVariable(setting))
            {
                skipped++;
            }
            else
            {
                notRead.Add(setting);
            }
        }

        return new UnusedReport(neverWinning, notRead, reads.Count, skipped);
    }

    private static void CollectOverridden(
        string key,
        IReadOnlyList<ProvenanceStep> steps,
        IReadOnlyList<ProvenanceStep> upstream,
        List<OverriddenValue> result)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.IsEffective)
            {
                // Only the winning branch matters: everything upstream of a losing step loses with it.
                CollectOverridden(key, step.Origins, [.. upstream, step], result);
                continue;
            }

            // A removal sets nothing, for example the marker for a Kustomize replace, so there is no value to drop.
            if (step.IsRemoval)
            {
                continue;
            }

            var winner = FindWinner(steps, i);
            if (winner is not null)
            {
                result.Add(new OverriddenValue(key, step, winner, upstream, SameValue(step, winner)));
            }
        }
    }

    // Origins from several resolvers sit in one list, each chain ending in its own winner, so a losing step is beaten
    // by the next step that wins or removes the value rather than by the last step in the list.
    private static ProvenanceStep? FindWinner(IReadOnlyList<ProvenanceStep> steps, int index)
    {
        for (var j = index + 1; j < steps.Count; j++)
        {
            if (steps[j].IsEffective || steps[j].IsRemoval)
            {
                return steps[j];
            }
        }

        return null;
    }

    private static bool SameValue(ProvenanceStep step, ProvenanceStep winner) =>
        !step.IsSensitive && !winner.IsSensitive && !step.IsRemoval && !winner.IsRemoval
        && step.Value is not null && string.Equals(step.Value, winner.Value, StringComparison.Ordinal);

    private static bool IsPlainEnvironmentVariable(TracedSetting setting) =>
        !setting.Key.Contains(':', StringComparison.Ordinal)
        && setting.Steps.Count > 0
        && setting.Steps.All(s => s.Origins.Count == 0
            && s.Reference is EnvironmentVariableRef env
            && string.Equals(env.Name, setting.Key, StringComparison.OrdinalIgnoreCase));
}
