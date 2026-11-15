using ConfigFerret.Shared;
using ConfigFerret.Sources;

namespace ConfigFerret.HashiCorp;

/// <summary>Describes the Consul KV source to hosts such as the desktop app and the command-line tool.</summary>
public sealed class ConsulSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc/>
    public string Id => "consul-kv";

    /// <inheritdoc/>
    public string DisplayName => "Consul KV";

    /// <inheritdoc/>
    public SourceCategory Category => SourceCategory.ConfigurationService;

    /// <inheritdoc/>
    public IReadOnlyList<SourceSetting> Settings { get; } =
    [
        new(nameof(ConsulOptions.Address), "Address", SourceSettingKind.Url, Required: true, Help: "The Consul HTTP API address.", DefaultValue: "http://localhost:8500/"),
        new(nameof(ConsulOptions.Token), "ACL token", SourceSettingKind.Secret, Help: "A Consul ACL token that can read the prefix. Leave empty when ACLs are off.", EnvironmentVariables: ["CONSUL_HTTP_TOKEN"]),
        new(nameof(ConsulOptions.Datacenter), "Datacenter", Help: "The datacenter to read. Leave empty for the agent's own."),
        new(nameof(ConsulOptions.Prefix), "Key prefix", Help: "The key prefix to read, for example config/orders/. It is stripped from each key."),
        new(nameof(ConsulOptions.ExpandJson), "Expand JSON values", SourceSettingKind.Boolean, Help: "Read a JSON object value as one key per leaf.", DefaultValue: "false"),
        new(nameof(ConsulOptions.IsSensitive), "Values are secret", SourceSettingKind.Boolean, Help: "Redact every value read from Consul.", DefaultValue: "false"),
        new(nameof(ConsulTraceOptions.MatchByName), "Match by name", SourceSettingKind.Boolean, Help: "Link environment variables and Kubernetes keys to keys that set the same configuration key.", DefaultValue: "true"),
        new(nameof(ConsulTraceOptions.StripPrefixes), "Strip prefixes", SourceSettingKind.Lines, Help: "Prefixes to remove from layer names before matching, one per line."),
        new(nameof(ConsulOptions.AllowUnencryptedHttp), "Allow plain http", SourceSettingKind.Boolean, Help: "Send the token over http to an address that is not loopback.", DefaultValue: "false"),
    ];

    /// <inheritdoc/>
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var options = new ConsulOptions
        {
            Address = SettingValues.Url(values, nameof(ConsulOptions.Address), "http://localhost:8500/"),
            Token = SettingValues.Text(values, nameof(ConsulOptions.Token)),
            Datacenter = SettingValues.Text(values, nameof(ConsulOptions.Datacenter)),
            Prefix = SettingValues.Text(values, nameof(ConsulOptions.Prefix)) ?? string.Empty,
            ExpandJson = SettingValues.Boolean(values, nameof(ConsulOptions.ExpandJson), false),
            IsSensitive = SettingValues.Boolean(values, nameof(ConsulOptions.IsSensitive), false),
            AllowUnencryptedHttp = SettingValues.Boolean(values, nameof(ConsulOptions.AllowUnencryptedHttp), false),
        };
        options.AllowNoToken = options.Token is null;
        JsonApi.RequireSafeBaseAddress(options.Address, options.AllowUnencryptedHttp, nameof(ConsulOptions.Address));
        var trace = new ConsulTraceOptions { MatchByName = SettingValues.Boolean(values, nameof(ConsulTraceOptions.MatchByName), true) };
        foreach (var prefix in SettingValues.Lines(values, nameof(ConsulTraceOptions.StripPrefixes)))
        {
            trace.StripPrefixes.Add(prefix);
        }

        return ConsulSource.Create(options, trace);
    }
}
