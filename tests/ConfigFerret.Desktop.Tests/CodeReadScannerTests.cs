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

    [Theory]
    [InlineData("""var s = builder.Configuration.Get<AppSettings>();""", "ApiUrl")]
    [InlineData("""services.Configure<AppSettings>(builder.Configuration);""", "Retries")]
    [InlineData("""services.Configure<AppSettings>("named", context.Configuration);""", "ApiUrl")]
    [InlineData("""services.Configure<AppSettings>(builder.Configuration, o => o.BindNonPublicProperties = true);""", "ApiUrl")]
    [InlineData("""services.AddOptions<AppSettings>().Bind(builder.Configuration);""", "Retries")]
    [InlineData("""services.AddOptions<AppSettings>("named").Bind(builder.Configuration, o => o.ErrorOnUnknownConfiguration = true).ValidateOnStart();""", "ApiUrl")]
    [InlineData("""var all = configuration.Get<Dictionary<string, string>>();""", "Anything:Else")]
    [InlineData("""services.Configure<Dictionary<string, string>>(builder.Configuration);""", "Anything:Else")]
    [InlineData("""services.Configure<Dictionary<string, List<string>>>(builder.Configuration);""", "Anything:Else")]
    [InlineData("""var s = new AppSettings(); Configuration.Bind(s);""", "Anything:Else")]
    [InlineData("""Configuration.Bind(s, o => o.BindNonPublicProperties = true);""", "Anything:Else")]
    [InlineData("""services.AddAuthentication().AddJwtBearer(o => Configuration.Bind("JwtSettings", o));""", "JwtSettings:Authority")]
    [InlineData("""var smtp = new SmtpOptions(); builder.Configuration.Bind("Smtp", smtp);""", "Smtp:Host")]
    [InlineData("""var url = Environment.GetEnvironmentVariable("DATABASE_URL");""", "DATABASE_URL")]
    [InlineData("""var host = Environment.GetEnvironmentVariable("Db__Host");""", "Db:Host")]
    public void Finds_reads_of_the_whole_configuration_and_of_environment_variables(string code, string key)
    {
        var scanner = Scan(code, "public sealed class AppSettings { public string ApiUrl { get; set; } = \"\"; public int Retries { get; set; } }");

        Assert.True(scanner.Check(key).IsRead, key);
        Assert.Empty(scanner.DynamicReads);
    }

    [Fact]
    public void The_whole_configuration_bound_to_a_class_reads_only_its_properties()
    {
        var scanner = Scan(
            """
            var s = builder.Configuration.Get<AppSettings>();
            var t = builder.Configuration.GetSection("Other")["Key"];
            """,
            """
            public sealed class AppSettings { public string ApiUrl { get; set; } = ""; public Smtp Smtp { get; set; } = new(); }
            public sealed class Smtp { public string Host { get; set; } = ""; }
            """);

        Assert.True(scanner.Check("ApiUrl").IsRead);
        Assert.True(scanner.Check("Smtp:Host").IsRead);
        Assert.True(scanner.Check("Other:Key").IsRead);
        Assert.True(scanner.Check("Logging:LogLevel:Default").IsRead);
        Assert.Equal(new ReadVerdict(false, "The whole configuration is bound to AppSettings, which has no property Timeout"), scanner.Check("Timeout"));
        Assert.Equal(new ReadVerdict(false, "The whole configuration is bound to AppSettings; Smtp has no property Port"), scanner.Check("Smtp:Port"));
    }

    [Fact]
    public void Reads_of_the_whole_configuration_the_patterns_cannot_take_apart_are_listed()
    {
        var scanner = Scan(
            """
            var host = Environment.GetEnvironmentVariable(string.Join("_", parts.Select(p => p.ToUpper())));
            builder.Configuration.Bind(new AppSettings
            {
                Retries = 3,
            });
            var s = builder.Configuration.Get <AppSettings>();
            ConfigurationBinder.Bind(builder.Configuration, settings);
            var t = builder.Configuration.Get(typeof(AppSettings));
            """);

        Assert.Equal(["File0.cs (line 1)", "File0.cs (line 2)", "File0.cs (line 7)", "File0.cs (line 8)"], scanner.DynamicReads);
        Assert.True(scanner.Check("ApiUrl").IsRead);
    }

    [Theory]
    [InlineData("ASPNETCORE_FORWARDEDHEADERS_ENABLED")]
    [InlineData("ASPNETCORE_Kestrel__Certificates__Default__Path")]
    [InlineData("DOTNET_gcServer")]
    [InlineData("dotnet_SYSTEM_GLOBALIZATION_INVARIANT")]
    public void Host_variables_count_as_read(string key)
    {
        var scanner = Scan("var x = 1;");

        Assert.Equal(new ReadVerdict(true, string.Empty), scanner.Check(key));
        Assert.False(scanner.Check("App:" + key).IsRead);
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

        var scanner = CodeReadScanner.Scan(temp.Full("src/Api"), TestContext.Current.CancellationToken);

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
        var verdict = CodeReadScanner.FromSources([], TestContext.Current.CancellationToken).Check("A:B");

        Assert.False(verdict.IsRead);
        Assert.Equal("No C# code found to read it", verdict.Reason);
        Assert.Equal("No code reads it", Scan("var x = 1;").Check("A:B").Reason);
    }

    [Fact]
    public void Rejects_missing_arguments()
    {
        Assert.Throws<ArgumentException>(() => CodeReadScanner.Scan(" ", TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => CodeReadScanner.Scan(null!, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => CodeReadScanner.FromSources(null!, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => Scan("var x = 1;").Check(null!));
    }

    [Fact]
    public void Lists_unreadable_reads_by_file_then_line_without_repeats()
    {
        var scanner = CodeReadScanner.FromSources(
        [
            ("b.cs", "var x = 1;\n\n\nvar a = configuration[name];"),
            ("a.cs", "var z = 1;\nvar b = configuration[other];\nvar c = configuration[third];"),
        ],
        TestContext.Current.CancellationToken);

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
    public void A_constant_in_another_file_resolves_by_its_type()
    {
        var scanner = Scan(
            """var a = configuration[Names.Key];""",
            """static class Other { public const string Key = "Wrong:Key"; }""",
            """static class Names { public const string Key = "Real:Key"; }""");

        Assert.True(scanner.Check("Real:Key").IsRead);
        Assert.False(scanner.Check("Wrong:Key").IsRead);
    }

    [Fact]
    public void Options_classes_with_their_own_section_name_constant_each_bind_their_section()
    {
        var scanner = Scan(
            """
            services.Configure<SmtpOptions>(cfg.GetSection(SmtpOptions.SectionName));
            services.Configure<JwtOptions>(cfg.GetSection(JwtOptions.SectionName));
            services.AddOptions<CacheOptions>().BindConfiguration(My.Options.CacheOptions.SectionName);
            """,
            """
            public sealed class SmtpOptions { public const string SectionName = "Smtp"; public string Host { get; set; } = ""; }
            public sealed class JwtOptions { public const string SectionName = "Jwt"; public string Issuer { get; set; } = ""; }
            public sealed class CacheOptions { public const string SectionName = nameof(Cache); public int Ttl { get; set; } }
            """);

        Assert.True(scanner.Check("Smtp:Host").IsRead);
        Assert.True(scanner.Check("Jwt:Issuer").IsRead);
        Assert.True(scanner.Check("Cache:Ttl").IsRead);
        Assert.False(scanner.Check("Jwt:Host").IsRead);
        Assert.Equal("SmtpOptions", scanner.BoundSections["Smtp"]);
        Assert.Equal("JwtOptions", scanner.BoundSections["Jwt"]);
        Assert.Empty(scanner.DynamicReads);
    }

    [Fact]
    public void A_bare_constant_name_resolves_only_when_its_value_is_unique()
    {
        var scanner = Scan(
            """
            var a = configuration.GetSection(SectionName);
            var b = configuration[Shared];
            var c = configuration[Missing.Shared];
            """,
            """
            class AOptions { public const string SectionName = "A"; public const string Shared = "Same:Key"; }
            class BOptions { public const string SectionName = "B"; public const string Shared = "Same:Key"; }
            """);

        Assert.False(scanner.Check("A:X").IsRead);
        Assert.False(scanner.Check("B:X").IsRead);
        Assert.True(scanner.Check("Same:Key").IsRead);
        Assert.Equal(["File0.cs (line 1)", "File0.cs (line 3)"], scanner.DynamicReads);
    }

    [Fact]
    public void A_bare_constant_name_resolves_first_in_the_type_the_read_is_in()
    {
        var scanner = Scan(
            """
            public class SmtpOptions
            {
                public const string SectionName = "Smtp";

                public static string Host(IConfiguration configuration) => configuration.GetSection(SectionName)["Host"];
            }
            """,
            """
            public class JwtOptions
            {
                public const string SectionName = "Jwt";

                public static string Issuer(IConfiguration configuration) => configuration.GetSection(SectionName)["Issuer"];

                public class Signing
                {
                    // Signing declares no SectionName, and the two outside it differ, so the scan can't tell which is read.
                    public static string Key(IConfiguration configuration) => configuration.GetSection(SectionName)["Key"];
                }
            }
            """);

        Assert.True(scanner.Check("Smtp:Host").IsRead);
        Assert.True(scanner.Check("Jwt:Issuer").IsRead);
        Assert.False(scanner.Check("Jwt:Host").IsRead);
        Assert.False(scanner.Check("Smtp:Issuer").IsRead);
        Assert.Equal(["File1.cs (line 10)"], scanner.DynamicReads);
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

        var scanner = CodeReadScanner.Scan(temp.Path, TestContext.Current.CancellationToken);

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

        var scanner = CodeReadScanner.Scan(temp.Path, TestContext.Current.CancellationToken);

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

        var scanner = CodeReadScanner.Scan(temp.Path, TestContext.Current.CancellationToken);

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

        Assert.Equal(5000, CodeReadScanner.Scan(temp.Path, TestContext.Current.CancellationToken).FilesScanned);
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

        Assert.Equal(50, CodeReadScanner.Scan(temp.Full("P0"), TestContext.Current.CancellationToken).FilesScanned);
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

        var scanner = CodeReadScanner.Scan(temp.Full("A"), TestContext.Current.CancellationToken);

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

    [Fact]
    public void A_type_keyword_followed_by_long_whitespace_does_not_backtrack_for_seconds()
    {
        // Found by fuzzing: the adjacent \s* in the type declaration pattern made each run of blanks cost a power of its length.
        var source = string.Concat(Enumerable.Repeat("class A" + new string(' ', 120) + "x\n", 20));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Scan(source);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Took {watch.Elapsed.TotalSeconds:0.0}s.");
    }

    [Fact]
    public void Every_pattern_has_a_match_timeout()
    {
        var patterns = typeof(CodeReadScanner)
            .GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(System.Text.RegularExpressions.Regex) && m.GetParameters().Length == 0)
            .Select(m => (m.Name, Regex: (System.Text.RegularExpressions.Regex)m.Invoke(null, null)!))
            .ToList();

        Assert.True(patterns.Count >= 10, $"Found only {patterns.Count} patterns.");
        Assert.All(patterns, p => Assert.True(p.Regex.MatchTimeout != System.Text.RegularExpressions.Regex.InfiniteMatchTimeout, p.Name));
    }

    [Fact]
    public void A_file_a_pattern_times_out_on_is_skipped_and_listed()
    {
        var scanner = CodeReadScanner.FromSources(
            [
                ("Slow.cs", """var a = configuration["Slow:Key"];"""),
                ("Fine.cs", """var b = configuration["Fine:Key"];"""),
            ],
            (name, _) =>
            {
                if (name == "Slow.cs")
                {
                    throw new System.Text.RegularExpressions.RegexMatchTimeoutException();
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, scanner.FilesScanned);
        Assert.Equal(["Slow.cs"], scanner.SkippedFiles);
        Assert.True(scanner.Check("Fine:Key").IsRead);
        Assert.False(scanner.Check("Slow:Key").IsRead);
    }

    [Fact]
    public void The_classes_of_a_file_that_times_out_while_its_properties_are_read_are_left_open()
    {
        var scanner = CodeReadScanner.FromSources(
            [
                ("Options.cs", """public class AppOptions : AppBase { public int Port { get; set; } }"""),
                ("Base.cs", """public class AppBase { public int Size { get; set; } }"""),
                ("Program.cs", """services.Configure<AppOptions>(cfg.GetSection("App")); services.Configure<AppBase>(cfg.GetSection("Base"));"""),
            ],
            (name, step) =>
            {
                if (name == "Options.cs" && step == "properties")
                {
                    throw new System.Text.RegularExpressions.RegexMatchTimeoutException();
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["Options.cs"], scanner.SkippedFiles);
        Assert.Equal(new ReadVerdict(true, string.Empty), scanner.Check("App:Port"));
        Assert.True(scanner.Check("App:Size").IsRead);
        Assert.Equal(new ReadVerdict(false, "Base is bound to AppBase, which has no property Port"), scanner.Check("Base:Port"));
    }

    [Fact]
    public void A_cancelled_scan_stops_inside_a_long_file()
    {
        using var cancel = new CancellationTokenSource();
        var source = string.Concat(Enumerable.Repeat("var a = configuration[\"A:Key\"];\n", 5_000));

        Assert.Throws<OperationCanceledException>(() => CodeReadScanner.FromSources([("Long.cs", source)], (_, step) =>
        {
            if (step == "calls")
            {
                cancel.Cancel();
            }
        },
        cancel.Token));
    }

    [Fact]
    public void Reads_the_patterns_cannot_take_apart_are_listed()
    {
        var scanner = Scan(
            "var a = config.GetSection(string.Join(\":\", parts.Take(2)));\n"
            + "var b = configuration[Keys.For(nameof(X))];\n"
            + "var c = configuration.GetValue<List<string>>(\"Hosts\");\n"
            + "var d = configuration.GetSection(" + string.Join(" + ", Enumerable.Repeat("x", 200)) + ");\n"
            + "var e = configuration.GetSection(\"Plain\")[\"Key\"];\n");

        Assert.Equal(["File0.cs (line 1)", "File0.cs (line 2)", "File0.cs (line 3)", "File0.cs (line 4)"], scanner.DynamicReads);
        Assert.True(scanner.Check("Plain:Key").IsRead);
    }

    [Fact]
    public void A_brace_in_a_string_or_character_does_not_close_a_class()
    {
        var scanner = Scan(
            """
            services.Configure<AppOptions>(cfg.GetSection("App"));
            public class AppOptions
            {
                public class Inner { public string F { get; set; } = "{"; public char C { get; set; } = '{'; }
                public int Port { get; set; }
                public string Close { get; set; } = "}";
                public string Verbatim { get; set; } = @"}""}";
                public int Last { get; set; }
            }
            """);

        Assert.True(scanner.Check("App:Port").IsRead);
        Assert.True(scanner.Check("App:Last").IsRead);
        Assert.False(scanner.Check("App:F").IsRead);
    }

    [Fact]
    public void A_cancelled_scan_stops_at_the_next_file()
    {
        using var cancel = new CancellationTokenSource();

        IEnumerable<(string, string)> Sources()
        {
            yield return ("A.cs", """var a = configuration["A:Key"];""");
            cancel.Cancel();
            yield return ("B.cs", """var b = configuration["B:Key"];""");
        }

        Assert.Throws<OperationCanceledException>(() => CodeReadScanner.FromSources(Sources(), cancel.Token));

        using var temp = new TempFolder();
        temp.Write("Program.cs", """var a = configuration["A:Key"];""");
        Assert.Throws<OperationCanceledException>(() => CodeReadScanner.Scan(temp.Path, cancel.Token));
    }

    [Fact]
    public void A_nested_type_keeps_its_properties_to_itself()
    {
        var scanner = Scan(
            """
            services.Configure<Outer>(configuration.GetSection("O"));
            services.Configure<Inner>(configuration.GetSection("I"));
            """,
            """
            public class Outer
            {
                public class Inner { public int Deep { get; set; } }
                public int Top { get; set; }
                public Inner Child { get; set; } = new();
            }
            """);

        Assert.True(scanner.Check("O:Top").IsRead);
        Assert.True(scanner.Check("O:Child:Deep").IsRead);
        Assert.True(scanner.Check("I:Deep").IsRead);
        Assert.False(scanner.Check("O:Deep").IsRead);
        Assert.False(scanner.Check("I:Top").IsRead);
    }

    [Fact]
    public void A_property_the_patterns_cannot_take_apart_leaves_its_class_open()
    {
        var scanner = Scan(
            """
            services.Configure<TupleOptions>(cfg.GetSection("Tuple"));
            services.Configure<GlobalOptions>(cfg.GetSection("Global"));
            services.Configure<WideOptions>(cfg.GetSection("Wide"));
            services.Configure<PlainOptions>(cfg.GetSection("Plain"));
            """,
            """
            public class TupleOptions { public (int Low, int High) Range { get; set; } }
            public class GlobalOptions { public global::System.String Name { get; set; } = ""; }
            public class WideOptions { public Dictionary<string, Func<int, int, int, int, int, int, int, int, int, int>> Handlers { get; set; } = new(); }
            public class PlainOptions { public int Size { get; set; } public int Twice => Size * 2; }
            """);

        Assert.True(scanner.Check("Tuple:Range:Low").IsRead);
        Assert.True(scanner.Check("Global:Name").IsRead);
        Assert.True(scanner.Check("Wide:Handlers:a").IsRead);
        Assert.Equal(new ReadVerdict(false, "Plain is bound to PlainOptions, which has no property Color"), scanner.Check("Plain:Color"));
    }

    [Fact]
    public void A_configuration_key_name_counts_after_any_number_of_attributes()
    {
        var scanner = Scan(
            """
            services.Configure<NamedOptions>(cfg.GetSection("Named"));
            services.Configure<ConstantOptions>(cfg.GetSection("Constant"));
            """,
            """
            public class NamedOptions
            {
                [ConfigurationKeyName("pwd")] [A] [B] [C] [D] [E] [F] [G] [H] [I] [J] [K]
                public string Password { get; set; } = "";
            }

            public class ConstantOptions
            {
                [ConfigurationKeyName(Keys.Token)]
                public string Token { get; set; } = "";
            }
            """);

        Assert.True(scanner.Check("Named:pwd").IsRead);
        Assert.Equal(new ReadVerdict(false, "Named is bound to NamedOptions, which has no property Color"), scanner.Check("Named:Color"));
        Assert.True(scanner.Check("Constant:token_key").IsRead);
    }

    [Fact]
    public void A_base_too_far_up_or_one_the_scan_cannot_take_apart_leaves_the_class_open()
    {
        // C0 derives from C1 and so on: C16 is the 16th base of C0, and C17 the 17th.
        var chain = string.Concat(Enumerable.Range(0, 17).Select(i => $"class C{i} : C{i + 1} {{ }}\n")) + "class C17 { public int Far { get; set; } }";
        var constraints = string.Join(", ", Enumerable.Range(0, 150).Select(i => $"IConstraint{i}"));
        var scanner = Scan(
            """
            services.Configure<C0>(cfg.GetSection("Deep"));
            services.Configure<C1>(cfg.GetSection("Near"));
            services.Configure<Derived>(cfg.GetSection("Derived"));
            """,
            chain,
            $$"""
            public class Derived : Base<int> { public int Own { get; set; } }
            public class Base<T> where T : {{constraints}} { public int Inherited { get; set; } }
            """);

        Assert.True(scanner.Check("Deep:Other").IsRead);
        Assert.True(scanner.Check("Near:Far").IsRead);
        Assert.Equal(new ReadVerdict(false, "Near is bound to C1, which has no property Other"), scanner.Check("Near:Other"));
        Assert.True(scanner.Check("Derived:Own").IsRead);
        Assert.True(scanner.Check("Derived:Inherited").IsRead);
    }

    [Fact]
    public void Class_declarations_with_generics_parameters_base_and_constraints_still_parse()
    {
        var scanner = Scan("""
            public class Options<T> : Base<T> where T : class { public int A { get; set; } }
            public record Pair(string X, int Y) : Base;
            public class Base<T> { public string Name { get; set; } }
            public class Base { public string Z { get; set; } }
            services.Configure<Options<int>>(config.GetSection("Opt"));
            """);

        Assert.True(scanner.Check("Opt:A").IsRead);
    }
}

// The hostile files run alone, after the tests that run in parallel, so other tests don't eat the time limit.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostileCodeFiles
{
    public const string Name = "Hostile code files";
}

[Collection(HostileCodeFiles.Name)]
public class CodeReadScannerHostileFileTests
{
    // Each of these took far longer than the limit, up to days, before the patterns and the loops around them were made
    // linear. Now each takes well under a second, so the limit leaves room for a slow, busy machine. The scan runs on a
    // thread of its own, so a thread pool kept busy by other tests doesn't delay its start.
    [Theory]
    [InlineData("blanks after a base list colon")]
    [InlineData("base lists with where clauses that never open a body")]
    [InlineData("type parameter lists that never close")]
    [InlineData("a long line of public words in a class")]
    [InlineData("attributes that never close in a class")]
    [InlineData("many attributes in a class")]
    [InlineData("a record parameter followed by blanks")]
    [InlineData("GetSection calls that never close")]
    [InlineData("GetValue calls that never close")]
    [InlineData("indexers that never close")]
    [InlineData("blanks after GetValue")]
    [InlineData("blanks after a config variable")]
    [InlineData("blanks after a GetSection call")]
    [InlineData("generic GetValue calls that never close")]
    [InlineData("many reads the scanner cannot work out")]
    [InlineData("classes nested without end")]
    [InlineData("a long chain of base classes")]
    [InlineData("public words before a brace again and again")]
    [InlineData("configuration key names that never close")]
    [InlineData("many GetSection calls")]
    [InlineData("many classes deriving from a class with many properties")]

    // Shapes for the patterns that find binds of the whole configuration, held to the same limit.
    [InlineData("a long word that ends in config again and again")]
    [InlineData("a Configure call with a long dotted argument")]
    [InlineData("AddOptions calls that never close")]
    [InlineData("Configure calls with type arguments that never close")]
    public async Task A_hostile_file_is_scanned_in_linear_time(string shape)
    {
        var source = HostileSource(shape);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var scan = Task.Factory.StartNew(() => CodeReadScanner.FromSources([("File0.cs", source)]), TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var finished = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) == scan;

        Assert.True(finished, $"Scanning {source.Length} characters of {shape} took longer than {watch.Elapsed.TotalSeconds:0}s.");
        Assert.Equal(1, (await scan).FilesScanned);
    }

    private static string HostileSource(string shape) => shape switch
    {
        "blanks after a base list colon" => "class A : " + new string(' ', 20_000),
        "base lists with where clauses that never open a body" => Repeat("class A : B where ", 3_000_000),
        "type parameter lists that never close" => Repeat("class A<", 3_000_000),
        "a long line of public words in a class" => "class A {" + Repeat("public a ", 200_000),
        "attributes that never close in a class" => "class A {" + Repeat("[a ", 3_000_000),
        "many attributes in a class" => "class A {" + Repeat("[a] ", 200_000),
        "a record parameter followed by blanks" => "record A(a" + new string(' ', 200_000) + ");",
        "GetSection calls that never close" => Repeat("GetSection(", 200_000),
        "GetValue calls that never close" => Repeat("GetValue(", 200_000),
        "indexers that never close" => Repeat("config[", 200_000),
        "blanks after GetValue" => "GetValue" + new string(' ', 200_000),
        "blanks after a config variable" => "config" + new string(' ', 200_000),
        "blanks after a GetSection call" => "config.GetSection(\"A\")" + new string(' ', 200_000),
        "generic GetValue calls that never close" => Repeat("GetValue<", 2_000_000),
        "many reads the scanner cannot work out" => Repeat("config[x];\n", 1_000_000),
        "classes nested without end" => Repeat("class A {", 400_000),
        "public words before a brace again and again" => "class A {" + Repeat("public " + new string('a', 390) + " ", 3_000_000),
        "configuration key names that never close" => "class A {" + Repeat("[ConfigurationKeyName(\"", 3_000_000),
        "a long chain of base classes" => string.Concat(Enumerable.Range(0, 40_000).Select(i => $"class C{i} : C{i + 1} {{ }}\n")),
        "many GetSection calls" => Repeat("GetSection(\"a\")", 3_000_000),
        "many classes deriving from a class with many properties" =>
            "class B {\n" + string.Concat(Enumerable.Range(0, 12_000).Select(i => $"public int P{i} {{ get; }}\n")) + "}\n"
            + string.Concat(Enumerable.Range(0, 12_000).Select(i => $"class D{i} : B {{ }}\n")),
        "a long word that ends in config again and again" => Repeat("config", 1_000_000) + " .Bind",
        "Configure calls with type arguments that never close" => Repeat("Configure<A<b ", 2_000_000) + Repeat("Configure<A<" + new string('b', 10_000), 1_000_000),
        "AddOptions calls that never close" => Repeat("AddOptions<A>(" + new string('a', 1_000), 3_000_000),
        "a Configure call with a long dotted argument" => Repeat("Configure<A>(" + Repeat("a.", 1_000) + "config ", 1_000_000),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static string Repeat(string unit, int length) => string.Concat(Enumerable.Repeat(unit, length / unit.Length));
}
