using ConfigFerret.Compose;

namespace ConfigFerret.Tests;

public class ComposeProjectTests
{
    private static ComposeProject Parse(string yaml, Action<ComposeOptions>? configure = null)
    {
        var options = new ComposeOptions();
        configure?.Invoke(options);
        return ComposeProject.Parse([("compose.yaml", yaml)], options);
    }

    private static ComposeService Service(ComposeProject project, string name = "app") => project.FindService(name)!;

    [Fact]
    public void Reads_environment_as_a_mapping_or_a_list()
    {
        var project = Parse("""
            services:
              app:
                environment:
                  A: one
                  B: 2
                  C: true
                  D: ""
              worker:
                environment:
                  - E=five
                  - F=
                  - G=x=y
                  - " H = spaced"
            """);

        Assert.Equal(["app", "worker"], project.ServiceNames);
        Assert.Equal(new Dictionary<string, string?> { ["A"] = "one", ["B"] = "2", ["C"] = "true", ["D"] = "" }, Service(project).Environment);
        Assert.Equal(new Dictionary<string, string?> { ["E"] = "five", ["F"] = "", ["G"] = "x=y", ["H"] = " spaced" }, Service(project, "worker").Environment);
        var a = Assert.Single(Service(project).Layers("A"));
        Assert.Equal(LayerKind.DockerCompose, a.Kind);
        Assert.Equal("compose service app env A", a.Source);
        Assert.Equal("compose.yaml:4", a.Location);
        Assert.Equal(new ComposeEnvironmentRef("app", "A"), a.Reference);
        Assert.True(a.IsEffective);
        Assert.Equal("compose.yaml:10", Service(project, "worker").Layers("E")[0].Location);
        Assert.Empty(Service(project).Layers("nope"));
        Assert.Null(project.FindService("nope"));
        Assert.Null(project.ProjectDirectory);
        Assert.Equal(["compose.yaml"], project.Files);
        Assert.Equal(2, project.Services.Count);
    }

