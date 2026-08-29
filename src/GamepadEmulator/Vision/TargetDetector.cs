using System.Drawing;
using System.Drawing.Imaging;

namespace GamepadEmulator.Vision;

internal readonly struct TargetMatch
{
    public required Point Point { get; init; }

    // Estimated on-screen height (px) of the matched marker blob, or null if too few
    // matching pixels were found nearby to estimate it reliably.
    public int? Height { get; init; }
}

internal static class TargetDetector
{
    // Scans for pixels matching the target color and returns the one closest to the
    // region's center (i.e. closest to the crosshair), so multiple enemies on screen
    // don't get averaged into a point between them. Also estimates the matched blob's
    // height via a local rescan, so callers can scale an offset (e.g. down to the
    // chest) with how big/close the marker appears instead of a fixed pixel count.
    public static TargetMatch? FindNearestMatch(Bitmap bitmap, Color target, int tolerance, int pixelStep)
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
                    if (!Matches(row, x, target, tolerance))
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

            if (best.X >= 0)
            {
                var height = EstimateBlobHeight(basePtr, data.Stride, bitmap.Width, bitmap.Height, best.X, best.Y, target, tolerance);
                bitmap.UnlockBits(data);
                return new TargetMatch { Point = new Point(best.X, best.Y), Height = height };
            }
        }

        bitmap.UnlockBits(data);
        return null;
    }

    private static unsafe bool Matches(byte* row, int x, Color target, int tolerance)
    {
        var pixel = row + x * 4;
        var b = pixel[0];
        var g = pixel[1];
        var r = pixel[2];
        return Math.Abs(r - target.R) <= tolerance
               && Math.Abs(g - target.G) <= tolerance
               && Math.Abs(b - target.B) <= tolerance;
    }

    private static unsafe int? EstimateBlobHeight(
        byte* basePtr, int stride, int bitmapWidth, int bitmapHeight,
        int centerX, int centerY, Color target, int tolerance)
    {
        const int windowHalf = 60;
        var minX = Math.Max(0, centerX - windowHalf);
        var maxX = Math.Min(bitmapWidth - 1, centerX + windowHalf);
        var minY = Math.Max(0, centerY - windowHalf);
        var maxY = Math.Min(bitmapHeight - 1, centerY + windowHalf);

        var topY = int.MaxValue;
        var bottomY = int.MinValue;
        var matchCount = 0;

        for (var y = minY; y <= maxY; y++)
        {
            var row = basePtr + y * stride;
            for (var x = minX; x <= maxX; x++)
            {
                if (!Matches(row, x, target, tolerance))
                    continue;

                matchCount++;
                if (y < topY) topY = y;
                if (y > bottomY) bottomY = y;
            }
        }

        if (matchCount < 3)
            return null;

        return bottomY - topY + 1;
    }
}
