using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using ConfigFerret.Sources;

namespace ConfigFerret.Aws;

/// <summary>
/// A snapshot of the AWS Systems Manager Parameter Store parameters under one path. Load it with
/// <see cref="LoadAsync(AwsParameterStoreOptions, CancellationToken)"/>, or build it from parameters you already have,
/// then pass it to <see cref="AwsTracerExtensions.WithAwsParameterStore"/>.
/// </summary>
public sealed class AwsParameters
{
    // GetParametersByPath returns at most 10 per page.
    private const int PageSize = 10;

    // Stop after this many pages so a misbehaving server cannot keep a trace looping.
    private const int MaxPages = 5000;

    private readonly Dictionary<string, AwsParameter> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AwsParameter> _byArn = new(StringComparer.Ordinal);

    /// <summary>Creates a snapshot from parameters you already have. A second parameter with the same name is ignored.</summary>
    /// <param name="path">The path the parameters were read under, for example <c>/orders/prod/</c>.</param>
    /// <param name="parameters">The parameters.</param>
    /// <param name="warnings">Problems found while reading them.</param>
    /// <param name="location">Where they were read from, for example the region.</param>
    public AwsParameters(string path, IEnumerable<AwsParameter> parameters, IEnumerable<string>? warnings = null, string? location = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(parameters);
        Path = path;
        var entries = new List<AwsParameter>();
        foreach (var parameter in parameters)
        {
            ArgumentNullException.ThrowIfNull(parameter, nameof(parameters));
            if (!string.IsNullOrEmpty(parameter.Name) && _byName.TryAdd(parameter.Name, parameter))
            {
                entries.Add(parameter);
                if (!string.IsNullOrEmpty(parameter.Arn))
                {
                    _byArn.TryAdd(parameter.Arn, parameter);
                }
            }
        }

        Entries = entries;
        Warnings = [.. warnings ?? []];
        Location = location;
    }

    /// <summary>The path the parameters were read under.</summary>
    public string Path { get; }

    /// <summary>Every parameter, in the order AWS returned them.</summary>
    public IReadOnlyList<AwsParameter> Entries { get; }

    /// <summary>Problems found while loading.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Where the parameters were read from, for example <c>eu-west-1</c>.</summary>
    public string? Location { get; }

    /// <summary>Finds a parameter by full name or ARN. Null when there is none.</summary>
    public AwsParameter? Find(string nameOrArn)
    {
        ArgumentNullException.ThrowIfNull(nameOrArn);
        return _byName.TryGetValue(nameOrArn, out var parameter) || _byArn.TryGetValue(nameOrArn, out parameter) ? parameter : null;
    }

    /// <summary>
    /// The .NET key <c>AddSystemsManager(path)</c> gives a parameter: the path is removed (case-insensitively), leading
    /// slashes are trimmed, and the remaining <c>/</c> become <c>:</c>. <c>/orders/prod/Db/Timeout</c> under
    /// <c>/orders/prod/</c> is <c>Db:Timeout</c>.
    /// </summary>
    public string ToConfigurationKey(AwsParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return ToConfigurationKey(parameter.Name, Path);
    }

    internal static string ToConfigurationKey(string name, string path)
    {
        var rest = path.Length > 0 && name.StartsWith(path, StringComparison.OrdinalIgnoreCase) ? name[path.Length..] : name;
        return rest.TrimStart('/').Replace("/", ":", StringComparison.Ordinal);
    }

    /// <summary>Reads the parameters with the AWS SDK, signed in with the default credential chain or the options' credentials.</summary>
    /// <exception cref="AmazonClientException">AWS refused the request or could not be reached.</exception>
    public static async Task<AwsParameters> LoadAsync(AwsParameterStoreOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Path);
        using var client = AwsClients.CreateParameterStore(options);
        return await LoadAsync(options, client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the parameters with your own client. Only <see cref="AwsParameterStoreOptions.Path"/>,
    /// <see cref="AwsParameterStoreOptions.Recursive"/> and <see cref="AwsParameterStoreOptions.IncludeValues"/> are used;
    /// the client decides the region and credentials.
    /// </summary>
    /// <exception cref="AmazonClientException">AWS refused the request or could not be reached.</exception>
    public static async Task<AwsParameters> LoadAsync(AwsParameterStoreOptions options, IAmazonSimpleSystemsManagement client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Path);
        var path = options.Path.Trim();
        if (!path.StartsWith('/'))
        {
            throw new ArgumentException("Path must start with /, for example /orders/prod/.", nameof(options));
        }

        var request = new GetParametersByPathRequest
        {
            Path = path,
            Recursive = options.Recursive,
            WithDecryption = options.IncludeValues,
            MaxResults = PageSize,
        };
        var parameters = new List<AwsParameter>();
        var warnings = new List<string>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; ; page++)
        {
            if (page == MaxPages)
            {
                warnings.Add($"Stopped reading AWS parameters under {path} after {MaxPages} pages.");
                break;
            }

            var response = await client.GetParametersByPathAsync(request, cancellationToken).ConfigureAwait(false);
            foreach (var parameter in response?.Parameters ?? [])
            {
                if (parameter is not null && !string.IsNullOrEmpty(parameter.Name))
                {
                    parameters.Add(ToParameter(parameter, options.IncludeValues));
                }
            }

            var next = response?.NextToken;
            if (string.IsNullOrEmpty(next) || !seenTokens.Add(next))
            {
                break;
            }

            request.NextToken = next;
        }

        return new AwsParameters(path, parameters, warnings, AwsClients.Describe(client.Config));
    }

    /// <summary>Wraps the snapshot for <see cref="IConfigSource"/> hosts.</summary>
    internal LoadedConfigSource ToLoadedSource(string displayName, AwsParametersTraceOptions traceOptions) =>
        new(
            displayName,
            new AwsParametersResolver(this, traceOptions),
            Entries.Select(p => new SourceEntry(p.Name, new AwsParameterRef(p.Name), p.IsSensitive, p.Type.ToString(), p.Location ?? Location)),
            Warnings);

    private static AwsParameter ToParameter(Parameter parameter, bool decrypted)
    {
        var type = parameter.Type?.Value switch
        {
            "String" => AwsParameterType.String,
            "StringList" => AwsParameterType.StringList,
            "SecureString" => AwsParameterType.SecureString,
            _ => AwsParameterType.Unknown,
        };

        // Without decryption AWS returns a SecureString's ciphertext, which is no use and should not be shown.
        var value = type is AwsParameterType.String or AwsParameterType.StringList || decrypted ? parameter.Value : null;
        return new AwsParameter(
            parameter.Name!,
            type,
            value,
            parameter.Version,
            AwsSecrets.ToOffset(parameter.LastModifiedDate),
            string.IsNullOrEmpty(parameter.ARN) ? null : parameter.ARN);
    }
}
