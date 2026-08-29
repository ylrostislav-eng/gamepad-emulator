# Gamepad Emulator

Windows tray app that remaps keyboard and mouse input into a virtual Xbox 360
controller, so a game that only supports gamepad input can be played with
keyboard and mouse.

It uses [ViGEmBus](https://github.com/ViGEm/ViGEmBus) to create a real
virtual Xbox 360 device that Windows and games see exactly like a physical
controller.

> **Note on multiplayer games:** many online games with keyboard/mouse and
> controller crossplay give controller players aim assist. Using this tool
> to get that assist while actually aiming with a mouse violates the terms
> of service of most such games and can get you banned. This project is
> meant for single-player games / accessibility, not for that.

## How it works

- A low-level keyboard/mouse hook reads your key presses and mouse movement.
- WASD (configurable) drives the left stick.
- Mouse movement drives the right stick, with a spring-back-to-center feel
  (like look input), configurable sensitivity/deadzone/decay.
- Other keys and mouse buttons map to face buttons, shoulders, triggers,
  d-pad, etc. per `mapping.json`.
- Mapped keys/buttons are swallowed so the game doesn't also see them as
  raw keyboard/mouse input (configurable).
- An `F9` hotkey (configurable) pauses/resumes the remap at any time, and a
  tray icon lets you toggle it or exit.

## Prerequisites

- Windows 10/11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [ViGEmBus driver](https://github.com/ViGEm/ViGEmBus/releases) installed
  (install the latest `ViGEmBus_x.x.x_x64_x86_arm64.exe` setup and reboot
  once)

## Build & run

```powershell
dotnet build .\GamepadEmulator.sln -c Release
dotnet run --project .\src\GamepadEmulator\GamepadEmulator.csproj
```

Or open `GamepadEmulator.sln` in Visual Studio and run it (Debug or Release,
x64).

Run the built app as Administrator if the game you're feeding also runs
elevated — a hook installed from a non-elevated process can't affect an
elevated window.

## Configuring the mapping

On first run the app uses `mapping.default.json` (copied next to the exe).
Copy it to `mapping.json` in the same folder and edit that instead — it's
picked up automatically and won't be overwritten by rebuilds.

```json
{
  "BlockPhysicalInputForMappedKeys": true,
  "ToggleHotkey": "F9",
  "LeftStick": { "Up": "W", "Down": "S", "Left": "A", "Right": "D" },
  "RightStick": { "Sensitivity": 1.0, "Deadzone": 0.05, "DecayPerTick": 0.80, "InvertY": false },
  "KeyToButton": { "Space": "A", "ShiftKey": "B", "E": "X", "Q": "Y" },
  "MouseButtonToButton": { "Left": "RightTrigger", "Right": "LeftTrigger" }
}
```

- Key names come from the .NET
  [`Keys`](https://learn.microsoft.com/dotnet/api/system.windows.forms.keys)
  enum (e.g. `Space`, `ShiftKey`, `Q`, `D1`, `Up`, `Escape`).
- Button names: `A`, `B`, `X`, `Y`, `LeftShoulder`, `RightShoulder`,
  `LeftTrigger`, `RightTrigger`, `Back`, `Start`, `Guide`, `LeftThumb`,
  `RightThumb`, `Up`, `Down`, `Left`, `Right` (d-pad).
- Mouse button names: `Left`, `Right`, `Middle`.

After editing, use the tray icon's "Reload mapping" or restart the app.

## Aim assist (screen-capture, for local QA damage testing)

Separate from the gamepad remap above: a screen-capture based aim helper
meant for testing hit/damage registration on your own local build, not for
playing against other people.

- Continuously knows the crosshair's screen coordinates (screen center +
  `CenterOffsetX/Y`), and searches effectively the whole primary screen for a
  configurable target color (an enemy marker, health bar, etc), minus a
  configurable band excluded at the top/bottom (`IgnoreTopPx`/`IgnoreBottomPx`)
  to avoid latching onto screen-anchored HUD elements (health/stamina bar,
  nameplate) instead of the actual in-world marker.
- When `SnapOnReleaseButton` is set (e.g. `"Left"`), the aim isn't pulled
  continuously — the real button-up is held back, the mouse jumps once,
  instantly, to the computed target the moment you release the button, and
  the real release is then let through after `SnapReleaseDelayMs` so the
  game's camera has time to visually catch up before the shot fires.
- The vertical offset from the marker down to the chest is a fixed pixel
  count, `ChestOffsetY` (calibrated from real screenshots, ~165px at
  2560x1440). An earlier version tried to auto-scale this from the marker's
  own measured on-screen size, but testing showed the marker is a
  fixed-screen-size HUD icon whose apparent size does **not** track distance
  — so there was no usable signal there. `UseMarkerHeightScaling` +
  `ChestOffsetMarkerRatio` are left in, off by default, in case a
  differently-implemented marker does scale with distance.
- `MinChestOffsetY`/`MaxChestOffsetY` clamp the final offset either way, as a
  safety net against one bad reading producing a wild correction.
- While the bow is drawn (button held), the marker's position is sampled
  every tick (without moving the mouse) to estimate its on-screen velocity.
  At release, the aim point is extrapolated `LeadTimeMs` further into the
  future instead of using the marker's last-seen (by then stale) position —
  so a moving/fighting target gets led, not shot at where it used to be.
  Only kicks in once at least `MinTrackingSamples` were gathered during the
  draw; a very quick draw-and-release falls back to the raw position.
- Toggled independently of the remap with `F10`; `F11` reads the color under
  the mouse cursor into a tray balloon tip, so you can hover the enemy
  marker in-game and copy the exact `ColorR/G/B` into `mapping.json`.

```json
"AimAssist": {
  "Enabled": true,
  "ToggleHotkey": "F10",
  "ProbeHotkey": "F11",
  "ColorR": 220, "ColorG": 30, "ColorB": 30,
  "ColorTolerance": 35,
  "ChestOffsetY": 165,
  "UseMarkerHeightScaling": false,
  "MinChestOffsetY": 60,
  "MaxChestOffsetY": 280,
  "IgnoreTopPx": 110,
  "IgnoreBottomPx": 170,
  "SnapOnReleaseButton": "Left",
  "SnapGain": 0.5,
  "SnapReleaseDelayMs": 90,
  "LeadTimeMs": 150,
  "MaxLeadPx": 180,
  "TrackingHistoryMs": 400,
  "MinTrackingSamples": 2,
  "UsePoseDetection": true,
  "PoseModelPath": "Models/yolov8n-pose.onnx",
  "PoseConfidenceThreshold": 0.4,
  "PoseIouThreshold": 0.5,
  "PoseKeypointConfThreshold": 0.3,
  "PoseChestHipRatio": 0.35,
  "DebugCaptureEnabled": true,
  "DebugCaptureDir": "debug_captures"
}
```

Tuning knobs for the snap-on-release mode:
- `ChestOffsetY` — vertical pixel gap from the marker down to the chest, at
  your resolution. Use the debug-capture screenshots (below) to measure it
  precisely: pick a shot, measure the marker-to-chest gap in the PNG, and set
  this to that value.
- `SnapGain` — calibration multiplier for the computed pixel offset. If the
  snap overshoots past the target, lower it (e.g. `0.25`); if it falls
  short, raise it. Tune one direction at a time.
- `SnapReleaseDelayMs` — how long (ms) the real button release is held back
  after the jump, giving the game's camera time to finish turning before the
  shot registers. Raise it if big corrections still land short.
- `LeadTimeMs` — how far ahead (ms) a moving target is led, roughly the total
  latency from detection to the shot actually registering (capture/detect
  overhead + `SnapReleaseDelayMs`). If shots against a moving/fighting enemy
  consistently land behind it, raise this; if they now land ahead of it
  (over-leading), lower it. `0` disables prediction (shoots at the raw
  last-seen position, like before).
- `MaxLeadPx` — hard cap on how far prediction may shift the aim, protecting
  against a noisy velocity estimate (e.g. the marker briefly jumping to a
  different blob) producing a wild extrapolated aim.
- `IgnoreTopPx`/`IgnoreBottomPx` — if a shot's debug log shows `MarkerFound`
  at a position that doesn't move between `press`/`release_before`/
  `release_after` even though the camera turned, that's almost always a
  screen-anchored HUD element being mistaken for the marker, not the marker
  itself — increase the matching margin to exclude it, or narrow
  `ColorTolerance` and re-probe the marker's exact color with `F11`.

If the game runs in exclusive fullscreen, screen capture may not work;
switch it to windowed/borderless for testing.

### Pose detection (aim at the person, not the marker)

`UsePoseDetection: true` switches the aim point from the color-matched
marker to a person detected directly from pixels (bundled pretrained
YOLOv8-pose model, `Models/yolov8n-pose.onnx`, COCO keypoints) — the chest is
estimated from the shoulder/hip keypoints instead of a fixed pixel offset
below the marker. This sidesteps two problems the marker has: it's immune to
other red HUD elements being mistaken for it (there's nothing color-specific
to confuse it), and a detected silhouette's size actually does track
distance, unlike the marker (a fixed-screen-size icon — see
`UseMarkerHeightScaling` above). It reuses the exact same downstream pipeline
as the marker path (crosshair math, snap-on-release, velocity/lead
prediction) — only where the aim point comes from changes.

- Runs on GPU via DirectML if available (NVIDIA/AMD/Intel, through the
  normal graphics driver — no separate CUDA/cuDNN install needed), falling
  back to CPU automatically otherwise.
- If the model file is missing or fails to load (wrong path, no compatible
  GPU/driver, corrupted file), the app automatically falls back to the
  color-marker path and shows a tray balloon saying so — it never blocks the
  tool from running.
- Tuning: `PoseConfidenceThreshold` (raise if it's latching onto
  background/props; lower if it's missing the actual enemy),
  `PoseKeypointConfThreshold` (how confident a shoulder/hip reading must be
  before it's trusted for the chest estimate — the fallback below this is a
  fixed fraction of the detected person's bounding box), `PoseChestHipRatio`
  (0 = right at the shoulders, 1 = right at the hips; where between them the
  chest actually sits).
- The pretrained model was trained on real photos of people, not this game's
  specific art style, so accuracy on your character may vary — that's what
  it's for testing. If it's unreliable, the fixed-offset marker path
  (`UsePoseDetection: false`) remains fully intact as a fallback; a next step
  from here would be fine-tuning the model on screenshots from your own game
  for better accuracy, which needs a labeled dataset and is a bigger
  undertaking than flipping this flag.

For a permanent, distance-proof fix without any detection at all (screen
color or pose model): since this is your own game with source access, the
most robust option remains moving the enemy marker's own anchor in the game
code to the chest position in 3D world space — then the color-marker path
can aim directly at it with `ChestOffsetY: 0`, correct at any distance, no
screen-space guessing needed. Not required if pose detection works well
enough for your testing.

### Debug capture (screenshots + coordinate log for each shot)

When `DebugCaptureEnabled: true`, the app writes into `DebugCaptureDir`
(next to the exe, default `debug_captures/`) for every shot:

- `<timestamp>_press.png` + a `press` row — taken the instant you press the
  button, before anything moves.
- `<timestamp>_release_before.png` + a `release_before` row — taken right
  before the mouse jumps, at the moment you release the button.
- `<timestamp>_release_after.png` + a `release_after` row — taken after the
  jump has landed (once the camera has had `SnapReleaseDelayMs` to catch
  up), right before the real button-up (the shot) is let through.

All three rows go into a single `log.csv` in that folder, with columns:
`Timestamp, Label, Screenshot, Mode, CrosshairX, CrosshairY, MarkerFound,
MarkerX, MarkerY, MarkerHeight, PoseFound, PoseConfidence, PoseBoxWidth,
PoseBoxHeight, TargetX, TargetY, PredictedX, PredictedY, VelocityXPerSec,
VelocityYPerSec`:
- `Mode` — `"Pose"` or `"ColorMarker"`, whichever actually ran for that row
  (lets you tell at a glance whether pose detection was active or had fallen
  back).
- `MarkerX/Y/Height` are populated only in `ColorMarker` mode; `PoseFound`,
  `PoseConfidence`, `PoseBoxWidth/Height` only in `Pose` mode.
- `TargetX/Y` is the raw detected aim point (marker+chest-offset, or the
  pose model's chest estimate); `PredictedX/Y` is the lead-predicted point
  actually used for the correction (only set on `release_before`, and only
  once there was enough tracking history — see `LeadTimeMs` above);
  `VelocityXPerSec/YPerSec` is the estimated target speed behind that
  prediction.
- `Screenshot` is the matching PNG filename in the same folder.

Turn this off (`DebugCaptureEnabled: false`) once you're done tuning — it
captures and saves a full-screen PNG on every shot, which costs disk space
and a bit of CPU per shot.

## Project layout

```
src/GamepadEmulator/
  Program.cs              tray app, event wiring, stick math
  Config/MappingConfig.cs  mapping.json schema
  Input/NativeMethods.cs   P/Invoke declarations for the low-level hooks
  Input/LowLevelHooks.cs   keyboard/mouse hook wrapper
  Virtual/VirtualXbox360Controller.cs  ViGEmBus wrapper
  Vision/TargetDetector.cs  color-marker detection
  Vision/PoseDetector.cs   ONNX pose-model detection
  Models/yolov8n-pose.onnx  bundled pretrained pose model
```

## Uninstalling

Exit the tray app and uninstall the ViGEmBus driver from Windows'
"Add or remove programs" if you no longer need it.
