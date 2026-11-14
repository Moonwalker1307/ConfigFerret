using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.Tests;

public class CodeReadScannerTests
{
    private static CodeReadScanner Scan(params string[] sources) =>
        CodeReadScanner.FromSources(sources.Select((s, i) => ($"File{i}.cs", s)));

    [Theory]
    [InlineData("""var x = builder.Configuration["Cache:Ttl"];""", "Cache:Ttl")]
    [InlineData("""var x = _config?["Cache:Ttl"];""", "Cache:Ttl")]
    [InlineData("""var x = configuration.GetValue<int>("Cache:Ttl");""", "Cache:Ttl")]
    [InlineData("""var x = configuration.GetValue<int>("Cache:Ttl", 30);""", "Cache:Ttl")]
    [InlineData("""var x = configuration.GetSection("Cache")["Ttl"];""", "Cache:Ttl")]
    [InlineData("""var x = configuration.GetSection("Cache").GetValue<int>("Ttl");""", "Cache:Ttl")]
    [InlineData("""var x = configuration.GetRequiredSection("A").GetSection("B")["C"];""", "A:B:C")]
    [InlineData("""var x = configuration.GetConnectionString("Orders");""", "ConnectionStrings:Orders")]
    [InlineData("""const string Key = "Cache:Ttl"; var x = configuration[Key];""", "Cache:Ttl")]
    [InlineData("""var x = configuration.GetSection("Cache");""", "Cache:Ttl")]
    [InlineData("""services.AddMicrosoftIdentityWebApi(builder.Configuration, "AzureAd");""", "AzureAd:ClientId")]
    public void Finds_reads(string code, string key)
    {
        var scanner = Scan(code);

        Assert.True(scanner.Check(key).IsRead, key);
        Assert.True(scanner.Check(key.Replace(":", "__", StringComparison.Ordinal)).IsRead);
    }

    [Fact]
    public void A_key_nobody_reads_is_dead()
    {
        var scanner = Scan("""var x = configuration["Cache:Ttl"];""");

        var verdict = scanner.Check("Payments:ApiKey");

        Assert.False(verdict.IsRead);
        Assert.Equal("No code reads it", verdict.Reason);
        Assert.False(scanner.Check("Cache:Size").IsRead);
        Assert.False(scanner.Check("Cache").IsRead);
    }

    [Fact]
    public void Commented_out_reads_do_not_count()
    {
        var scanner = Scan("""
            // var a = configuration["A:B"];
            /* var c = configuration["C:D"]; */
            var url = "https://example.test"; var e = configuration["E:F"];
            """);

        Assert.False(scanner.Check("A:B").IsRead);
        Assert.False(scanner.Check("C:D").IsRead);
        Assert.True(scanner.Check("E:F").IsRead);
    }

    [Fact]
    public void Host_and_library_sections_count_as_read()
    {
        var scanner = Scan("var x = 1;");

        Assert.True(scanner.Check("Logging:LogLevel:Default").IsRead);
        Assert.True(scanner.Check("AllowedHosts").IsRead);
        Assert.True(scanner.Check("Kestrel:Endpoints:Http:Url").IsRead);
    }

    [Fact]
    public void A_bound_section_reads_only_the_options_class_properties()
    {
        var scanner = Scan(
            """
            builder.Services.Configure<DbOptions>(builder.Configuration.GetSection(DbOptions.SectionName));
            builder.Services.AddOptions<SmtpOptions>().BindConfiguration("Smtp");
            var retry = builder.Configuration.GetSection("Retry").Get<RetryOptions>();
            """,
            """
            public sealed class DbOptions : DbBase
            {
                public const string SectionName = "Db";
                public string Host { get; set; } = "";
                public Pool Pool { get; set; } = new();
                public Dictionary<string, string> Tags { get; set; } = [];
                [ConfigurationKeyName("pwd")] public string Password { get; set; } = "";
            }
            public abstract class DbBase { public int Port { get; init; } }
            public sealed class Pool { public int Size { get; set; } }
            public sealed record SmtpOptions(string Host, int Port);
            public sealed record RetryOptions { public required int Count { get; init; } }
            """);

        Assert.True(scanner.Check("Db:Host").IsRead);
        Assert.True(scanner.Check("db:host").IsRead);
        Assert.True(scanner.Check("Db:Port").IsRead);
        Assert.True(scanner.Check("Db:Pool:Size").IsRead);
        Assert.True(scanner.Check("Db:Tags:anything").IsRead);
        Assert.True(scanner.Check("Db:pwd").IsRead);
        Assert.True(scanner.Check("Smtp:Port").IsRead);
        Assert.True(scanner.Check("Retry:Count").IsRead);

        var timeout = scanner.Check("Db:Timeout");
        Assert.False(timeout.IsRead);
        Assert.Equal("Db is bound to DbOptions, which has no property Timeout", timeout.Reason);
        Assert.False(scanner.Check("Db:Pool:Max").IsRead);
        Assert.False(scanner.Check("Smtp:User").IsRead);
        Assert.False(scanner.Check("Retry:Delay").IsRead);
    }

