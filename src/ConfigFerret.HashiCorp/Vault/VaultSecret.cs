namespace ConfigFerret.HashiCorp;

/// <summary>One HashiCorp Vault KV secret.</summary>
/// <param name="Path">The path inside the mount, for example <c>orders/prod/db</c>.</param>
/// <param name="RelativePath">
/// The path relative to the configured path it was found under, for example <c>db</c> under <c>orders/prod</c>; empty
/// for the configured path itself. Names are matched against it.
/// </param>
/// <param name="Data">
/// The keys and values inside the secret, when they were read (<see cref="VaultOptions.IncludeValues"/>); null when only
/// the name was read. Values are secret.
/// </param>
/// <param name="Version">The current version (KV version 2), when known.</param>
/// <param name="UpdatedTime">When the secret was last written (KV version 2), when known.</param>
/// <param name="IsDeleted">True when the current version is deleted or destroyed (KV version 2).</param>
public sealed record VaultSecret(
    string Path,
    string RelativePath,
    IReadOnlyDictionary<string, string?>? Data = null,
    long? Version = null,
    DateTimeOffset? UpdatedTime = null,
    bool IsDeleted = false);

/// <summary>A pointer to a HashiCorp Vault KV secret, or one key inside it.</summary>
/// <param name="Mount">The KV mount, for example <c>secret</c>.</param>
/// <param name="Path">The secret's path inside the mount.</param>
/// <param name="Key">The key inside the secret, or null for the whole secret.</param>
public sealed record VaultSecretRef(string Mount, string Path, string? Key = null) : SourceRef;
