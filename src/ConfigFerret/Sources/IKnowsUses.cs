namespace ConfigFerret.Sources;

/// <summary>
/// A resolver that knows an entry is used even when no trace step links to it, for example a Flux variable written in an
/// Ingress host. <see cref="LoadedConfigSource.FindUnused"/> does not list such an entry.
/// </summary>
internal interface IKnowsUses
{
    /// <summary>True when the entry that <paramref name="reference"/> points at is used.</summary>
    bool IsUsed(SourceRef reference);
}
