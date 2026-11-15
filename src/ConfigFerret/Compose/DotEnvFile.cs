using System.Globalization;
using System.Text;

namespace ConfigFerret.Compose;

/// <summary>
/// One dotenv (<c>.env</c>) file, parsed the way Docker Compose reads <c>.env</c> and <c>env_file</c> files:
/// <c>KEY=VALUE</c> lines, an optional <c>export</c>, single, double and backtick quotes, values in double quotes that
/// span lines, <c>\n</c> escapes in double quotes, <c>#</c> comments (after a space when the value is not quoted) and
/// <c>${VAR}</c> interpolation from the variables passed in and from earlier keys.
/// </summary>
/// <remarks>
/// Values in double quotes and unquoted values are interpolated; values in single quotes or backticks are kept as
/// written. A variable passed in wins over an earlier key in the file, as in Docker Compose, where the shell wins.
/// A line that holds only <c>KEY</c> takes the key's value from the variables passed in, and is skipped when they
/// don't have it.
/// </remarks>
public sealed class DotEnvFile
{
    private DotEnvFile(string name, string? path, List<DotEnvEntry> entries, List<string> warnings)
    {
        Name = name;
        Path = path;
        Entries = entries;
        Warnings = warnings;
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            variables[entry.Name] = entry.Value;
        }

