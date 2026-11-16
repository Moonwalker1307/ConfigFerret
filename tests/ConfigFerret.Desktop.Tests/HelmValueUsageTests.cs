using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.Tests;

public class HelmValueUsageTests
{
    private static List<string> Dead(string template, string values, params string[] subcharts) =>
        HelmValueUsage.FindDead([template], HelmValueUsage.Leaves(values).Select(p => (p, "values.yaml")), subcharts).Select(d => d.Path).ToList();

    [Fact]
    public void Values_no_template_reads_are_dead()
    {
        var dead = Dead(
            "image: {{ .Values.image.repository }}:{{ $.Values.image.tag }}\n{{- with .Values.env }}{{ toYaml . }}{{ end }}",
            "image:\n  repository: x\n  tag: y\n  pullPolicy: Always\nenv:\n  A: 1\n  B: 2\nlegacy:\n  enabled: true\nglobal:\n  region: eu\nredis:\n  port: 1\n",
            "redis");

        Assert.Equal(["image.pullPolicy", "legacy.enabled"], dead);
    }

    [Fact]
    public void A_template_reading_a_value_below_a_list_or_with_index_counts()
    {
        var dead = Dead(
            """{{ index .Values "feature-flags" "beta" }} {{ index .Values.db "host" }} {{ range .Values.hosts }}{{ . }}{{ end }}""",
            "feature-flags:\n  beta: true\n  gamma: false\ndb:\n  host: h\nhosts: [a, b]\n");

        Assert.Equal(["feature-flags.gamma"], dead);
    }

    [Fact]
    public void A_bare_Values_reads_everything()
    {
        Assert.Empty(Dead("{{ toYaml .Values }}", "a: 1\nb:\n  c: 2\n"));
        Assert.Empty(Dead("{{ index .Values $key }}", "a: 1\n"));
    }

    [Fact]
    public void Reads_the_chart_and_its_values_files()
    {
        var dead = HelmValueUsage.FindDead(OrdersRepo.Full("deploy/chart"), [OrdersRepo.Full("deploy/values-prod.yaml")]);

        Assert.Equal(["features.recommendations"], dead.Select(d => d.Path).Distinct());
        Assert.Contains(dead, d => d.File == "values.yaml");
        Assert.Contains(dead, d => d.File == "values-prod.yaml");
    }

    [Fact]
    public void Rejects_missing_arguments()
    {
        Assert.Throws<ArgumentException>(() => HelmValueUsage.FindDead(" ", []));
        Assert.Throws<ArgumentNullException>(() => HelmValueUsage.FindDead("chart", null!));
        Assert.Throws<ArgumentNullException>(() => HelmValueUsage.FindDead(null!, [], []));
        Assert.Throws<ArgumentNullException>(() => HelmValueUsage.FindDead([], null!, []));
        Assert.Throws<ArgumentNullException>(() => HelmValueUsage.FindDead([], [], null!));
    }

    [Fact]
    public void A_name_that_only_starts_like_another_is_not_related_to_it()
    {
        Assert.Equal(["logLevel"], Dead("{{ .Values.log }}", "log: 1\nlogLevel: debug\n"));
        Assert.Equal(["log"], Dead("{{ .Values.logLevel }}", "log: 1\nlogLevel: debug\n"));
        Assert.Equal(["b.c"], Dead("{{ .Values.a }}", "a: 1\nb:\n  c: 2\n"));
    }

    [Fact]
    public void A_chart_without_a_templates_folder_has_no_dead_values()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\n");
        temp.Write("notes.txt", "{{ .Values.b }}");

