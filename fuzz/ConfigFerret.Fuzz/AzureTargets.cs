using System.Net;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.AppConfiguration;
using Azure.Security.KeyVault.Secrets;
using ConfigFerret.Azure;
using ConfigFerret.Sources;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Fuzz;

/// <summary>
/// Fuzz targets for the Azure package, fed the same way as the GitHub and Octopus ones: the first byte picks the
/// options, and the rest holds one response per request split by <c>%%</c> lines, each optionally starting with
/// <c>HTTP &lt;status&gt;</c>. Requests without a token get Key Vault's 401 challenge first, as a real vault sends.
/// Loading may fail only with <see cref="RequestFailedException"/>, and neither the token nor a secret value may reach a
/// message, a note or an unredacted report.
/// </summary>
internal static partial class Targets
{
    private const string AzureVault = "https://contoso.vault.azure.net/";

    private static void AzureKeyVault(ReadOnlySpan<byte> data)
    {
        var flags = data.Length > 0 ? data[0] : (byte)0;
        var responses = Responses(data.Length > 0 ? data[1..] : data);
        var options = new AzureKeyVaultOptions
        {
            VaultUri = new Uri(AzureVault),
            IncludeValues = (flags & 1) != 0,
            IncludeDisabled = (flags & 2) != 0,
        };
        if ((flags & 4) != 0)
        {
            options.SecretNames.Add("Db--*");
            options.SecretNames.Add("api-key");
        }

        using var server = new ScriptedServer(responses, """{"value":[],"nextLink":null}""");
        using var challenge = new ChallengingHandler(server);
        var clientOptions = new SecretClientOptions { Transport = new HttpClientTransport(new HttpClient(challenge, disposeHandler: false)) };
        clientOptions.Retry.MaxRetries = 0;
        AzureKeyVaultSecrets secrets;
        try
        {
            secrets = AzureKeyVaultSecrets.LoadAsync(options, new FuzzCredential(), clientOptions).GetAwaiter().GetResult();
        }
        catch (RequestFailedException ex)
        {
            CheckNoCredential(ex.Message, server, "Key Vault load error");
            return;
        }

        foreach (var warning in secrets.Warnings)
        {
            CheckNoCredential(warning, server, "Key Vault warning");
        }

        foreach (var secret in secrets.Secrets)
        {
            if (string.IsNullOrEmpty(secret.Name) || secrets.Find(secret.Name) != secret || !secrets.FindByConfigurationKey(secret.ConfigurationKey).Contains(secret))
            {
                throw new InvariantException($"AzureKeyVaultSecrets cannot find secret '{secret.Name}'.");
            }

            if (secret.Value is not null && !options.IncludeValues)
            {
                throw new InvariantException($"Secret {secret.Name} has a value that was not asked for.");
            }

            if (!secret.Enabled && (!options.IncludeDisabled || secret.Value is not null))
            {
                throw new InvariantException($"Disabled secret {secret.Name} was kept or read.");
            }
        }

        var resolver = new AzureKeyVaultResolver(secrets, new AzureKeyVaultTraceOptions { MatchByName = (flags & 8) == 0 });
        var diagnostics = new TraceDiagnostics();
        foreach (var secret in secrets.Secrets.Take(100))
        {
            var name = secret.Name;
            var layers = NamedLayers(name)
                .Append(Step(LayerKind.KubernetesSecret, new ExternalSecretRef((flags & 16) != 0 ? "azurekv" : null, "store", name, Version: "v1"), LayerValue))
                .Append(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "AzureKeyVaultConfigurationProvider", Value = LayerValue, ConfigurationKey = secret.ConfigurationKey });
            if (Uri.TryCreate($"{AzureVault}secrets/{Uri.EscapeDataString(name)}", UriKind.Absolute, out var secretId))
            {
                layers = layers.Append(new ProvenanceStep { Kind = LayerKind.AzureAppConfiguration, Source = "ref", Reference = new AzureAppConfigurationSettingRef("store", name, null, secretId) });
            }

            foreach (var layer in layers)
            {
                foreach (var step in resolver.Resolve(layer, diagnostics))
                {
                    if (step.Kind != LayerKind.AzureKeyVault || step.Reference is not AzureKeyVaultSecretRef || !step.IsSensitive)
                    {
                        throw new InvariantException($"Key Vault step for {name} has kind {step.Kind}, reference {step.Reference} and sensitive {step.IsSensitive}.");
                    }

                    CheckNote(step, name);
                }
            }
        }