        Variables = variables;
    }

    /// <summary>The name shown in reports, for example <c>.env.local</c>.</summary>
    public string Name { get; }

    /// <summary>The full path of the file, when it was read from disk.</summary>
    public string? Path { get; }

    /// <summary>Every <c>KEY=VALUE</c> line, in file order. A key set twice is listed twice; the later one wins.</summary>
    public IReadOnlyList<DotEnvEntry> Entries { get; }

    /// <summary>Each key's value: the last line that sets it wins.</summary>
    public IReadOnlyDictionary<string, string> Variables { get; }

    /// <summary>Problems that don't stop the file being read, such as a <c>${VAR}</c> that is not set.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Reads and parses the dotenv file at <paramref name="path"/>.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="variables">Variables that <c>${VAR}</c> references and bare <c>KEY</c> lines read, such as the shell's. They win over earlier keys in the file. The process environment is never read.</param>
    /// <exception cref="FormatException">A line is not valid dotenv syntax, or a <c>${VAR:?error}</c> variable is not set.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    public static DotEnvFile Load(string path, IReadOnlyDictionary<string, string?>? variables = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = System.IO.Path.GetFullPath(path);
        return Parse(File.ReadAllText(full), System.IO.Path.GetFileName(full), full, VariablesLookup(variables));
    }

    /// <summary>Parses dotenv text.</summary>
    /// <param name="text">The file's contents.</param>
    /// <param name="name">The name shown in reports and error messages, for example <c>.env</c>.</param>
    /// <param name="variables">Variables that <c>${VAR}</c> references and bare <c>KEY</c> lines read, such as the shell's. They win over earlier keys in the file.</param>
    /// <exception cref="FormatException">A line is not valid dotenv syntax, or a <c>${VAR:?error}</c> variable is not set.</exception>
    public static DotEnvFile Parse(string text, string name, IReadOnlyDictionary<string, string?>? variables = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Parse(text, name, null, VariablesLookup(variables));
    }

    /// <summary>Finds the last entry that sets <paramref name="name"/>, or null.</summary>
    public DotEnvEntry? Find(string name) => Entries.LastOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));

    internal static Func<string, LookupResult?> VariablesLookup(IReadOnlyDictionary<string, string?>? variables) =>
        name => variables is not null && variables.TryGetValue(name, out var value) && value is not null ? new LookupResult(value, null) : null;

    /// <summary>Parses <paramref name="text"/>. <paramref name="lookup"/> is asked first, then earlier keys in the file.</summary>
    /// <param name="text">The file's contents.</param>
    /// <param name="name">The name shown in reports.</param>
    /// <param name="path">The full path, when read from disk.</param>
    /// <param name="lookup">Asked first, like the shell.</param>
    /// <param name="raw">True to keep each value exactly as written, like compose's <c>format: raw</c>.</param>
    /// <param name="fallback">Asked after the file's own earlier keys, for keys set by files read before this one.</param>
    internal static DotEnvFile Parse(string text, string name, string? path, Func<string, LookupResult?> lookup, bool raw = false, Func<string, LookupResult?>? fallback = null)
    {
        var parser = new Parser(text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('\uFEFF'), name, path, lookup, raw, fallback);
        parser.Run();
        return new DotEnvFile(name, path, parser.Entries, parser.Warnings);
    }

    private sealed class Parser(string text, string name, string? path, Func<string, LookupResult?> lookup, bool raw, Func<string, LookupResult?>? fallback)
    {
        private readonly Dictionary<string, DotEnvEntry> _seen = new(StringComparer.Ordinal);
        private int _pos;
        private int _line = 1;

        public List<DotEnvEntry> Entries { get; } = [];

        public List<string> Warnings { get; } = [];

        public void Run()
        {
            while (_pos < text.Length)
            {
                SkipBlanks();
                if (_pos >= text.Length)
                {
                    break;
                }

                if (text[_pos] is '\n' or '#')
                {
                    SkipLine();
                    continue;
                }

                ReadEntry();
            }
        }

        private void ReadEntry()
        {
            var line = _line;
            if (string.CompareOrdinal(text, _pos, "export", 0, 6) == 0 && _pos + 6 < text.Length && text[_pos + 6] is ' ' or '\t')
            {
                _pos += 6;
                SkipBlanks();
            }

            var start = _pos;
            while (_pos < text.Length && IsKeyChar(text[_pos]))
            {
                _pos++;
            }

            var key = text[start.._pos];
            if (key.Length == 0)
            {
                throw Error(line, "expected a KEY=VALUE line");
            }

            SkipBlanks();
            if (_pos >= text.Length || text[_pos] is '\n' or '#')
            {
                // A bare KEY takes its value from the variables passed in, the way Compose reads it from the shell.
                SkipLine();
                if (lookup(key) is { } inherited)
                {
                    Add(new DotEnvEntry(key, inherited.Value, name, line)
                    {
                        Path = path,
                        IsInherited = true,
                        References = [new DotEnvReference(key, inherited.Entry, IsSet: true) { Value = inherited.Value }],
                    });
                }

                return;
            }

            if (text[_pos] != '=')
            {
                throw Error(line, $"expected = after {key}");
            }

            _pos++;
            if (raw)
            {
                var end = LineEnd();
                Add(new DotEnvEntry(key, text[_pos..end], name, line) { Path = path });
                _pos = end;
                SkipLine();
                return;
            }

            var afterEquals = _pos;
            SkipBlanks();
            var interpolation = new Interpolation(Lookup, string.Create(CultureInfo.InvariantCulture, $"{name} line {line}"));
            var value = _pos < text.Length ? text[_pos] switch
            {
                '\'' or '`' => ReadLiteral(text[_pos], line),
                '"' => ReadDoubleQuoted(line, interpolation),
                _ => ReadUnquoted(interpolation, spaced: _pos > afterEquals),
            } : string.Empty;

            Warnings.AddRange(interpolation.Unset.Select(v => string.Create(CultureInfo.InvariantCulture, $"{name} line {line}: {v} is not set, so an empty string is used.")));
            Add(new DotEnvEntry(key, value, name, line) { Path = path, References = interpolation.References });
        }

        private LookupResult? Lookup(string variable) =>
            lookup(variable) ?? (_seen.TryGetValue(variable, out var earlier) ? new LookupResult(earlier.Value, earlier) : fallback?.Invoke(variable));

        private void Add(DotEnvEntry entry)
        {
            Entries.Add(entry);
            _seen[entry.Name] = entry;
        }

        private string ReadLiteral(char quote, int line)
        {
            var close = text.IndexOf(quote, _pos + 1);
            if (close < 0)
            {
                throw Error(line, $"the {quote} quote is not closed");
            }

            var value = text[(_pos + 1)..close];
            CountLines(_pos, close);
            _pos = close + 1;
            EndAfterQuote(line);
            return value;
        }

        private string ReadDoubleQuoted(int line, Interpolation interpolation)
        {
            var sb = new StringBuilder();
            var i = _pos + 1;
            while (true)
            {
                if (i >= text.Length)
                {
                    throw Error(line, "the \" quote is not closed");
                }

                var c = text[i];
                if (c == '"')
                {
                    break;
                }

                if (c == '\\' && i + 1 < text.Length)
                {
                    var escaped = text[i + 1];
                    sb.Append(escaped switch
                    {
                        'n' => "\n",
                        'r' => "\r",
                        't' => "\t",
                        '"' or '\\' or '$' => escaped.ToString(),
                        _ => "\\" + escaped,
                    });
                    i += 2;
                }
                else if (c == '$')
                {
                    // A reference may not run past the closing quote.
                    var close = ClosingQuote(i);
                    var inner = text[..close];
                    interpolation.ExpandAt(inner, ref i, sb, 0);
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }

            CountLines(_pos, i);
            _pos = i + 1;
            EndAfterQuote(line);
            return sb.ToString();
        }

        // The index of the unescaped " that closes the value the reference at start is in, or the end of the text.
        private int ClosingQuote(int start)
        {
            for (var i = start; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                }
                else if (text[i] == '"')
                {
                    return i;
                }
            }

            return text.Length;
        }

        // A # starts a comment only after a space or tab, so A=#x is the value #x but A= #x is empty.
        private string ReadUnquoted(Interpolation interpolation, bool spaced)
        {
            var end = LineEnd();
            var value = text[_pos..end];
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '#' && (i == 0 ? spaced : value[i - 1] is ' ' or '\t'))
                {
                    value = value[..i];
                    break;
                }
            }

            _pos = end;
            SkipLine();
            return interpolation.Expand(value.TrimEnd(' ', '\t', '\r'));
        }

        private void EndAfterQuote(int line)
        {
            SkipBlanks();
            if (_pos < text.Length && text[_pos] is not ('\n' or '#'))
            {
                throw Error(line, "unexpected text after the closing quote");
            }

            SkipLine();
        }

        private void SkipBlanks()
        {
            while (_pos < text.Length && text[_pos] is ' ' or '\t' or '\r')
            {
                _pos++;
            }
        }

        private void SkipLine()
        {
            var end = LineEnd();
            _pos = end < text.Length ? end + 1 : end;
            if (end < text.Length)
            {
                _line++;
            }
        }

        private int LineEnd()
        {
            var end = text.IndexOf('\n', _pos);
            return end < 0 ? text.Length : end;
        }

        private void CountLines(int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                if (text[i] == '\n')
                {
                    _line++;
                }
            }
        }

        private FormatException Error(int line, string message) =>
            new(string.Create(CultureInfo.InvariantCulture, $"{name} line {line}: {message}."));

        private static bool IsKeyChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '/' or '[' or ']';
    }
}