    [Fact]
    public void A_section_bound_to_a_class_it_cannot_see_reads_everything_under_it()
    {
        var scanner = Scan("""services.Configure<ExternalOptions>(config.GetSection("External"));""");

        Assert.True(scanner.Check("External:Anything:Deep").IsRead);
    }

    [Fact]
    public void Reads_it_cannot_work_out_are_listed()
    {
        var scanner = Scan("""
            var name = GetName();
            var a = configuration[name];
            var b = configuration.GetSection($"Tenants:{id}");
            """);

        Assert.Equal(["File0.cs (line 2)", "File0.cs (line 3)"], scanner.DynamicReads);
    }

    [Fact]
    public void Scans_the_app_folder_and_the_projects_it_references()
    {
        using var temp = new TempFolder();
        temp.Write("src/Api/Api.csproj", """<Project><ItemGroup><ProjectReference Include="..\Shared\Shared.csproj" /><ProjectReference Include="../Api.Tests/Api.Tests.csproj" /></ItemGroup></Project>""");
        temp.Write("src/Api/Program.cs", """var a = configuration["A:B"];""");
        temp.Write("src/Api/obj/Generated.cs", """var c = configuration["Obj:Key"];""");
        temp.Write("src/Shared/Shared.csproj", "<Project />");
        temp.Write("src/Shared/Reader.cs", """var s = configuration["Shared:Key"];""");
        temp.Write("src/Api.Tests/Tests.cs", """var t = configuration["Test:Key"];""");

        var scanner = CodeReadScanner.Scan(temp.Full("src/Api"));

        Assert.Equal(2, scanner.FilesScanned);
        Assert.True(scanner.Check("A:B").IsRead);
        Assert.True(scanner.Check("Shared:Key").IsRead);
        Assert.False(scanner.Check("Obj:Key").IsRead);
        Assert.False(scanner.Check("Test:Key").IsRead);
    }

    [Theory]
    [InlineData("Logging")]
    [InlineData("AllowedHosts")]
    [InlineData("Kestrel")]
    [InlineData("Urls")]
    [InlineData("HTTP_PORTS")]
    [InlineData("HTTPS_PORTS")]
    [InlineData("ASPNETCORE_URLS")]
    [InlineData("ASPNETCORE_HTTP_PORTS")]
    [InlineData("ASPNETCORE_HTTPS_PORTS")]
    [InlineData("ASPNETCORE_ENVIRONMENT")]
    [InlineData("DOTNET_ENVIRONMENT")]
    [InlineData("environment")]
    [InlineData("applicationName")]
    [InlineData("contentRoot")]
    [InlineData("webroot")]
    [InlineData("DetailedErrors")]
    [InlineData("ShutdownTimeoutSeconds")]
    [InlineData("HostingStartupAssemblies")]
    [InlineData("HostingStartupExcludeAssemblies")]
    [InlineData("PreventHostingStartup")]
    [InlineData("SuppressStatusMessages")]
    [InlineData("https_port")]
    [InlineData("PathBase")]
    public void Every_framework_section_counts_as_read_without_a_reason(string section)
    {
        var scanner = Scan("var x = 1;");

        Assert.Equal(new ReadVerdict(true, string.Empty), scanner.Check(section));
        Assert.True(scanner.Check(section.ToUpperInvariant()).IsRead);
        Assert.True(scanner.Check(section + ":Child").IsRead);
        Assert.False(scanner.Check("App:" + section).IsRead);
    }

