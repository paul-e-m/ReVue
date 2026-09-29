namespace ReVueVRO.Services;

public static class RemoteVideoCacheMaintenance
{
    private static readonly object CleanupGate = new();

    public static RemoteVideoCacheCleanupResult Cleanup(int expirationHours, int maximumFiles)
    {
        lock (CleanupGate)
        {
            expirationHours = Math.Clamp(expirationHours, 1, 8760);
            maximumFiles = Math.Clamp(maximumFiles, 1, 500);
            var deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(AppPaths.LocalVroRemoteVideoCacheDir))
                return new RemoteVideoCacheCleanupResult(deletedPaths);

            var files = Directory
                .EnumerateFiles(AppPaths.LocalVroRemoteVideoCacheDir, "*.mp4", SearchOption.AllDirectories)
                .Select(path =>
                {
                    try { return new FileInfo(path); }
                    catch { return null; }
                })
                .Where(file => file is { Exists: true })
                .Cast<FileInfo>()
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();

            var cutoffUtc = DateTime.UtcNow.AddHours(-expirationHours);
            foreach (var partialPath in Directory.EnumerateFiles(
                         AppPaths.LocalVroRemoteVideoCacheDir,
                         "*.downloading",
                         SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(partialPath) < cutoffUtc) TryDelete(partialPath);
                }
                catch { }
            }

            foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoffUtc).ToList())
            {
                if (TryDelete(file.FullName)) deletedPaths.Add(file.FullName);
            }

            var remaining = files
                .Where(file => !deletedPaths.Contains(file.FullName) && File.Exists(file.FullName))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();
            var excess = Math.Max(0, remaining.Count - maximumFiles);
            foreach (var file in remaining)
            {
                if (excess <= 0) break;
                if (!TryDelete(file.FullName)) continue;
                deletedPaths.Add(file.FullName);
                excess--;
            }

            DeleteEmptyDirectories();
            return new RemoteVideoCacheCleanupResult(deletedPaths);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            // A video currently open for playback cannot always be removed on
            // Windows. A later maintenance pass will try it again.
            return false;
        }
    }

    private static void DeleteEmptyDirectories()
    {
        foreach (var directory in Directory
                     .EnumerateDirectories(AppPaths.LocalVroRemoteVideoCacheDir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch { }
        }
    }
}

public sealed record RemoteVideoCacheCleanupResult(IReadOnlySet<string> DeletedPaths);