/// <summary>One <c>KEY=VALUE</c> line of a dotenv file.</summary>
/// <param name="Name">The key.</param>
/// <param name="Value">The value after quotes, escapes and interpolation.</param>
/// <param name="File">The file's name as shown in reports.</param>
/// <param name="Line">The line the entry starts on, from 1.</param>
public sealed record DotEnvEntry(string Name, string Value, string File, int Line)
{
    /// <summary>The file's full path, when it was read from disk.</summary>
    public string? Path { get; init; }

    /// <summary>True for a bare <c>KEY</c> line, whose value came from the variables passed in.</summary>
    public bool IsInherited { get; init; }

    /// <summary>The variables the value refers to with <c>${VAR}</c>, in order, each once.</summary>
    public IReadOnlyList<DotEnvReference> References { get; init; } = [];

    /// <summary>The key, file and line. The value is left out because it may be secret.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Name} ({File} line {Line})");
}

/// <summary>A <c>${VAR}</c> reference in a dotenv value or a Compose file.</summary>
/// <param name="Name">The variable's name.</param>
/// <param name="From">The dotenv entry that set it, or null when it came from the variables passed in (the shell) or is not set.</param>
/// <param name="IsSet">False when the variable is not set anywhere.</param>
/// <param name="UsedDefault">True when the reference's own default (<c>${VAR:-default}</c>) was used.</param>
public sealed record DotEnvReference(string Name, DotEnvEntry? From, bool IsSet, bool UsedDefault = false)
{
    /// <summary>The variable's value, or null when it is not set.</summary>
    public string? Value { get; init; }

    /// <summary>The name and where it came from. The value is left out because it may be secret.</summary>
    public override string ToString() => IsSet ? $"${{{Name}}} from {From?.ToString() ?? "the variables passed in"}" : $"${{{Name}}} (not set)";
}
