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
  `CenterOffsetX/Y`), and searches the **entire** primary screen for a
  configurable target color (an enemy marker, health bar, etc) — no fixed
  detection radius.
- When `SnapOnReleaseButton` is set (e.g. `"Left"`), the aim isn't pulled
  continuously — the real button-up is held back, the mouse jumps once,
  instantly, to the computed target the moment you release the button, and
  the real release is then let through after `SnapReleaseDelayMs` so the
  game's camera has time to visually catch up before the shot fires.
- The vertical offset from the marker down to the chest is **auto-calculated**
  from the marker's own measured on-screen size (`ChestOffsetMarkerRatio x
  detected marker height`), so it scales automatically with distance instead
  of using one fixed pixel count. `ChestOffsetY` is only a fallback for when
  the marker blob is too small/noisy to measure.
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
  "ChestOffsetY": 90,
  "ChestOffsetMarkerRatio": 3.5,
  "SnapOnReleaseButton": "Left",
  "SnapGain": 0.35,
  "SnapReleaseDelayMs": 90,
  "DebugCaptureEnabled": true,
  "DebugCaptureDir": "debug_captures"
}
```

Tuning knobs for the snap-on-release mode:
- `SnapGain` — calibration multiplier for the computed pixel offset. If the
  snap overshoots past the target, lower it (e.g. `0.25`); if it falls
  short, raise it. Tune one direction at a time.
- `SnapReleaseDelayMs` — how long (ms) the real button release is held back
  after the jump, giving the game's camera time to finish turning before the
  shot registers. Raise it if big corrections still land short.

If the game runs in exclusive fullscreen, screen capture may not work;
switch it to windowed/borderless for testing.

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
`Timestamp, Label, Screenshot, CrosshairX, CrosshairY, MarkerFound, MarkerX,
MarkerY, MarkerHeight, TargetX, TargetY` — `TargetX/Y` is the computed
chest-aim point (marker position + the auto-calculated chest offset), and
`Screenshot` is the matching PNG filename in the same folder. Turn this off
(`DebugCaptureEnabled: false`) once you're done tuning — it captures and
saves a full-screen PNG on every shot, which costs disk space and a bit of
CPU per shot.

## Project layout

```
src/GamepadEmulator/
  Program.cs              tray app, event wiring, stick math
  Config/MappingConfig.cs  mapping.json schema
  Input/NativeMethods.cs   P/Invoke declarations for the low-level hooks
  Input/LowLevelHooks.cs   keyboard/mouse hook wrapper
  Virtual/VirtualXbox360Controller.cs  ViGEmBus wrapper
```

## Uninstalling

Exit the tray app and uninstall the ViGEmBus driver from Windows'
"Add or remove programs" if you no longer need it.
