namespace ConfigFerret.HashiCorp;

/// <summary>
/// Which Consul KV keys to read, and how to sign in. Can be bound from a configuration section, see
/// <c>services.AddConfigFerretConsul(...)</c>.
/// </summary>
public sealed class ConsulOptions
{
    /// <summary>The Consul HTTP API address. Default <c>http://localhost:8500/</c>, the local agent. The same as <c>CONSUL_HTTP_ADDR</c>.</summary>
    public Uri? Address { get; set; } = new("http://localhost:8500/");

    /// <summary>
    /// A Consul ACL token (the same as <c>CONSUL_HTTP_TOKEN</c>) with <c>key_prefix "..." { policy = "read" }</c> on the
    /// prefix. Read it from user secrets, an environment variable or a key vault; never put it in a committed appsettings
    /// file. ConfigFerret sends it only in the <c>X-Consul-Token</c> header and never writes it to a report or exception.
    /// Leave it null when ACLs are off, and set <see cref="AllowNoToken"/> for dependency injection.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>When true, startup validation accepts a missing <see cref="Token"/>, for a Consul agent without ACLs. Off by default.</summary>
    public bool AllowNoToken { get; set; }

    /// <summary>The datacenter to read, sent as <c>?dc=</c>. Leave null for the agent's own.</summary>
    public string? Datacenter { get; set; }

    /// <summary>
    /// The key prefix to read, for example <c>config/orders/</c>. It is stripped from each key, and the rest becomes the
    /// configuration key: <c>config/orders/Db/Timeout</c> sets <c>Db:Timeout</c>, as Winton.Extensions.Configuration.Consul
    /// maps it. Leave empty to read every key.
    /// </summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>
    /// When true, a value that is a JSON object or array is expanded into one key per leaf, the way
    /// Winton.Extensions.Configuration.Consul's JSON parser and the .NET JSON provider read it: key
    /// <c>config/orders/Db</c> holding <c>{"Timeout": 30}</c> sets <c>Db:Timeout</c>. Off by default.
    /// </summary>
    public bool ExpandJson { get; set; }

    /// <summary>When true, every value is treated as secret and redacted. Off by default: Consul KV usually holds plain configuration.</summary>
    public bool IsSensitive { get; set; }

    /// <summary>Stop after this many keys (after JSON expansion), so a huge response cannot keep a trace going. Default 10000.</summary>
    public int MaxKeys { get; set; } = 10000;

    /// <summary>Allows a plain-http <see cref="Address"/> that is not loopback. Off by default, so the token is not sent in clear text.</summary>
    public bool AllowUnencryptedHttp { get; set; }
}

/// <summary>One configuration value read from Consul KV.</summary>
/// <param name="Key">The Consul key, for example <c>config/orders/Db/Timeout</c>.</param>
/// <param name="ConfigurationKey">The configuration key it sets, for example <c>Db:Timeout</c>.</param>
/// <param name="Value">The value, decoded from base64; for an expanded JSON value, the leaf's value.</param>
/// <param name="ModifyIndex">Consul's modify index for the key, which grows each time it is written.</param>
/// <param name="JsonPath">For a key expanded from a JSON value, the path inside the JSON, for example <c>Db:Timeout</c>; otherwise null.</param>
public sealed record ConsulEntry(string Key, string ConfigurationKey, string? Value, long? ModifyIndex = null, string? JsonPath = null);

/// <summary>A pointer to a Consul KV key, or to one leaf of its JSON value.</summary>
/// <param name="Datacenter">The datacenter, or null for the agent's own.</param>
/// <param name="Key">The Consul key.</param>
/// <param name="JsonPath">The path inside the key's JSON value, or null for the whole key.</param>
public sealed record ConsulKeyRef(string? Datacenter, string Key, string? JsonPath = null) : SourceRef;
