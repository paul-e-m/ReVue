namespace ReVueVRO;

internal static class AppPaths
{
    private const string AppFolderName = "ReVue";
    private const string VroAppFolderName = "ReVue-VRO";
    private const string RemoteReplayConfigFileName = "remote-replay.json";

    public static string LocalAppRootDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);

    public static string LocalDataDir => Path.Combine(LocalAppRootDir, "data");
    public static string LocalMediaDir => Path.Combine(LocalAppRootDir, "media");
    public static string LocalVroAppDir => Path.Combine(LocalAppRootDir, VroAppFolderName);
    public static string LocalVroConfigPath => Path.Combine(LocalVroAppDir, "appconfig.json");
    public static string LocalVroRemoteReplayConfigPath => Path.Combine(LocalVroAppDir, RemoteReplayConfigFileName);
    public static string LocalVroWebView2UserDataDir => Path.Combine(LocalVroAppDir, "WebView2");
    public static string LocalMediaMtxConfigPath => Path.Combine(LocalVroAppDir, "mediamtx.yml");
    public static string LocalElementsPath => Path.Combine(LocalDataDir, "SessionInfo.json");
    public static string LocalDemoVideoPath => Path.Combine(LocalMediaDir, "demovideo.mp4");
    public static string LocalHighResVideoPath => Path.Combine(LocalMediaDir, "current-high-res.mp4");
    public static string LocalHighResTempVideoPath => Path.Combine(LocalMediaDir, "current-high-res-recording.mp4");
    public static string LocalLowResVideoPath => Path.Combine(LocalMediaDir, "current-low-res.mp4");
    public static string LocalLowResTempVideoPath => Path.Combine(LocalMediaDir, "current-low-res-recording.mp4");

    public static string DefaultSavedVideosFolder
    {
        get
        {
            var videosRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            if (string.IsNullOrWhiteSpace(videosRoot))
                return Path.Combine(LocalAppRootDir, "videos");

            return Path.Combine(videosRoot, AppFolderName);
        }
    }

    public static void EnsureLocalDataDirectory()
    {
        Directory.CreateDirectory(LocalDataDir);
        Directory.CreateDirectory(LocalMediaDir);
    }

    public static void EnsureVroDataDirectory()
    {
        Directory.CreateDirectory(LocalVroAppDir);
    }

    public static string GetBundledDataDir(string contentRoot) => Path.Combine(contentRoot, "data");
    public static string GetBundledConfigPath(string contentRoot) => Path.Combine(GetBundledDataDir(contentRoot), "appconfig.json");
    public static string GetBundledElementsPath(string contentRoot) => Path.Combine(GetBundledDataDir(contentRoot), "SessionInfo.json");
    public static string GetBundledDemoVideoPath(string contentRoot) => Path.Combine(GetBundledDataDir(contentRoot), "demovideo.mp4");

    public static string ResolveDemoVideoPath(string contentRoot)
    {
        if (File.Exists(LocalDemoVideoPath))
            return LocalDemoVideoPath;

        var bundledDemoVideoPath = GetBundledDemoVideoPath(contentRoot);
        if (File.Exists(bundledDemoVideoPath))
            return bundledDemoVideoPath;

        return LocalDemoVideoPath;
    }

    public static string ResolveElementsPath(string contentRoot)
    {
        if (File.Exists(LocalElementsPath))
            return LocalElementsPath;

        var bundledElementsPath = GetBundledElementsPath(contentRoot);
        if (File.Exists(bundledElementsPath))
            return bundledElementsPath;

        return LocalElementsPath;
    }

    public static void EnsureSharedDataFiles(string contentRoot)
    {
        EnsureLocalDataDirectory();

        if (!File.Exists(LocalElementsPath))
        {
            var bundledElementsPath = GetBundledElementsPath(contentRoot);
            TryCopyIfMissing(bundledElementsPath, LocalElementsPath);
        }

        if (!File.Exists(LocalDemoVideoPath))
        {
            var bundledDemoVideoPath = GetBundledDemoVideoPath(contentRoot);
            TryCopyIfMissing(bundledDemoVideoPath, LocalDemoVideoPath);
        }
    }

    private static bool TryCopyIfMissing(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
            return true;

        if (!File.Exists(sourcePath))
            return false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

}
