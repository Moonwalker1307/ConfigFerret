using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace ConfigFerret.Tool;

/// <summary>
/// An environment variables source that reads a given set of variables instead of the tool's own process
/// environment, so the trace shows what the app would see and none of the tool's variables (or tokens) leak in.
/// </summary>
internal sealed class SimulatedEnvironmentVariablesSource(IReadOnlyDictionary<string, string?> variables, string prefix) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new SimulatedEnvironmentVariablesProvider(variables, prefix);
}

/// <summary>
/// Maps variables to keys the way <see cref="EnvironmentVariablesConfigurationProvider"/> does: the prefix is removed,
/// <c>__</c> becomes <c>:</c>, and <c>*CONNSTR_</c> variables become <c>ConnectionStrings</c> entries. It derives from
/// that provider so the trace describes each value as an environment variable.
/// </summary>
internal sealed class SimulatedEnvironmentVariablesProvider(IReadOnlyDictionary<string, string?> variables, string prefix)
    : EnvironmentVariablesConfigurationProvider(prefix)
{
    private static readonly (string Prefix, string? ProviderName)[] ConnectionStringPrefixes =
    [
        ("MYSQLCONNSTR_", "MySql.Data.MySqlClient"),
        ("SQLAZURECONNSTR_", "System.Data.SqlClient"),
        ("SQLCONNSTR_", "System.Data.SqlClient"),
        ("POSTGRESQLCONNSTR_", "Npgsql"),
        ("CUSTOMCONNSTR_", null),
    ];

    private readonly string _normalizedPrefix = Normalize(prefix);

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in variables)
        {
            var connectionString = ConnectionStringPrefixes.FirstOrDefault(c => name.StartsWith(c.Prefix, StringComparison.OrdinalIgnoreCase));
            if (connectionString.Prefix is not null)
            {
                var key = "ConnectionStrings:" + Normalize(name[connectionString.Prefix.Length..]);
                Add(data, key, value);
                if (connectionString.ProviderName is not null)
                {
                    Add(data, key + "_ProviderName", connectionString.ProviderName);
                }
            }
            else
            {
                Add(data, Normalize(name), value);
            }
        }

        Data = data;
    }

    private void Add(Dictionary<string, string?> data, string normalizedKey, string? value)
    {
        if (normalizedKey.StartsWith(_normalizedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            data[normalizedKey[_normalizedPrefix.Length..]] = value;
        }
    }

    private static string Normalize(string name) => name.Replace("__", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
}
