namespace GamepadEmulator.Vision;

internal sealed class AimOverlayForm : Form
{
    private const int WsExTransparent = 0x20;
    private const int WsExLayered = 0x80000;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x8000000;

    public AimOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate;
            return cp;
        }
    }

    public void ShowAt(int left, int top, int width, int height)
    {
        var bounds = new Rectangle(left, top, width, height);
        if (Bounds != bounds)
            Bounds = bounds;

        if (!Visible)
            Show();

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(Color.Lime, 2);
        e.Graphics.DrawEllipse(pen, 1, 1, Width - 3, Height - 3);
    }
}
