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
    private readonly System.Windows.Forms.Timer _aimAssistTimer;
    private readonly System.Windows.Forms.Timer _snapReleaseTimer;
    private readonly VirtualXbox360Controller _controller;
    private readonly AimOverlayForm _overlay;

    private MappingConfig _config = new();
    private Keys _toggleKey = Keys.F9;
    private Keys _aimAssistToggleKey = Keys.F10;
    private Keys _colorProbeKey = Keys.F11;
    private readonly Dictionary<Keys, string> _keyToButton = new();
    private readonly Dictionary<Keys, string[]> _keyToButtonCombo = new();
    private readonly Dictionary<MouseButtons, string> _mouseButtonToButton = new();
    private Keys _leftUp, _leftDown, _leftLeft, _leftRight;
    private double? _smoothedAimX, _smoothedAimY;
    private MouseButtons? _snapOnReleaseButton;
    private MouseButtons? _snapReleasePending;
    private string _debugCaptureDir = "";
    private bool _isDrawing;
    private readonly List<(long TimestampMs, double X, double Y)> _trackingHistory = new();

    private bool _enabled = true;
    private bool _aimAssistEnabled;
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

        _aimAssistTimer = new System.Windows.Forms.Timer { Interval = Math.Max(8, _config.AimAssist.TickIntervalMs) };
        _aimAssistTimer.Tick += (_, _) => OnAimAssistTick();
        _aimAssistTimer.Start();

        _snapReleaseTimer = new System.Windows.Forms.Timer();
        _snapReleaseTimer.Tick += (_, _) => CompleteSnapRelease();

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

        if (_aimAssistTimer != null)
            _aimAssistTimer.Interval = Math.Max(8, _config.AimAssist.TickIntervalMs);

        _toggleKey = Enum.TryParse<Keys>(_config.ToggleHotkey, ignoreCase: true, out var toggle) ? toggle : Keys.F9;
        _aimAssistToggleKey = Enum.TryParse<Keys>(_config.AimAssist.ToggleHotkey, ignoreCase: true, out var aimToggle) ? aimToggle : Keys.F10;
        _colorProbeKey = Enum.TryParse<Keys>(_config.AimAssist.ProbeHotkey, ignoreCase: true, out var probe) ? probe : Keys.F11;
        _aimAssistEnabled = _config.AimAssist.Enabled;
        _snapOnReleaseButton = Enum.TryParse<MouseButtons>(_config.AimAssist.SnapOnReleaseButton, ignoreCase: true, out var snapBtn)
            ? snapBtn
            : null;
        _debugCaptureDir = Path.Combine(exeDir, string.IsNullOrWhiteSpace(_config.AimAssist.DebugCaptureDir)
            ? "debug_captures"
            : _config.AimAssist.DebugCaptureDir);

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

    private bool ShouldSuppressMouseButton(MouseButtons button, bool isDown)
    {
        if (!isDown && _snapReleasePending == null && button == _snapOnReleaseButton
            && _aimAssistEnabled && _config.AimAssist.Enabled && IsActiveNow())
        {
            return true;
        }

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
        if (pressed && button == _snapOnReleaseButton
            && _aimAssistEnabled && _config.AimAssist.Enabled && IsActiveNow())
        {
            // Start tracking the marker's position while the bow is drawn, so a
            // velocity can be estimated for lead prediction at release.
            _trackingHistory.Clear();
            _isDrawing = true;

            if (_config.AimAssist.DebugCaptureEnabled)
            {
                // Snapshot of what the tool sees the instant the bow-draw button is pressed,
                // before anything has moved - lets you compare against the "after release"
                // snapshot below to see exactly what the correction did.
                CaptureDebugSnapshot("press");
            }
        }

        if (!pressed && _snapReleasePending == null && _snapOnReleaseButton == button
            && _aimAssistEnabled && _config.AimAssist.Enabled && IsActiveNow())
        {
            // The real button-up was suppressed by ShouldSuppressMouseButton - do the
            // correction now, then hold the actual release until the game has had time
            // to visually catch up to it, so a big correction doesn't fire the shot
            // before the camera finishes turning.
            _isDrawing = false;
            _snapReleasePending = button;
            PerformSnapCorrection();
            _snapReleaseTimer.Interval = Math.Max(1, _config.AimAssist.SnapReleaseDelayMs);
            _snapReleaseTimer.Start();
            return;
        }

        if (pressed && !IsActiveNow())
            return;

        if (_mouseButtonToButton.TryGetValue(button, out var buttonName))
            _controller.SetButton(buttonName, pressed);
    }

    private void CompleteSnapRelease()
    {
        _snapReleaseTimer.Stop();
        if (_snapReleasePending is not { } button)
            return;

        _snapReleasePending = null;

        if (_config.AimAssist.DebugCaptureEnabled)
        {
            // Snapshot taken after the correction move has landed (and the game has had
            // SnapReleaseDelayMs to visually catch up), right before the shot is allowed
            // to actually fire - shows where the marker/target ended up relative to the
            // crosshair after the jump.
            CaptureDebugSnapshot("release_after");
        }

        MouseInput.SendButtonUp(button);
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

    // Effectively the whole primary screen is searched for the marker, minus a
    // configurable band at the top/bottom that's excluded to avoid latching onto
    // screen-anchored HUD elements (health bar, nameplate) instead of the actual
    // in-world enemy marker. Returns the region to capture plus where the
    // crosshair sits within it (in the returned region's local coordinates).
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

    private TargetMatch? DetectMarker(Rectangle region)
    {
        using var capture = ScreenCapture.CaptureRegion(region);
        return DetectMarkerFromBitmap(capture);
    }

    private TargetMatch? DetectMarkerFromBitmap(Bitmap bitmap)
    {
        var targetColor = System.Drawing.Color.FromArgb(
            _config.AimAssist.ColorR, _config.AimAssist.ColorG, _config.AimAssist.ColorB);
        return TargetDetector.FindNearestMatch(
            bitmap, targetColor, _config.AimAssist.ColorTolerance, Math.Max(1, _config.AimAssist.PixelStep));
    }

    // Captures the current screen fresh, saves it, and logs crosshair/marker/target
    // coordinates alongside it. Used for the "press" and "release_after" debug events.
    private void CaptureDebugSnapshot(string label)
    {
        var region = GetAimRegion(out var crosshairX, out var crosshairY);
        using var screenshot = ScreenCapture.CaptureRegion(region);
        var marker = DetectMarkerFromBitmap(screenshot);
        SaveDebugSnapshot(label, screenshot, crosshairX, crosshairY, marker);
    }

    private void SaveDebugSnapshot(string label, Bitmap screenshot, int crosshairX, int crosshairY, TargetMatch? marker,
        (double X, double Y)? predicted = null, (double VxPerMs, double VyPerMs)? velocity = null)
    {
        double? targetX = null, targetY = null;
        if (marker is { } match)
        {
            targetX = match.Point.X;
            targetY = match.Point.Y + ComputeChestOffsetY(match);
        }

        double? velocityXPerSec = velocity?.VxPerMs * 1000;
        double? velocityYPerSec = velocity?.VyPerMs * 1000;

        DebugCapture.Save(_debugCaptureDir, label, screenshot, new (string, object?)[]
        {
            ("CrosshairX", crosshairX),
            ("CrosshairY", crosshairY),
            ("MarkerFound", marker != null),
            ("MarkerX", marker?.Point.X),
            ("MarkerY", marker?.Point.Y),
            ("MarkerHeight", marker?.Height),
            ("TargetX", targetX),
            ("TargetY", targetY),
            // The lead-predicted aim point actually used for the correction, and the
            // estimated marker velocity (px/sec) behind it - null unless there was
            // enough tracking history during the draw to predict from.
            ("PredictedX", predicted?.X),
            ("PredictedY", predicted?.Y),
            ("VelocityXPerSec", velocityXPerSec),
            ("VelocityYPerSec", velocityYPerSec),
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

    private void OnAimAssistTick()
    {
        if (_snapOnReleaseButton != null)
        {
            // Continuous pull is unused once snap-on-release is configured -
            // PerformSnapCorrection does its own fresh capture at the moment that
            // matters instead. But while the bow is drawn, sample the marker's
            // position (without moving the mouse) so a velocity can be estimated
            // for lead prediction against a moving target at release.
            if (_isDrawing && _aimAssistEnabled && _config.AimAssist.Enabled && IsActiveNow())
                RecordTrackingSample();
            return;
        }

        if (!_aimAssistEnabled || !_config.AimAssist.Enabled || !IsActiveNow())
            return;

        var region = GetAimRegion(out var crosshairX, out var crosshairY);
        var marker = DetectMarker(region);

        if (marker is not { } match)
        {
            _smoothedAimX = null;
            _smoothedAimY = null;
            return;
        }

        var rawAimX = (double)match.Point.X;
        var rawAimY = match.Point.Y + ComputeChestOffsetY(match);

        var smoothing = Math.Clamp(_config.AimAssist.Smoothing, 0.0, 0.98);
        _smoothedAimX = _smoothedAimX is { } sx ? sx * smoothing + rawAimX * (1 - smoothing) : rawAimX;
        _smoothedAimY = _smoothedAimY is { } sy ? sy * smoothing + rawAimY * (1 - smoothing) : rawAimY;

        var dx = _smoothedAimX.Value - crosshairX;
        var dy = _smoothedAimY.Value - crosshairY;
        var dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist < Math.Max(0, _config.AimAssist.DeadZonePx))
            return;

        int moveX, moveY;
        if (dist >= Math.Max(1, _config.AimAssist.SnapThresholdPx))
        {
            var snapStrength = Math.Clamp(_config.AimAssist.SnapStrength, 0.0, 1.0);
            moveX = (int)Math.Round(dx * snapStrength);
            moveY = (int)Math.Round(dy * snapStrength);
        }
        else
        {
            var strength = Math.Max(0.0, _config.AimAssist.Strength);
            var maxStep = Math.Max(1, _config.AimAssist.MaxStepPx);
            moveX = Math.Clamp((int)Math.Round(dx * strength), -maxStep, maxStep);
            moveY = Math.Clamp((int)Math.Round(dy * strength), -maxStep, maxStep);
        }

        MouseInput.MoveRelative(moveX, moveY);
    }

    // Records the marker's current position (without moving the mouse) while the bow
    // is drawn, building a short history used to estimate its on-screen velocity.
    private void RecordTrackingSample()
    {
        var region = GetAimRegion(out _, out _);
        var marker = DetectMarker(region);
        if (marker is not { } match)
            return;

        var x = (double)match.Point.X;
        var y = match.Point.Y + ComputeChestOffsetY(match);
        var now = Environment.TickCount64;

        _trackingHistory.Add((now, x, y));

        var cutoff = now - Math.Max(1, _config.AimAssist.TrackingHistoryMs);
        _trackingHistory.RemoveAll(s => s.TimestampMs < cutoff);
    }

    // Extrapolates from the oldest retained tracking sample to "now" to estimate
    // velocity (px/ms), then projects LeadTimeMs further into the future - so the
    // correction aims where a moving target will be by the time the shot actually
    // registers, not where it was at the moment of detection. Falls back to the raw
    // (un-extrapolated) position when there isn't enough tracking history yet, e.g.
    // a very quick draw-and-release, or the target isn't actually moving.
    private (double X, double Y, double VxPerMs, double VyPerMs) PredictLeadPosition(double rawX, double rawY, long now)
    {
        var leadMs = Math.Max(0, _config.AimAssist.LeadTimeMs);
        var minSamples = Math.Max(2, _config.AimAssist.MinTrackingSamples);

        if (leadMs == 0 || _trackingHistory.Count < minSamples)
            return (rawX, rawY, 0, 0);

        var oldest = _trackingHistory[0];
        var dtMs = now - oldest.TimestampMs;
        if (dtMs < 16)
            return (rawX, rawY, 0, 0);

        var vx = (rawX - oldest.X) / dtMs;
        var vy = (rawY - oldest.Y) / dtMs;

        var maxLead = Math.Max(0, _config.AimAssist.MaxLeadPx);
        var leadX = Math.Clamp(vx * leadMs, -maxLead, maxLead);
        var leadY = Math.Clamp(vy * leadMs, -maxLead, maxLead);

        return (rawX + leadX, rawY + leadY, vx, vy);
    }

    private void PerformSnapCorrection()
    {
        var region = GetAimRegion(out var crosshairX, out var crosshairY);
        using var screenshot = ScreenCapture.CaptureRegion(region);
        var marker = DetectMarkerFromBitmap(screenshot);

        if (marker is not { } match)
        {
            if (_config.AimAssist.DebugCaptureEnabled)
                SaveDebugSnapshot("release_before", screenshot, crosshairX, crosshairY, null);
            return;
        }

        var rawX = (double)match.Point.X;
        var rawY = match.Point.Y + ComputeChestOffsetY(match);
        var now = Environment.TickCount64;

        var (aimX, aimY, vx, vy) = PredictLeadPosition(rawX, rawY, now);

        if (_config.AimAssist.DebugCaptureEnabled)
        {
            // Snapshot of the screen, the raw detection, and the lead-predicted target
            // the instant before the mouse is moved, i.e. what the correction is about
            // to do and why (velocity-based prediction, if any was applied).
            SaveDebugSnapshot("release_before", screenshot, crosshairX, crosshairY, marker, (aimX, aimY), (vx, vy));
        }

        var dx = aimX - crosshairX;
        var dy = aimY - crosshairY;

        var gain = Math.Max(0.0, _config.AimAssist.SnapGain);
        MouseInput.MoveRelative((int)Math.Round(dx * gain), (int)Math.Round(dy * gain));
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
            _trackingHistory.Clear();
            CompleteSnapRelease();
        }
    }

    private void ExitApp()
    {
        _tickTimer.Stop();
        _aimAssistTimer.Stop();
        CompleteSnapRelease();
        _hooks.Dispose();
        _controller.Dispose();
        _overlay.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }
}