        Assert.Empty(HelmValueUsage.FindDead(temp.Path, []));
    }

    [Fact]
    public void Reads_every_file_under_templates_including_nested_ones()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\nb: 2\nc: 3\nd: 4\n");
        temp.Write("templates/deployment.yaml", "{{ .Values.a }}");
        temp.Write("templates/_helpers.tpl", "{{ .Values.b }}");
        temp.Write("templates/sub/deeper/cm.yaml", "{{ .Values.c }}");

        Assert.Equal(["d"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
    }

    [Fact]
    public void Reads_a_template_of_exactly_two_megabytes_and_gives_no_dead_values_with_a_bigger_one()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\nb: 2\nc: 3\n");
        const string Exact = "{{ .Values.a }}";
        temp.Write("templates/exact.yaml", Exact + new string(' ', 2 * 1024 * 1024 - Exact.Length));
        Assert.Equal(["b", "c"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));

        temp.Write("templates/over.yaml", "{{ .Values.b }}" + new string(' ', 2 * 1024 * 1024));
        Assert.Equal("templates/over.yaml is bigger than 2 MB", PartlyRead(temp));
    }

    // Why the chart wasn't read in full, from the note of a scan that must give no dead values.
    private static string PartlyRead(TempFolder temp, params string[] valuesFiles)
    {
        var dead = HelmValueUsage.FindDead(temp.Path, valuesFiles.Select(temp.Full), "Helm chart app", out var notes);

        Assert.Empty(dead);
        var note = Assert.Single(notes);
        const string Start = "Helm chart app wasn't read in full: ";
        const string End = ". So no Helm value is marked dead, as the part not read may read any of them.";
        Assert.StartsWith(Start, note, StringComparison.Ordinal);
        Assert.EndsWith(End, note, StringComparison.Ordinal);
        return note[Start.Length..^End.Length];
    }

    [Fact]
    public void Does_not_follow_symbolic_links_in_templates()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var outside = new TempFolder();
        outside.Write("linked.yaml", "{{ .Values.a }}");
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\n");
        temp.Write("templates/own.yaml", "kind: ConfigMap");
        Directory.CreateSymbolicLink(temp.Full("templates/link"), outside.Path);
        File.CreateSymbolicLink(temp.Full("templates/file.yaml"), outside.Full("linked.yaml"));

        Assert.Matches("^templates/(link|file.yaml) is a link$", PartlyRead(temp));
    }

    [Fact]
    public void A_chart_with_more_than_2000_files_to_read_has_no_dead_values()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\n");
        // values.yaml is one of them.
        for (var i = 0; i < 1_999; i++)
        {
            temp.Write($"templates/t{i}.yaml", "kind: ConfigMap");
        }

        Assert.Equal(["a"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
        temp.Write("templates/t1999.yaml", "kind: ConfigMap");
        Assert.Equal("it holds more than 2,000 files to read", PartlyRead(temp));
    }

    [Fact]
    public void A_chart_with_more_than_32_megabytes_to_read_has_no_dead_values()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\n");
        for (var i = 0; i < 17; i++)
        {
            temp.Write($"templates/t{i}.yaml", "kind: ConfigMap\n#" + new string('x', 2_000_000));
        }

        Assert.Equal("it holds more than 32 MB to read", PartlyRead(temp));
    }

    [Theory]
    [InlineData("Chart.yaml")]
    [InlineData("requirements.yaml")]
    public void Chart_yaml_and_requirements_yaml_count_toward_the_32_megabytes(string chartFile)
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\n");
        for (var i = 0; i < 16; i++)
        {
            temp.Write($"templates/t{i}.yaml", "kind: ConfigMap\n#" + new string('x', 2_000_000));
        }

        temp.Write(chartFile, "apiVersion: v2\n");
        Assert.Equal(["a"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));

        temp.Write(chartFile, "apiVersion: v2\n#" + new string('x', 1_600_000));
        Assert.Equal("it holds more than 32 MB to read", PartlyRead(temp));
    }

    [Fact]
    public void A_folder_that_can_t_be_listed_gives_no_dead_values()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\n");
        temp.Write("templates/hidden/cm.yaml", "{{ .Values.a }}");
        File.SetUnixFileMode(temp.Full("templates/hidden"), UnixFileMode.None);
        try
        {
            // Root can list it anyway.
            try
            {
                _ = Directory.GetFiles(temp.Full("templates/hidden"));
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            Assert.Equal("a folder of it can't be listed", PartlyRead(temp));
        }
        finally
        {
            File.SetUnixFileMode(temp.Full("templates/hidden"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void A_link_among_the_templates_of_a_subchart_gives_no_dead_values()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var outside = new TempFolder();
        outside.Write("_lib.tpl", "{{- define \"lib.x\" -}}{{ .Values.a }}{{- end }}");
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\n");
        temp.Write("templates/cm.yaml", "{{ include \"lib.x\" . }}");
        temp.Write("charts/lib/Chart.yaml", "apiVersion: v2\nname: lib\n");
        Directory.CreateSymbolicLink(temp.Full("charts/lib/templates"), outside.Path);

        Assert.Equal("charts/lib/templates is a link", PartlyRead(temp));
    }

    [Fact]
    public void Values_for_subcharts_count_as_read()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "postgres:\n  x: 1\nredis:\n  y: 1\nkafka:\n  z: 1\ncache:\n  w: 1\nglobal:\n  g: 1\ntemplates:\n  enabled: true\nother:\n  o: 1\n");
        temp.Write("templates/cm.yaml", "kind: ConfigMap");
        temp.Write("charts/postgres/Chart.yaml", "name: postgres");
        temp.Write("charts/redis-17.0.1.tgz", "x");
        temp.Write("Chart.yaml", "apiVersion: v2\ndependencies:\n  - name: kafka\n    version: 1.0.0\n  - name: kafka-ui\n    alias: cache\n");

        var dead = HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path).ToList();

        Assert.Equal(["templates.enabled", "other.o"], dead);
    }

    [Fact]
    public void Without_a_charts_folder_or_Chart_yaml_nothing_is_a_subchart()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "templates:\n  enabled: true\nChart:\n  x: 1\n");
        temp.Write("templates/cm.yaml", "kind: ConfigMap");

        Assert.Equal(["templates.enabled", "Chart.x"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
    }

    [Fact]
    public void Names_values_files_by_where_they_are()
    {
        using var temp = new TempFolder();
        temp.Write("chart/values.yaml", "a: 1\n");
        temp.Write("chart/templates/cm.yaml", "kind: ConfigMap");
        temp.Write("chart/env/values-eu.yaml", "b: 1\n");
        temp.Write("elsewhere/values-us.yaml", "c: 1\n");
        temp.Write("chart/broken.yaml", "a: [1, 2\n");
        temp.Write("chart/not-a-map.yaml", "- one\n- two\n");

        var dead = HelmValueUsage.FindDead(
            temp.Full("chart"),
            [temp.Full("chart/env/values-eu.yaml"), temp.Full("elsewhere/values-us.yaml"), temp.Full("chart/missing.yaml"), temp.Full("chart/broken.yaml"), temp.Full("chart/not-a-map.yaml"), temp.Full("chart/values.yaml")]);

        Assert.Equal([("a", "values.yaml"), ("b", "env/values-eu.yaml"), ("c", "values-us.yaml")], dead.Select(d => (d.Path, d.File)));
    }

    [Fact]
    public void Keeps_a_backslash_in_a_values_file_name_as_part_of_the_name()
    {
        // On Windows a backslash is the separator, so no name can hold one.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempFolder();
        temp.Write("templates/cm.yaml", "kind: ConfigMap");
        temp.Write("env/values-eu.yaml", "a: 1\n");
        temp.Write("env\\values-eu.yaml", "a: 1\n");

        var dead = HelmValueUsage.FindDead(temp.Path, [temp.Full("env/values-eu.yaml"), temp.Full("env\\values-eu.yaml")]);

        Assert.Equal([("a", "env/values-eu.yaml"), ("a", "env\\values-eu.yaml")], dead.Select(d => (d.Path, d.File)));
    }

    [Fact]
    public void Lists_a_value_once_per_file_that_sets_it()
    {
        var dead = HelmValueUsage.FindDead(
            ["kind: ConfigMap"],
            [("a", "one.yaml"), ("a", "one.yaml"), ("a", "two.yaml")],
            []);

        Assert.Equal([("a", "one.yaml"), ("a", "two.yaml")], dead.Select(d => (d.Path, d.File)));
    }

    [Fact]
    public void Reads_dependency_names_from_a_Chart_yaml_with_Windows_line_endings()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "kafka:\n  z: 1\ncache:\n  w: 1\nquoted:\n  q: 1\nother:\n  o: 1\n");
        temp.Write("templates/cm.yaml", "kind: ConfigMap");
        temp.Write("Chart.yaml", "apiVersion: v2\r\ndependencies:\r\n  - name: kafka \r\n  -   name: kafka-ui\r\n      alias: cache\r\n  - name: \"quoted\"\r\n");

        Assert.Equal(["other.o"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
    }

    [Fact]
    public void A_Chart_yaml_over_two_megabytes_gives_no_dead_values()
    {
        // Without its dependencies the scan can't tell a subchart's values from the chart's own.
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\n");
        temp.Write("templates/cm.yaml", "kind: ConfigMap");
        temp.Write("Chart.yaml", "apiVersion: v2\n#" + new string('x', 2 * 1024 * 1024));

        Assert.Empty(HelmValueUsage.FindDead(temp.Path, []));
    }

    [Fact]
    public void A_values_file_over_two_megabytes_gives_no_dead_values()
    {
        // tpl may render a string of it that reads another value.
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\n");
        temp.Write("templates/cm.yaml", "kind: ConfigMap");
        temp.Write("values-big.yaml", "b: 1\n#" + new string('x', 2 * 1024 * 1024));

        Assert.Equal("values-big.yaml is bigger than 2 MB", PartlyRead(temp, "values-big.yaml"));
    }

    [Fact]
    public void More_than_100000_values_are_a_YamlException()
    {
        Assert.Equal(100_000, HelmValueUsage.Leaves(Aliases(99)).Count());
        Assert.ThrowsAny<YamlDotNet.Core.YamlException>(() => HelmValueUsage.Leaves(Aliases(100)).ToList());
    }

    [Fact]
    public void Reads_dependency_names_quoted_with_a_comment_and_from_requirements_yaml()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "redis:\n  x: 1\nkafka:\n  y: 1\npostgres:\n  z: 1\nother:\n  o: 1\n");
        temp.Write("templates/cm.yaml", "kind: ConfigMap");
        temp.Write("Chart.yaml", "apiVersion: v2\ndependencies:\n  - name: 'redis' # the cache\n  - name: kafka\t# the queue\n");
        temp.Write("requirements.yaml", "dependencies:\n- name: postgres\n");

        Assert.Equal(["other.o"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
    }

    [Theory]
    [InlineData("dependencies: [{name: redis, version: 1.0.0}]\n", "redis")]
    [InlineData("dependencies:\n  - name:\n      redis\n", "redis")]
    [InlineData("dependencies:\n  - name: redis-ha\n    alias: redis\n", "redis")]
    [InlineData("dependencies:\n- {name: \"redis\"}\n", "redis")]
    public void Reads_dependency_names_in_any_YAML_form(string chart, string subchart)
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", $"{subchart}:\n  auth:\n    password: not-a-real-secret\nother: 1\n");
        temp.Write("templates/cm.yaml", "kind: ConfigMap");
        temp.Write("Chart.yaml", "apiVersion: v2\nname: app\n" + chart);

        Assert.Equal(["other"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
    }

    [Theory]
    [InlineData("dependencies: [{name: redis\n")]
    [InlineData("dependencies: redis\n")]
    [InlineData("dependencies:\n  - redis\n")]
    public void A_Chart_yaml_whose_dependencies_can_t_be_read_gives_no_dead_values(string chart)
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "redis:\n  x: 1\nother: 1\n");
        temp.Write("templates/cm.yaml", "kind: ConfigMap");
        temp.Write("Chart.yaml", "apiVersion: v2\nname: app\n" + chart);

        Assert.Empty(HelmValueUsage.FindDead(temp.Path, []));
    }

    [Fact]
    public void Does_not_read_values_files_or_Chart_yaml_through_links()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var outside = new TempFolder();
        outside.Write("values.yaml", "outside: 1\n");
        outside.Write("Chart.yaml", "apiVersion: v2\nname: x\n");
        using var temp = new TempFolder();
        temp.Write("templates/cm.yaml", "kind: ConfigMap");
        temp.Write("values-prod.yaml", "a: 1\n");
        File.CreateSymbolicLink(temp.Full("values.yaml"), outside.Full("values.yaml"));
        File.CreateSymbolicLink(temp.Full("values-eu.yaml"), outside.Full("values.yaml"));

        Assert.Equal("values.yaml is a link", PartlyRead(temp, "values-prod.yaml", "values-eu.yaml"));
        File.Delete(temp.Full("values.yaml"));
        Assert.Equal("values-eu.yaml is a link", PartlyRead(temp, "values-prod.yaml", "values-eu.yaml"));
        Assert.Equal(["a"], HelmValueUsage.FindDead(temp.Path, [temp.Full("values-prod.yaml")]).Select(d => d.Path));

        // Without the dependencies a subchart's values can't be told from the chart's own.
        File.CreateSymbolicLink(temp.Full("Chart.yaml"), outside.Full("Chart.yaml"));
        Assert.Empty(HelmValueUsage.FindDead(temp.Path, [temp.Full("values-prod.yaml")]));
    }

    [Theory]
    [InlineData("templates")]
    [InlineData("charts")]
    public void A_templates_or_charts_folder_that_is_a_link_gives_no_dead_values(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var outside = new TempFolder();
        outside.Write("cm.yaml", "kind: ConfigMap");
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\n");
        if (folder != "templates")
        {
            temp.Write("templates/own.yaml", "kind: ConfigMap");
        }

        Directory.CreateSymbolicLink(temp.Full(folder), outside.Path);

        Assert.Empty(HelmValueUsage.FindDead(temp.Path, []));
    }

    [Fact]
    public async Task Does_not_open_a_device_that_reports_no_length()
    {
        if (!File.Exists("/dev/zero"))
        {
            return;
        }

        using var temp = new TempFolder();
        temp.Write("templates/cm.yaml", "kind: ConfigMap");
        temp.Write("values-prod.yaml", "a: 1\n");
        var scan = Task.Factory.StartNew(() => HelmValueUsage.FindDead(temp.Path, ["/dev/zero", temp.Full("values-prod.yaml")]), TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var finished = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) == scan;

        Assert.True(finished, "Reading /dev/zero as a values file did not end.");
        Assert.Equal(["a"], (await scan).Select(d => d.Path));
    }

    [Fact]
    public void Leaves_out_a_value_whose_path_is_longer_than_256_characters()
    {
        var key = new string('k', 200);

        Assert.Equal(["a", key + ".b"], HelmValueUsage.Leaves($"a: 1\n{new string('l', 257)}: 1\n{key}:\n  b: 1\n  {new string('c', 56)}: 1\n"));
    }

    // Blank lines in Chart.yaml took hours with the dependency pattern that let blanks run across lines, a long key above
    // many values or values that aliases repeat took minutes and gigabytes before long paths were left out and the
    // values counted. The other Chart.yaml shapes guard the YAML read of the dependencies (they aren't YAML, so they give
    // no dead values), and the template shapes guard the patterns that were already linear, so a change can't make them
    // try a long text in many ways. Each takes about a second now, and under three on a machine kept busy by other
    // builds, while the old scan took minutes to an hour. The limit is a minute, so a slow runner that runs several test
    // assemblies at once stays under it and a scan that is no longer linear does not. The scan runs on a thread of its
    // own, so a thread pool kept busy by other tests doesn't delay its start.
    [Theory]
    [InlineData("blank lines in Chart.yaml", "b")]
    [InlineData("a long line of dashes and blanks in Chart.yaml", "")]
    [InlineData("blanks after name: in Chart.yaml", "")]
    [InlineData("index calls that never get a key", "")]
    [InlineData("an index key that never closes", "")]
    [InlineData("a long index path that never gets a key", "b")]
    [InlineData("a long .Values path", "b")]
    [InlineData("a long key above many values", "b")]
    [InlineData("values that aliases repeat", "")]
    public async Task A_hostile_chart_is_scanned_in_linear_time(string shape, string dead)
    {
        using var temp = new TempFolder();
        temp.Write("Chart.yaml", "apiVersion: v2\nname: x\nversion: 1.0.0\n");
        temp.Write("values.yaml", "a: 1\nb: 2\n");
        temp.Write("templates/a.yaml", "{{ .Values.a }}");
        var (file, text) = HostileFile(shape);
        temp.Write(file, file == "values.yaml" ? "a: 1\nb: 2\n" + text : text);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var scan = Task.Factory.StartNew(() => HelmValueUsage.FindDead(temp.Path, []), TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var finished = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken)) == scan;

        Assert.True(finished, $"Scanning {text.Length} characters of {shape} took longer than {watch.Elapsed.TotalSeconds:0}s.");
        string[] expected = dead.Length == 0 ? [] : [dead];
        Assert.Equal(expected, (await scan).Select(d => d.Path));
    }

    private static (string File, string Text) HostileFile(string shape) => shape switch
    {
        "blank lines in Chart.yaml" => ("Chart.yaml", "apiVersion: v2\nname: x\nversion: 1.0.0\n" + new string('\n', 20_000)),
        "a long line of dashes and blanks in Chart.yaml" => ("Chart.yaml", Repeat(" - \t", 2_000_000)),
        "blanks after name: in Chart.yaml" => ("Chart.yaml", Repeat("name:" + new string(' ', 100) + "\n", 2_000_000)),
        "index calls that never get a key" => ("templates/hostile.yaml", Repeat("index .Values ", 2_000_000)),
        "an index key that never closes" => ("templates/hostile.yaml", "index .Values \"" + new string('a', 2_000_000)),
        "a long index path that never gets a key" => ("templates/hostile.yaml", "index .Values" + Repeat(".a", 2_000_000)),
        "a long .Values path" => ("templates/hostile.yaml", ".Values" + Repeat(".a", 2_000_000)),
        "a long key above many values" => ("values.yaml", "? " + new string('k', 5_000) + "\n:\n" + string.Concat(Enumerable.Range(0, 20_000).Select(i => $"  v{i}: 1\n"))),
        "values that aliases repeat" => ("values.yaml", Aliases(1_300)),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    // A thousand values, then a mapping that repeats them under a long key through an alias, times times.
    private static string Aliases(int times) =>
        "base: &b {" + string.Join(", ", Enumerable.Range(0, 1_000).Select(i => $"k{i:000}: 1")) + "}\n"
        + new string('k', 240) + ": {" + string.Join(", ", Enumerable.Range(0, times).Select(i => $"a{i}: *b")) + "}\n";

    private static string Repeat(string unit, int length) => string.Concat(Enumerable.Repeat(unit, length / unit.Length));

    [Fact]
    public void Every_pattern_has_a_match_timeout()
    {
        var patterns = typeof(HelmValueUsage)
            .GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(System.Text.RegularExpressions.Regex) && m.GetParameters().Length == 0)
            .Select(m => (m.Name, Regex: (System.Text.RegularExpressions.Regex)m.Invoke(null, null)!))
            .ToList();

        Assert.True(patterns.Count >= 3, $"Found only {patterns.Count} patterns.");
        Assert.All(patterns, p => Assert.True(p.Regex.MatchTimeout != System.Text.RegularExpressions.Regex.InfiniteMatchTimeout, p.Name));
    }

    private const string LibraryValues = "image:\n  repository: example/app\n  tag: \"1.0\"\nreplicas: 2\nunused: 1\n";

    private const string LibraryTemplate = "{{- define \"base.deployment\" -}}\nimage: {{ .Values.image.repository }}:{{ .Values.image.tag }}\nreplicas: {{ .Values.replicas }}\n{{- end }}\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_chart_that_includes_a_template_it_does_not_have_has_no_dead_values(bool packed)
    {
        using var temp = new TempFolder();
        temp.Write("Chart.yaml", "apiVersion: v2\nname: app\nversion: 1.0.0\ndependencies:\n  - name: base\n    version: 1.0.0\n    repository: https://charts.example.invalid\n");
        temp.Write("values.yaml", "image:\n  repository: example/app\n  tag: \"1.0\"\nreplicas: 2\ndb:\n  host: db.example.invalid\nconfig: \"host={{ .Values.db.host }}\"\n");
        temp.Write("templates/all.yaml", "{{ include \"base.deployment\" . }}\n---\napiVersion: v1\nkind: ConfigMap\ndata:\n  config: {{ tpl .Values.config . | quote }}\n");
        if (packed)
        {
            temp.Write("charts/base-1.0.0.tgz", "not read");
        }

        Assert.Empty(HelmValueUsage.FindDead(temp.Path, []));
    }

    [Fact]
    public void A_chart_that_defines_every_template_it_includes_still_has_dead_values()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "nameOverride: x\nimage: example/app\nunused: 1\n");
        temp.Write("templates/_helpers.tpl", "{{/* Helpers, see _helpers.tpl */}}\n{{- define \"app.name\" -}}{{ .Values.nameOverride | default .Chart.Name }}{{- end }}\n");
        temp.Write("templates/deploy.yaml", "name: {{ include \"app.name\" . }}\nimage: {{ .Values.image }}\nlabel: {{- template \"app.name\" . }}\nchecksum: {{ include (print $.Template.BasePath \"/deploy.yaml\") . | sha256sum }}\n");

        Assert.Equal(["unused"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
    }

    [Fact]
    public void Values_a_library_chart_under_charts_reads_for_the_chart_count_as_read()
    {
        using var temp = new TempFolder();
        temp.Write("Chart.yaml", "apiVersion: v2\nname: app\nversion: 1.0.0\ndependencies:\n  - name: base\n    version: 1.0.0\n");
        temp.Write("values.yaml", LibraryValues);
        temp.Write("templates/all.yaml", "{{ include \"base.deployment\" . }}\n");
        temp.Write("charts/base/Chart.yaml", "apiVersion: v2\nname: base\ntype: library\nversion: 1.0.0\n");
        temp.Write("charts/base/templates/_deployment.tpl", LibraryTemplate);

        Assert.Equal(["unused"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
    }

    [Fact]
    public void A_subchart_template_the_chart_does_not_include_reads_only_the_subchart_values()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", LibraryValues + "redis:\n  port: 1\n");
        temp.Write("templates/cm.yaml", "replicas: {{ .Values.replicas }}\n");
        temp.Write("charts/redis/Chart.yaml", "apiVersion: v2\nname: redis\nversion: 1.0.0\n");
        temp.Write("charts/redis/templates/deployment.yaml", "image: {{ .Values.image.repository }}:{{ .Values.image.tag }}\n");

        Assert.Equal(["image.repository", "image.tag", "unused"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
    }

    [Fact]
    public void Values_read_through_tpl_count_as_read()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "config: \"host={{ .Values.db.host }}\"\ndb:\n  host: h\n  port: 1\nunused: 1\nextra: 1\n");
        temp.Write("files/app.conf", "port={{ .Values.db.port }}\n");
        temp.Write("values-prod.yaml", "note: \"{{ .Values.extra }}\"\n");
        temp.Write("templates/cm.yaml", "a: {{ tpl .Values.config . }}\nb: {{ tpl (.Files.Get \"files/app.conf\") $ }}\nc: {{ tpl .Values.note . }}\n");

        Assert.Equal(["unused"], HelmValueUsage.FindDead(temp.Path, [temp.Full("values-prod.yaml")]).Select(d => d.Path).Distinct());
    }

    [Fact]
    public void Without_tpl_text_in_values_and_chart_files_reads_nothing()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "config: \"host={{ .Values.db.host }}\"\ndb:\n  host: h\n");
        temp.Write("files/app.conf", "{{ .Values.db.host }}\n");
        temp.Write("templates/cm.yaml", "a: {{ .Values.config }}\nb: {{ .Files.Get \"files/app.conf\" }}\n");

        Assert.Equal(["db.host"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
    }

    [Theory]
    [InlineData("{{ include (printf \"%s.labels\" .Chart.Name) . }}")]
    [InlineData("{{ include $name . }}")]
    [InlineData("{{- template .Values.which . }}")]
    public void A_template_included_by_a_name_worked_out_at_render_time_gives_no_dead_values(string template)
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\nunused: 1\n");
        temp.Write("templates/_helpers.tpl", "{{- define \"app.labels\" -}}{{ .Values.a }}{{- end }}\n");
        temp.Write("templates/cm.yaml", template);

        Assert.Empty(HelmValueUsage.FindDead(temp.Path, []));
    }

    [Theory]
    [InlineData("undefined")]
    [InlineData("worked out")]
    [InlineData("big Chart.yaml")]
    [InlineData("undefined in a value")]
    [InlineData("dependencies not a list")]
    public void Says_why_a_chart_has_no_dead_values(string reason)
    {
        using var temp = new TempFolder();

        // A name tpl finds in a values file is part of a value, which may be a secret, so the note doesn't quote it.
        temp.Write("values.yaml", reason == "undefined in a value"
            ? "password: not-a-real-secret\nx: '{{ include \"s3cr3t-abc\" . }}'\nunused: 1\n"
            : "password: not-a-real-secret\nunused: 1\n");
        temp.Write("templates/cm.yaml", reason switch
        {
            "undefined" => "{{ include \"base.\u202Edeploy\u0007ment\" . }}",
            "worked out" => "{{ include $name . }}",
            "undefined in a value" => "{{ tpl .Values.x . }}",
            _ => "kind: ConfigMap",
        });
        if (reason == "big Chart.yaml")
        {
            temp.Write("Chart.yaml", "apiVersion: v2\n#" + new string('x', 2 * 1024 * 1024));
        }

        if (reason == "dependencies not a list")
        {
            temp.Write("Chart.yaml", "apiVersion: v2\ndependencies: redis\n");
        }

        // Control and format characters in a name could hide or reorder the text of the note.
        var dead = HelmValueUsage.FindDead(temp.Path, [], "Helm chart deploy/\u2066app\u001b[0m", out var notes);

        Assert.Empty(dead);
        var note = Assert.Single(notes);
        Assert.Equal(
            reason switch
            {
                "undefined" => "The templates of Helm chart deploy/app[0m include \"base.deployment\", which isn't in the chart, so no Helm value is marked dead: that template may read any of them. Put the dependency that defines it under charts/, unpacked, to check them.",
                "worked out" => "The templates of Helm chart deploy/app[0m include a template whose name is worked out when the chart is rendered, so no Helm value is marked dead: that template may read any of them.",
                "undefined in a value" => "The templates of Helm chart deploy/app[0m include a template that isn't in the chart, so no Helm value is marked dead: that template may read any of them. Put the dependency that defines it under charts/, unpacked, to check them.",
                "big Chart.yaml" => "Helm chart deploy/app[0m wasn't read in full: Chart.yaml is bigger than 2 MB. So no Helm value is marked dead, as the part not read may read any of them.",
                _ => "The dependencies of Helm chart deploy/app[0m in Chart.yaml, requirements.yaml or charts/ can't be read, so no Helm value is marked dead: without them a subchart's values can't be told from the chart's own.",
            },
            note);
        Assert.DoesNotContain("not-a-real-secret", note, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", note, StringComparison.Ordinal);
    }

    [Fact]
    public void No_note_when_the_scan_knows_what_the_templates_read()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\nunused: 1\n");
        temp.Write("templates/cm.yaml", "{{ .Values.a }}");

        Assert.Equal(["unused"], HelmValueUsage.FindDead(temp.Path, [], string.Empty, out var notes).Select(d => d.Path));
        Assert.Empty(notes);
    }

    [Fact]
    public void Without_a_source_name_a_note_names_the_chart_folder()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\n");
        temp.Write("templates/cm.yaml", "{{ include $name . }}");

        HelmValueUsage.FindDead(temp.Path, [], " ", out var notes);

        Assert.StartsWith($"The templates of Helm chart {System.IO.Path.GetFileName(temp.Path)} include", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_template_that_text_rendered_with_tpl_includes_counts(bool committed)
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "extra: '{{ include \"lib.sidecar\" . }}'\nsidecar:\n  image: x\nunused: 1\n");
        temp.Write("templates/cm.yaml", "data: {{ tpl .Values.extra . }}\n");
        if (committed)
        {
            temp.Write("charts/lib/Chart.yaml", "apiVersion: v2\nname: lib\ntype: library\nversion: 1.0.0\n");
            temp.Write("charts/lib/templates/_s.tpl", "{{- define \"lib.sidecar\" -}}image: {{ .Values.sidecar.image }}{{- end }}\n");
        }

        string[] expected = committed ? ["unused"] : [];
        Assert.Equal(expected, HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
    }

    [Fact]
    public void A_chart_file_in_a_dot_folder_that_tpl_renders_counts()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "port: 1\nunused: 1\n");
        temp.Write(".config/app.tpl", "port={{ .Values.port }}\n");
        temp.Write("templates/cm.yaml", "data: {{ tpl (.Files.Get \".config/app.tpl\") . }}\n");

        Assert.Equal(["unused"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
    }

    [Fact]
    public void A_large_chart_gives_what_comparing_every_value_with_every_reference_gives()
    {
        // Short names from a few parts, some with dots, so values and references are often the same, above or below each
        // other. Values also use parts no reference has.
        string[] parts = ["a", "b", "ab", "a-b", "x_1", "c.d", "global", "sub", "e", "f.g", "h", "i"];
        var random = new Random(7);
        string Path(int most, int kinds) => string.Join('.', Enumerable.Range(0, random.Next(1, most + 1)).Select(_ => parts[random.Next(kinds)]));
        var references = Enumerable.Range(0, 1_500).Select(_ => Path(5, 6)).ToList();
        var values = Enumerable.Range(0, 3_000).Select(i => (Path(4, parts.Length), i % 3 == 0 ? "values-prod.yaml" : "values.yaml")).ToList();
        string[] subcharts = ["sub"];
        var template = string.Concat(references.Select(r => $"{{{{ .Values.{r} }}}}\n"));

        var expected = new List<(string, string)>();
        foreach (var (path, file) in values)
        {
            var read = path.Split('.')[0] is "global" or "sub" || references.Any(r =>
                r == path || path.StartsWith(r + ".", StringComparison.Ordinal) || r.StartsWith(path + ".", StringComparison.Ordinal));
            if (!read && !expected.Contains((path, file)))
            {
                expected.Add((path, file));
            }
        }

        var dead = HelmValueUsage.FindDead([template], values, subcharts).Select(d => (d.Path, d.File)).ToList();

        Assert.InRange(expected.Count, 300, 2_000);
        Assert.InRange(values.Count - values.Distinct().Count(), 100, 2_000);
        Assert.Equal(expected, dead);
    }

    [Fact]
    public void Checks_each_value_without_building_a_string_for_every_reference()
    {
        var template = string.Concat(Enumerable.Range(0, 1_500).Select(i => $"{{{{ .Values.read{i}.x }}}}\n"));
        var values = Enumerable.Range(0, 3_000).Select(i => ($"unread{i}.x", "values.yaml")).ToList();
        HelmValueUsage.FindDead([template], values.Take(1), []);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var dead = HelmValueUsage.FindDead([template], values, []);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(3_000, dead.Count);
        // Two strings for each of the 4.5 million pairs of a value and a reference come to hundreds of megabytes.
        Assert.True(allocated < 32 * 1024 * 1024, $"Allocated {allocated / 1024 / 1024} MB.");
    }

    [Fact]
    public void Checks_each_value_without_building_a_string_for_every_path_above_a_reference()
    {
        // 2 MB of references, each 125 parts below a name of its own, and one value as long as a path may be.
        var below = string.Concat(Enumerable.Repeat(".a", 125));
        var template = string.Concat(Enumerable.Range(0, 7_700).Select(i => $"{{{{ .Values.k{i:00000}{below} }}}}\n"));
        (string, string)[] values = [(new string('v', 256), "values.yaml")];
        HelmValueUsage.FindDead(["{{ .Values.x }}"], values, []);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var dead = HelmValueUsage.FindDead([template], values, []);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Single(dead);
        // The template and its references are about 12 MB; a string for every path above each reference is about 300 MB.
        Assert.True(allocated < 48 * 1024 * 1024, $"Allocated {allocated / 1024 / 1024} MB.");
    }

    [Fact]
    public void Malformed_values_YAML_is_a_YamlException_whatever_YamlDotNet_throws()
    {
        // Found by fuzzing: this made YamlDotNet's scanner throw InvalidOperationException.
        const string values = "replicaCount: 2\nimage:\n  repository: nginx\n  tag: \"1.25\"\n  pullPolra: {{ index .Values \"config\" \"level\" }}\n  {{ index .Values.ingresel: info\n  unusedKey: x\ningress:\n  hosts: [a, b]\n";

        Assert.ThrowsAny<YamlDotNet.Core.YamlException>(() => HelmValueUsage.Leaves(values).ToList());
    }

    [Theory]
    [InlineData("a: &x\n  b: *x\n")]
    [InlineData("a: &x\n  - *x\n")]
    public void Recursive_anchors_in_values_are_a_YamlException_not_a_stack_overflow(string values)
    {
        // Found by fuzzing: YamlDotNet hashes a mapping that holds itself until the stack overflows.
        Assert.ThrowsAny<YamlDotNet.Core.YamlException>(() => HelmValueUsage.Leaves(values).ToList());
    }
}
