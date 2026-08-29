using System.Drawing;
using System.Drawing.Imaging;

namespace GamepadEmulator.Vision;

internal static class TargetDetector
{
    // Scans for pixels matching the target color and returns the one closest to the
    // region's center (i.e. closest to the crosshair), so multiple enemies on screen
    // don't get averaged into a point between them.
    public static Point? FindNearestMatch(Bitmap bitmap, Color target, int tolerance, int pixelStep)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        var centerX = bitmap.Width / 2.0;
        var centerY = bitmap.Height / 2.0;
        var bestDistSq = double.MaxValue;
        var best = (X: -1, Y: -1);

        unsafe
        {
            var basePtr = (byte*)data.Scan0;
            for (var y = 0; y < bitmap.Height; y += pixelStep)
            {
                var row = basePtr + y * data.Stride;
                for (var x = 0; x < bitmap.Width; x += pixelStep)
                {
                    var pixel = row + x * 4;
                    var b = pixel[0];
                    var g = pixel[1];
                    var r = pixel[2];

                    if (Math.Abs(r - target.R) > tolerance ||
                        Math.Abs(g - target.G) > tolerance ||
                        Math.Abs(b - target.B) > tolerance)
                        continue;

                    var dx = x - centerX;
                    var dy = y - centerY;
                    var distSq = dx * dx + dy * dy;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = (x, y);
                    }
                }
            }
        }

        bitmap.UnlockBits(data);

        return best.X < 0 ? null : new Point(best.X, best.Y);
    }
}
