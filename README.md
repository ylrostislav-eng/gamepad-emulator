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
  `CenterOffsetX/Y`), and searches effectively the whole primary screen for the
  target, minus a configurable band excluded at the top/bottom
  (`IgnoreTopPx`/`IgnoreBottomPx`) to avoid latching onto screen-anchored HUD
  elements (health/stamina bar, nameplate) instead of the actual in-world target.
- While `HardLockButton` is held (e.g. `"Left"`, the bow-draw button), the aim
  is continuously pulled ("hard-locked") onto the detected target every
  `PullIntervalMs` - you choose the exact moment to release, at your own
  timing. The real button press/release always passes straight through to
  the game completely untouched, with no suppression or delay - the shot
  fires exactly when you let go, wherever the aim happens to be at that instant.
- **Detection runs on a dedicated background thread**, entirely separate from
  the mouse/keyboard hook thread, capturing and re-detecting the target every
  `DetectionIntervalMs`. This separation matters: capture + detection
  (especially the pose model) is too heavy to run on the same thread that
  processes your real mouse/keyboard input without making it janky, and if it
  ever ran directly inside the hook callback, a slow detection could make
  Windows' low-level-hook timeout kick in and cause your button press/release
  to reach the game out of order. The UI-thread pull timer (`PullIntervalMs`)
  only ever reads the latest cached detection and nudges the mouse - it never
  captures or runs detection itself.
- The vertical offset from the marker down to the chest is a fixed pixel
  count, `ChestOffsetY` (calibrated from real screenshots, ~165px at
  2560x1440) - only used by the color-marker path; pose detection (below)
  computes the chest point directly and ignores this. An earlier version
  tried to auto-scale this from the marker's own measured on-screen size, but
  testing showed the marker is a fixed-screen-size HUD icon whose apparent
  size does **not** track distance - `UseMarkerHeightScaling` +
  `ChestOffsetMarkerRatio` are left in, off by default, in case a
  differently-implemented marker does scale with distance.
