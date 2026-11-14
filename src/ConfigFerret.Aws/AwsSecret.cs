using System.Text.Json;

namespace ConfigFerret.Aws;

/// <summary>One AWS Secrets Manager secret.</summary>
public sealed class AwsSecret
{
    private IReadOnlyDictionary<string, string?>? _properties;
    private bool _parsed;

    /// <summary>Creates a secret.</summary>
    /// <param name="name">The secret name, for example <c>prod/orders/db</c>.</param>
    public AwsSecret(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <summary>The secret name, for example <c>prod/orders/db</c>. AWS compares names case-sensitively.</summary>
    public string Name { get; }

    /// <summary>The secret's ARN, when known.</summary>
    public string? Arn { get; init; }

    /// <summary>The description, when it has one.</summary>
    public string? Description { get; init; }

    /// <summary>When the secret was last changed, when known.</summary>
    public DateTimeOffset? LastChanged { get; init; }

    /// <summary>The secret's tags.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The current secret string. Null unless values were read (<see cref="AwsSecretsManagerOptions.IncludeValues"/>),
    /// and for binary secrets.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>True when the value was read, or reading it was tried and the secret is binary.</summary>
    public bool ValueRead { get; init; }

    /// <summary>True when the secret holds binary data instead of a string.</summary>
    public bool IsBinary { get; init; }

    /// <summary>
    /// The properties of a JSON object secret, flattened the way .NET configuration flattens JSON: nested objects and
    /// arrays become <c>a:b</c> and <c>a:0</c>. Null when the value was not read or is not a JSON object with properties.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Properties
    {
        get
        {
            if (!_parsed)
            {
                _properties = JsonSecret.Flatten(Value);
                _parsed = true;
            }

            return _properties;
        }
    }

    /// <summary>Where the secret lives: the region and account from its ARN, when known.</summary>
    public string? Location => AwsArn.Describe(Arn);
}

/// <summary>A pointer to an AWS Secrets Manager secret, or to one property of a JSON secret.</summary>
/// <param name="Name">The secret name.</param>
/// <param name="Property">The flattened JSON property, for example <c>password</c> or <c>db:host</c>, or null for the whole secret.</param>
public sealed record AwsSecretRef(string Name, string? Property = null) : SourceRef;

/// <summary>Reads ARNs such as <c>arn:aws:secretsmanager:eu-west-1:123456789012:secret:prod/db-AbCdEf</c>.</summary>
internal static class AwsArn
{
    /// <summary><c>eu-west-1, account 123456789012</c>, or null when <paramref name="arn"/> is not an ARN.</summary>
    public static string? Describe(string? arn)
    {
        var parts = arn?.Split(':', 6);
        if (parts is not { Length: 6 } || parts[0] != "arn" || parts[3].Length == 0)
        {
            return null;
        }

        return parts[4].Length == 0 ? parts[3] : $"{parts[3]}, account {parts[4]}";
    }
}

/// <summary>Flattens JSON object secrets the way the .NET JSON configuration provider and Kralizek's provider do.</summary>
internal static class JsonSecret
{
    private const int MaxProperties = 10_000;

    public static Dictionary<string, string?>? Flatten(string? value)
    {
        if (value is null || !value.AsSpan().TrimStart().StartsWith("{"))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            Walk(doc.RootElement, null, result);
            return result.Count == 0 ? null : result;
        }
        catch (JsonException)
        {
            // Not JSON after all: a plain string secret that starts with '{'.
            return null;
        }
    }

    private static void Walk(JsonElement element, string? prefix, Dictionary<string, string?> into)
    {
        if (into.Count >= MaxProperties)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, prefix is null ? property.Name : $"{prefix}:{property.Name}", into);
                }

                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{prefix}:{i++}", into);
                }

                break;
            default:
                if (prefix is not null)
                {
                    into[prefix] = element.ValueKind switch
                    {
                        JsonValueKind.String => element.GetString(),
                        JsonValueKind.Null => null,
                        _ => element.GetRawText(),
                    };
                }

                break;
        }
    }

    /// <summary>Finds a property the way External Secrets Operator names it (<c>a.b</c>) or flattened (<c>a:b</c>).</summary>
    public static string? FindProperty(IReadOnlyDictionary<string, string?>? properties, string property)
    {
        if (properties is null)
        {
            return null;
        }

        if (properties.ContainsKey(property))
        {
            return property;
        }

        var flattened = property.Replace('.', ':');
        return properties.ContainsKey(flattened) ? flattened : null;
    }
}
