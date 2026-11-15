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

    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _bound = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OptionsShape> _shapes = new(StringComparer.Ordinal);
    private readonly List<(string File, int Line)> _dynamic = [];

    private CodeReadScanner()
    {
    }

    /// <summary>How many C# files were read.</summary>
    public int FilesScanned { get; private set; }

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
    public static CodeReadScanner Scan(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var scanner = new CodeReadScanner();
        var sources = new List<(string Path, string Text)>();
        foreach (var file in SourceFiles(folder))
        {
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

        scanner.Read(sources.Select(s => (Path.GetRelativePath(folder, s.Path), s.Text)));
        return scanner;
    }

    /// <summary>Reads C# sources given as text, for tests and callers that already have the files.</summary>
    public static CodeReadScanner FromSources(IEnumerable<(string Name, string Text)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var scanner = new CodeReadScanner();
        scanner.Read(sources);
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

            if (!shape.Properties.TryGetValue(segment, out var propertyType))
            {
                return (current, segment);
            }

            if (propertyType is null)
            {
                return null; // A plain value, a collection or a dictionary.
            }

            current = propertyType;
        }

        return null;
    }

    private void Read(IEnumerable<(string Name, string Text)> sources)
    {
        var files = sources.Select(s => (s.Name, Text: StripComments(s.Text))).ToList();
        FilesScanned = files.Count;
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, text) in files)
        {
            foreach (Match m in StringConstant().Matches(text))
            {
                constants.TryAdd(m.Groups["name"].Value, m.Groups["value"].Value);
            }

            ReadShapes(text);
        }

        foreach (var (name, text) in files)
        {
            ReadCalls(name, text, constants);
        }

        ResolveInheritance();
    }

    private void ReadCalls(string file, string text, Dictionary<string, string> constants)
    {
        var consumed = new List<(int Start, int End)>();

        bool Consumed(int index) => consumed.Any(c => index >= c.Start && index < c.End);

        foreach (Match m in SectionCall().Matches(text))
        {
            // A GetSection that continues an earlier chain, such as the second one in GetRequiredSection("A").GetSection("B").
            if (Consumed(m.Index))
            {
                continue;
            }

            var path = Resolve(m.Groups["arg"].Value, constants);
            var end = m.Index + m.Length;
            if (path is null)
            {
                Dynamic(file, text, m.Index);
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

                var arg = next.Groups["arg"].Success ? Resolve(next.Groups["arg"].Value, constants) : null;
                if (next.Groups["section"].Success || next.Groups["index"].Success || next.Groups["value"].Success)
                {
                    if (arg is null)
                    {
                        if (path is not null)
                        {
                            Dynamic(file, text, next.Index);
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
            if (Consumed(m.Index))
            {
                continue;
            }

            var key = Resolve(m.Groups["arg"].Value, constants);
            if (key is null)
            {
                Dynamic(file, text, m.Index);
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
            if (Consumed(m.Index))
            {
                continue;
            }

            var key = Resolve(m.Groups["arg"].Value, constants);
            if (key is null)
            {
                Dynamic(file, text, m.Index);
            }
            else
            {
                _keys.Add(key);
            }
        }

        // Libraries that take the configuration and a section name, such as AddMicrosoftIdentityWebApi(builder.Configuration, "AzureAd").
        foreach (Match m in SectionNameArgument().Matches(text))
        {
            if (!Consumed(m.Index) && Resolve(m.Groups["arg"].Value, constants) is { } section)
            {
                _sections.Add(section);
            }
        }

        // Code that hands the whole configuration to a library, such as Serilog's ReadFrom.Configuration.
        if (text.Contains("ReadFrom.Configuration", StringComparison.Ordinal))
        {
            _sections.Add("Serilog");
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

    private void Dynamic(string file, string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        _dynamic.Add((file, line));
    }

    // Configure<T>(...GetSection, AddOptions<T>().Bind(...GetSection, AddOptions<T>().BindConfiguration(...
    private static string? OptionsTypeBefore(string text, int index)
    {
        var start = Math.Max(0, index - 200);
        var before = text[start..index];
        var m = OptionsTypeTail().Match(before);
        return m.Success ? Simple(m.Groups["type"].Value) : null;
    }

    private static string? Resolve(string arg, Dictionary<string, string> constants)
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
            return constants.GetValueOrDefault(arg.Split('.')[^1]);
        }

        return null;
    }

    private void ReadShapes(string text)
    {
        foreach (Match m in TypeDeclaration().Matches(text))
        {
            var name = m.Groups["name"].Value;
            var shape = new OptionsShape(m.Groups["base"].Success ? Simple(m.Groups["base"].Value.Split(',')[0]) : null);

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

            var open = m.Index + m.Length - 1;
            if (text[open] == '{')
            {
                var body = Body(text, open);
                foreach (Match p in Property().Matches(body))
                {
                    var key = p.Groups["key"].Success ? p.Groups["key"].Value : p.Groups["name"].Value;
                    shape.Properties[key] = PropertyType(p.Groups["type"].Value);
                }
            }

            _shapes.TryAdd(name, shape);
        }
    }

    private void ResolveInheritance()
    {
        foreach (var shape in _shapes.Values)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var baseName = shape.Base;
            while (baseName is not null && seen.Add(baseName) && _shapes.TryGetValue(baseName, out var parent))
            {
                foreach (var (key, type) in parent.Properties)
                {
                    shape.Properties.TryAdd(key, type);
                }

                baseName = parent.Base;
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

    private static string Body(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return text[(open + 1)..i];
            }
        }

        return text[(open + 1)..];
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
            else if (c == '"' || (c == '@' && next == '"'))
            {
                var verbatim = c == '@';
                var start = i;
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

                        i++;
                        break;
                    }

                    if (!verbatim && text[i] == '\n')
                    {
                        break;
                    }

                    i++;
                }

                sb.Append(text, start, Math.Min(i, text.Length) - start);
            }
            else if (c == '\'')
            {
                var start = i;
                i++;
                while (i < text.Length && text[i] != '\'' && text[i] != '\n')
                {
                    i += text[i] == '\\' ? 2 : 1;
                }

                i++;
                sb.Append(text, start, Math.Min(i, text.Length) - start);
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
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

    private sealed class OptionsShape(string? baseName)
    {
        public string? Base { get; } = baseName;

        public Dictionary<string, string?> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private const string Arg = @"(?<arg>@?""[^""\r\n]*""|nameof\(\s*[\w.]+\s*\)|[A-Za-z_][\w.]*|[^)\]\r\n]+?)";

    [GeneratedRegex(@"\bGet(?:Required)?Section\s*\(\s*" + Arg + @"\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex SectionCall();

    [GeneratedRegex(
        @"\G\s*(?:\?|!)?\s*(?:\.\s*Get(?:Required)?Section\s*\(\s*(?<section>)" + Arg + @"\s*\)" +
        @"|\[\s*(?<index>)" + Arg + @"\s*\]" +
        @"|\.\s*GetValue\s*(?:<[^>()]+>)?\s*\(\s*(?<value>)" + Arg + @"\s*[,)]" +
        @"|\.\s*Get\s*<\s*(?<get>[\w.]+)\s*>\s*\(" +
        @"|\.\s*Bind\s*\()",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChainStep();

    [GeneratedRegex(@"\b(?<kind>GetValue|GetConnectionString|BindConfiguration)\s*(?:<[^>()]+>)?\s*\(\s*" + Arg + @"\s*[,)]", RegexOptions.CultureInvariant)]
    private static partial Regex ValueCall();

    [GeneratedRegex(@"\b(?:[\w]*[Cc]onfig(?:uration)?|[Cc]fg|[\w]*[Ss]ection)\s*\??\s*\[\s*" + Arg + @"\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigIndexer();

    [GeneratedRegex(@"\b\w+\s*\(\s*(?:[\w.]*\.)?[Cc]onfiguration\s*,\s*" + Arg + @"\s*[,)]", RegexOptions.CultureInvariant)]
    private static partial Regex SectionNameArgument();

    [GeneratedRegex(@"(?:Configure\s*<\s*(?<type>[\w.]+)\s*>\s*\(\s*(?:""[^""]*""\s*,\s*)?[\w.]*\s*\.?\s*|AddOptions\s*<\s*(?<type>[\w.]+)\s*>\s*\([^()]*\)\s*\.\s*(?:Bind\s*\(\s*[\w.]*\s*\.?\s*)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex OptionsTypeTail();

    [GeneratedRegex(@"\bconst\s+string\s+(?<name>\w+)\s*=\s*""(?<value>[^""\r\n]*)""|\bstatic\s+readonly\s+string\s+(?<name>\w+)\s*=\s*""(?<value>[^""\r\n]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex StringConstant();

    [GeneratedRegex(@"^nameof\(\s*(?<name>[\w.]+)\s*\)$", RegexOptions.CultureInvariant)]
    private static partial Regex NameOf();

    [GeneratedRegex(@"^[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex(@"\b(?:class|record(?:\s+class)?|struct)\s+(?<name>[A-Za-z_]\w*)\s*(?:<[^>{]*>\s*)?(?:\((?<parameters>[^()]*)\)\s*)?(?::\s*(?<base>[\w.<>, ]+?)\s*)?(?:where\b[^{;]*)?[{;]", RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*(?<type>[\w.<>\[\], ?]+?)\s+(?<name>[A-Za-z_]\w*)\s*(?:=.*)?$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Parameter();

    [GeneratedRegex(@"(?:\[\s*ConfigurationKeyName\s*\(\s*""(?<key>[^""]+)""\s*\)\s*\]\s*)?(?:\[[^\]]*\]\s*)*\bpublic\s+(?:(?:required|virtual|override|new|sealed)\s+)*(?<type>[\w.<>\[\], ?]+?)\s+(?<name>[A-Za-z_]\w*)\s*\{\s*(?:get|init|set)", RegexOptions.CultureInvariant)]
    private static partial Regex Property();
}
