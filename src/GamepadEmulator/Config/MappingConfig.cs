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

    // Beyond this distance (px), do one decisive near-full correction instead of the
    // gentle Strength-scaled one - recovers fast from being knocked off-target (e.g.
    // by strafing) without needing continuous high-gain correction near the center,
    // which is what was causing the oscillation.
    public int SnapThresholdPx { get; set; } = 130;
    public double SnapStrength { get; set; } = 0.9;

    // Mouse button name (Left/Right/Middle). When set, the aim isn't pulled
    // continuously at all - it only snaps once, right when this button is
    // released (e.g. releasing LMB to fire an arrow). Empty = continuous pull
    // (the Strength/Smoothing/etc knobs above) instead.
    public string SnapOnReleaseButton { get; set; } = "";

    // Calibration multiplier for the release snap ONLY: raw on-screen pixel offset
    // to target is multiplied by this before being sent as a mouse move. 1.0 assumes
    // 1 injected pixel = 1 screen pixel of camera turn, which is rarely true - the
    // game's mouse sensitivity scales it. If the snap flies past the target, lower
    // this (e.g. 0.3); if it falls short, raise it. Tune in one direction at a time.
    public double SnapGain { get; set; } = 0.5;

    // Holds the actual button release for this many ms after the correction move,
    // so the game's own camera-turn-rate cap has time to fully catch up to a large
    // correction before the shot is allowed to register - fixes big corrections
    // landing short because the shot fired before the camera finished turning.
    public int SnapReleaseDelayMs { get; set; } = 90;

    // While the bow is drawn (button held), the marker's position is sampled every
    // tick (without moving the mouse) to estimate its on-screen velocity. At
    // release, the aim point is extrapolated LeadTimeMs further into the future -
    // roughly the total latency until the shot actually registers (capture+detect
    // overhead plus SnapReleaseDelayMs) - so a moving target is led instead of shot
    // at its last-seen (by then stale) position. 0 disables prediction entirely.
    public int LeadTimeMs { get; set; } = 150;

    // Hard cap (px) on how far the lead prediction may shift the aim point, in
    // either direction - guards against a noisy velocity estimate (e.g. the marker
    // briefly jumping to a different blob) producing a wild extrapolated aim.
    public int MaxLeadPx { get; set; } = 180;

    // How far back (ms) tracking samples are kept for the velocity estimate. Older
    // samples are dropped so the estimate reflects recent movement, not the whole draw.
    public int TrackingHistoryMs { get; set; } = 400;

    // Minimum tracking samples required before prediction kicks in; below this, the
    // raw (un-predicted) detection is used - e.g. for a very quick draw-and-release
    // there wasn't time to gather enough samples for a reliable velocity estimate.
    public int MinTrackingSamples { get; set; } = 2;

    // When true, aim at a person detected directly from pixels (pretrained YOLOv8-pose
    // model, chest estimated from shoulder/hip keypoints) instead of the color-matched
    // marker. Immune to the marker's false positives (other red HUD elements) and to
    // its lack of a real distance signal - a detected silhouette's size does track
    // distance, unlike the fixed-screen-size marker icon. Falls back to the color
    // marker automatically if the model file is missing or fails to load.
    public bool UsePoseDetection { get; set; } = false;

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

    // When true, saves a screenshot + logs crosshair/marker/target coordinates on
    // every LMB press, and again right after the release-snap correction moves the
    // mouse - so you can verify after the fact exactly what the tool saw and where
    // it decided to jump, for each shot.
    public bool DebugCaptureEnabled { get; set; } = false;

    // Folder (relative to the exe) where debug screenshots and log.csv are written.
    public string DebugCaptureDir { get; set; } = "debug_captures";
}
