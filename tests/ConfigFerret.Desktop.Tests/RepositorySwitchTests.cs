using System.Net;
using System.Text;
using System.Text.Json;
using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.Model;
using ConfigFerret.Desktop.Services;
using ConfigFerret.Desktop.ViewModels;

namespace ConfigFerret.Desktop.Tests;

/// <summary>Picking another repository while one is still opening shows the one picked last.</summary>
public sealed class RepositorySwitchTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("configferret-switch-");

    public void Dispose() => _temp.Delete(recursive: true);

    /// <summary>
    /// One thread runs every continuation, like Blazor's renderer and the desktop app's UI thread. The test decides when
    /// each queued continuation runs.
    /// </summary>
    private sealed class SingleThreadContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue)
            {
                _queue.Enqueue((d, state));
                Monitor.PulseAll(_queue);
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public override SynchronizationContext CreateCopy() => this;

        /// <summary>Waits until a continuation is queued, without running it.</summary>
        public void WaitForWork()
        {
            lock (_queue)
            {
                var deadline = DateTime.UtcNow + Timeout;
                while (_queue.Count == 0)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !Monitor.Wait(_queue, left))
                    {
                        throw new TimeoutException("Nothing was queued.");
                    }
                }
            }
        }

        /// <summary>Runs every continuation queued so far, without waiting for more.</summary>
        public void RunQueued()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) next;
                lock (_queue)
                {
                    if (_queue.Count == 0)
                    {
                        return;
                    }

                    next = _queue.Dequeue();
                }

                next.Callback(next.State);
            }
        }

        /// <summary>Runs the first queued continuation.</summary>
        public void RunOne()
        {
            WaitForWork();
            (SendOrPostCallback Callback, object? State) next;
            lock (_queue)
            {
                next = _queue.Dequeue();
            }

            next.Callback(next.State);
        }

        /// <summary>Runs queued continuations until <paramref name="done"/> holds, or the time is up.</summary>
        public bool RunUntil(Func<bool> done)
        {
            // The condition can also turn true on another thread, so it is checked again now and then while nothing
            // is queued.
            var deadline = DateTime.UtcNow + Timeout;
            while (!done())
            {
                (SendOrPostCallback Callback, object? State) next;
                lock (_queue)
                {
                    if (_queue.Count == 0)
                    {
                        if (DateTime.UtcNow >= deadline)
                        {
                            return false;
                        }

                        Monitor.Wait(_queue, TimeSpan.FromMilliseconds(20));
                        continue;
                    }

                    next = _queue.Dequeue();
                }

                next.Callback(next.State);
            }

            return true;
        }
    }

    /// <summary>The fake GitHub, with a second repository whose download waits until the test lets it go.</summary>
    private sealed class SlowBilling(string folder) : DelegatingHandler(new FakeGitHub())
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/repos/contoso/billing/tarball/main")
            {
                await Release.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FakeGitHub.Tarball(folder, "contoso-billing-5e6f7a8/")) };
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public void Picking_another_repository_while_the_first_is_scanned_shows_the_last_one_picked()
    {
        var billing = Path.Join(_temp.FullName, "billing");
        Directory.CreateDirectory(Path.Join(billing, "src", "Billing"));
        File.WriteAllText(Path.Join(billing, "src", "Billing", "appsettings.json"), """{ "From": "BILLING" }""");
        using var handler = new SlowBilling(billing);
        var credentials = new InMemoryCredentialStore();
        credentials.Write(MainWindowViewModel.GitHubAccountName, FakeGitHub.Token);
        var context = new SingleThreadContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            using var viewModel = new MainWindowViewModel(
                new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
                credentials,
                handler,
                new RepositoryAnalyzer(render: (_, _) => null),
                Path.Join(_temp.FullName, "downloads"));
            var loading = viewModel.LoadRepositoriesAsync();
            Assert.True(context.RunUntil(() => loading.IsCompleted), viewModel.StatusText);
            var orders = viewModel.Repositories.Single(r => r.FullName == "contoso/orders");
            var other = viewModel.Repositories.Single(r => r.FullName == "contoso/billing");

            // Open contoso/orders, and pick contoso/billing once its scan has finished but before the view model has
            // seen the result.
            _ = viewModel.AnalyzeRepositoryAsync(orders);
            Assert.True(context.RunUntil(() => viewModel.BusyText?.StartsWith("Looking", StringComparison.Ordinal) == true), viewModel.StatusText);
            context.WaitForWork();
            _ = viewModel.AnalyzeRepositoryAsync(other);
            context.RunOne();
            handler.Release.SetResult();

            context.RunUntil(() => viewModel.SubjectTitle == other.FullName && viewModel.HasResult && !viewModel.IsBusy);

            Assert.Equal(other.FullName, viewModel.SubjectTitle);
            Assert.True(viewModel.HasResult, string.Join(Environment.NewLine, viewModel.Problems.Select(p => p.Text)));
            Assert.Equal("BILLING", viewModel.Rows.Single(r => r.Key == "From").Setting.Value);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void A_failed_open_after_a_stopped_one_keeps_the_repository_on_show()
    {
        var billing = Path.Join(_temp.FullName, "billing");
        Directory.CreateDirectory(Path.Join(billing, "src", "Billing"));
        File.WriteAllText(Path.Join(billing, "src", "Billing", "appsettings.json"), """{ "From": "BILLING" }""");
        using var handler = new SlowBilling(billing);
        handler.Release.SetResult();
        var credentials = new InMemoryCredentialStore();
        credentials.Write(MainWindowViewModel.GitHubAccountName, FakeGitHub.Token);
        InContext(context =>
        {
            using var viewModel = new MainWindowViewModel(
                new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
                credentials,
                handler,
                new RepositoryAnalyzer(render: (_, _) => null),
                Path.Join(_temp.FullName, "downloads"));
            var loading = viewModel.LoadRepositoriesAsync();
            Assert.True(context.RunUntil(() => loading.IsCompleted), viewModel.StatusText);
            var shown = viewModel.AnalyzeRepositoryAsync(viewModel.Repositories.Single(r => r.FullName == "contoso/orders"));
            Assert.True(context.RunUntil(() => shown.IsCompleted));
            Assert.True(viewModel.HasResult, string.Join(Environment.NewLine, viewModel.Problems.Select(p => p.Text)));

            // Open contoso/billing, and while it is scanned pick ferret-fan/dotfiles, whose download fails.
            _ = viewModel.AnalyzeRepositoryAsync(viewModel.Repositories.Single(r => r.FullName == "contoso/billing"));
            Assert.True(context.RunUntil(() => viewModel.BusyText?.StartsWith("Looking", StringComparison.Ordinal) == true), viewModel.StatusText);
            context.WaitForWork();
            var failing = viewModel.AnalyzeRepositoryAsync(viewModel.Repositories.Single(r => r.FullName == "ferret-fan/dotfiles"));
            Assert.True(context.RunUntil(() => failing.IsCompleted && !viewModel.IsBusy));

            Assert.Equal("contoso/orders", viewModel.SelectedRepository?.FullName);
            Assert.Equal("contoso/orders", viewModel.SubjectTitle);
            Assert.True(viewModel.HasResult);
            Assert.DoesNotContain(viewModel.Problems, p => p.Severity == "error");
            Assert.DoesNotContain("Traced", viewModel.StatusText, StringComparison.Ordinal);
            var retrace = viewModel.RetraceAsync();
            Assert.True(context.RunUntil(() => retrace.IsCompleted));
            Assert.True(viewModel.HasResult, string.Join(Environment.NewLine, viewModel.Problems.Select(p => p.Text)));
            Assert.Contains(viewModel.Rows, r => r.Key == "Db:Timeout");
            Assert.DoesNotContain(viewModel.Problems, p => p.Severity == "error");
        });
    }

    [Fact]
    public void Stopped_opens_leave_the_files_of_the_repository_on_show()
    {
        var billing = Path.Join(_temp.FullName, "billing");
        Directory.CreateDirectory(Path.Join(billing, "src", "Billing"));
        File.WriteAllText(Path.Join(billing, "src", "Billing", "appsettings.json"), """{ "From": "BILLING" }""");
        using var handler = new SlowDotfiles(billing);
        var credentials = new InMemoryCredentialStore();
        credentials.Write(MainWindowViewModel.GitHubAccountName, FakeGitHub.Token);
        var downloads = Path.Join(_temp.FullName, "downloads");
        InContext(context =>
        {
            using var viewModel = new MainWindowViewModel(
                new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
                credentials,
                handler,
                new RepositoryAnalyzer(render: (_, _) => null),
                downloads);
            var loading = viewModel.LoadRepositoriesAsync();
            Assert.True(context.RunUntil(() => loading.IsCompleted), viewModel.StatusText);
            viewModel.SelectedRepository = viewModel.Repositories.Single(r => r.FullName == "contoso/orders");
            Assert.True(context.RunUntil(() => viewModel.HasResult && !viewModel.IsBusy));

            // Pick contoso/billing and, once it is downloaded and scanned, ferret-fan/dotfiles, whose download waits;
            // then change a choice of contoso/orders, which stops that download and traces contoso/orders again.
            viewModel.SelectedRepository = viewModel.Repositories.Single(r => r.FullName == "contoso/billing");
            Assert.True(context.RunUntil(() => viewModel.BusyText?.StartsWith("Looking", StringComparison.Ordinal) == true), viewModel.StatusText);
            context.WaitForWork();
            viewModel.SelectedRepository = viewModel.Repositories.Single(r => r.FullName == "ferret-fan/dotfiles");
            context.RunQueued();
            viewModel.SelectedEnvironment = "Staging";
            var retrace = viewModel.RetraceAsync();
            Assert.True(context.RunUntil(() => retrace.IsCompleted && !viewModel.IsBusy && viewModel.SelectedRepository?.FullName == "contoso/orders"));

            Assert.Equal("contoso/orders", viewModel.SubjectTitle);
            Assert.DoesNotContain(viewModel.Problems, p => p.Text.Contains("does not exist", StringComparison.Ordinal));
            Assert.True(viewModel.HasResult, string.Join(Environment.NewLine, viewModel.Problems.Select(p => p.Text)));
            Assert.Contains(viewModel.Rows, r => r.Key == "Db:Timeout");
            Assert.Single(Directory.GetDirectories(downloads));
        });
    }

    // Guards the download kept back until an open takes over: before that, the open replaced the download on show at
    // once, so closing deleted it with the window and this passed too.
    [Fact]
    public void Closing_while_a_repository_opens_deletes_its_download()
    {
        var billing = Path.Join(_temp.FullName, "billing");
        Directory.CreateDirectory(Path.Join(billing, "src", "Billing"));
        File.WriteAllText(Path.Join(billing, "src", "Billing", "appsettings.json"), """{ "From": "BILLING" }""");
        using var handler = new SlowDotfiles(billing);
        var credentials = new InMemoryCredentialStore();
        credentials.Write(MainWindowViewModel.GitHubAccountName, FakeGitHub.Token);
        var downloads = Path.Join(_temp.FullName, "downloads");
        InContext(context =>
        {
            using var viewModel = new MainWindowViewModel(
                new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
                credentials,
                handler,
                new RepositoryAnalyzer(render: (_, _) => null),
                downloads);
            var loading = viewModel.LoadRepositoriesAsync();
            Assert.True(context.RunUntil(() => loading.IsCompleted), viewModel.StatusText);
            var shown = viewModel.AnalyzeRepositoryAsync(viewModel.Repositories.Single(r => r.FullName == "contoso/orders"));
            Assert.True(context.RunUntil(() => shown.IsCompleted));

            // Close the window once contoso/billing is downloaded and scanned, before its open takes over.
            var opening = viewModel.AnalyzeRepositoryAsync(viewModel.Repositories.Single(r => r.FullName == "contoso/billing"));
            Assert.True(context.RunUntil(() => viewModel.BusyText?.StartsWith("Looking", StringComparison.Ordinal) == true), viewModel.StatusText);
            context.WaitForWork();
            viewModel.Dispose();
            Assert.True(context.RunUntil(() => opening.IsCompleted));

            Assert.Empty(Directory.GetDirectories(downloads));
        });
    }

    /// <summary>The fake GitHub, serving contoso/billing at once and ferret-fan/dotfiles only once the request is stopped.</summary>
    private sealed class SlowDotfiles(string folder) : DelegatingHandler(new FakeGitHub())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/repos/contoso/billing/tarball/main":
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FakeGitHub.Tarball(folder, "contoso-billing-5e6f7a8/")) };
                case "/repos/ferret-fan/dotfiles/tarball/trunk":
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                default:
                    return await base.SendAsync(request, cancellationToken);
            }
        }
    }

    [Fact]
    public void A_stopped_trace_that_fails_later_leaves_the_newer_repository_alone()
    {
        Write("orders/src/Orders/appsettings.json", """{ "From": "ORDERS" }""");
        Write("orders/deploy/chart/Chart.yaml", "apiVersion: v2\nname: orders\nversion: 1.0.0\n");
        Write("orders/deploy/chart/values.yaml", "replicas: 1\n");
        Write("billing/src/Billing/appsettings.json", """{ "From": "BILLING" }""");
        using var rendering = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        string? Render(string tool, IReadOnlyList<string> arguments)
        {
            rendering.Set();
            Assert.True(release.Wait(Timeout));
            throw new IOException("helm stopped for the older repository");
        }

        InContext(context =>
        {
            using var viewModel = new MainWindowViewModel(
                new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
                new InMemoryCredentialStore(),
                analyzer: new RepositoryAnalyzer(render: Render),
                downloadRoot: Path.Join(_temp.FullName, "downloads"));

            // Open orders and, while its chart renders, open billing.
            var older = viewModel.AnalyzeFolderAsync(Path.Join(_temp.FullName, "orders"));
            Assert.True(context.RunUntil(() => rendering.IsSet || older.IsCompleted));
            Assert.True(rendering.Wait(Timeout));
            var newer = viewModel.AnalyzeFolderAsync(Path.Join(_temp.FullName, "billing"));
            Assert.True(context.RunUntil(() => newer.IsCompleted));
            Assert.True(viewModel.HasResult, string.Join(Environment.NewLine, viewModel.Problems.Select(p => p.Text)));
            release.Set();
            Assert.True(context.RunUntil(() => older.IsCompleted));

            Assert.Equal("billing", viewModel.SubjectTitle);
            Assert.Equal("BILLING", viewModel.Rows.Single(r => r.Key == "From").Setting.Value);
            Assert.DoesNotContain("helm", viewModel.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain(viewModel.Problems, p => p.Text.Contains("helm", StringComparison.Ordinal));
            Assert.DoesNotContain(viewModel.Discoveries, d => d.Title == "Helm chart");
        });
    }

    [Fact]
    public void Octopus_answers_for_a_stopped_open_do_not_change_the_choices()
    {
        Write("orders/src/Orders/appsettings.json", """{ "From": "ORDERS" }""");
        Write("billing/src/Billing/appsettings.json", """{ "From": "BILLING" }""");
        var store = new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json"));
        store.Save(new Workspace { OctopusServer = "https://octopus.example.test/" });
        var credentials = new InMemoryCredentialStore();
        credentials.Write(MainWindowViewModel.OctopusAccountName, "octopus-key-for-tests");
        using var octopus = new SlowOctopus();
        InContext(context =>
        {
            using var viewModel = new MainWindowViewModel(store, credentials, octopus, new RepositoryAnalyzer(render: (_, _) => null), Path.Join(_temp.FullName, "downloads"));
            Assert.True(viewModel.IsOctopusConnected);

            // Open orders and, while Octopus is slow to list its projects, open billing.
            var older = viewModel.AnalyzeFolderAsync(Path.Join(_temp.FullName, "orders"));
            Assert.True(context.RunUntil(() => octopus.Held || older.IsCompleted));
            Assert.True(octopus.Held);
            var newer = viewModel.AnalyzeFolderAsync(Path.Join(_temp.FullName, "billing"));
            Assert.True(context.RunUntil(() => newer.IsCompleted));
            Assert.Equal("billing", viewModel.SelectedOctopusProject);
            octopus.Release.SetResult();
            Assert.True(context.RunUntil(() => older.IsCompleted));

            Assert.Equal("billing", viewModel.SubjectTitle);
            Assert.Equal("billing", viewModel.SelectedOctopusProject);
            viewModel.ToggleThemeCommand.Execute(null);
            using var saved = JsonDocument.Parse(File.ReadAllText(store.Path));
            var choices = saved.RootElement.GetProperty("repositories").EnumerateObject().ToDictionary(p => Path.GetFileName(p.Name), p => p.Value);
            Assert.NotEqual("billing", choices["orders"].TryGetProperty("octopusProject", out var project) ? project.GetString() : null);
        });
    }

    [Fact]
    public void A_repository_whose_open_failed_can_be_picked_again()
    {
        var github = new FakeGitHub();
        var credentials = new InMemoryCredentialStore();
        credentials.Write(MainWindowViewModel.GitHubAccountName, FakeGitHub.Token);
        int Downloads()
        {
            lock (github.Requests)
            {
                return github.Requests.Count(r => r.RequestUri!.AbsolutePath == "/repos/ferret-fan/dotfiles/tarball/trunk");
            }
        }

        InContext(context =>
        {
            using var viewModel = new MainWindowViewModel(
                new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
                credentials,
                github,
                new RepositoryAnalyzer(render: (_, _) => null),
                Path.Join(_temp.FullName, "downloads"));
            var loading = viewModel.LoadRepositoriesAsync();
            Assert.True(context.RunUntil(() => loading.IsCompleted), viewModel.StatusText);
            var shown = viewModel.AnalyzeRepositoryAsync(viewModel.Repositories.Single(r => r.FullName == "contoso/orders"));
            Assert.True(context.RunUntil(() => shown.IsCompleted));
            var failing = viewModel.Repositories.Single(r => r.FullName == "ferret-fan/dotfiles");

            viewModel.SelectedRepository = failing;
            Assert.True(context.RunUntil(() => Downloads() == 1 && !viewModel.IsBusy));
            Assert.Equal("contoso/orders", viewModel.SelectedRepository?.FullName);

            viewModel.SelectedRepository = failing;
            context.RunUntil(() => Downloads() == 2 && !viewModel.IsBusy);
            Assert.Equal(2, Downloads());
            Assert.Equal("contoso/orders", viewModel.SelectedRepository?.FullName);
        });
    }

    [Fact]
    public void Refresh_after_a_failed_open_downloads_the_repository_on_show_again()
    {
        var github = new FakeGitHub();
        var credentials = new InMemoryCredentialStore();
        credentials.Write(MainWindowViewModel.GitHubAccountName, FakeGitHub.Token);
        int Downloads()
        {
            lock (github.Requests)
            {
                return github.Requests.Count(r => r.RequestUri!.AbsolutePath == "/repos/contoso/orders/tarball/main");
            }
        }

        InContext(context =>
        {
            using var viewModel = new MainWindowViewModel(
                new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
                credentials,
                github,
                new RepositoryAnalyzer(render: (_, _) => null),
                Path.Join(_temp.FullName, "downloads"));
            var loading = viewModel.LoadRepositoriesAsync();
            Assert.True(context.RunUntil(() => loading.IsCompleted), viewModel.StatusText);
            var shown = viewModel.AnalyzeRepositoryAsync(viewModel.Repositories.Single(r => r.FullName == "contoso/orders"));
            Assert.True(context.RunUntil(() => shown.IsCompleted));
            var failing = viewModel.AnalyzeRepositoryAsync(viewModel.Repositories.Single(r => r.FullName == "ferret-fan/dotfiles"));
            Assert.True(context.RunUntil(() => failing.IsCompleted));

            var refresh = viewModel.RefreshCommand.ExecuteAsync(null);
            Assert.True(context.RunUntil(() => refresh.IsCompleted));

            Assert.Equal(2, Downloads());
            Assert.Equal("contoso/orders", viewModel.SubjectTitle);
            Assert.True(viewModel.HasResult, string.Join(Environment.NewLine, viewModel.Problems.Select(p => p.Text)));
        });
    }

    [Fact]
    public void A_choice_that_stops_an_open_leaves_the_stopped_repository_unselected()
    {
        var billing = Path.Join(_temp.FullName, "billing");
        Directory.CreateDirectory(Path.Join(billing, "src", "Billing"));
        File.WriteAllText(Path.Join(billing, "src", "Billing", "appsettings.json"), """{ "From": "BILLING" }""");
        using var handler = new SlowBilling(billing);
        var credentials = new InMemoryCredentialStore();
        credentials.Write(MainWindowViewModel.GitHubAccountName, FakeGitHub.Token);
        InContext(context =>
        {
            using var viewModel = new MainWindowViewModel(
                new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
                credentials,
                handler,
                new RepositoryAnalyzer(render: (_, _) => null),
                Path.Join(_temp.FullName, "downloads"));
            var loading = viewModel.LoadRepositoriesAsync();
            Assert.True(context.RunUntil(() => loading.IsCompleted), viewModel.StatusText);
            var shown = viewModel.AnalyzeRepositoryAsync(viewModel.Repositories.Single(r => r.FullName == "contoso/orders"));
            Assert.True(context.RunUntil(() => shown.IsCompleted));

            // Pick contoso/billing, whose download waits, and change a choice of contoso/orders meanwhile.
            viewModel.SelectedRepository = viewModel.Repositories.Single(r => r.FullName == "contoso/billing");
            Assert.StartsWith("Downloading", viewModel.BusyText, StringComparison.Ordinal);
            viewModel.RevealSecrets = true;
            context.RunUntil(() => !viewModel.IsBusy && viewModel.SelectedRepository?.FullName == "contoso/orders");

            Assert.Equal("contoso/orders", viewModel.SelectedRepository?.FullName);
            Assert.Equal("contoso/orders", viewModel.SubjectTitle);
            Assert.True(viewModel.HasResult, string.Join(Environment.NewLine, viewModel.Problems.Select(p => p.Text)));
        });
    }

    [Fact]
    public void A_stopped_trace_of_the_same_repository_leaves_the_sources_of_the_newer_one()
    {
        Write("orders/src/Orders/appsettings.json", """{ "From": "ORDERS" }""");
        Write("orders/deploy/chart/Chart.yaml", "apiVersion: v2\nname: orders\nversion: 1.0.0\n");
        Write("orders/deploy/chart/values.yaml", "replicas: 1\n");
        using var rendering = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 0;
        var renders = 0;
        string? Render(string tool, IReadOnlyList<string> arguments)
        {
            Interlocked.Increment(ref renders);
            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                rendering.Set();
                Assert.True(release.Wait(Timeout));
                throw new IOException("helm stopped for the older trace");
            }

            return null;
        }

        InContext(context =>
        {
            using var viewModel = new MainWindowViewModel(
                new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
                new InMemoryCredentialStore(),
                analyzer: new RepositoryAnalyzer(render: Render),
                downloadRoot: Path.Join(_temp.FullName, "downloads"));
            var opened = viewModel.AnalyzeFolderAsync(Path.Join(_temp.FullName, "orders"));
            Assert.True(context.RunUntil(() => opened.IsCompleted));
            Assert.True(viewModel.Discoveries.Single(d => d.Title == "Helm chart").Used);

            // A trace for Staging is held while one for Development starts and finishes.
            Volatile.Write(ref hold, 1);
            viewModel.SelectedEnvironment = "Staging";
            Assert.True(context.RunUntil(() => rendering.IsSet));
            viewModel.SelectedEnvironment = "Development";
            Assert.True(context.RunUntil(() => Volatile.Read(ref renders) == 3 && !viewModel.IsBusy));
            context.RunQueued();
            Assert.True(viewModel.Discoveries.Single(d => d.Title == "Helm chart").Used);

            release.Set();
            context.WaitForWork();
            context.RunQueued();

            Assert.Equal("Development", viewModel.SelectedEnvironment);
            Assert.True(viewModel.Discoveries.Single(d => d.Title == "Helm chart").Used);
            Assert.DoesNotContain("helm", viewModel.StatusText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_refused_pick_drops_the_trace_of_the_choice_before_it()
    {
        Write("orders/src/Orders/appsettings.json", """{ "From": "ORDERS" }""");
        Write("orders/deploy/chart/Chart.yaml", "apiVersion: v2\nname: orders\nversion: 1.0.0\n");
        Write("orders/deploy/chart/values.yaml", "replicas: 1\n");
        using var rendering = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 0;
        string? Render(string tool, IReadOnlyList<string> arguments)
        {
            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                rendering.Set();
                Assert.True(release.Wait(Timeout));
            }

            return null;
        }

        InContext(context =>
        {
            using var viewModel = new MainWindowViewModel(
                new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
                new InMemoryCredentialStore(),
                analyzer: new RepositoryAnalyzer(render: Render),
                downloadRoot: Path.Join(_temp.FullName, "downloads"));
            var opened = viewModel.AnalyzeFolderAsync(Path.Join(_temp.FullName, "orders"));
            Assert.True(context.RunUntil(() => opened.IsCompleted));
            Assert.True(viewModel.HasResult, string.Join(Environment.NewLine, viewModel.Problems.Select(p => p.Text)));

            // A trace for Staging is held while an environment the app does not have is picked.
            Volatile.Write(ref hold, 1);
            viewModel.SelectedEnvironment = "Staging";
            Assert.True(context.RunUntil(() => rendering.IsSet));
            viewModel.SelectedEnvironment = "Nowhere";
            Assert.False(viewModel.HasResult);
            var refusal = viewModel.StatusText;

            release.Set();
            Assert.True(context.RunUntil(() => !viewModel.IsBusy));
            context.RunQueued();

            Assert.False(viewModel.HasResult);
            Assert.Empty(viewModel.Rows);
            Assert.Equal(refusal, viewModel.StatusText);
            Assert.Contains(viewModel.Problems, p => p.Severity == "error" && p.Text == refusal);
        });
    }

    [Fact]
    public void A_refused_pick_stops_the_work_still_running()
    {
        using var handler = new SlowDotfiles(_temp.FullName);
        var credentials = new InMemoryCredentialStore();
        credentials.Write(MainWindowViewModel.GitHubAccountName, FakeGitHub.Token);
        InContext(context =>
        {
            using var viewModel = new MainWindowViewModel(
                new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
                credentials,
                handler,
                new RepositoryAnalyzer(render: (_, _) => null),
                Path.Join(_temp.FullName, "downloads"));
            var loading = viewModel.LoadRepositoriesAsync();
            Assert.True(context.RunUntil(() => loading.IsCompleted), viewModel.StatusText);
            var shown = viewModel.AnalyzeRepositoryAsync(viewModel.Repositories.Single(r => r.FullName == "contoso/orders"));
            Assert.True(context.RunUntil(() => shown.IsCompleted));

            // Pick ferret-fan/dotfiles, whose download waits until it is stopped, and an environment contoso/orders does not have.
            var opening = viewModel.AnalyzeRepositoryAsync(viewModel.Repositories.Single(r => r.FullName == "ferret-fan/dotfiles"));
            Assert.StartsWith("Downloading", viewModel.BusyText, StringComparison.Ordinal);
            viewModel.SelectedEnvironment = "Nowhere";
            var refusal = viewModel.StatusText;

            Assert.True(context.RunUntil(() => opening.IsCompleted && !viewModel.IsBusy));
            Assert.Equal("contoso/orders", viewModel.SubjectTitle);
            Assert.False(viewModel.HasResult);
            Assert.Equal(refusal, viewModel.StatusText);
        });
    }

    /// <summary>An Octopus server whose first project list waits until the test lets it go.</summary>
    private sealed class SlowOctopus : HttpMessageHandler
    {
        private int _projectLists;

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Held => Volatile.Read(ref _projectLists) > 0 && !Release.Task.IsCompleted;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/api/spaces/all" => """[{"Id":"Spaces-1","Name":"Default","IsDefault":true}]""",
                "/api/Spaces-1/projects/all" => """[{"Name":"orders","Slug":"orders"},{"Name":"billing","Slug":"billing"}]""",
                _ => "[]",
            };
            if (request.RequestUri.AbsolutePath == "/api/Spaces-1/projects/all" && Interlocked.Increment(ref _projectLists) == 1)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private void Write(string relativePath, string text)
    {
        var file = Path.Join(_temp.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    private static void InContext(Action<SingleThreadContext> test)
    {
        var context = new SingleThreadContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            test(context);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
