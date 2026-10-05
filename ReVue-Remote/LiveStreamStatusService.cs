using System.Text.Json;

namespace ReVueRemote;

// Reads MediaMTX path status and disconnects an RTMP publisher when an operator
// pauses its Live Rink ID.
public sealed class LiveStreamStatusService
{
    private readonly HttpClient _client;
    private readonly string _apiBase;
    private readonly ILogger<LiveStreamStatusService> _logger;

    public LiveStreamStatusService(
        IHttpClientFactory clients, IConfiguration configuration,
        ILogger<LiveStreamStatusService> logger)
    {
        _client = clients.CreateClient("mediamtx-status");
        _apiBase = (configuration["ReVueRemote:MediaMtxApiBase"] ?? "http://127.0.0.1:9997").TrimEnd('/');
        _logger = logger;
    }

    public async Task<LiveStreamStatusSnapshot> GetAsync(
        IEnumerable<ManagedSessionKeyRecord> liveKeys, RemoteSessionStore sessions,
        CancellationToken cancellationToken)
    {
        var keys = liveKeys.ToArray();
        var paths = new Dictionary<string, LiveStreamStatus>(StringComparer.Ordinal);
        var available = true;
        try
        {
            for (var page = 0; page < 20; page++)
            {
                using var response = await _client.GetAsync(
                    $"{_apiBase}/v3/paths/list?page={page}&itemsPerPage=500", cancellationToken);
                response.EnsureSuccessStatusCode();
                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var root = document.RootElement;
                if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    throw new JsonException("MediaMTX did not return a path list.");
                foreach (var item in items.EnumerateArray())
                {
                    var code = String(item, "name");
                    if (code is null) continue;
                    var ready = Boolean(item, "online") ?? Boolean(item, "ready") ?? false;
                    var started = DateTimeOffset.TryParse(
                        String(item, "onlineTime") ?? String(item, "readyTime"), out var parsed)
                        ? parsed : (DateTimeOffset?)null;
                    var bytes = Integer(item, "inboundBytes") ?? Integer(item, "bytesReceived");
                    var (width, height, codecs) = ReadTracks(item);
                    paths[code] = new(code, true, ready, ready ? started : null,
                        ready ? bytes : null, ready ? width : null, ready ? height : null,
                        ready ? codecs : Array.Empty<string>(), false, 0, []);
                }
                var pageCount = Integer(root, "pageCount") ?? 1;
                if (page + 1 >= pageCount) break;
                if (page == 19) throw new JsonException("MediaMTX returned too many path pages.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            available = false;
        }

        var streams = new List<LiveStreamStatus>(keys.Length);
        foreach (var key in keys)
        {
            var connections = await sessions.GetRemoteConnectionStatusAsync(key.Code, cancellationToken);
            var media = paths.GetValueOrDefault(key.Code) ?? new LiveStreamStatus(
                key.Code, key.LiveFeedActive, false, null, null, null, null,
                Array.Empty<string>(), false, 0, []);
            streams.Add(media with
            {
                Active = key.LiveFeedActive,
                VroConnected = connections.VroConnected,
                RemoteClientCount = connections.RemoteClientCount,
                Roles = connections.Roles
            });
        }
        return new(available, streams.ToArray());
    }

    public async Task KickPublisherAsync(string rinkId, CancellationToken cancellationToken)
    {
        rinkId = RemoteSessionStore.NormalizeSessionCode(rinkId);
        try
        {
            for (var page = 0; page < 20; page++)
            {
                using var response = await _client.GetAsync(
                    $"{_apiBase}/v3/rtmpconns/list?page={page}&itemsPerPage=500", cancellationToken);
                response.EnsureSuccessStatusCode();
                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var root = document.RootElement;
                if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    throw new JsonException("MediaMTX did not return an RTMP connection list.");
                foreach (var item in items.EnumerateArray())
                {
                    var id = String(item, "id");
                    var path = String(item, "path");
                    var state = String(item, "state");
                    if (id is null || !string.Equals(path, rinkId, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(state, "publish", StringComparison.OrdinalIgnoreCase)) continue;
                    using var kicked = await _client.PostAsync(
                        $"{_apiBase}/v3/rtmpconns/kick/{Uri.EscapeDataString(id)}", null, cancellationToken);
                    kicked.EnsureSuccessStatusCode();
                }
                var pageCount = Integer(root, "pageCount") ?? 1;
                if (page + 1 >= pageCount) break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex,
                "The live feed for {RinkId} was paused, but MediaMTX did not accept the publisher disconnect request",
                rinkId);
        }
    }

    private static (int? Width, int? Height, string[] Codecs) ReadTracks(JsonElement path)
    {
        var codecs = new List<string>();
        int? width = null, height = null;
        if (path.TryGetProperty("tracks2", out var tracks2) && tracks2.ValueKind == JsonValueKind.Array)
        {
            foreach (var track in tracks2.EnumerateArray())
            {
                var codec = String(track, "codec");
                if (codec is not null) codecs.Add(codec);
                if (!track.TryGetProperty("codecProps", out var props) ||
                    props.ValueKind != JsonValueKind.Object) continue;
                var trackWidth = Integer(props, "width");
                var trackHeight = Integer(props, "height");
                if (trackWidth is > 0 and <= 16384 && trackHeight is > 0 and <= 16384)
                    (width, height) = ((int)trackWidth.Value, (int)trackHeight.Value);
            }
        }
        else if (path.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
        {
            foreach (var track in tracks.EnumerateArray())
                if (track.ValueKind == JsonValueKind.String && track.GetString() is { } codec)
                    codecs.Add(codec);
        }
        return (width, height, codecs.ToArray());
    }

    private static string? String(JsonElement value, string property)
        => value.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String
            ? child.GetString() : null;

    private static bool? Boolean(JsonElement value, string property)
        => value.TryGetProperty(property, out var child) &&
            (child.ValueKind == JsonValueKind.True || child.ValueKind == JsonValueKind.False)
            ? child.GetBoolean() : null;

    private static long? Integer(JsonElement value, string property)
        => value.TryGetProperty(property, out var child) &&
            child.ValueKind == JsonValueKind.Number && child.TryGetInt64(out var result)
            ? result : null;
}

public sealed record LiveStreamStatusSnapshot(bool Available, LiveStreamStatus[] Streams);
public sealed record LiveStreamStatus(
    string Code, bool Active, bool Ready, DateTimeOffset? StartedAtUtc, long? TotalBytes,
    int? Width, int? Height, string[] Codecs, bool VroConnected,
    int RemoteClientCount, RemoteViewerRoleCount[] Roles);
