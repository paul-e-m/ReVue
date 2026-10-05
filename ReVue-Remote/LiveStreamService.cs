using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ReVueRemote;

// FFmpeg only remuxes the publisher's fixed H.264/AAC feed. The independent
// five-second MP4 files are retained for the current competitor; the one-second
// fMP4 HLS segments let browsers start and seek without downloading a whole event.
public sealed partial class LiveStreamService : IDisposable
{
    private readonly string _storageRoot;
    private readonly string _ffmpegPath;
    private readonly string _hlsBase;
    private readonly ConcurrentDictionary<string, Process> _processes = new(StringComparer.Ordinal);
    private readonly ILogger<LiveStreamService> _logger;
    private readonly RemoteSessionStore _sessions;
    private readonly IHttpClientFactory _clients;

    public LiveStreamService(IConfiguration config, IWebHostEnvironment environment,
        RemoteSessionStore sessions, ILogger<LiveStreamService> logger, IHttpClientFactory clients)
    {
        _sessions = sessions;
        _clients = clients;
        _logger = logger;
        var configuredRoot = config["ReVueRemote:StorageRoot"]?.Trim();
        _storageRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(environment.ContentRootPath, "data", "sessions")
            : Path.GetFullPath(configuredRoot, environment.ContentRootPath);
        _ffmpegPath = config["ReVueRemote:FfmpegPath"]?.Trim() is { Length: > 0 } configuredFfmpeg
            ? configuredFfmpeg : "ffmpeg";
        _hlsBase = (config["ReVueRemote:MediaMtxHlsBase"] ?? "http://127.0.0.1:8888").TrimEnd('/');
    }

    [GeneratedRegex("^(?:index\\.m3u8|init\\.mp4|seg_[0-9]{6}\\.m4s)$", RegexOptions.CultureInvariant)]
    private static partial Regex EventFileNameRegex();

    private static string RequireCode(string code)
    {
        code = RemoteSessionStore.NormalizeSessionCode(code);
        if (!RemoteSessionStore.IsValidSessionCode(code))
            throw new ArgumentException("Invalid Rink ID.", nameof(code));
        return code;
    }

    private static string RequireEventId(string eventId)
    {
        if (!Guid.TryParseExact(eventId, "N", out _))
            throw new ArgumentException("Invalid live event ID.", nameof(eventId));
        return eventId;
    }

    private string EventDirectory(string code, string eventId)
        => Path.Combine(_storageRoot, RequireCode(code), "live", RequireEventId(eventId));

    public string? GetEventFile(string code, string eventId, string name)
    {
        if (!EventFileNameRegex().IsMatch(name)) return null;
        var path = Path.Combine(EventDirectory(code, eventId), name);
        return File.Exists(path) ? path : null;
    }

    public async Task<bool> StartEventAsync(string code, string eventId, int? liveDelaySeconds = null,
        CancellationToken cancellationToken = default)
    {
        code = RequireCode(code);
        eventId = RequireEventId(eventId);
        var key = code + ":" + eventId;
        if (_processes.TryGetValue(key, out var existing) && !existing.HasExited) return false;
        // Never overwrite an event's cached segments following a failed
        // producer or a repeated control request.
        if (File.Exists(Path.Combine(EventDirectory(code, eventId), "index.m3u8")))
            throw new IOException("The live recording stopped unexpectedly. Its saved footage has been retained; stop recording before starting a new competitor.");
        var storage = _sessions.GetStorageStatus();
        if (storage is null || storage.AvailableWithinLimitBytes <= 0)
            throw new IOException("Maximum ReVue video storage capacity has been reached.");

        var delaySeconds = Math.Clamp(liveDelaySeconds ?? 5, 4, 7);
        var inputUrl = new Uri($"{_hlsBase}/{code}/index.m3u8");
        // MediaMTX segment lengths depend on actual keyframes. A delay in
        // seconds must not be passed to FFmpeg as a count of segments.
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(4));
        var delaySegments = await GetStartSegmentCountAsync(inputUrl, delaySeconds, startup.Token);