    [Fact]
    public void A_read_has_an_empty_reason_whichever_way_it_is_found()
    {
        var scanner = Scan("""var a = config["A:B"]; var s = config.GetSection("S"); services.Configure<O>(config.GetSection("O")); class O { public int P { get; set; } }""");

        Assert.Equal(new ReadVerdict(true, string.Empty), scanner.Check("A:B"));
        Assert.Equal(new ReadVerdict(true, string.Empty), scanner.Check("S:Deep"));
        Assert.Equal(new ReadVerdict(true, string.Empty), scanner.Check("O:P"));
    }

    [Fact]
    public void Without_any_code_the_reason_says_so()
    {
        var verdict = CodeReadScanner.FromSources([]).Check("A:B");

        Assert.False(verdict.IsRead);
        Assert.Equal("No C# code found to read it", verdict.Reason);
        Assert.Equal("No code reads it", Scan("var x = 1;").Check("A:B").Reason);
    }

    [Fact]
    public void Rejects_missing_arguments()
    {
        Assert.Throws<ArgumentException>(() => CodeReadScanner.Scan(" "));
        Assert.Throws<ArgumentNullException>(() => CodeReadScanner.Scan(null!));
        Assert.Throws<ArgumentNullException>(() => CodeReadScanner.FromSources(null!));
        Assert.Throws<ArgumentNullException>(() => Scan("var x = 1;").Check(null!));
    }

    [Fact]
    public void Lists_unreadable_reads_by_file_then_line_without_repeats()
    {
        var scanner = CodeReadScanner.FromSources(
        [
            ("b.cs", "var x = 1;\n\n\nvar a = configuration[name];"),
            ("a.cs", "var z = 1;\nvar b = configuration[other];\nvar c = configuration[third];"),
        ]);

        Assert.Equal(["a.cs (line 2)", "a.cs (line 3)", "b.cs (line 4)"], scanner.DynamicReads);
    }

    [Fact]
    public void A_dynamic_read_inside_a_chain_is_listed_and_adds_no_key()
    {
        var scanner = Scan("""
            var a = configuration.GetSection("A")[name];
            var b = configuration.GetSection("B").GetSection(other).GetValue<int>("X");
            var c = configuration.GetSection($"C:{id}")["Y"];
            var d = configuration.GetSection($"D:{id}").GetValue<int>("Z");
            var e = configuration.GetSection(name).Get<EOptions>();
            """);

        Assert.Equal(["File0.cs (line 1)", "File0.cs (line 2)", "File0.cs (line 3)", "File0.cs (line 4)", "File0.cs (line 5)"], scanner.DynamicReads);
        Assert.Empty(scanner.Keys);
        Assert.Empty(scanner.Sections);
        Assert.Empty(scanner.BoundSections);
        Assert.False(scanner.Check(":Y").IsRead);
        Assert.False(scanner.Check("A").IsRead);
        Assert.False(scanner.Check("B:X").IsRead);
    }

    [Fact]
    public void The_line_of_a_dynamic_read_is_counted_from_the_start_of_the_file()
    {
        var scanner = Scan("var a = 1;\r\n\r\nvar b = configuration.GetSection(\"A\")\r\n    .GetSection(name);\r\n");

        Assert.Equal(["File0.cs (line 3)"], scanner.DynamicReads);
    }

    [Fact]
    public void Dynamic_value_reads_are_listed_and_not_added()
    {
        var scanner = Scan("""
            var a = configuration.GetValue<int>(name);
            var b = configuration.GetConnectionString(name);
            var c = configuration.GetValue<int>("");
            var d = configuration["A:{x}"];
            var e = configuration[""];
            """);

        Assert.Equal(["File0.cs (line 1)", "File0.cs (line 2)", "File0.cs (line 3)", "File0.cs (line 4)", "File0.cs (line 5)"], scanner.DynamicReads);
        Assert.Empty(scanner.Keys);
    }