        foreach (var warning in diagnostics.Warnings)
        {
            CheckNoCredential(warning, server, "Key Vault trace warning");
        }

        // Every value must be redacted: a value may show up in the report only if it shows up without any values too.
        var values = secrets.Secrets.ToDictionary(s => s.ConfigurationKey, s => (string?)(s.Value ?? LayerValue), StringComparer.OrdinalIgnoreCase);
        var withValues = Render(secrets.ToLoadedSource(), values, provider: true);
        var withoutValues = Render(new AzureKeyVaultSecrets(secrets.Vault, secrets.Secrets.Select(s => s with { Value = null }), secrets.Warnings).ToLoadedSource(), values.ToDictionary(p => p.Key, _ => (string?)LayerValue, StringComparer.OrdinalIgnoreCase), provider: true);
        foreach (var value in secrets.Secrets.Select(s => s.Value).OfType<string>().Where(v => v.Length >= 4))
        {
            if (withValues.Contains(value, StringComparison.Ordinal) && !withoutValues.Contains(value, StringComparison.Ordinal))
            {
                throw new InvariantException($"A Key Vault secret value is not redacted: {value}");
            }
        }
    }

    private static void AzureAppConfiguration(ReadOnlySpan<byte> data)
    {
        var flags = data.Length > 0 ? data[0] : (byte)0;
        var responses = Responses(data.Length > 0 ? data[1..] : data);
        var accessKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(Token));
        var options = (flags & 64) != 0
            ? new AzureAppConfigurationOptions { ConnectionString = $"Endpoint=https://contoso.azconfig.io;Id=fuzz;Secret={accessKey}" }
            : new AzureAppConfigurationOptions { Endpoint = new Uri("https://contoso.azconfig.io") };
        options.IncludeKeyVaultReferences = (flags & 2) == 0;
        options.IncludeFeatureFlags = (flags & 4) == 0;
        if ((flags & 1) != 0)
        {
            options.Labels.Add(string.Empty);
            options.Labels.Add("prod");
        }

        if ((flags & 8) != 0)
        {
            options.KeyFilter = "App:*";
        }

        if ((flags & 16) != 0)
        {
            options.TrimKeyPrefixes.Add("App:");
        }

        using var server = new ScriptedServer(responses, """{"items":[]}""");
        var clientOptions = new ConfigurationClientOptions { Transport = new HttpClientTransport(new HttpClient(server, disposeHandler: false)) };
        clientOptions.Retry.MaxRetries = 0;
        AzureAppConfigurationSettings settings;
        try
        {
            settings = AzureAppConfigurationSettings.LoadAsync(options, (flags & 64) != 0 ? null : new FuzzCredential(), clientOptions).GetAwaiter().GetResult();
        }
        catch (RequestFailedException ex)
        {
            CheckNoCredential(ex.Message, server, "App Configuration load error");
            CheckNoAccessKey(ex.Message, accessKey, server);
            return;
        }

        foreach (var warning in settings.Warnings)
        {
            CheckNoCredential(warning, server, "App Configuration warning");
            CheckNoAccessKey(warning, accessKey, server);
        }

        foreach (var setting in settings.Settings)
        {
            if (string.IsNullOrEmpty(setting.Key) || !settings.Find(setting.Key).Contains(setting) || !settings.FindByConfigurationKey(setting.ConfigurationKey).Contains(setting))
            {
                throw new InvariantException($"AzureAppConfigurationSettings cannot find key '{setting.Key}'.");
            }

            switch (setting.Kind)
            {
                case AzureAppConfigurationSettingKind.KeyVaultReference when setting.Value is not null || !options.IncludeKeyVaultReferences:
                    throw new InvariantException($"Key Vault reference {setting.Key} has a value or was not asked for.");
                case AzureAppConfigurationSettingKind.FeatureFlag when !setting.ConfigurationKey.StartsWith("FeatureManagement:", StringComparison.Ordinal) || !options.IncludeFeatureFlags:
                    throw new InvariantException($"Feature flag {setting.Key} maps to {setting.ConfigurationKey} or was not asked for.");
                default:
                    break;
            }
        }

        var resolver = new AzureAppConfigurationResolver(settings, new AzureAppConfigurationTraceOptions { MatchByName = (flags & 32) == 0 });
        var diagnostics = new TraceDiagnostics();
        foreach (var setting in settings.Settings.Take(100))
        {
            var layers = NamedLayers(setting.Key)
                .Append(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "AzureAppConfigurationProvider", Value = LayerValue, ConfigurationKey = setting.ConfigurationKey })
                .Append(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "AzureAppConfigurationProvider", Value = LayerValue, ConfigurationKey = setting.ConfigurationKey + ":EnabledFor:0:Name" });
            foreach (var layer in layers)
            {
                foreach (var step in resolver.Resolve(layer, diagnostics))
                {
                    if (step.Kind != LayerKind.AzureAppConfiguration || step.Reference is not AzureAppConfigurationSettingRef reference)
                    {
                        throw new InvariantException($"App Configuration step for {setting.Key} has kind {step.Kind} and reference {step.Reference}.");
                    }

                    if (reference.SecretId is not null && (!step.IsSensitive || step.Value is not null))
                    {
                        throw new InvariantException($"Key Vault reference step for {setting.Key} is not sensitive or has a value.");
                    }

                    CheckNote(step, setting.Key);
                }
            }
        }

        var keys = settings.Settings.Take(200).Select(s => s.ConfigurationKey).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(k => k, _ => (string?)LayerValue, StringComparer.OrdinalIgnoreCase);
        var loaded = settings.ToLoadedSource();
        Render(loaded, keys, provider: false);
    }

    // Traces keys set by a stand-in for the Azure configuration provider, and returns the text and JSON reports.
    private static string Render(LoadedConfigSource loaded, Dictionary<string, string?> data, bool provider)
    {
        IConfigurationSource source = provider ? new AzureKeyVaultConfigurationProvider(data) : new AzureAppConfigurationProvider(data);
        ProvenanceReport report;
        try
        {
            report = ConfigTracer.For(new ConfigurationBuilder().Add(source).Build())
                .WithOptions(o => o.Environment = new Dictionary<string, string?>())
                .WithSource(loaded)
                .Trace();
        }
        catch (InvalidOperationException)
        {
            // Configuration keys that the configuration builder itself cannot hold, such as "a" and "a:b" with values in a tree it rejects.
            return string.Empty;
        }

        var unused = loaded.FindUnused(report);
        if (unused.Any(e => !loaded.Entries.Contains(e)))
        {
            throw new InvariantException("FindUnused returned an entry that was not loaded.");
        }

        return report.ToText() + report.ToJson();
    }

    private static void CheckNoAccessKey(string message, string accessKey, ScriptedServer server)
    {
        if (message.Contains(accessKey, StringComparison.Ordinal) && !server.Echoed)
        {
            throw new InvariantException($"A message holds the App Configuration access key: {message}");
        }
    }

    /// <summary>Hands out <see cref="Token"/>, so a leak of it shows up in the checks.</summary>
    private sealed class FuzzCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(Token, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(GetToken(requestContext, cancellationToken));
    }

    /// <summary>Answers requests without a token with Key Vault's 401 challenge, and passes the rest to the scripted server.</summary>
    private sealed class ChallengingHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        // The scripted server is disposed by its owner.
        private readonly HttpMessageInvoker _inner = new(inner, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization is not null)
            {
                return _inner.SendAsync(request, cancellationToken);
            }

            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(string.Empty) };
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer authorization=\"https://login.microsoftonline.com/tenant\", resource=\"https://vault.azure.net\"");
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Stands in for the Azure SDK's Key Vault configuration provider: ConfigFerret only sees its type name.</summary>
    private sealed class AzureKeyVaultConfigurationProvider(Dictionary<string, string?> data) : ConfigurationProvider, IConfigurationSource
    {
        public override void Load() => Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);

        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
    }

    /// <summary>Stands in for the App Configuration provider.</summary>
    private sealed class AzureAppConfigurationProvider(Dictionary<string, string?> data) : ConfigurationProvider, IConfigurationSource
    {
        public override void Load() => Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);

        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
    }
}
