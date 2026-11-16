using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ConfigFerret.Desktop.Services;

/// <summary>Why a key counts as read, or why it does not.</summary>
/// <param name="IsRead">True when code reads the key.</param>
/// <param name="Reason">For a key that is not read, why: for example "No code reads it".</param>
public sealed record ReadVerdict(bool IsRead, string Reason);

/// <summary>
/// The configuration keys an app's C# code reads, found by reading the source: <c>config["A:B"]</c>,
/// <c>GetValue&lt;T&gt;("A:B")</c>, <c>GetSection("A")</c>, <c>GetConnectionString("Db")</c>, and sections bound to options
/// classes with <c>Configure&lt;T&gt;</c>, <c>Bind</c>, <c>Get&lt;T&gt;</c> or <c>BindConfiguration</c>. For a bound section,
/// only keys that match a property of the options class count as read.
/// </summary>
/// <remarks>
/// This reads text, not compiled code, so it can miss a read: a key built at run time, or read by a library. Such reads
/// are counted in <see cref="DynamicReads"/>, and keys that ASP.NET Core and common libraries read themselves, such as
/// <c>Logging</c> and <c>AllowedHosts</c>, always count as read.
/// </remarks>
public sealed partial class CodeReadScanner
{
    // Keys the host and common libraries read without app code asking for them.
    private static readonly string[] FrameworkSections =
    [
        "Logging", "AllowedHosts", "Kestrel", "Urls", "HTTP_PORTS", "HTTPS_PORTS", "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS",
        "ASPNETCORE_HTTPS_PORTS", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "environment", "applicationName",
        "contentRoot", "webroot", "DetailedErrors", "ShutdownTimeoutSeconds", "HostingStartupAssemblies",
        "HostingStartupExcludeAssemblies", "PreventHostingStartup", "SuppressStatusMessages", "https_port", "PathBase",
    ];

    private const int MaxFiles = 5000;
    private const long MaxFileBytes = 1024 * 1024;

    // The patterns are linear, so this only stops a file that a pattern somehow still can't finish: the file is skipped.
    private const int MatchTimeoutMilliseconds = 10_000;

    // How many base classes a class takes properties from, so a long chain of them can't make the scan quadratic.
    private const int MaxBaseClasses = 16;

    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _bound = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OptionsShape> _shapes = new(StringComparer.Ordinal);
    private readonly List<(string File, int Line)> _dynamic = [];
    private readonly List<string> _skipped = [];
    private CancellationToken _cancellationToken;
    private int _steps;

    private CodeReadScanner()
    {
    }

    /// <summary>How many C# files were read.</summary>
    public int FilesScanned { get; private set; }

    /// <summary>C# files the scan gave up on because reading them took too long, so a key only they read counts as not read.</summary>
    public IReadOnlyList<string> SkippedFiles => _skipped;

    /// <summary>Where code reads a key the scanner cannot work out, such as <c>config[name]</c>, as file and line.</summary>
    public IReadOnlyList<string> DynamicReads => _dynamic.OrderBy(d => d.File, StringComparer.Ordinal).ThenBy(d => d.Line).Select(d => $"{d.File} (line {d.Line})").Distinct().ToList();

    /// <summary>The exact keys read, such as <c>Cache:Ttl</c>.</summary>
    public IReadOnlyCollection<string> Keys => _keys;

    /// <summary>Sections read as a whole, such as <c>Logging</c>.</summary>
    public IReadOnlyCollection<string> Sections => _sections;

    /// <summary>Sections bound to an options class, and the class name.</summary>
    public IReadOnlyDictionary<string, string> BoundSections => _bound;

