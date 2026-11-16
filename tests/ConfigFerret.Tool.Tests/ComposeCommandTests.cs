using System.Text.Json;

namespace ConfigFerret.Tool.Tests;

public class ComposeCommandTests
{
    private static string[] Compose(params string[] extra)
    {
        var dir = ToolRunner.Golden("compose");
        return
        [
            "container",
            "--compose-file", Path.Join(dir, "compose.yaml"),
            "--compose-file", Path.Join(dir, "compose.override.yaml"),
            "--compose-env-file", Path.Join(dir, "project.envfile"),
            .. extra,
        ];
    }

    [Fact]
    public async Task Traces_a_compose_service_back_to_the_env_file()
    {
        var result = await new ToolRunner().RunAsync(Compose("--compose-service", "api"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[effective]  compose service api env Db__Host (compose.yaml:21) = \"postgres.internal\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("<- [effective]  project.envfile (line 3) = \"postgres.internal\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("/run/secrets/db_password", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("pr0d-s3cret", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explains_one_variable_as_json_with_secrets_redacted()
    {
        var result = await new ToolRunner().RunAsync(Compose("--compose-service", "api", "Db__Password", "--format", "json"));

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var setting = Assert.Single(json.RootElement.GetProperty("settings").EnumerateArray());
        Assert.Equal("***", setting.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Never_reads_the_tools_own_environment()
    {
        var runner = new ToolRunner();
        runner.Environment["ENVIRONMENT"] = "Leaked";

        var result = await runner.RunAsync(Compose("--compose-service", "worker", "--format", "json"));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("Leaked", result.Output, StringComparison.Ordinal);
        Assert.Contains("TZ", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warns_when_it_picks_a_service()
    {
        var result = await new ToolRunner().RunAsync(Compose());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("The compose project has 2 services; using api. Pass a service name to pick another.", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--compose-service", "api")]
    [InlineData("--compose-env-file", "x.envfile")]
    public async Task Compose_options_need_a_compose_file(string option, string value)
    {
        var dir = ToolRunner.Golden("container");
        var result = await new ToolRunner().RunAsync("container", "-m", Path.Join(dir, "cluster.yaml"), option, value);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--compose-service and --compose-env-file need --compose-file.", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-m")]
    [InlineData("-w")]
    [InlineData("-c")]
    public async Task Compose_files_and_manifests_do_not_mix(string option)
    {
        var result = await new ToolRunner().RunAsync(Compose(option, Path.Join(ToolRunner.Golden("container"), "cluster.yaml")));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--compose-file can't be combined with --manifests, --workload or --container.", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_a_missing_compose_file()
    {
        var result = await new ToolRunner().RunAsync("container", "--compose-file", "no-such-compose.yaml");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("File not found: no-such-compose.yaml", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Escapes_control_characters_in_the_message_about_a_missing_file()
    {
        var result = await new ToolRunner().RunAsync("container", "--compose-file", "no\nsuch\u001b[2K.yaml");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("configferret: File not found: no\\nsuch\\u001B[2K.yaml", result.Error.TrimEnd());
    }

    [Fact]
    public async Task Reports_an_invalid_compose_file()
    {
        var file = Path.Join(Path.GetTempPath(), $"configferret-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(file, "services: [x]\n", TestContext.Current.CancellationToken);
        try
        {
            var result = await new ToolRunner().RunAsync("container", "--compose-file", file);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("services must be a mapping", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
