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
    public async Task Does_not_follow_links_in_a_manifest_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        var root = Directory.CreateTempSubdirectory("configferret-tool-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var k8s = Directory.CreateDirectory(Path.Join(root.FullName, "k8s")).FullName;
            var outside = Directory.CreateDirectory(Path.Join(root.FullName, "outside")).FullName;
            await File.WriteAllTextAsync(Path.Join(k8s, "pod.yaml"), "apiVersion: v1\nkind: Pod\nmetadata: {name: api}\nspec:\n  containers:\n    - name: api\n      env: [{name: A, value: inside-value}]\n", ct);
            await File.WriteAllTextAsync(Path.Join(outside, "pod.yaml"), "apiVersion: v1\nkind: Pod\nmetadata: {name: other}\nspec:\n  containers:\n    - name: other\n      env: [{name: B, value: fake-outside-value}]\n", ct);
            Directory.CreateSymbolicLink(Path.Join(k8s, "loop"), ".");
            Directory.CreateSymbolicLink(Path.Join(k8s, "extra"), outside);
            File.CreateSymbolicLink(Path.Join(k8s, "linked.yaml"), Path.Join(outside, "pod.yaml"));

            var inside = await new ToolRunner().RunAsync("container", "-m", k8s, "-w", "api");
            var other = await new ToolRunner().RunAsync("container", "-m", k8s, "-w", "other");

            Assert.Equal(0, inside.ExitCode);
            Assert.Contains("inside-value", inside.Output, StringComparison.Ordinal);
            Assert.Contains("No Kubernetes workload named 'other'", other.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("fake-outside-value", other.Output, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Names_a_manifest_in_a_folder_by_its_path_inside_it()
    {
        var root = Directory.CreateTempSubdirectory("configferret-tool-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            Directory.CreateDirectory(Path.Join(root.FullName, "api"));
            Directory.CreateDirectory(Path.Join(root.FullName, "worker"));
            await File.WriteAllTextAsync(Path.Join(root.FullName, "pod.yaml"), "apiVersion: v1\nkind: Pod\nmetadata: {name: api}\nspec:\n  containers:\n    - name: api\n      envFrom: [{configMapRef: {name: api-config}}, {configMapRef: {name: worker-config}}]\n", ct);
            await File.WriteAllTextAsync(Path.Join(root.FullName, "api", "configmap.yaml"), "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: api-config}\ndata: {Api__Host: api.internal}\n", ct);
            await File.WriteAllTextAsync(Path.Join(root.FullName, "worker", "configmap.yaml"), "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: worker-config}\ndata: {Worker__Host: worker.internal}\n", ct);

            var folder = await new ToolRunner().RunAsync("container", "-m", root.FullName);
            var files = await new ToolRunner().RunAsync("container", "-m", Path.Join(root.FullName, "pod.yaml"), "-m", Path.Join(root.FullName, "api", "configmap.yaml"));

            Assert.Equal(0, folder.ExitCode);
            Assert.Contains("ConfigMap api-config (key Api__Host, api/configmap.yaml:4)", folder.Output, StringComparison.Ordinal);
            Assert.Contains("ConfigMap worker-config (key Worker__Host, worker/configmap.yaml:4)", folder.Output, StringComparison.Ordinal);
            Assert.Contains("ConfigMap api-config (key Api__Host, configmap.yaml:4)", files.Output, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
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

    [Theory]
    [InlineData("""{ "Db": { "Timeout": 30, "timeout": 31 } }""", "duplicate key 'Db:timeout'", "")]
    [InlineData("[1,2]", "Top-level JSON element must be an object", "")]
    [InlineData("{ \"Db\": {\n  \"Password\": @fake-secret-value\n} }", "Could not parse the JSON file", "line 2")]
    public async Task Reports_a_malformed_settings_file_in_one_line_without_its_content(string json, string reason, string position)
    {
        var directory = Directory.CreateTempSubdirectory("configferret-malformed-").FullName;
        try
        {
            var file = Path.Join(directory, "appsettings.json");
            await File.WriteAllTextAsync(file, json, TestContext.Current.CancellationToken);

            var result = await new ToolRunner().RunAsync("trace", "-a", directory);

            Assert.Equal(2, result.ExitCode);
            Assert.Equal(string.Empty, result.Output);
            Assert.Single(result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains("appsettings.json", result.Error, StringComparison.Ordinal);
            Assert.Contains(reason, result.Error, StringComparison.Ordinal);
            Assert.Contains(position, result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("@", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("fake-secret-value", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Reports_a_malformed_config_file_in_one_line()
    {
        var directory = Directory.CreateTempSubdirectory("configferret-malformed-").FullName;
        try
        {
            var file = Path.Join(directory, "extra.json");
            await File.WriteAllTextAsync(file, """{ "Db": { "Password": } }""", TestContext.Current.CancellationToken);

            var result = await new ToolRunner().RunAsync("trace", "-a", ToolRunner.Golden("local-dev"), "--config", file);

            Assert.Equal(2, result.ExitCode);
            Assert.Single(result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains("extra.json", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("octopus")]
    [InlineData("github")]
    public async Task Reports_a_request_timeout_in_one_line_with_exit_code_2(string service)
    {
        using var handler = new NeverAnswers();
        var (host, output, error) = RemoteHost(() => new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMilliseconds(200) });

        var code = await Cli.RunAsync(RemoteArgs(service), host, TestContext.Current.CancellationToken);

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Single(error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("configferret: ", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ends_quietly_with_exit_code_130_when_the_user_cancels()
    {
        using var handler = new NeverAnswers();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var (host, output, error) = RemoteHost(() => new HttpClient(handler, disposeHandler: false));

        var run = Cli.RunAsync(RemoteArgs("octopus"), host, cancel.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        var code = await run;

        Assert.Equal(130, code);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    // Ctrl+C reaches the action through a token that System.CommandLine owns, not through the one RunAsync is given.
    [Fact]
    public async Task Ends_quietly_with_exit_code_130_when_a_request_is_cancelled_by_the_console()
    {
        using var handler = new NeverAnswers { Cancelled = new TaskCanceledException("A task was canceled.") };
        var (host, output, error) = RemoteHost(() => new HttpClient(handler, disposeHandler: false));

        var code = await Cli.RunAsync(RemoteArgs("github"), host, TestContext.Current.CancellationToken);

        Assert.Equal(130, code);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    private const string FakeSecret = "fake-token-never-printed";

    private static (CliHost Host, StringWriter Output, StringWriter Error) RemoteHost(Func<HttpClient> client)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var host = new CliHost
        {
            Out = output,
            Error = error,
            Environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["OCTOPUS_API_KEY"] = FakeSecret, ["GITHUB_TOKEN"] = FakeSecret },
            HttpClientFactory = client,
        };
        return (host, output, error);
    }

    private static string[] RemoteArgs(string service) => service == "octopus"
        ? ["trace", "-a", ToolRunner.Golden("local-dev"), "--no-user-secrets", "--octopus-server", "https://octopus.example.com", "--octopus-project", "api"]
        : ["trace", "-a", ToolRunner.Golden("local-dev"), "--no-user-secrets", "--github-repo", "contoso/api"];

    // A server that accepts the request and never answers, so only a timeout or a cancellation ends it.
    private sealed class NeverAnswers : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? Cancelled { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            if (Cancelled is not null)
            {
                throw Cancelled;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable: the delay only ends by cancellation.");
        }
    }

    [Fact]
    public async Task Refuses_a_user_secrets_id_that_leaves_the_secrets_folder()
    {
        var result = await new ToolRunner().RunAsync("trace", "-a", ToolRunner.Golden("local-dev"), "--user-secrets-id", "..");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("not a valid UserSecretsId", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_a_file_in_a_manifest_folder_over_the_size_limit()
    {
        var folder = Directory.CreateTempSubdirectory("configferret-manifests-");
        try
        {
            await File.WriteAllTextAsync(Path.Join(folder.FullName, "pod.yaml"), "apiVersion: v1\nkind: Pod\nmetadata: {name: api}\nspec:\n  containers: [{name: api}]\n", TestContext.Current.CancellationToken);
            using (var file = File.Create(Path.Join(folder.FullName, "big.yaml")))
            {
                file.SetLength(InputReader.MaxFoundFileBytes + 1);
            }

            var result = await new ToolRunner().RunAsync("container", "-m", folder.FullName, "-w", "api");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("big.yaml", result.Error, StringComparison.Ordinal);
            Assert.Contains("64 MB", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
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
