using System.Text.Json;

namespace ConfigFerret.Tool.Tests;

public class TraceCommandTests
{
    // The Helm release fixture: an orders service whose Deployment loads a ConfigMap with envFrom and prefix ORDERS_.
    private static string[] HelmRelease(params string[] extra)
    {
        var dir = ToolRunner.Golden("helm-release");
        return
        [
            "trace",
            "--app", Path.Join(dir, "app"),
            "--manifests", Path.Join(dir, "rendered.yaml"),
            "--workload", "orders",
            "--container", "orders",
            "--env-prefix", "ORDERS_",
            "--chart", Path.Join(dir, "chart"),
            "--values", Path.Join(dir, "values-prod.yaml"),
            "--values", Path.Join(dir, "values-eu.yaml"),
            "--set", "cache.ttl=900",
            .. extra,
        ];
    }

    [Fact]
    public async Task Traces_a_key_from_appsettings_through_the_ConfigMap_to_the_Helm_values()
    {
        var result = await new ToolRunner().RunAsync(HelmRelease("Db:Timeout"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("""Db:Timeout = "60""", result.Output, StringComparison.Ordinal);
        Assert.Contains("""[overridden] appsettings.Production.json (line 3) = "30""", result.Output, StringComparison.Ordinal);
        Assert.Contains("""[effective]  environment variable ORDERS_Db__Timeout = "60""", result.Output, StringComparison.Ordinal);
        Assert.Contains("ConfigMap orders-config (key Db__Timeout, rendered.yaml:11)", result.Output, StringComparison.Ordinal);
        Assert.Contains("""[effective]  values-eu.yaml (line 2, db.timeout) = "60""", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Db:Port", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepts_double_underscore_keys()
    {
        var result = await new ToolRunner().RunAsync(HelmRelease("Db__Timeout"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("""Db:Timeout = "60""", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redacts_secrets_by_default()
    {
        var result = await new ToolRunner().RunAsync(HelmRelease());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("""Db:Password = "***""", result.Output, StringComparison.Ordinal);
        Assert.Contains("Secret orders-db (key password, rendered.yaml:31)", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("prod-s3cr3t", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("prod-s3cr3t", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shows_secrets_only_with_reveal_secrets_and_warns()
    {
        var result = await new ToolRunner().RunAsync(HelmRelease("Db:Password", "--reveal-secrets"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("""Db:Password = "prod-s3cr3t""", result.Output, StringComparison.Ordinal);
        Assert.Contains("secret values are shown", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_json_with_only_the_keys_asked_for()
    {
        var result = await new ToolRunner().RunAsync(HelmRelease("Db:Timeout", "Db:Password", "--format", "json"));

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var settings = json.RootElement.GetProperty("settings").EnumerateArray().ToList();
        Assert.Equal(["Db:Timeout", "Db:Password"], settings.Select(s => s.GetProperty("key").GetString()));
        Assert.Equal("***", settings[1].GetProperty("value").GetString());
        Assert.True(settings[1].GetProperty("sensitive").GetBoolean());
    }

    [Fact]
    public async Task Exits_with_1_when_a_key_is_not_set()
    {
        var result = await new ToolRunner().RunAsync(HelmRelease("Db:Timeout", "Orders:Missing"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Orders:Missing is not set by any configuration provider.", result.Error, StringComparison.Ordinal);
        Assert.Contains("Db:Timeout", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Escapes_control_characters_in_a_key_that_is_not_set()
    {
        var result = await new ToolRunner().RunAsync(HelmRelease("Orders:Missing\n\u001b[2Kforged"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Orders:Missing\\n\\u001B[2Kforged is not set by any configuration provider.", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Error.ReplaceLineEndings(""), c => char.IsControl(c));
    }

    [Fact]
    public async Task Passes_arguments_after_double_dash_to_the_app_as_its_command_line()
    {
        var result = await new ToolRunner().RunAsync([.. HelmRelease("Features:Search"), "--", "--Features:Search=false"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("""Features:Search = "false""", result.Output, StringComparison.Ordinal);
        Assert.Contains("[effective]  command-line argument --Features:Search", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-p=fake-hunter2")]
    [InlineData("--Db:Host=x", "-p=fake-hunter2")]
    public async Task Leaves_the_argument_out_of_the_error_for_an_app_argument_the_command_line_rejects(params string[] appArgs)
    {
        var result = await new ToolRunner().RunAsync([.. HelmRelease("Db:Host"), "--", .. appArgs]);

        Assert.Equal(2, result.ExitCode);
        Assert.DoesNotContain("fake-hunter2", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-hunter2", result.Output, StringComparison.Ordinal);
        Assert.Contains("app's arguments after --", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=fake-hunter2")]
    [InlineData("fake-hunter2")]
    [InlineData(" =fake-hunter2")]
    [InlineData("\t =fake-hunter2")]
    public async Task Leaves_the_argument_out_of_the_error_for_an_env_without_a_name(string pair)
    {
        var result = await new ToolRunner().RunAsync([.. HelmRelease("Db:Host"), "--env", "Db__Host=x", "--env", pair]);

        Assert.Equal(2, result.ExitCode);
        Assert.DoesNotContain("fake-hunter2", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-hunter2", result.Output, StringComparison.Ordinal);
        Assert.Contains("--env expects NAME=VALUE", result.Error, StringComparison.Ordinal);
        Assert.Contains("--env number 2", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("trace", "TOKEN=fake-s3cret")]
    [InlineData("trace", "--token=fake-s3cret")]
    [InlineData("unused", "TOKEN=fake-s3cret")]
    [InlineData("container", "TOKEN=fake-s3cret")]
    public async Task Leaves_out_a_pair_that_no_env_took(string command, string pair)
    {
        string[] args = command == "container"
            ? ["container", "-m", Path.Join(ToolRunner.Golden("helm-release"), "rendered.yaml"), "-w", "orders", pair]
            : [command, .. HelmRelease()[1..], "--env", "Db__Host=x", pair];

        var result = await new ToolRunner().RunAsync(args);

        Assert.Equal(2, result.ExitCode);
        Assert.DoesNotContain("fake-s3cret", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-s3cret", result.Output, StringComparison.Ordinal);
        Assert.Contains("Give each variable its own --env", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Never_reads_the_tools_own_environment_into_the_trace()
    {
        var runner = new ToolRunner();
        runner.Environment["ORDERS_Db__Timeout"] = "999";
        runner.Environment["ORDERS_Leaked"] = "from-the-tool";
        runner.Environment["GITHUB_TOKEN"] = "ghp_not_for_the_report";

        var result = await runner.RunAsync(HelmRelease());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("""Db:Timeout = "60""", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("999", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Leaked", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Names_variables_the_manifests_have_no_value_for_and_takes_them_from_env()
    {
        var without = await new ToolRunner().RunAsync(HelmRelease("Pod:Name"));
        Assert.Equal(1, without.ExitCode);
        Assert.Contains("don't hold the value of ORDERS_Pod__Name", without.Error, StringComparison.Ordinal);

        var with = await new ToolRunner().RunAsync(HelmRelease("Pod:Name", "--env", "ORDERS_Pod__Name=orders-0"));
        Assert.Equal(0, with.ExitCode);
        Assert.Contains("""Pod:Name = "orders-0""", with.Output, StringComparison.Ordinal);
        Assert.Contains("downward API metadata.name", with.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDERS_Pod__Name", with.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Env_wins_over_env_file_which_wins_over_the_manifests()
    {
        var folder = Directory.CreateTempSubdirectory("configferret-tool-");
        try
        {
            var envFile = Path.Join(folder.FullName, "app.properties");
            await File.WriteAllTextAsync(envFile, "# overrides\nexport ORDERS_Db__Timeout=\"70\"\nORDERS_Db__Port=6543\n", TestContext.Current.CancellationToken);

            var result = await new ToolRunner().RunAsync(HelmRelease("Db:Timeout", "Db:Port", "--env-file", envFile, "--env", "ORDERS_Db__Port=7000"));

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("""Db:Timeout = "70""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""Db:Port = "7000""", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Keeps_the_variables_without_a_prefix_when_an_env_prefix_is_given()
    {
        var app = Directory.CreateTempSubdirectory("configferret-app-");
        try
        {
            await File.WriteAllTextAsync(Path.Join(app.FullName, "appsettings.json"), """{ "Logging": { "LogLevel": { "Default": "Information" } }, "Db": { "Timeout": 30 } }""", TestContext.Current.CancellationToken);

            var result = await new ToolRunner().RunAsync(
                "trace", "-a", app.FullName, "--no-user-secrets", "--env-prefix", "ORDERS_",
                "--env", "Logging__LogLevel__Default=Debug", "--env", "ORDERS_Db__Timeout=42", "Logging:LogLevel:Default", "Db:Timeout");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("""Logging:LogLevel:Default = "Debug""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""[effective]  environment variable Logging__LogLevel__Default = "Debug""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""[effective]  environment variable ORDERS_Db__Timeout = "42""", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            app.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Leaves_out_the_variables_without_a_prefix_with_no_default_env()
    {
        var app = Directory.CreateTempSubdirectory("configferret-app-");
        try
        {
            await File.WriteAllTextAsync(Path.Join(app.FullName, "appsettings.json"), """{ "Logging": { "LogLevel": { "Default": "Information" } }, "Db": { "Timeout": 30 } }""", TestContext.Current.CancellationToken);
            string[] env = ["--env", "Logging__LogLevel__Default=Debug", "--env", "ORDERS_Db__Timeout=42", "Logging:LogLevel:Default", "Db:Timeout"];

            var prefixed = await new ToolRunner().RunAsync(["trace", "-a", app.FullName, "--no-user-secrets", "--no-default-env", "--env-prefix", "ORDERS_", .. env]);
            var none = await new ToolRunner().RunAsync(["trace", "-a", app.FullName, "--no-user-secrets", "--no-default-env", .. env]);

            Assert.Equal(0, prefixed.ExitCode);
            Assert.Contains("""[effective]  appsettings.json (line 1) = "Information""", prefixed.Output, StringComparison.Ordinal);
            Assert.Contains("""[effective]  environment variable ORDERS_Db__Timeout = "42""", prefixed.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Logging__LogLevel__Default", prefixed.Output, StringComparison.Ordinal);

            Assert.Equal(0, none.ExitCode);
            Assert.Contains("""Db:Timeout = "30""", none.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("environment variable", none.Output, StringComparison.Ordinal);
        }
        finally
        {
            app.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk.Web" />""", "Orders.Api")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><AssemblyName>Orders</AssemblyName></PropertyGroup></Project>""", "Orders")]
    public async Task Reads_the_application_settings_files_after_the_appsettings_files(string project, string applicationName)
    {
        var app = Directory.CreateTempSubdirectory("configferret-app-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Join(app.FullName, "Orders.Api.csproj"), project, ct);
            await File.WriteAllTextAsync(Path.Join(app.FullName, "appsettings.json"), """{ "Db": { "Timeout": 30, "Host": "localhost" } }""", ct);
            await File.WriteAllTextAsync(Path.Join(app.FullName, "appsettings.Staging.json"), """{ "Db": { "Timeout": 35 } }""", ct);
            await File.WriteAllTextAsync(Path.Join(app.FullName, applicationName + ".settings.json"), """{ "Db": { "Timeout": 40, "Host": "db.app" } }""", ct);
            await File.WriteAllTextAsync(Path.Join(app.FullName, applicationName + ".settings.Staging.json"), """{ "Db": { "Host": "db.staging" } }""", ct);

            var result = await new ToolRunner().RunAsync("trace", "-a", app.FullName, "--no-user-secrets", "-e", "Staging", "--env", "Db__Host=db.env", "Db:Timeout", "Db:Host");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("""Db:Timeout = "40""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""[overridden] appsettings.Staging.json (line 1) = "35""", result.Output, StringComparison.Ordinal);
            Assert.Contains($"""[effective]  {applicationName}.settings.json (line 1) = "40""", result.Output, StringComparison.Ordinal);
            Assert.Contains($"""[overridden] {applicationName}.settings.Staging.json (line 1) = "db.staging""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""[effective]  environment variable Db__Host = "db.env""", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            app.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Says_once_when_the_app_folder_holds_several_project_files(int projects)
    {
        var app = Directory.CreateTempSubdirectory("configferret-tool-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Join(app.FullName, "appsettings.json"), """{ "Db": { "Host": "localhost" } }""", ct);
            for (var i = 0; i < projects; i++)
            {
                await File.WriteAllTextAsync(
                    Path.Join(app.FullName, $"Orders{i}.csproj"),
                    "<Project><PropertyGroup><AssemblyName>Hidden</AssemblyName></PropertyGroup></Project>",
                    ct);
            }

            var result = await new ToolRunner().RunAsync("trace", "-a", app.FullName, "--no-user-secrets", "Db:Host");

            Assert.Equal(0, result.ExitCode);
            string[] expected = projects < 2
                ? []
                : [$"configferret: {app.FullName} holds several project files, so no {{ApplicationName}}.settings.json or UserSecretsId is read."];
            Assert.Equal(expected, result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')));
            Assert.DoesNotContain("Hidden", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("Orders0", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            app.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("link", "links to a file outside the app folder")]
    [InlineData("dtd", "holds a DTD or is not valid XML")]
    [InlineData("xml", "holds a DTD or is not valid XML")]
    [InlineData("size", "is bigger than 64 MB")]
    public async Task Takes_the_application_name_from_the_file_name_of_a_project_it_does_not_read(string how, string reason)
    {
        Assert.SkipWhen(how == "link" && OperatingSystem.IsWindows(), "Creating a symbolic link on Windows needs rights the tests may not have.");

        var root = Directory.CreateTempSubdirectory("configferret-tool-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var app = Directory.CreateDirectory(Path.Join(root.FullName, "app")).FullName;
            var project = Path.Join(app, "Orders.csproj");
            await File.WriteAllTextAsync(Path.Join(app, "appsettings.json"), """{ "Db": { "Host": "localhost" } }""", ct);
            await File.WriteAllTextAsync(Path.Join(app, "Orders.settings.json"), """{ "Db": { "Host": "from-the-file-name" } }""", ct);
            await File.WriteAllTextAsync(Path.Join(app, "Unread.settings.json"), """{ "Db": { "Host": "from-the-project" } }""", ct);
            const string Unread = """<Project><PropertyGroup><AssemblyName>Unread</AssemblyName></PropertyGroup></Project>""";
            switch (how)
            {
                case "link":
                    var outside = Path.Join(root.FullName, "Outside.csproj");
                    await File.WriteAllTextAsync(outside, Unread, ct);
                    File.CreateSymbolicLink(project, outside);
                    break;
                case "dtd":
                    await File.WriteAllTextAsync(
                        project,
                        """<!DOCTYPE Project [ <!ENTITY name "Unread"> ]><Project><PropertyGroup><AssemblyName>&name;</AssemblyName></PropertyGroup></Project>""",
                        ct);
                    break;
                case "xml":
                    await File.WriteAllTextAsync(project, """<Project><PropertyGroup><AssemblyName>Unread</AssemblyName>""", ct);
                    break;
                default:
                    // A valid project file, padded with white space after its root to just over the limit.
                    await using (var stream = File.Create(project))
                    {
                        var start = System.Text.Encoding.UTF8.GetBytes(Unread);
                        await stream.WriteAsync(start, ct);
                        var padding = new byte[1024 * 1024];
                        Array.Fill(padding, (byte)' ');
                        while (stream.Length <= InputReader.MaxFoundFileBytes)
                        {
                            await stream.WriteAsync(padding, ct);
                        }
                    }

                    break;
            }

            var result = await new ToolRunner().RunAsync("trace", "-a", app, "--no-user-secrets", "Db:Host");
            var json = await new ToolRunner().RunAsync("trace", "-a", app, "--no-user-secrets", "--format", "json", "Db:Host");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("""Db:Host = "from-the-file-name""", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Unread.settings.json", result.Output, StringComparison.Ordinal);

            // One warning that names the file and the reason, and holds nothing from the file.
            var warning = $"configferret: {project} {reason}, so its UserSecretsId and AssemblyName are not used.";
            Assert.Equal([warning], result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')));
            Assert.DoesNotContain("Unread", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("<", result.Error, StringComparison.Ordinal);

            Assert.Equal(0, json.ExitCode);
            using var document = System.Text.Json.JsonDocument.Parse(json.Output);
            Assert.Contains(warning, json.Error, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Traces_with_user_secrets_id_when_the_project_file_links_to_nothing()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link on Windows needs rights the tests may not have.");

        var app = Directory.CreateTempSubdirectory("configferret-app-");
        try
        {
            await File.WriteAllTextAsync(Path.Join(app.FullName, "appsettings.json"), """{ "Db": { "Host": "localhost" } }""", TestContext.Current.CancellationToken);
            File.CreateSymbolicLink(Path.Join(app.FullName, "Orders.csproj"), Path.Join(app.FullName, "missing", "Orders.csproj"));

            var result = await new ToolRunner().RunAsync("trace", "-a", app.FullName, "--user-secrets-id", "orders-fake-id", "Db:Host");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("""Db:Host = "localhost""", result.Output, StringComparison.Ordinal);
            Assert.Contains(
                $"configferret: {Path.Join(app.FullName, "Orders.csproj")} could not be read, so its UserSecretsId and AssemblyName are not used.",
                result.Error,
                StringComparison.Ordinal);
            Assert.DoesNotContain("missing", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            app.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Maps_ASPNETCORE_and_DOTNET_variables_to_keys_below_the_settings_files()
    {
        var app = Directory.CreateTempSubdirectory("configferret-app-");
        try
        {
            await File.WriteAllTextAsync(Path.Join(app.FullName, "appsettings.json"), """{ "Db": { "Timeout": 30 } }""", TestContext.Current.CancellationToken);
            string[] input = ["trace", "-a", app.FullName, "--no-user-secrets", "--env", "ASPNETCORE_URLS=http://+:8080", "--env", "DOTNET_Db__Timeout=5", "urls", "Db:Timeout"];

            var result = await new ToolRunner().RunAsync(input);
            var withoutDefaults = await new ToolRunner().RunAsync([.. input, "--no-default-env"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("""URLS = "http://+:8080""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""[effective]  environment variable ASPNETCORE_URLS = "http://+:8080""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""Db:Timeout = "30""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""[overridden] environment variable DOTNET_Db__Timeout = "5""", result.Output, StringComparison.Ordinal);

            Assert.Equal(1, withoutDefaults.ExitCode);
            Assert.Contains("urls is not set by any configuration provider.", withoutDefaults.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("DOTNET_Db__Timeout", withoutDefaults.Output, StringComparison.Ordinal);
        }
        finally
        {
            app.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Loads_config_files_after_the_environment_variables_and_the_command_line()
    {
        var app = Directory.CreateTempSubdirectory("configferret-app-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Join(app.FullName, "appsettings.json"), """{ "Db": { "Timeout": 30, "Port": 5432 } }""", ct);
            var extra = Path.Join(app.FullName, "extra.json");
            await File.WriteAllTextAsync(extra, """{ "Db": { "Timeout": 99, "Port": 6543 } }""", ct);

            var result = await new ToolRunner().RunAsync(
                "trace", "-a", app.FullName, "--no-user-secrets", "--config", extra, "--env", "Db__Timeout=5", "Db:Timeout", "Db:Port", "--", "--Db:Port=7000");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("""Db:Timeout = "99""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""[overridden] environment variable Db__Timeout = "5""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""Db:Port = "6543""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""[overridden] command-line argument --Db:Port = "7000""", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            app.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Loads_config_files_right_after_the_settings_files_with_config_before_env()
    {
        var app = Directory.CreateTempSubdirectory("configferret-app-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Join(app.FullName, "appsettings.json"), """{ "Db": { "Timeout": 30, "Port": 5432, "Host": "localhost" } }""", ct);
            var extra = Path.Join(app.FullName, "extra.json");
            await File.WriteAllTextAsync(extra, """{ "Db": { "Timeout": 99, "Port": 6543, "Host": "db.extra" } }""", ct);

            var result = await new ToolRunner().RunAsync(
                "trace", "-a", app.FullName, "--no-user-secrets", "--config", extra, "--config-before-env", "--env", "Db__Timeout=5",
                "Db:Timeout", "Db:Port", "Db:Host", "--", "--Db:Port=7000");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("""Db:Timeout = "5""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""[overridden] extra.json (line 1) = "99""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""Db:Port = "7000""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""[overridden] extra.json (line 1) = "6543""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""[effective]  extra.json (line 1) = "db.extra""", result.Output, StringComparison.Ordinal);
            Assert.Contains("""[overridden] appsettings.json (line 1) = "localhost""", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            app.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Reads_a_settings_file_that_links_outside_the_app_folder_once_when_passed_with_config()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link on Windows needs rights the tests may not have.");

        var root = Directory.CreateTempSubdirectory("configferret-tool-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var app = Directory.CreateDirectory(Path.Join(root.FullName, "app")).FullName;
            await File.WriteAllTextAsync(Path.Join(app, "appsettings.json"), """{ "Db": { "Host": "localhost" } }""", ct);
            var outside = Directory.CreateDirectory(Path.Join(root.FullName, "mounted")).FullName;
            var target = Path.Join(outside, "production.json");
            await File.WriteAllTextAsync(target, """{ "Db": { "Host": "db.prod" } }""", ct);
            File.CreateSymbolicLink(Path.Join(app, "appsettings.Production.json"), target);

            var refused = await new ToolRunner().RunAsync("trace", "-a", app, "--no-user-secrets", "Db:Host");
            var passed = await new ToolRunner().RunAsync("trace", "-a", app, "--no-user-secrets", "--config", target, "Db:Host");

            Assert.Equal(2, refused.ExitCode);
            Assert.Contains("outside the app folder", refused.Error, StringComparison.Ordinal);

            Assert.Equal(0, passed.ExitCode);
            Assert.Contains("""Db:Host = "db.prod""", passed.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("appsettings.Production.json", passed.Output, StringComparison.Ordinal);
            Assert.Single(passed.Output.Split('\n'), line => line.Contains("production.json", StringComparison.Ordinal) && line.Contains('['));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Traces_a_Kustomize_overlay()
    {
        var dir = ToolRunner.Golden("kustomize-overlay");
        var result = await new ToolRunner().RunAsync(
            "trace", "-a", Path.Join(dir, "app"), "-m", Path.Join(dir, "kustomize-build.yaml"),
            "-k", Path.Join(dir, "kustomize", "overlays", "staging"), "--env-prefix", "BILLING_", "Billing:RetryCount");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("""Billing:RetryCount = "10""", result.Output, StringComparison.Ordinal);
        Assert.Contains("kustomize", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reads_manifests_from_standard_input()
    {
        var dir = ToolRunner.Golden("helm-release");
        var runner = new ToolRunner { StandardInput = await File.ReadAllTextAsync(Path.Join(dir, "rendered.yaml"), TestContext.Current.CancellationToken) };

        var result = await runner.RunAsync("trace", "-a", Path.Join(dir, "app"), "-m", "-", "-w", "orders", "-c", "orders", "--env-prefix", "ORDERS_", "Db:Host");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("""Db:Host = "orders-db.prod.svc""", result.Output, StringComparison.Ordinal);
        Assert.Contains("(stdin:", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_the_report_to_a_file_with_output()
    {
        var file = Path.Join(Path.GetTempPath(), $"configferret-{Guid.NewGuid():N}.json");
        try
        {
            var result = await new ToolRunner().RunAsync(HelmRelease("Db:Timeout", "--format", "json", "--output", file));

            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Output);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
            Assert.Equal("60", json.RootElement.GetProperty("settings")[0].GetProperty("value").GetString());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Picks_the_environment_file_from_the_apps_variables()
    {
        var dir = ToolRunner.Golden("local-dev");

        var production = await new ToolRunner().RunAsync("trace", "-a", dir, "--no-user-secrets", "--format", "json");
        var development = await new ToolRunner().RunAsync("trace", "-a", dir, "--no-user-secrets", "--env", "DOTNET_ENVIRONMENT=Development", "--format", "json");

        Assert.Equal(0, production.ExitCode);
        Assert.Equal(0, development.ExitCode);
        Assert.DoesNotContain("appsettings.Development.json", production.Output, StringComparison.Ordinal);
        Assert.Contains("appsettings.Development.json", development.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Staging", "ASPNETCORE_ENVIRONMENT=Staging")]
    [InlineData("Development", "ASPNETCORE_ENVIRONMENT=Staging", "DOTNET_ENVIRONMENT=Development")]
    [InlineData("Testing", "ASPNETCORE_ENVIRONMENT=Staging", "DOTNET_ENVIRONMENT=Development", "--", "--environment", "Testing")]
    public async Task Picks_the_environment_as_the_web_host_does(string expected, params string[] input)
    {
        var app = Directory.CreateTempSubdirectory("configferret-app-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Join(app.FullName, "appsettings.json"), """{ "Picked": "Production" }""", ct);
            foreach (var name in new[] { "Staging", "Development", "Testing" })
            {
                await File.WriteAllTextAsync(Path.Join(app.FullName, $"appsettings.{name}.json"), $$"""{ "Picked": "{{name}}" }""", ct);
            }

            var split = Array.IndexOf(input, "--");
            var variables = split < 0 ? input : input[..split];
            var appArgs = split < 0 ? [] : input[split..];
            var result = await new ToolRunner().RunAsync(
                ["trace", "-a", app.FullName, "--no-user-secrets", .. variables.SelectMany(v => new[] { "--env", v }), "Picked", .. appArgs]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains($"""Picked = "{expected}""", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            app.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Leaves_the_value_out_of_the_error_for_an_environment_name_taken_from_an_app_argument()
    {
        var app = Directory.CreateTempSubdirectory("configferret-app-");
        try
        {
            await File.WriteAllTextAsync(Path.Join(app.FullName, "appsettings.json"), """{ "Db": { "Host": "localhost" } }""", TestContext.Current.CancellationToken);

            var result = await new ToolRunner().RunAsync("trace", "-a", app.FullName, "--no-user-secrets", "--", "--environment", "--Jwt:Key=fake/jwt-key");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("The environment name from the app's command line", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("fake/jwt-key", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("fake/jwt-key", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            app.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("--env")]
    [InlineData("--manifests")]
    [InlineData("--environment")]
    [InlineData("--")]
    public async Task Refuses_an_environment_name_that_leaves_the_app_folder(string how)
    {
        var root = Directory.CreateTempSubdirectory("configferret-tool-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var app = Directory.CreateDirectory(Path.Join(root.FullName, "repo", "app")).FullName;
            await File.WriteAllTextAsync(Path.Join(app, "appsettings.json"), """{ "Db": { "Host": "localhost" } }""", ct);
            Directory.CreateDirectory(Path.Join(root.FullName, "outside"));
            await File.WriteAllTextAsync(Path.Join(root.FullName, "outside", "secret.json"), """{ "Outside": { "Region": "fake-outside-value" } }""", ct);
            const string Name = "x/../../../outside/secret";
            var manifest = Path.Join(root.FullName, "repo", "pod.yaml");
            await File.WriteAllTextAsync(manifest, $"apiVersion: v1\nkind: Pod\nmetadata:\n  name: api\nspec:\n  containers:\n    - name: api\n      env:\n        - name: ASPNETCORE_ENVIRONMENT\n          value: \"{Name}\"\n", ct);
            string[] input = how switch
            {
                "--env" => ["--env", "ASPNETCORE_ENVIRONMENT=" + Name],
                "--manifests" => ["--manifests", manifest],
                "--" => ["--", "--environment", Name],
                _ => ["--environment", Name],
            };

            var result = await new ToolRunner().RunAsync(["trace", "-a", app, "--no-user-secrets", .. input]);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("holds a slash or a character a file name can't have", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(Name, result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("Outside", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("fake-outside-value", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Refuses_a_settings_file_that_links_outside_the_app_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        var root = Directory.CreateTempSubdirectory("configferret-tool-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var app = Directory.CreateDirectory(Path.Join(root.FullName, "repo", "app")).FullName;
            await File.WriteAllTextAsync(Path.Join(app, "appsettings.json"), """{ "Db": { "Host": "localhost" } }""", ct);
            Directory.CreateDirectory(Path.Join(root.FullName, "outside", "deep"));
            await File.WriteAllTextAsync(Path.Join(root.FullName, "outside", "secret.json"), """{ "Outside": { "Region": "fake-outside-value" } }""", ct);

            // The link names a file in a folder inside the app folder, but that folder leads out of it.
            Directory.CreateSymbolicLink(Path.Join(app, "config"), Path.Join(root.FullName, "outside", "deep"));
            File.CreateSymbolicLink(Path.Join(app, "appsettings.Production.json"), Path.Join("config", "..", "secret.json"));

            var result = await new ToolRunner().RunAsync("trace", "-a", app, "--no-user-secrets");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("appsettings.Production.json", result.Error, StringComparison.Ordinal);
            Assert.Contains("outside the app folder", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("fake-outside-value", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Reads_a_settings_file_that_links_inside_the_app_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        // The layout of a mounted ConfigMap: the file links to ..data, which links to a folder beside it.
        var app = Directory.CreateTempSubdirectory("configferret-app-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Join(app.FullName, "appsettings.json"), """{ "Db": { "Host": "localhost" } }""", ct);
            Directory.CreateDirectory(Path.Join(app.FullName, "..2026_01_01"));
            await File.WriteAllTextAsync(Path.Join(app.FullName, "..2026_01_01", "appsettings.Production.json"), """{ "Db": { "Host": "db.prod" } }""", ct);
            Directory.CreateSymbolicLink(Path.Join(app.FullName, "..data"), "..2026_01_01");
            File.CreateSymbolicLink(Path.Join(app.FullName, "appsettings.Production.json"), Path.Join("..data", "appsettings.Production.json"));

            var result = await new ToolRunner().RunAsync("trace", "-a", app.FullName, "--no-user-secrets", "Db:Host");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("""Db:Host = "db.prod""", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            app.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Refuses_a_settings_file_over_the_size_limit()
    {
        var app = Directory.CreateTempSubdirectory("configferret-app-");
        try
        {
            using (var file = File.Create(Path.Join(app.FullName, "appsettings.json")))
            {
                file.SetLength(InputReader.MaxFoundFileBytes + 1);
            }

            var result = await new ToolRunner().RunAsync("trace", "-a", app.FullName, "--no-user-secrets");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("appsettings.json", result.Error, StringComparison.Ordinal);
            Assert.Contains("64 MB", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            app.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Refuses_a_linked_settings_file_over_the_size_limit()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        // The link itself is a few bytes; the file it leads to, inside the app folder, is over the limit.
        var app = Directory.CreateTempSubdirectory("configferret-app-");
        try
        {
            await File.WriteAllTextAsync(Path.Join(app.FullName, "appsettings.json"), """{ "Db": { "Host": "localhost" } }""", TestContext.Current.CancellationToken);
            Directory.CreateDirectory(Path.Join(app.FullName, "..data"));
            using (var file = File.Create(Path.Join(app.FullName, "..data", "appsettings.Production.json")))
            {
                file.SetLength(InputReader.MaxFoundFileBytes + 1);
            }

            File.CreateSymbolicLink(Path.Join(app.FullName, "appsettings.Production.json"), Path.Join("..data", "appsettings.Production.json"));

            var result = await new ToolRunner().RunAsync("trace", "-a", app.FullName, "--no-user-secrets");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("appsettings.Production.json", result.Error, StringComparison.Ordinal);
            Assert.Contains("64 MB", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            app.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Reads_user_secrets_in_Development_and_redacts_them()
    {
        var home = Directory.CreateTempSubdirectory("configferret-home-");
        var app = Directory.CreateTempSubdirectory("configferret-app-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Join(app.FullName, "appsettings.json"), """{ "Smtp": { "Password": "placeholder", "Host": "localhost" } }""", ct);
            await File.WriteAllTextAsync(Path.Join(app.FullName, "Api.csproj"), """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><UserSecretsId>api-1234</UserSecretsId></PropertyGroup></Project>""", ct);
            var secrets = Directory.CreateDirectory(Path.Join(home.FullName, ".microsoft", "usersecrets", "api-1234"));
            await File.WriteAllTextAsync(Path.Join(secrets.FullName, "secrets.json"), """{ "Smtp:Password": "hunter2", "Smtp:Host": "smtp.dev" }""", ct);

            var runner = new ToolRunner();
            runner.Environment["HOME"] = home.FullName;
            runner.Environment["APPDATA"] = null;

            var development = await runner.RunAsync("trace", "-a", app.FullName, "-e", "Development");
            var production = await runner.RunAsync("trace", "-a", app.FullName);

            Assert.Equal(0, development.ExitCode);
            Assert.Contains("user secrets (secrets.json)", development.Output, StringComparison.Ordinal);
            Assert.Contains("""Smtp:Host = "***""", development.Output, StringComparison.Ordinal);

            Assert.DoesNotContain("hunter2", development.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("user secrets", production.Output, StringComparison.Ordinal);
            Assert.Contains("""Smtp:Host = "localhost""", production.Output, StringComparison.Ordinal);
        }
        finally
        {
            home.Delete(recursive: true);
            app.Delete(recursive: true);
        }
    }
}
