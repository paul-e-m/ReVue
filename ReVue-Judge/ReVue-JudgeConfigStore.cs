using System.Text.Json;

namespace ReVueJudge;

internal static class ReVueJudgeConfigStore
{
    public const string JudgeRole = "judge";
    public const string RefereeRole = "referee";
    public const int DefaultUiZoomPercent = 100;
    public const int MinUiZoomPercent = 50;
    public const int MaxUiZoomPercent = 150;
    private const string AppFolderName = "ReVue";
    private const string AppSpecificFolderName = "ReVue-Judge";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static string SharedAppRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    public static string AppDataRoot => Path.Combine(SharedAppRoot, AppSpecificFolderName);

    public static string ConfigPath => Path.Combine(AppDataRoot, "appconfig.json");
    public static string ReplayMediaDirectory => Path.Combine(SharedAppRoot, "media");
    public static string WebView2UserDataDir => Path.Combine(AppDataRoot, "WebView2");

    public static ReVueJudgeConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            var config = Normalize(new ReVueJudgeConfig());
            Save(config);
            return config;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = Normalize(JsonSerializer.Deserialize<ReVueJudgeConfig>(json, JsonOptions));
            Save(config);
            return config;
        }
        catch
        {
            return Normalize(new ReVueJudgeConfig());
        }
    }

    public static ReVueJudgeConfig Save(ReVueJudgeConfig? config)
    {
        config = Normalize(config);
        Directory.CreateDirectory(AppDataRoot);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
        return config;
    }

    public static ReVueJudgeConfig Normalize(ReVueJudgeConfig? config)
    {
        config ??= new ReVueJudgeConfig();
        config.ServerIp = string.IsNullOrWhiteSpace(config.ServerIp)
            ? "127.0.0.1"
            : config.ServerIp.Trim();
        config.Language = string.Equals(config.Language?.Trim(), "fr", StringComparison.OrdinalIgnoreCase)
            ? "fr"
            : "en";
        config.Role = NormalizeRole(config.Role);
        config.JudgeUI = NormalizeRoleUi(
            config.JudgeUI,
            displayTimerStopwatch: true,
            displayDanceLiftPresets: false,
            updateVideoWhileScrubbing: true);
        config.RefereeUI = NormalizeRoleUi(
            config.RefereeUI,
            displayTimerStopwatch: true,
            displayDanceLiftPresets: true,
            updateVideoWhileScrubbing: true);
        config.UiZoomPercent = Math.Clamp(config.UiZoomPercent, MinUiZoomPercent, MaxUiZoomPercent);
        return config;
    }

    private static string NormalizeRole(string? role)
    {
        var normalized = role?.Trim().ToLowerInvariant();
        return normalized switch
        {
            JudgeRole => JudgeRole,
            RefereeRole => RefereeRole,
            _ => RefereeRole
        };
    }

    private static ReVueJudgeRoleUiConfig NormalizeRoleUi(
        ReVueJudgeRoleUiConfig? config,
        bool displayTimerStopwatch,
        bool displayDanceLiftPresets,
        bool updateVideoWhileScrubbing)
    {
        config ??= new ReVueJudgeRoleUiConfig();
        config.DisplayTimerStopwatch = NormalizeBooleanValue(config.DisplayTimerStopwatch, displayTimerStopwatch);
        config.DisplayDanceLiftPresets = NormalizeBooleanValue(config.DisplayDanceLiftPresets, displayDanceLiftPresets);
        config.UpdateVideoWhileScrubbing = NormalizeBooleanValue(config.UpdateVideoWhileScrubbing, updateVideoWhileScrubbing);
        return config;
    }

    private static string NormalizeBooleanValue(object? value, bool defaultValue)
        => IsBooleanValueEnabled(value, defaultValue) ? "true" : "false";

    private static bool IsBooleanValueEnabled(object? value, bool defaultValue)
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
                JsonValueKind.String => IsBooleanValueEnabled(element.GetString(), defaultValue),
                _ => defaultValue
            };
        }

        var normalized = value?.ToString()?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "true" or "1" or "yes" or "y" or "on" => true,
            "false" or "0" or "no" or "n" or "off" => false,
            _ => defaultValue
        };
    }
}
