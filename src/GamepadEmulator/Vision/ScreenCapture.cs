using System.Drawing;
using System.Drawing.Imaging;

namespace GamepadEmulator.Vision;

internal static class ScreenCapture
{
    public static Bitmap CaptureRegion(Rectangle region)
    {
        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    public static Color GetPixelColor(Point screenPoint)
    {
        using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(screenPoint.X, screenPoint.Y, 0, 0, new Size(1, 1), CopyPixelOperation.SourceCopy);
        return bitmap.GetPixel(0, 0);
    }
}
