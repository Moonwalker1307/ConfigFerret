namespace ConfigFerret.HashiCorp;

/// <summary>
/// Which HashiCorp Vault KV secrets to read, and how to sign in. Can be bound from a configuration section, see
/// <c>services.AddConfigFerretVault(...)</c>.
/// </summary>
public sealed class VaultOptions
{
    /// <summary>The Vault address, for example <c>https://vault.example.com:8200/</c>. The same as <c>VAULT_ADDR</c>.</summary>
    public Uri? Address { get; set; }

    /// <summary>
    /// A Vault token (the same as <c>VAULT_TOKEN</c>) whose policy allows <c>list</c> and <c>read</c> on the paths below,
    /// see the readme. Read it from user secrets, an environment variable or a key vault; never hard-code it or put it in
    /// a committed appsettings file. ConfigFerret sends it only in the <c>X-Vault-Token</c> header and never writes it to
    /// a report or exception.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>The Vault Enterprise namespace, sent as <c>X-Vault-Namespace</c>, for example <c>admin/orders</c>. Leave null otherwise.</summary>
    public string? Namespace { get; set; }

    /// <summary>The KV secrets engine mount path. Default <c>secret</c>.</summary>
    public string Mount { get; set; } = "secret";

    /// <summary>
    /// The KV version of <see cref="Mount"/>: 1 or 2. Leave null to detect it from <c>sys/mounts</c>, which needs read
    /// access to <c>sys/mounts/&lt;mount&gt;</c> or <c>sys/internal/ui/mounts/&lt;mount&gt;</c>; when neither can be read, version 2
    /// is assumed and the trace carries a warning.
    /// </summary>
    public int? KvVersion { get; set; }

    /// <summary>
    /// The paths inside the mount to read, for example <c>orders/prod</c>. Each can be a secret, a folder, or both.
    /// Leave empty to read the whole mount. Names inside a path are matched relative to it, see the readme.
    /// </summary>
    public IList<string> Paths { get; } = [];

    /// <summary>When true (the default), folders under each path are listed too, down to <see cref="MaxDepth"/>.</summary>
    public bool Recursive { get; set; } = true;

    /// <summary>How many folder levels below each path to list. Default 10.</summary>
    public int MaxDepth { get; set; } = 10;

    /// <summary>Stop after this many secrets, so a large mount or a misbehaving server cannot keep a trace going. Default 2000.</summary>
    public int MaxSecrets { get; set; } = 2000;

    /// <summary>Stop after this many requests. Default 5000.</summary>
    public int MaxRequests { get; set; } = 5000;

    /// <summary>
    /// Also read secret data, so each key inside a secret can be linked and its value compared. Off by default: then
    /// only names and (for KV version 2) metadata are read, and layers are linked to whole secrets. Values that are
    /// read are always marked sensitive and are redacted in reports unless revealing is turned on.
    /// </summary>
    public bool IncludeValues { get; set; }

    /// <summary>Allows a plain-http <see cref="Address"/> that is not loopback. Off by default, so the token is not sent in clear text.</summary>
    public bool AllowUnencryptedHttp { get; set; }
}