    [Fact]
    public void A_chain_read_names_exactly_the_key_it_reads()
    {
        var scanner = Scan("""
            var a = configuration.GetSection("Cache")["Ttl"];
            var b = configuration.GetSection("Pool").GetValue<int>("Size");
            var c = configuration.GetRequiredSection("X").GetSection("Y")["Z"];
            var d = configuration.GetSection("M")["N"]["O"];
            """);

        Assert.Equal(["Cache:Ttl", "M:N", "Pool:Size", "X:Y:Z"], scanner.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(scanner.Sections);
        Assert.False(scanner.Check("Cache:Other").IsRead);
        Assert.False(scanner.Check("Cache").IsRead);
        Assert.False(scanner.Check("Pool:Max").IsRead);
        Assert.False(scanner.Check("Ttl").IsRead);
        Assert.False(scanner.Check("Size").IsRead);
        Assert.False(scanner.Check("M:N:O").IsRead);
    }

    [Fact]
    public void A_read_that_starts_where_a_chain_ends_still_counts()
    {
        var scanner = Scan("""var x = list.Select(c => c.GetSection("A")config["Z"]);""");

        Assert.True(scanner.Check("Z").IsRead);
    }

    [Fact]
    public void A_plain_section_is_listed_as_a_section_and_not_as_bound()
    {
        var scanner = Scan("""var s = configuration.GetSection("Plain"); var t = configuration.GetValue<int>("Direct");""");

        Assert.Equal(["Plain"], scanner.Sections);
        Assert.Empty(scanner.BoundSections);
        Assert.Equal(["Direct"], scanner.Keys);
        Assert.True(scanner.Check("Plain:Any:Thing").IsRead);
    }

    [Fact]
    public void A_connection_string_is_a_key_and_not_a_section()
    {
        var scanner = Scan("""var s = configuration.GetConnectionString("Orders");""");

        Assert.Equal(["ConnectionStrings:Orders"], scanner.Keys);
        Assert.Empty(scanner.Sections);
        Assert.False(scanner.Check("ConnectionStrings:Billing").IsRead);
    }

    [Fact]
    public void BindConfiguration_without_options_reads_the_whole_section()
    {
        var scanner = Scan("""
            builder.Services.AddOptions().BindConfiguration("Plain");
            builder.Services.AddOptions<SmtpOptions>().BindConfiguration("Smtp");
            class SmtpOptions { public string Host { get; set; } }
            """);

        Assert.Equal(["Plain"], scanner.Sections);
        Assert.Equal("SmtpOptions", scanner.BoundSections["Smtp"]);
        Assert.True(scanner.Check("Plain:Whatever").IsRead);
        Assert.False(scanner.Check("Smtp:Port").IsRead);
        Assert.True(scanner.Check("Smtp:Host").IsRead);
    }

    [Fact]
    public void Options_bound_with_Bind_read_only_their_properties()
    {
        var scanner = Scan("""
            builder.Services.AddOptions<AOptions>().Bind(builder.Configuration.GetSection("A"));
            var tmp = new BOptions(); configuration.GetSection("B").Bind(tmp);
            class AOptions { public int One { get; set; } }
            """);

        Assert.Equal("AOptions", scanner.BoundSections["A"]);
        Assert.True(scanner.Check("A:One").IsRead);
        Assert.False(scanner.Check("A:Two").IsRead);
        // Bind(instance) does not name the class, so the whole section counts as read.
        Assert.DoesNotContain("B", scanner.BoundSections.Keys);
        Assert.Contains("B", scanner.Sections);
    }

    [Fact]
    public void A_section_read_whole_is_not_narrowed_by_a_later_binding()
    {
        var scanner = Scan("""
            var all = configuration.GetSection("X");
            services.Configure<XOptions>(configuration.GetSection("X"));
            class XOptions { public int One { get; set; } }
            """);

        Assert.Empty(scanner.BoundSections);
        Assert.Equal(["X"], scanner.Sections);
        Assert.True(scanner.Check("X:Two").IsRead);
    }

    [Fact]
    public void A_section_bound_to_two_classes_counts_as_read_whole()
    {
        var scanner = Scan("""
            services.Configure<AOptions>(configuration.GetSection("X"));
            services.Configure<BOptions>(configuration.GetSection("X"));
            services.Configure<COptions>(configuration.GetSection("Y"));
            services.Configure<COptions>(configuration.GetSection("Y"));
            class AOptions { public int One { get; set; } }
            class BOptions { public int Two { get; set; } }
            class COptions { public int Three { get; set; } }
            """);

        Assert.DoesNotContain("X", scanner.BoundSections.Keys);
        Assert.Contains("X", scanner.Sections);
        Assert.True(scanner.Check("X:Anything").IsRead);
        Assert.Equal("COptions", scanner.BoundSections["Y"]);
        Assert.DoesNotContain("Y", scanner.Sections);
        Assert.False(scanner.Check("Y:Four").IsRead);
    }

    [Fact]
    public void Serilog_is_read_when_the_code_reads_the_configuration_into_it()
    {
        var with = Scan("""new LoggerConfiguration().ReadFrom.Configuration(configuration);""");

        Assert.Equal(["Serilog"], with.Sections);
        Assert.True(with.Check("Serilog:MinimumLevel:Default").IsRead);
        Assert.False(Scan("var x = 1;").Check("Serilog:MinimumLevel").IsRead);
    }

    [Fact]
    public void Resolves_names_constants_and_literals()
    {
        var scanner = Scan("""
            const string Cache = "Cache";
            static readonly string Ttl = "Cache:Ttl";
            var a = configuration[Ttl];
            var b = configuration[Keys.Cache + "x"];
            var c = configuration[nameof(Options.Retries)];
            var d = configuration[nameof(Plain)];
            var e = configuration.GetSection(Settings.Cache)["Size"];
            var f = configuration[@"Verbatim:Key"];
            var g = configuration.GetSection(@"Sec")["K"];
            """);

        Assert.True(scanner.Check("Cache:Ttl").IsRead);
        Assert.True(scanner.Check("Retries").IsRead);
        Assert.True(scanner.Check("Plain").IsRead);
        Assert.True(scanner.Check("Cache:Size").IsRead);
        Assert.True(scanner.Check("Verbatim:Key").IsRead);
        Assert.True(scanner.Check("Sec:K").IsRead);
        Assert.Equal(["File0.cs (line 4)"], scanner.DynamicReads);
    }

    [Fact]
    public void A_constant_in_another_file_resolves_and_the_first_definition_wins()
    {
        var scanner = Scan(
            """var a = configuration[Names.Key];""",
            """static class Names { public const string Key = "Real:Key"; }""",
            """static class Other { public const string Key = "Wrong:Key"; }""");

        Assert.True(scanner.Check("Real:Key").IsRead);
        Assert.False(scanner.Check("Wrong:Key").IsRead);
    }

    [Fact]
    public void Record_parameters_and_nested_classes_follow_the_shape()
    {
        var scanner = Scan(
            """
            services.Configure<Outer>(configuration.GetSection("O"));
            """,
            """
            public record Outer(string Host, Inner Child, Dictionary<string, int> Map, int Count);
            public record Inner(int Size);
            public class Later { public int Leak { get; set; } }
            """);

        Assert.True(scanner.Check("O:Host").IsRead);
        Assert.True(scanner.Check("O:Count").IsRead);
        Assert.True(scanner.Check("O:Child:Size").IsRead);
        Assert.False(scanner.Check("O:Child:Nope").IsRead);
        Assert.True(scanner.Check("O:Map:any:key").IsRead);
        Assert.False(scanner.Check("O:Missing").IsRead);
    }

    [Fact]
    public void A_class_does_not_take_properties_from_the_classes_after_it()
    {
        var scanner = Scan(
            """
            services.Configure<First>(configuration.GetSection("F"));
            """,
            """
            public class First
            {
                public int A { get; set; }
                public void Run() { if (A > 1) { A = 2; } }
                public int B { get; init; }
            }
            public class Second { public int Leak { get; set; } }
            """);

        Assert.True(scanner.Check("F:A").IsRead);
        Assert.True(scanner.Check("F:B").IsRead);
        Assert.False(scanner.Check("F:Leak").IsRead);
    }

    [Fact]
    public void A_class_that_is_never_closed_still_gives_its_properties()
    {
        var scanner = Scan(
            """services.Configure<Broken>(configuration.GetSection("B"));""",
            "public class Broken { public int A { get; set; }");

        Assert.True(scanner.Check("B:A").IsRead);
        Assert.False(scanner.Check("B:Z").IsRead);
    }

    [Fact]
    public void Inheritance_adds_the_properties_of_every_base_and_survives_a_cycle()
    {
        var scanner = Scan(
            """
            services.Configure<Leaf>(configuration.GetSection("L"));
            services.Configure<LoopA>(configuration.GetSection("Loop"));
            """,
            """
            class Leaf : Middle<int>, IThing { public int Own { get; set; } }
            class Middle<T> : Root { public int Mid { get; set; } }
            class Root { public int Top { get; set; } public int Own { get; set; } }
            class LoopA : LoopB { public int A { get; set; } }
            class LoopB : LoopA { public int B { get; set; } }
            """);

        Assert.True(scanner.Check("L:Own").IsRead);
        Assert.True(scanner.Check("L:Mid").IsRead);
        Assert.True(scanner.Check("L:Top").IsRead);
        Assert.False(scanner.Check("L:Nope").IsRead);
        Assert.True(scanner.Check("Loop:A").IsRead);
        Assert.True(scanner.Check("Loop:B").IsRead);
        Assert.False(scanner.Check("Loop:C").IsRead);
    }

    [Fact]
    public void Nullable_and_qualified_property_types_are_followed()
    {
        var scanner = Scan(
            """services.Configure<Root>(configuration.GetSection("R"));""",
            """
            class Root { public Child? One { get; set; } public My.Ns.Child Two { get; set; } public Child[] Many { get; set; } public List<Child> Items { get; set; } }
            class Child { public int Size { get; set; } }
            """);

        Assert.True(scanner.Check("R:One:Size").IsRead);
        Assert.False(scanner.Check("R:One:Other").IsRead);
        Assert.True(scanner.Check("R:Two:Size").IsRead);
        Assert.False(scanner.Check("R:Two:Other").IsRead);
        Assert.True(scanner.Check("R:Many:0:Other").IsRead);
        Assert.True(scanner.Check("R:Items:0:Other").IsRead);
    }

    [Theory]
    [InlineData("a // c\nb", "a \nb")]
    [InlineData("a /* x\ny */b", "a \nb")]
    [InlineData("a /**/b", "a b")]
    [InlineData("a /* x * y / z */b", "a b")]
    [InlineData("a /* never ends", "a ")]
    [InlineData("a /* ends *", "a ")]
    [InlineData("a // c", "a ")]
    [InlineData("a / b", "a / b")]
    [InlineData("\"http://x\" // c\nb", "\"http://x\" \nb")]
    [InlineData("\"a\\\"b\" // c", "\"a\\\"b\" ")]
    [InlineData("\"a\\\\\" // c", "\"a\\\\\" ")]
    [InlineData("@\"C:\\\" // c", "@\"C:\\\" ")]
    [InlineData("@\"a\"\"b\" // c", "@\"a\"\"b\" ")]
    [InlineData("@\"a\"\"\" // c", "@\"a\"\"\" ")]
    [InlineData("@\"x\ny//z\" // c", "@\"x\ny//z\" ")]
    [InlineData("@\"\" // c", "@\"\" ")]
    [InlineData("\"\" // c", "\"\" ")]
    [InlineData("\"abc\n// c\nx", "\"abc\n\nx")]
    [InlineData("\"abc", "\"abc")]
    [InlineData("@\"abc", "@\"abc")]
    [InlineData("@x // c\ny", "@x \ny")]
    [InlineData("'\"' // c", "'\"' ")]
    [InlineData("'/' + '/' // c", "'/' + '/' ")]
    [InlineData("'\\'' // c", "'\\'' ")]
    [InlineData("'\\\\' // c", "'\\\\' ")]
    [InlineData("'a", "'a")]
    [InlineData("'a\n// c\nx", "'a\n\nx")]
    [InlineData("x '", "x '")]
    public void Strips_comments_and_keeps_strings(string source, string expected)
    {
        Assert.Equal(expected, CodeReadScanner.StripComments(source));
    }

    [Fact]
    public void Splits_a_record_with_many_parameters()
    {
        var scanner = Scan(
            """services.Configure<Smtp>(configuration.GetSection("Smtp"));""",
            """record Smtp(string Host, int Port, [Required] string User, Dictionary<string, string> Headers, bool Tls = true);""");

        Assert.True(scanner.Check("Smtp:Host").IsRead);
        Assert.True(scanner.Check("Smtp:Port").IsRead);
        Assert.True(scanner.Check("Smtp:User").IsRead);
        Assert.True(scanner.Check("Smtp:Headers:X-Id").IsRead);
        Assert.True(scanner.Check("Smtp:Tls").IsRead);
        Assert.False(scanner.Check("Smtp:Password").IsRead);
    }

    [Fact]
    public void Skips_bin_obj_node_modules_and_git_folders_and_reads_nested_ones()
    {
        using var temp = new TempFolder();
        temp.Write("Program.cs", """var a = configuration["Top:Key"];""");
        temp.Write("deep/er/Reader.cs", """var a = configuration["Deep:Key"];""");
        temp.Write("bin/Debug/A.cs", """var a = configuration["Bin:Key"];""");
        temp.Write("obj/B.cs", """var a = configuration["Obj:Key"];""");
        temp.Write("node_modules/pkg/C.cs", """var a = configuration["Node:Key"];""");
        temp.Write(".git/D.cs", """var a = configuration["Git:Key"];""");
        temp.Write("binary/E.cs", """var a = configuration["Binary:Key"];""");

        var scanner = CodeReadScanner.Scan(temp.Path);

        Assert.Equal(3, scanner.FilesScanned);
        Assert.True(scanner.Check("Top:Key").IsRead);
        Assert.True(scanner.Check("Deep:Key").IsRead);
        Assert.True(scanner.Check("Binary:Key").IsRead);
        Assert.False(scanner.Check("Bin:Key").IsRead);
        Assert.False(scanner.Check("Obj:Key").IsRead);
        Assert.False(scanner.Check("Node:Key").IsRead);
        Assert.False(scanner.Check("Git:Key").IsRead);
    }

    [Fact]
    public void Does_not_follow_symbolic_links()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var outside = new TempFolder();
        outside.Write("Linked.cs", """var a = configuration["Linked:Key"];""");
        using var temp = new TempFolder();
        temp.Write("Program.cs", """var a = configuration["Top:Key"];""");
        Directory.CreateSymbolicLink(temp.Full("link"), outside.Path);

        var scanner = CodeReadScanner.Scan(temp.Path);

        Assert.Equal(1, scanner.FilesScanned);
        Assert.False(scanner.Check("Linked:Key").IsRead);
    }

