# ReVue-Remote

ReVue-Remote stores browser-ready MP4 source videos in six-character Rink ID folders and provides the passive browser player controlled by ReVue VRO Remote mode.

## Run locally

```text
dotnet run --project ReVue-Remote/ReVue-Remote.csproj --urls http://0.0.0.0:5080
```

Open `http://localhost:5080`, enter the same Rink ID configured in ReVue VRO, and adjust the volume as needed.

Open `/config` to manage users, valid six-character Rink IDs, and the videos stored under each ID. The first startup creates `admin@admin.com` with temporary password `admin`; it must be changed immediately after the first login. Every account created by an administrator must likewise change its temporary password before accessing management functions.

The Videos page shows the used percentage and available capacity of the filesystem containing the configured video storage directory. This is server disk space, shared by all Rink IDs, rather than a per-Rink quota.

Uploads may use MP4, M4V, MOV, MKV, TS, or M2TS containers and may not exceed 10 GiB each. The video track must already be H.264/AVC using the 8-bit 4:2:0 pixel format (`yuv420p`) and have 720 or 1080 vertical pixels; audio must be AAC or absent. Accepted files are first remuxed without transcoding into a fast-start MP4. The `/config` library then displays resolution and scan type (for example, 1080p or 1080i), frame rate, and GOP as compact buttons, with bitrate as text. Preview opens the selected video in an account-protected player with seeking controls. Clicking a 1080p/1080i button offers conversion to 720p; clicking a frame-rate button above 30 fps offers conversion to 29.97 fps. The 720p and 30-fps-or-lower buttons do nothing. These conversions replace the library entry only after FFmpeg finishes successfully. They preserve AAC audio and source keyframe timestamps; 1080i-to-720p conversion also deinterlaces. Resolution conversion uses a video bitrate ceiling of 5 Mbps, and all conversions limit FFmpeg to two threads. Queue and transcoding percentages appear beside the video. Uploads show no bitrate warning. Unsupported codecs such as H.265/HEVC are rejected.

Install FFmpeg and ffprobe on the host before starting ReVue-Remote:

```text
sudo apt update
sudo apt install -y ffmpeg
ffmpeg -version
ffprobe -version
```

Video processing runs in the background, one file at a time, so an upload request does not remain open during remuxing or transcoding. Pending jobs resume automatically after a service restart. Allow enough free storage for the source and processed MP4 at the same time; the service requires twice the source size plus a 512 MiB safety margin before processing. A video selected by the operator cannot be converted until the operator clears that selection.

## Production configuration

Set these configuration values through `appsettings.Production.json` or environment variables:

```text
ReVueRemote__StorageRoot=/absolute/path/to/persistent/storage
ReVueRemote__FFmpegPath=ffmpeg
ReVueRemote__FFprobePath=ffprobe
```

## Live Rink IDs

In `/config` → Rink IDs, choose **Recorded video playback** or **Live stream (RTMP / SRT)** when creating each Rink ID. Its type cannot be changed afterward. A Live ID requires a publish password at creation, shared by RTMP and SRT. Its Stream settings let you copy either publish URL, copy the SRT Stream ID, or change the password later. Live IDs are omitted from the Videos library. Recorded IDs retain the existing uploaded MP4 workflow and have no stream settings.

Run a MediaMTX service on the same host as ReVue-Remote using [mediamtx.live.example.yml](mediamtx.live.example.yml) as the starting configuration. Enable HTTP/2 on the public HTTPS Nginx listener: several viewer tabs in one browser each hold an EventSource connection, and HTTP/1.1 limits their combined connections to the same host. For Nginx 1.24, use `listen 443 ssl http2;`, then run `sudo nginx -t && sudo systemctl reload nginx`; `curl -sS -o /dev/null -w '%{http_version}\n' --http2 https://HOST/api/health` should print `2`. Use `hlsVariant: fmp4`, one-second segments, and at least 10 retained segments for the delayed preview and recording lead-in; 20 retained segments, as in the example, give more room for recorder startup. The low-latency variant's 200 ms parts add unnecessary browser and HTTP work when several viewers run at once. Generate a random token with `openssl rand -hex 32`, put it in the MediaMTX `authHTTPAddress` query, and set the matching `ReVueRemote__MediaAuthToken` environment variable on ReVue-Remote. Adjust the callback port if ReVue-Remote does not listen on `5080`. Allow RTMP publisher access to TCP `1935`, SRT publisher access to UDP `8890`, and optional public RTSP viewer access to TCP `554`; keep MediaMTX HLS on loopback TCP `8888` and its read-only status API on loopback TCP `9997`. Set `ReVueRemote__MediaMtxHlsBase` or `ReVueRemote__MediaMtxApiBase` only if those internal addresses differ from `http://127.0.0.1:8888` or `http://127.0.0.1:9997`.

