using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace GamepadEmulator.Virtual;

public sealed class VirtualXbox360Controller : IDisposable
{
    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _controller;

    public VirtualXbox360Controller()
    {
        _client = new ViGEmClient();
        _controller = _client.CreateXbox360Controller();
        _controller.Connect();
    }

    public void SetButton(string buttonName, bool pressed)
    {
        switch (buttonName)
        {
            case "LeftTrigger":
                _controller.SetSliderValue(Xbox360Slider.LeftTrigger, pressed ? byte.MaxValue : (byte)0);
                return;
            case "RightTrigger":
                _controller.SetSliderValue(Xbox360Slider.RightTrigger, pressed ? byte.MaxValue : (byte)0);
                return;
        }

        var button = ResolveButton(buttonName);
        if (button is not null)
            _controller.SetButtonState(button, pressed);
    }

    public void SetLeftStick(double x, double y)
    {
        _controller.SetAxisValue(Xbox360Axis.LeftThumbX, ToAxisValue(x));
        _controller.SetAxisValue(Xbox360Axis.LeftThumbY, ToAxisValue(y));
    }

    public void SetRightStick(double x, double y)
    {
        _controller.SetAxisValue(Xbox360Axis.RightThumbX, ToAxisValue(x));
        _controller.SetAxisValue(Xbox360Axis.RightThumbY, ToAxisValue(y));
    }

    private static short ToAxisValue(double normalized)
    {
        var clamped = Math.Clamp(normalized, -1.0, 1.0);
        return (short)Math.Round(clamped * short.MaxValue);
    }

    private static Xbox360Button? ResolveButton(string name) => name switch
    {
        "A" => Xbox360Button.A,
        "B" => Xbox360Button.B,
        "X" => Xbox360Button.X,
        "Y" => Xbox360Button.Y,
        "LeftShoulder" => Xbox360Button.LeftShoulder,
        "RightShoulder" => Xbox360Button.RightShoulder,
        "Back" => Xbox360Button.Back,
        "Start" => Xbox360Button.Start,
        "Guide" => Xbox360Button.Guide,
        "LeftThumb" => Xbox360Button.LeftThumb,
        "RightThumb" => Xbox360Button.RightThumb,
        "Up" => Xbox360Button.Up,
        "Down" => Xbox360Button.Down,
        "Left" => Xbox360Button.Left,
        "Right" => Xbox360Button.Right,
        _ => null,
    };

    public void Dispose()
    {
        _controller.Disconnect();
        _client.Dispose();
    }
}
