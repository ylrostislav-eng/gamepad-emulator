using System.Text.Json;
using GamepadEmulator.Config;
using GamepadEmulator.Input;
using GamepadEmulator.Virtual;

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
    private readonly VirtualXbox360Controller _controller;

    private MappingConfig _config = new();
    private Keys _toggleKey = Keys.F9;
    private readonly Dictionary<Keys, string> _keyToButton = new();
    private readonly Dictionary<Keys, string[]> _keyToButtonCombo = new();
    private readonly Dictionary<MouseButtons, string> _mouseButtonToButton = new();
    private Keys _leftUp, _leftDown, _leftLeft, _leftRight;

    private bool _enabled = true;
    private bool _leftUpHeld, _leftDownHeld, _leftLeftHeld, _leftRightHeld;
    private double _rightStickX, _rightStickY;

    public EmulatorApplicationContext()
    {
        LoadConfig();

        _controller = new VirtualXbox360Controller();

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

        _toggleKey = Enum.TryParse<Keys>(_config.ToggleHotkey, ignoreCase: true, out var toggle) ? toggle : Keys.F9;

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
        if (key == _toggleKey)
            return false;

        if (!_enabled || !_config.BlockPhysicalInputForMappedKeys)
            return false;

        return key == _leftUp || key == _leftDown || key == _leftLeft || key == _leftRight
               || _keyToButton.ContainsKey(key) || _keyToButtonCombo.ContainsKey(key);
    }

    private bool ShouldSuppressMouseButton(MouseButtons button)
    {
        if (!_enabled || !_config.BlockPhysicalInputForMappedKeys)
            return false;

        return _mouseButtonToButton.ContainsKey(button);
    }

    private void OnKeyDown(Keys key)
    {
        if (key == _toggleKey)
        {
            ToggleEnabled();
            return;
        }

        if (!_enabled)
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
        if (!_enabled)
            return;

        if (_mouseButtonToButton.TryGetValue(button, out var buttonName))
            _controller.SetButton(buttonName, pressed);
    }

    private void OnMouseMoveDelta(int dx, int dy)
    {
        if (!_enabled)
            return;

        var sensitivity = _config.RightStick.Sensitivity * 0.02;
        _rightStickX = Math.Clamp(_rightStickX + dx * sensitivity, -1.0, 1.0);
        var signedDy = _config.RightStick.InvertY ? dy : -dy;
        _rightStickY = Math.Clamp(_rightStickY + signedDy * sensitivity, -1.0, 1.0);
    }

    private void OnTick()
    {
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
        _hooks.Dispose();
        _controller.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }
}