    [Fact]
    public void A_name_without_a_value_comes_from_the_shell_or_the_env_file()
    {
        var project = Parse("""
            services:
              app:
                environment:
                  - FROM_SHELL
                  - MISSING
                  - FROM_MAP
            """, o =>
            {
                o.Variables["FROM_SHELL"] = "shell";
                o.Variables["FROM_MAP"] = "map";
                o.Variables["NULL"] = null;
            });

        var service = Service(project);
        Assert.Equal("shell", service.Environment["FROM_SHELL"]);
        Assert.Null(service.Environment["MISSING"]);
        var shell = service.Layers("FROM_SHELL")[0];
        Assert.Equal("a name without a value, so the value comes from the shell or the project's env file", shell.Note);
        var origin = Assert.Single(shell.Origins);
        Assert.Equal("shell variable FROM_SHELL", origin.Source);
        Assert.Equal("shell", origin.Value);
        Assert.Null(origin.Note);
        Assert.Equal(new EnvironmentVariableRef("FROM_SHELL"), origin.Reference);
        var missing = service.Layers("MISSING")[0];
        Assert.True(missing.IsRemoval);
        Assert.False(missing.IsEffective);
        Assert.Null(missing.Value);
        Assert.Empty(missing.Origins);
        Assert.Contains("don't set, so the container does not get it", missing.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Interpolates_from_the_shell_then_the_env_files()
    {
        using var folder = new TempFolder();
        folder.Write(".env", "HOST=from-dotenv\nPORT=5432\nUSER=dotenv-user\n");
        var compose = folder.Write("compose.yaml", """
            services:
              app:
                environment:
                  URL: postgres://${USER}@${HOST}:${PORT}/${DB:-orders}
                  TAG: $TAG
                  LITERAL: $$HOST
                  EMPTY: ${NOT_SET}
            """);
        var options = new ComposeOptions();
        options.Variables["USER"] = "shell-user";
        options.Variables["TAG"] = "v1";

        var project = ComposeProject.Load([compose], options);

        var service = Service(project);
        Assert.Equal(folder.Path, project.ProjectDirectory);
        Assert.Equal("postgres://shell-user@from-dotenv:5432/orders", service.Environment["URL"]);
        Assert.Equal("v1", service.Environment["TAG"]);
        Assert.Equal("$HOST", service.Environment["LITERAL"]);
        Assert.Equal("", service.Environment["EMPTY"]);
        Assert.Equal(".env", Assert.Single(project.Environment.Files).Name);
        var origins = service.Layers("URL")[0].Origins;
        Assert.Equal(["shell variable USER", ".env", ".env", "variable DB, not set"], origins.Select(o => o.Source));
        Assert.Equal([null, "line 1", "line 2", null], origins.Select(o => o.Location));
        Assert.All(origins, o => Assert.True(o.IsEffective));
        Assert.Equal("interpolated as ${HOST} in compose.yaml:4", origins[1].Note);
        Assert.Equal("interpolated as ${DB} in compose.yaml:4; the reference's default is used", origins[3].Note);
        Assert.Null(origins[3].Reference);
        Assert.Equal(new DotEnvEntryRef(Path.Join(folder.Path, ".env"), "HOST"), origins[1].Reference);
        Assert.Equal("interpolated as ${NOT_SET} in compose.yaml:7; an empty string is used", Assert.Single(service.Layers("EMPTY")[0].Origins).Note);
        Assert.Equal(["compose.yaml:7: the NOT_SET variable is not set, so an empty string is used."], service.Warnings);
    }

    [Fact]
    public void Env_files_passed_in_replace_the_default_dotenv_and_layer_in_order()
    {
        using var folder = new TempFolder();
        folder.Write(".env", "A=dotenv");
        var first = folder.Write("one.envfile", "A=one\nB=one");
        var second = folder.Write("two.envfile", "B=two-${A}");
        var compose = folder.Write("compose.yaml", "services:\n  app:\n    environment:\n      A: $A\n      B: $B\n");
        var options = new ComposeOptions();
        options.EnvFiles.Add(first);
        options.EnvFiles.Add(second);

        var project = ComposeProject.Load([compose], options);

        Assert.Equal("one", Service(project).Environment["A"]);
        Assert.Equal("two-one", Service(project).Environment["B"]);
        Assert.Equal(["one.envfile", "two.envfile"], project.Environment.Files.Select(f => f.Name));
        options.EnvFiles.Add(Path.Join(folder.Path, "missing.envfile"));
        Assert.Throws<FileNotFoundException>(() => ComposeProject.Load([compose], options));
    }

    [Fact]
    public void Never_reads_the_process_environment_unless_asked()
    {
        var name = "CF_COMPOSE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "from-process");
        try
        {
            var yaml = $"services:\n  app:\n    environment:\n      A: ${{{name}}}\n";
            Assert.Equal("", Service(Parse(yaml)).Environment["A"]);
            Assert.Equal("from-process", Service(Parse(yaml, o => o.UseProcessEnvironment = true)).Environment["A"]);
            Assert.Equal("explicit", Service(Parse(yaml, o =>
            {
                o.UseProcessEnvironment = true;
                o.Variables[name] = "explicit";
            })).Environment["A"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Env_file_lines_come_first_and_environment_wins()
    {
        using var folder = new TempFolder();
        folder.Write("common.envfile", "A=common\nB=common\nREF=${SHARED}\n");
        folder.Write("app.envfile", "B=app\nC=${A}-app\nexport SHARED=x\nSHARED\n");
        folder.Write("raw.envfile", "R='quoted ${A}'\n");
        var compose = folder.Write("compose.yaml", """
            services:
              app:
                env_file:
                  - common.envfile
                  - path: app.envfile
                    required: true
                  - path: optional.envfile
                    required: false
                  - path: raw.envfile
                    format: raw
                environment:
                  B: environment
            """);
        var options = new ComposeOptions();
        options.Variables["SHARED"] = "shell";

        var service = Service(ComposeProject.Load([compose], options));

        Assert.Equal("common", service.Environment["A"]);
        Assert.Equal("environment", service.Environment["B"]);
        Assert.Equal("common-app", service.Environment["C"]);
        Assert.Equal("shell", service.Environment["REF"]);
        Assert.Equal("'quoted ${A}'", service.Environment["R"]);
        Assert.Equal(new DotEnvEntryRef(Path.Join(folder.Path, "raw.envfile"), "R"), service.Layers("R")[0].Reference);
        var inherited = service.Layers("SHARED")[^1];
        Assert.Equal("env_file of service app (compose.yaml:5); a bare key, so the value comes from the shell", inherited.Note);
        Assert.Equal("shell", inherited.Value);
        var b = service.Layers("B");
        Assert.Equal(["env_file common.envfile", "env_file app.envfile", "compose service app env B"], b.Select(s => s.Source));
        Assert.Equal([LayerKind.DotEnv, LayerKind.DotEnv, LayerKind.DockerCompose], b.Select(s => s.Kind));
        Assert.Equal([false, false, true], b.Select(s => s.IsEffective));
        Assert.Equal("env_file of service app (compose.yaml:4)", b[0].Note);
        Assert.Equal("line 2", b[0].Location);
        Assert.Equal(new DotEnvEntryRef(Path.Join(folder.Path, "common.envfile"), "B"), b[0].Reference);
        var c = Assert.Single(service.Layers("C")[0].Origins);
        Assert.Equal("common.envfile", c.Source);
        Assert.Equal("interpolated as ${A} in app.envfile line 2", c.Note);
        Assert.Empty(service.Warnings);
    }

    [Fact]
    public void Reports_an_env_file_passed_in_that_does_not_exist()
    {
        using var folder = new TempFolder();
        var compose = folder.Write("compose.yaml", "services: {}\n");
        var options = new ComposeOptions();
        options.EnvFiles.Add(Path.Join(folder.Path, "missing.envfile"));

        var ex = Assert.Throws<FileNotFoundException>(() => ComposeProject.Load([compose], options));

        Assert.Equal("The env file missing.envfile does not exist or is larger than 1 MiB.", ex.Message);
        Assert.Equal("Pass at least one compose file. (Parameter 'files')", Assert.Throws<ArgumentException>(() => ComposeProject.Load()).Message);
    }

    [Fact]
    public void Warns_about_a_required_env_file_that_does_not_exist()
    {
        using var folder = new TempFolder();
        var compose = folder.Write("compose.yaml", "services:\n  app:\n    env_file: missing.envfile\n    environment: { A: 1 }\n");

        var service = Service(ComposeProject.Load(compose));

        Assert.Equal("1", service.Environment["A"]);
        Assert.Equal("compose.yaml:3: env_file missing.envfile does not exist (or is larger than 1 MiB); docker compose stops with an error when a required env_file is missing.", Assert.Single(service.Warnings));
    }

    [Fact]
    public void Without_a_project_directory_no_other_file_is_read()
    {
        var project = Parse("""
            include:
              - other.yaml
            services:
              app:
                extends:
                  file: base.yaml
                  service: base
                env_file: app.envfile
                secrets: [s]
            secrets:
              s:
                file: s.txt
            """, o => o.ReadSecretFiles = true);

        var service = Service(project);
        Assert.Equal(
            [
                "compose.yaml:5: the extends file base.yaml was not read, because the compose files were parsed from text without a project directory.",
                "compose.yaml:8: env_file app.envfile was not read, because the compose files were parsed from text without a project directory.",
            ],
            service.Warnings);
        Assert.Equal("compose.yaml:2: the include of other.yaml was not read, because the compose files were parsed from text without a project directory.", Assert.Single(project.Warnings));
        var secret = service.MountLayers("/run/secrets/s")[0];
        Assert.Equal("s.txt", secret.Location);
        Assert.Null(secret.Value);
        Assert.EndsWith("; the file could not be read", secret.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_reads_relative_files_from_the_project_directory()
    {
        using var folder = new TempFolder();
        folder.Write("app.envfile", "A=from-file");

        var project = Parse("services:\n  app:\n    env_file: app.envfile\n", o => o.ProjectDirectory = folder.Path);

        Assert.Equal("from-file", Service(project).Environment["A"]);
        Assert.Equal(folder.Path, project.ProjectDirectory);
    }

    [Fact]
    public void A_later_file_wins_per_variable_and_appends_env_files()
    {
        using var folder = new TempFolder();
        folder.Write("a.envfile", "X=a\nY=a");
        folder.Write("b.envfile", "Y=b");
        var main = folder.Write("compose.yaml", "services:\n  app:\n    env_file: a.envfile\n    environment:\n      A: base\n      B: base\n");
        var over = folder.Write("compose.override.yaml", "services:\n  app:\n    env_file: [b.envfile, a.envfile]\n    environment:\n      - B=override\n  extra:\n    environment: [C=1]\n");

        var project = ComposeProject.Load(main, over);

        var service = Service(project);
        Assert.Equal(["compose.yaml", "compose.override.yaml"], project.Files);
        Assert.Equal(["app", "extra"], project.ServiceNames);
        Assert.Equal("base", service.Environment["A"]);
        Assert.Equal("override", service.Environment["B"]);
        Assert.Equal(["compose.yaml:6", "compose.override.yaml:5"], service.Layers("B").Select(s => s.Location));
        // a.envfile is listed again last, so it wins over b.envfile.
        Assert.Equal("a", service.Environment["Y"]);
        Assert.Equal(["env_file b.envfile", "env_file a.envfile"], service.Layers("Y").Select(s => s.Source));
    }

    [Fact]
    public void Reset_and_override_tags_drop_what_earlier_files_set()
    {
        using var folder = new TempFolder();
        folder.Write("a.envfile", "FROM_FILE=1");
        var main = folder.Write("compose.yaml", """
            services:
              app:
                env_file: a.envfile
                environment: { A: 1, B: 1 }
                secrets: [s]
                configs: [c]
              other:
                environment: { A: 1, B: 1 }
            secrets:
              s: { environment: S }
            configs:
              c: { content: x }
            """);
        var over = folder.Write("compose.override.yaml", """
            services:
              app:
                env_file: !reset []
                environment: !override
                  C: 3
                secrets: !reset []
              other:
                environment: !reset { IGNORED: 1 }
                env_file: !reset [a.envfile]
                secrets: !reset [s]
            """);

        var project = ComposeProject.Load(main, over);

        Assert.Equal(["C"], Service(project).Environment.Keys);
        Assert.DoesNotContain("FROM_FILE", Service(project).Environment.Keys);
        Assert.Equal(["/c"], Service(project).Mounts.Select(m => m.Path));
        Assert.Empty(Service(project, "other").Environment);
        Assert.Empty(Service(project, "other").Mounts);
    }

    [Fact]
    public void Applies_yaml_merge_keys()
    {
        var project = Parse("""
            x-a: &a
              A: from-a
              B: from-a
            x-b: &b
              B: from-b
              C: from-b
            x-svc: &svc
              environment:
                FROM_SVC: yes
            services:
              app:
                <<: *svc
              merged:
                environment:
                  <<: [*a, *b]
                  C: own
              quoted:
                environment:
                  "<<": literal
            """);

        Assert.Equal("yes", Service(project).Environment["FROM_SVC"]);
        Assert.Equal(new Dictionary<string, string?> { ["A"] = "from-a", ["B"] = "from-a", ["C"] = "own" }, Service(project, "merged").Environment);
        Assert.Equal("compose.yaml:2", Service(project, "merged").Layers("A")[0].Location);
        Assert.Equal("literal", Service(project, "quoted").Environment["<<"]);
    }

    [Fact]
    public void Follows_extends_in_the_same_file_and_in_another_file()
    {
        using var folder = new TempFolder();
        folder.Write("shared/base.yaml", """
            services:
              root:
                environment: { ROOT: root, LEVEL: root }
              base:
                extends: root
                env_file: base.envfile
                environment: { LEVEL: base }
            """);
        folder.Write("shared/base.envfile", "FROM_BASE_FILE=1");
        var compose = folder.Write("compose.yaml", """
            services:
              common:
                environment: { COMMON: 1, LEVEL: common }
              app:
                extends:
                  file: shared/base.yaml
                  service: base
                environment: { LEVEL: app }
              web:
                extends: common
              worker:
                extends:
                  service: common
                environment: { LEVEL: worker }
            """);

        var project = ComposeProject.Load(compose);

        var app = Service(project);
        Assert.Equal("app", app.Environment["LEVEL"]);
        Assert.Equal("root", app.Environment["ROOT"]);
        Assert.Equal("1", app.Environment["FROM_BASE_FILE"]);
        Assert.Equal(["shared/base.yaml:3", "shared/base.yaml:7", "compose.yaml:8"], app.Layers("LEVEL").Select(s => s.Location));
        Assert.Equal("env_file shared/base.envfile", app.Layers("FROM_BASE_FILE")[0].Source);
        Assert.Equal("common", Service(project, "web").Environment["LEVEL"]);
        Assert.Equal("worker", Service(project, "worker").Environment["LEVEL"]);
        Assert.Empty(app.Warnings);
    }

    [Fact]
    public void Reads_an_extends_file_once_for_every_service_that_uses_it()
    {
        using var folder = new TempFolder();
        folder.Write("base.yaml", "services:\n  base:\n    environment: { A: base }\n");
        var compose = folder.Write("compose.yaml", "services:\n  a:\n    extends: { file: base.yaml, service: base }\n  b:\n    extends: { file: ./base.yaml, service: base }\n");

        var project = ComposeProject.Load(compose);

        Assert.Equal("base", Service(project, "a").Environment["A"]);
        Assert.Equal("base.yaml:3", Service(project, "b").Layers("A")[0].Location);
    }

    [Fact]
    public void Rejects_extends_nested_too_deep()
    {
        var yaml = "services:\n" + string.Concat(Enumerable.Range(0, 20).Select(i => $"  s{i}:\n    extends: s{i + 1}\n")) + "  s20: {}\n";

        var ex = Assert.Throws<FormatException>(() => Parse(yaml));

        Assert.Equal("Service s16 in compose.yaml: extends nests more than 16 deep.", ex.Message);
    }

    [Fact]
    public void Extends_in_an_override_file_can_name_a_service_from_the_main_file()
    {
        using var folder = new TempFolder();
        var main = folder.Write("compose.yaml", "services:\n  base:\n    environment: { A: base }\n  app:\n    image: x\n");
        var over = folder.Write("compose.override.yaml", "services:\n  app:\n    extends: base\n");

        Assert.Equal("base", Service(ComposeProject.Load(main, over)).Environment["A"]);
    }

    [Fact]
    public void Warns_when_an_extended_service_or_file_is_missing()
    {
        using var folder = new TempFolder();
        folder.Write("other.yaml", "services:\n  x: {}\n");
        var compose = folder.Write("compose.yaml", """
            services:
              a:
                extends: nope
              b:
                extends: { file: missing.yaml, service: x }
              c:
                extends: { file: other.yaml, service: y }
              d:
                extends: { file: compose.yaml, service: a }
            """);

        var project = ComposeProject.Load(compose);

        Assert.Equal("compose.yaml:3: service a extends nope, which is not defined.", Assert.Single(Service(project, "a").Warnings));
        Assert.Equal("compose.yaml:5: the extends file missing.yaml does not exist.", Assert.Single(Service(project, "b").Warnings));
        Assert.Equal("compose.yaml:7: other.yaml has no service y to extend.", Assert.Single(Service(project, "c").Warnings));
        Assert.Equal(["compose.yaml:3: service a extends nope, which is not defined."], Service(project, "d").Warnings);
    }

    [Theory]
    [InlineData("services:\n  a:\n    extends: a\n", "Service a in compose.yaml extends itself.")]
    [InlineData("services:\n  a:\n    extends: b\n  b:\n    extends: a\n", "Service a in compose.yaml extends itself.")]
    [InlineData("services:\n  a:\n    extends: {}\n", "compose.yaml:3: extends needs a service.")]
    [InlineData("- a\n", "compose.yaml: a compose file must be a mapping at the top level.")]
    [InlineData("services: [a]\n", "compose.yaml:1: services must be a mapping.")]
    [InlineData("services:\n  a: b\n", "compose.yaml:2: service a must be a mapping.")]
    [InlineData("services:\n  a:\n    environment: x\n", "compose.yaml:3: environment must be a mapping or a list.")]
    [InlineData("services:\n  a:\n    environment:\n      A: [1]\n", "compose.yaml:4: the value of A must be a string, a number or a boolean.")]
    [InlineData("services:\n  a:\n    environment:\n      - {A: 1}\n", "compose.yaml:4: an environment list entry must be NAME=VALUE or NAME.")]
    [InlineData("services:\n  a:\n    environment:\n      - =1\n", "compose.yaml:4: an environment entry needs a name.")]
    [InlineData("services:\n  a:\n    env_file:\n      - required: false\n", "compose.yaml:4: an env_file entry needs a path.")]
    [InlineData("services:\n  a:\n    secrets:\n      - target: x\n", "compose.yaml:4: a secret entry needs a source.")]
    [InlineData("services:\n  a:\n    configs:\n      - target: x\n", "compose.yaml:4: a config entry needs a source.")]
    [InlineData("services:\n  a:\n    environment:\n      A: ${B:?B is needed}\n", "compose.yaml:4: B: B is needed.")]
    [InlineData("include:\n  - {}\n", "compose.yaml:2: an include needs a path.")]
    [InlineData("services:\n  a: [\n", null)]
    public void Rejects_invalid_compose_files(string yaml, string? message)
    {
        var ex = Assert.Throws<FormatException>(() => Parse(yaml));
        if (message is not null)
        {
            Assert.Equal(message, ex.Message);
        }
        else
        {
            Assert.StartsWith("Could not parse the compose file compose.yaml: ", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Reads_empty_and_multi_document_files()
    {
        Assert.Empty(Parse("").ServiceNames);
        Assert.Empty(Parse("name: x\n").ServiceNames);
        Assert.Empty(Parse("services:\n").ServiceNames);
        var multi = Parse("services:\n  a: {}\n---\nservices:\n  b: {}\n");
        Assert.Equal(["a"], multi.ServiceNames);
        Assert.Equal("compose.yaml holds 2 YAML documents; only the first is read.", Assert.Single(multi.Warnings));
    }

    [Fact]
    public void Rejects_merge_keys_nested_too_deep()
    {
        var yaml = new System.Text.StringBuilder("x0: &a0 {A: 1}\n");
        for (var i = 1; i < 20; i++)
        {
            yaml.Append(System.Globalization.CultureInfo.InvariantCulture, $"x{i}: &a{i} {{<<: *a{i - 1}}}\n");
        }

        yaml.Append("services:\n  app:\n    environment:\n      <<: *a19\n");
        var ex = Assert.Throws<FormatException>(() => Parse(yaml.ToString()));
        Assert.Contains("merge keys nest more than 16 deep", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lists_secrets_and_configs_as_mounted_files()
    {
        using var folder = new TempFolder();
        folder.Write("secrets/db.txt", "s3cret\n");
        folder.Write("settings.json", "{}");
        var compose = folder.Write("compose.yaml", """
            services:
              app:
                secrets:
                  - db
                  - source: api_key
                    target: api.key
                  - source: cert
                    target: /etc/ssl/app.pem
                  - undefined
                  - external
                  - empty
                configs:
                  - settings
                  - source: inline
                    target: /app/inline.txt
                  - source: from_env
                    target: relative.txt
                  - from_unset_env
            secrets:
              db: { file: ./secrets/db.txt }
              api_key: { environment: API_KEY }
              cert: { file: ./missing.pem }
              external: { external: true }
              empty: { name: x }
            configs:
              settings: { file: settings.json }
              inline: { content: "Value: ${TAG}" }
              from_env: { environment: TAG }
              from_unset_env: { environment: NOT_SET }
            """);
        var options = new ComposeOptions();
        options.Variables["API_KEY"] = "key-value";
        options.Variables["TAG"] = "v2";

        var service = Service(ComposeProject.Load([compose], options));

        Assert.Equal(
            ["/app/inline.txt", "/etc/ssl/app.pem", "/from_unset_env", "/relative.txt", "/run/secrets/api.key", "/run/secrets/db", "/run/secrets/empty", "/run/secrets/external", "/run/secrets/undefined", "/settings"],
            service.Mounts.Select(m => m.Path));
        Assert.Equal(new ComposeMount("/run/secrets/db", "db", IsSecret: true), service.Mounts.Single(m => m.Name == "db"));
        var db = service.MountLayers("/run/secrets/db")[0];
        Assert.Equal("compose secret db", db.Source);
        Assert.Equal("secrets/db.txt", db.Location);
        Assert.Null(db.Value);
        Assert.True(db.IsSensitive);
        Assert.True(db.IsEffective);
        Assert.Equal("mounted at /run/secrets/db on service app; the file is not read", db.Note);
        Assert.Equal(new ComposeMountRef("db", IsSecret: true), db.Reference);
        var key = service.MountLayers("/run/secrets/api.key")[0];
        Assert.Equal("key-value", key.Value);
        Assert.Equal("compose.yaml:21", key.Location);
        Assert.Equal("mounted at /run/secrets/api.key on service app; from the variable API_KEY", key.Note);
        Assert.Equal("{}", service.MountLayers("/settings")[0].Value);
        Assert.False(service.MountLayers("/settings")[0].IsSensitive);
        Assert.Equal("Value: v2", service.MountLayers("/app/inline.txt")[0].Value);
        Assert.Equal("v2", service.MountLayers("/relative.txt")[0].Value);
        Assert.EndsWith("from the variable NOT_SET, which is not set", service.MountLayers("/from_unset_env")[0].Note, StringComparison.Ordinal);
        Assert.EndsWith("; it is external, so its contents are not known", service.MountLayers("/run/secrets/external")[0].Note, StringComparison.Ordinal);
        Assert.EndsWith("; its definition has no file, environment or content", service.MountLayers("/run/secrets/empty")[0].Note, StringComparison.Ordinal);
        Assert.EndsWith("; it is not defined", service.MountLayers("/run/secrets/undefined")[0].Note, StringComparison.Ordinal);
        Assert.EndsWith("; the file is not read", service.MountLayers("/etc/ssl/app.pem")[0].Note, StringComparison.Ordinal);
        Assert.Equal("compose.yaml:9: service app uses secret undefined, which is not defined under the top-level secrets.", Assert.Single(service.Warnings));
        Assert.Empty(service.MountLayers("/nope"));
        Assert.Single(service.MountLayers("\\run\\secrets\\db"));

        options.ReadSecretFiles = true;
        var read = Service(ComposeProject.Load([compose], options));
        Assert.Equal("s3cret\n", read.MountLayers("/run/secrets/db")[0].Value);
        Assert.EndsWith("; the file could not be read", read.MountLayers("/etc/ssl/app.pem")[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_and_a_config_at_one_path_lists_the_secret()
    {
        var service = Service(Parse("""
            services:
              app:
                secrets: [{ source: s, target: /x }]
                configs: [{ source: c, target: /x }]
            secrets:
              s: { content: s }
            configs:
              c: { content: c }
            """));

        Assert.True(Assert.Single(service.Mounts).IsSecret);
    }

    [Fact]
    public void Marks_values_with_secret_names_sensitive()
    {
        var service = Service(Parse("""
            services:
              app:
                environment:
                  DB_PASSWORD: x
                  URL: postgres://u:${DB_PASSWORD}@h
                  PLAIN: ${HOST}
                  CACHE_KEYPREFIX: k
            """, o =>
            {
                o.Variables["DB_PASSWORD"] = "p";
                o.Variables["HOST"] = "h";
            }));

        Assert.True(service.Layers("DB_PASSWORD")[0].IsSensitive);
        Assert.True(service.Layers("URL")[0].IsSensitive);
        Assert.True(service.Layers("URL")[0].Origins[0].IsSensitive);
        Assert.False(service.Layers("PLAIN")[0].IsSensitive);
        Assert.False(service.Layers("PLAIN")[0].Origins[0].IsSensitive);
        Assert.False(service.Layers("CACHE_KEYPREFIX")[0].IsSensitive);
    }

    [Fact]
    public void Configs_that_take_a_secret_named_variable_are_sensitive()
    {
        var service = Service(Parse("""
            services:
              app:
                configs: [from_env, from_content, plain]
            configs:
              from_env: { environment: DB_PASSWORD }
              from_content: { content: "password=${DB_PASSWORD}" }
              plain: { content: "host=${HOST}" }
            """, o =>
            {
                o.Variables["DB_PASSWORD"] = "hunter2";
                o.Variables["HOST"] = "h";
            }));

        Assert.True(service.MountLayers("/from_env")[0].IsSensitive);
        Assert.True(service.MountLayers("/from_content")[0].IsSensitive);
        Assert.False(service.MountLayers("/plain")[0].IsSensitive);
        Assert.DoesNotContain("hunter2", ConfigTracer.ForCompose(ComposeProject.Parse([("compose.yaml", "services:\n  app:\n    configs: [c]\nconfigs:\n  c: { environment: API_TOKEN }\n")]), "app").Trace().ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Values_that_take_a_secret_through_a_second_variable_are_sensitive()
    {
        using var folder = new TempFolder();
        folder.Write(".env", """
            DB_PASSWORD=fake-db-password
            DB_URL=postgres://app:${DB_PASSWORD}@db/app
            APP_CFG=pw=${DB_PASSWORD}
            API_TOKEN=fake-api-token
            ENV_DIR=cfg/${API_TOKEN}
            """);
        var compose = folder.Write("compose.yaml", """
            services:
              api:
                environment:
                  URL: ${DB_URL}
                configs: [app_cfg, env_cfg]
            configs:
              app_cfg: { content: "url=${DB_URL}" }
              env_cfg: { environment: APP_CFG }
            """);

        var project = ComposeProject.Load(compose);

        var service = Service(project, "api");
        Assert.True(service.Layers("URL")[0].IsSensitive);
        Assert.True(service.MountLayers("/app_cfg")[0].IsSensitive);
        Assert.True(service.MountLayers("/env_cfg")[0].IsSensitive);
        var report = ConfigTracer.ForCompose(project, "api").Trace();
        foreach (var output in new[] { report.ToText(), report.ToJson() })
        {
            Assert.DoesNotContain("fake-db-password", output, StringComparison.Ordinal);
            Assert.DoesNotContain("fake-api-token", output, StringComparison.Ordinal);
        }

        folder.Write("compose.yaml", "services:\n  api:\n    env_file: ${ENV_DIR}/app.env\n");
        var ex = Assert.Throws<FormatException>(() => ComposeProject.Load(compose));
        Assert.Equal("compose.yaml:3: a path or a name may not refer to a variable named like a secret (ENV_DIR).", ex.Message);
    }

    [Fact]
    public void A_secret_stays_sensitive_through_a_long_chain_of_variables()
    {
        using var folder = new TempFolder();
        folder.Write(".env", "DB_PASSWORD=fake-db-password\nV1=${DB_PASSWORD}\n" + string.Concat(Enumerable.Range(2, 8).Select(i => $"V{i}=${{V{i - 1}}}\n")));
        var compose = folder.Write("compose.yaml", "services:\n  app:\n    environment:\n      PLAIN: ${V9}\n");

        var project = ComposeProject.Load(compose);

        var step = Service(project).Layers("PLAIN")[0];
        Assert.Equal("fake-db-password", step.Value);
        Assert.True(step.IsSensitive);
        Assert.True(step.Origins[0].IsSensitive);
        Assert.DoesNotContain("fake-db-password", ConfigTracer.ForCompose(project, "app").Trace().ToText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("services:\n  app:\n    env_file: ${DB_PASSWORD}.envfile\n")]
    [InlineData("services:\n  app:\n    extends: ${API_KEY:-x}\n")]
    [InlineData("services:\n  app:\n    secrets: [s]\nsecrets:\n  s: { file: \"./${DB_PASSWORD}\" }\n")]
    public void Rejects_paths_and_names_that_take_a_secret_named_variable(string yaml)
    {
        var ex = Assert.Throws<FormatException>(() => Parse(yaml, o =>
        {
            o.Variables["DB_PASSWORD"] = "hunter2";
            o.Variables["API_KEY"] = "hunter2";
        }));

        Assert.Contains("a path or a name may not refer to a variable named like a secret", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_required_variable_message_is_shown_as_written()
    {
        var ex = Assert.Throws<FormatException>(() => Parse("services:\n  app:\n    environment:\n      A: ${MISSING:?needs $DB_PASSWORD}\n", o => o.Variables["DB_PASSWORD"] = "hunter2"));

        Assert.Equal("compose.yaml:4: MISSING: needs $DB_PASSWORD.", ex.Message);
    }

    [Fact]
    public void Includes_other_compose_projects()
    {
        using var folder = new TempFolder();
        folder.Write("db/compose.yaml", "services:\n  db:\n    environment:\n      PORT: ${PORT}\n      NAME: ${NAME:-none}\n");
        folder.Write("db/.env", "PORT=5432\n");
        folder.Write("cache/compose.yaml", "services:\n  cache:\n    environment: { MODE: $MODE }\n");
        folder.Write("cache.envfile", "MODE=lru\n");
        var compose = folder.Write("compose.yaml", """
            include:
              - db/compose.yaml
              - path: [cache/compose.yaml]
                env_file: cache.envfile
                project_directory: cache
              - missing/compose.yaml
            services:
              app:
                environment: { A: 1 }
            """);

        var project = ComposeProject.Load(compose);

        Assert.Equal(["app", "db", "cache"], project.ServiceNames);
        var db = Service(project, "db");
        Assert.Equal("5432", db.Environment["PORT"]);
        Assert.Equal("db/compose.yaml:4", db.Layers("PORT")[0].Location);
        Assert.Equal("db/.env", db.Layers("PORT")[0].Origins[0].Source);
        Assert.Equal("lru", Service(project, "cache").Environment["MODE"]);
        Assert.Equal("compose.yaml:6: the included file missing/compose.yaml does not exist, so its services are not listed.", Assert.Single(project.Warnings));
    }

    [Fact]
    public void An_include_resolves_paths_against_its_project_directory_and_keeps_the_options()
    {
        using var folder = new TempFolder();
        folder.Write("cache/compose.yaml", "services:\n  cache:\n    env_file: cache/mode.envfile\n    environment: { TAG: $TAG }\n    secrets: [s]\nsecrets:\n  s: { file: cache/s.txt }\n---\nx: 1\n");
        folder.Write("cache/mode.envfile", "MODE=lru\n");
        folder.Write("cache/s.txt", "secret");
        var compose = folder.Write("compose.yaml", "include:\n  - path: cache/compose.yaml\n    project_directory: .\n");
        var options = new ComposeOptions { ReadSecretFiles = true, UseProcessEnvironment = false };
        options.Variables["TAG"] = "v3";

        var project = ComposeProject.Load([compose], options);

        var cache = Service(project, "cache");
        Assert.Equal("lru", cache.Environment["MODE"]);
        Assert.Equal("v3", cache.Environment["TAG"]);
        Assert.Equal("secret", cache.MountLayers("/run/secrets/s")[0].Value);
        Assert.Equal("cache/compose.yaml holds 2 YAML documents; only the first is read.", Assert.Single(project.Warnings));
    }

    [Fact]
    public void Rejects_an_include_that_redefines_a_service_or_includes_itself()
    {
        using var folder = new TempFolder();
        folder.Write("other.yaml", "services:\n  app: {}\n");
        folder.Write("loop.yaml", "include: [compose.yaml]\n");
        var clash = folder.Write("compose.yaml", "include: [other.yaml]\nservices:\n  app: {}\n");

        Assert.Equal("compose.yaml:1: the include brings in service app, which is already defined.", Assert.Throws<FormatException>(() => ComposeProject.Load(clash)).Message);

        var loop = folder.Write("start.yaml", "include: [loop.yaml]\n");
        folder.Write("loop.yaml", "include: [start.yaml]\n");
        Assert.Equal("loop.yaml:1: start.yaml includes itself.", Assert.Throws<FormatException>(() => ComposeProject.Load(loop)).Message);
    }

    [Fact]
    public void Rejects_includes_nested_too_deep()
    {
        using var folder = new TempFolder();
        for (var i = 0; i < 10; i++)
        {
            folder.Write($"c{i}.yaml", $"include: [c{i + 1}.yaml]\n");
        }

        folder.Write("c10.yaml", "services: {}\n");

        var ex = Assert.Throws<FormatException>(() => ComposeProject.Load(Path.Join(folder.Path, "c0.yaml")));
        Assert.Equal("c8.yaml:1: includes nest more than 8 deep.", ex.Message);
    }

    [Fact]
    public void Shows_paths_outside_the_project_directory_in_full()
    {
        using var folder = new TempFolder();
        var outside = folder.Write("outside.envfile", "A=1");
        var compose = folder.Write("project/compose.yaml", $"services:\n  app:\n    env_file: {outside.Replace('\\', '/')}\n");

        var service = Service(ComposeProject.Load([compose], new ComposeOptions { AllowFilesOutsideProject = true }));

        Assert.Equal("env_file " + outside.Replace('\\', '/'), service.Layers("A")[0].Source);
    }

    [Fact]
    public void Does_not_read_an_env_file_outside_the_project_directory_by_default()
    {
        using var folder = new TempFolder();
        var outside = folder.Write("outside.envfile", "A=1");
        var compose = folder.Write("project/compose.yaml", $"services:\n  app:\n    env_file: {outside.Replace('\\', '/')}\n");

        var service = Service(ComposeProject.Load(compose));

        Assert.Empty(service.Layers("A"));
        Assert.Equal(
            $"compose.yaml:3: env_file {outside.Replace('\\', '/')} is outside the project directory, so it is not read; set AllowFilesOutsideProject (--compose-allow-files-outside-project) to read it.",
            Assert.Single(service.Warnings));
    }

    [Fact]
    public void Reads_files_outside_the_project_directory_only_when_allowed()
    {
        using var folder = new TempFolder();
        var outsideText = folder.Write("outside.txt", "fake-outside-config");
        folder.Write("outside.envfile", "FROM_ENV_FILE=fake-outside-env-file");
        folder.Write("outside-base.yaml", "services:\n  base:\n    environment: { FROM_EXTENDS: fake-outside-extends }\n");
        folder.Write("outside-include.yaml", "services:\n  other:\n    environment: { FROM_INCLUDE: fake-outside-include }\n");
        var compose = folder.Write("project/compose.yaml", $$"""
            include:
              - ../outside-include.yaml
            services:
              app:
                extends: { file: ../outside-base.yaml, service: base }
                env_file: ../outside.envfile
                configs: [up, absolute]
            configs:
              up: { file: ../outside.txt }
              absolute: { file: "{{outsideText.Replace('\\', '/')}}" }
            """);
        string Shown(string name) => Path.Join(folder.Path, name).Replace('\\', '/');
        const string Allow = "is outside the project directory, so it is not read; set AllowFilesOutsideProject (--compose-allow-files-outside-project) to read it.";

        var project = ComposeProject.Load(compose);

        var service = Service(project);
        Assert.Equal(["app"], project.ServiceNames);
        Assert.Empty(service.Layers("FROM_EXTENDS"));
        Assert.Empty(service.Layers("FROM_ENV_FILE"));
        Assert.Null(service.MountLayers("/up")[0].Value);
        Assert.Null(service.MountLayers("/absolute")[0].Value);
        Assert.Equal("mounted at /up on service app; the file is outside the project directory, so it is not read", service.MountLayers("/up")[0].Note);
        Assert.Equal($"compose.yaml:2: the included file {Shown("outside-include.yaml")} {Allow}", Assert.Single(project.Warnings));
        Assert.Equal(
            [
                $"compose.yaml:5: the extends file {Shown("outside-base.yaml")} {Allow}",
                $"compose.yaml:6: env_file {Shown("outside.envfile")} {Allow}",
                $"compose.yaml:9: the config file {Shown("outside.txt")} {Allow}",
                $"compose.yaml:10: the config file {Shown("outside.txt")} {Allow}",
            ],
            service.Warnings);
        Assert.DoesNotContain("fake-outside", ConfigTracer.ForCompose(project, "app").Trace().ToText(), StringComparison.Ordinal);

        var allowed = ComposeProject.Load([compose], new ComposeOptions { AllowFilesOutsideProject = true });

        var app = Service(allowed);
        Assert.Equal(["app", "other"], allowed.ServiceNames);
        Assert.Equal("fake-outside-extends", app.Environment["FROM_EXTENDS"]);
        Assert.Equal("fake-outside-env-file", app.Environment["FROM_ENV_FILE"]);
        Assert.Equal("fake-outside-config", app.MountLayers("/up")[0].Value);
        Assert.Equal("fake-outside-config", app.MountLayers("/absolute")[0].Value);
        Assert.Equal("fake-outside-include", Service(allowed, "other").Environment["FROM_INCLUDE"]);
        Assert.Empty(allowed.Warnings);
        Assert.Empty(app.Warnings);
    }

    [Fact]
    public void Does_not_follow_a_link_out_of_the_project_directory_by_default()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        using var folder = new TempFolder();
        var outsideText = folder.Write("outside.txt", "fake-outside-config");
        var outsideEnv = folder.Write("outside.env", "TAG=fake-outside-tag\n");
        var compose = folder.Write("project/compose.yaml", "services:\n  app:\n    configs: [linked]\n    environment: { TAG: $TAG }\nconfigs:\n  linked: { file: link.txt }\n");
        File.CreateSymbolicLink(Path.Join(folder.Path, "project", "link.txt"), outsideText);
        File.CreateSymbolicLink(Path.Join(folder.Path, "project", ".env"), outsideEnv);

        var project = ComposeProject.Load(compose);

        var service = Service(project);
        Assert.Null(service.MountLayers("/linked")[0].Value);
        Assert.Equal("", service.Environment["TAG"]);
        Assert.Empty(project.Environment.Files);
        Assert.Equal("The env file .env is outside the project directory, so it is not read; set AllowFilesOutsideProject (--compose-allow-files-outside-project) to read it.", project.Warnings[0]);
        Assert.Contains("compose.yaml:6: the config file link.txt is outside the project directory, so it is not read; set AllowFilesOutsideProject (--compose-allow-files-outside-project) to read it.", service.Warnings);

        var allowed = Service(ComposeProject.Load([compose], new ComposeOptions { AllowFilesOutsideProject = true }));
        Assert.Equal("fake-outside-config", allowed.MountLayers("/linked")[0].Value);
        Assert.Equal("fake-outside-tag", allowed.Environment["TAG"]);
    }

    [Fact]
    public void An_include_with_its_own_project_directory_does_not_widen_what_is_read()
    {
        using var folder = new TempFolder();
        folder.Write(".env", "FROM_DOTENV=fake-outside-dotenv\n");
        folder.Write("outside.envfile", "FROM_ENV_FILE=fake-outside-env-file\n");
        folder.Write("project/sub/compose.yaml", "services:\n  sub:\n    env_file: outside.envfile\n    environment: { FROM_DOTENV: $FROM_DOTENV }\n");
        folder.Write("project/other/compose.yaml", "services:\n  other:\n    environment: { FROM_ENV_FILE: $FROM_ENV_FILE }\n");
        var compose = folder.Write("project/compose.yaml", """
            include:
              - path: sub/compose.yaml
                project_directory: ..
              - path: other/compose.yaml
                env_file: ../outside.envfile
            """);

        var project = ComposeProject.Load(compose);

        var sub = Service(project, "sub");
        Assert.Equal("", sub.Environment["FROM_DOTENV"]);
        Assert.Empty(sub.Layers("FROM_ENV_FILE"));
        Assert.Contains(sub.Warnings, w => w.Contains("env_file " + Path.Join(folder.Path, "outside.envfile").Replace('\\', '/') + " is outside the project directory", StringComparison.Ordinal));
        Assert.Equal("", Service(project, "other").Environment["FROM_ENV_FILE"]);
        Assert.Equal(
            [
                $"The env file {Path.Join(folder.Path, ".env").Replace('\\', '/')} is outside the project directory, so it is not read; set AllowFilesOutsideProject (--compose-allow-files-outside-project) to read it.",
                $"The env file {Path.Join(folder.Path, "outside.envfile").Replace('\\', '/')} is outside the project directory, so it is not read; set AllowFilesOutsideProject (--compose-allow-files-outside-project) to read it.",
            ],
            project.Warnings.Where(w => w.StartsWith("The env file", StringComparison.Ordinal)));

        var allowed = ComposeProject.Load([compose], new ComposeOptions { AllowFilesOutsideProject = true });
        Assert.Equal("fake-outside-dotenv", Service(allowed, "sub").Environment["FROM_DOTENV"]);
        Assert.Equal("fake-outside-env-file", Service(allowed, "sub").Environment["FROM_ENV_FILE"]);
        Assert.Equal("fake-outside-env-file", Service(allowed, "other").Environment["FROM_ENV_FILE"]);
    }

    [Fact]
    public void Reads_only_regular_files()
    {
        using var folder = new TempFolder();
        folder.Write("folder.envfile/x", "A=1");
        folder.Write("config.d/x", "x");
        folder.Write("empty.txt", "");
        var compose = folder.Write("compose.yaml", "services:\n  app:\n    env_file: folder.envfile\n    configs: [dir, empty]\nconfigs:\n  dir: { file: config.d }\n  empty: { file: empty.txt }\n");

        foreach (var allow in new[] { false, true })
        {
            var service = Service(ComposeProject.Load([compose], new ComposeOptions { AllowFilesOutsideProject = allow }));

            Assert.Null(service.MountLayers("/dir")[0].Value);
            Assert.Equal("mounted at /dir on service app; the file is not a regular file, so it is not read", service.MountLayers("/dir")[0].Note);
            Assert.Equal("", service.MountLayers("/empty")[0].Value);
            Assert.Equal(
                [
                    "compose.yaml:3: env_file folder.envfile is not a regular file, so it is not read.",
                    "compose.yaml:6: the config file config.d is not a regular file, so it is not read.",
                ],
                service.Warnings);
        }
    }

    [Fact]
    public void Reports_a_link_whose_target_is_missing_as_a_missing_file()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        using var folder = new TempFolder();
        var compose = folder.Write("compose.yaml", "include:\n  - inc.yaml\nservices:\n  app:\n    env_file: app.env\n    configs: [c]\nconfigs:\n  c: { file: cfg.txt }\n");
        File.CreateSymbolicLink(Path.Join(folder.Path, "app.env"), "missing.env");
        File.CreateSymbolicLink(Path.Join(folder.Path, "cfg.txt"), "missing.txt");
        File.CreateSymbolicLink(Path.Join(folder.Path, "inc.yaml"), "missing.yaml");

        var project = ComposeProject.Load(compose);

        var service = Service(project);
        Assert.Equal("compose.yaml:2: the included file inc.yaml does not exist, so its services are not listed.", Assert.Single(project.Warnings));
        Assert.Equal("compose.yaml:5: env_file app.env does not exist (or is larger than 1 MiB); docker compose stops with an error when a required env_file is missing.", Assert.Single(service.Warnings));
        Assert.Null(service.MountLayers("/c")[0].Value);
        Assert.EndsWith("; the file could not be read", service.MountLayers("/c")[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Passes_over_a_dotenv_link_whose_target_is_missing()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        using var folder = new TempFolder();
        var compose = folder.Write("compose.yaml", "services:\n  app:\n    environment:\n      A: ${A:-default}\n");
        File.CreateSymbolicLink(Path.Join(folder.Path, ".env"), "missing.env");

        var project = ComposeProject.Load(compose);

        Assert.Empty(project.Environment.Files);
        Assert.Equal("default", Service(project).Environment["A"]);
    }

    [Fact]
    public void Reads_a_link_through_a_linked_folder_the_way_the_file_system_does()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        // d/l is sub/deep/l, which leads to ../../real.txt from sub/deep: real.txt in the project, not next to it.
        using var folder = new TempFolder();
        folder.Write("project/real.txt", "fake-real-content");
        Directory.CreateDirectory(Path.Join(folder.Path, "project", "sub", "deep"));
        Directory.CreateSymbolicLink(Path.Join(folder.Path, "project", "d"), Path.Join("sub", "deep"));
        File.CreateSymbolicLink(Path.Join(folder.Path, "project", "sub", "deep", "l"), Path.Join("..", "..", "real.txt"));
        var compose = folder.Write("project/compose.yaml", "services:\n  app:\n    configs: [c]\nconfigs:\n  c: { file: d/l }\n");

        var service = Service(ComposeProject.Load(compose));

        Assert.Equal("fake-real-content", service.MountLayers("/c")[0].Value);
        Assert.Empty(service.Warnings);
    }

    [Fact(Timeout = 60_000)]
    public async Task Takes_a_fifo_as_empty_without_waiting_for_a_writer()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows has no FIFOs in the file system.
        }

        var ct = TestContext.Current.CancellationToken;
        using var folder = new TempFolder();
        var compose = folder.Write("compose.yaml", "services:\n  app:\n    env_file: pipe\n    configs: [c]\nconfigs:\n  c: { file: pipe }\n");
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", Path.Join(folder.Path, "pipe"))!)
        {
            await mkfifo.WaitForExitAsync(ct);
            Assert.Equal(0, mkfifo.ExitCode);
        }

        var project = await Task.Run(() => ComposeProject.Load(compose), ct);

        var service = Service(project);
        Assert.Equal("", service.MountLayers("/c")[0].Value);
        Assert.Empty(service.Warnings);
    }

    [Fact]
    public void Refuses_a_path_on_another_root_without_looking_at_it()
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        Assert.True(ComposeProject.SharesRoot(Path.Join(temp, "project", "a.env"), Path.Join(temp, "project")));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(ComposeProject.SharesRoot(@"c:\project\a.env", @"C:\project"));
            Assert.False(ComposeProject.SharesRoot(@"\\attacker\share\a.env", @"C:\project"));
            Assert.False(ComposeProject.SharesRoot(Path.GetFullPath(Path.Join(@"//attacker/share", ".env")), @"C:\project"));
            Assert.False(ComposeProject.SharesRoot(@"\\.\pipe\a", @"C:\project"));
            Assert.False(ComposeProject.SharesRoot(@"\\?\C:\project\a.env", @"C:\project"));
            Assert.False(ComposeProject.SharesRoot(@"D:\project\a.env", @"C:\project"));
        }
    }

    [Fact]
    public void Validates_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => ComposeProject.Load(null!, new ComposeOptions()));
        Assert.Throws<ArgumentException>(() => ComposeProject.Load());
        Assert.Throws<ArgumentNullException>(() => ComposeProject.Parse(null!));
        Assert.Throws<ArgumentNullException>(() => Parse("").FindService(null!));
        var service = Service(Parse("services:\n  app: {}\n"));
        Assert.Throws<ArgumentNullException>(() => service.Layers(null!));
        Assert.Throws<ArgumentNullException>(() => service.MountLayers(null!));
        Assert.Empty(service.Environment);
        Assert.Equal("app", service.Name);
    }
}
