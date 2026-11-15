using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ConfigFerret.Desktop.Model;
using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Fuzz;

/// <summary>Fuzz targets for the input parsing in the desktop app.</summary>
internal static partial class Targets
{
    private static readonly TimeSpan SlowLimit = TimeSpan.FromSeconds(2);

    private static void DesktopTar(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte[] gzip;
        if (data[0] == 0)
        {
            gzip = data[1..].ToArray();
        }
        else
        {
            using var packed = new MemoryStream();
            using (var zip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zip.Write(data[1..]);
            }

            gzip = packed.ToArray();
        }

        // The target sits in its own parent so that anything written outside it shows up in the parent.
        var parent = Directory.CreateDirectory(Path.Join(Scratch, "tar", Guid.NewGuid().ToString("N")));
        var target = Directory.CreateDirectory(Path.Join(parent.FullName, "target"));
        try
        {
            try
            {
                GitHubAccount.ExtractAsync(new MemoryStream(gzip), target.FullName, CancellationToken.None).GetAwaiter().GetResult();
            }
            // What MainWindowViewModel catches around the download.
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or JsonException
                or System.Net.Http.HttpRequestException or TaskCanceledException)
            {
            }

            var outside = parent.EnumerateFileSystemInfos().Select(i => i.Name).Where(n => n != "target").ToList();
            if (outside.Count > 0)
            {
                throw new InvariantException($"Extraction wrote outside the target: {string.Join(", ", outside)}.");
            }

            long total = 0;
            foreach (var info in target.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (info.LinkTarget is not null)
                {
                    throw new InvariantException($"Extraction created a link: {info.FullName}.");
                }

                if (info is FileInfo file)
                {
                    total += file.Length;
                }
            }

            if (total > GitHubAccount.MaxDownloadBytes)
            {
                throw new InvariantException("Extraction wrote more than the size cap.");
            }
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    private static void DotEnvTarget(ReadOnlySpan<byte> data)
    {
        try
        {
            foreach (var (name, _) in DotEnv.Parse(Encoding.UTF8.GetString(data), "fuzz.env"))
            {
                if (name.Length == 0 || name.Contains('\n', StringComparison.Ordinal))
                {
                    throw new InvariantException($"DotEnv returned a bad name '{name}'.");
                }
            }
        }
        catch (FormatException)
        {
        }
    }

    private static void SecretAuditTarget(ReadOnlySpan<byte> data)
    {
        var text = Encoding.UTF8.GetString(data);
        foreach (var value in text.Split('\n').Append(text))
        {
            var watch = Stopwatch.StartNew();
            var placeholder = SecretAudit.IsPlaceholder(value);
            var credential = SecretAudit.LooksLikeCredential(value);
            if (watch.Elapsed > SlowLimit)
            {
                throw new InvariantException($"SecretAudit took {watch.Elapsed.TotalSeconds:0.0}s on {value.Length} characters.");
            }

            if (placeholder && credential)
            {
                throw new InvariantException("A placeholder was rated as a credential.");
            }
        }
    }

    private static void ScannerTarget(ReadOnlySpan<byte> data)
    {
        var root = Directory.CreateDirectory(Path.Join(Scratch, "scan", Guid.NewGuid().ToString("N")));
        try
        {
            foreach (var chunk in Encoding.UTF8.GetString(data).Split("\n%%\n").Take(16))
            {
                var newline = chunk.IndexOf('\n', StringComparison.Ordinal);
                var name = newline < 0 ? chunk : chunk[..newline];
                var relative = string.Join('/', name.Split('/', '\\')
                    .Where(p => p.Length is > 0 and < 64 && p is not "." and not ".." && p.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                    .Take(6));
                if (relative.Length == 0)
                {
                    continue;
                }

                var path = Path.Join(root.FullName, relative);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, newline < 0 ? string.Empty : chunk[(newline + 1)..]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file and a folder with the same name: this input does not describe a tree.
                }
            }

            var scan = RepositoryScanner.Scan(root.FullName);
            var all = scan.Apps.Select(a => a.RelativePath)
                .Concat(scan.HelmCharts.SelectMany(c => c.ValuesFiles.Append(c.RelativePath)))
                .Concat(scan.Kustomizations).Concat(scan.Manifests).Concat(scan.Workflows);
            foreach (var relative in all)
            {
                var full = scan.Full(relative);
                if (!full.StartsWith(root.FullName, StringComparison.Ordinal) || !(File.Exists(full) || Directory.Exists(full)))
                {
                    throw new InvariantException($"The scan reported '{relative}', which is not inside the repository or does not exist.");
                }
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static void WorkspaceTarget(ReadOnlySpan<byte> data)
    {
        var directory = Directory.CreateDirectory(Path.Join(Scratch, "ws", Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Join(directory.FullName, "workspace.json");
            File.WriteAllBytes(path, data.ToArray());
            var store = new WorkspaceStore(path);
            Workspace workspace;
            try
            {
                workspace = store.Load();
            }
            // What MainWindowViewModel catches around the load.
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return;
            }

            // The app reads these without null checks.
            if (workspace.Repositories is null || workspace.RecentFolders is null
                || workspace.GitHubApiUrl is null || workspace.OctopusServer is null || workspace.OctopusSpace is null)
            {
                throw new InvariantException("Load returned a workspace with a null member.");
            }

            if (workspace.Repositories.Any(r => r.Key is null || r.Value is null || r.Value.Environment is null))
            {
                throw new InvariantException("Load returned a repository choice with a null member.");
            }

            store.Save(workspace);
            var again = store.Load();
            if (WorkspaceStore.Serialize(again) != WorkspaceStore.Serialize(workspace))
            {
                throw new InvariantException("A saved workspace does not load back the same.");
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