Publish to `rtmp://HOST/RINKID`. For a password protected Live ID, use `rtmp://HOST/RINKID?user=publisher&pass=PASSWORD`, URL encoding special characters in the password. The feed must be 1280×720 progressive, H.264/AVC 8-bit 4:2:0, constant 60000/1001 fps, with a 60-frame GOP, **no B-frames**, and AAC-LC audio. The `/config` Rink ID manager shows these requirements under **Encoder settings**, with 4500 kbps CBR video and 48 kHz stereo 128 kbps audio as the recommended rate settings. Set OBS to 59.94 fps, a one-second keyframe interval, and zero B-frames. MediaMTX remuxes the feed to HLS; ReVue VRO and the browser viewers use that same server HLS preview. Remote Live does not run a second RTSP-to-HLS preview on the VRO laptop. In VRO Settings, select **Remote Live**, enter the ReVue-Remote HTTPS host, and enter the Live Rink ID on the main screen. No video folder is selected.

### SRT hardware encoders

Existing Live Rink IDs accept either RTMP or SRT using the same saved publish password. There is no new Rink ID type or separate SRT encryption passphrase. Publish only one feed to a given Rink ID at a time. SRT ingest uses the same HLS preview, Record/Stop, replay, and Next Competitor workflow as RTMP. The video/audio requirements above apply to both protocols; the SRT payload is MPEG-TS, not FLV.

In `/config` → Rink IDs → **Stream settings**, copy the **SRT Stream ID**. For a hardware encoder with separate connection fields, use:

| Encoder field | Setting |
| --- | --- |
| Protocol | SRT |
| Connection mode | Caller (the encoder initiates the connection) |
| Destination / server | ReVue-Remote hostname or public IP |
| Destination port | `8890` / UDP |
| Container / payload | MPEG-TS |
| Stream ID | Paste the complete value copied from Stream settings |
| Encryption / SRT passphrase | Disabled / blank |

Some encoders label their SRT encryption field **Password**. That field is not the ReVue publish password: leave encryption disabled and carry the saved ReVue password in **Stream ID**. Encoder firmware must support Caller mode and a configurable Stream ID; check those capabilities when setting up an AVMATRIX SE-1117 or another hardware encoder. Listener-only encoder configurations are not supported by this push workflow.

The Stream ID uses the standard hardware-compatible syntax `#!::m=publish,r=RINKID,u=publisher,s=PASSWORD`. ReVue percent-encodes the password within the copied Stream ID so delimiters such as commas survive transport, and accepts both literal and encoded SRT passwords. Use the copy button rather than editing encoded credentials by hand. The SRT copy buttons use the saved password; save password changes before copying. Showing the publish password also reveals the Stream ID. Very long escaped passwords can exceed SRT's 512-byte Stream ID limit; the UI then explains that a shorter shared password is needed, without disabling RTMP.

For an encoder accepting one complete connection URL, use **Copy SRT publish URL**. This includes `mode=caller`, the encoded Stream ID, and `pkt_size=1316`. For a client that expects a separate Stream ID, use **Copy SRT Stream ID** instead. The server address is `srt://HOST:8890`; the default port must match the deployed MediaMTX configuration. Set transport latency in the encoder according to the network link; its units vary by encoder.

### Enabling SRT on an existing server

Update ReVue-Remote and enable these settings in the running MediaMTX configuration (the supplied example already includes them):

```yaml
srt: yes
srtAddress: :8890
```

Keep `source: publisher` for the rink paths and the existing HTTP authentication callback/token. Leave `srtPublishPassphrase` unset: SRT uses the existing per-rink publish password for authentication, with no additional media encryption. ReVue rejects SRT reads; viewers continue to use HTTPS/HLS or the existing optional RTSP relay. Pausing a Live Rink ID disconnects both RTMP and SRT publishers and rejects reconnection while paused.

Open **UDP**, not TCP, port `8890` in both the host firewall and any provider firewall. For Ubuntu UFW:

```text
sudo ufw allow 8890/udp
```

On Azure, also add an inbound UDP `8890` rule to the VM's Network Security Group. On a VPS, apply any corresponding provider firewall rule. SRT goes directly to MediaMTX; no new Nginx location or public access to ports `5080`, `8888`, or `9997` is needed. Keep the existing Nginx upstream HTTP connection pooling for HLS and control traffic. Apply service/configuration restarts in a maintenance window because they interrupt active feeds.

