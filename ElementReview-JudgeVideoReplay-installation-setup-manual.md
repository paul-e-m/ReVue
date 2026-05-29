# ElementReview and JudgeVideoReplay Installation and Setup Manual

This manual covers a normal event setup with one VRO computer and separate judge/referee computers.

## Which App Goes Where

Install `ElementReview` only on the VRO computer.

The VRO computer records the incoming video, creates the replay files, runs the local ElementReview UI for the VRO, and serves replay clips to the judging panel.

Install `JudgeVideoReplay` on every judge and referee computer.

JudgeVideoReplay is the panel replay client. It does not record video and does not replace ElementReview on the VRO computer.

## Before Installing

Confirm that all computers are on the same event network and that the VRO computer has a stable (not dynamic) IP address. The judge and referee computers will use that VRO IP address to connect to ElementReview.

On Windows 10 computers, ElementReview and JudgeVideoReplay require the Microsoft Edge WebView2 Runtime. If either app opens to a blank window or fails immediately on a Windows 10 computer, install the [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/#download-section) and start the app again. Do not install WebView2 separately on Windows 11 as this runtime is already included in Windows 11.

## Recommended Computer Requirements

ElementReview does the heavier work. It records the incoming stream, creates high-res and low-res replay video, serves replay files to the panel, and writes/deletes a large amount of video data during an event.

Minimum recommended VRO computer for ElementReview:

| Component | Minimum recommendation | Preferred event setup |
| --- | --- | --- |
| CPU | Recent Intel Core i5/i7 or AMD Ryzen 5/7, 4 cores or better | Intel Core i7/i9 or AMD Ryzen 7/9, 6 cores or better |
| RAM | `16 GB` | `32 GB` if available |
| Graphics | Integrated graphics may work for basic use | Hardware video encoding support is strongly preferred, such as Intel Quick Sync, NVIDIA NVENC, or AMD hardware encoding |
| Storage | SSD, `1 TB` minimum with plenty of free space |  |
| Network | Wired Gigabit Ethernet |  |

Do not use a mechanical hard drive for the VRO computer. Avoid small capacity, or bargain no name drives for the VRO computer. ElementReview writes and deletes video data continuously, so an SSD gives better recording/replay performance. A `1 TB` or larger SSD is recommended because smaller drives can fill quickly and may wear faster when used repeatedly for video recording workloads. Ensure a minimum 15-20% drive capacity is available at all times.

For JudgeVideoReplay computers, the requirements are lighter because they receive and play back low-res replay video rather than recording and encoding the main feed.

Minimum recommended judge/referee computer:

| Component | Minimum recommendation |
| --- | --- |
| CPU | Recent Intel Core i3/i5 or AMD Ryzen 3/5 |
| RAM | `8 GB` |
| Graphics | Standard integrated graphics are usually sufficient |
| Storage | SSD recommended |
| Network | Reliable Wi-Fi or Ethernet |

The VRO computer must use wired Gigabit Ethernet. Judge and referee laptops can usually run successfully on a good closed event Wi-Fi LAN, provided the wireless network is not shared with spectators or general venue traffic.

Recommended live video encoder:

- Use a dedicated encoder that provides an RTSP stream to the VRO computer.
- If the video camera supports SDI output (more reliable than HDMI), the AVMatrix SE-1117 or a similar SDI-to-RTSP encoder is recommended.
- The preferred live encoder feed is `1080p 60 fps`.
- `1080p 30 fps` is acceptable.
- `1080i 60` is acceptable when progressive output is not available. In normal video terminology, `1080i 60` means 60 interlaced fields per second, which is effectively 30 full frames per second.
- The important requirement is consistency: the configured/demo video frame rate should match the frame rate produced by the encoder stream.

## Windows Defender and SmartScreen

Windows may warn about newly downloaded or newly provided installers, especially before the app has a broad reputation with Microsoft SmartScreen.

If Windows SmartScreen blocks the installer:

1. Click `More info`.
2. Confirm the publisher/file is the expected ElementReview or JudgeVideoReplay installer.
3. Click `Run anyway`.

If Windows Defender or the browser marks the file as suspicious:

1. Confirm the file came from a trusted source.
2. If Windows shows a `Keep` or `Keep anyway` option, choose it only after confirming the file source.
3. If the file is blocked after copying, right-click the installer, choose `Properties`, check `Unblock` if present, then click `OK`.

Do not bypass these warnings for files from an unknown source.

## Install ElementReview on the VRO Computer

1. Run the `ElementReview-Setup-<version>.exe` installer on the VRO computer.
2. Start `ElementReview`.
3. Open the settings screen by clicking the gear icon located at the top right of the window.
4. Configure the CSS link, video source, encoding, and saved-video settings.
5. Save settings and restart ElementReview.

## Install JudgeVideoReplay on Judge and Referee Computers

1. Run the `JudgeVideoReplay-Setup-<version>.exe` installer on every judge and referee computer.
2. Start `JudgeVideoReplay`.
3. Open the settings screen by clicking the gear icon located at the top right of the window.
4. Set `Server IP address` to the IP address of the VRO computer.
5. Set the role to `Judge` or `Referee`.
6. Save settings.

The `Server IP address` must be the VRO computer IP address, not the judge/referee computer IP address.

Example:

```text
192.168.6.60
```

## ElementReview Initial Configuration

Open ElementReview settings on the VRO computer by clicking the gear icon located at the top right of the window.

### CSS Integration

If integrating ElementReview with Legacy CSS, set `CSS Link Type` to:

```text
Legacy CSS
```

Then, set `MSSQL Database Host` to the IP address of the CSS database host (normally the EC computer).

If integrating ElementReview with Online CSS or Offline CSS, set `CSS Link Type` to:

```text
None
```

The display of executed element codes and automatic determination of discipline/category/segment-specific halfway time values is currently only supported for Legacy CSS. Similar support for Online CSS and Offline CSS is forthcoming.

When CSS integration is set to `None`, ElementReview cannot automatically detect category, discipline, segment, or halfway timing from CSS data. Use the manual `HW:` dropdown in the recording/replay UI to manually select the appropriate halfway time value when halfway timing is needed.

### Video Source

Turn on Demo Mode for training or demonstration purposes. This mode uses a locally stored video instead of an RTSP video stream as an input source. The local video can be customized by replacing the file `demovideo.mp4` located in `User\AppData\Local\ElementReview\data`. 

Supported video format:

	Container: MP4
	Video codec: H.264 / AVC
	Frame rate: constant, matching the frame rate produced by the video encoder
	Resolution: 1920x1080 preferred, 1280x720 acceptable
	Audio: optional; not needed for Demo Mode
	Fast start: enabled
	Avoid: HEVC/H.265, HDR, variable frame rate, unusual codecs

Turn off Demo Mode for real event recording.

Set the RTSP URL to the video encoder stream URL.

Example without a special port:

```text
rtsp://192.168.6.200/0
```

Example with an explicit port and path:

```text
rtsp://192.168.1.168:8554/video
```

Set `RTSP Transport Protocol` to `UDP` in most cases. UDP is usually the best choice on a clean, local event network because it has lower latency.

Use `TCP` only if the network is unreliable or if unable to establish RTSP stream connection using `UDP`.

### Encoding Settings

Recommended settings:

| Setting | Recommended value | Notes |
| --- | --- | --- |
| High-res Video GOP | `10` | Used by ElementReview on the VRO computer. Lower GOP helps responsive seeking. |
| Low-res Video GOP | `30` | Used by JudgeVideoReplay clients. |
| Low-res Video Bitrate | `2500` to `4000` kbps | Higher values improve quality but use more network bandwidth. |

Use `2500` kbps when bandwidth is limited or many clients are connected. Use `4000` kbps when the event network is strong and better judge replay quality is desired.

Enable `Use Hardware Encoding` in most situations. Disable it only if hardware encoding causes a confirmed issue on the VRO computer.

## Firewall and Network Requirements

The VRO computer must allow judge and referee computers to connect to ElementReview.

Required inbound rule on the VRO computer:

```text
TCP 5050
```

ElementReview listens on:

```text
http://0.0.0.0:5050
```

JudgeVideoReplay uses TCP port `5050` for status, replay data, and video file downloads from the VRO computer.

If Windows Firewall prompts when ElementReview first runs, allow access on the event/private network.

If connections still fail, options include:

- Add an inbound Windows Firewall rule allowing TCP `5050` for `ElementReview.exe`.
- Add an inbound Windows Firewall rule allowing TCP `5050` for the network profile.
- Temporarily turn off Windows Firewall on the VRO computer for the event network.

Turning off Windows Firewall is simple for troubleshooting, but a targeted TCP `5050` allow rule is preferred when possible to maintain network security.

## Connection Checklist

On the VRO computer:

1. Start ElementReview.
2. Confirm the VRO computer is connected to the event network.
3. Confirm the VRO IP address.
4. Confirm TCP port `5050` is allowed through the firewall.

On each judge/referee computer:

1. Start JudgeVideoReplay.
2. Open the settings screen by clicking the gear icon located at the top right of the window.
3. Enter the VRO computer IP address.
4. Select the correct role (Judge or Referee)
5. Save settings.
6. Confirm JudgeVideoReplay connects when ElementReview is running.

## Troubleshooting

If JudgeVideoReplay stays on the waiting screen:

- Confirm ElementReview is running on the VRO computer.
- Confirm the VRO IP address in JudgeVideoReplay is correct.
- Confirm all computers are on the same LAN/VLAN.
- Confirm the event network does not block client-to-client traffic.
- Confirm TCP port `5050` is open inbound on the VRO computer.

If ElementReview does not show video:

- Confirm Demo Mode is off for live event recording.
- Confirm the RTSP URL is correct.
- Confirm the encoder is powered on and connected to the network.
- Try `UDP` first, then try `TCP` if the stream is unstable or unavailable.
- Confirm the encoder IP address is reachable from the VRO computer.

If judge replay video is low quality or stutters:

- Increase low-res bitrate toward `4000` kbps (or possibly even higher) for better quality if bandwidth allows.
- Reduce low-res bitrate toward `2500` kbps (or possibly even lower) if the network is congested.
- Confirm all clients have strong wired or wireless network connectivity.
- Keep low-res GOP at `30` unless directed otherwise.
