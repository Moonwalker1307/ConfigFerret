using System.Text.Json;
using ConfigFerret.Tests.Shared;

namespace ConfigFerret.Tool.Tests;

public class UnusedCommandTests
{
    private static string[] HelmRelease(params string[] extra)
    {
        var dir = ToolRunner.Golden("helm-release");
        return
        [
            "unused",
            "--app", Path.Join(dir, "app"),
            "--manifests", Path.Join(dir, "rendered.yaml"),
            "-w", "orders", "-c", "orders",
            "--env-prefix", "ORDERS_",
            "--chart", Path.Join(dir, "chart"),
            "-f", Path.Join(dir, "values-prod.yaml"),
            "-f", Path.Join(dir, "values-eu.yaml"),
            .. extra,
        ];
    }

    [Fact]
    public async Task Lists_values_that_never_win()
    {
        var result = await new ToolRunner().RunAsync(HelmRelease());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Values that never win", result.Output, StringComparison.Ordinal);
        Assert.Contains("overridden by environment variable ORDERS_Db__Timeout", result.Output, StringComparison.Ordinal);
        Assert.Contains("Keys the app did not read: not checked.", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Db:Host\nDb:Port\n# a comment\n\nDb:Timeout\n")]
    [InlineData("""["Db:Host", "Db:Port", "Db:Timeout"]""")]
    public async Task Lists_keys_missing_from_the_reads_file(string reads)
    {
        var file = Path.Join(Path.GetTempPath(), $"configferret-reads-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(file, reads, TestContext.Current.CancellationToken);

            var result = await new ToolRunner().RunAsync(HelmRelease("--reads", file, "--format", "json"));

            Assert.Equal(0, result.ExitCode);
            using var json = JsonDocument.Parse(result.Output);
            var text = json.RootElement.ToString();
            Assert.Contains("Api:BaseUrl", text, StringComparison.Ordinal);
            Assert.Contains("Cache:Ttl", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Exits_with_1_on_findings_only_with_exit_code()
    {
        Assert.Equal(0, (await new ToolRunner().RunAsync(HelmRelease())).ExitCode);
        Assert.Equal(1, (await new ToolRunner().RunAsync(HelmRelease("--exit-code"))).ExitCode);
    }
}

public class ContainerCommandTests
{
    private static string[] Container(params string[] extra)
    {
        var dir = ToolRunner.Golden("container");
        return
        [
            "container",
            "-m", Path.Join(dir, "cluster.yaml"),
            "-w", "api-7d9f8c-x2kqp",
            "-c", "api",
            "--chart", Path.Join(dir, "helm"),
            "-f", Path.Join(dir, "helm", "release-values.yaml"),
            .. extra,
        ];
    }

    [Fact]
    public async Task Traces_environment_variables_and_mounted_files_to_the_Helm_release()
    {
        var result = await new ToolRunner().RunAsync(Container());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ConfigMap api-config (key DB__Timeout, cluster.yaml:10)", result.Output, StringComparison.Ordinal);
        Assert.Contains("""[effective]  release-values.yaml (line 3, db.timeout) = "60""", result.Output, StringComparison.Ordinal);
        Assert.Contains("/app/", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explains_one_variable_as_json_with_secrets_redacted()
    {
        var result = await new ToolRunner().RunAsync(Container("DB__Password", "--format", "json"));

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var setting = Assert.Single(json.RootElement.GetProperty("settings").EnumerateArray());
        Assert.Equal("DB__Password", setting.GetProperty("key").GetString());
        Assert.Equal("***", setting.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Matches_variable_names_case_sensitively()
    {
        var result = await new ToolRunner().RunAsync(Container("db__timeout"));

        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Needs_manifests()
    {
        var result = await new ToolRunner().RunAsync("container");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--manifests", result.Error, StringComparison.Ordinal);
    }
}

public class InputErrorTests
{
    [Fact]
    public async Task Points_to_appsettings_files_below_a_folder_without_one()
    {
        var result = await new ToolRunner().RunAsync("trace", "-a", ToolRunner.Golden("helm-release"));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("No appsettings.json in", result.Error, StringComparison.Ordinal);
        Assert.Contains(Path.Join("app", "appsettings.json"), result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("trace", "--values", "values.yaml")]
    [InlineData("trace", "--env", "NOEQUALS")]
    [InlineData("trace", "--workload", "api")]
    [InlineData("trace", "--github-environment", "production")]
    [InlineData("trace", "--octopus-environment", "Production")]
    [InlineData("trace", "--manifests", "does-not-exist.yaml")]
    [InlineData("trace", "--chart", "does-not-exist")]
    [InlineData("trace", "--no-such-option")]
    [InlineData("frobnicate")]
    public async Task Reports_bad_input_with_exit_code_2_and_no_stack_trace(params string[] args)
    {
        var result = await new ToolRunner().RunAsync([.. args, "-a", ToolRunner.Golden("local-dev")]);

        Assert.Equal(2, result.ExitCode);
        Assert.NotEmpty(result.Error);
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_a_user_secrets_id_that_leaves_the_secrets_folder()
    {
        var result = await new ToolRunner().RunAsync("trace", "-a", ToolRunner.Golden("local-dev"), "--user-secrets-id", "..");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("not a valid UserSecretsId", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prints_help()
    {
        var result = await new ToolRunner().RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("trace", result.Output, StringComparison.Ordinal);
        Assert.Contains("unused", result.Output, StringComparison.Ordinal);
        Assert.Contains("container", result.Output, StringComparison.Ordinal);
    }
}

public class RemoteSourceTests
{
    private const string GitHubToken = "ghp_cli_test_token_never_printed";
    private const string OctopusKey = "API-CLITESTKEYNEVERPRINTED";

    private static string LocalDev => ToolRunner.Golden("local-dev");

    [Fact]
    public async Task GitHub_needs_a_token_from_the_environment()
    {
        var result = await new ToolRunner().RunAsync("trace", "-a", LocalDev, "--github-repo", "contoso/api");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("GITHUB_TOKEN", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_rejects_a_repository_without_an_owner()
    {
        var runner = new ToolRunner();
        runner.Environment["GITHUB_TOKEN"] = GitHubToken;

        var result = await runner.RunAsync("trace", "-a", LocalDev, "--github-repo", "api");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("owner/name", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Links_values_to_GitHub_variables_and_keeps_the_token_out_of_the_report()
    {
        const string repo = "/repos/contoso/api";
        var handler = new FakeHttpHandler()
            .Respond($"{repo}/actions/organization-variables?per_page=30&page=1", """{"total_count":0,"variables":[]}""")
            .Respond($"{repo}/actions/organization-secrets?per_page=100&page=1", """{"total_count":0,"secrets":[]}""")
            .Respond($"{repo}/actions/variables?per_page=30&page=1", """{"total_count":1,"variables":[{"name":"DEVAPP_Paging__PageSize","value":"10","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}]}""")
            .Respond($"{repo}/actions/secrets?per_page=100&page=1", """{"total_count":0,"secrets":[]}""");
        var runner = new ToolRunner { Http = handler };
        runner.Environment["GH_TOKEN"] = GitHubToken;

        var result = await runner.RunAsync(
            "trace", "-a", LocalDev, "--no-user-secrets", "--env-prefix", "DEVAPP_", "--env", "DEVAPP_Paging__PageSize=10",
            "--github-repo", "contoso/api", "--format", "json", "Paging:PageSize");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("GitHubActions", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, result.Error, StringComparison.Ordinal);
        Assert.All(handler.Requests, r => Assert.Equal(GitHubToken, r.Headers.Authorization?.Parameter));
    }

    [Fact]
    public async Task Octopus_needs_a_server_and_an_api_key_from_the_environment()
    {
        var noServer = await new ToolRunner().RunAsync("trace", "-a", LocalDev, "--octopus-project", "api");
        Assert.Equal(2, noServer.ExitCode);
        Assert.Contains("OCTOPUS_URL", noServer.Error, StringComparison.Ordinal);

        var runner = new ToolRunner();
        runner.Environment["OCTOPUS_URL"] = "https://octopus.example.com";
        var noKey = await runner.RunAsync("trace", "-a", LocalDev, "--octopus-project", "api");
        Assert.Equal(2, noKey.ExitCode);
        Assert.Contains("OCTOPUS_API_KEY", noKey.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Links_values_to_Octopus_variables_scoped_to_the_environment()
    {
        var handler = new FakeHttpHandler()
            .Respond("/api/spaces/all", """[{"Id":"Spaces-1","Name":"Default","IsDefault":true}]""")
            .Respond("/api/Spaces-1/projects/all", """[{"Id":"Projects-7","Name":"Orders","Slug":"orders","VariableSetId":"variableset-Projects-7","IncludedLibraryVariableSetIds":[]}]""")
            .Respond("/api/Spaces-1/variables/variableset-Projects-7", """
                {"Id":"variableset-Projects-7","Variables":[
                  {"Id":"1","Name":"Paging:PageSize","Value":"25","Type":"String","IsSensitive":false,"Scope":{"Environment":["Environments-2"]}},
                  {"Id":"2","Name":"Paging:PageSize","Value":"10","Type":"String","IsSensitive":false,"Scope":{}}],
                 "ScopeValues":{"Environments":[{"Id":"Environments-1","Name":"Staging"},{"Id":"Environments-2","Name":"Production"}]}}
                """);
        var runner = new ToolRunner { Http = handler };
        runner.Environment["OCTOPUS_API_KEY"] = OctopusKey;

        var result = await runner.RunAsync(
            "trace", "-a", LocalDev, "--no-user-secrets", "--octopus-server", "https://octopus.example.com",
            "--octopus-project", "orders", "--octopus-environment", "Staging", "Paging:PageSize");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Octopus", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(OctopusKey, result.Output, StringComparison.Ordinal);
        Assert.All(handler.Requests, r => Assert.Equal(OctopusKey, r.Headers.GetValues("X-Octopus-ApiKey").Single()));
    }
}
