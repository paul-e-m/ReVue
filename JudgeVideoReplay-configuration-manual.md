# JudgeVideoReplay Configuration Manual

JudgeVideoReplay is configured on each judge and referee laptop. It connects to the ElementReview server running on the VRO laptop.

Most settings can be changed from the JudgeVideoReplay settings button. The backing file is:

```text
%LocalAppData%\JudgeVideoReplay\appconfig.json
```

## Required Information From The VRO Laptop

Before configuring judge and referee laptops, get the VRO laptop IP address.

The VRO laptop must be running ElementReview. ElementReview listens for clients on TCP port `5050`.

## Settings

`ServerIp`

- IP address or host name of the VRO laptop.
- Default is `127.0.0.1`, which only works when JudgeVideoReplay is running on the same computer as ElementReview.
- For real judge/referee laptops, replace this with the VRO laptop IP address.

Example:

```json
{
  "ServerIp": "192.168.6.25",
  "Role": "referee",
  "Language": "en",
  "UiZoomPercent": 100,
  "JudgeUI": {
    "DisplayTimerStopwatch": "true",
    "DisplayDanceLiftPresets": "false",
    "UpdateVideoWhileScrubbing": "true"
  },
  "RefereeUI": {
    "DisplayTimerStopwatch": "true",
    "DisplayDanceLiftPresets": "true",
    "UpdateVideoWhileScrubbing": "true"
  }
}
```

`Role`

- Supported values are `judge` and `referee`.
- The active role selects which hidden UI section, `JudgeUI` or `RefereeUI`, controls the replay window.

`JudgeUI` / `RefereeUI`

- Role-specific UI settings. These sections are saved in `appconfig.json`, but they are not shown in the settings dialog.
- The `Role` value decides which section is active when the app opens.
- `JudgeUI` applies when `Role` is `judge`.
- `RefereeUI` applies when `Role` is `referee`.
- Each setting accepts `true` or `false`, either as a JSON boolean or as a string.

| Setting | What it controls |
| --- | --- |
| `DisplayTimerStopwatch` | Shows or hides the stopwatch icon. When this is `false`, the timer stopwatch function is unavailable for that role. |
| `DisplayDanceLiftPresets` | Shows or hides the 8, 10, and 13-second dance lift preset buttons. These buttons only appear after the stopwatch has been turned on. |
| `UpdateVideoWhileScrubbing` | Controls whether the video image updates live while the scrub bar is dragged. When `false`, the video jumps to the selected time only after the scrub bar is released. |

Example role-dependent behavior:

```json
"JudgeUI": {
  "DisplayTimerStopwatch": "true",
  "DisplayDanceLiftPresets": "false",
  "UpdateVideoWhileScrubbing": "true"
},
"RefereeUI": {
  "DisplayTimerStopwatch": "true",
  "DisplayDanceLiftPresets": "true",
  "UpdateVideoWhileScrubbing": "true"
}
```

With this setup, judges can use the stopwatch but do not see the dance lift preset buttons, while referees see both the stopwatch and preset buttons. Both roles get live video updates while dragging the scrub bar.

To turn off live video updates for judges while scrubbing, change only the judge setting:

```json
"JudgeUI": {
  "DisplayTimerStopwatch": "true",
  "DisplayDanceLiftPresets": "false",
  "UpdateVideoWhileScrubbing": "false"
}
```

`Language`

- UI language.
- Supported values are `en` and `fr`.

`UiZoomPercent`

- Default zoom level for the JudgeVideoReplay window.
- `100` means normal size.
- Values are clamped from `50` to `150`.

## Configuration Steps

1. Start `JudgeVideoReplay.exe`.
2. Click the settings button.
3. Set `Server IP address` to the VRO laptop IP address.
4. Choose `Judge` or `Referee` in the role selector.
5. Set `UI Zoom (%)` if the replay UI should appear larger or smaller.
6. Choose the language.
7. Click `Save and Close`.

The app saves settings to:

```text
%LocalAppData%\JudgeVideoReplay\appconfig.json
```

## Expected Behavior

When ElementReview is not yet serving replay clips, JudgeVideoReplay may show a waiting screen. That is normal.

When replay clips are available:

- Element buttons appear in the left rail.
- Selecting an element plays that clipped region and loops it.
- The full-video icon opens the full recording timeline.
- The full-video view does not loop automatically at the end.
- Downloaded media chunks are cached locally and reused.

## Troubleshooting

If the app cannot connect:

- Confirm `ServerIp` is the VRO laptop IP address, not the judge laptop IP address.
- Confirm ElementReview is running on the VRO laptop.
- Confirm both laptops are on the same competition network.
- Confirm the VRO laptop allows inbound TCP port `5050`.
- Confirm no VPN or guest Wi-Fi isolation is blocking laptop-to-laptop traffic.

If the app opens but the UI does not load:

- Confirm Microsoft Edge WebView2 Runtime is installed.
- On Windows 11 it is normally already present.
- On Windows 10 it is often already present, but install the Evergreen Runtime if there is any doubt.
- Download WebView2 from Microsoft: https://developer.microsoft.com/en-us/microsoft-edge/webview2/
