using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using ReVueJudge.Models;
using ReVueVRO.Models;
using ReVueVRO.Services;
using ReVueVRO.Shell;

namespace ReVueVRO.Hosting;

public static class AppServer
{
    public const string ListenUrl = "http://0.0.0.0:5050";
    public const string LocalBaseUrl = "http://127.0.0.1:5050";
    public const string MainPageUrl = LocalBaseUrl + "/index.html";
    public const string SettingsPageUrl = LocalBaseUrl + "/config.html";
    public static string OperatorAuthToken { get; private set; } = "";

    private static string ResolveContentRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current != null)
        {
            var candidate = current.FullName;
            var hasProjectFile = File.Exists(Path.Combine(candidate, "ReVueVRO.csproj"));
            var hasWwwroot = Directory.Exists(Path.Combine(candidate, "wwwroot"));

            if (hasProjectFile && hasWwwroot)
                return candidate;

            current = current.Parent;
        }

        return AppContext.BaseDirectory;
    }

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = ResolveContentRoot(),
        });
        builder.WebHost.UseUrls(ListenUrl);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = long.MaxValue;
        });
        AppPaths.EnsureLocalDataDirectory();
        AppPaths.EnsureVroDataDirectory();
        AppPaths.EnsureSharedDataFiles(builder.Environment.ContentRootPath);
        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddSingleton<MediaMtxManager>();
        builder.Services.AddSingleton<RecorderManager>();
        builder.Services.AddSingleton<CssHelperManager>();
        builder.Services.AddSingleton<RemotePlaybackManager>();
        builder.Services.AddSingleton<RemoteUploadProgressStore>();
        builder.Services.AddSingleton<RemoteDownloadStore>();

        var app = builder.Build();
        var cssHelperManager = app.Services.GetRequiredService<CssHelperManager>();

        var jsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        OperatorAuthToken = GenerateOperatorAuthToken();

        static string GenerateOperatorAuthToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        static bool IsUsableVideoFile(string path)
        {
            try
            {
                return File.Exists(path) && new FileInfo(path).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        static IResult RecordingStartProblem(
            string errorCode,
            string title,
            string detail,
            int statusCode,
            string? diagnosticDetail = null)
        {
            var extensions = new Dictionary<string, object?>
            {
                ["errorCode"] = errorCode
            };

            if (!string.IsNullOrWhiteSpace(diagnosticDetail))
                extensions["diagnosticDetail"] = diagnosticDetail;

            return Results.Problem(
                title: title,
                detail: detail,
                statusCode: statusCode,
                extensions: extensions);
        }

        static (string ErrorCode, string Detail) DescribeRecordingStartFailure(string sourceMode, string? diagnostic)
        {
            var demoMode = string.Equals(sourceMode, "Demo", StringComparison.OrdinalIgnoreCase);
            var remoteMode = string.Equals(sourceMode, "Remote", StringComparison.OrdinalIgnoreCase);
            var text = diagnostic?.Trim() ?? string.Empty;
            var comparable = text.ToLowerInvariant();

            if (comparable.Contains("no space left on device") ||
                comparable.Contains("not enough space on the disk"))
            {
                return (
                    "RECORDING_STORAGE_FULL",
                    "The recording drive does not have enough free space. Free some space and try again.");
            }

            if (comparable.Contains("permission denied") || comparable.Contains("access is denied"))
            {
                return (
                    "RECORDER_ACCESS_DENIED",
                    "Windows prevented ReVue from starting or writing the recording. Check Windows Security Protection History and folder permissions.");
            }

            if (comparable.Contains("connection refused") ||
                comparable.Contains("connection timed out") ||
                comparable.Contains("could not find codec parameters") ||
                comparable.Contains("invalid data found when processing input") ||
                comparable.Contains("server returned 4"))
            {
                return demoMode
                    ? (
                        "DEMO_VIDEO_UNREADABLE",
                        "The demo video could not be read. Replace it with a constant-frame-rate H.264/AVC MP4 and try again.")
                    : remoteMode
                    ? (
                        "REMOTE_VIDEO_UNAVAILABLE",
                        "The selected hosted video could not be read. Check the Remote host, Rink ID, and selected video.")
                    : (
                        "LIVE_SOURCE_UNAVAILABLE",
                        "ReVue did not receive a usable video stream from the configured live source. Check the encoder stream and RTSP settings.");
            }

            if (comparable.Contains("error initializing output stream") ||
                comparable.Contains("error while opening encoder") ||
                comparable.Contains("cannot load nvcuda") ||
                comparable.Contains("device failed"))
            {
                return (
                    "VIDEO_ENCODER_UNAVAILABLE",
                    "The video encoder could not be initialized. Try disabling hardware encoding in ReVue settings, restart ReVue, and try again.");
            }

            return (
                "RECORDING_START_FAILED",
                demoMode
                    ? "ReVue could not start recording from the demo video. Confirm that the file is a supported H.264/AVC MP4."
                    : remoteMode
                    ? "ReVue could not start recording from the selected hosted MP4 video."
                    : "ReVue could not start recording because no usable live video frames were received.");
        }

        static bool IsLoopbackRequest(HttpContext http)
        {
            var remoteIp = http.Connection.RemoteIpAddress;
            if (remoteIp == null) return false;

            if (IPAddress.IsLoopback(remoteIp)) return true;

            return remoteIp.IsIPv4MappedToIPv6 &&
                IPAddress.IsLoopback(remoteIp.MapToIPv4());
        }

        static string? GetBearerToken(HttpRequest request)
        {
            var authorization = request.Headers.Authorization.ToString();
            const string bearerPrefix = "Bearer ";

            return authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
                ? authorization[bearerPrefix.Length..].Trim()
                : null;
        }

        static bool IsHeadOrGet(HttpRequest request)
        {
            return HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        }

        static bool IsJudgeReplayReadOnlyEndpoint(HttpContext http)
        {
            if (!IsHeadOrGet(http.Request)) return false;

            var path = http.Request.Path.Value ?? "";
            if (string.Equals(path, "/api/status", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(path, "/api/sessionInfo", StringComparison.OrdinalIgnoreCase)) return true;

            if (string.Equals(path, "/api/recording/file", StringComparison.OrdinalIgnoreCase))
            {
                var kind = http.Request.Query["kind"].ToString();
                return string.Equals(kind, "low-res", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kind, "lowres", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        bool HasValidOperatorToken(HttpContext http)
        {
            var providedToken = GetBearerToken(http.Request);
            if (string.IsNullOrWhiteSpace(providedToken)) return false;

            var providedTokenBytes = Encoding.UTF8.GetBytes(providedToken);
            var expectedTokenBytes = Encoding.UTF8.GetBytes(OperatorAuthToken);

            return providedTokenBytes.Length == expectedTokenBytes.Length &&
                CryptographicOperations.FixedTimeEquals(providedTokenBytes, expectedTokenBytes);
        }

        app.Use(async (http, next) =>
        {
            var isLoopback = IsLoopbackRequest(http);
            var path = http.Request.Path.Value ?? "";

            if (IsJudgeReplayReadOnlyEndpoint(http))
            {
                await next();
                return;
            }

            if (isLoopback &&
                (string.Equals(path, "/api/demoVideo", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(path, "/api/remote/local-video", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(path, "/api/recording/file", StringComparison.OrdinalIgnoreCase) ||
                 !path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)))
            {
                await next();
                return;
            }

            if (!isLoopback)
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync("This endpoint is only available on the ReVue VRO computer.");
                return;
            }

            if (!HasValidOperatorToken(http))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                http.Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
                await http.Response.WriteAsync("Missing or invalid ReVue VRO operator token.");
                return;
            }

            await next();
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();

        static AppConfig NormalizeConfig(AppConfig? cfg)
        {
            cfg ??= new AppConfig();
            var requestedSourceMode = cfg.VideoSourceMode?.Trim();
            cfg.VideoSourceMode = requestedSourceMode?.ToUpperInvariant() switch
            {
                "RTSP" => "RTSP",
                "DEMO" => "Demo",
                "REMOTE" => "Remote",
                _ => cfg.DemoMode ? "Demo" : "RTSP"
            };
            cfg.DemoMode = string.Equals(cfg.VideoSourceMode, "Demo", StringComparison.OrdinalIgnoreCase);
            cfg.RemoteHostUrl = (cfg.RemoteHostUrl ?? "").Trim().TrimEnd('/');
            cfg.RemoteSessionCode = RemotePlaybackManager.NormalizeSessionCode(cfg.RemoteSessionCode);
            cfg.RemoteVideoId = (cfg.RemoteVideoId ?? "").Trim();
            cfg.RemoteVideoFolder = (cfg.RemoteVideoFolder ?? "").Trim();
            cfg.RemoteVideoLocalPath = (cfg.RemoteVideoLocalPath ?? "").Trim();
            cfg.RemoteVideoMappings = (cfg.RemoteVideoMappings ?? [])
                .Where(mapping => !string.IsNullOrWhiteSpace(mapping.LocalPath) &&
                                  !string.IsNullOrWhiteSpace(mapping.RemoteVideoId))
                .GroupBy(mapping => mapping.LocalPath.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();
            cfg.RemoteVideoCacheMaximumFiles = cfg.RemoteVideoCacheMaximumFiles <= 0
                ? 50
                : Math.Clamp(cfg.RemoteVideoCacheMaximumFiles, 1, 500);
            cfg.RemoteVideoCacheExpirationHours = cfg.RemoteVideoCacheExpirationHours <= 0
                ? 16
                : Math.Clamp(cfg.RemoteVideoCacheExpirationHours, 1, 8760);
            cfg.RtspTransportProtocol = NormalizeRtspTransportProtocol(cfg.RtspTransportProtocol);
            if (cfg.LowresVideoBitrate <= 0)
            {
                cfg.LowresVideoBitrate = 3500;
            }
            if (cfg.LowresVideoGop < 1)
            {
                cfg.LowresVideoGop = 30;
            }
            if (cfg.HighresVideoGop < 1)
            {
                cfg.HighresVideoGop = 2;
            }

            var cssLink = cfg.CSSLink?.Trim();
            cfg.CSSLink = string.IsNullOrWhiteSpace(cssLink) ? "None" : cssLink;
            cfg.SelectedCategoryID = cfg.SelectedCategoryID?.Trim() ?? string.Empty;
            cfg.SelectedSegmentID = cfg.SelectedSegmentID?.Trim() ?? string.Empty;
            cfg.ManualHalfwayTimingPreset = cfg.ManualHalfwayTimingPreset?.Trim() switch
            {
                "None" or "SeniorSP" or "SeniorFS" or "JuniorSP" or "JuniorFS" => cfg.ManualHalfwayTimingPreset.Trim(),
                _ => "None"
            };

            if (cfg.DemoMode)
            {
                cfg.SaveVideos = false;
            }

            if (string.IsNullOrWhiteSpace(cfg.SavedVideosFolder))
            {
                cfg.SavedVideosFolder = AppPaths.DefaultSavedVideosFolder;
            }

            return cfg;
        }

        static ReVueJudgeConfig NormalizeReVueJudgeConfig(ReVueJudgeConfig? cfg)
        {
            cfg ??= new ReVueJudgeConfig();
            cfg.ServerIp = string.IsNullOrWhiteSpace(cfg.ServerIp)
                ? "127.0.0.1"
                : cfg.ServerIp.Trim();
            cfg.Language = string.Equals(cfg.Language?.Trim(), "fr", StringComparison.OrdinalIgnoreCase)
                ? "fr"
                : "en";
            cfg.Role = NormalizeReVueJudgeRole(cfg.Role);
            cfg.JudgeUI = NormalizeReVueJudgeRoleUi(
                cfg.JudgeUI,
                displayTimerStopwatch: true,
                displayDanceLiftPresets: false,
                updateVideoWhileScrubbing: true);
            cfg.RefereeUI = NormalizeReVueJudgeRoleUi(
                cfg.RefereeUI,
                displayTimerStopwatch: true,
                displayDanceLiftPresets: true,
                updateVideoWhileScrubbing: true);
            cfg.UiZoomPercent = Math.Clamp(cfg.UiZoomPercent, 50, 150);
            return cfg;
        }

        static string NormalizeReVueJudgeRole(string? role)
        {
            return role?.Trim().ToLowerInvariant() switch
            {
                "judge" => "judge",
                "referee" => "referee",
                _ => "referee"
            };
        }

        static ReVueJudgeRoleUiConfig NormalizeReVueJudgeRoleUi(
            ReVueJudgeRoleUiConfig? cfg,
            bool displayTimerStopwatch,
            bool displayDanceLiftPresets,
            bool updateVideoWhileScrubbing)
        {
            cfg ??= new ReVueJudgeRoleUiConfig();
            cfg.DisplayTimerStopwatch = NormalizeReVueJudgeBooleanValue(cfg.DisplayTimerStopwatch, displayTimerStopwatch);
            cfg.DisplayDanceLiftPresets = NormalizeReVueJudgeBooleanValue(cfg.DisplayDanceLiftPresets, displayDanceLiftPresets);
            cfg.UpdateVideoWhileScrubbing = NormalizeReVueJudgeBooleanValue(cfg.UpdateVideoWhileScrubbing, updateVideoWhileScrubbing);
            return cfg;
        }

        static string NormalizeReVueJudgeBooleanValue(object? value, bool defaultValue)
            => IsReVueJudgeBooleanValueEnabled(value, defaultValue) ? "true" : "false";

        static bool IsReVueJudgeBooleanValueEnabled(object? value, bool defaultValue)
        {
            if (value is bool enabled)
            {
                return enabled;
            }

            if (value is JsonElement element)
            {
                return element.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => IsReVueJudgeBooleanValueEnabled(element.GetString(), defaultValue),
                    _ => defaultValue
                };
            }

            return value?.ToString()?.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "y" or "on" => true,
                "false" or "0" or "no" or "n" or "off" => false,
                _ => defaultValue
            };
        }

        static string NormalizeRtspTransportProtocol(string? protocol)
        {
            return string.Equals(protocol?.Trim(), "TCP", StringComparison.OrdinalIgnoreCase)
                ? "TCP"
                : "UDP";
        }

        static string GetAppVersion()
        {
            var version =
                Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "0.0.0";

            return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : $"v{version}";
        }

        static List<LocalRemoteVideoDescriptor> ListLocalRemoteVideos(AppConfig cfg, string? folderOverride = null)
        {
            var folder = string.IsNullOrWhiteSpace(folderOverride)
                ? cfg.RemoteVideoFolder
                : folderOverride;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return [];

            var mappings = (cfg.RemoteVideoMappings ?? [])
                .Select(mapping =>
                {
                    try
                    {
                        return (Path: Path.GetFullPath(mapping.LocalPath), Mapping: mapping);
                    }
                    catch
                    {
                        return (Path: "", Mapping: mapping);
                    }
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Path))
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Mapping, StringComparer.OrdinalIgnoreCase);

            var videos = new List<LocalRemoteVideoDescriptor>();
            foreach (var path in Directory.EnumerateFiles(folder, "*.mp4", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase))
            {
                var file = new FileInfo(path);
                mappings.TryGetValue(file.FullName, out var mapping);
                var mappedAndUnchanged = mapping != null &&
                    string.Equals(mapping.HostUrl?.Trim().TrimEnd('/'), cfg.RemoteHostUrl, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(RemotePlaybackManager.NormalizeSessionCode(mapping.SessionCode), cfg.RemoteSessionCode, StringComparison.Ordinal) &&
                    mapping.SizeBytes == file.Length &&
                    mapping.LastWriteUtc == file.LastWriteTimeUtc &&
                    !string.IsNullOrWhiteSpace(mapping.RemoteVideoId);
                videos.Add(new LocalRemoteVideoDescriptor
                {
                    LocalPath = file.FullName,
                    FileName = file.Name,
                    SizeBytes = file.Length,
                    LastWriteUtc = file.LastWriteTimeUtc,
                    RemoteVideoId = mappedAndUnchanged ? mapping!.RemoteVideoId : "",
                    IsUploaded = mappedAndUnchanged
                });
            }

            return videos;
        }

        static Task<string?> SelectRemoteVideoFolderAsync(string initialFolder)
        {
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    using var dialog = new FolderBrowserDialog
                    {
                        Description = "Select the folder containing the local MP4 videos for Remote mode",
                        UseDescriptionForTitle = true,
                        ShowNewFolderButton = false,
                        InitialDirectory = Directory.Exists(initialFolder) ? initialFolder : ""
                    };
                    completion.TrySetResult(dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return completion.Task;
        }

        AppConfig? cachedConfig = null;
        DateTime cachedConfigWriteUtc = DateTime.MinValue;
        bool cachedConfigExists = false;
        var remoteCacheMaintenanceCancellation = new CancellationTokenSource();

        long categorySegmentRefreshVersion = 0;
        DateTime categorySegmentRefreshUtc = DateTime.UtcNow;
        object categorySegmentRefreshGate = new();

        ReVueJudgeConfig? cachedReVueJudgeConfig = null;
        DateTime cachedReVueJudgeConfigWriteUtc = DateTime.MinValue;
        bool cachedReVueJudgeConfigExists = false;

        string? cachedSessionInfoPath = null;
        DateTime cachedSessionInfoWriteUtc = DateTime.MinValue;
        long cachedSessionInfoLength = -1;
        JsonElement? cachedSessionInfoRoot = null;
        Dictionary<int, bool>? cachedSessionInfoReviewFlags = null;

        AppConfig LoadConfig()
        {
            var path = AppPaths.LocalVroConfigPath;
            if (!File.Exists(path))
            {
                var cfg = NormalizeConfig(new AppConfig());
                SaveConfig(cfg);
                return cfg;
            }

            var writeUtc = File.GetLastWriteTimeUtc(path);
            if (cachedConfig != null && cachedConfigExists && cachedConfigWriteUtc == writeUtc)
                return cachedConfig;

            try
            {
                var json = File.ReadAllText(path);
                var shouldWriteDefaults =
                    IsMissingOrBlankConfigProperty(json, "highresVideoGop") ||
                    IsMissingOrBlankConfigProperty(json, "lowresVideoBitrate") ||
                    IsMissingOrBlankConfigProperty(json, "lowresVideoGop") ||
                    IsMissingOrBlankConfigProperty(json, "CSSLink") ||
                    IsMissingOrBlankConfigProperty(json, "SelectedCategoryID") ||
                    IsMissingOrBlankConfigProperty(json, "SelectedSegmentID") ||
                    IsMissingOrBlankConfigProperty(json, "AutoplaySelectedClip") ||
                    IsMissingOrBlankConfigProperty(json, "ManualHalfwayTimingPreset") ||
                    IsMissingOrBlankConfigProperty(json, "VideoSourceMode") ||
                    IsMissingOrBlankConfigProperty(json, "RemoteVideoCacheMaximumFiles") ||
                    IsMissingOrBlankConfigProperty(json, "RemoteVideoCacheExpirationHours");
                var loadedConfig = JsonSerializer.Deserialize<AppConfig>(json, jsonOpts);
                if (loadedConfig != null &&
                    IsMissingOrBlankConfigProperty(json, "highresVideoGop") &&
                    TryReadConfigIntProperty(json, "RecordingGop", out var legacyGop))
                {
                    loadedConfig.HighresVideoGop = legacyGop;
                }
                cachedConfig = NormalizeConfig(loadedConfig);
                if (OnlineCssDataNeedsRefresh(json, cachedConfig))
                {
                    shouldWriteDefaults = true;
                }
                cachedConfigWriteUtc = writeUtc;
                cachedConfigExists = true;
                if (shouldWriteDefaults)
                {
                    SaveConfig(cachedConfig);
                }
                return cachedConfig;
            }
            catch
            {
                return NormalizeConfig(new AppConfig());
            }
        }

        void SaveConfig(AppConfig cfg)
        {
            cfg = NormalizeConfig(cfg);
            var path = AppPaths.LocalVroConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(cfg, jsonOpts));
            cachedConfig = cfg;
            cachedConfigWriteUtc = File.GetLastWriteTimeUtc(path);
            cachedConfigExists = true;
        }

        void MaintainRemoteVideoCache(bool reconcileSelection = true)
        {
            var cfg = LoadConfig();
            RemoteVideoCacheMaintenance.Cleanup(
                cfg.RemoteVideoCacheExpirationHours,
                cfg.RemoteVideoCacheMaximumFiles);

            if (!reconcileSelection || string.IsNullOrWhiteSpace(cfg.RemoteVideoId)) return;
            try
            {
                var selectedPath = AppPaths.GetRemoteVideoCachePath(
                    cfg.RemoteSessionCode,
                    cfg.RemoteVideoId);
                if (File.Exists(selectedPath)) return;
            }
            catch
            {
            }

            cfg.RemoteVideoId = "";
            cfg.RemoteVideoLocalPath = "";
            SaveConfig(cfg);
        }

        async Task RunRemoteCacheMaintenanceAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try { MaintainRemoteVideoCache(reconcileSelection: false); }
                catch { }

                try { await Task.Delay(TimeSpan.FromMinutes(15), cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }

        ReVueJudgeConfig LoadReVueJudgeConfig()
        {
            var path = AppPaths.LocalVroRemoteReplayConfigPath;
            if (!File.Exists(path))
            {
                var cfg = NormalizeReVueJudgeConfig(new ReVueJudgeConfig());
                SaveReVueJudgeConfig(cfg);
                return cfg;
            }

            var writeUtc = File.GetLastWriteTimeUtc(path);
            if (cachedReVueJudgeConfig != null && cachedReVueJudgeConfigExists && cachedReVueJudgeConfigWriteUtc == writeUtc)
                return cachedReVueJudgeConfig;

            try
            {
                var json = File.ReadAllText(path);
                cachedReVueJudgeConfig = NormalizeReVueJudgeConfig(JsonSerializer.Deserialize<ReVueJudgeConfig>(json, jsonOpts));
                SaveReVueJudgeConfig(cachedReVueJudgeConfig);
                return cachedReVueJudgeConfig;
            }
            catch
            {
                return NormalizeReVueJudgeConfig(new ReVueJudgeConfig());
            }
        }

        void SaveReVueJudgeConfig(ReVueJudgeConfig cfg)
        {
            cfg = NormalizeReVueJudgeConfig(cfg);
            var path = AppPaths.LocalVroRemoteReplayConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(cfg, jsonOpts));
            cachedReVueJudgeConfig = cfg;
            cachedReVueJudgeConfigWriteUtc = File.GetLastWriteTimeUtc(path);
            cachedReVueJudgeConfigExists = true;
        }

        static AppConfig MergeConfig(AppConfig existing, AppConfig incoming)
        {
            if (string.IsNullOrWhiteSpace(incoming.RtspTransportProtocol))
                incoming.RtspTransportProtocol = existing.RtspTransportProtocol;
            return incoming;
        }

        static bool OnlineCssDataNeedsRefresh(string json, AppConfig cfg)
        {
            if (!string.Equals(cfg.CSSLink, "Online CSS", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("onlineCssData", out var onlineCssData) ||
                    onlineCssData.ValueKind != JsonValueKind.Object)
                {
                    return true;
                }

                return
                    !HasMatchingJsonString(onlineCssData, "eventId", cfg.EventId) ||
                    !HasMatchingJsonString(onlineCssData, "activeCategoryId", cfg.SelectedCategoryID) ||
                    !HasMatchingJsonString(onlineCssData, "activeSegmentId", cfg.SelectedSegmentID);
            }
            catch
            {
                return true;
            }
        }

        static bool HasMatchingJsonString(JsonElement parent, string propertyName, string? expected)
        {
            return parent.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String &&
                string.Equals(property.GetString(), expected ?? string.Empty, StringComparison.Ordinal);
        }

        static bool IsMissingOrBlankConfigProperty(string json, string propertyName)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    return property.Value.ValueKind == JsonValueKind.String &&
                        string.IsNullOrWhiteSpace(property.Value.GetString());
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        static bool TryReadConfigIntProperty(string json, string propertyName, out int value)
        {
            value = 0;
            try
            {
                using var document = JsonDocument.Parse(json);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        return property.Value.TryGetInt32(out value);
                    }

                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        return int.TryParse(property.Value.GetString(), out value);
                    }

                    return false;
                }
            }
            catch
            {
            }

            return false;
        }

        string ResolveElementsPath()
        {
            return AppPaths.ResolveElementsPath(app.Environment.ContentRootPath);
        }

        static async Task<JsonElement?> ReadJsonRootAsync(HttpRequest req)
        {
            try
            {
                if (req.ContentLength is null || req.ContentLength == 0) return null;
                using var doc = await JsonDocument.ParseAsync(req.Body);
                return doc.RootElement.Clone();
            }
            catch
            {
                return null;
            }
        }

        static int? TryGetInt(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value)) return null;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)) return parsed;
            return null;
        }

        static double? TryGetDouble(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value)) return null;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out var parsed)) return parsed;
            return null;
        }

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            remoteCacheMaintenanceCancellation.Cancel();
            try
            {
                var recorder = app.Services.GetRequiredService<RecorderManager>();
                recorder.StopIfRunning();
            }
            catch
            {
            }

            cssHelperManager.Dispose();
        });

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            _ = Task.Run(() => RunRemoteCacheMaintenanceAsync(remoteCacheMaintenanceCancellation.Token));
            try
            {
                cssHelperManager.Synchronize(LoadConfig(), recoverStaleProcesses: true);
            }
            catch
            {
            }
        });

        app.MapGet("/api/liveUrl", async (
            MediaMtxManager mtx,
            RecorderManager recorder,
            RemotePlaybackManager remote,
            SessionManager session,
            HttpContext http) =>
        {
            MaintainRemoteVideoCache();
            var cfg = LoadConfig();

            if (string.Equals(cfg.VideoSourceMode, "Demo", StringComparison.OrdinalIgnoreCase))
            {
                recorder.Warmup(cfg);
                return Results.Ok(new
                {
                    url = $"/demo-live?ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    mode = "demo"
                });
            }

            if (RemotePlaybackManager.IsRemoteMode(cfg))
            {
                try
                {
                    _ = RemotePlaybackManager.BuildVideoUri(cfg);
                    _ = RemotePlaybackManager.BuildLocalVideoPath(cfg);
                    recorder.Warmup(cfg);

                    if (!session.IsRecording && string.Equals(session.Mode, "record", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            await remote.PublishIdleAsync(cfg, http.RequestAborted);
                        }
                        catch
                        {
                        }
                    }

                    return Results.Ok(new
                    {
                        url = "/remote-live.html?src=" + Uri.EscapeDataString(
                            "/api/remote/local-video?ts=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                        mode = "remote"
                    });
                }
                catch (Exception ex)
                {
                    return Results.Ok(new
                    {
                        url = $"/remote-live.html?error={Uri.EscapeDataString(ex.Message)}&ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                        mode = "remote"
                    });
                }
            }

            mtx.EnsureRunning(cfg);
            recorder.Warmup(cfg);

            var url = $"/rtsp-live?ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            return Results.Ok(new
            {
                url,
                mode = "rtsp"
            });
        });

        app.MapGet("/api/status", (SessionManager session) =>
        {
            var cfg = LoadConfig();
            var status = session.GetStatus(cfg.SourceFps);
            return Results.Ok(status);
        });

        app.MapGet("/api/appconfig", () =>
        {
            var cfg = LoadConfig();
            return Results.Json(cfg, jsonOpts);
        });

        app.MapGet("/api/judge-video-replay/config", () =>
        {
            return Results.Json(LoadReVueJudgeConfig(), jsonOpts);
        });

        app.MapGet("/api/appinfo", () =>
        {
            return Results.Json(new
            {
                version = GetAppVersion()
            }, jsonOpts);
        });

        app.MapGet("/api/category-segment-refresh", () =>
        {
            lock (categorySegmentRefreshGate)
            {
                return Results.Json(new
                {
                    version = categorySegmentRefreshVersion,
                    refreshedUtc = categorySegmentRefreshUtc
                }, jsonOpts);
            }
        });

        app.MapPost("/api/category-segment-refresh", () =>
        {
            lock (categorySegmentRefreshGate)
            {
                categorySegmentRefreshVersion++;
                categorySegmentRefreshUtc = DateTime.UtcNow;
                return Results.Json(new
                {
                    version = categorySegmentRefreshVersion,
                    refreshedUtc = categorySegmentRefreshUtc
                }, jsonOpts);
            }
        });

        app.MapPost("/api/appconfig", async (AppConfig cfg, MediaMtxManager mtx, RemotePlaybackManager remote, HttpContext http) =>
        {
            var previous = LoadConfig();
            cfg = NormalizeConfig(MergeConfig(previous, cfg));
            if (string.Equals(cfg.VideoSourceMode, "Remote", StringComparison.OrdinalIgnoreCase))
            {
                await remote.ValidateSessionAsync(cfg.RemoteHostUrl, cfg.RemoteSessionCode, http.RequestAborted);
                if (!string.Equals(previous.RemoteHostUrl?.Trim().TrimEnd('/'), cfg.RemoteHostUrl, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(RemotePlaybackManager.NormalizeSessionCode(previous.RemoteSessionCode), cfg.RemoteSessionCode, StringComparison.Ordinal))
                {
                    cfg.RemoteVideoId = "";
                    cfg.RemoteVideoLocalPath = "";
                }
            }
            SaveConfig(cfg);
            MaintainRemoteVideoCache();
            cfg = LoadConfig();

            try
            {
                cssHelperManager.Synchronize(cfg);
            }
            catch
            {
            }

            if (string.Equals(cfg.VideoSourceMode, "RTSP", StringComparison.OrdinalIgnoreCase))
                mtx.Restart(cfg);

            return Results.Json(cfg, jsonOpts);
        });

        app.MapPost("/api/remote/session/validate", async (RemoteConnectionRequest request, RemotePlaybackManager remote, HttpContext http) =>
        {
            try
            {
                await remote.ValidateSessionAsync(request.HostUrl, request.SessionCode, http.RequestAborted);
                return Results.Ok(new { valid = true, code = RemotePlaybackManager.NormalizeSessionCode(request.SessionCode) });
            }
            catch (Exception ex) { return Results.BadRequest(ex.Message); }
        });

        app.MapGet("/api/remote/library", async (RemotePlaybackManager remote, RemoteDownloadStore downloads, HttpContext http) =>
        {
            MaintainRemoteVideoCache();
            var cfg = LoadConfig();
            try
            {
                var videos = await remote.ListVideosAsync(cfg.RemoteHostUrl, cfg.RemoteSessionCode, http.RequestAborted);
                var active = downloads.ActiveFor(cfg.RemoteSessionCode).ToDictionary(job => job.VideoId, StringComparer.OrdinalIgnoreCase);
                var items = videos.Select(video =>
                {
                    active.TryGetValue(video.Id, out var job);
                    var path = AppPaths.GetRemoteVideoCachePath(cfg.RemoteSessionCode, video.Id);
                    // Downloads are first written to a .downloading file and
                    // atomically moved here only after the complete response
                    // has been received. Existence of a non-empty final file
                    // is therefore the reliable completion signal; some HTTP
                    // servers do not report a byte count identical to the
                    // upload metadata.
                    var downloaded = File.Exists(path) && new FileInfo(path).Length > 0;
                    return new RemoteVideoLibraryItem
                    {
                        Id = video.Id, FileName = video.FileName, SizeBytes = video.SizeBytes, UploadedAtUtc = video.UploadedAtUtc,
                        Status = job?.Status ?? (downloaded ? "downloaded" : "available"),
                        DownloadJobId = job?.JobId ?? "", ProgressPercent = job?.ProgressPercent ?? (downloaded ? 100 : 0),
                        IsSelected = downloaded && string.Equals(cfg.RemoteVideoId, video.Id, StringComparison.OrdinalIgnoreCase)
                    };
                });
                return Results.Json(new { sessionCode = cfg.RemoteSessionCode, videos = items }, jsonOpts);
            }
            catch (Exception ex) { return Results.Problem(title: "Could not load Remote video library", detail: ex.Message, statusCode: 502); }
        });

        app.MapPost("/api/remote/library/{videoId}/download", async (string videoId, RemotePlaybackManager remote, RemoteDownloadStore downloads, HttpContext http) =>
        {
            var cfg = LoadConfig();
            var videos = await remote.ListVideosAsync(cfg.RemoteHostUrl, cfg.RemoteSessionCode, http.RequestAborted);
            var video = videos.FirstOrDefault(item => string.Equals(item.Id, videoId, StringComparison.OrdinalIgnoreCase));
            if (video == null) return Results.NotFound("Video not found on the Remote host.");
            return Results.Json(downloads.Start(cfg, video, remote), jsonOpts);
        });
        app.MapGet("/api/remote/downloads/{jobId}", (string jobId, RemoteDownloadStore downloads) =>
        {
            var job = downloads.Get(jobId);
            return job == null ? Results.NotFound() : Results.Json(job, jsonOpts);
        });
        app.MapDelete("/api/remote/downloads/{jobId}", (string jobId, RemoteDownloadStore downloads) =>
            downloads.Cancel(jobId) ? Results.Ok() : Results.NotFound());
        app.MapPost("/api/remote/library/{videoId}/select", (string videoId) =>
        {
            var cfg = LoadConfig();
            var path = AppPaths.GetRemoteVideoCachePath(cfg.RemoteSessionCode, videoId);
            if (!File.Exists(path)) return Results.BadRequest("Download the complete video before selecting it.");
            cfg.RemoteVideoId = videoId; cfg.RemoteVideoLocalPath = path; SaveConfig(cfg);
            return Results.Json(cfg, jsonOpts);
        });

        app.MapPost("/api/remote/local-folder/select", async () =>
        {
            var selected = await SelectRemoteVideoFolderAsync(LoadConfig().RemoteVideoFolder);
            return Results.Json(new { cancelled = string.IsNullOrWhiteSpace(selected), folderPath = selected ?? "" }, jsonOpts);
        });

        app.MapGet("/api/remote/local-videos", () =>
        {
            var cfg = LoadConfig();
            return Results.Json(new
            {
                folderPath = cfg.RemoteVideoFolder,
                selectedLocalPath = cfg.RemoteVideoLocalPath,
                videos = ListLocalRemoteVideos(cfg)
            }, jsonOpts);
        });

        app.MapPost("/api/remote/local-videos/preview", (RemoteVideoFolderUploadRequest request) =>
        {
            var cfg = LoadConfig();
            return Results.Json(new
            {
                folderPath = request.FolderPath,
                videos = ListLocalRemoteVideos(cfg, request.FolderPath)
            }, jsonOpts);
        });

        app.MapPost("/api/remote/local-videos/select", (SelectLocalRemoteVideoRequest request) =>
        {
            var cfg = LoadConfig();
            var selected = ListLocalRemoteVideos(cfg)
                .FirstOrDefault(video => string.Equals(video.LocalPath, request.LocalPath, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
                return Results.NotFound("The selected local MP4 is no longer in the configured folder.");
            if (!selected.IsUploaded || string.IsNullOrWhiteSpace(selected.RemoteVideoId))
                return Results.BadRequest("Upload this video to the Remote host before selecting it.");

            cfg.RemoteVideoLocalPath = selected.LocalPath;
            cfg.RemoteVideoId = selected.RemoteVideoId;
            SaveConfig(cfg);
            return Results.Json(cfg, jsonOpts);
        });

        app.MapGet("/api/remote/local-video", () =>
        {
            try
            {
                var path = RemotePlaybackManager.BuildLocalVideoPath(LoadConfig());
                return Results.File(path, "video/mp4", enableRangeProcessing: true);
            }
            catch (Exception ex)
            {
                return Results.NotFound(ex.Message);
            }
        });

        app.MapPost("/api/remote/videos/upload-folder", (
            RemoteVideoFolderUploadRequest request,
            RemotePlaybackManager remote,
            RemoteUploadProgressStore progressStore) =>
        {
            var cfg = LoadConfig();
            var folder = (request.FolderPath ?? "").Trim();
            if (!Directory.Exists(folder))
                return Results.BadRequest("Select an existing local video folder.");

            cfg.RemoteVideoFolder = Path.GetFullPath(folder);
            var selectedPaths = (request.LocalPaths ?? [])
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pending = ListLocalRemoteVideos(cfg)
                .Where(video => selectedPaths.Count == 0 || selectedPaths.Contains(video.LocalPath))
                .Where(video => !video.IsUploaded)
                .ToList();
            if (pending.Count == 0)
                return Results.Ok(new { complete = true, jobId = "", message = "All local videos are already uploaded." });

            var job = progressStore.Create(pending.Sum(video => video.SizeBytes), pending.Count);
            job.Status = "uploading";

            _ = Task.Run(async () =>
            {
                try
                {
                    var results = await remote.UploadLocalVideosAsync(
                        request.HostUrl,
                        request.SessionCode,
                        pending.Select(video => video.LocalPath).ToList(),
                        (bytes, completed, current) => job.Report(bytes, completed, current),
                        CancellationToken.None);

                    var latest = LoadConfig();
                    latest.RemoteVideoFolder = Path.GetFullPath(folder);
                    latest.RemoteHostUrl = request.HostUrl;
                    latest.RemoteSessionCode = request.SessionCode;
                    foreach (var result in results)
                    {
                        var file = new FileInfo(result.LocalPath);
                        latest.RemoteVideoMappings.RemoveAll(mapping =>
                            string.Equals(mapping.LocalPath, file.FullName, StringComparison.OrdinalIgnoreCase));
                        latest.RemoteVideoMappings.Add(new RemoteVideoMapping
                        {
                            HostUrl = request.HostUrl.Trim().TrimEnd('/'),
                            SessionCode = RemotePlaybackManager.NormalizeSessionCode(request.SessionCode),
                            LocalPath = file.FullName,
                            FileName = file.Name,
                            SizeBytes = file.Length,
                            LastWriteUtc = file.LastWriteTimeUtc,
                            RemoteVideoId = result.RemoteVideo.Id
                        });
                    }
                    SaveConfig(latest);
                    job.Report(job.TotalBytes, job.TotalFiles, "");
                    job.Status = "complete";
                }
                catch (Exception ex)
                {
                    job.Error = ex.Message;
                    job.Status = "failed";
                }
            });

            return Results.Accepted(value: new { complete = false, jobId = job.JobId });
        });

        app.MapGet("/api/remote/videos/upload-progress/{jobId}", (
            string jobId,
            RemoteUploadProgressStore progressStore) =>
        {
            var job = progressStore.Get(jobId);
            return job == null ? Results.NotFound() : Results.Json(job.Snapshot(), jsonOpts);
        });

        app.MapPost("/api/judge-video-replay/config", (ReVueJudgeConfig cfg) =>
        {
            SaveReVueJudgeConfig(cfg);
            return Results.Json(LoadReVueJudgeConfig(), jsonOpts);
        });

        app.MapPost("/api/remote/videos/list", async (
            RemoteConnectionRequest request,
            RemotePlaybackManager remote,
            HttpContext http) =>
        {
            try
            {
                var videos = await remote.ListVideosAsync(
                    request.HostUrl,
                    request.SessionCode,
                    http.RequestAborted);
                return Results.Json(videos, jsonOpts);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Could not load Remote videos",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/remote/videos/upload", async (
            HttpContext http,
            RemotePlaybackManager remote) =>
        {
            try
            {
                if (!http.Request.HasFormContentType)
                    return Results.BadRequest("Expected a multipart upload.");

                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                var files = form.Files.ToList();
                var videos = await remote.UploadVideosAsync(
                    form["hostUrl"].ToString(),
                    form["sessionCode"].ToString(),
                    files,
                    http.RequestAborted);
                return Results.Json(videos, jsonOpts);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Remote video upload failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/remote/playback", async (
            LocalRemotePlaybackCommand command,
            RemotePlaybackManager remote,
            HttpContext http) =>
        {
            var cfg = LoadConfig();
            if (!RemotePlaybackManager.IsRemoteMode(cfg))
                return Results.Ok(new { ignored = true });

            try
            {
                await remote.PublishReplayStateAsync(cfg, command, http.RequestAborted);
                return Results.Ok(new { published = true });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Remote playback synchronization failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapGet("/api/sessionInfo", (SessionManager session) =>
        {
            try
            {
                var cfg = LoadConfig();
                if (string.Equals(cfg.CSSLink, "None", StringComparison.OrdinalIgnoreCase))
                    return Results.Ok(new { elements = new Dictionary<string, object>() });

                var path = ResolveElementsPath();
                if (!File.Exists(path))
                    return Results.Ok(new { elements = new Dictionary<string, object>() });

                var fileInfo = new FileInfo(path);
                if (cachedSessionInfoRoot.HasValue &&
                    string.Equals(cachedSessionInfoPath, path, StringComparison.OrdinalIgnoreCase) &&
                    cachedSessionInfoWriteUtc == fileInfo.LastWriteTimeUtc &&
                    cachedSessionInfoLength == fileInfo.Length)
                {
                    session.UpdateReviewHistory(cachedSessionInfoReviewFlags);
                    return Results.Json(cachedSessionInfoRoot.Value.Clone(), jsonOpts);
                }

                var json = File.ReadAllText(path);
                var reviewFlags = SessionManager.ExtractReviewFlagsFromElementsJson(json);

                session.UpdateReviewHistory(reviewFlags);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement.Clone();
                cachedSessionInfoPath = path;
                cachedSessionInfoWriteUtc = fileInfo.LastWriteTimeUtc;
                cachedSessionInfoLength = fileInfo.Length;
                cachedSessionInfoRoot = root;
                cachedSessionInfoReviewFlags = reviewFlags;

                return Results.Json(root, jsonOpts);
            }
            catch
            {
                return Results.Ok(new { elements = new Dictionary<string, object>() });
            }
        });

        app.MapGet("/api/demoVideo", () =>
        {
            var path = AppPaths.ResolveDemoVideoPath(app.Environment.ContentRootPath);
            if (!IsUsableVideoFile(path))
            {
                return Results.NotFound(
                    "Demo video unavailable. No demo video was found. Live recording is unaffected. " +
                    "Place a supported H.264/AVC MP4 at %LOCALAPPDATA%\\ReVue\\media\\demovideo.mp4.");
            }

            return Results.File(path, contentType: "video/mp4", enableRangeProcessing: true);
        });

        app.MapGet("/demo-live", () =>
        {
            var videoSrc = $"/api/demoVideo?ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            var demoVideoPath = AppPaths.ResolveDemoVideoPath(app.Environment.ContentRootPath);
            var demoVideoAvailable = IsUsableVideoFile(demoVideoPath);
            var language = string.Equals(LoadConfig().Language, "fr", StringComparison.OrdinalIgnoreCase)
                ? "fr"
                : "en";
            var missingTitle = language == "fr"
                ? "Vidéo de démonstration indisponible"
                : "Demo video unavailable";
            var missingDetail = language == "fr"
                ? "Aucune vidéo de démonstration n'a été trouvée. L'enregistrement en direct n'est pas touché."
                : "No demo video was found. Live recording is unaffected.";
            var playbackDetail = language == "fr"
                ? "La vidéo de démonstration n'a pas pu être lue. Utilisez un fichier MP4 H.264/AVC à fréquence d'images constante. L'enregistrement en direct n'est pas touché."
                : "The demo video could not be played. Use a constant-frame-rate H.264/AVC MP4. Live recording is unaffected.";
            var pathPrompt = language == "fr"
                ? "Placez un fichier pris en charge à cet emplacement :"
                : "Place a supported file at:";
            var videoDisplay = demoVideoAvailable ? "block" : "none";
            var messageDisplay = demoVideoAvailable ? "none" : "flex";
            var availableJs = demoVideoAvailable ? "true" : "false";

            var html = $$"""
<!doctype html>
<html lang="{{language}}">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <style>
        html, body {
            margin: 0;
            width: 100%;
            height: 100%;
            background: #000;
            overflow: hidden;
        }
        .wrap {
            position: fixed;
            inset: 0;
            display: flex;
            align-items: flex-start;
            justify-content: flex-start;
            background: #000;
        }
        video {
            width: 100%;
            height: 100%;
            object-fit: contain;
            object-position: top left;
            background: #000;
            display: {{videoDisplay}};
        }
        .msg {
            position: fixed;
            inset: 0;
            display: {{messageDisplay}};
            align-items: center;
            justify-content: center;
            color: #fff;
            font-family: system-ui, Segoe UI, Arial, sans-serif;
            text-align: center;
            padding: 24px;
        }
        .msgCard {
            max-width: 680px;
            padding: 22px 26px;
            border: 1px solid #666;
            background: #181818;
        }
        .msgTitle {
            margin-bottom: 10px;
            font-size: 21px;
            font-weight: 700;
        }
        .msgDetail {
            font-size: 16px;
            font-weight: 500;
            line-height: 1.4;
        }
        .msgPath {
            margin-top: 12px;
            color: #ddd;
            font: 500 14px/1.4 Consolas, monospace;
            overflow-wrap: anywhere;
        }
    </style>
</head>
<body>
    <div class="wrap">
        <video id="demoVideo" autoplay muted loop playsinline>
            <source src="{{videoSrc}}" type="video/mp4">
        </video>
    </div>

    <div id="msg" class="msg">
        <div class="msgCard">
            <div class="msgTitle">{{missingTitle}}</div>
            <div id="msgDetail" class="msgDetail">{{missingDetail}}</div>
            <div class="msgPath">{{pathPrompt}}<br>%LOCALAPPDATA%\ReVue\media\demovideo.mp4</div>
        </div>
    </div>

    <script>
        const video = document.getElementById("demoVideo");
        const msg = document.getElementById("msg");
        const msgDetail = document.getElementById("msgDetail");
        const demoVideoAvailable = {{availableJs}};
        const playbackDetail = {{JsonSerializer.Serialize(playbackDetail)}};

        video.addEventListener("error", () => {
            video.style.display = "none";
            if (demoVideoAvailable) msgDetail.textContent = playbackDetail;
            msg.style.display = "flex";
        });

        if (demoVideoAvailable) video.play().catch(() => { });
    </script>
</body>
</html>
""";

            return Results.Content(html, "text/html");
        });

        app.MapGet("/rtsp-live", () =>
        {
            var whepUrl = "http://127.0.0.1:8889/mystream/whep";

            var html = $$"""
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <style>
        html, body {
            margin: 0;
            width: 100%;
            height: 100%;
            background: #000;
            overflow: hidden;
        }
        .wrap {
            position: fixed;
            inset: 0;
            background: #000;
        }
        video {
            width: 100%;
            height: 100%;
            object-fit: contain;
            object-position: top left;
            background: #000;
            display: block;
        }
        .msg {
            position: fixed;
            inset: 0;
            display: none;
            align-items: center;
            justify-content: center;
            color: #fff;
            font: 600 18px system-ui, Segoe UI, Arial, sans-serif;
            text-align: center;
            padding: 24px;
        }
    </style>
    <script defer src="/mediamtx-reader.js"></script>
</head>
<body>
    <div class="wrap">
        <video id="liveVideo" autoplay muted playsinline disablepictureinpicture></video>
    </div>

    <div id="msg" class="msg">Live stream unavailable</div>

    <script>
        const video = document.getElementById("liveVideo");
        const msg = document.getElementById("msg");
        let reader = null;

        function showError(message) {
            msg.textContent = message || "Live stream unavailable";
            msg.style.display = "flex";
        }

        function hideError() {
            msg.style.display = "none";
        }

        window.addEventListener("load", () => {
            if (!window.MediaMTXWebRTCReader) {
                showError("Live stream client failed to load");
                return;
            }

            reader = new MediaMTXWebRTCReader({
                url: "{{whepUrl}}",
                onError: (err) => {
                    showError(String(err || "Live stream unavailable"));
                },
                onTrack: (evt) => {
                    video.srcObject = evt.streams[0];
                    hideError();
                    video.play().catch(() => { });
                },
            });
        });

        window.addEventListener("beforeunload", () => {
            if (reader !== null) {
                reader.close();
                reader = null;
            }
        });
    </script>
</body>
</html>
""";

            return Results.Content(html, "text/html");
        });

        app.MapPost("/api/record/start", async Task<IResult> (
            MediaMtxManager mtx,
            RecorderManager recorder,
            RemotePlaybackManager remote,
            StartRecordingRequest? req,
            HttpContext http) =>
        {
            var cfg = LoadConfig();
            var sourceMode = cfg.VideoSourceMode;
            var isDemoMode = string.Equals(sourceMode, "Demo", StringComparison.OrdinalIgnoreCase);
            var isRemoteMode = RemotePlaybackManager.IsRemoteMode(cfg);
            var sourceStartSeconds = Math.Max(0, req?.sourceStartSeconds ?? req?.demoStartSeconds ?? 0);

            if (isDemoMode)
            {
                var demoVideoPath = AppPaths.ResolveDemoVideoPath(app.Environment.ContentRootPath);
                if (!IsUsableVideoFile(demoVideoPath))
                {
                    return RecordingStartProblem(
                        "DEMO_VIDEO_MISSING",
                        "Demo recording could not start",
                        "No demo video was found. Place a supported H.264/AVC MP4 at %LOCALAPPDATA%\\ReVue\\media\\demovideo.mp4. Live recording is unaffected.",
                        StatusCodes.Status422UnprocessableEntity);
                }
            }

            if (isRemoteMode)
            {
                try
                {
                    _ = RemotePlaybackManager.BuildVideoUri(cfg);
                    _ = RemotePlaybackManager.BuildLocalVideoPath(cfg);
                }
                catch (Exception ex)
                {
                    return RecordingStartProblem(
                        "REMOTE_VIDEO_NOT_CONFIGURED",
                        "Remote recording could not start",
                        ex.Message,
                        StatusCodes.Status422UnprocessableEntity);
                }
            }

            try
            {
                if (string.Equals(sourceMode, "RTSP", StringComparison.OrdinalIgnoreCase))
                    mtx.EnsureRunning(cfg);

                var started = await recorder.StartRecordingAsync(
                    cfg,
                    (isDemoMode || isRemoteMode) ? sourceStartSeconds : null);

                if (!started)
                {
                    var failure = DescribeRecordingStartFailure(sourceMode, recorder.LastStartError);
                    return RecordingStartProblem(
                        failure.ErrorCode,
                        isDemoMode
                            ? "Demo recording could not start"
                            : isRemoteMode
                            ? "Remote recording could not start"
                            : "Live recording could not start",
                        failure.Detail,
                        StatusCodes.Status503ServiceUnavailable,
                        recorder.LastStartError);
                }

                if (isRemoteMode)
                {
                    try
                    {
                        await remote.PublishRecordingStartedAsync(cfg, sourceStartSeconds, http.RequestAborted);
                    }
                    catch
                    {
                        // Recording is already running and must not be rolled
                        // back solely because passive-viewer sync is offline.
                        remote.SetRecordingSourceStart(sourceStartSeconds);
                    }
                }
            }
            catch (FileNotFoundException ex)
            {
                var missingFileName = Path.GetFileName(ex.FileName ?? string.Empty);
                var isDemoVideo = isDemoMode &&
                    string.Equals(missingFileName, "demovideo.mp4", StringComparison.OrdinalIgnoreCase);

                if (isDemoVideo)
                {
                    return RecordingStartProblem(
                        "DEMO_VIDEO_MISSING",
                        "Demo recording could not start",
                        "No demo video was found. Place a supported H.264/AVC MP4 at %LOCALAPPDATA%\\ReVue\\media\\demovideo.mp4. Live recording is unaffected.",
                        StatusCodes.Status422UnprocessableEntity);
                }

                var isFfmpeg = string.Equals(missingFileName, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase);
                return RecordingStartProblem(
                    isFfmpeg ? "FFMPEG_MISSING" : "RECORDING_COMPONENT_MISSING",
                    "Recording component missing",
                    isFfmpeg
                        ? "ReVue could not find ffmpeg.exe. Repair or reinstall ReVue and check Windows Security Protection History."
                        : $"ReVue could not find the required recording component {missingFileName}.",
                    StatusCodes.Status503ServiceUnavailable,
                    ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return RecordingStartProblem(
                    "RECORDER_ACCESS_DENIED",
                    "Recording access denied",
                    "Windows prevented ReVue from starting or writing the recording. Check Windows Security Protection History and folder permissions.",
                    StatusCodes.Status503ServiceUnavailable,
                    ex.Message);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return RecordingStartProblem(
                    "RECORDER_LAUNCH_FAILED",
                    "Recording component could not start",
                    "Windows could not start a required ReVue recording component. Check Windows Security Protection History, then repair or reinstall ReVue if necessary.",
                    StatusCodes.Status503ServiceUnavailable,
                    ex.Message);
            }
            catch (Exception ex)
            {
                return RecordingStartProblem(
                    "RECORDING_START_FAILED",
                    isDemoMode
                        ? "Demo recording could not start"
                        : isRemoteMode
                        ? "Remote recording could not start"
                        : "Live recording could not start",
                    isDemoMode
                        ? "ReVue could not start recording from the demo video. Confirm that the file is a supported H.264/AVC MP4."
                        : isRemoteMode
                        ? "ReVue could not start recording from the selected hosted MP4 video."
                        : "ReVue could not start recording from the configured live source.",
                    StatusCodes.Status500InternalServerError,
                    ex.Message);
            }

            var session = app.Services.GetRequiredService<SessionManager>();
            return Results.Ok(session.GetStatus(cfg.SourceFps));
        });

        app.MapPost("/api/record/stop", async (
            RecorderManager recorder,
            RemotePlaybackManager remote,
            StopRecordingRequest req,
            HttpContext http) =>
        {
            var cfg = LoadConfig();
            var duration = await recorder.StopRecordingAndGetDurationSecondsAsync(cfg, req.uiElapsedSeconds, req.programTimerStartOffsetSeconds);

            if (RemotePlaybackManager.IsRemoteMode(cfg))
            {
                try
                {
                    await remote.PublishRecordingStoppedAsync(cfg, duration, http.RequestAborted);
                }
                catch
                {
                }
            }

            var session = app.Services.GetRequiredService<SessionManager>();
            return Results.Ok(session.GetStatus(cfg.SourceFps));
        });

        app.MapPost("/api/record/clipToggle", (ClipToggleRequest req, SessionManager session) =>
        {
            var nowSeconds = req.nowSeconds;

            if (session.OpenClipStartSeconds is null)
            {
                var lastClipEnd = 0.0;
                foreach (var clip in session.Clips)
                {
                    if (clip.EndSeconds > lastClipEnd)
                        lastClipEnd = clip.EndSeconds;
                }

                nowSeconds = Math.Max(nowSeconds, lastClipEnd);
            }

            session.ToggleClipMarker(nowSeconds);
            var cfg = LoadConfig();
            return Results.Ok(session.GetStatus(cfg.SourceFps));
        });

        app.MapPost("/api/record/undo", (SessionManager session) =>
        {
            session.UndoLastClipAction();
            var cfg = LoadConfig();
            return Results.Ok(session.GetStatus(cfg.SourceFps));
        });

        app.MapPost("/api/record/redo", (SessionManager session) =>
        {
            session.RedoLastClipAction();
            var cfg = LoadConfig();
            return Results.Ok(session.GetStatus(cfg.SourceFps));
        });

        app.MapPost("/api/session/clear", async (
            SessionManager session,
            RecorderManager recorder,
            RemotePlaybackManager remote,
            HttpContext http) =>
        {
            recorder.StopIfRunning();

            try { if (File.Exists(recorder.HighResOutputFilePath)) File.Delete(recorder.HighResOutputFilePath); } catch { }
            try { if (File.Exists(recorder.HighResTempFilePath)) File.Delete(recorder.HighResTempFilePath); } catch { }
            try { if (File.Exists(recorder.LowResOutputFilePath)) File.Delete(recorder.LowResOutputFilePath); } catch { }
            try { if (File.Exists(recorder.LowResTempFilePath)) File.Delete(recorder.LowResTempFilePath); } catch { }

            session.ClearAll();
            var cfg = LoadConfig();

            if (RemotePlaybackManager.IsRemoteMode(cfg))
            {
                try
                {
                    await remote.PublishIdleAsync(cfg, http.RequestAborted);
                }
                catch
                {
                }
                remote.ClearRecordingSourceStart();
            }

            return Results.Ok(session.GetStatus(cfg.SourceFps));
        });

        app.MapGet("/api/recording/file", async (HttpContext http, RecorderManager recorder, SessionManager session, string? kind, string? v) =>
        {
            if (!session.IsReplayMediaAvailable())
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsync("No replay clips currently available.");
                return;
            }

            var wantLowRes = string.Equals(kind, "low-res", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, "lowres", StringComparison.OrdinalIgnoreCase);
            if (wantLowRes && !session.IsReplayMediaTokenCurrent(v))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsync("Replay media token is no longer current.");
                return;
            }

            var path = wantLowRes
                ? recorder.LowResOutputFilePath
                : recorder.HighResOutputFilePath;

            if (!File.Exists(path))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsync("No recording found.");
                return;
            }

            var fileInfo = new FileInfo(path);
            var lastModified = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);
            var entityTag = new EntityTagHeaderValue($"\"{fileInfo.Length:x}-{fileInfo.LastWriteTimeUtc.Ticks:x}\"");
            http.Response.Headers[HeaderNames.CacheControl] = wantLowRes
                ? "private, max-age=31536000, immutable"
                : "public, max-age=0, must-revalidate";

            await Results.File(
                path,
                contentType: "video/mp4",
                lastModified: lastModified,
                entityTag: entityTag,
                enableRangeProcessing: true).ExecuteAsync(http);
        });

        app.MapPost("/api/replay/delete", async (HttpRequest req, SessionManager session) =>
        {
            var cfg = LoadConfig();
            var root = await ReadJsonRootAsync(req);
            if (root is null) return Results.BadRequest("Missing JSON body.");

            var clipIndex = TryGetInt(root.Value, "index");
            if (clipIndex is null || clipIndex.Value <= 0) return Results.BadRequest("Missing index.");

            session.DeleteClip(clipIndex.Value);
            return Results.Ok(session.GetStatus(cfg.SourceFps));
        });

        app.MapPost("/api/record/delete", async (HttpRequest req, SessionManager session) =>
        {
            var cfg = LoadConfig();
            var root = await ReadJsonRootAsync(req);
            if (root is null) return Results.BadRequest("Missing JSON body.");

            var clipIndex = TryGetInt(root.Value, "index");
            if (clipIndex is null || clipIndex.Value <= 0) return Results.BadRequest("Missing index.");

            session.DeleteClipWhileRecording(clipIndex.Value);
            return Results.Ok(session.GetStatus(cfg.SourceFps));
        });

        app.MapPost("/api/replay/split", async (HttpRequest req, SessionManager session) =>
        {
            var cfg = LoadConfig();
            var root = await ReadJsonRootAsync(req);
            if (root is null) return Results.BadRequest("Missing JSON body.");

            var clipIndex = TryGetInt(root.Value, "index");
            var splitSeconds = TryGetDouble(root.Value, "splitSeconds");

            if (clipIndex is null || clipIndex.Value <= 0) return Results.BadRequest("Missing index.");
            if (splitSeconds is null) return Results.BadRequest("Missing splitSeconds.");

            session.SplitClip(clipIndex.Value, splitSeconds.Value);
            return Results.Ok(session.GetStatus(cfg.SourceFps));
        });

        app.MapPost("/api/replay/insert", async (HttpRequest req, SessionManager session) =>
        {
            var cfg = LoadConfig();
            var root = await ReadJsonRootAsync(req);
            if (root is null) return Results.BadRequest("Missing JSON body.");

            var startSeconds = TryGetDouble(root.Value, "startSeconds");
            var endSeconds = TryGetDouble(root.Value, "endSeconds");
            if (startSeconds is null || endSeconds is null)
                return Results.BadRequest("Missing startSeconds or endSeconds.");

            session.InsertClip(startSeconds.Value, endSeconds.Value);
            return Results.Ok(session.GetStatus(cfg.SourceFps));
        });

        app.MapPost("/api/replay/trimIn", async (HttpRequest req, SessionManager session) =>
        {
            var cfg = LoadConfig();
            var root = await ReadJsonRootAsync(req);
            if (root is null) return Results.BadRequest("Missing JSON body.");

            var clipIndex = TryGetInt(root.Value, "clipIndex");
            var atSeconds = TryGetDouble(root.Value, "atSeconds");

            if (clipIndex is null || clipIndex.Value <= 0) return Results.BadRequest("Missing clipIndex.");
            if (atSeconds is null) return Results.BadRequest("Missing atSeconds.");

            session.TrimIn(clipIndex.Value, atSeconds.Value);
            return Results.Ok(session.GetStatus(cfg.SourceFps));
        });

        app.MapPost("/api/replay/trimOut", async (HttpRequest req, SessionManager session) =>
        {
            var cfg = LoadConfig();
            var root = await ReadJsonRootAsync(req);
            if (root is null) return Results.BadRequest("Missing JSON body.");

            var clipIndex = TryGetInt(root.Value, "clipIndex");
            var atSeconds = TryGetDouble(root.Value, "atSeconds");

            if (clipIndex is null || clipIndex.Value <= 0) return Results.BadRequest("Missing clipIndex.");
            if (atSeconds is null) return Results.BadRequest("Missing atSeconds.");

            session.TrimOut(clipIndex.Value, atSeconds.Value);
            return Results.Ok(session.GetStatus(cfg.SourceFps));
        });

        app.MapPost("/api/app/restart", () =>
        {
            if (ShellCommands.RequestRestart())
                return Results.Ok(new { ok = true });

            return Results.BadRequest("Restart is only available when running the native shell app.");
        });

        app.MapPost("/api/judge-video-replay/restart", () =>
        {
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/hostping", async (string? host) =>
        {
            host = (host ?? "").Trim();

            if (string.IsNullOrWhiteSpace(host))
            {
                return Results.Ok(new
                {
                    ok = false,
                    host = "",
                    roundTripMs = (long?)null,
                    color = "red",
                    error = "Missing host."
                });
            }

            var sw = Stopwatch.StartNew();

            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 500);
                sw.Stop();

                if (reply.Status != IPStatus.Success)
                {
                    return Results.Ok(new
                    {
                        ok = false,
                        host,
                        roundTripMs = (long?)null,
                        color = "red",
                        error = reply.Status.ToString()
                    });
                }

                var roundTripMs = Math.Max(1L, sw.ElapsedMilliseconds);
                var color = roundTripMs <= 100 ? "green" : "yellow";

                return Results.Ok(new
                {
                    ok = true,
                    host,
                    roundTripMs,
                    color
                });
            }
            catch (Exception ex)
            {
                sw.Stop();

                return Results.Ok(new
                {
                    ok = false,
                    host,
                    roundTripMs = (long?)null,
                    color = "red",
                    error = ex.Message
                });
            }
        });

        return app;
    }
}

public record StartRecordingRequest(double? demoStartSeconds, double? sourceStartSeconds);
public record StopRecordingRequest(double? uiElapsedSeconds, double? programTimerStartOffsetSeconds);
