using System.CommandLine;
using System.Text.Json;
using ConfigFerret.Kubernetes;

namespace ConfigFerret.Tool;

/// <summary>The <c>configferret</c> command: <c>trace</c>, <c>unused</c> and <c>container</c>.</summary>
internal static class Cli
{
    public static async Task<int> RunAsync(string[] args, CliHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);

        // Everything after "--" is the traced app's own command line, as with dotnet run.
        var split = Array.IndexOf(args, "--");
        var appArgs = split < 0 ? [] : args[(split + 1)..];
        var parse = BuildRootCommand(host, appArgs).Parse(split < 0 ? args : args[..split]);
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors)
            {
                await host.Error.WriteLineAsync(error.Message).ConfigureAwait(false);
            }

            await host.Error.WriteLineAsync("Run 'configferret --help' for usage.").ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        try
        {
            var invocation = new InvocationConfiguration { Output = host.Out, Error = host.Error, EnableDefaultExceptionHandler = false };
            return await parse.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UsageException or IOException or UnauthorizedAccessException or FormatException
            or InvalidOperationException or ArgumentException or HttpRequestException or JsonException)
        {
            // One line, no stack trace: these are problems with the input, not bugs.
            await host.Error.WriteLineAsync("configferret: " + ex.Message).ConfigureAwait(false);
            return ExitCodes.Usage;
        }
    }

    internal static Command BuildRootCommand(CliHost host, IReadOnlyList<string> appArgs)
    {
        // A plain command rather than RootCommand, which takes its name from the executable (ConfigFerret.Tool.dll).
        var root = new Command("configferret", "Traces .NET configuration values back through appsettings files, environment variables, Kubernetes, Helm, Kustomize, GitHub Actions and Octopus Deploy. Secret values are shown as *** unless you pass --reveal-secrets.");
        root.Options.Add(new System.CommandLine.Help.HelpOption());
        root.Options.Add(new VersionOption());
        root.Subcommands.Add(TraceCommand(host, appArgs));
        root.Subcommands.Add(UnusedCommand(host, appArgs));
        root.Subcommands.Add(ContainerCommand(host, appArgs));
        return root;
    }

    private static Command TraceCommand(CliHost host, IReadOnlyList<string> appArgs)
    {
        var keys = KeysArgument("Keys to explain, with : or __ as the separator. Leave out to trace every key.");
        var app = new AppOptions();
        var kubernetes = new KubernetesOptions(required: false);
        var upstream = new UpstreamOptions();
        var output = new OutputOptions();
        var command = new Command("trace", "Trace every value of an app's configuration, or the keys you name, through each layer that sets it. Pass the app's own command-line arguments after --.")
        {
            keys,
        };
        app.AddTo(command);
        kubernetes.AddTo(command);
        upstream.AddTo(command);
        output.AddTo(command);
        command.SetAction(async (result, cancellationToken) =>
        {
            var run = new Run(host, result, output);
            var tracer = await run.AppTracerAsync(app, kubernetes, upstream, appArgs, cancellationToken).ConfigureAwait(false);
            return await run.WriteTraceAsync(tracer.Trace(), result.GetValue(keys) ?? []).ConfigureAwait(false);
        });
        return command;
    }

    private static Command UnusedCommand(CliHost host, IReadOnlyList<string> appArgs)
    {
        var reads = new Option<string[]>("--reads")
        {
            Description = "Keys the app was seen reading (ConfigReadRecorder.ReadKeys), one per line or a JSON array. Repeat to combine runs. Without it, only values that never win are listed.",
            HelpName = "file",
        };
        var exitCode = new Option<bool>("--exit-code")
        {
            Description = "Exit with 1 when anything is found, for use in CI.",
        };
        var app = new AppOptions();
        var kubernetes = new KubernetesOptions(required: false);
        var upstream = new UpstreamOptions();
        var output = new OutputOptions();
        var command = new Command("unused", "List configuration that is possibly unused: values a later layer always overrides, and with --reads, keys the app never read. Findings are possibly unused, never proven safe to delete.")
        {
            reads,
            exitCode,
        };
        app.AddTo(command);
        kubernetes.AddTo(command);
        upstream.AddTo(command);
        output.AddTo(command);
        command.SetAction(async (result, cancellationToken) =>
        {
            var run = new Run(host, result, output);
            var tracer = await run.AppTracerAsync(app, kubernetes, upstream, appArgs, cancellationToken).ConfigureAwait(false);
            var readFiles = result.GetValue(reads) ?? [];
            var report = tracer.Trace();
            var unused = readFiles.Length == 0 ? report.FindUnused() : report.FindUnused(readFiles.SelectMany(run.ReadKeys).ToList());
            await run.WriteAsync(result.GetValue(output.Format) == OutputFormat.Json ? unused.ToJson() : unused.ToText()).ConfigureAwait(false);
            var found = unused.NeverWinning.Count > 0 || unused.NotRead is { Count: > 0 };
            return found && result.GetValue(exitCode) ? ExitCodes.Findings : ExitCodes.Success;
        });
        return command;
    }

    private static Command ContainerCommand(CliHost host, IReadOnlyList<string> appArgs)
    {
        var keys = KeysArgument("Environment variable names or mounted file paths to explain. Leave out to trace them all.");
        var kubernetes = new KubernetesOptions(required: true);
        var upstream = new UpstreamOptions();
        var output = new OutputOptions();
        var command = new Command("container", "Trace what one Kubernetes container gets, seen from outside the app: every environment variable and every file a ConfigMap or Secret volume mounts, back to Helm or Kustomize.")
        {
            keys,
        };
        kubernetes.AddTo(command);
        upstream.AddTo(command);
        output.AddTo(command);
        command.SetAction(async (result, cancellationToken) =>
        {
            if (appArgs.Count > 0)
            {
                throw new UsageException("container takes no app arguments after --.");
            }

            var run = new Run(host, result, output);
            var manifests = run.Input.ReadManifests(result.GetValue(kubernetes.Manifests) ?? []);
            var tracer = ConfigTracer.ForContainer(manifests, result.GetValue(kubernetes.Workload), result.GetValue(kubernetes.Container));
            await run.AddUpstreamAsync(tracer, upstream, cancellationToken).ConfigureAwait(false);
            return await run.WriteTraceAsync(tracer.Trace(), result.GetValue(keys) ?? []).ConfigureAwait(false);
        });
        return command;
    }

    private static Argument<string[]> KeysArgument(string description)
    {
        var keys = new Argument<string[]>("keys")
        {
            Description = description,
            Arity = ArgumentArity.ZeroOrMore,
        };

        // An unknown option would otherwise be taken for a key and reported as "not set".
        keys.Validators.Add(result =>
        {
            foreach (var token in result.Tokens.Where(t => t.Value.StartsWith('-')))
            {
                result.AddError($"Unrecognized option '{token.Value}'.");
            }
        });
        return keys;
    }

    /// <summary>One invocation: reads the inputs, builds the tracer and writes the report.</summary>
    private sealed class Run(CliHost host, ParseResult result, OutputOptions output)
    {
        public InputReader Input { get; } = new(host);

        private bool Reveal => result.GetValue(output.RevealSecrets);

        public async Task<ConfigTracer> AppTracerAsync(AppOptions app, KubernetesOptions kubernetes, UpstreamOptions upstream, IReadOnlyList<string> appArgs, CancellationToken cancellationToken)
        {
            // The app's environment: what the container gets from the manifests, then --env-file, then --env.
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
            var manifestPaths = result.GetValue(kubernetes.Manifests) ?? [];
            KubernetesManifests? manifests = null;
            var workload = result.GetValue(kubernetes.Workload);
            var container = result.GetValue(kubernetes.Container);
            if (manifestPaths.Length > 0)
            {
                manifests = Input.ReadManifests(manifestPaths);
                foreach (var (name, value) in ContainerEnvironment(manifests, workload, container))
                {
                    environment[name] = value;
                }
            }
            else if (workload is not null || container is not null)
            {
                throw new UsageException("--workload and --container need --manifests.");
            }

            foreach (var file in result.GetValue(app.EnvFile) ?? [])
            {
                foreach (var (name, value) in EnvironmentInput.ParseFile(Input.ReadText(file), file))
                {
                    environment[name] = value;
                }
            }

            foreach (var pair in result.GetValue(app.Env) ?? [])
            {
                var (name, value) = EnvironmentInput.ParsePair(pair, "--env");
                environment[name] = value;
            }

            var unknown = environment.Where(e => e.Value is null).Select(e => e.Key).Order(StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                await host.Error.WriteLineAsync(
                    "configferret: the manifests don't hold the value of " + string.Join(", ", unknown)
                    + " (set at run time, or from a ConfigMap or Secret that wasn't passed). Pass --env NAME=VALUE to include them.").ConfigureAwait(false);
                foreach (var name in unknown)
                {
                    environment.Remove(name);
                }
            }

            var prefixes = result.GetValue(app.EnvPrefix) is { Length: > 0 } given ? given : [string.Empty];
            var inputs = new AppInputs(
                result.GetValue(app.App) ?? ".",
                result.GetValue(app.Environment),
                result.GetValue(app.Config) ?? [],
                prefixes,
                environment,
                appArgs,
                result.GetValue(app.UserSecretsId),
                !result.GetValue(app.NoUserSecrets));
            var configuration = AppConfiguration.Build(inputs, host, out _);

            var tracer = ConfigTracer.For(configuration).WithOptions(o => o.Environment = environment);
            if (manifests is not null)
            {
                tracer.WithKubernetes(manifests, workload, container);
            }

            await AddUpstreamAsync(tracer, upstream, cancellationToken).ConfigureAwait(false);
            return tracer;
        }

        public async Task AddUpstreamAsync(ConfigTracer tracer, UpstreamOptions upstream, CancellationToken cancellationToken)
        {
            tracer.WithOptions(o => o.RevealSensitiveValues = Reveal);
            Upstream.AddHelm(tracer, result.GetValue(upstream.Chart), result.GetValue(upstream.Values) ?? [], result.GetValue(upstream.Set) ?? []);
            Upstream.AddKustomize(tracer, result.GetValue(upstream.Kustomize), result.GetValue(upstream.KustomizeAllowFilesOutsideFolder));
            await Upstream.AddGitHubAsync(tracer, upstream, result, host, cancellationToken).ConfigureAwait(false);
            await Upstream.AddOctopusAsync(tracer, upstream, result, host, cancellationToken).ConfigureAwait(false);
            if (Reveal)
            {
                await host.Error.WriteLineAsync("configferret: secret values are shown. Don't paste this output anywhere shared.").ConfigureAwait(false);
            }
        }

        public IEnumerable<string> ReadKeys(string file)
        {
            var text = Input.ReadText(file);
            if (text.TrimStart().StartsWith('['))
            {
                return JsonSerializer.Deserialize<string[]>(text)?.Where(k => !string.IsNullOrWhiteSpace(k)) ?? [];
            }

            return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        }

        public async Task<int> WriteTraceAsync(ProvenanceReport report, string[] keys)
        {
            var exitCode = ExitCodes.Success;
            if (keys.Length > 0)
            {
                var found = new List<TracedSetting>();
                foreach (var key in keys)
                {
                    if (report.Find(key) is { } setting)
                    {
                        found.Add(setting);
                    }
                    else
                    {
                        await host.Error.WriteLineAsync($"{key} is not set by any configuration provider.").ConfigureAwait(false);
                        exitCode = ExitCodes.Findings;
                    }
                }

                report = report.Only(found);
            }

            await WriteAsync(result.GetValue(output.Format) == OutputFormat.Json ? report.ToJson() : report.ToText()).ConfigureAwait(false);
            return exitCode;
        }

        public async Task WriteAsync(string text)
        {
            var file = result.GetValue(output.Output);
            if (file is null)
            {
                await host.Out.WriteAsync(text).ConfigureAwait(false);
                if (!text.EndsWith('\n'))
                {
                    await host.Out.WriteLineAsync().ConfigureAwait(false);
                }

                return;
            }

            await File.WriteAllTextAsync(file, text).ConfigureAwait(false);
        }

        // The variables the container gets, with their real values, so the app's configuration is built from them.
        // The trace itself redacts them again unless --reveal-secrets is passed. Mounted files (absolute paths) are
        // left out. A variable without a known value (a downward API field, or a source not in the manifests) is
        // returned with a null value.
        private static IEnumerable<(string Name, string? Value)> ContainerEnvironment(KubernetesManifests manifests, string? workload, string? container)
        {
            var report = ConfigTracer.ForContainer(manifests, workload, container)
                .WithOptions(o => o.RevealSensitiveValues = true)
                .Trace();
            return report.Settings.Where(s => !s.Key.StartsWith('/')).Select(s => (s.Key, s.Value));
        }
    }
}