        var eventDirectory = EventDirectory(code, eventId);
        var archiveDirectory = Path.Combine(eventDirectory, "archive");
        Directory.CreateDirectory(archiveDirectory);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                WorkingDirectory = eventDirectory
            },
            EnableRaisingEvents = true
        };
        var args = process.StartInfo.ArgumentList;
        // The operator and viewers watch HLS a few seconds behind the publisher.
        // Read from that same position so the saved event starts on the frame
        // visible when Record was pressed, rather than on a future RTSP frame.
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "warning", "-y",
            "-live_start_index", $"-{delaySegments}", "-analyzeduration", "0", "-probesize", "32768",
            "-i", inputUrl.AbsoluteUri, "-map", "0:v:0", "-map", "0:a:0", "-c", "copy",
            "-f", "hls", "-hls_time", "1", "-hls_list_size", "0", "-hls_playlist_type", "event",
            "-hls_segment_type", "fmp4", "-hls_flags", "independent_segments+temp_file+program_date_time",
            "-hls_segment_filename", Path.Combine(eventDirectory, "seg_%06d.m4s"),
            Path.Combine(eventDirectory, "index.m3u8"),
            "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-f", "segment",
            "-segment_time", "5", "-reset_timestamps", "1", "-segment_format", "mp4",
            Path.Combine(archiveDirectory, "chunk_%06d.mp4") }) args.Add(arg);
        process.ErrorDataReceived += (_, line) =>
        {
            if (!string.IsNullOrWhiteSpace(line.Data))
                _logger.LogWarning("Live remux {RinkId}/{EventId}: {Message}", code, eventId, line.Data);
        };
        process.Exited += (_, _) =>
        {
            var unexpected = ((ICollection<KeyValuePair<string, Process>>)_processes)
                .Remove(new KeyValuePair<string, Process>(key, process));
            int? exitCode = null;
            try { exitCode = process.ExitCode; } catch (InvalidOperationException) { }
            _logger.LogInformation("Live remux stopped for {RinkId}/{EventId} with exit code {ExitCode}",
                code, eventId, exitCode);
            if (unexpected)
                _logger.LogError("Live remux exited unexpectedly for {RinkId}/{EventId}; saved footage retained", code, eventId);
        };
        if (!_processes.TryAdd(key, process)) { process.Dispose(); return false; }
        try
        {
            process.Start();
            _logger.LogInformation("Live remux started for {RinkId}/{EventId}", code, eventId);
            process.BeginErrorReadLine();
            _ = MonitorCapacityAsync(key, process);
            return true;
        }
        catch
        {
            ((ICollection<KeyValuePair<string, Process>>)_processes)
                .Remove(new KeyValuePair<string, Process>(key, process));
            process.Dispose();
            throw;
        }
    }

    private async Task<int> GetStartSegmentCountAsync(Uri url, double delaySeconds,
        CancellationToken cancellationToken)
    {
        var client = _clients.CreateClient("live-preview");
        for (var depth = 0; depth < 3; depth++)
        {
            var playlist = await client.GetStringAsync(url, cancellationToken);
            var lines = playlist.Split('\n', StringSplitOptions.TrimEntries);
            var durations = new List<double>();
            Uri? variant = null;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("#EXTINF:", StringComparison.Ordinal) &&
                    double.TryParse(lines[i][8..].Split(',')[0], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var duration) && duration > 0)
                    durations.Add(duration);
                if (variant is null && lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    var next = lines.Skip(i + 1).FirstOrDefault(line => line.Length > 0 && line[0] != '#');
                    if (next is not null) variant = new Uri(url, next);
                }
            }
            if (durations.Count > 0) return StartSegmentCount(durations, delaySeconds);
            if (variant is null) break;
            url = variant;
        }
        throw new IOException("The live stream has no complete video segments yet. Try Record again shortly.");
    }

    internal static int StartSegmentCount(IReadOnlyList<double> durations, double delaySeconds)
    {
        var total = 0d;
        var closest = double.PositiveInfinity;
        var count = 1;
        for (var n = 1; n <= durations.Count; n++)
        {
            total += durations[durations.Count - n];
            var difference = Math.Abs(total - delaySeconds);
            if (difference < closest) { closest = difference; count = n; }
            if (total >= delaySeconds) break;
        }
        return count;
    }

    private async Task MonitorCapacityAsync(string key, Process process)
    {
        try
        {
            while (!process.HasExited)
            {
                await Task.Delay(2000);
                if (process.HasExited || !_processes.TryGetValue(key, out var current) || current != process) break;
                var storage = _sessions.GetStorageStatus();
                if (storage is null)
                {
                    _logger.LogWarning("Live recording storage measurement unavailable for {EventKey}; retrying", key);
                    continue;
                }
                if (storage.AvailableWithinLimitBytes > 0) continue;
                _logger.LogError("Stopping live recording {EventKey}: maximum ReVue video storage capacity reached", key);
                var parts = key.Split(':', 2);
                await File.WriteAllTextAsync(Path.Combine(EventDirectory(parts[0], parts[1]), "error.txt"),
                    "Maximum ReVue video storage capacity has been reached.");
                await StopProcessAsync(process);
                break;
            }
        }
        catch (InvalidOperationException) { /* The event finished and its process was disposed. */ }
        catch (Exception ex) { _logger.LogWarning(ex, "Unable to monitor live recording capacity"); }
    }

    public async Task FinishEventAsync(string code, string eventId, double? durationSeconds = null)
    {
        var key = RequireCode(code) + ":" + RequireEventId(eventId);
        if (_processes.TryRemove(key, out var process)) await StopProcessAsync(process);
        if (durationSeconds is > 0 && double.IsFinite(durationSeconds.Value))
            await LimitReplayToRecordingAsync(code, eventId, durationSeconds.Value);
    }

    private async Task LimitReplayToRecordingAsync(string code, string eventId, double durationSeconds)
    {
        var directory = EventDirectory(code, eventId);
        var playlistPath = Path.Combine(directory, "index.m3u8");
        if (!File.Exists(playlistPath)) return;
        var lines = await File.ReadAllLinesAsync(playlistPath);
        var retained = new List<string>();
        var removed = new List<string>();
        var segmentDuration = 0d;
        var retainedDuration = 0d;
        var retaining = true;
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var comma = line.IndexOf(',', 8);
                var durationText = comma > 8 ? line.AsSpan(8, comma - 8) : line.AsSpan(8);
                if (double.TryParse(durationText, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var parsed))
                    segmentDuration = parsed;
                if (retainedDuration >= durationSeconds + 1) retaining = false;
            }
            if (EventFileNameRegex().IsMatch(line) && line.StartsWith("seg_", StringComparison.Ordinal))
            {
                if (retaining)
                {
                    retainedDuration += segmentDuration;
                    retained.Add(line);
                }
                else removed.Add(line);
                segmentDuration = 0;
                continue;
            }
            if (line == "#EXT-X-ENDLIST") continue;
            if (retaining || (!line.StartsWith("#EXTINF:", StringComparison.Ordinal) &&
                              !line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.Ordinal)))
                retained.Add(line);
        }
        if (removed.Count == 0) return;
        retained.Add("#EXT-X-ENDLIST");
        var temporary = playlistPath + ".trim";
        await File.WriteAllLinesAsync(temporary, retained);
        File.Move(temporary, playlistPath, true);
        foreach (var name in removed) File.Delete(Path.Combine(directory, name));
        _logger.LogInformation("Limited replay for {RinkId}/{EventId} to {Duration:F1}s ({Removed} extra segments)",
            code, eventId, durationSeconds, removed.Count);
    }

    public async Task DeleteEventAsync(string code, string eventId)
    {
        await FinishEventAsync(code, eventId);
        var path = EventDirectory(code, eventId);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    public async Task StopInactiveEventsAsync()
    {
        foreach (var key in _processes.Keys.ToArray())
        {
            var parts = key.Split(':', 2);
            if (parts.Length != 2) continue;
            await _sessions.WithPlaybackStateAsync(parts[0], async state =>
            {
                if (state.VideoId == parts[1] && state.Mode is "recording" or "preparing") return;
                _logger.LogWarning("Finishing inactive live remux {RinkId}/{EventId}: mode {Mode}, current event {CurrentEvent}",
                    parts[0], parts[1], state.Mode, state.VideoId);
                await FinishEventAsync(parts[0], parts[1],
                    state.VideoId == parts[1] && state.Mode == "replay"
                        ? state.TimelineDurationSeconds : null);
                if (state.VideoId != parts[1])
                {
                    var path = EventDirectory(parts[0], parts[1]);
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                }
            });
        }
    }

    public async Task StopAllForRinkAsync(string code)
    {
        code = RequireCode(code);
        foreach (var key in _processes.Keys.Where(key => key.StartsWith(code + ":", StringComparison.Ordinal)).ToArray())
        {
            var parts = key.Split(':', 2);
            await FinishEventAsync(parts[0], parts[1]);
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("q");
                await process.StandardInput.FlushAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); }
            }
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    public void Dispose()
    {
        foreach (var process in _processes.Values)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            process.Dispose();
        }
        _processes.Clear();
    }
}
