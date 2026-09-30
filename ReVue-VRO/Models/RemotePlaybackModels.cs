namespace ReVueVRO.Models;

public class RemoteVideoDescriptor
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset UploadedAtUtc { get; set; }
}

public class RemoteConnectionRequest
{
    public string HostUrl { get; set; } = "";
    public string SessionCode { get; set; } = "";
}

public sealed class RemoteRinkIdRequest
{
    public string SessionCode { get; set; } = "";
}

public sealed class RemoteVideoFolderUploadRequest : RemoteConnectionRequest
{
    public string FolderPath { get; set; } = "";
    public List<string> LocalPaths { get; set; } = [];
}

public sealed class LocalRemoteVideoDescriptor
{
    public string LocalPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string RemoteVideoId { get; set; } = "";
    public bool IsUploaded { get; set; }
}

public sealed class SelectLocalRemoteVideoRequest
{
    public string LocalPath { get; set; } = "";
    public string VideoId { get; set; } = "";
}

public sealed class RemoteVideoLibraryItem : RemoteVideoDescriptor
{
    public string Status { get; set; } = "available";
    public bool IsSelected { get; set; }
    public string DownloadJobId { get; set; } = "";
    public double ProgressPercent { get; set; }
    public string DownloadError { get; set; } = "";
}

public sealed class UploadedLocalVideoResult
{
    public string LocalPath { get; set; } = "";
    public RemoteVideoDescriptor RemoteVideo { get; set; } = new();
}

public sealed class RemotePlaybackCommand
{
    public string OperatorInstanceId { get; set; } = "";
    public long OperatorGeneration { get; set; }
    public long OperatorSequence { get; set; }
    public string VideoId { get; set; } = "";
    public double PositionSeconds { get; set; }
    public double TimelinePositionSeconds { get; set; }
    public double TimelineDurationSeconds { get; set; }
    public bool IsPlaying { get; set; }
    public double PlaybackRate { get; set; } = 1;
    public long PlaybackDiscontinuity { get; set; }
    public string Mode { get; set; } = "paused";
    public double? ProgramStartSeconds { get; set; }
    public double? HalfwaySeconds { get; set; }
    public double? OpenClipStartSeconds { get; set; }
    public double ZoomScale { get; set; } = 1;
    public double ZoomOffsetX { get; set; }
    public double ZoomOffsetY { get; set; }
    public List<RemoteTimelineClip> Clips { get; set; } = [];
}

public sealed class LocalRemotePlaybackCommand
{
    public string Mode { get; set; } = "replay";
    public double PositionSeconds { get; set; }
    public double TimelinePositionSeconds { get; set; }
    public double TimelineDurationSeconds { get; set; }
    public bool IsPlaying { get; set; }
    public double PlaybackRate { get; set; } = 1;
    public long PlaybackDiscontinuity { get; set; }
    public bool IsReverse { get; set; }
    public double? ProgramStartSeconds { get; set; }
    public double? HalfwaySeconds { get; set; }
    public double? OpenClipStartSeconds { get; set; }
    public double ZoomScale { get; set; } = 1;
    public double ZoomOffsetX { get; set; }
    public double ZoomOffsetY { get; set; }
    public List<RemoteTimelineClip> Clips { get; set; } = [];
}

public sealed class RemoteTimelineClip
{
    public int Index { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
}
