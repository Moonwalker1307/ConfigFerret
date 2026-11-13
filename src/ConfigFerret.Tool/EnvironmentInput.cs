namespace ConfigFerret.Tool;

/// <summary>Reads <c>NAME=VALUE</c> pairs from <c>--env</c>, <c>--env-file</c> and <c>--set</c> style arguments.</summary>
internal static class EnvironmentInput
{
    /// <summary>Parses one <c>NAME=VALUE</c> pair. The value may be empty and may contain <c>=</c>.</summary>
    public static (string Name, string Value) ParsePair(string text, string optionName)
    {
        var eq = text.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            throw new UsageException($"{optionName} expects NAME=VALUE, got '{text}'.");
        }

        return (text[..eq].Trim(), text[(eq + 1)..]);
    }

    /// <summary>
    /// Reads a dotenv-style file: one <c>NAME=VALUE</c> per line, blank lines and <c>#</c> comments skipped, an optional
    /// leading <c>export</c>, and the value's surrounding quotes removed.
    /// </summary>
    public static IEnumerable<(string Name, string Value)> ParseFile(string text, string fileName)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw new UsageException($"{fileName} line {i + 1}: expected NAME=VALUE.");
            }

            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            yield return (line[..eq].Trim(), value);
        }
    }
}
