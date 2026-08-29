using System.Text.Json;
using GamepadEmulator.Config;
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

    private bool ShouldSuppressMouseButton(MouseButtons button)
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
        if (!pressed && _snapOnReleaseButton == button
            && _aimAssistEnabled && _config.AimAssist.Enabled && IsActiveNow())
        {
            PerformSnapCorrection();
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

    private Rectangle GetAimRegion(out int radius, out int side)
    {
        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        radius = Math.Max(10, _config.AimAssist.DetectionRadius);
        side = radius * 2;
        var crosshairX = screen.X + screen.Width / 2 + _config.AimAssist.CenterOffsetX;
        var crosshairY = screen.Y + screen.Height / 2 + _config.AimAssist.CenterOffsetY;
        return new Rectangle(crosshairX - radius, crosshairY - radius, side, side);
    }

    private Point? DetectMarker(Rectangle region)
    {
        using var capture = ScreenCapture.CaptureRegion(region);
        var targetColor = System.Drawing.Color.FromArgb(
            _config.AimAssist.ColorR, _config.AimAssist.ColorG, _config.AimAssist.ColorB);
        return TargetDetector.FindNearestMatch(
            capture, targetColor, _config.AimAssist.ColorTolerance, Math.Max(1, _config.AimAssist.PixelStep));
    }

    private void OnAimAssistTick()
    {
        if (!_aimAssistEnabled || !_config.AimAssist.Enabled || !IsActiveNow())
        {
            _overlay.Hide();
            return;
        }

        var region = GetAimRegion(out var radius, out var side);

        if (_config.AimAssist.ShowOverlay)
            _overlay.ShowAt(region.Left, region.Top, side, side);
        else
            _overlay.Hide();

        var marker = DetectMarker(region);

        if (marker is not { } markerPoint)
        {
            _smoothedAimX = null;
            _smoothedAimY = null;
            return;
        }

        var rawAimX = (double)markerPoint.X;
        var rawAimY = markerPoint.Y + _config.AimAssist.ChestOffsetY;

        var smoothing = Math.Clamp(_config.AimAssist.Smoothing, 0.0, 0.98);
        _smoothedAimX = _smoothedAimX is { } sx ? sx * smoothing + rawAimX * (1 - smoothing) : rawAimX;
        _smoothedAimY = _smoothedAimY is { } sy ? sy * smoothing + rawAimY * (1 - smoothing) : rawAimY;

        // In snap-on-release mode the pull only happens in PerformSnapCorrection -
        // here we just keep the overlay/smoothed estimate fresh for that moment.
        if (_snapOnReleaseButton != null)
            return;

        var centerX = side / 2.0;
        var centerY = side / 2.0;
        var dx = _smoothedAimX.Value - centerX;
        var dy = _smoothedAimY.Value - centerY;
        var dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist > radius)
            return;

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
            var edgeMultiplier = Math.Max(1.0, _config.AimAssist.EdgeGainMultiplier);
            var edgeFactor = Math.Clamp(dist / radius, 0.0, 1.0);
            var strength = Math.Max(0.0, _config.AimAssist.Strength) * (1.0 + (edgeMultiplier - 1.0) * edgeFactor);
            var maxStep = Math.Max(1, _config.AimAssist.MaxStepPx);
            moveX = Math.Clamp((int)Math.Round(dx * strength), -maxStep, maxStep);
            moveY = Math.Clamp((int)Math.Round(dy * strength), -maxStep, maxStep);
        }

        MouseInput.MoveRelative(moveX, moveY);
    }

    private void PerformSnapCorrection()
    {
        var region = GetAimRegion(out var radius, out var side);
        var marker = DetectMarker(region);

        if (marker is not { } markerPoint)
            return;

        var aimX = markerPoint.X;
        var aimY = markerPoint.Y + _config.AimAssist.ChestOffsetY;

        var centerX = side / 2.0;
        var centerY = side / 2.0;
        var dx = aimX - centerX;
        var dy = aimY - centerY;
        var dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist > radius)
            return;

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
        }
    }

    private void ExitApp()
    {
        _tickTimer.Stop();
        _aimAssistTimer.Stop();
        _hooks.Dispose();
        _controller.Dispose();
        _overlay.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }
}
