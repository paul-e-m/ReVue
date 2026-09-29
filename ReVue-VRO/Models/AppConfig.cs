using System.Text.Json.Serialization;

namespace ReVueVRO.Models;

public class AppConfig
{
    [JsonPropertyOrder(0)]
    public string Language { get; set; } = "en";

    [JsonPropertyOrder(1)]
    public int UiZoomPercent { get; set; } = 90;

    [JsonPropertyOrder(2)]
    public int ClipMarkerAdvanceMsec { get; set; } = 500;

    [JsonPropertyOrder(3)]
    public bool DemoMode { get; set; } = true;

    [JsonPropertyOrder(4)]
    public string RtspUrl { get; set; } = "rtsp://192.168.6.200:8554/0";

    [JsonPropertyOrder(5)]
    public int SourceFps { get; set; } = 60;

    [JsonPropertyOrder(6)]
    public string RtspTransportProtocol { get; set; } = "UDP";

    [JsonPropertyOrder(7)]
    public bool UseHardwareEncodingWhenAvailable { get; set; } = true;

    [JsonPropertyName("highresVideoGop")]
    [JsonPropertyOrder(8)]
    public int HighresVideoGop { get; set; } = 2;

    [JsonPropertyName("lowresVideoBitrate")]
    [JsonPropertyOrder(9)]
    public int LowresVideoBitrate { get; set; } = 3500;

    [JsonPropertyOrder(10)]
    [JsonPropertyName("lowresVideoGop")]
    public int LowresVideoGop { get; set; } = 30;

    [JsonPropertyOrder(11)]
    public string CSSLink { get; set; } = "None";

    [JsonPropertyOrder(12)]
    public string DatabaseLocation { get; set; } = "localhost";

    [JsonPropertyOrder(13)]
    public string EventId { get; set; } = "";

    [JsonPropertyOrder(14)]
    public string SelectedCategoryID { get; set; } = "";

    [JsonPropertyOrder(15)]
    public string SelectedSegmentID { get; set; } = "";

    [JsonPropertyOrder(16)]
    [JsonPropertyName("onlineCssData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OnlineCssData? OnlineCssData =>
        string.Equals(CSSLink?.Trim(), "Online CSS", StringComparison.OrdinalIgnoreCase)
            ? new OnlineCssData
            {
                EventId = EventId ?? "",
                ActiveCategoryId = SelectedCategoryID ?? "",
                ActiveSegmentId = SelectedSegmentID ?? ""
            }
            : null;

    [JsonPropertyOrder(17)]
    public string CSSServerHost { get; set; } = "";

    [JsonPropertyOrder(18)]
    public bool SaveVideos { get; set; } = false;

    [JsonPropertyOrder(19)]
    public string SavedVideosFolder { get; set; } = "C:/Event_Videos";

    [JsonPropertyOrder(20)]
    public bool AutoplaySelectedClip { get; set; } = false;

    [JsonPropertyOrder(21)]
    public string ManualHalfwayTimingPreset { get; set; } = "None";

    // VideoSourceMode supersedes DemoMode while keeping DemoMode in the
    // serialized configuration for compatibility with earlier releases.
    [JsonPropertyOrder(22)]
    public string VideoSourceMode { get; set; } = "";

    [JsonPropertyOrder(23)]
    public string RemoteHostUrl { get; set; } = "";

    [JsonPropertyOrder(24)]
    public string RemoteSessionCode { get; set; } = "";

    [JsonPropertyOrder(25)]
    public string RemoteVideoId { get; set; } = "";

    [JsonPropertyOrder(26)]
    public string RemoteVideoFolder { get; set; } = "";

    [JsonPropertyOrder(27)]
    public string RemoteVideoLocalPath { get; set; } = "";

    [JsonPropertyOrder(28)]
    public List<RemoteVideoMapping> RemoteVideoMappings { get; set; } = [];

    [JsonPropertyOrder(29)]
    public int RemoteVideoCacheMaximumFiles { get; set; } = 50;

    [JsonPropertyOrder(30)]
    public int RemoteVideoCacheExpirationHours { get; set; } = 16;
}

public class OnlineCssData
{
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = "";

    [JsonPropertyName("activeCategoryId")]
    public string ActiveCategoryId { get; set; } = "";

    [JsonPropertyName("activeSegmentId")]
    public string ActiveSegmentId { get; set; } = "";
}

public sealed class RemoteVideoMapping
{
    public string HostUrl { get; set; } = "";
    public string SessionCode { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string RemoteVideoId { get; set; } = "";
}
