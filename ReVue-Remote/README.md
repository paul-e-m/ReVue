# ReVue-Remote

ReVue-Remote stores browser-ready MP4 source videos in six-character Rink ID folders and provides the passive browser player controlled by ReVue VRO Remote mode.

## Run locally

```text
dotnet run --project ReVue-Remote/ReVue-Remote.csproj --urls http://0.0.0.0:5080
```

Open `http://localhost:5080`, enter the same Rink ID configured in ReVue VRO, and adjust the volume as needed.

Open `/config` to manage users, valid six-character Rink IDs, and the videos stored under each ID. The first startup creates `admin@admin.com` with temporary password `admin`; it must be changed immediately after the first login. Every account created by an administrator must likewise change its temporary password before accessing management functions.

Uploads may use MP4, M4V, MOV, MKV, TS, or M2TS containers and may not exceed 10 GiB each. The video track must already be H.264/AVC using the 8-bit 4:2:0 pixel format (`yuv420p`); audio must be AAC or absent. ReVue-Remote validates each upload and remuxes it, without transcoding, into one fast-start MP4. The original upload and all temporary files are deleted after processing. Unsupported codecs such as H.265/HEVC are rejected rather than transcoded.

Install FFmpeg and ffprobe on the host before starting ReVue-Remote:

```text
sudo apt update
sudo apt install -y ffmpeg
ffmpeg -version
ffprobe -version
```

Video processing runs in the background, one file at a time, so an upload request does not remain open during remuxing. Pending processing jobs resume automatically after a service restart. Allow enough free storage for the uploaded source and processed MP4 at the same time; the service requires twice the source size plus a 512 MiB safety margin before processing.

## Production configuration

Set these configuration values through `appsettings.Production.json` or environment variables:

```text
ReVueRemote__StorageRoot=/absolute/path/to/persistent/storage
ReVueRemote__FFmpegPath=ffmpeg
ReVueRemote__FFprobePath=ffprobe
```

The valid Rink ID is the only credential ReVue VRO sends. ReVue VRO can list and download that ID's videos and publish non-destructive playback state, while account authentication remains required for video uploads, deletion, user management, and Rink ID management. Invalid Rink ID validation attempts are delayed by an additional five seconds per failure; the ninth failure warns that one attempt remains, and the tenth blocks the originating IP address for 24 hours. A valid Rink ID resets that IP's failure count. Deploy behind HTTPS and ensure the reverse proxy permits large request bodies, byte-range responses, unbuffered `text/event-stream` responses, and passes the original address in `X-Forwarded-For`.

Run one ReVue-Remote application instance per deployment. One instance can host many simultaneous Rink IDs, but live viewer notifications are currently distributed in memory; a horizontally scaled deployment would need shared pub/sub infrastructure.

Each rink library is stored under `<StorageRoot>/<RINK-ID>/`. Video files are immutable and can be served with HTTP range requests. The passive player has no transport or timeline controls; ReVue VRO publishes the selected file, position, play/pause state, forward playback speed, element clips, program/halfway markers, and synchronized zoom state. When recording begins, the viewer caches that complete video in the browser for replay; starting a different recording replaces the previous cached video.

User, lockout, and Rink ID records are stored under `<StorageRoot>/_system/`. Back up this directory along with the Rink ID folders. Admin and Regular users may create/delete Rink IDs and upload/delete videos. Only Admin users may create, reset, unlock, change the type of, or delete other users. After the third failed login, retry delays grow by 30 seconds per additional failure; the tenth failure locks the account until an Admin unlocks it.

Playback is aligned at transport boundaries (initial load, operator seek, resume, and pause). During uninterrupted playback the browser is allowed to play every frame at the VRO-selected rate; the Remote player does not periodically seek or change speed to chase network drift. A pause may therefore arrive after normal network latency, but the player then settles on the exact timestamp reported by ReVue VRO.
