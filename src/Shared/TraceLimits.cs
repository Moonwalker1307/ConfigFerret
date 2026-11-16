namespace ConfigFerret.Shared;

/// <summary>
/// Bounds that ConfigFerret and the packages must agree on, compiled into ConfigFerret and into each package that
/// compiles src/Shared, so that each keeps its own internal copy and no package needs another's internals.
/// </summary>
internal static class TraceLimits
{
    /// <summary>
    /// The most origin steps the tracer keeps for one setting. A resolver that looks at many entries for one step, such as
    /// the GitLab and Azure DevOps ones, looks at no more than this, since the tracer would not keep the rest.
    /// </summary>
    public const int MaxStepsPerSetting = 1_000;
}
