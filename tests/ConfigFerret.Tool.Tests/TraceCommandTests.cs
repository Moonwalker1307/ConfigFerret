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
    public async Task Passes_arguments_after_double_dash_to_the_app_as_its_command_line()
    {
        var result = await new ToolRunner().RunAsync([.. HelmRelease("Features:Search"), "--", "--Features:Search=false"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("""Features:Search = "false""", result.Output, StringComparison.Ordinal);
        Assert.Contains("[effective]  command-line argument --Features:Search", result.Output, StringComparison.Ordinal);
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
