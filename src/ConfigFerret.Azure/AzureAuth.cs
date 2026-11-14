using System.Globalization;
using Azure;
using Azure.Core;
using Azure.Identity;

namespace ConfigFerret.Azure;

/// <summary>Sign-in and naming helpers shared by the Key Vault and App Configuration sources.</summary>
internal static class AzureAuth
{
    /// <summary>
    /// A service principal credential when a client secret is given, otherwise <see cref="DefaultAzureCredential"/>
    /// (environment, workload identity, managed identity, Visual Studio, Azure CLI and so on), limited to the tenant and
    /// managed or workload identity client ID when those are set.
    /// </summary>
    public static TokenCredential Create(string? tenantId, string? clientId, string? clientSecret)
    {
        tenantId = Clean(tenantId);
        clientId = Clean(clientId);
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            if (tenantId is null || clientId is null)
            {
                throw new ArgumentException("A client secret needs a tenant ID and a client ID too.", nameof(clientSecret));
            }

            return new ClientSecretCredential(tenantId, clientId, clientSecret);
        }

        var options = new DefaultAzureCredentialOptions();
        if (tenantId is not null)
        {
            options.TenantId = tenantId;
        }

        if (clientId is not null)
        {
            options.ManagedIdentityClientId = clientId;
            options.WorkloadIdentityClientId = clientId;
        }

        return new DefaultAzureCredential(options);
    }

    /// <summary>Checks the sign-in settings without building a credential. Returns the problem, or null.</summary>
    public static string? Check(string? tenantId, string? clientId, string? clientSecret, string secretKey) =>
        !string.IsNullOrWhiteSpace(clientSecret) && (Clean(tenantId) is null || Clean(clientId) is null)
            ? $"{secretKey} is set, but a client secret also needs TenantId and ClientId."
            : null;

    /// <summary>The first label of a host name: <c>contoso-prod</c> for <c>contoso-prod.vault.azure.net</c>.</summary>
    public static string ShortName(Uri uri)
    {
        var host = uri.IdnHost;
        var dot = host.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? host[..dot] : host;
    }

    /// <summary>A failed request as a short line: status and error code only, since the full message holds headers and the response body.</summary>
    public static string Describe(RequestFailedException ex) =>
        string.IsNullOrEmpty(ex.ErrorCode)
            ? string.Create(CultureInfo.InvariantCulture, $"status {ex.Status}")
            : string.Create(CultureInfo.InvariantCulture, $"status {ex.Status} ({Truncate(ex.ErrorCode)})");

    /// <summary>Parses a true/false setting from a provider's values.</summary>
    public static bool ReadBoolean(IReadOnlyDictionary<string, string?> values, string name, bool defaultValue)
    {
        if (!values.TryGetValue(name, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return defaultValue;
        }

        return bool.TryParse(text.Trim(), out var value)
            ? value
            : throw new ArgumentException($"{name} must be true or false.", nameof(values));
    }

    /// <summary>Reads an optional text setting from a provider's values.</summary>
    public static string? ReadText(IReadOnlyDictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out var text) ? Clean(text) : null;

    /// <summary>Splits a multi-line setting into its non-empty lines.</summary>
    public static IEnumerable<string> ReadLines(IReadOnlyDictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out var text) && text is not null
            ? text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
            : [];

    /// <summary>Reads a required https URL setting from a provider's values.</summary>
    public static Uri? ReadUrl(IReadOnlyDictionary<string, string?> values, string name, bool required)
    {
        var text = ReadText(values, name);
        if (text is null)
        {
            return required ? throw new ArgumentException($"{name} is required.", nameof(values)) : null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException($"{name} must be an absolute https URL without credentials.", nameof(values));
        }

        return uri;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Truncate(string text) => text.Length > 100 ? text[..100] + "..." : text;
}
