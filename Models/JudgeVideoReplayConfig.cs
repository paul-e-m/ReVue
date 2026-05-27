using System.Text.Json.Serialization;

namespace ElementReview.Models;

public sealed class JudgeVideoReplayConfig
{
    [JsonPropertyOrder(0)]
    public string ServerIp { get; set; } = "127.0.0.1";

    [JsonPropertyOrder(1)]
    public string Role { get; set; } = "";

    [JsonPropertyOrder(2)]
    public string Language { get; set; } = "en";

    [JsonPropertyOrder(3)]
    public int UiZoomPercent { get; set; } = 100;

    [JsonPropertyOrder(4)]
    public JudgeVideoReplayRoleUiConfig? JudgeUI { get; set; }

    [JsonPropertyOrder(5)]
    public JudgeVideoReplayRoleUiConfig? RefereeUI { get; set; }
}

public sealed class JudgeVideoReplayRoleUiConfig
{
    [JsonPropertyOrder(0)]
    public object? DisplayTimerStopwatch { get; set; }

    [JsonPropertyOrder(1)]
    public object? DisplayDanceLiftPresets { get; set; }

    [JsonPropertyOrder(2)]
    public object? UpdateVideoWhileScrubbing { get; set; }
}
