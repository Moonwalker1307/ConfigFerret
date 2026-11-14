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
}