    /// <summary>
    /// Reads the C# files in <paramref name="folder"/> and in the projects its project files reference, skipping
    /// <c>bin</c>, <c>obj</c> and test projects.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static CodeReadScanner Scan(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var scanner = new CodeReadScanner();
        var sources = new List<(string Path, string Text)>();
        foreach (var file in SourceFiles(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(file).Length <= MaxFileBytes)
                {
                    sources.Add((file, File.ReadAllText(file)));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        scanner.Read(sources.Select(s => (Path.GetRelativePath(folder, s.Path), s.Text)), null, cancellationToken);
        return scanner;
    }

    /// <summary>Reads C# sources given as text, for tests and callers that already have the files.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static CodeReadScanner FromSources(IEnumerable<(string Name, string Text)> sources, CancellationToken cancellationToken = default) =>
        FromSources(sources, null, cancellationToken);

    // For tests: atStep runs with the file name and "types", "properties" or "calls" where a pattern could time out.
    internal static CodeReadScanner FromSources(IEnumerable<(string Name, string Text)> sources, Action<string, string>? atStep, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var scanner = new CodeReadScanner();
        scanner.Read(sources, atStep, cancellationToken);
        return scanner;
    }

    /// <summary>Tells whether code reads <paramref name="key"/>, and if not, why.</summary>
    public ReadVerdict Check(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        key = key.Replace("__", ":", StringComparison.Ordinal);
        if (_keys.Contains(key))
        {
            return new ReadVerdict(true, string.Empty);
        }

        var segments = key.Split(':');
        for (var i = 1; i <= segments.Length; i++)
        {
            var prefix = string.Join(':', segments.Take(i));
            if (_sections.Contains(prefix) || (i == 1 && FrameworkSections.Contains(prefix, StringComparer.OrdinalIgnoreCase)))
            {
                return new ReadVerdict(true, string.Empty);
            }

            if (_bound.TryGetValue(prefix, out var type))
            {
                var missing = MissingProperty(type, segments.Skip(i).ToList());
                if (missing is null)
                {
                    return new ReadVerdict(true, string.Empty);
                }

                return new ReadVerdict(false, $"{prefix} is bound to {missing.Value.Type}, which has no property {missing.Value.Name}");
            }
        }

        return new ReadVerdict(false, FilesScanned == 0 ? "No C# code found to read it" : "No code reads it");
    }

    // The first segment the options class can't take, or null when the binder sets a property from it.
    private (string Type, string Name)? MissingProperty(string type, IReadOnlyList<string> rest)
    {
        var current = type;
        foreach (var segment in rest)
        {
            if (!_shapes.TryGetValue(current, out var shape))
            {
                return null; // A class we can't see, or a collection: the binder may take anything under it.
            }

            if (!TryGetProperty(shape, segment, out var propertyType, out var open))
            {
                return open ? null : (current, segment);
            }

            if (propertyType is null)
            {
                return null; // A plain value, a collection or a dictionary.
            }

            current = propertyType;
        }

        return null;
    }

    // Looks a property up in the class and then in its bases, at most MaxBaseClasses up, so no class copies the
    // properties of its bases. Open is true when the class or a base it reached may have properties the scan can't see.
    private bool TryGetProperty(OptionsShape shape, string name, out string? type, out bool open)
    {
        open = false;
        var seen = new HashSet<OptionsShape> { shape };
        var current = shape;
        while (true)
        {
            if (current.Properties.TryGetValue(name, out type))
            {
                return true;
            }

            open |= current.Open;
            if (current.Base is null || !_shapes.TryGetValue(current.Base, out var parent))
            {
                break;
            }

            // A cycle doesn't compile, and adds nothing more to find.
            if (!seen.Add(parent))
            {
                break;
            }

            // A base past the last one followed may hold any property.
            if (seen.Count > MaxBaseClasses + 1)
            {
                open = true;
                break;
            }

            current = parent;
        }

        type = null;
        return false;
    }

    private void Read(IEnumerable<(string Name, string Text)> sources, Action<string, string>? atStep, CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        var files = new List<(string Name, string Text)>();
        foreach (var (name, text) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add((name, StripComments(text)));
        }

        // A file a pattern times out on is skipped from then on. What it gave before the timeout stays.
        var skipped = new bool[files.Count];
        // The classes of a file skipped in the first pass may have lost properties, so they are left open.
        var constants = new Constants();
        var typeBodies = new List<TypeBody>[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            var (name, text) = files[i];
            var declared = new List<OptionsShape>();
            var bodies = typeBodies[i] = [];
            skipped[i] = !TryRead(name, "types", () =>
            {
                ReadShapes(text, declared, bodies, () => atStep?.Invoke(name, "properties"));
                var sweep = new TypeSweep(bodies);
                foreach (Match m in StringConstant().Matches(text))
                {
                    Step();
                    var value = m.Groups["value"].Success ? m.Groups["value"].Value : m.Groups["nameof"].Value.Split('.')[^1];
                    constants.Add(sweep.At(m.Index)?.Name, m.Groups["name"].Value, value);
                }
            });

            if (skipped[i])
            {
                declared.ForEach(shape => shape.Open = true);
            }
        }

        for (var i = 0; i < files.Count; i++)
        {
            var (name, text) = files[i];
            var bodies = typeBodies[i];
            skipped[i] = skipped[i] || !TryRead(name, "calls", () => ReadCalls(name, text, constants, bodies));
        }

        FilesScanned = skipped.Count(s => !s);
        _skipped.AddRange(files.Where((_, i) => skipped[i]).Select(f => f.Name));

        bool TryRead(string name, string step, Action read)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                atStep?.Invoke(name, step);
                read();
                return true;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }
    }