- `MinChestOffsetY`/`MaxChestOffsetY` clamp the final offset either way, as a
  safety net against one bad reading producing a wild correction.
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
  "HardLockButton": "Left",
  "LockGain": 0.4,
  "LockDeadZonePx": 6,
  "MaxPullStepPx": 70,
  "PullIntervalMs": 16,
  "DetectionIntervalMs": 33,
  "LockStuckTicks": 6,
  "LockProgressTolerancePx": 3,
  "LockWatchdogMs": 500,
  "LockWatchdogRadiusPx": 500,
  "DetectionMode": "Custom",
  "PoseModelPath": "Models/yolov8n-pose.onnx",
  "PoseConfidenceThreshold": 0.4,
  "PoseIouThreshold": 0.5,
  "PoseKeypointConfThreshold": 0.3,
  "PoseChestHipRatio": 0.35,
  "PoseMaxBoxHeightFraction": 0.45,
  "CustomModelPath": "Models/target-v1.onnx",
  "CustomConfidenceThreshold": 0.25,
  "CustomIouThreshold": 0.5,
  "DebugCaptureEnabled": true,
  "DebugCaptureDir": "debug_captures"
}
```

Tuning knobs for the hard-lock:
- `LockGain` — fraction of the remaining distance to the target closed on
  each pull tick. This plays the same role `SnapGain` did in an earlier,
  one-shot version of this tool: it's also your calibration for the game's
  actual mouse-to-camera-turn sensitivity, which is rarely 1:1. If the lock
  overshoots/oscillates around the target, lower it (e.g. `0.25`); if it
  visibly lags behind or never quite settles, raise it. Because this now
  applies every `PullIntervalMs` (not once), even a slightly too-high value
  tends to just wobble down to the target fast rather than fly past it
  outright - but very high values (close to `1.0`) can still oscillate.
- `ChestOffsetY` — vertical pixel gap from the marker down to the chest, at
  your resolution (color-marker mode only). Use the debug-capture screenshots
  (below) to measure it precisely.
- `LockDeadZonePx` — stop nudging once within this many pixels of the
  target, to avoid buzzing/micro-jitter once locked on instead of chasing
  detection noise.
- `MaxPullStepPx` — hard per-tick cap, protecting against a single bad/noisy
  detection causing a visible jerk.
- `PullIntervalMs`/`DetectionIntervalMs` — how often the mouse is nudged vs.
  how often the (heavier) capture+detection re-runs on the background thread.
  Keep `PullIntervalMs` fast (16ms) for smoothness; raise `DetectionIntervalMs`
  if the background thread can't keep up (e.g. slow GPU/CPU for the pose
  model) rather than touching `PullIntervalMs`.
- `IgnoreTopPx`/`IgnoreBottomPx` — if a debug-log row shows `MarkerFound` at a
  position that doesn't move across consecutive `hold` rows even though the
  camera is turning, that's almost always a screen-anchored HUD element being
  mistaken for the marker - increase the matching margin to exclude it, or
  narrow `ColorTolerance` and re-probe the marker's exact color with `F11`.
- `LockStuckTicks`/`LockProgressTolerancePx` — the primary runaway guard: if
  the remaining distance to the target hasn't shrunk by at least
  `LockProgressTolerancePx` for `LockStuckTicks` pull ticks in a row (about
  100ms at the defaults), the lock disengages immediately for the rest of the
  hold (release and re-press to retry). A real target's distance drops tick
  over tick as the camera turns toward it; a false detection that isn't
  actually part of the game world (a screen-anchored HUD element, or the
  player's own view-model before `PoseMaxBoxHeightFraction` below) never gets
  any closer, so this catches a runaway spin/dive within a fraction of a
  second - fast enough to stop it well before it becomes disorienting.
- `LockWatchdogMs`/`LockWatchdogRadiusPx` — a slower backstop behind the
  above, in case that check somehow doesn't trip: if the aim is still farther
  than `LockWatchdogRadiusPx` from the target after `LockWatchdogMs` of
  continuous pulling, disengage regardless.

If the game runs in exclusive fullscreen, screen capture may not work;
switch it to windowed/borderless for testing.

### Detection modes: color marker, pretrained pose, or your own fine-tuned model

`AimAssist.DetectionMode` picks which detector supplies the aim point -
`"ColorMarker"` (the original, default/fallback), `"Pose"` (pretrained
general-purpose person detector, below), or `"Custom"` (a model you fine-tune
yourself on screenshots from this specific game - see the next section). All
three reuse the exact same downstream pipeline (crosshair math, hard-lock
pull, background detection loop) - only where the aim point comes from changes.
If the chosen mode's model file is missing or fails to load, the app
automatically falls back to `ColorMarker` and shows a tray balloon saying so.

#### Pose detection (aim at the person, not the marker)

`DetectionMode: "Pose"` uses a person detected directly from pixels (bundled
pretrained YOLOv8-pose model, `Models/yolov8n-pose.onnx`, COCO keypoints) —
the chest is estimated from the shoulder/hip keypoints instead of a fixed
pixel offset below the marker. This sidesteps two problems the marker has:
it's immune to other red HUD elements being mistaken for it (there's nothing
color-specific to confuse it), and a detected silhouette's size actually does
track distance, unlike the marker (a fixed-screen-size icon — see
`UseMarkerHeightScaling` above).

- Runs on GPU via DirectML if available (NVIDIA/AMD/Intel, through the
  normal graphics driver — no separate CUDA/cuDNN install needed), falling
  back to CPU automatically otherwise.
- **Rejects anything taller than `PoseMaxBoxHeightFraction` of the screen** -
  this specifically filters out the player's own visible arm/bow (the view
  model), which fills a large fraction of the frame up close to the camera
  and would otherwise often win "nearest to center" over the actual, smaller,
  farther-away enemy. Since the view model is attached to the camera rather
  than the game world, locking onto it by mistake doesn't just miss - it
  causes a runaway spin/dive, because turning the camera toward it never
  makes the on-screen distance to it shrink (see `LockWatchdogMs` above,
  which is the backstop if this filter alone doesn't catch it in some other
  game/camera setup).
- Tuning: `PoseConfidenceThreshold` (raise if it's latching onto
  background/props; lower if it's missing the actual enemy),
  `PoseKeypointConfThreshold` (how confident a shoulder/hip reading must be
  before it's trusted for the chest estimate — the fallback below this is a
  fixed fraction of the detected person's bounding box), `PoseChestHipRatio`
  (0 = right at the shoulders, 1 = right at the hips; where between them the
  chest actually sits), `PoseMaxBoxHeightFraction` (lower it if the player's
  own view-model is still occasionally picked; raise it if a legitimately
  close/large enemy gets rejected).
- The pretrained model was trained on real photos of people, not this game's
  specific art style, so accuracy on your character may vary — that's what
  it's for testing. If it's unreliable, the fixed-offset marker path
  (`DetectionMode: "ColorMarker"`) remains fully intact as a fallback, or
  fine-tune your own model instead - see below.

#### Custom detection (a model fine-tuned on your own game)

`DetectionMode: "Custom"` uses a YOLOv8 model you train yourself on
screenshots from this specific game (`Models/target-v1.onnx` by default -
`CustomModelPath` to point elsewhere). Unlike the pretrained pose model, it
never saw the player's own arm/bow labeled as a target during training, so -
given enough varied examples - it learns not to fire on it, and it only
needs a single tight box per enemy (no keypoints), so it's simpler and faster
to label a dataset for than pose. The detected box's center is used directly
as the aim point (it was trained by labeling boxes around the chest/torso
itself, so no `ChestOffsetY`-style guessing is needed).

To build/update one:
1. Turn on `DebugCaptureEnabled` and play normally for 15-20 minutes - the
   background detection loop already saves screenshots covering near/far
   enemies, different lighting, idle and fighting poses, for free.
2. Label ~150-300 of the more varied ones with a single class (a tight box
   around each visible enemy's chest/torso; leave frames with no enemy
   unlabeled) - e.g. via [Roboflow](https://roboflow.com)'s free web
   annotator, which also handles the train/val split and YOLOv8-format export.
   Explicitly including some frames with only the player's own arm/bow
   visible and *no* box drawn helps it learn that's not a target.
3. Export as YOLOv8 format and fine-tune `yolov8n.pt` on it (e.g. via
   `ultralytics`' `model.train(data=...)`, starting from the COCO-pretrained
   nano checkpoint so a small dataset still transfers reasonably well), then
   export to ONNX (`model.export(format='onnx', imgsz=640)`) and drop it in
   as `CustomModelPath`.

Tuning: `CustomConfidenceThreshold` (raise if it's latching onto background/
props; lower if it's missing the actual enemy), `CustomIouThreshold` (how
much overlapping duplicate detections get collapsed into one). A model
trained on only a few dozen images will have real gaps in accuracy - that's
expected for a first pass; add more varied examples and retrain if specific
situations (a certain lighting, distance, or pose) keep failing.

For a permanent, distance-proof fix without any detection at all (screen
color or pose model): since this is your own game with source access, the
most robust option remains moving the enemy marker's own anchor in the game
code to the chest position in 3D world space — then the color-marker path
can aim directly at it with `ChestOffsetY: 0`, correct at any distance, no
screen-space guessing needed. Not required if pose detection works well
enough for your testing.

### Debug capture (screenshots + coordinate log for each shot)

When `DebugCaptureEnabled: true`, the background detection loop (never the
UI/input-hook thread) writes into `DebugCaptureDir` (next to the exe,
default `debug_captures/`) on its own schedule while `HardLockButton` is held:

- One `<timestamp>_press.png` + a `press` row for the first sample right
  after you press the button.
- One `<timestamp>_hold.png` + a `hold` row for every subsequent detection
  cycle (every `DetectionIntervalMs`) while still held - so you get the
  whole trajectory of what the tool saw during the draw, not just the ends.
- One `<timestamp>_release.png` + a `release` row for one last, purely
  informational capture right after you let go (never used to move the
  mouse - just shows where things ended up).

All rows go into a single `log.csv` in that folder, with columns:
`Timestamp, Label, Screenshot, Mode, CrosshairX, CrosshairY, MarkerFound,
MarkerX, MarkerY, MarkerHeight, PoseFound, PoseConfidence, PoseBoxWidth,
PoseBoxHeight, CustomFound, CustomConfidence, CustomBoxWidth, CustomBoxHeight,
TargetX, TargetY`:
- `Mode` — `"ColorMarker"`, `"Pose"`, or `"Custom"`, whichever actually ran
  for that row (lets you tell at a glance which detector was active).
- `MarkerX/Y/Height` are populated only in `ColorMarker` mode; `PoseFound`/
  `PoseConfidence`/`PoseBoxWidth/Height` only in `Pose` mode;
  `CustomFound`/`CustomConfidence`/`CustomBoxWidth/Height` only in `Custom` mode.
- `TargetX/Y` is the detected aim point (marker+chest-offset, the pose
  model's chest estimate, or the custom model's box center) - this is what
  the pull timer was nudging toward around that time.
- `Screenshot` is the matching PNG filename in the same folder.

Turn this off (`DebugCaptureEnabled: false`) once you're done tuning — with
a whole shot's worth of `hold` rows now saved instead of just two, it adds
up in disk space and background-thread work fast.

## Project layout

```
src/GamepadEmulator/
  Program.cs              tray app, event wiring, stick math
  Config/MappingConfig.cs  mapping.json schema
  Input/NativeMethods.cs   P/Invoke declarations for the low-level hooks
  Input/LowLevelHooks.cs   keyboard/mouse hook wrapper
  Virtual/VirtualXbox360Controller.cs  ViGEmBus wrapper
  Vision/TargetDetector.cs  color-marker detection
  Vision/PoseDetector.cs   ONNX pretrained pose-model detection
  Vision/ObjectDetector.cs  ONNX custom fine-tuned model detection
  Models/yolov8n-pose.onnx  bundled pretrained pose model
  Models/target-v1.onnx   custom model fine-tuned on this game (update as you retrain)
```

## Uninstalling

Exit the tray app and uninstall the ViGEmBus driver from Windows'
"Add or remove programs" if you no longer need it.
