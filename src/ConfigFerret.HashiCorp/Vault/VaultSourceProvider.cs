using System.Globalization;
using ConfigFerret.Shared;
using ConfigFerret.Sources;

namespace ConfigFerret.HashiCorp;

/// <summary>Describes the HashiCorp Vault KV source to hosts such as the desktop app and the command-line tool.</summary>
public sealed class VaultSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc/>
    public string Id => "hashicorp-vault";

    /// <inheritdoc/>
    public string DisplayName => "HashiCorp Vault";

    /// <inheritdoc/>
    public SourceCategory Category => SourceCategory.SecretStore;

    /// <inheritdoc/>
    public IReadOnlyList<SourceSetting> Settings { get; } =
    [
        new(nameof(VaultOptions.Address), "Address", SourceSettingKind.Url, Required: true, Help: "The Vault address, for example https://vault.example.com:8200/."),
        new(nameof(VaultOptions.Token), "Token", SourceSettingKind.Secret, Required: true, Help: "A Vault token whose policy allows list and read on the paths.", EnvironmentVariables: ["VAULT_TOKEN"]),
        new(nameof(VaultOptions.Namespace), "Namespace", Help: "The Vault Enterprise namespace, if any."),
        new(nameof(VaultOptions.Mount), "KV mount", Help: "The KV secrets engine mount path.", DefaultValue: "secret"),
        new(nameof(VaultOptions.KvVersion), "KV version", Help: "1 or 2. Leave empty to detect it from sys/mounts."),
        new(nameof(VaultOptions.Paths), "Paths", SourceSettingKind.Lines, Help: "The paths inside the mount to read, one per line. Leave empty for the whole mount."),
        new(nameof(VaultOptions.Recursive), "List sub-folders", SourceSettingKind.Boolean, Help: "Also list the folders under each path.", DefaultValue: "true"),
        new(nameof(VaultOptions.IncludeValues), "Read secret values", SourceSettingKind.Boolean, Help: "Read secret data to link each key and compare values. Values stay redacted.", DefaultValue: "false"),
        new(nameof(VaultTraceOptions.MatchByName), "Match by name", SourceSettingKind.Boolean, Help: "Link environment variables and Kubernetes keys to secrets with the same name.", DefaultValue: "true"),
        new(nameof(VaultTraceOptions.StripPrefixes), "Strip prefixes", SourceSettingKind.Lines, Help: "Prefixes to remove from layer names before matching, one per line."),
        new(nameof(VaultOptions.AllowUnencryptedHttp), "Allow plain http", SourceSettingKind.Boolean, Help: "Send the token over http to an address that is not loopback.", DefaultValue: "false"),
    ];

    /// <inheritdoc/>
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var options = new VaultOptions
        {
            Address = SettingValues.Url(values, nameof(VaultOptions.Address)),
            Token = SettingValues.Required(values, nameof(VaultOptions.Token)),
            Namespace = SettingValues.Text(values, nameof(VaultOptions.Namespace)),
            Mount = SettingValues.Text(values, nameof(VaultOptions.Mount)) ?? "secret",
            Recursive = SettingValues.Boolean(values, nameof(VaultOptions.Recursive), true),
            IncludeValues = SettingValues.Boolean(values, nameof(VaultOptions.IncludeValues), false),
            AllowUnencryptedHttp = SettingValues.Boolean(values, nameof(VaultOptions.AllowUnencryptedHttp), false),
        };
        options.KvVersion = SettingValues.Text(values, nameof(VaultOptions.KvVersion)) switch
        {
            null => null,
            var text when int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v is 1 or 2 => v,
            _ => throw new ArgumentException("KvVersion must be 1 or 2, or empty to detect it.", nameof(values)),
        };
        foreach (var path in SettingValues.Lines(values, nameof(VaultOptions.Paths)))
        {
            options.Paths.Add(path);
        }

        JsonApi.RequireSafeBaseAddress(options.Address, options.AllowUnencryptedHttp, nameof(VaultOptions.Address));
        var trace = new VaultTraceOptions { MatchByName = SettingValues.Boolean(values, nameof(VaultTraceOptions.MatchByName), true) };
        foreach (var prefix in SettingValues.Lines(values, nameof(VaultTraceOptions.StripPrefixes)))
        {
            trace.StripPrefixes.Add(prefix);
        }

        return VaultSource.Create(options, trace);
    }
}
