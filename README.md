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

- Watches a circular region around the crosshair for a configurable target
  color (an enemy marker, health bar, etc).
- When a match is inside that circle, nudges the real mouse cursor (via
  `SendInput`, so it affects raw-input camera look the same as a physical
  mouse) toward the match, offset down by `ChestOffsetY` pixels to land on
  the body instead of the marker itself.
- Draws a green circle overlay on screen showing the detection radius while
  active.
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
  "DetectionRadius": 220,
  "ChestOffsetY": 90,
  "Strength": 0.15,
  "ShowOverlay": true
}
```

`Strength` is the fraction of the remaining distance closed per tick (~30
times/sec) — higher snaps harder, lower feels like a gentle pull. If the
game runs in exclusive fullscreen, screen capture may not work; switch it to
windowed/borderless for testing.

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
