using System.Globalization;
using System.Text;

namespace ConfigFerret.Compose;

/// <summary>Where an interpolated variable's value came from: a dotenv entry, or the variables passed in (the shell).</summary>
/// <param name="Value">The value.</param>
/// <param name="Entry">The dotenv entry that set it, or null when it came from the variables passed in.</param>
internal sealed record LookupResult(string Value, DotEnvEntry? Entry);

/// <summary>
/// Expands <c>$VAR</c>, <c>${VAR}</c>, <c>${VAR:-default}</c>, <c>${VAR-default}</c>, <c>${VAR:?error}</c>,
/// <c>${VAR?error}</c>, <c>${VAR:+other}</c>, <c>${VAR+other}</c> and <c>$$</c> the way Docker Compose does.
/// A <c>$</c> that starts none of these is kept as it is.
/// </summary>
internal sealed class Interpolation(Func<string, LookupResult?> lookup, string where)
{
    /// <summary>How deeply defaults may nest, as in <c>${A:-${B:-${C}}}</c>. Deeper input is rejected instead of overflowing the stack.</summary>
    internal const int MaxNesting = 16;

    private readonly List<DotEnvReference> _references = [];

    /// <summary>Every variable the text referred to, in order, each name once.</summary>
    public IReadOnlyList<DotEnvReference> References => _references;

    /// <summary>The names of referenced variables that were not set and had no default, in order.</summary>
    public List<string> Unset { get; } = [];

    /// <summary>Expands every reference in <paramref name="text"/>.</summary>
    /// <exception cref="FormatException">A reference is malformed, or a <c>:?</c> reference is not set.</exception>
    public string Expand(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '$')
            {
                ExpandAt(text, ref i, sb, 0);
            }
            else
            {
                sb.Append(text[i++]);
            }
        }

        return sb.ToString();
    }

    /// <summary>Expands the reference that starts at the <c>$</c> at <paramref name="index"/> and moves past it.</summary>
    public void ExpandAt(string text, ref int index, StringBuilder sb, int nesting)
    {
        var next = index + 1 < text.Length ? text[index + 1] : '\0';
        if (next == '$')
        {
            sb.Append('$');
            index += 2;
            return;
        }

        if (IsNameStart(next))
        {
            var end = index + 1;
            while (end < text.Length && IsNameChar(text[end]))
            {
                end++;
            }

            sb.Append(Lookup(text[(index + 1)..end]));
            index = end;
            return;
        }

        if (next != '{')
        {
            sb.Append('$');
            index++;
            return;
        }

        var close = FindClose(text, index + 2);
        var body = text[(index + 2)..close];
        index = close + 1;
        sb.Append(ExpandBraced(body, nesting));
    }

    private string ExpandBraced(string body, int nesting)
    {
        if (nesting >= MaxNesting)
        {
            throw Error($"the reference nests more than {MaxNesting} defaults deep");
        }

        var nameEnd = 0;
        while (nameEnd < body.Length && IsNameChar(body[nameEnd]))
        {
            nameEnd++;
        }

        var name = body[..nameEnd];
        if (name.Length == 0 || !IsNameStart(name[0]))
        {
            // Only the name is shown: the rest of the text could be a default that holds a secret.
            throw Error(name.Length == 0 ? "'${' is not followed by a variable name" : $"'{Shorten(name)}' is not a valid variable name");
        }

        var rest = body[nameEnd..];
        if (rest.Length == 0)
        {
            return Lookup(name);
        }

        var colon = rest[0] == ':';
        var op = colon ? (rest.Length > 1 ? rest[1] : '\0') : rest[0];
        if (op is not ('-' or '?' or '+'))
        {
            throw Error($"the reference to {name} is not valid: after the name, expect }}, :-, -, :?, ?, :+ or +");
        }

        var argument = rest[(colon ? 2 : 1)..];
        var found = lookup(name);
        var isSet = found is not null && (!colon || found.Value.Length > 0);
        switch (op)
        {
            case '-':
                Record(name, found, usedDefault: !isSet);
                return isSet ? found!.Value : ExpandNested(argument, nesting);
            case '+':
                Record(name, found, usedDefault: false);
                return isSet ? ExpandNested(argument, nesting) : string.Empty;
            default:
                if (!isSet)
                {
                    // The message is the file author's text. It is shown as written, not expanded, so no value can
                    // reach the exception.
                    var message = argument;
                    throw Error(message.Length > 0 ? $"{name}: {message}" : $"{name} is required but not set");
                }

                Record(name, found, usedDefault: false);
                return found!.Value;
        }
    }

    private string ExpandNested(string text, int nesting)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '$')
            {
                ExpandAt(text, ref i, sb, nesting + 1);
            }
            else
            {
                sb.Append(text[i++]);
            }
        }

        return sb.ToString();
    }

    private string Lookup(string name)
    {
        var found = lookup(name);
        Record(name, found, usedDefault: false);
        if (found is null)
        {
            if (!Unset.Contains(name, StringComparer.Ordinal))
            {
                Unset.Add(name);
            }

            return string.Empty;
        }

        return found.Value;
    }

    private void Record(string name, LookupResult? found, bool usedDefault)
    {
        if (_references.Exists(r => string.Equals(r.Name, name, StringComparison.Ordinal)))
        {
            return;
        }

        _references.Add(new DotEnvReference(name, found?.Entry, found is not null, usedDefault) { Value = found?.Value });
    }

    // The closing brace of a reference whose body starts at start, counting nested ${...}.
    private int FindClose(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '$' && i + 1 < text.Length && text[i + 1] == '{')
            {
                depth++;
                i++;
            }
            else if (text[i] == '}')
            {
                if (depth == 0)
                {
                    return i;
                }

                depth--;
            }
        }

        throw Error("a ${ is not closed with }");
    }

    private FormatException Error(string message) => new(string.Create(CultureInfo.InvariantCulture, $"{where}: {message}."));

    private static string Shorten(string text) => text.Length > 40 ? text[..40] + "..." : text;

    internal static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_';

    internal static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
