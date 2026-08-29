using System.Drawing.Imaging;

namespace GamepadEmulator.Debugging;

// Writes a PNG screenshot plus one row into a shared log.csv for a single debug
// "event" (e.g. LMB pressed, or the moment right after the aim-assist snap moved
// the mouse). Used only when AimAssist.DebugCaptureEnabled is on - lets the game
// author check exactly what the tool saw and computed for each shot, after the
// fact, without watching it live.
internal static class DebugCapture
{
    private static readonly object Lock = new();

    public static void Save(string dir, string label, Bitmap screenshot, IReadOnlyList<(string Key, object? Value)> fields)
    {
        Directory.CreateDirectory(dir);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var imageFileName = $"{timestamp}_{label}.png";

        lock (Lock)
        {
            screenshot.Save(Path.Combine(dir, imageFileName), ImageFormat.Png);

            var logPath = Path.Combine(dir, "log.csv");
            var isNew = !File.Exists(logPath);
            using var writer = new StreamWriter(logPath, append: true);
            if (isNew)
                writer.WriteLine("Timestamp,Label,Screenshot," + string.Join(",", fields.Select(f => f.Key)));

            var values = fields.Select(f => f.Value switch
            {
                null => "",
                bool b => b ? "true" : "false",
                double d => d.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                _ => f.Value.ToString() ?? "",
            });
            writer.WriteLine($"{timestamp},{label},{imageFileName},{string.Join(",", values)}");
        }
    }
}
