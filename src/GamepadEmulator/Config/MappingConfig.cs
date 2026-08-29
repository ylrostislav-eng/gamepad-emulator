namespace GamepadEmulator.Config;

public sealed class MappingConfig
{
    public bool BlockPhysicalInputForMappedKeys { get; set; } = true;
    public string ToggleHotkey { get; set; } = "F9";

    // Whether the virtual Xbox 360 controller drives its sticks from WASD/mouse at all.
    // Leave false when only using the mouse-based AimAssist tool - keeping the virtual
    // pad "moving" in parallel with native keyboard input makes some games flicker
    // between showing keyboard and gamepad prompts.
    public bool GamepadRemapEnabled { get; set; } = true;

    // Process name (without .exe) to restrict the remap to; empty = active system-wide.
    public string TargetProcessName { get; set; } = "";

    public LeftStickConfig LeftStick { get; set; } = new();
    public RightStickConfig RightStick { get; set; } = new();

    public Dictionary<string, string> KeyToButton { get; set; } = new();
    public Dictionary<string, string[]> KeyToButtonCombo { get; set; } = new();
    public Dictionary<string, string> MouseButtonToButton { get; set; } = new();

    public AimAssistConfig AimAssist { get; set; } = new();
}

public sealed class LeftStickConfig
{
    public string Up { get; set; } = "W";
    public string Down { get; set; } = "S";
    public string Left { get; set; } = "A";
    public string Right { get; set; } = "D";
}

public sealed class RightStickConfig
{
    public double Sensitivity { get; set; } = 1.0;
    public double Deadzone { get; set; } = 0.05;
    public double DecayPerTick { get; set; } = 0.80;
    public bool InvertY { get; set; } = false;
}

public sealed class AimAssistConfig
{
    public bool Enabled { get; set; } = false;
    public string ToggleHotkey { get; set; } = "F10";
    public string ProbeHotkey { get; set; } = "F11";
    public int ColorR { get; set; } = 220;
    public int ColorG { get; set; } = 30;
    public int ColorB { get; set; } = 30;
    public int ColorTolerance { get; set; } = 35;
    public int DetectionRadius { get; set; } = 550;
    public int ChestOffsetY { get; set; } = 90;
    public int PixelStep { get; set; } = 2;
    public double Strength { get; set; } = 0.15;
    public bool ShowOverlay { get; set; } = true;

    // 0 = no smoothing (raw, jittery), closer to 1 = heavier smoothing (laggier but steadier).
    public double Smoothing { get; set; } = 0.65;

    // How often (ms) to capture + correct. Lower = snappier but more prone to
    // overshoot oscillation if the game's camera can't visually keep up; if the
    // aim wobbles back and forth along one line, raise this before touching Strength.
    public int TickIntervalMs { get; set; } = 80;

    // Hard cap on pixels moved in a single tick, regardless of Strength - a safety
    // net against a single correction being big enough to itself cause overshoot.
    public int MaxStepPx { get; set; } = 90;

    // Extra pull multiplier applied as the target drifts toward the edge of the
    // detection circle (1 = no extra pull at the edge, i.e. same as at center).
    // Gives a strong "snap back" when strafing pushes the target off-center while
    // staying gentle near the center where oscillation would otherwise start.
    public double EdgeGainMultiplier { get; set; } = 3.0;

    // Manual nudge for where the crosshair actually sits, if it isn't exact screen center
    // (e.g. residual offset from display scaling). Positive Y = crosshair is below center.
    public int CenterOffsetX { get; set; } = 0;
    public int CenterOffsetY { get; set; } = 0;

    // If the (smoothed) target is already within this many pixels of the crosshair,
    // don't move the mouse at all - kills small residual wobble/idle-animation sway
    // instead of chasing it back and forth.
    public int DeadZonePx { get; set; } = 12;
}
