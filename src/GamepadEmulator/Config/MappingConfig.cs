namespace GamepadEmulator.Config;

public sealed class MappingConfig
{
    public bool BlockPhysicalInputForMappedKeys { get; set; } = true;
    public string ToggleHotkey { get; set; } = "F9";

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
    public int RegionWidth { get; set; } = 500;
    public int RegionHeight { get; set; } = 400;
    public int PixelStep { get; set; } = 2;
    public double Strength { get; set; } = 1.0;
    public double PullPerTick { get; set; } = 0.15;
}