The Live Streams page shows live ingest status from the MediaMTX control API and offers an on-demand preview of active feeds. It displays start time, total received data, and a receive rate calculated from successive byte counts. Resolution and codecs appear when the installed MediaMTX version provides them; FPS is not reported by this API. The preview uses the existing HLS feed and stops downloading when closed. Each live stream also has a **Public RTSP** switch, which is off by default. Enabling it requires a separate RTSP password. Its copy button produces `rtsp://viewer:PASSWORD@HOST/RINKID`, with special characters URL encoded. Disabling RTSP or changing its password disconnects current RTSP readers. MediaMTX serves the same encoded tracks through RTSP without transcoding.

The Remote Viewer requires the user to select a role each time it opens; it does not connect or display video with no role selected. It displays three shared panel status indicators at all times: **Judges Ready**, **Tech Panel Ready**, and **Competitor Scored**. The Referee controls Judges Ready, the Technical Controller controls Tech Panel Ready, and the Service Technician controls Competitor Scored. Each connected viewer sees changes immediately; multiple viewers with the same role share one toggle. The flags are saved with the Rink ID playback state, survive a server restart, and reset when the VRO advances from a selected competitor to the next. Status controls work independently of the VRO connection and playback mode. Viewer roles are selected in the app rather than verified against individual accounts.

The VRO operator and remote viewers show the continuous live feed about five seconds behind the publisher, including after Next Competitor. Whenever that continuous preview is visibly playing, the Remote Viewer shows a flashing red **LIVE** indicator, including while the VRO is offline; recordings and VRO-controlled playback do not show it. Without a connected VRO, Live Rink IDs show the incoming feed even if the previous session left a saved replay; replay controls return when the VRO resumes control. Recorded media remains cached until Next Competitor. Record measures the VRO preview's current delay. The server reads actual HLS segment durations to select the corresponding recording start, rather than treating seconds as a segment count. The VRO records and displays the canonical event timeline so clip positions match replay. Every competitor has its own media URLs, including on the VRO local proxy, to prevent browser caches from substituting an earlier competitor's footage. Next Competitor replaces the event player with a fresh rolling preview and enables Record once it is playing near the live edge; there is no catch-up seek during the performance. A viewer refreshed during recording joins the current event position; missing replay history is fetched after recording stops. The server remuxes the event into five-second MP4 archive chunks and one-second fMP4 playback segments without video encoding. Viewers switch to the event HLS once its first five segments are available; the last preview frame is held while the new stream starts so the test pattern does not flash. Stop finalizes the event playlist and limits replay to the operator's recorded duration; Announcer and Service Technician viewers show STAND BY instead of replay. Next deletes that event's server files and viewer cache while the continuous live feed remains available. ReVue-Remote caps combined video responses to each viewer at 30 Mbps. The server storage limit also includes Live event files. Update both ReVue VRO and ReVue-Remote together for this timing change.

The valid Rink ID is the only credential ReVue VRO sends. ReVue VRO can list and download that ID's videos and publish non-destructive playback state, while account authentication remains required for video uploads, deletion, user management, and Rink ID management. Invalid Rink ID validation attempts are delayed by an additional five seconds per failure; the ninth failure warns that one attempt remains, and the tenth blocks the originating IP address for 24 hours. A valid Rink ID resets that IP's failure count. Deploy behind HTTPS and ensure the reverse proxy permits large request bodies, byte-range responses, unbuffered `text/event-stream` responses, and passes the original address in `X-Forwarded-For`.

Run one ReVue-Remote application instance per deployment. One instance can host many simultaneous Rink IDs, but live viewer notifications are currently distributed in memory; a horizontally scaled deployment would need shared pub/sub infrastructure.

Each rink library is stored under `<StorageRoot>/<RINK-ID>/`. Video files are immutable and served with HTTP range requests. Selecting a video publishes a ready state to viewers on that Rink ID; until then, viewers receive no video. The service worker retains every downloaded 64 KiB range. The browser starts buffering the selected video while the NO SIGNAL screen remains visible, targeting five seconds ahead. During recording it permits downloads until the media element reports about nine seconds ahead, then waits for playback to advance. The server paces all video responses to at most 30 Mbps per viewer session, including simultaneous range requests. The remote player carries its viewer ID through recorded video requests and live HLS playlists; direct media requests use a browser session cookie. Stop Recording keeps the ranges for Judge, Referee, and Observer-Judge self-replay and operator-controlled Technical and Observer-Tech replay; Announcer and Service Technician viewers show STAND BY without replay controls. Next Competitor clears the selection, purges the previous video's cache, and returns viewers to NO SIGNAL. The Mbps indicator measures bytes sent by the server to this viewer for the Rink ID, including live media segments, while the MB indicator counts completed ranges stored locally. Browser preload decisions, network speed, and storage quotas can prevent an exact five-second buffer or a complete local recording; missing ranges are fetched on demand during replay.