    // Called for each match in a file, so a cancelled scan stops inside a long file too.
    private void Step()
    {
        if (++_steps % 1024 == 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void ReadCalls(string file, string text, Constants constants, List<TypeBody> bodies)
    {
        var consumed = new List<(int Start, int End)>();
        var newlines = new List<int>();
        for (var i = text.IndexOf('\n', StringComparison.Ordinal); i >= 0; i = text.IndexOf('\n', i + 1))
        {
            newlines.Add(i);
        }

        // The chains are added in text order and never overlap, so the one that may hold a position is found by halving.
        bool Consumed(int index)
        {
            var low = 0;
            var high = consumed.Count - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                if (consumed[middle].End <= index)
                {
                    low = middle + 1;
                }
                else if (consumed[middle].Start > index)
                {
                    high = middle - 1;
                }
                else
                {
                    return true;
                }
            }

            return false;
        }

        var covered = new HashSet<int>();

        // A bare constant name resolves first in the type around the read.
        var types = new TypeSweep(bodies);
        string? Key(Match match) => Resolve(match.Groups["arg"].Value, constants, types.At(match.Index)?.Name);

        void Dynamic(int index)
        {
            var before = newlines.BinarySearch(index);
            _dynamic.Add((file, (before >= 0 ? before : ~before) + 1));
        }

        foreach (Match m in SectionCall().Matches(text))
        {
            Step();

            // A GetSection that continues an earlier chain, such as the second one in GetRequiredSection("A").GetSection("B").
            if (Consumed(m.Index))
            {
                continue;
            }

            var path = Key(m);
            var end = m.Index + m.Length;
            if (path is null)
            {
                Dynamic(m.Index);
            }

            // Follow the chain: .GetSection("B"), ["C"], .GetValue<T>("D"), .Get<T>(), .Bind(x). A chain that starts
            // with a key the scanner cannot work out is followed too, so its tail is not read as a key of its own.
            var exact = false;
            string? boundType = OptionsTypeBefore(text, m.Index);
            while (true)
            {
                var next = ChainStep().Match(text, end);
                if (!next.Success || next.Index != end)
                {
                    break;
                }

                var arg = next.Groups["arg"].Success ? Key(next) : null;
                if (next.Groups["section"].Success || next.Groups["index"].Success || next.Groups["value"].Success)
                {
                    if (arg is null)
                    {
                        if (path is not null)
                        {
                            Dynamic(next.Index);
                        }

                        path = null;
                    }
                    else if (path is not null)
                    {
                        path = path + ":" + arg;
                    }

                    end = next.Index + next.Length;
                    if (!next.Groups["section"].Success)
                    {
                        exact = true;
                        break;
                    }
                }
                else
                {
                    if (next.Groups["get"].Success)
                    {
                        boundType = next.Groups["get"].Value;
                    }

                    end = next.Index + next.Length;
                    break;
                }
            }

            consumed.Add((m.Index, end));
            if (path is null)
            {
                continue;
            }

            if (exact)
            {
                _keys.Add(path);
            }
            else if (boundType is not null)
            {
                Bind(path, boundType);
            }
            else
            {
                _sections.Add(path);
            }
        }

        foreach (Match m in ValueCall().Matches(text))
        {
            Step();
            covered.Add(m.Index);
            if (Consumed(m.Index))
            {
                continue;
            }

            var key = Key(m);
            if (key is null)
            {
                Dynamic(m.Index);
                continue;
            }

            if (m.Groups["kind"].Value == "GetConnectionString")
            {
                _keys.Add("ConnectionStrings:" + key);
            }
            else if (m.Groups["kind"].Value == "BindConfiguration")
            {
                var type = OptionsTypeBefore(text, m.Index);
                if (type is null)
                {
                    _sections.Add(key);
                }
                else
                {
                    Bind(key, type);
                }
            }
            else
            {
                _keys.Add(key);
            }
        }

        foreach (Match m in ConfigIndexer().Matches(text))
        {
            Step();
            covered.Add(m.Index);
            if (Consumed(m.Index))
            {
                continue;
            }

            var key = Key(m);
            if (key is null)
            {
                Dynamic(m.Index);
            }
            else
            {
                _keys.Add(key);
            }
        }

        // Libraries that take the configuration and a section name, such as AddMicrosoftIdentityWebApi(builder.Configuration, "AzureAd").
        foreach (Match m in SectionNameArgument().Matches(text))
        {
            Step();
            if (!Consumed(m.Index) && Key(m) is { } section)
            {
                _sections.Add(section);
            }
        }

        // Code that hands the whole configuration to a library, such as Serilog's ReadFrom.Configuration.
        if (text.Contains("ReadFrom.Configuration", StringComparison.Ordinal))
        {
            _sections.Add("Serilog");
        }

        // A read the patterns above could not take apart, such as GetSection(string.Join(":", parts.Take(2))) or
        // GetValue<List<string>>("A"), is listed, so no key counts as dead because of code the scan could not read.
        foreach (Match m in ReadStart().Matches(text))
        {
            Step();
            if (!covered.Contains(m.Index) && !Consumed(m.Index))
            {
                Dynamic(m.Index);
            }
        }
    }

    private void Bind(string path, string type)
    {
        // A section bound to two classes, or bound and also read whole, counts as read whole.
        if (_sections.Contains(path))
        {
            return;
        }

        if (_bound.TryGetValue(path, out var existing) && existing != type)
        {
            _bound.Remove(path);
            _sections.Add(path);
            return;
        }

        _bound[path] = type;
    }

    // Configure<T>(...GetSection, AddOptions<T>().Bind(...GetSection, AddOptions<T>().BindConfiguration(...
    private static string? OptionsTypeBefore(string text, int index)
    {
        var start = Math.Max(0, index - 200);
        var before = text[start..index];
        var m = OptionsTypeTail().Match(before);
        return m.Success ? Simple(m.Groups["type"].Value) : null;
    }

    private static string? Resolve(string arg, Constants constants, string? type)
    {
        arg = arg.Trim();
        if (arg.Length >= 2 && arg[0] == '"' && arg[^1] == '"')
        {
            var value = arg[1..^1];
            return value.Length == 0 || value.Contains('{', StringComparison.Ordinal) ? null : value;
        }

        if (arg.Length >= 3 && arg.StartsWith("@\"", StringComparison.Ordinal) && arg[^1] == '"')
        {
            return arg[2..^1];
        }

        var nameOf = NameOf().Match(arg);
        if (nameOf.Success)
        {
            return nameOf.Groups["name"].Value.Split('.')[^1];
        }

        if (Identifier().IsMatch(arg))
        {
            return constants.Resolve(arg, type);
        }

        return null;
    }

    // Reads the types in text into _shapes and their bodies, in text order, into bodies. Each shape goes into declared as
    // soon as it is known.
    private void ReadShapes(string text, List<OptionsShape> declared, List<TypeBody> bodies, Action beforeProperties)
    {
        Dictionary<int, int>? closes = null;
        var parsed = new List<(int Start, int End)>();
        foreach (Match m in TypeDeclaration().Matches(text))
        {
            Step();
            var name = m.Groups["name"].Value;
            parsed.Add((m.Index, m.Index + m.Length));
            var shape = new OptionsShape(m.Groups["base"].Success ? Simple(m.Groups["base"].Value.Split(',')[0]) : null);
            declared.Add(shape);

            if (m.Groups["parameters"].Success)
            {
                foreach (var parameter in SplitTopLevel(m.Groups["parameters"].Value))
                {
                    var p = Parameter().Match(parameter);
                    if (p.Success)
                    {
                        shape.Properties[p.Groups["name"].Value] = PropertyType(p.Groups["type"].Value);
                    }
                }
            }

            // A brace inside a string, such as "class A {", is no body.
            var open = m.Index + m.Length - 1;
            closes ??= text[open] == '{' ? MatchBraces(text) : null;
            if (closes is not null && closes.TryGetValue(open, out var close))
            {
                bodies.Add(new TypeBody(name, shape, open, close));
            }

            _shapes.TryAdd(name, shape);
        }

        // A type the pattern above could not take apart, such as one with a where clause over 1000 characters, may have
        // properties the scan can't see, so it is left open.
        var next = 0;
        foreach (Match m in TypeStart().Matches(text))
        {
            Step();
            while (next < parsed.Count && parsed[next].End <= m.Index)
            {
                next++;
            }

            if (!m.Groups["interface"].Success && (next == parsed.Count || parsed[next].Start > m.Index))
            {
                var name = m.Groups["name"].Value;
                if (!_shapes.TryGetValue(name, out var shape))
                {
                    shape = new OptionsShape(null);
                    _shapes.Add(name, shape);
                }

                shape.Open = true;
                declared.Add(shape);
            }
        }

        if (bodies.Count == 0)
        {
            return;
        }

        beforeProperties();

        // A property belongs to the innermost type around it. The text is matched once, however deep types nest.
        var sweep = new TypeSweep(bodies);
        var matched = new List<(int Start, int End)>();
        foreach (Match p in Property().Matches(text))
        {
            Step();
            matched.Add((p.Index, p.Index + p.Length));
            if (sweep.At(p.Index) is { } body)
            {
                var key = p.Groups["key"].Success ? p.Groups["key"].Value : p.Groups["name"].Value;
                body.Shape.Properties[key] = PropertyType(p.Groups["type"].Value);
            }
        }

        // A property the pattern above can't take apart, such as one of a tuple type, a global:: type or a type of more
        // than 11 words, may take any key, so its class is left open.
        next = 0;
        foreach (Match p in PropertyStart().Matches(text))
        {
            Step();
            while (next < matched.Count && matched[next].End <= p.Index)
            {
                next++;
            }

            if ((next == matched.Count || matched[next].Start > p.Index) && sweep.At(p.Index) is { } body)
            {
                body.Shape.Open = true;
            }
        }

        // ConfigurationKeyName("x") names a key the class takes, wherever it stands, such as after more attributes than
        // the pattern above takes. A name the scan can't read leaves the class open.
        foreach (Match k in ConfigurationKeyName().Matches(text))
        {
            Step();
            if (sweep.At(k.Index) is not { } body)
            {
                continue;
            }

            if (k.Groups["key"].Success)
            {
                body.Shape.Properties.TryAdd(k.Groups["key"].Value, null);
            }
            else
            {
                body.Shape.Open = true;
            }
        }
    }

    // The class to follow into, or null for a value, collection or dictionary, which take any key below them.
    private static string? PropertyType(string type)
    {
        type = type.Trim().TrimEnd('?');
        if (type.Contains('<', StringComparison.Ordinal) || type.EndsWith(']'))
        {
            return null;
        }

        return Simple(type);
    }

    private static string Simple(string type)
    {
        type = type.Trim().TrimEnd('?');
        var generic = type.IndexOf('<', StringComparison.Ordinal);
        if (generic >= 0)
        {
            type = type[..generic];
        }

        return type.Split('.')[^1].Trim();
    }

    // Where each { closes, or the end of the text for one that never does, in one pass. Braces in strings and characters,
    // as StripComments finds them, don't count.
    private static Dictionary<int, int> MatchBraces(string text)
    {
        var closes = new Dictionary<int, int>();
        var open = new Stack<int>();
        for (var i = 0; i < text.Length; i++)
        {
            if (LiteralEnd(text, i) is var end && end > i)
            {
                i = end - 1;
            }
            else if (text[i] == '{')
            {
                open.Push(i);
            }
            else if (text[i] == '}' && open.Count > 0)
            {
                closes[open.Pop()] = i;
            }
        }

        while (open.Count > 0)
        {
            closes[open.Pop()] = text.Length;
        }

        return closes;
    }

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<' or '(' or '[':
                    depth++;
                    break;
                case '>' or ')' or ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(text[start..i]);
                    start = i + 1;
                    break;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    // Removes // and /* */ comments, keeping strings and line breaks, so commented-out reads don't count.
    internal static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && next == '*')
            {
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    if (text[i] == '\n')
                    {
                        sb.Append('\n');
                    }

                    i++;
                }

                i += 2;
            }
            else if (LiteralEnd(text, i) is var end && end > i)
            {
                sb.Append(text, i, Math.Min(end, text.Length) - i);
                i = end;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    // Where the string or character that starts at i ends, which can be past the end of the text, or i when none starts there.
    private static int LiteralEnd(string text, int i)
    {
        var c = text[i];
        var next = i + 1 < text.Length ? text[i + 1] : '\0';
        if (c == '"' || (c == '@' && next == '"'))
        {
            var verbatim = c == '@';
            i += verbatim ? 2 : 1;
            while (i < text.Length)
            {
                if (!verbatim && text[i] == '\\')
                {
                    i += 2;
                    continue;
                }

                if (text[i] == '"')
                {
                    if (verbatim && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }

                if (!verbatim && text[i] == '\n')
                {
                    break;
                }

                i++;
            }

            return i;
        }

        if (c == '\'')
        {
            i++;
            while (i < text.Length && text[i] != '\'' && text[i] != '\n')
            {
                i += text[i] == '\\' ? 2 : 1;
            }

            return i + 1;
        }

        return i;
    }

    private static IEnumerable<string> SourceFiles(string folder)
    {
        var folders = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(Path.GetFullPath(folder));
        while (queue.Count > 0 && folders.Count < 50)
        {
            var current = queue.Dequeue();
            if (!Directory.Exists(current) || folders.Contains(current, StringComparer.Ordinal))
            {
                continue;
            }

            folders.Add(current);
            foreach (var reference in ProjectReferences(current))
            {
                queue.Enqueue(reference);
            }
        }

        var count = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in folders)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", options))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Split('/').Any(p => p is "bin" or "obj" or "node_modules" or ".git") || !seen.Add(file))
                {
                    continue;
                }

                if (++count > MaxFiles)
                {
                    yield break;
                }

                yield return file;
            }
        }
    }

