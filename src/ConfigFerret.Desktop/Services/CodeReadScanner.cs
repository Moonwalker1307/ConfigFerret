using System.Globalization;
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
/// <c>GetValue&lt;T&gt;("A:B")</c>, <c>GetSection("A")</c>, <c>GetConnectionString("Db")</c>,
/// <c>Environment.GetEnvironmentVariable("A__B")</c>, and sections, or the whole configuration, bound to options classes
/// with <c>Configure&lt;T&gt;</c>, <c>Bind</c>, <c>Get&lt;T&gt;</c> or <c>BindConfiguration</c>. For a bound section, only keys
/// that match a property of the options class count as read, unless a section below it is read or bound on its own or
/// the class may have properties the scan can't see: it derives from a class not in the code, more than 16 levels up or
/// of its own name, or the scan can't take its declaration apart. A base named like an interface, such as
/// <c>IDisposable</c>, adds no property. A <c>Bind</c> that names no class reads its section, or the whole
/// configuration, whole.
/// </summary>
/// <remarks>
/// This reads text, not compiled code, so it can miss a read: a key built at run time, or read by a library. Such reads
/// are counted in <see cref="DynamicReads"/>, and keys that ASP.NET Core and common libraries read themselves, such as
/// <c>Logging</c>, <c>AllowedHosts</c> and any <c>ASPNETCORE_</c> or <c>DOTNET_</c> variable, always count as read.
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
    private const int MaxFolders = 50;
    private const long MaxFileBytes = 1024 * 1024;

    // A project file is only searched for its references, so one this big is not a real one. The project files of one
    // scan are read up to MaxTotalBytes in all.
    private const int MaxProjectBytes = 1024 * 1024;

    // The code a scan holds in memory at once, as file sizes. A large solution has a few MB of C#, so this leaves room for
    // ten times that while the stripped text, two bytes a character, stays near 128 MB.
    private const long MaxTotalBytes = 64L * 1024 * 1024;

    // How many files SkippedFiles lists. The rest are only counted, so a repository of many files can't fill memory.
    private const int MaxSkippedFiles = 1000;

    // How many places DynamicReads lists. The rest are only counted, so a file of many such reads can't fill memory.
    private const int MaxDynamicReads = 1000;

    // The patterns are linear, so this only stops a file that a pattern somehow still can't finish: the file is skipped.
    private const int MatchTimeoutMilliseconds = 10_000;

    // How many base classes a class takes properties from, so a long chain of them can't make the scan quadratic.
    private const int MaxBaseClasses = 16;

    // How far Check follows a key: at most this many bound sections along it, and this many segments below each. A key
    // past either counts as read, so a long key and many bound sections can't make Check cost their product.
    private const int MaxBoundDepth = 64;

    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _bound = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OptionsShape> _shapes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _interfaces = new(StringComparer.Ordinal);
    private readonly HashSet<string> _typesInSkippedFiles = new(StringComparer.Ordinal);
    private readonly SectionNode _tree = new();
    private readonly List<(string File, int Line)> _dynamic = [];
    private int _dynamicCount;
    private readonly List<string> _skipped = [];
    private readonly List<string> _skippedReferences = [];
    private readonly HashSet<string> _skippedReferenceSet = new(StringComparer.Ordinal);
    private char[]? _typeNameBuffer;
    private long _projectBytes;
    private CancellationToken _cancellationToken;
    private int _steps;

    private CodeReadScanner()
    {
    }

    /// <summary>How many C# files were read.</summary>
    public int FilesScanned { get; private set; }

    /// <summary>
    /// C# files the scan skipped because they are over 1 MB, don't fit in the 64 MB of code the scan holds, come past the
    /// first 5000 files, or reading them took too long, so a key only they read counts as not read: the first 1000 of
    /// them. <see cref="SkippedFileCount"/> counts them all.
    /// </summary>
    public IReadOnlyList<string> SkippedFiles => _skipped;

    /// <summary>How many C# files the scan skipped, listed in <see cref="SkippedFiles"/> or not.</summary>
    public int SkippedFileCount { get; private set; }

    /// <summary>
    /// True when the scan skipped a file without searching it for the types it declares, so any class may have more parts
    /// there. Every class then counts as having any property.
    /// </summary>
    public bool AllClassesOpen { get; private set; }

    /// <summary>
    /// Project references the scan did not follow, and project files it could not read, each with why, such as
    /// <c>"../P50/P50.csproj" (past the first 50 projects)</c>, so a key only that project reads counts as not read.
    /// </summary>
    public IReadOnlyList<string> SkippedReferences => _skippedReferences;

    /// <summary>
    /// Where code reads a key the scanner cannot work out, such as <c>config[name]</c>, as file and line: the first 1000
    /// places found. <see cref="DynamicReadCount"/> counts them all.
    /// </summary>
    public IReadOnlyList<string> DynamicReads => _dynamic.OrderBy(d => d.File, StringComparer.Ordinal).ThenBy(d => d.Line).Select(d => $"{d.File} (line {d.Line})").ToList();

    /// <summary>How many places in the code read a key the scanner cannot work out, listed in <see cref="DynamicReads"/> or not.</summary>
    public int DynamicReadCount => _dynamicCount;

    /// <summary>The exact keys read, such as <c>Cache:Ttl</c>.</summary>
    public IReadOnlyCollection<string> Keys => _keys;

    /// <summary>Sections read as a whole, such as <c>Logging</c>. An empty name is the whole configuration.</summary>
    public IReadOnlyCollection<string> Sections => _sections;

    /// <summary>Sections bound to an options class, and the class name. An empty name is the whole configuration.</summary>
    public IReadOnlyDictionary<string, string> BoundSections => _bound;

    /// <summary>
    /// Reads the C# files in <paramref name="folder"/> and in the projects its project files reference, skipping
    /// <c>bin</c>, <c>obj</c> and test projects.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static CodeReadScanner Scan(string folder, CancellationToken cancellationToken = default) => Scan(folder, MaxTotalBytes, cancellationToken);

    // For tests: maxTotalBytes is the code the scan holds in memory at once.
    internal static CodeReadScanner Scan(string folder, long maxTotalBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var scanner = new CodeReadScanner();
        scanner.Read(scanner.ReadFiles(folder, maxTotalBytes, cancellationToken), null, cancellationToken);
        return scanner;
    }

    // Reads each file as Read asks for it, so the text is held only once comments are stripped from it. A file over
    // MaxFileBytes is searched for the names of the types it declares, while all such searches stay within
    // maxTotalBytes. A file past MaxFiles, one that doesn't fit in maxTotalBytes, and one over MaxFileBytes past the
    // search budget, are listed without being opened, so every class is left open.
    private IEnumerable<(string Name, string Text)> ReadFiles(string folder, long maxTotalBytes, CancellationToken cancellationToken)
    {
        var total = 0L;
        var searched = 0L;
        foreach (var (file, read) in SourceFiles(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetRelativePath(folder, file);
            if (!read)
            {
                SkipFile(name);
                AllClassesOpen = true;
                continue;
            }

            string? text = null;
            var listed = false;
            try
            {
                var length = new FileInfo(file).Length;
                if (length <= MaxFileBytes && total + length <= maxTotalBytes)
                {
                    text = File.ReadAllText(file);
                    total += length;
                }
                else
                {
                    // Listed first, so a file that fails to read partway is still listed.
                    SkipFile(name);
                    listed = true;
                    if (length > MaxFileBytes && searched + length <= maxTotalBytes)
                    {
                        searched += length;
                        ReadTypeNames(file, length, cancellationToken);
                    }
                    else
                    {
                        AllClassesOpen = true;
                    }
                }
            }
            catch (IOException)
            {
                AllClassesOpen |= listed;
            }
            catch (UnauthorizedAccessException)
            {
                AllClassesOpen |= listed;
            }

            if (text is not null)
            {
                yield return (name, text);
            }
        }
    }

    private void SkipFile(string name)
    {
        if (++SkippedFileCount <= MaxSkippedFiles)
        {
            _skipped.Add(name);
        }
    }

    /// <summary>Reads C# sources given as text, for tests and callers that already have the files.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static CodeReadScanner FromSources(IEnumerable<(string Name, string Text)> sources, CancellationToken cancellationToken = default) =>
        FromSources(sources, null, cancellationToken);

    // A file too big to read is still searched for the names of the types it declares, a block at a time. Each of them may
    // have properties the scan can't see, such as a part of a partial class, so it is left open.
    // At most length characters are read, so a file that grows while it is read can't make the search longer. One buffer
    // serves the whole scan.
    private void ReadTypeNames(string file, long length, CancellationToken cancellationToken)
    {
        const int Overlap = 1024;
        using var reader = new StreamReader(file);
        var buffer = _typeNameBuffer ??= new char[MaxFileBytes];
        var carried = 0;
        var left = length;
        while (true)
        {
            var wanted = (int)Math.Min(buffer.Length - carried, left);
            var read = reader.ReadBlock(buffer, carried, wanted);
            left -= read;
            cancellationToken.ThrowIfCancellationRequested();
            var filled = carried + read;
            try
            {
                foreach (Match m in TypeStart().Matches(new string(buffer, 0, filled)))
                {
                    (m.Groups["interface"].Success ? _interfaces : _typesInSkippedFiles).Add(m.Groups["name"].Value);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // The types after the timeout are unknown.
                AllClassesOpen = true;
            }

            // The last characters are read again with the next block, so a declaration split between blocks is found.
            carried = Math.Min(Overlap, filled);
            Array.Copy(buffer, filled - carried, buffer, 0, carried);
            if (read < wanted || left <= 0)
            {
                break;
            }
        }
    }

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

        // The whole configuration read, or bound to a class that takes the key.
        (string Type, string Name)? rootMissing = null;
        string? root = null;
        if (_sections.Contains(string.Empty)
            || (_bound.TryGetValue(string.Empty, out root) && (rootMissing = MissingProperty(root, segments, 0)) is null))
        {
            return new ReadVerdict(true, string.Empty);
        }

        if (IsFramework(segments[0]))
        {
            return new ReadVerdict(true, string.Empty);
        }

        // A class without the property doesn't make the key dead while a longer prefix is read or bound on its own, such
        // as Messaging:Kafka under Messaging. When none is, the reason names the longest prefix bound. The prefixes are
        // walked one segment at a time down the sections the code reads. Each bound one is followed at most MaxBoundDepth
        // segments down, and a key under more than MaxBoundDepth bound sections counts as read, so the cost is bounded by
        // the key's length plus MaxBoundDepth squared lookups.
        (int End, string Type, string Name)? dead = null;
        var node = _tree;
        var end = -1;
        var bound = 0;
        for (var i = 0; i < segments.Length && node.Children.TryGetValue(segments[i], out node); i++)
        {
            end += segments[i].Length + 1;
            if (node.Read)
            {
                return new ReadVerdict(true, string.Empty);
            }

            if (node.Bound is { } type)
            {
                if (++bound > MaxBoundDepth || MissingProperty(type, segments, i + 1) is not { } missing)
                {
                    return new ReadVerdict(true, string.Empty);
                }

                dead = (end, missing.Type, missing.Name);
            }
        }

        if (dead is { } longest)
        {
            return new ReadVerdict(false, $"{key[..longest.End]} is bound to {longest.Type}, which has no property {longest.Name}");
        }

        if (rootMissing is { } missingAtRoot)
        {
            return new ReadVerdict(
                false,
                missingAtRoot.Type == root
                    ? $"The whole configuration is bound to {root}, which has no property {missingAtRoot.Name}"
                    : $"The whole configuration is bound to {root}; {missingAtRoot.Type} has no property {missingAtRoot.Name}");
        }

        return new ReadVerdict(false, FilesScanned == 0 ? "No C# code found to read it" : "No code reads it");
    }

    // A key the host or a common library reads, or a variable such as ASPNETCORE_FORWARDEDHEADERS_ENABLED or
    // DOTNET_gcServer that configures the host or the runtime.
    private static bool IsFramework(string section) =>
        FrameworkSections.Contains(section, StringComparer.OrdinalIgnoreCase)
        || section.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
        || section.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase);

    // The first segment the options class can't take, or null when the binder sets a property from it.
    // A key more than MaxBoundDepth segments below the bound section counts as taken.
    private (string Type, string Name)? MissingProperty(string type, string[] segments, int start)
    {
        var current = type;
        foreach (var segment in segments.AsSpan(start, Math.Min(segments.Length - start, MaxBoundDepth)))
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
    // properties of its bases. Open is true when the class or a base it reached may have properties the scan can't see:
    // a class it could not read whole, a base class not in the code, such as a library's options class, or a base past
    // the last one followed. A base named like an interface, such as IValidatableObject, gives the binder no property.
    // Every class is open when a file was skipped without being searched for the types it declares.
    private bool TryGetProperty(OptionsShape shape, string name, out string? type, out bool open)
    {
        open = AllClassesOpen;
        var seen = new HashSet<OptionsShape> { shape };
        var current = shape;
        while (true)
        {
            if (current.Properties.TryGetValue(name, out type))
            {
                return true;
            }

            open |= current.Open;
            if (current.Base is null)
            {
                break;
            }

            if (!_shapes.TryGetValue(current.Base, out var parent))
            {
                open |= !_interfaces.Contains(current.Base) && !IsInterfaceName(current.Base);
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

        foreach (var name in _typesInSkippedFiles)
        {
            if (!_shapes.TryGetValue(name, out var shape))
            {
                shape = new OptionsShape(null);
                _shapes.Add(name, shape);
            }

            shape.Open = true;
        }

        for (var i = 0; i < files.Count; i++)
        {
            var (name, text) = files[i];
            var bodies = typeBodies[i];
            skipped[i] = skipped[i] || !TryRead(name, "calls", () => ReadCalls(name, text, constants, bodies));
        }

        FilesScanned = skipped.Count(s => !s);
        foreach (var (name, _) in files.Where((_, i) => skipped[i]))
        {
            SkipFile(name);
        }

        // The sections read or bound, by segment, so Check walks a key once. The whole configuration, the empty path, is
        // checked on its own.
        foreach (var section in _sections.Where(section => section.Length > 0))
        {
            SectionAt(section).Read = true;
        }

        foreach (var (path, type) in _bound.Where(bound => bound.Key.Length > 0))
        {
            SectionAt(path).Bound = type;
        }

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
        var lines = new HashSet<int>();

        // A bare constant name resolves first in the type around the read.
        var types = new TypeSweep(bodies);
        string? Key(Match match) => Resolve(match.Groups["arg"].Value, constants, types.At(match.Index)?.Name);

        void Dynamic(int index)
        {
            var before = newlines.BinarySearch(index);
            var line = (before >= 0 ? before : ~before) + 1;
            if (lines.Add(line) && ++_dynamicCount <= MaxDynamicReads)
            {
                _dynamic.Add((file, line));
            }
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
            else if (m.Groups["kind"].Value == "GetEnvironmentVariable")
            {
                _keys.Add(key.Replace("__", ":", StringComparison.Ordinal));
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

        // The whole configuration bound to a class: Configure<T>(configuration), AddOptions<T>().Bind(configuration) and
        // configuration.Get<T>(). A Bind names no
        // class, so configuration.Bind(settings) reads it whole, and configuration.Bind("Smtp", smtp) reads Smtp whole.
        // So does configuration.Get<T>() of a collection or dictionary.
        foreach (Match m in ConfigureWhole().Matches(text))
        {
            Step();
            if (m.Groups["open"].Success)
            {
                _sections.Add(string.Empty);
            }
            else
            {
                Bind(string.Empty, Simple(m.Groups["type"].Value));
            }
        }

        foreach (Match m in WholeConfigurationCall().Matches(text))
        {
            Step();
            covered.Add(m.Index);
            if (m.Groups["get"].Success && !m.Groups["open"].Success)
            {
                Bind(string.Empty, Simple(m.Groups["get"].Value));
            }
            else
            {
                _sections.Add(m.Groups["key"].Success && Key(m) is { } section ? section : string.Empty);
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
            var baseName = m.Groups["base"].Success ? Simple(m.Groups["base"].Value.Split([',', '('])[0]) : null;
            parsed.Add((m.Index, m.Index + m.Length));

            // Another part of a partial class, or a class of the same name in another namespace, adds to the one shape.
            // Two different base classes can't both be followed, so the shape is left open.
            if (_shapes.TryGetValue(name, out var shape))
            {
                shape.Open |= baseName is not null && shape.Base is not null && baseName != shape.Base;
                shape.Base ??= baseName;
            }
            else
            {
                shape = new OptionsShape(baseName);
                _shapes.Add(name, shape);
            }

            // A base of the same name, such as Settings : Lib.Settings, is another class the scan can't tell apart.
            if (baseName == name)
            {
                shape.Open = true;
            }

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
        }

        // A type the pattern above could not take apart, such as one with a where clause over 1000 characters or a base
        // that takes nested calls, may have properties the scan can't see, so it is left open. An interface is noted, so a
        // base of that name gives no property.
        var next = 0;
        foreach (Match m in TypeStart().Matches(text))
        {
            Step();
            while (next < parsed.Count && parsed[next].End <= m.Index)
            {
                next++;
            }

            var name = m.Groups["name"].Value;
            if (m.Groups["interface"].Success)
            {
                _interfaces.Add(name);
            }
            else if (next == parsed.Count || parsed[next].Start > m.Index)
            {
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

    // Each C# file of the folder and of the project folders it references, and whether the scan may read it. The
    // project files of the first MaxFolders folders are read, and each folder is walked once: one is left out only when
    // a walk already went through it. A reference left past MaxFolders is listed; when a walk went through its folder,
    // its files are read, but its own references aren't followed. The files past the first MaxFiles are only listed.
    // Paths are taken in ordinal order, so the same repository gives the same result on every file system.
    private IEnumerable<(string File, bool Read)> SourceFiles(string folder)
    {
        // Paths compare as IsInFolder compares them, so ..\Shared and ..\shared are one folder where case is ignored.
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var folders = new List<string>();
        var known = new HashSet<string>(comparer);
        var queue = new Queue<(string Folder, string? Include)>();
        queue.Enqueue((Path.GetFullPath(folder), null));
        while (queue.Count > 0 && folders.Count < MaxFolders)
        {
            var (current, _) = queue.Dequeue();
            if (!known.Add(Path.TrimEndingDirectorySeparator(current)) || !Directory.Exists(current))
            {
                continue;
            }

            folders.Add(Path.TrimEndingDirectorySeparator(current));
            foreach (var reference in ProjectReferences(folder, current))
            {
                queue.Enqueue(reference);
            }
        }

        // A folder is skipped only when a walk went through it. One below another as text may still need a walk of its
        // own, when it lies behind a link or below bin, obj, node_modules or .git.
        var walked = new HashSet<string>(comparer);
        var count = 0;
        foreach (var root in folders.Where(f => !walked.Contains(f)))
        {
            foreach (var file in CodeFiles(root, walked))
            {
                yield return (file, ++count <= MaxFiles);
            }
        }

        // A reference left past the first MaxFolders is listed. When a walk went through its folder, its files were
        // read, but its project file never was.
        foreach (var (current, include) in queue)
        {
            var trimmed = Path.TrimEndingDirectorySeparator(current);
            if (include is not null && known.Add(trimmed))
            {
                SkipReference(include, walked.Contains(trimmed)
                    ? $"past the first {MaxFolders} projects, so its references aren't followed"
                    : $"past the first {MaxFolders} projects");
            }
        }
    }

    // The C# files below a folder, in ordinal order within each folder, then the folders below it in ordinal order.
    // bin, obj, node_modules, .git, links and folders already walked are not entered. Each folder walked goes into walked.
    private static IEnumerable<string> CodeFiles(string root, HashSet<string> walked)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!walked.Add(Path.TrimEndingDirectorySeparator(current)))
            {
                continue;
            }

            string[] files;
            string[] folders;
            try
            {
                files = Directory.GetFiles(current, "*.cs", options);
                folders = Directory.GetDirectories(current, "*", options);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                yield return file;
            }

            Array.Sort(folders, StringComparer.Ordinal);
            for (var i = folders.Length - 1; i >= 0; i--)
            {
                if (Path.GetFileName(folders[i]) is not ("bin" or "obj" or "node_modules" or ".git"))
                {
                    stack.Push(folders[i]);
                }
            }
        }
    }

    // The project references of the project files in a folder. A project file that is too big or isn't XML is listed by
    // its path below the scanned folder, and its references are not followed.
    private IEnumerable<(string Folder, string Include)> ProjectReferences(string scanned, string folder)
    {
        string[] projects;
        try
        {
            projects = Directory.GetFiles(folder, "*.csproj");
            Array.Sort(projects, StringComparer.Ordinal);
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
            var name = Path.GetRelativePath(scanned, project).Replace('\\', '/');
            XDocument? document = null;
            try
            {
                using var stream = File.OpenRead(project);
                if (stream.Length <= MaxProjectBytes && _projectBytes + stream.Length <= MaxTotalBytes)
                {
                    _projectBytes += stream.Length;
                    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxProjectBytes };
                    using var reader = XmlReader.Create(stream, settings);
                    document = XDocument.Load(reader);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
            }

            if (document is null)
            {
                SkipReference(name, "a project file the scan can't read, so its references aren't followed");
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
                    yield return (directory, include);
                }
            }
        }
    }

    // Lists a reference once, as written, at most 200 characters of it, with characters that could disguise it replaced,
    // and why.
    private void SkipReference(string include, string reason)
    {
        var cut = include.Length > 200 ? (char.IsHighSurrogate(include[199]) ? 199 : 200) : include.Length;
        var text = new string(include[..cut].Select(c => IsHidden(c) ? '?' : c).ToArray());
        var entry = $"\"{text}{(cut < include.Length ? "…" : string.Empty)}\" ({reason})";
        if (_skippedReferenceSet.Add(entry))
        {
            _skippedReferences.Add(entry);
        }

        // Control and format characters, such as U+202E, which turns the text after it around, and the separators that
        // break a line.
        static bool IsHidden(char c) => char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
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

    // IValidatableObject, IDisposable: named like an interface, by the .NET convention of I, a capital and a lower-case
    // letter. IISServerOptions and IPAddressOptions are not, so a base of that name counts as a class the scan can't see.
    private static bool IsInterfaceName(string name) => name.Length > 2 && name[0] == 'I' && char.IsUpper(name[1]) && char.IsLower(name[2]);

    private SectionNode SectionAt(string path)
    {
        var node = _tree;
        foreach (var segment in path.Split(':'))
        {
            if (!node.Children.TryGetValue(segment, out var child))
            {
                child = new SectionNode();
                node.Children.Add(segment, child);
            }

            node = child;
        }

        return node;
    }

    // A segment of the sections the code reads: read whole, bound to a class, or only on the way to one below it.
    private sealed class SectionNode
    {
        public Dictionary<string, SectionNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Read { get; set; }

        public string? Bound { get; set; }
    }

    private sealed class OptionsShape(string? baseName)
    {
        public string? Base { get; set; } = baseName;

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

    [GeneratedRegex(@"\b(?<kind>GetValue|GetConnectionString|BindConfiguration|GetEnvironmentVariable)\s*(?:<[^<>()]+>\s*)?\(\s*" + Arg + @"\s*[,)]", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex ValueCall();

    [GeneratedRegex(@"\b(?:[\w]*[Cc]onfig(?:uration)?|[Cc]fg|[\w]*[Ss]ection)\s*(?:\?\s*)?\[\s*" + Arg + @"\s*\]", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex ConfigIndexer();

    [GeneratedRegex(@"\b\w+\s*\(\s*(?:[\w.]*\.)?[Cc]onfiguration\s*,\s*" + Arg + @"\s*[,)]", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex SectionNameArgument();

    // Where a read starts, as SectionCall, ValueCall, ConfigIndexer and WholeConfigurationCall start, whatever follows. The
    // ValueCall start also takes a type argument of any shape, such as GetValue<List<string>>(. ConfigurationBinder.Bind(config, x)
    // and config.Get(typeof(T)) are only listed.
    [GeneratedRegex(
        @"\bGet(?:Required)?Section\s*\(|\b(?:GetValue|GetConnectionString|BindConfiguration|GetEnvironmentVariable)\s*[<(]" +
        @"|\b(?:[\w]*[Cc]onfig(?:uration)?|[Cc]fg|[\w]*[Ss]ection)\s*(?:\?\s*)?\[|\b(?:\w*[Cc]onfig(?:uration)?|[Cc]fg)\s*\.\s*(?:Get\s*<|Bind\s*\()" +
        @"|\bConfigurationBinder\s*\.\s*\w+\s*\(|\.\s*Get\s*\(\s*typeof\b",
        RegexOptions.CultureInvariant,
        MatchTimeoutMilliseconds)]
    private static partial Regex ReadStart();

    // The type argument of Configure<T> or AddOptions<T>, and the call's open bracket. A generic T, such as
    // Dictionary<string, string>, is marked with open.
    private const string OptionsTypeArgument = @"\s*<\s*(?<type>[\w.]+)\s*(?:(?<open><)(?>[^<>()\r\n]*)(?:<(?>[^<>()\r\n]*)>(?>[^<>()\r\n]*))?>\s*)?>\s*\(";

    // Configure<T>(builder.Configuration), with or without an options name first, and AddOptions<T>().Bind(builder.Configuration),
    // each with or without binder options after it.
    [GeneratedRegex(
        @"\b(?:Configure" + OptionsTypeArgument + @"\s*(?:""[^""\r\n]*""\s*,\s*)?|AddOptions" + OptionsTypeArgument + @"[^()]*\)\s*\.\s*Bind\s*\(\s*)" +
        @"(?:[\w.]*\.)?(?:\w*[Cc]onfig(?:uration)?|[Cc]fg)\s*[,)]",
        RegexOptions.CultureInvariant,
        MatchTimeoutMilliseconds)]
    private static partial Regex ConfigureWhole();

    // configuration.Get<T>(), configuration.Bind(settings) and configuration.Bind("Section", settings). A generic T, such as
    // Dictionary<string, string>, is marked with open.
    [GeneratedRegex(@"\b(?:\w*[Cc]onfig(?:uration)?|[Cc]fg)\s*\.\s*(?:Get\s*<\s*(?<get>[\w.]+)\s*(?:(?<open><)|>\s*\()|Bind\s*\(\s*" + Arg + @"\s*(?:(?<key>,)|\)))", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex WholeConfigurationCall();

    // Runs on the 200 characters before a call only, so its cost is bounded by that.
    [GeneratedRegex(@"(?:Configure\s*<\s*(?<type>[\w.]+)\s*>\s*\(\s*(?:""[^""]*""\s*,\s*)?[\w.]*\s*\.?\s*|AddOptions\s*<\s*(?<type>[\w.]+)\s*>\s*\([^()]*\)\s*\.\s*(?:Bind\s*\(\s*[\w.]*\s*\.?\s*)?)$", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex OptionsTypeTail();

    [GeneratedRegex(@"\b(?:const|static\s+readonly)\s+string\s+(?<name>\w+)\s*=\s*(?:""(?<value>[^""\r\n]*)""|nameof\(\s*(?<nameof>[\w.]+)\s*\))", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex StringConstant();

    [GeneratedRegex(@"^nameof\(\s*(?<name>[\w.]+)\s*\)$", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex NameOf();

    [GeneratedRegex(@"^[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*$", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex Identifier();

    // The base list is words split by blanks or line breaks, each taken whole, and stops before where; the first word may
    // pass arguments, as in Base(x), without nested calls. A where clause is at most 1000 characters long. A type
    // parameter list and a parameter list can't hold the < or ( that starts another one.
    [GeneratedRegex(@"\b(?:class|record(?:\s+class)?|struct)\s+(?<name>[A-Za-z_]\w*)\s*(?:<[^<>{]*>\s*)?(?:\((?<parameters>[^()]*)\)\s*)?(?::\s*(?<base>(?>[\w.<>,]+(?:\([^()]*\))?(?:\s*(?!where\b)[\w.<>,]+)*))\s*)?(?:where\b[^{;]{0,1000})?[{;]", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
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