    [Fact]
    public void Reads_a_file_of_exactly_one_megabyte_and_skips_a_bigger_one()
    {
        using var temp = new TempFolder();
        const string Code = """var a = configuration["Big:Key"];""";
        temp.Write("Exact.cs", Code + new string(' ', 1024 * 1024 - Code.Length));
        temp.Write("Over.cs", """var a = configuration["Over:Key"];""" + new string(' ', 1024 * 1024));

        var scanner = CodeReadScanner.Scan(temp.Path);

        Assert.Equal(1, scanner.FilesScanned);
        Assert.True(scanner.Check("Big:Key").IsRead);
        Assert.False(scanner.Check("Over:Key").IsRead);
    }

    [Fact]
    public void Reads_at_most_five_thousand_files()
    {
        using var temp = new TempFolder();
        for (var i = 0; i < 5002; i++)
        {
            File.WriteAllText(temp.Full($"F{i}.cs"), "var x = 1;");
        }

        Assert.Equal(5000, CodeReadScanner.Scan(temp.Path).FilesScanned);
    }

    [Fact]
    public void Follows_project_references_for_at_most_fifty_folders()
    {
        using var temp = new TempFolder();
        for (var i = 0; i < 52; i++)
        {
            var next = i + 1 < 52 ? $"""<ProjectReference Include="..\P{i + 1}\P{i + 1}.csproj" />""" : string.Empty;
            temp.Write($"P{i}/P{i}.csproj", $"<Project><ItemGroup>{next}</ItemGroup></Project>");
            temp.Write($"P{i}/Code.cs", "var x = 1;");
        }

        Assert.Equal(50, CodeReadScanner.Scan(temp.Full("P0")).FilesScanned);
    }