    private static IEnumerable<string> ProjectReferences(string folder)
    {
        string[] projects;
        try
        {
            projects = Directory.GetFiles(folder, "*.csproj");
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var project in projects)
        {
            XDocument document;
            try
            {
                using var stream = File.OpenRead(project);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                document = XDocument.Load(reader);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                continue;
            }

            foreach (var include in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference").Select(e => (string?)e.Attribute("Include")))
            {
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                var path = Path.GetFullPath(Path.Combine(folder, include.Replace('\\', Path.DirectorySeparatorChar)));
                var directory = Path.GetDirectoryName(path);
                if (directory is not null && !Path.GetFileName(directory).Contains("Test", StringComparison.OrdinalIgnoreCase))
                {
                    yield return directory;
                }
            }
        }
    }

    private sealed record TypeBody(string Name, OptionsShape Shape, int Open, int Close);

    // Finds the innermost type body around an index. Bodies are in text order, so asking for indexes in text order walks
    // them once, however deep types nest. Asking for an earlier index starts the walk again.
    private sealed class TypeSweep(List<TypeBody> bodies)
    {
        private readonly Stack<TypeBody> _around = new();
        private int _next;
        private int _last = -1;

        public TypeBody? At(int index)
        {
            if (index < _last)
            {
                _around.Clear();
                _next = 0;
            }

            _last = index;
            for (; _next < bodies.Count && bodies[_next].Open < index; _next++)
            {
                Leave(bodies[_next].Open);
                _around.Push(bodies[_next]);
            }

            Leave(index);
            return _around.Count > 0 ? _around.Peek() : null;
        }

        // Drops the bodies that close at or before index. They close innermost first, so the stack holds only the
        // bodies around index.
        private void Leave(int index)
        {
            while (_around.Count > 0 && _around.Peek().Close <= index)
            {
                _around.Pop();
            }
        }
    }

    // String constants by name, and by type and name. A name declared twice with different values resolves to null, so
    // the read shows as one the scan cannot work out instead of taking either value.
    private sealed class Constants
    {
        private readonly Dictionary<string, string?> _byName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _byType = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _outsideTypes = new(StringComparer.Ordinal);

        public void Add(string? type, string name, string value)
        {
            Merge(_byName, name, value);
            if (type is null)
            {
                Merge(_outsideTypes, name, value);
            }
            else
            {
                Merge(_byType, type + "." + name, value);
            }
        }

        // Options.SectionName resolves by its type. A bare SectionName resolves by the type the read is in, and otherwise
        // only when every constant of that name agrees. A type the scan did not see falls back only to a constant declared
        // outside any type, such as in top-level code.
        public string? Resolve(string identifier, string? type)
        {
            var parts = identifier.Split('.');
            if (parts.Length == 1)
            {
                return type is not null && _byType.TryGetValue(type + "." + identifier, out var own) ? own : _byName.GetValueOrDefault(identifier);
            }

            return _byType.TryGetValue(parts[^2] + "." + parts[^1], out var value) ? value : _outsideTypes.GetValueOrDefault(parts[^1]);
        }

        private static void Merge(Dictionary<string, string?> constants, string key, string value)
        {
            constants[key] = constants.TryGetValue(key, out var existing) && existing != value ? null : value;
        }
    }

    private sealed class OptionsShape(string? baseName)
    {
        public string? Base { get; } = baseName;

        // True when the class may have properties the scan can't see, so a key it lacks may still be bound.
        public bool Open { get; set; }

        public Dictionary<string, string?> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // Every pattern runs in time linear in the text, whatever the text: parts next to each other can't pass the same
    // characters back and forth, and a part that could run on is bounded. Each also has a timeout, in case one still can't
    // finish.
    // The last kind of argument, an expression, is at most 300 characters or bracketed parts long, starts and ends with
    // something other than a blank, and closes every ( and [ it opens, one level deep. So it ends at the next call or
    // index that is left open, and an argument can't run on over a line of them.
    private const string ArgPart = @"(?:[^()\[\]\r\n]|\([^()\r\n]*\)|\[[^\[\]\r\n]*\])";
    private const string ArgEdge = @"(?:[^()\[\]\s]|\([^()\r\n]*\)|\[[^\[\]\r\n]*\])";
    private const string Arg = @"(?<arg>@?""[^""\r\n]*""|nameof\(\s*[\w.]+\s*\)|[A-Za-z_][\w.]*|" + ArgEdge + "(?:" + ArgPart + "{0,300}?" + ArgEdge + ")??)";

    [GeneratedRegex(@"\bGet(?:Required)?Section\s*\(\s*" + Arg + @"\s*\)", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex SectionCall();

    [GeneratedRegex(
        @"\G\s*(?:[?!]\s*)?(?:\.\s*Get(?:Required)?Section\s*\(\s*(?<section>)" + Arg + @"\s*\)" +
        @"|\[\s*(?<index>)" + Arg + @"\s*\]" +
        @"|\.\s*GetValue\s*(?:<[^<>()]+>\s*)?\(\s*(?<value>)" + Arg + @"\s*[,)]" +
        @"|\.\s*Get\s*<\s*(?<get>[\w.]+)\s*>\s*\(" +
        @"|\.\s*Bind\s*\()",
        RegexOptions.CultureInvariant,
        MatchTimeoutMilliseconds)]
    private static partial Regex ChainStep();

    [GeneratedRegex(@"\b(?<kind>GetValue|GetConnectionString|BindConfiguration)\s*(?:<[^<>()]+>\s*)?\(\s*" + Arg + @"\s*[,)]", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex ValueCall();

    [GeneratedRegex(@"\b(?:[\w]*[Cc]onfig(?:uration)?|[Cc]fg|[\w]*[Ss]ection)\s*(?:\?\s*)?\[\s*" + Arg + @"\s*\]", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex ConfigIndexer();

    [GeneratedRegex(@"\b\w+\s*\(\s*(?:[\w.]*\.)?[Cc]onfiguration\s*,\s*" + Arg + @"\s*[,)]", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex SectionNameArgument();

    // Where a read starts, as SectionCall, ValueCall and ConfigIndexer start, whatever follows. The ValueCall start also
    // takes a type argument of any shape, such as GetValue<List<string>>(.
    [GeneratedRegex(@"\bGet(?:Required)?Section\s*\(|\b(?:GetValue|GetConnectionString|BindConfiguration)\s*[<(]|\b(?:[\w]*[Cc]onfig(?:uration)?|[Cc]fg|[\w]*[Ss]ection)\s*(?:\?\s*)?\[", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex ReadStart();

    // Runs on the 200 characters before a call only, so its cost is bounded by that.
    [GeneratedRegex(@"(?:Configure\s*<\s*(?<type>[\w.]+)\s*>\s*\(\s*(?:""[^""]*""\s*,\s*)?[\w.]*\s*\.?\s*|AddOptions\s*<\s*(?<type>[\w.]+)\s*>\s*\([^()]*\)\s*\.\s*(?:Bind\s*\(\s*[\w.]*\s*\.?\s*)?)$", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex OptionsTypeTail();

    [GeneratedRegex(@"\b(?:const|static\s+readonly)\s+string\s+(?<name>\w+)\s*=\s*(?:""(?<value>[^""\r\n]*)""|nameof\(\s*(?<nameof>[\w.]+)\s*\))", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex StringConstant();

    [GeneratedRegex(@"^nameof\(\s*(?<name>[\w.]+)\s*\)$", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex NameOf();

    [GeneratedRegex(@"^[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*$", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex Identifier();

    // The base list is words split by spaces, each taken whole, and stops before where; a where clause is at most 1000
    // characters long. A type parameter list and a parameter list can't hold the < or ( that starts another one.
    [GeneratedRegex(@"\b(?:class|record(?:\s+class)?|struct)\s+(?<name>[A-Za-z_]\w*)\s*(?:<[^<>{]*>\s*)?(?:\((?<parameters>[^()]*)\)\s*)?(?::\s*(?<base>(?>[\w.<>,]+(?: +(?!where\b)[\w.<>,]+)*))\s*)?(?:where\b[^{;]{0,1000})?[{;]", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex TypeDeclaration();

    // Where a type declaration starts, as TypeDeclaration starts, whatever follows.
    [GeneratedRegex(@"\b(?:class|record|struct|(?<interface>interface))\s+(?<name>[A-Za-z_]\w*)", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex TypeStart();

    // Runs on one parameter of a record. The type is words split by spaces, each taken whole.
    [GeneratedRegex(@"^\s*(?>(?:\[[^\]]*\]\s*)*)(?<type>(?>[\w.<>\[\],?]+)(?:(?> +)(?>[\w.<>\[\],?]+))*?)\s+(?<name>[A-Za-z_]\w*)\s*(?:=.*)?$", RegexOptions.CultureInvariant | RegexOptions.Singleline, MatchTimeoutMilliseconds)]
    private static partial Regex Parameter();

    // At most ten attributes, five modifiers and eleven words of type, each taken whole.
    [GeneratedRegex(@"(?:\[\s*ConfigurationKeyName\s*\(\s*""(?<key>[^""]+)""\s*\)\s*\]\s*)?(?>(?:\[[^\[\]]*\]\s*){0,10})\bpublic\s+(?>(?:(?:required|virtual|override|new|sealed)\s+){0,5})(?<type>(?>[\w.<>\[\],?]+)(?:(?> +)(?>[\w.<>\[\],?]+)){0,10}?)\s+(?<name>[A-Za-z_]\w*)\s*\{\s*(?:get|init|set)", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex Property();

    // Where a settable public property starts, whatever its type: at most 400 characters before its accessors.
    [GeneratedRegex(@"\bpublic\b[^;{}=]{0,400}\{\s*(?:get|init|set)\b", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex PropertyStart();

    // ConfigurationKeyName("x"), or one with a name the scan can't read, in which case key doesn't match.
    [GeneratedRegex(@"\bConfigurationKeyName(?:Attribute)?\s*\(\s*(?:""(?<key>[^""\r\n]+)""\s*\))?", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex ConfigurationKeyName();
}