When Record is pressed, ReVue VRO sends a `preparing` state with the exact source start position while its recorder arms. The remote viewer can fill its bounded buffer at that position before the `recording` state arrives. The selected video keeps the same media resource through `ready`, `preparing`, and `recording`, so Safari does not discard and reload metadata just before playback. On the recording transition the player starts playback while the seek completes. Both ReVue VRO and ReVue-Remote must be updated for this improvement.

User, lockout, and Rink ID records are stored under `<StorageRoot>/_system/`. Back up this directory along with the Rink ID folders. Admin and Regular users may create/delete Rink IDs and upload/delete videos. Only Admin users may create, reset, unlock, change the type of, or delete other users. After the third failed login, retry delays grow by 30 seconds per additional failure; the tenth failure locks the account until an Admin unlocks it.

Playback is aligned at transport boundaries (initial load, operator seek, resume, and pause). During uninterrupted playback the browser is allowed to play every frame at the VRO-selected rate; the Remote player does not periodically seek or change speed to chase network drift. A pause may therefore arrive after normal network latency, but the player then settles on the exact timestamp reported by ReVue VRO.

ReVue VRO renews its Remote playback state every two seconds. After three missed renewals, the connection metric reports VRO Offline. Recorded mode then stops new video downloads. An active Remote Live recording continues on its existing event timeline despite lost control updates, including for refreshed viewers; Stop, Next Competitor, or the server storage limit ends capture. A permanently disconnected VRO therefore leaves its live recording running until a new operator session resets it or the storage limit is reached. Abandoned Live preparation expires after an additional 60 seconds. Accepted Live commands and event cleanup share the rink's state lock, so canceled or outdated requests cannot remove a newer recording. Changing the operator's Rink ID or leaving Remote mode publishes NO SIGNAL to the previous Rink ID immediately when the host is reachable.

## Remote Live regression test

Build ReVue-Remote in Debug, then run `node tests/live-cycles.mjs` from this directory. Requires Node 22+, FFmpeg, MediaMTX, and Chrome/Chromium; `DOTNET_BINARY`, `FFMPEG_BINARY`, `MEDIAMTX_BINARY`, and `CHROME_BINARY` can override executable paths. The test uses temporary storage and loopback ports 11938, 18898, 15098, and 18098, and refuses to run if those ports are occupied.

It publishes video with a changing color time marker, uses real two-second HLS segments, and runs five Record → Stop → Next cycles with a viewer refresh during every recording. It checks decoded frames for old competitor footage, recording jumps, refresh lag, and clip replay. It runs the production server, viewer, and VRO HLS page through a test local proxy; the Windows WebView2 shell itself still requires a Windows check. Add `--control-gap` to interrupt VRO control updates for 65 seconds during competitor three and verify continuous recording, refresh recovery, and rejection of obsolete recording commands. Add `--offline` to check live joins before any VRO, operator disconnect after replay, new joins with an old recording, browser refresh, cache retention, and return to operator-directed replay, across the technical, Judge, Referee, Announcer, and Service Technician roles. Logs and results are retained in the printed temporary directory.

Add `--storage-churn` to either run to create and delete live storage subdirectories throughout the test, exercising capacity measurements during folder cleanup.

## SRT publishing regression tests

After building ReVue-Remote in Debug, run:

```text
node --test tests/publish-settings.test.js
dotnet run --project ../tests/RemotePublishing/RemotePublishing.Tests.csproj
node tests/srt-ingest.mjs
```

The first test validates hardware Stream IDs, URL escaping, IPv6 addresses, shared passwords, and the SRT length limit. The .NET tests verify current and legacy MediaMTX publisher-list/kick APIs, pagination, rink isolation, and independent SRT cleanup when the RTMP API fails.

The integration test requires Node 22+, FFmpeg/ffprobe, MediaMTX, and `srt-live-transmit` (from the SRT project). It uses FFmpeg as a synthetic hardware encoder and the SRT CLI as a Caller, so FFmpeg itself does not need libsrt support. It starts the production ReVue server with isolated temporary account/rink data and real HTTP authentication, verifies shared RTMP/SRT passwords and rejected credentials, plays advancing HLS video frames in Chrome, records and finalizes replay, checks Next Competitor cleanup, pauses/disconnects publishers, rejects paused reconnects, and checks that another rink stays online. `DOTNET_BINARY`, `FFMPEG_BINARY`, `FFPROBE_BINARY`, `MEDIAMTX_BINARY`, and `SRT_BINARY` can override executable paths. The test also uses Chrome/Chromium to check the management page's SRT copy buttons, hidden/revealed credentials, legacy keys, and narrow-screen layout; `CHROME_BINARY` can override its path. It reserves loopback TCP ports `15018`, `19918`, `18818`, `11918` and UDP `18890`; it refuses to run if they are occupied. Logs and results remain in the printed temporary directory. A physical encoder and production firewall still require deployment validation.