    [Fact]
    public void Ignores_project_references_that_are_missing_broken_or_circular()
    {
        using var temp = new TempFolder();
        temp.Write("A/A.csproj", """<Project><ItemGroup><ProjectReference Include="..\Missing\Missing.csproj" /><ProjectReference Include="..\B\B.csproj" /><ProjectReference Include="" /><ProjectReference /></ItemGroup></Project>""");
        temp.Write("A/A.cs", """var a = configuration["A:Key"];""");
        temp.Write("B/B.csproj", """<Project><ItemGroup><ProjectReference Include="..\A\A.csproj" /></ItemGroup></Project>""");
        temp.Write("B/B.cs", """var b = configuration["B:Key"];""");
        temp.Write("A/Broken.csproj", "<Project");

        var scanner = CodeReadScanner.Scan(temp.Full("A"));

        Assert.Equal(2, scanner.FilesScanned);
        Assert.True(scanner.Check("A:Key").IsRead);
        Assert.True(scanner.Check("B:Key").IsRead);
    }

    [Fact]
    public void A_verbatim_string_with_no_content_does_not_throw()
    {
        // Found by fuzzing: the argument @" is both the start and the end of a verbatim string.
        var scanner = Scan("var x = configuration.GetSection(@\"    ); class A { A get; set; } }");

        Assert.False(scanner.Check("A").IsRead && string.IsNullOrEmpty(scanner.Check("A").Reason));
    }
}
