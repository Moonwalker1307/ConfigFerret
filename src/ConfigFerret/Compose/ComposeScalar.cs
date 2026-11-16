using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ConfigFerret.Compose;

/// <summary>
/// The text compose passes for a YAML scalar in the map form of <c>environment</c>. Compose decodes the YAML into
/// typed values first, the way go-yaml resolves a scalar, and then formats each value with Go's <c>fmt.Sprint</c>:
/// <c>3.10</c> is the number 3.1, <c>True</c> is true, <c>0755</c> is 493 and <c>2001-01-01</c> is the time
/// <c>2001-01-01 00:00:00 +0000 UTC</c>.
/// </summary>
internal static partial class ComposeScalar
{
    /// <summary>The tag of a YAML integer, <c>!!int</c>.</summary>
    public const string IntTag = "tag:yaml.org,2002:int";

    /// <summary>The tag of a YAML float, <c>!!float</c>.</summary>
    public const string FloatTag = "tag:yaml.org,2002:float";

    /// <summary>The tag of a YAML boolean, <c>!!bool</c>.</summary>
    public const string BoolTag = "tag:yaml.org,2002:bool";

    /// <summary>The tag of a YAML date, <c>!!timestamp</c>.</summary>
    public const string TimestampTag = "tag:yaml.org,2002:timestamp";

    private enum Kind
    {
        Bool,
        Int,

        // An integer beyond int64, which go-yaml keeps as a uint64 and does not turn into a float for !!float.
        Uint,
        Float,
        Timestamp,
    }

    /// <summary>
    /// The text compose passes for a scalar, or null when it passes the text as written. <paramref name="tag"/> is null
    /// for a plain scalar without a tag, or <see cref="IntTag"/>, <see cref="FloatTag"/>, <see cref="BoolTag"/> or
    /// <see cref="TimestampTag"/>. It is also null for a value that does not fit its tag, which go-yaml refuses.
    /// </summary>
    public static string? Format(string text, string? tag = null)
    {
        if (Resolve(text) is not var (kind, formatted, number))
        {
            return null;
        }

        return tag switch
        {
            null => formatted,
            IntTag => kind is Kind.Int or Kind.Uint ? formatted : null,
            BoolTag => kind == Kind.Bool ? formatted : null,
            FloatTag => kind == Kind.Float ? formatted : kind == Kind.Int ? FormatFloat(number) : null,
            TimestampTag => kind == Kind.Timestamp ? formatted : null,
            _ => null,
        };
    }

    // What go-yaml resolves a scalar to, formatted the way Go prints it, with the number for an int or a float.
    private static (Kind Kind, string Text, double Number)? Resolve(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        switch (text)
        {
            case "true" or "True" or "TRUE":
                return (Kind.Bool, "true", 0);
            case "false" or "False" or "FALSE":
                return (Kind.Bool, "false", 0);
            case ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF":
                return (Kind.Float, "+Inf", double.PositiveInfinity);
            case "-.inf" or "-.Inf" or "-.INF":
                return (Kind.Float, "-Inf", double.NegativeInfinity);
            case ".nan" or ".NaN" or ".NAN":
                return (Kind.Float, "NaN", double.NaN);
            case "-0" or "-0.0":
                return (Kind.Float, "-0", -0.0);
        }

        var first = text[0];
        if (first == '.')
        {
            return Float(text) is { } value ? (Kind.Float, FormatFloat(value), value) : null;
        }

        if (!char.IsAsciiDigit(first) && first is not ('+' or '-'))
        {
            return null;
        }

        if (char.IsAsciiDigit(first) && Timestamp(text) is { } timestamp)
        {
            return (Kind.Timestamp, timestamp, 0);
        }

        var plain = text.Replace("_", string.Empty, StringComparison.Ordinal);
        if (Integer(plain) is { } integer)
        {
            return integer;
        }

        return Float(plain) is { } parsed ? (Kind.Float, FormatFloat(parsed), parsed) : null;
    }

    // Like Go's strconv.ParseInt(text, 0, 64), then ParseUint: a sign, then 0x, 0o, 0b or a leading 0 for octal. Each
    // digit is read once and the loop stops at the first value beyond 64 bits.
    private static (Kind Kind, string Text, double Number)? Integer(string text)
    {
        var negative = text[0] == '-';
        var sign = text[0] is '+' or '-' ? 1 : 0;
        var start = sign;
        var radix = 10;
        if (text.Length - sign > 1 && text[sign] == '0')
        {
            (radix, start) = text[sign + 1] switch
            {
                'x' or 'X' => (16, sign + 2),
                'o' or 'O' => (8, sign + 2),
                'b' or 'B' => (2, sign + 2),
                _ => (8, sign + 1),
            };
        }

        var digits = text.AsSpan(start);
        if (digits.IsEmpty)
        {
            return null;
        }

        UInt128 value = 0;
        foreach (var c in digits)
        {
            var digit = char.IsAsciiDigit(c) ? c - '0' : char.IsAsciiHexDigit(c) ? (c | 0x20) - 'a' + 10 : int.MaxValue;
            if (digit >= radix)
            {
                return null;
            }

            value = (value * (uint)radix) + (uint)digit;
            if (value > ulong.MaxValue)
            {
                return null;
            }
        }

        // ParseUint takes no sign, so a signed number is in range only as an int64.
        if (sign == 1 && value > (negative ? (UInt128)long.MaxValue + 1 : long.MaxValue))
        {
            return null;
        }

        var shown = value.ToString(CultureInfo.InvariantCulture);
        return negative && value != 0 ? (Kind.Int, "-" + shown, -(double)value)
            : (value > long.MaxValue ? Kind.Uint : Kind.Int, shown, (double)value);
    }

