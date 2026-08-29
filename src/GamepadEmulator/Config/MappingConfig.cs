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

    // Fixed vertical offset (px) from the detected marker down to the chest.
    // Calibrated from real screenshots at ~2560x1440 (a ~160-170px gap between the
    // marker and the chest). Used directly unless UseMarkerHeightScaling is on.
    public int ChestOffsetY { get; set; } = 165;

    // OFF by default: measured marker blob height turned out to be ~constant
    // (~18px) across near and far shots in testing - the enemy marker is a
    // fixed-screen-size HUD icon, not a world-space silhouette, so its apparent
    // size does NOT track distance. Scaling the chest offset by it just produced
    // a different near-constant offset, not a real distance-adaptive one. Left in
    // as an opt-in in case a differently-implemented marker (or a different game)
    // does scale with distance.
    public bool UseMarkerHeightScaling { get; set; } = false;

    // Chest offset = detected marker height (px) x this ratio, only when
    // UseMarkerHeightScaling is true.
    public double ChestOffsetMarkerRatio { get; set; } = 3.5;

    // Safety clamp applied to the final chest offset regardless of how it was
    // computed - guards against a single bad blob-size reading (e.g. two markers
    // merging in a rescan window) producing a wildly wrong correction.
    public int MinChestOffsetY { get; set; } = 60;
    public int MaxChestOffsetY { get; set; } = 280;

    // Vertical bands (px) excluded from marker search at the top/bottom of the
    // screen, to avoid latching onto screen-anchored HUD elements (health/stamina
    // bar, nameplate) that happen to match the target color instead of the actual
    // in-world enemy marker. The marker search still covers effectively the whole
    // game view otherwise.
    public int IgnoreTopPx { get; set; } = 110;
    public int IgnoreBottomPx { get; set; } = 170;

    public int PixelStep { get; set; } = 2;
    public bool ShowOverlay { get; set; } = true;

    // Manual nudge for where the crosshair actually sits, if it isn't exact screen center
    // (e.g. residual offset from display scaling). Positive Y = crosshair is below center.
    public int CenterOffsetX { get; set; } = 0;
    public int CenterOffsetY { get; set; } = 0;

    // Mouse button name (Left/Right/Middle). While held, the aim is continuously pulled
    // onto the detected target (a hard lock, not a one-time snap) - release whenever
    // you want to shoot, at your own timing; the real release always passes straight
    // through immediately, with no suppression or delay. Empty = aim-assist off.
    public string HardLockButton { get; set; } = "Left";

    // Fraction of the remaining distance to the target closed per pull tick while
    // locked on. Higher snaps onto the target faster/harder; lower feels smoother but
    // takes longer to fully settle. 1.0 = jump straight onto it every tick.
    public double LockGain { get; set; } = 0.4;

    // If already within this many pixels of the target, don't move the mouse at all -
    // kills micro-jitter once locked on instead of chasing tiny sensor noise.
    public int LockDeadZonePx { get; set; } = 6;

    // Hard cap (px) on how far the mouse can move in a single pull tick - a safety net
    // against a single bad/noisy detection causing a wild jerk.
    public int MaxPullStepPx { get; set; } = 70;

    // How often (ms) the mouse is nudged toward the latest cached detection while
    // locked on. This runs on the UI thread but does no capture/inference itself (just
    // reads what the background detection loop last found) - keep this fast for a
    // smooth feel; it's cheap.
    public int PullIntervalMs { get; set; } = 16;

    // How often (ms) the background thread re-captures the screen and re-detects the
    // target while locked on. This is the (heavier) detection rate, deliberately
    // decoupled from PullIntervalMs and run off the UI/input-hook thread entirely -
    // capture+detection must never block mouse/keyboard hook processing, or real input
    // becomes jerky and button suppression/timing becomes unreliable.
    public int DetectionIntervalMs { get; set; } = 33;

    // Fast runaway detector: if the remaining distance to the target hasn't shrunk by
    // at least LockProgressTolerancePx for this many consecutive pull ticks, the lock
    // disengages for the rest of the hold immediately (release and re-press to retry).
    // A real target's on-screen distance drops tick over tick as the camera turns
    // toward it; a false detection that isn't actually part of the game world (e.g.
    // something screen-anchored, or the player's own view-model arm before
    // PoseMaxBoxHeightFraction below existed) never gets any closer, so this catches a
    // runaway spin/dive within a fraction of a second instead of letting it run.
    public int LockStuckTicks { get; set; } = 6;
    public int LockProgressTolerancePx { get; set; } = 3;

    // Slower backstop, in case the stuck-ticks check above somehow doesn't catch it:
    // if the aim is still farther than LockWatchdogRadiusPx from the target after
    // LockWatchdogMs of continuous pulling, disengage the lock regardless.
    public int LockWatchdogMs { get; set; } = 500;
    public int LockWatchdogRadiusPx { get; set; } = 500;

    // Which detector supplies the aim point: "ColorMarker" (the original color-matched
    // marker + ChestOffsetY), "Pose" (pretrained general-purpose YOLOv8-pose model,
    // chest estimated from shoulder/hip keypoints), or "Custom" (a YOLOv8 model you
    // fine-tuned yourself on screenshots from this specific game - see CustomModelPath
    // below; the box it detects IS the chest, no offset needed). Falls back to the
    // color marker automatically if the chosen model's file is missing or fails to load.
    public string DetectionMode { get; set; } = "ColorMarker";

    // Path (relative to the exe) to the ONNX pose model.
    public string PoseModelPath { get; set; } = "Models/yolov8n-pose.onnx";

    // Minimum detection confidence (0-1) for a person to be considered at all.
    public float PoseConfidenceThreshold { get; set; } = 0.4f;

    // IoU threshold for non-max suppression - collapses duplicate/overlapping
    // detections of the same person into one.
    public float PoseIouThreshold { get; set; } = 0.5f;

    // Minimum per-keypoint confidence (0-1) for a shoulder/hip keypoint to be trusted
    // for the chest estimate; below this, a cruder box-based fallback is used instead
    // (e.g. the person is partly occluded and the model isn't sure where a hip is).
    public float PoseKeypointConfThreshold { get; set; } = 0.3f;

    // How far down from the shoulder line toward the hip line the chest point sits,
    // as a fraction of the shoulder-to-hip distance (0 = at the shoulders, 1 = at the
    // hips). Tune from debug-capture screenshots the same way as ChestOffsetY.
    public double PoseChestHipRatio { get; set; } = 0.35;

    // Rejects a detected "person" whose box is taller than this fraction of the
    // captured screen height - filters out the player's own visible arm/torso (which
    // fills a huge fraction of the frame up close to the camera), so it can't be
    // mistaken for the enemy. Lower this if a legitimately close/large enemy keeps
    // getting rejected; raise it if the player's own view-model still gets picked.
    public double PoseMaxBoxHeightFraction { get; set; } = 0.45;

    // Path (relative to the exe) to your fine-tuned custom ONNX model, used when
    // DetectionMode is "Custom".
    public string CustomModelPath { get; set; } = "Models/target-v1.onnx";

    // Minimum detection confidence (0-1) to accept a custom-model detection at all.
    public float CustomConfidenceThreshold { get; set; } = 0.25f;

    // IoU threshold for non-max suppression on the custom model's detections.
    public float CustomIouThreshold { get; set; } = 0.5f;

    // When true, saves a screenshot + logs crosshair/target coordinates on every LMB
    // press and release - so you can verify after the fact exactly what the tool saw
    // and where it was pulling toward, for each shot.
    public bool DebugCaptureEnabled { get; set; } = false;

    // Folder (relative to the exe) where debug screenshots and log.csv are written.
    public string DebugCaptureDir { get; set; } = "debug_captures";

    // Mouse button that, on press, saves a plain screenshot on demand - for building or
    // expanding a labeling dataset by hand (e.g. specific poses/angles/distances you
    // want to make sure are covered), independent of the automatic capture during a
    // hard-lock hold. Names: Left/Right/Middle/XButton1/XButton2 - XButton1/XButton2 are
    // the mouse's side ("back"/"forward") buttons, often labeled M4/M5 on gaming mice.
    // If your mouse's own software remaps them to something else, this may not see them
    // - check the mouse's button settings if it doesn't trigger. Empty = disabled.
    public string ManualCaptureButton { get; set; } = "XButton1";

    // Folder (relative to the exe) where manual screenshots are saved.
    public string ManualCaptureDir { get; set; } = "manual_captures";
}
