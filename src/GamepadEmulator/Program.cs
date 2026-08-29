using System.Text.Json;
using GamepadEmulator.Config;
using GamepadEmulator.Debugging;
using GamepadEmulator.Input;
using GamepadEmulator.Virtual;
using GamepadEmulator.Vision;

namespace GamepadEmulator;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new EmulatorApplicationContext());
    }
}

internal sealed class EmulatorApplicationContext : ApplicationContext
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _enabledMenuItem;
    private readonly LowLevelHooks _hooks;
    private readonly System.Windows.Forms.Timer _tickTimer;
    private readonly System.Windows.Forms.Timer _pullTimer;
    private readonly VirtualXbox360Controller _controller;
    private readonly AimOverlayForm _overlay;

    // Reassigned wholesale by LoadConfig() (never mutated in place), and read from
    // both the UI thread and the background detection loop thread - volatile so a
    // reload becomes visible to the loop promptly.
    private volatile MappingConfig _config = new();
    private Keys _toggleKey = Keys.F9;
    private Keys _aimAssistToggleKey = Keys.F10;
    private Keys _colorProbeKey = Keys.F11;
    private readonly Dictionary<Keys, string> _keyToButton = new();
    private readonly Dictionary<Keys, string[]> _keyToButtonCombo = new();
    private readonly Dictionary<MouseButtons, string> _mouseButtonToButton = new();
    private Keys _leftUp, _leftDown, _leftLeft, _leftRight;
    private MouseButtons? _hardLockButton;
    private string _debugCaptureDir = "";

    // Runaway-lock watchdog state - all only ever touched from the UI thread
    // (OnMouseButton and OnPullTick both run there), so no synchronization needed.
    private long _lockEngagedAtMs;
    private bool _lockWatchdogTripped;
    private double? _lockLastDist;
    private int _lockStuckTicks;

    // Set from the input hook thread (button press/release), read from the background
    // detection loop thread - both plain field accesses, so this needs to be volatile
    // for the write to be visible promptly across threads.
    private volatile bool _isDrawing;

    // Published by the background detection loop, read by the fast UI-thread pull
    // timer. Guarded by a plain lock - contention is negligible (a few field reads a
    // tick), so a full lock is simpler and cheap enough here.
    private readonly object _detectionLock = new();
    private (long TimestampMs, Point Point)? _latestDetection;

    private readonly CancellationTokenSource _detectionLoopCts = new();

    // Guards _poseDetector: the background detection loop calls Run() on it, while
    // ApplyPendingPoseSwap (UI thread) may Dispose() and replace it after a reload -
    // without this, those two could race on the same native session.
    private readonly ReaderWriterLockSlim _poseDetectorLock = new();
    private PoseDetector? _poseDetector;
    private string _poseModelPath = "";
    private int _poseLoadGeneration;
    private volatile bool _poseSwapPending;
    private PoseDetector? _pendingPoseDetector;
    private (string Message, ToolTipIcon Icon)? _pendingPoseNotice;

    private bool _enabled = true;
    private volatile bool _aimAssistEnabled;
    private bool _leftUpHeld, _leftDownHeld, _leftLeftHeld, _leftRightHeld;
    private double _rightStickX, _rightStickY;

    public EmulatorApplicationContext()
    {
        LoadConfig();

        _controller = new VirtualXbox360Controller();
        _overlay = new AimOverlayForm();

        _hooks = new LowLevelHooks();
        _hooks.KeyDown += OnKeyDown;
        _hooks.KeyUp += OnKeyUp;
        _hooks.MouseButtonDown += b => OnMouseButton(b, true);
        _hooks.MouseButtonUp += b => OnMouseButton(b, false);
        _hooks.MouseMoveDelta += OnMouseMoveDelta;
        _hooks.SuppressKey = ShouldSuppressKey;
        _hooks.SuppressMouseButton = ShouldSuppressMouseButton;
        _hooks.Install();

        _tickTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _tickTimer.Tick += (_, _) => OnTick();
        _tickTimer.Start();

        // Cheap (no capture/inference) - just reads the latest cached detection and
        // nudges the mouse. All the heavy work happens on the background detection
        // loop thread below, deliberately kept off the UI/input-hook thread, which
        // must stay responsive or real mouse/keyboard input becomes janky and button
        // suppression/timing becomes unreliable.
        _pullTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, _config.AimAssist.PullIntervalMs) };
        _pullTimer.Tick += (_, _) => OnPullTick();
        _pullTimer.Start();

        Task.Run(() => DetectionLoop(_detectionLoopCts.Token));

        _enabledMenuItem = new ToolStripMenuItem("Enabled", null, OnToggleEnabled) { Checked = _enabled };
        var reloadItem = new ToolStripMenuItem("Reload mapping", null, (_, _) => LoadConfig());
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitApp());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledMenuItem);
        menu.Items.Add(reloadItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Gamepad Emulator",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => OnToggleEnabled(_enabledMenuItem, EventArgs.Empty);
    }

    private void LoadConfig()
    {
        var exeDir = AppContext.BaseDirectory;
        var userPath = Path.Combine(exeDir, "mapping.json");
        var defaultPath = Path.Combine(exeDir, "mapping.default.json");
        var path = File.Exists(userPath) ? userPath : defaultPath;

        var json = File.ReadAllText(path);
        _config = JsonSerializer.Deserialize<MappingConfig>(json, JsonOptions) ?? new MappingConfig();

        if (_pullTimer != null)
            _pullTimer.Interval = Math.Max(1, _config.AimAssist.PullIntervalMs);

        _toggleKey = Enum.TryParse<Keys>(_config.ToggleHotkey, ignoreCase: true, out var toggle) ? toggle : Keys.F9;
        _aimAssistToggleKey = Enum.TryParse<Keys>(_config.AimAssist.ToggleHotkey, ignoreCase: true, out var aimToggle) ? aimToggle : Keys.F10;
        _colorProbeKey = Enum.TryParse<Keys>(_config.AimAssist.ProbeHotkey, ignoreCase: true, out var probe) ? probe : Keys.F11;
        _aimAssistEnabled = _config.AimAssist.Enabled;
        _hardLockButton = Enum.TryParse<MouseButtons>(_config.AimAssist.HardLockButton, ignoreCase: true, out var lockBtn)
            ? lockBtn
            : null;
        _debugCaptureDir = Path.Combine(exeDir, string.IsNullOrWhiteSpace(_config.AimAssist.DebugCaptureDir)
            ? "debug_captures"
            : _config.AimAssist.DebugCaptureDir);

        LoadPoseDetector(exeDir);

        _leftUp = ParseKey(_config.LeftStick.Up, Keys.W);
        _leftDown = ParseKey(_config.LeftStick.Down, Keys.S);
        _leftLeft = ParseKey(_config.LeftStick.Left, Keys.A);
        _leftRight = ParseKey(_config.LeftStick.Right, Keys.D);

        _keyToButton.Clear();
        foreach (var (keyName, buttonName) in _config.KeyToButton)
        {
            if (Enum.TryParse<Keys>(keyName, ignoreCase: true, out var key))
                _keyToButton[key] = buttonName;
        }

        _keyToButtonCombo.Clear();
        foreach (var (keyName, buttonNames) in _config.KeyToButtonCombo)
        {
            if (Enum.TryParse<Keys>(keyName, ignoreCase: true, out var key))
                _keyToButtonCombo[key] = buttonNames;
        }

        _mouseButtonToButton.Clear();
        foreach (var (buttonKey, buttonName) in _config.MouseButtonToButton)
        {
            if (Enum.TryParse<MouseButtons>(buttonKey, ignoreCase: true, out var mb))
                _mouseButtonToButton[mb] = buttonName;
        }
    }

    // Kicks off (re)loading the ONNX pose model in the BACKGROUND when UsePoseDetection
    // is on and the resolved path changed (including on the tray menu's "Reload
    // mapping"). Model init + DirectML setup can take a while, and on a bad
    // GPU/driver combo could even hang - this must never run on the UI thread, or the
    // whole app (tray icon, message loop, the ability to Exit at all) freezes solid
    // until Windows itself is rebooted. The actual swap into _poseDetector happens
    // later, on the UI thread, from OnPullTick - see ApplyPendingPoseSwap.
    private void LoadPoseDetector(string exeDir)
    {
        if (!_config.AimAssist.UsePoseDetection)
        {
            _poseLoadGeneration++;
            if (_poseModelPath != "")
            {
                _pendingPoseDetector = null;
                _pendingPoseNotice = null;
                _poseSwapPending = true;
            }
            _poseModelPath = "";
            return;
        }

        var poseModelPath = Path.Combine(exeDir, string.IsNullOrWhiteSpace(_config.AimAssist.PoseModelPath)
            ? "Models/yolov8n-pose.onnx"
            : _config.AimAssist.PoseModelPath);

        if (_poseDetector != null && _poseModelPath == poseModelPath)
            return;

        _poseModelPath = poseModelPath;
        var generation = ++_poseLoadGeneration;

        Task.Run(() =>
        {
            PoseDetector? loaded = null;
            (string Message, ToolTipIcon Icon)? notice = null;

            if (!File.Exists(poseModelPath))
            {
                notice = ($"Pose model not found: {poseModelPath}. Falling back to color-marker detection.", ToolTipIcon.Warning);
            }
            else
            {
                try
                {
                    loaded = new PoseDetector(poseModelPath);
                }
                catch (Exception ex)
                {
                    notice = ($"Failed to load pose model: {ex.Message}. Falling back to color-marker detection.", ToolTipIcon.Error);
                }
            }

            if (generation != _poseLoadGeneration)
            {
                // Superseded by a newer LoadConfig() call (e.g. a quick second "Reload
                // mapping") while this was loading - discard rather than commit stale state.
                loaded?.Dispose();
                return;
            }

            _pendingPoseDetector = loaded;
            _pendingPoseNotice = notice;
            _poseSwapPending = true;
        });
    }

    // Applies a pose-model load/unload that finished on a background thread (see
    // LoadPoseDetector). Runs on the UI thread only; the actual Dispose() is guarded by
    // _poseDetectorLock so it can never race with an in-flight inference call on the
    // background detection loop thread.
    private void ApplyPendingPoseSwap()
    {
        if (!_poseSwapPending)
            return;

        _poseSwapPending = false;

        _poseDetectorLock.EnterWriteLock();
        try
        {
            _poseDetector?.Dispose();
            _poseDetector = _pendingPoseDetector;
        }
        finally
        {
            _poseDetectorLock.ExitWriteLock();
        }
        _pendingPoseDetector = null;

        if (_pendingPoseNotice is { } notice)
        {
            _trayIcon.ShowBalloonTip(6000, "Gamepad Emulator", notice.Message, notice.Icon);
            _pendingPoseNotice = null;
        }
    }

    private static Keys ParseKey(string name, Keys fallback) =>
        Enum.TryParse<Keys>(name, ignoreCase: true, out var key) ? key : fallback;

    private bool ShouldSuppressKey(Keys key)
    {
        if (key == _toggleKey || key == _aimAssistToggleKey || key == _colorProbeKey)
            return false;

        if (!IsActiveNow() || !_config.BlockPhysicalInputForMappedKeys)
            return false;

        return key == _leftUp || key == _leftDown || key == _leftLeft || key == _leftRight
               || _keyToButton.ContainsKey(key) || _keyToButtonCombo.ContainsKey(key);
    }

    // No aim-assist-specific suppression at all: the hard-lock button's press/release
    // always passes straight through to the game untouched, at the exact moment the
    // player presses/releases it - only the mouse gets nudged (continuously, while
    // held), never the button events themselves. This is what makes the shot moment
    // fully player-controlled with no extra latency or reordering risk.
    private bool ShouldSuppressMouseButton(MouseButtons button, bool isDown)
    {
        if (!IsActiveNow() || !_config.BlockPhysicalInputForMappedKeys)
            return false;

        return _mouseButtonToButton.ContainsKey(button);
    }

    private bool IsActiveNow()
    {
        if (!_enabled)
            return false;

        if (string.IsNullOrEmpty(_config.TargetProcessName))
            return true;

        var hWnd = NativeMethods.GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
            return false;

        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == 0)
            return false;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return string.Equals(process.ProcessName, _config.TargetProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void OnKeyDown(Keys key)
    {
        if (key == _toggleKey)
        {
            ToggleEnabled();
            return;
        }

        if (key == _aimAssistToggleKey)
        {
            _aimAssistEnabled = !_aimAssistEnabled;
            _trayIcon.ShowBalloonTip(1200, "Gamepad Emulator",
                _aimAssistEnabled ? "Aim-assist: ON" : "Aim-assist: OFF", ToolTipIcon.Info);
            return;
        }

        if (key == _colorProbeKey)
        {
            var color = ScreenCapture.GetPixelColor(Cursor.Position);
            _trayIcon.ShowBalloonTip(4000, "Color probe",
                $"R={color.R} G={color.G} B={color.B}  (put these into AimAssist.ColorR/G/B)", ToolTipIcon.Info);
            return;
        }

        if (!IsActiveNow())
            return;

        if (key == _leftUp) _leftUpHeld = true;
        else if (key == _leftDown) _leftDownHeld = true;
        else if (key == _leftLeft) _leftLeftHeld = true;
        else if (key == _leftRight) _leftRightHeld = true;
        else if (_keyToButton.TryGetValue(key, out var buttonName))
            _controller.SetButton(buttonName, true);
        else if (_keyToButtonCombo.TryGetValue(key, out var buttonNames))
            foreach (var name in buttonNames)
                _controller.SetButton(name, true);
    }

    private void OnKeyUp(Keys key)
    {
        if (key == _leftUp) _leftUpHeld = false;
        else if (key == _leftDown) _leftDownHeld = false;
        else if (key == _leftLeft) _leftLeftHeld = false;
        else if (key == _leftRight) _leftRightHeld = false;
        else if (_keyToButton.TryGetValue(key, out var buttonName))
            _controller.SetButton(buttonName, false);
        else if (_keyToButtonCombo.TryGetValue(key, out var buttonNames))
            foreach (var name in buttonNames)
                _controller.SetButton(name, false);
    }

    private void OnMouseButton(MouseButtons button, bool pressed)
    {
        // Just flips a flag - no capture, no detection, no suppression. The background
        // detection loop and pull timer pick this up on their own schedules; the real
        // button event itself is never touched, so the shot always fires exactly when
        // the player releases, never before or after a correction.
        if (button == _hardLockButton && _aimAssistEnabled && _config.AimAssist.Enabled && IsActiveNow())
        {
            _isDrawing = pressed;
            if (pressed)
            {
                _lockEngagedAtMs = Environment.TickCount64;
                _lockWatchdogTripped = false;
                _lockLastDist = null;
                _lockStuckTicks = 0;
            }
        }

        if (pressed && !IsActiveNow())
            return;

        if (_mouseButtonToButton.TryGetValue(button, out var buttonName))
            _controller.SetButton(buttonName, pressed);
    }

    private void OnMouseMoveDelta(int dx, int dy)
    {
        if (!IsActiveNow())
            return;

        var sensitivity = _config.RightStick.Sensitivity * 0.02;
        _rightStickX = Math.Clamp(_rightStickX + dx * sensitivity, -1.0, 1.0);
        var signedDy = _config.RightStick.InvertY ? dy : -dy;
        _rightStickY = Math.Clamp(_rightStickY + signedDy * sensitivity, -1.0, 1.0);
    }

    private void OnTick()
    {
        if (!_config.GamepadRemapEnabled)
            return;

        var lx = (_leftLeftHeld ? -1.0 : 0.0) + (_leftRightHeld ? 1.0 : 0.0);
        var ly = (_leftDownHeld ? -1.0 : 0.0) + (_leftUpHeld ? 1.0 : 0.0);
        if (lx != 0 && ly != 0)
        {
            var norm = 1.0 / Math.Sqrt(2);
            lx *= norm;
            ly *= norm;
        }

        _controller.SetLeftStick(lx, ly);

        var decay = Math.Clamp(_config.RightStick.DecayPerTick, 0.0, 0.999);
        _rightStickX *= decay;
        _rightStickY *= decay;

        var deadzone = _config.RightStick.Deadzone;
        var outX = Math.Abs(_rightStickX) < deadzone ? 0.0 : _rightStickX;
        var outY = Math.Abs(_rightStickY) < deadzone ? 0.0 : _rightStickY;
        _controller.SetRightStick(outX, outY);
    }

    // Cheap: just reads the latest cached detection (published by the background
    // detection loop) and nudges the mouse toward it. Does its own capture/inference -
    // never; that all happens on DetectionLoop's background thread.
    private void OnPullTick()
    {
        ApplyPendingPoseSwap();

        if (!_isDrawing || !_aimAssistEnabled || !_config.AimAssist.Enabled || !IsActiveNow() || _lockWatchdogTripped)
            return;

        Point? target;
        lock (_detectionLock)
        {
            target = _latestDetection?.Point;
        }

        if (target is not { } point)
            return;

        GetAimRegion(out var crosshairX, out var crosshairY);

        var dx = point.X - crosshairX;
        var dy = point.Y - crosshairY;
        var dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist < Math.Max(0, _config.AimAssist.LockDeadZonePx))
        {
            // Genuinely converged - not "stuck", reset so a later drift doesn't trip
            // the detector using a stale reference distance from before it settled.
            _lockStuckTicks = 0;
            _lockLastDist = dist;
            return;
        }

        // Fast runaway detector: a real target's on-screen distance drops tick over
        // tick as the camera turns toward it. If it isn't dropping, this is almost
        // certainly a false detection that isn't actually part of the game world (nothing
        // to turn "toward") - disengage within a fraction of a second instead of
        // spinning/diving indefinitely. Release and re-press the hard-lock button to retry.
        var tolerance = Math.Max(0, _config.AimAssist.LockProgressTolerancePx);
        if (_lockLastDist is { } lastDist && dist >= lastDist - tolerance)
            _lockStuckTicks++;
        else
            _lockStuckTicks = 0;
        _lockLastDist = dist;

        if (_lockStuckTicks >= Math.Max(1, _config.AimAssist.LockStuckTicks))
        {
            _lockWatchdogTripped = true;
            _trayIcon.ShowBalloonTip(4000, "Gamepad Emulator",
                "Aim-lock disengaged: target distance stopped shrinking (likely a false detection) - release and re-press to retry.",
                ToolTipIcon.Warning);
            return;
        }

        // Slower backstop in case the check above somehow doesn't catch it.
        var watchdogRadius = Math.Max(1, _config.AimAssist.LockWatchdogRadiusPx);
        var watchdogMs = Math.Max(1, _config.AimAssist.LockWatchdogMs);
        if (dist > watchdogRadius && Environment.TickCount64 - _lockEngagedAtMs > watchdogMs)
        {
            _lockWatchdogTripped = true;
            _trayIcon.ShowBalloonTip(4000, "Gamepad Emulator",
                "Aim-lock disengaged: target never settled (likely a false detection) - release and re-press to retry.",
                ToolTipIcon.Warning);
            return;
        }

        var gain = Math.Clamp(_config.AimAssist.LockGain, 0.0, 1.0);
        var maxStep = Math.Max(1, _config.AimAssist.MaxPullStepPx);
        var moveX = Math.Clamp((int)Math.Round(dx * gain), -maxStep, maxStep);
        var moveY = Math.Clamp((int)Math.Round(dy * gain), -maxStep, maxStep);

        MouseInput.MoveRelative(moveX, moveY);
    }

    // Runs for the lifetime of the app on a background thread, deliberately separate
    // from the UI/input-hook thread. While the hard-lock button is held, repeatedly
    // captures the screen and re-detects the target, publishing the latest result for
    // OnPullTick to read. Never touches the mouse/keyboard hook or the UI message loop
    // directly - that separation is what keeps input responsive regardless of how
    // heavy detection is (color-marker or neural pose model).
    private void DetectionLoop(CancellationToken token)
    {
        var wasDrawing = false;

        while (!token.IsCancellationRequested)
        {
            if (!_isDrawing || !_aimAssistEnabled || !_config.AimAssist.Enabled || !IsActiveNow())
            {
                if (wasDrawing)
                {
                    // Just released - one last informational capture for the debug log
                    // (never used to move the mouse), so you can see where things ended
                    // up relative to where the last pull landed.
                    CaptureAndPublish("release", updateCache: false);
                }

                wasDrawing = false;
                lock (_detectionLock)
                {
                    _latestDetection = null;
                }

                Thread.Sleep(50);
                continue;
            }

            CaptureAndPublish(wasDrawing ? "hold" : "press", updateCache: true);
            wasDrawing = true;

            Thread.Sleep(Math.Max(1, _config.AimAssist.DetectionIntervalMs));
        }
    }

    private void CaptureAndPublish(string debugLabel, bool updateCache)
    {
        var region = GetAimRegion(out var crosshairX, out var crosshairY);
        using var capture = ScreenCapture.CaptureRegion(region);
        var detected = DetectAim(capture);

        if (updateCache)
        {
            lock (_detectionLock)
            {
                _latestDetection = detected is { } d ? (Environment.TickCount64, d.Point) : null;
            }
        }

        if (_config.AimAssist.DebugCaptureEnabled)
            SaveDebugSnapshot(debugLabel, capture, crosshairX, crosshairY, detected?.Marker, detected?.Pose);
    }

    // Effectively the whole primary screen is searched for the target, minus a
    // configurable band at the top/bottom that's excluded to avoid latching onto
    // screen-anchored HUD elements (health bar, nameplate) instead of the actual
    // in-world target. Returns the region to capture plus where the crosshair sits
    // within it (in the returned region's local coordinates). Cheap (no capture) -
    // safe to call from either the UI thread or the background detection loop.
    private Rectangle GetAimRegion(out int crosshairX, out int crosshairY)
    {
        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        var ignoreTop = Math.Max(0, _config.AimAssist.IgnoreTopPx);
        var ignoreBottom = Math.Max(0, _config.AimAssist.IgnoreBottomPx);
        var height = Math.Max(1, screen.Height - ignoreTop - ignoreBottom);

        crosshairX = screen.Width / 2 + _config.AimAssist.CenterOffsetX;
        crosshairY = screen.Height / 2 + _config.AimAssist.CenterOffsetY - ignoreTop;

        return new Rectangle(screen.Left, screen.Top + ignoreTop, screen.Width, height);
    }

    private TargetMatch? DetectMarkerFromBitmap(Bitmap bitmap)
    {
        var targetColor = System.Drawing.Color.FromArgb(
            _config.AimAssist.ColorR, _config.AimAssist.ColorG, _config.AimAssist.ColorB);
        return TargetDetector.FindNearestMatch(
            bitmap, targetColor, _config.AimAssist.ColorTolerance, Math.Max(1, _config.AimAssist.PixelStep));
    }

    // Detects the final aim point using whichever detection mode is configured: the
    // pretrained pose model if UsePoseDetection is on and it loaded successfully
    // (already gives a chest point directly, no further offset needed), otherwise the
    // color-marker path (marker position + ComputeChestOffsetY). Returns null if
    // nothing was detected. Also returns the underlying marker/pose result so callers
    // can log detailed diagnostics. Safe to call from the background detection loop
    // thread - _poseDetector access is guarded by _poseDetectorLock.
    private (Point Point, TargetMatch? Marker, PoseMatch? Pose)? DetectAim(Bitmap bitmap)
    {
        if (_config.AimAssist.UsePoseDetection)
        {
            PoseMatch? pose = null;
            var haveDetector = false;

            _poseDetectorLock.EnterReadLock();
            try
            {
                if (_poseDetector != null)
                {
                    haveDetector = true;
                    pose = _poseDetector.DetectNearestChest(bitmap,
                        _config.AimAssist.PoseConfidenceThreshold, _config.AimAssist.PoseIouThreshold,
                        _config.AimAssist.PoseKeypointConfThreshold, _config.AimAssist.PoseChestHipRatio,
                        _config.AimAssist.PoseMaxBoxHeightFraction);
                }
            }
            finally
            {
                _poseDetectorLock.ExitReadLock();
            }

            if (haveDetector)
                return pose is { } p ? (p.ChestPoint, null, p) : null;
        }

        var marker = DetectMarkerFromBitmap(bitmap);
        if (marker is not { } m)
            return null;

        var point = new Point(m.Point.X, (int)Math.Round(m.Point.Y + ComputeChestOffsetY(m)));
        return (point, marker, null);
    }

    private void SaveDebugSnapshot(string label, Bitmap screenshot, int crosshairX, int crosshairY,
        TargetMatch? marker, PoseMatch? pose)
    {
        double? targetX = null, targetY = null;
        if (marker is { } match)
        {
            targetX = match.Point.X;
            targetY = match.Point.Y + ComputeChestOffsetY(match);
        }
        else if (pose is { } p)
        {
            targetX = p.ChestPoint.X;
            targetY = p.ChestPoint.Y;
        }

        DebugCapture.Save(_debugCaptureDir, label, screenshot, new (string, object?)[]
        {
            ("Mode", _config.AimAssist.UsePoseDetection && _poseDetector != null ? "Pose" : "ColorMarker"),
            ("CrosshairX", crosshairX),
            ("CrosshairY", crosshairY),
            ("MarkerFound", marker != null),
            ("MarkerX", marker?.Point.X),
            ("MarkerY", marker?.Point.Y),
            ("MarkerHeight", marker?.Height),
            ("PoseFound", pose != null),
            ("PoseConfidence", pose?.Confidence),
            ("PoseBoxWidth", pose?.Box.Width),
            ("PoseBoxHeight", pose?.Box.Height),
            ("TargetX", targetX),
            ("TargetY", targetY),
        });
    }

    // Fixed offset by default (calibrated from real screenshots) - the marker's own
    // on-screen size doesn't track distance (see UseMarkerHeightScaling), so it isn't
    // a usable distance signal. Clamped either way as a safety net against outliers.
    private double ComputeChestOffsetY(TargetMatch match)
    {
        var offset = _config.AimAssist.UseMarkerHeightScaling && match.Height is { } h && h > 0
            ? h * _config.AimAssist.ChestOffsetMarkerRatio
            : _config.AimAssist.ChestOffsetY;

        var min = Math.Max(0, _config.AimAssist.MinChestOffsetY);
        var max = Math.Max(min + 1, _config.AimAssist.MaxChestOffsetY);
        return Math.Clamp(offset, min, max);
    }

    private void OnToggleEnabled(object? sender, EventArgs e) => ToggleEnabled();

    private void ToggleEnabled()
    {
        _enabled = !_enabled;
        _enabledMenuItem.Checked = _enabled;
        _trayIcon.Text = _enabled ? "Gamepad Emulator (active)" : "Gamepad Emulator (paused)";

        if (!_enabled)
        {
            _leftUpHeld = _leftDownHeld = _leftLeftHeld = _leftRightHeld = false;
            _rightStickX = _rightStickY = 0;
            _controller.SetLeftStick(0, 0);
            _controller.SetRightStick(0, 0);
            _isDrawing = false;
        }
    }

    private void ExitApp()
    {
        _tickTimer.Stop();
        _pullTimer.Stop();
        _detectionLoopCts.Cancel();
        _hooks.Dispose();
        _controller.Dispose();
        _overlay.Dispose();
        _poseDetector?.Dispose();
        _pendingPoseDetector?.Dispose();
        _poseDetectorLock.Dispose();
        _detectionLoopCts.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }
}