    // go-yaml's yamlStyleFloat, then Go's ParseFloat, which fails out of range.
    private static double? Float(string text) =>
        FloatPattern().IsMatch(text)
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value)
            ? value
            : null;

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex FloatPattern();

    // Like Go's %v for a float64: the shortest digits that read back the same, as %e when the exponent is below -4 or
    // at least 6, else as %f.
    private static string FormatFloat(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "+Inf" : "-Inf";
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0" : "0";
        }

        // "E16" is not shortest, so take the shortest round-trip text and read its digits and exponent from it.
        var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var e = shortest.IndexOf('E', StringComparison.Ordinal);
        var mantissa = e < 0 ? shortest : shortest[..e];
        var exponent = e < 0 ? 0 : int.Parse(shortest[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var all = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        var point = (dot < 0 ? mantissa.Length : dot) + exponent;
        var digits = all.TrimStart('0');
        point -= all.Length - digits.Length;
        digits = digits.TrimEnd('0');

        var text = new StringBuilder(value < 0 ? "-" : string.Empty);
        var exp = point - 1;
        if (exp < -4 || exp >= 6)
        {
            text.Append(digits[0]);
            if (digits.Length > 1)
            {
                text.Append('.').Append(digits, 1, digits.Length - 1);
            }

            text.Append(exp < 0 ? "e-" : "e+").Append(Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture));
        }
        else if (point <= 0)
        {
            text.Append("0.").Append('0', -point).Append(digits);
        }
        else if (point >= digits.Length)
        {
            text.Append(digits).Append('0', point - digits.Length);
        }
        else
        {
            text.Append(digits, 0, point).Append('.').Append(digits, point, digits.Length - point);
        }

        return text.ToString();
    }

    // go-yaml's timestamps, which Go's time.Parse reads with these layouts: "2006-1-2T15:4:5.999999999Z07:00" (also
    // with a lower-case t), "2006-1-2 15:4:5.999999999" and "2006-1-2". The value is then formatted like Go's
    // Time.String. A zone offset other than Z is shown as a fixed zone: compose shows the zone of the machine it runs on
    // instead when that has the same offset at that time.
    private static string? Timestamp(string text)
    {
        var match = TimestampPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        int Number(string group) => match.Groups[group].Success ? int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) : 0;
        var (year, month, day) = (Number("year"), Number("month"), Number("day"));
        var separator = match.Groups["sep"].Value;
        var zone = match.Groups["zone"].Value;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Max(year, 1), month)
            || (separator is "T" or "t" && zone.Length == 0) || (separator == " " && zone.Length > 0))
        {
            return null;
        }

        var (hour, minute, second) = (Number("hour"), Number("minute"), Number("second"));
        if (hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        var fraction = match.Groups["fraction"].Value;
        fraction = (fraction.Length > 9 ? fraction[..9] : fraction).TrimEnd('0');
        var shown = string.Create(CultureInfo.InvariantCulture, $"{year:0000}-{month:00}-{day:00} {hour:00}:{minute:00}:{second:00}");
        if (fraction.Length > 0)
        {
            shown += "." + fraction;
        }

        if (zone.Length == 0 || zone == "Z")
        {
            return shown + " +0000 UTC";
        }

        var offsetHours = int.Parse(zone.AsSpan(1, 2), CultureInfo.InvariantCulture);
        var offsetMinutes = int.Parse(zone.AsSpan(4, 2), CultureInfo.InvariantCulture);
        if (offsetHours > 24 || offsetMinutes > 60)
        {
            return null;
        }

        var offset = string.Concat(zone.AsSpan(0, 3), zone.AsSpan(4, 2));
        return $"{shown} {offset} {offset}";
    }

    [GeneratedRegex(@"^(?<year>[0-9]{4})-(?<month>[0-9]{1,2})-(?<day>[0-9]{1,2})(?:(?<sep>[Tt ])(?<hour>[0-9]{1,2}):(?<minute>[0-9]{1,2}):(?<second>[0-9]{1,2})(?:[.,](?<fraction>[0-9]+))?(?<zone>Z|[-+][0-9]{2}:[0-9]{2})?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();
}
