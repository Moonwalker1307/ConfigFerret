using System.Runtime.Versioning;
using ConfigFerret.Kubernetes;
using ConfigFerret.Sources;
using ConfigFerret.Terraform;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tests;

public sealed class TerraformTests : IDisposable
{
    private static readonly string Dir = SourceGoldenTests.Fixture("terraform");
    private static readonly string OutputsFile = Path.Join(Dir, "outputs.json");
    private static readonly string StateFile = Path.Join(Dir, "infra", "terraform.tfstate");
    private static readonly string Folder = Path.Join(Dir, "infra");

    private readonly string _root = Directory.CreateTempSubdirectory("cf-terraform-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ProvenanceStep Env(string name, string? value = null) =>
        new() { Kind = LayerKind.DotNetProvider, Source = $"environment variable {name}", Value = value, Reference = new EnvironmentVariableRef(name) };

    [Fact]
    public void Parses_outputs_and_splits_objects_and_lists_into_nested_values()
    {
        var outputs = TerraformOutputs.Parse("""
            {
              "s": {"value": "text", "type": "string"},
              "n": {"value": 4.50, "type": "number", "sensitive": false},
              "b": {"value": true},
              "f": {"value": false},
              "z": {"value": null},
              "secret": {"value": {"user": "u", "pass": "p"}, "sensitive": true, "type": ["object", {"user": "string"}]},
              "list": {"value": [1, {"k": "v"}], "type": ["tuple", ["number", "object"]]},
              "empty": {"value": {}},
              "broken": {"type": "string"},
              "odd": 5
            }
            """, "out.json");

        Assert.Equal(
            ["s", "n", "b", "f", "z", "secret", "secret.user", "secret.pass", "list", "list.0", "list.1", "list.1.k", "empty"],
            outputs.Values.Select(v => v.Name));
        Assert.Equal(
            ["text", "4.50", "true", "false", null, """{"user":"u","pass":"p"}""", "u", "p", """[1,{"k":"v"}]""", "1", """{"k":"v"}""", "v", "{}"],
            outputs.Values.Select(v => v.Value));
        Assert.Equal(
            [false, false, false, false, false, true, true, true, false, false, false, false, false],
            outputs.Values.Select(v => v.IsSensitive));
        Assert.Equal(
            [false, false, false, false, false, true, false, false, true, false, true, false, false],
            outputs.Values.Select(v => v.HasNestedValues));
        Assert.Equal("string", outputs.Values[0].Type);
        Assert.Equal("""["object",{"user":"string"}]""", outputs.Values[5].Type);
        Assert.Null(outputs.Values[6].Type);
        Assert.Null(outputs.Values[2].Type);
        Assert.Equal(("secret", "user"), (outputs.Values[6].Output, outputs.Values[6].Path));
        Assert.Null(outputs.Values[0].Path);
        Assert.All(outputs.Values, v => Assert.Equal("out.json", v.Source));
        Assert.Equal(["Terraform output broken in out.json has no value.", "Terraform output odd in out.json has no value."], outputs.Warnings);
        Assert.Empty(outputs.Definitions);
    }

    [Fact]
    public void A_sensitive_output_leaves_its_value_out_of_to_string()
    {
        var outputs = TerraformOutputs.Parse("""{"pw": {"value": "hunter2", "sensitive": true}, "host": {"value": "db"}, "none": {"value": null}}""");
        Assert.Equal("Terraform output pw = (sensitive)", outputs.Values[0].ToString());
        Assert.Equal("Terraform output host = db", outputs.Values[1].ToString());
        Assert.Equal("Terraform output none = null", outputs.Values[2].ToString());
    }

    [Theory]
    [InlineData("[]", "Terraform outputs in x.json must be a JSON object of outputs.")]
    [InlineData("\"text\"", "Terraform outputs in x.json must be a JSON object of outputs.")]
    public void Rejects_outputs_that_are_not_an_object(string json, string message)
    {
        Assert.Equal(message, Assert.Throws<FormatException>(() => TerraformOutputs.Parse(json, "x.json")).Message);
    }

    [Fact]
    public void Rejects_invalid_json_and_arguments()
    {
        var ex = Assert.Throws<FormatException>(() => TerraformOutputs.Parse("{", "x.json"));
        Assert.StartsWith("Could not parse Terraform outputs in x.json: ", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => TerraformOutputs.Parse(null!));
        Assert.Throws<ArgumentException>(() => TerraformOutputs.Parse("{}", " "));
        Assert.Throws<ArgumentNullException>(() => TerraformOutputs.ParseState(null!));
        Assert.Throws<ArgumentException>(() => TerraformOutputs.ParseState("{}", " "));
        Assert.Equal("terraform output -json", TerraformOutputs.Parse("""{"a": {"value": 1}}""").Values[0].Source);
        Assert.Equal("terraform.tfstate", TerraformOutputs.ParseState("""{"version": 4, "outputs": {"a": {"value": 1}}}""").Values[0].Source);
    }

    [Fact]
    public void Reads_only_the_outputs_of_a_state_file()
    {
        var outputs = TerraformOutputs.ParseState(File.ReadAllText(StateFile), "terraform.tfstate");

        Assert.Equal(["db_host", "db_port"], outputs.Values.Select(v => v.Name));
        Assert.Equal(["stale-host-from-state", "5432"], outputs.Values.Select(v => v.Value));
        Assert.DoesNotContain(outputs.Values, v => v.Value?.Contains("resource-attribute", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Reads_state_outputs_wherever_they_are_and_ignores_the_rest()
    {
        var outputs = TerraformOutputs.ParseState("""
            {
              // comments are skipped
              "resources": [{"instances": [{"attributes": {"password": "never"}}]}],
              "version": 4,
              "outputs": {"a": {"value": "1"}},
              "outputs": {"b": {"value": "2"}},
              "nested": {"version": 3, "outputs": {"c": {"value": 3}}},
              "check_results": null
            }
            """);
        Assert.Equal(["a"], outputs.Values.Select(v => v.Name));

        Assert.Empty(TerraformOutputs.ParseState("""{"version": 4}""").Values);
        Assert.Empty(TerraformOutputs.ParseState("""{"version": 4, "outputs": []}""").Values);
        Assert.Empty(TerraformOutputs.ParseState("""{"outputs": "x", "version": 4}""").Values);
    }

    [Theory]
    [InlineData("""{"version": 3, "outputs": {}}""", "The Terraform state s has format version 3; only version 4 is read.")]
    [InlineData("""{"outputs": {}}""", "The Terraform state s has format version (none); only version 4 is read.")]
    [InlineData("""{"version": "4"}""", "The Terraform state s has format version (none); only version 4 is read.")]
    [InlineData("""{"version": 4.5}""", "The Terraform state s has format version (none); only version 4 is read.")]
    [InlineData("[]", "The Terraform state s is not a JSON object.")]
    [InlineData("", "The Terraform state s is not a JSON object.")]
    public void Rejects_state_files_it_cannot_read(string json, string message)
    {
        var ex = Assert.ThrowsAny<FormatException>(() => TerraformOutputs.ParseState(json, "s"));
        if (json.Length == 0)
        {
            Assert.StartsWith("Could not parse the Terraform state s: ", ex.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(message, ex.Message);
        }
    }

    [Fact]
    public void Invalid_state_json_is_a_format_exception()
    {
        var ex = Assert.Throws<FormatException>(() => TerraformOutputs.ParseState("""{"version": 4, "outputs": {"a": """, "s"));
        Assert.StartsWith("Could not parse the Terraform state s: ", ex.Message, StringComparison.Ordinal);
        Assert.IsType<System.Text.Json.JsonException>(ex.InnerException, exactMatch: false);
    }

    [Fact]
    public void Load_merges_outputs_over_the_state_and_reads_definitions()
    {
        var outputs = TerraformOutputs.Load(OutputsFile, StateFile, Folder);

        Assert.Equal(
            ["cache", "cache.endpoint", "cache.ttl", "db_host", "db_password", "queue_urls", "queue_urls.0", "queue_urls.1", "unused_bucket", "db_port"],
            outputs.Values.Select(v => v.Name));
        Assert.Equal("orders-db.abc123.eu-west-1.rds.example.test", outputs.Find("db_host")!.Value);
        Assert.Equal("outputs.json", outputs.Find("db_host")!.Source);
        Assert.Equal("terraform.tfstate", outputs.Find("db_port")!.Source);
        Assert.Equal(new TerraformDefinition("db_host", "outputs.tf", 1, "aws_db_instance.orders.address"), outputs.Definitions["db_host"]);
        Assert.Equal(new TerraformDefinition("cache", "outputs.tf", 15, null), outputs.Definitions["cache"]);
        Assert.Equal("random_password.db.result", outputs.Definitions["db_password"].Expression);
        Assert.Equal("[aws_sqs_queue.orders.url, aws_sqs_queue.dlq.url]", outputs.Definitions["queue_urls"].Expression);
        Assert.Equal(5, outputs.Definitions.Count);
        Assert.Empty(outputs.Warnings);
    }

    [Fact]
    public void Load_reads_the_state_in_the_folder_when_no_file_is_given()
    {
        var outputs = TerraformOutputs.Load(terraformFolder: Folder);
        Assert.Equal(["db_host", "db_port"], outputs.Values.Select(v => v.Name));
        Assert.Equal(5, outputs.Definitions.Count);

        var onlyOutputs = TerraformOutputs.Load(OutputsFile, terraformFolder: _root);
        Assert.Empty(onlyOutputs.Definitions);
        Assert.Equal(9, onlyOutputs.Values.Count);
    }

    [Fact]
    public void Load_explains_what_is_missing()
    {
        Assert.Equal(
            $"{_root} has no terraform.tfstate; pass the output of terraform output -json or a state file. (Parameter 'terraformFolder')",
            Assert.Throws<ArgumentException>(() => TerraformOutputs.Load(terraformFolder: _root)).Message);
        Assert.Equal(
            "Pass the output of terraform output -json, a state file, or a Terraform folder. (Parameter 'outputsFile')",
            Assert.Throws<ArgumentException>(() => TerraformOutputs.Load()).Message);
        Assert.Throws<ArgumentException>(() => TerraformOutputs.Load(" ", " ", " "));

        var missingFolder = TerraformOutputs.Load(OutputsFile, terraformFolder: Path.Join(_root, "nope"));
        Assert.Equal([$"The Terraform folder {Path.Join(_root, "nope")} does not exist."], missingFolder.Warnings);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void A_tf_file_it_may_not_read_is_a_warning()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses Unix file modes.");
        var locked = Path.Join(_root, "locked.tf");
        File.WriteAllText(locked, "output \"a\" {\n  value = 1\n}\n");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Assert.SkipWhen(CanRead(locked), "The process may read any file, for example as root.");

            var outputs = TerraformOutputs.Load(OutputsFile, terraformFolder: _root);

            Assert.StartsWith($"Could not read {locked}: ", Assert.Single(outputs.Warnings), StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static bool CanRead(string path)
    {
        try
        {
            File.ReadAllText(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void Load_keeps_the_warnings_of_each_file()
    {
        File.WriteAllText(Path.Join(_root, "o.json"), """{"a": {"type": "string"}}""");
        File.WriteAllText(Path.Join(_root, "s.tfstate"), """{"version": 4, "outputs": {"b": {}}}""");

        var outputs = TerraformOutputs.Load(Path.Join(_root, "o.json"), Path.Join(_root, "s.tfstate"));

        Assert.Equal(["Terraform output a in o.json has no value.", "Terraform output b in s.tfstate has no value."], outputs.Warnings);
    }

    [Fact]
    public void Finds_output_blocks_in_terraform_code()
    {
        var outputs = TerraformOutputs.Parse("{}");
        outputs.AddDefinitions("""
            output "a" {
              value = var.a
            }
              output   "b"{
              description = "value = not this"
              sensitive = true
              value = { x = 1 }
            }
            output "c" {
              value = {
                x = 1
              }
            }
            output "d" {
              nested {
                value = inner
              }
              value = outer
            }
            output "a" {
              value = later
            }
            output "e" {}
            # output "f" { value = 1 }
            """, "outputs.tf");

        Assert.Equal(
            [
                new TerraformDefinition("a", "outputs.tf", 1, "var.a"),
                new TerraformDefinition("b", "outputs.tf", 4, "{ x = 1 }"),
                new TerraformDefinition("c", "outputs.tf", 9, null),
                new TerraformDefinition("d", "outputs.tf", 14, "outer"),
                new TerraformDefinition("e", "outputs.tf", 23, null),
            ],
            outputs.Definitions.Values);
    }

    [Fact]
    public void Find_accepts_dots_or_colons_and_any_case()
    {
        var outputs = TerraformOutputs.Parse("""{"db": {"value": {"Host": "h"}}, "DB": {"value": 1}}""");
        Assert.Equal("h", outputs.Find("db.Host")!.Value);
        Assert.Equal("h", outputs.Find("db:Host")!.Value);
        Assert.Equal("h", outputs.Find("DB:HOST")!.Value);
        Assert.Equal("1", outputs.Find("DB")!.Value);
        Assert.Null(outputs.Find("nope"));
        Assert.Throws<ArgumentNullException>(() => outputs.Find(null!));
    }

    [Fact]
    public void Matches_names_exactly_then_ignoring_case_then_by_separator()
    {
        var outputs = TerraformOutputs.Parse("""
            {
              "db_timeout": {"value": "loose"},
              "DB_TIMEOUT": {"value": "case"},
              "Db_Timeout": {"value": "exact"},
              "cache": {"value": {"ttl": 900, "end-point": "e"}}
            }
            """);
        var resolver = new TerraformResolver(outputs, new TerraformTraceOptions());

        Assert.Equal("exact", resolver.Match("Db_Timeout")!.Value);
        Assert.Equal("case", resolver.Match("DB_TIMEOUT")!.Value);
        Assert.Equal("db_timeout", resolver.Match("db_TIMEOUT")!.Output);
        Assert.Equal("loose", resolver.Match("DB__TIMEOUT")!.Value);
        Assert.Equal("loose", resolver.Match("db.timeout")!.Value);
        Assert.Equal("loose", resolver.Match("Db:Timeout")!.Value);
        Assert.Equal("900", resolver.Match("cache:ttl")!.Value);
        Assert.Equal("900", resolver.Match("cache.ttl")!.Value);
        Assert.Equal("900", resolver.Match("CACHE:TTL")!.Value);
        Assert.Equal("900", resolver.Match("CACHE_TTL")!.Value);
        Assert.Equal("900", resolver.Match("Cache__Ttl")!.Value);
        Assert.Equal("e", resolver.Match("CACHE__END_POINT")!.Value);
        Assert.Null(resolver.Match("cachettl"));
        Assert.Null(resolver.Match("db"));
    }

    [Fact]
    public void Links_layers_to_outputs_with_notes_that_never_hold_values()
    {
        var outputs = TerraformOutputs.Parse("""
            {
              "db_host": {"value": "db.internal"},
              "db_password": {"value": "hunter2", "sensitive": true},
              "port": {"value": 5432}
            }
            """, "out.json");
        outputs.AddDefinitions("output \"db_host\" {\n  value = aws_db.address\n}\noutput \"db_password\" {\n  value = random.pw\n}\n", "outputs.tf");
        var resolver = new TerraformResolver(outputs, new TerraformTraceOptions());
        var diagnostics = new TraceDiagnostics();

        var host = Assert.Single(resolver.Resolve(Env("DB_HOST", "db.internal"), diagnostics));
        Assert.Equal(LayerKind.Terraform, host.Kind);
        Assert.Equal("Terraform output db_host", host.Source);
        Assert.Equal("outputs.tf:1", host.Location);
        Assert.Equal("db.internal", host.Value);
        Assert.False(host.IsSensitive);
        Assert.True(host.IsInferred);
        Assert.Equal("matched by name; value = aws_db.address", host.Note);
        Assert.Equal(new TerraformOutputRef("db_host"), host.Reference);

        var password = Assert.Single(resolver.Resolve(Env("DB_PASSWORD", "other"), diagnostics));
        Assert.True(password.IsSensitive);
        Assert.Equal("hunter2", password.Value);
        Assert.Equal("matched by name; sensitive output; the Terraform value differs from the value seen here, so the deployment may be out of date or the value was transformed", password.Note);
        Assert.DoesNotContain("random.pw", password.Note, StringComparison.Ordinal);

        var port = Assert.Single(resolver.Resolve(Env("PORT", "5432"), diagnostics));
        Assert.Equal("out.json", port.Location);
        Assert.Equal("matched by name", port.Note);
        Assert.Equal("matched by name", Assert.Single(resolver.Resolve(Env("PORT"), diagnostics)).Note);
        Assert.Empty(resolver.Resolve(Env("OTHER"), diagnostics));
        Assert.Empty(diagnostics.Warnings);
    }

    [Fact]
    public void A_key_that_is_secret_by_name_never_shows_a_literal_from_its_output_expression_in_a_note()
    {
        // None of the outputs is marked sensitive: only the names of the variables say they are secret.
        var outputsFile = Path.Join(_root, "outputs.json");
        File.WriteAllText(outputsFile, """
            {
              "db_password": {"value": "fake-literal-pw"},
              "db_connstr": {"value": "Host=db.internal;Password=fake-template-pw"},
              "account_key": {"value": 13572468},
              "api_token": {"value": "fake-resource-token"}
            }
            """);
        File.WriteAllText(Path.Join(_root, "outputs.tf"), """
            output "db_password" {
              value = "fake-literal-pw"
            }
            output "db_connstr" {
              value = "Host=${aws_db.address};Password=fake-template-pw"
            }
            output "account_key" {
              value = 13572468
            }
            output "api_token" {
              value = random_string.api[0].result
            }
            """);
        var manifests = KubernetesManifests.Parse("""
            apiVersion: v1
            kind: Pod
            metadata: {name: p}
            spec:
              containers:
                - name: c
                  env:
                    - {name: DB_PASSWORD, value: fake-literal-pw}
                    - {name: DB_CONNSTR, value: "Host=db.internal;Password=fake-template-pw"}
                    - {name: ACCOUNT_KEY, value: "13572468"}
                    - {name: API_TOKEN, value: fake-resource-token}
            """);

        var report = ConfigTracer.ForContainer(manifests).WithTerraform(TerraformOutputs.Load(outputsFile, terraformFolder: _root)).Trace();
        var output = report.ToText() + report.ToJson() + string.Join("\n", report.Warnings);

        Assert.Equal(["ACCOUNT_KEY", "API_TOKEN", "DB_CONNSTR", "DB_PASSWORD"], report.Settings.Select(s => s.Key));
        Assert.All(report.Settings, s => Assert.True(s.IsSensitive));
        foreach (var secret in new[] { "fake-literal-pw", "fake-template-pw", "13572468", "fake-resource-token" })
        {
            Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        }

        // A reference names where the value comes from without holding it, so it stays.
        Assert.Equal(
            ["matched by name", "matched by name; value = random_string.api[0].result", "matched by name", "matched by name"],
            report.Settings.Select(s => Assert.Single(Assert.Single(s.Steps).Origins).Note));
    }

    [Fact]
    public void Explicit_links_win_and_warn_when_the_output_is_missing()
    {
        var outputs = TerraformOutputs.Parse("""{"db": {"value": {"host": "h"}}, "orders_db_host": {"value": "by-name"}}""");
        var options = new TerraformTraceOptions().Map("ORDERS_DB_HOST", " db:host ").Map("MISSING", "nope");
        options.Links["BLANK"] = " ";
        var resolver = new TerraformResolver(outputs, options);
        var diagnostics = new TraceDiagnostics();

        var linked = Assert.Single(resolver.Resolve(Env("ORDERS_DB_HOST", "h"), diagnostics));
        Assert.False(linked.IsInferred);
        Assert.Null(linked.Note);
        Assert.Equal("Terraform output db.host", linked.Source);
        Assert.Equal(new TerraformOutputRef("db", "host"), linked.Reference);
        Assert.Empty(resolver.Resolve(Env("MISSING"), diagnostics));
        Assert.Equal(["MISSING is linked to Terraform output nope, but no such output was loaded."], diagnostics.Warnings);
        Assert.Empty(resolver.Resolve(Env("BLANK"), diagnostics));
        Assert.Equal("by-name", Assert.Single(resolver.Resolve(Env("orders.db.host"), diagnostics)).Value);
        Assert.Throws<ArgumentException>(() => options.Map(" ", "x"));
        Assert.Throws<ArgumentException>(() => options.Map("x", " "));
    }

    [Fact]
    public void Name_matching_can_be_turned_off_and_only_named_layers_are_linked()
    {
        var outputs = TerraformOutputs.Parse("""{"a": {"value": "1"}}""");
        var off = new TerraformResolver(outputs, new TerraformTraceOptions { MatchByName = false });
        Assert.Empty(off.Resolve(Env("a"), new TraceDiagnostics()));

        var on = new TerraformResolver(outputs, new TerraformTraceOptions());
        var diagnostics = new TraceDiagnostics();
        Assert.Single(on.Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesWorkload, Source = "w", Reference = new WorkloadEnvRef("w", "c", "a") }, diagnostics));
        Assert.Single(on.Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "c", Reference = new ConfigMapKeyRef("cm", "a") }, diagnostics));
        Assert.Single(on.Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "s", Reference = new SecretKeyRef("s", "a") }, diagnostics));
        Assert.Empty(on.Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "e", Reference = new ExternalSecretRef(null, null, "a") }, diagnostics));
        Assert.Empty(on.Resolve(new ProvenanceStep { Kind = LayerKind.Helm, Source = "h", Reference = new HelmValueRef("a") }, diagnostics));
        Assert.Empty(on.Resolve(new ProvenanceStep { Kind = LayerKind.Helm, Source = "h" }, diagnostics));
        Assert.Throws<ArgumentNullException>(() => on.Resolve(null!, diagnostics));
        Assert.Throws<ArgumentNullException>(() => on.Resolve(Env("a"), null!));
    }

    [Fact]
    public void Output_warnings_are_added_to_the_trace_once()
    {
        var outputs = TerraformOutputs.Parse("""{"a": {"value": "1"}, "bad": {}}""");
        var resolver = new TerraformResolver(outputs, new TerraformTraceOptions());
        var first = new TraceDiagnostics();
        resolver.Resolve(Env("x"), first);
        var second = new TraceDiagnostics();
        resolver.Resolve(Env("x"), second);

        Assert.Equal(["Terraform output bad in terraform output -json has no value."], first.Warnings);
        Assert.Empty(second.Warnings);
    }

    [Fact]
    public void Traces_an_app_configuration_through_environment_variables()
    {
        var prefix = $"CFTF{Guid.NewGuid():N}_";
        var environment = new Dictionary<string, string?> { [prefix + "Db__Timeout"] = "30" };
        Environment.SetEnvironmentVariable(prefix + "Db__Timeout", "30");
        try
        {
            var config = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var report = ConfigTracer.For(config)
                .WithOptions(o => o.Environment = environment)
                .WithTerraform(TerraformOutputs.Parse("""{"db": {"value": {"timeout": 30}}}"""), o => o.Map(prefix + "Db__Timeout", "db.timeout"))
                .Trace();

            var origin = Assert.Single(report.Find("Db:Timeout")!.EffectiveStep!.Origins);
            Assert.Equal("Terraform output db.timeout", origin.Source);
            Assert.True(origin.IsEffective);
            Assert.Equal("set in environment variable " + prefix + "Db__Timeout from Terraform output db.timeout", report.Find("Db:Timeout")!.Story);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Db__Timeout", null);
        }

        Assert.Throws<ArgumentNullException>(() => ConfigTracer.For(new ConfigurationBuilder().Build()).WithTerraform(null!));
    }

    [Fact]
    public void Provider_describes_itself_and_checks_settings()
    {
        var provider = new TerraformSourceProvider();
        Assert.Equal("terraform-outputs", provider.Id);
        Assert.Equal("Terraform outputs", provider.DisplayName);
        Assert.Equal(SourceCategory.GitOps, provider.Category);
        Assert.Equal(["OutputsFile", "StateFile", "TerraformFolder", "Links"], provider.Settings.Select(s => s.Name));
        Assert.Equal([SourceSettingKind.Path, SourceSettingKind.Path, SourceSettingKind.Path, SourceSettingKind.Lines], provider.Settings.Select(s => s.Kind));
        Assert.All(provider.Settings, s => Assert.False(s.Required));
        Assert.Equal(["Outputs file", "State file", "Terraform folder", "Links"], provider.Settings.Select(s => s.Label));

        Assert.Throws<ArgumentNullException>(() => provider.Create(null!));
        Assert.Equal("Set OutputsFile, StateFile or TerraformFolder. (Parameter 'values')", Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?> { ["OutputsFile"] = " " })).Message);
        Assert.StartsWith("OutputsFile /nonexistent.json does not exist.", Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?> { ["OutputsFile"] = "/nonexistent.json" })).Message, StringComparison.Ordinal);
        Assert.StartsWith("StateFile /nonexistent does not exist.", Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?> { ["StateFile"] = "/nonexistent" })).Message, StringComparison.Ordinal);
        Assert.StartsWith("TerraformFolder /nonexistent does not exist.", Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?> { ["TerraformFolder"] = "/nonexistent" })).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?> { ["TerraformFolder"] = OutputsFile }));
        Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?> { ["OutputsFile"] = Folder }));

        foreach (var bad in new[] { "NO_EQUALS", "=output", "NAME=", "NAME = " })
        {
            var ex = Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?> { ["OutputsFile"] = OutputsFile, ["Links"] = bad }));
            Assert.StartsWith($"The link '{bad.Trim()}' is not NAME=output.", ex.Message, StringComparison.Ordinal);
        }

        var source = Assert.IsType<TerraformSource>(provider.Create(new Dictionary<string, string?>
        {
            ["OutputsFile"] = $" {OutputsFile} ",
            ["StateFile"] = StateFile,
            ["TerraformFolder"] = Folder,
            ["Links"] = "A = b\n\n  C=d.e  \r\n",
        }));
        Assert.Equal((OutputsFile, StateFile, Folder), (source.OutputsFile, source.StateFile, source.TerraformFolder));
        Assert.Equal(new Dictionary<string, string> { ["A"] = "b", ["C"] = "d.e" }, source.TraceOptions.Links);
        Assert.Equal("Terraform outputs outputs.json", source.DisplayName);
    }

    [Fact]
    public async Task Source_loads_leaf_outputs_as_entries_and_finds_unused_ones()
    {
        var source = new TerraformSource(OutputsFile, StateFile, Folder);
        var tracer = ConfigTracer.ForContainer(KubernetesManifests.LoadFile(Path.Join(Dir, "rendered.yaml")), "orders", "orders");
        var loaded = await tracer.WithSourceAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal("Terraform outputs outputs.json", loaded.DisplayName);
        Assert.Equal(
            ["cache.endpoint", "cache.ttl", "db_host", "db_password", "queue_urls.0", "queue_urls.1", "unused_bucket", "db_port"],
            loaded.Entries.Select(e => e.Name));
        Assert.Equal(new SourceEntry("db_password", new TerraformOutputRef("db_password"), true, "string", "outputs.json"), loaded.Entries[3]);
        Assert.Equal(new TerraformOutputRef("cache", "ttl"), loaded.Entries[1].Reference);
        Assert.Null(loaded.Entries[1].Detail);
        Assert.Equal(["queue_urls.0", "unused_bucket"], loaded.FindUnused(tracer.Trace()).Select(e => e.Name));
        Assert.Empty(loaded.Warnings);
    }

    [Fact]
    public async Task Source_checks_arguments_and_names_itself_after_what_it_reads()
    {
        Assert.Throws<ArgumentException>(() => new TerraformSource(" ", "", null));
        Assert.Equal("Terraform outputs terraform.tfstate", new TerraformSource(stateFile: StateFile).DisplayName);
        Assert.Equal("Terraform outputs infra", new TerraformSource(terraformFolder: Folder + Path.DirectorySeparatorChar).DisplayName);
        var folderOnly = new TerraformSource(terraformFolder: Folder);
        Assert.Null(folderOnly.OutputsFile);
        Assert.Null(folderOnly.StateFile);
        Assert.True(folderOnly.TraceOptions.MatchByName);
        Assert.Equal(2, (await folderOnly.LoadAsync(TestContext.Current.CancellationToken)).Entries.Count);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => folderOnly.LoadAsync(cancelled.Token));
    }
}
