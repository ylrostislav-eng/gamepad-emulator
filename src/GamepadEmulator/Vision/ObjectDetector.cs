using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GamepadEmulator.Vision;

// A detected target's aim point, in the same bitmap-local coordinate space
// TargetMatch/PoseMatch use.
internal readonly struct ObjectMatch
{
    public required Point ChestPoint { get; init; }
    public required float Confidence { get; init; }
    public required Rectangle Box { get; init; }
}

// Runs a custom-trained single-class YOLOv8 detector, fine-tuned on screenshots from
// this specific game (via mapping.json's AimAssist.CustomModelPath) rather than the
// generic pretrained pose model. The training data was labeled directly around the
// enemy's chest/torso, so the detected box IS the chest - the box center is the aim
// point directly, no further chest-offset guessing needed. It also never saw the
// player's own arm/bow labeled as a target, so - given enough varied training examples
// - it should learn not to fire on it, unlike a general-purpose person detector.
internal sealed class ObjectDetector : IDisposable
{
    private const int InputSize = 640;
    private readonly InferenceSession _session;

    public ObjectDetector(string modelPath)
    {
        var options = new SessionOptions();
        try
        {
            // GPU via DirectML - works on NVIDIA/AMD/Intel through the normal graphics
            // driver already installed for gaming, no separate CUDA/cuDNN toolkit needed.
            options.AppendExecutionProvider_DML(0);
        }
        catch
        {
            // No DirectML-capable adapter found - falls back to CPU automatically.
        }

        _session = new InferenceSession(modelPath, options);
    }

    public void Dispose() => _session.Dispose();

    // Runs detection on the whole bitmap and returns the detection nearest to the
    // bitmap's center (i.e. nearest the crosshair), mirroring TargetDetector's and
    // PoseDetector's approach to picking among multiple targets on screen.
    public ObjectMatch? DetectNearestTarget(Bitmap bitmap, float confThreshold, float iouThreshold)
    {
        var (tensor, scale, padX, padY) = Preprocess(bitmap);

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor("images", tensor) });
        var output = results.First(r => r.Name == "output0").AsTensor<float>();

        var detections = Decode(output, confThreshold);
        detections = NonMaxSuppress(detections, iouThreshold);

        if (detections.Count == 0)
            return null;

        var centerX = bitmap.Width / 2.0;
        var centerY = bitmap.Height / 2.0;

        ObjectMatch? best = null;
        var bestDistSq = double.MaxValue;

        foreach (var det in detections)
        {
            var match = ToObjectMatch(det, scale, padX, padY);
            var dx = match.ChestPoint.X - centerX;
            var dy = match.ChestPoint.Y - centerY;
            var distSq = dx * dx + dy * dy;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = match;
            }
        }

        return best;
    }

    // Letterbox-resizes the capture into a 640x640 canvas (grey padding, standard for
    // YOLO models) and packs it into a normalized [0,1] RGB, channel-first tensor.
    private static (DenseTensor<float> Tensor, double Scale, int PadX, int PadY) Preprocess(Bitmap bitmap)
    {
        var scale = Math.Min((double)InputSize / bitmap.Width, (double)InputSize / bitmap.Height);
        var newW = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var newH = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        var padX = (InputSize - newW) / 2;
        var padY = (InputSize - newH) / 2;

        using var canvas = new Bitmap(InputSize, InputSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.Clear(Color.FromArgb(114, 114, 114));
            g.DrawImage(bitmap, padX, padY, newW, newH);
        }

        var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        var data = canvas.LockBits(new Rectangle(0, 0, InputSize, InputSize), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            var basePtr = (byte*)data.Scan0;
            for (var y = 0; y < InputSize; y++)
            {
                var row = basePtr + y * data.Stride;
                for (var x = 0; x < InputSize; x++)
                {
                    var pixel = row + x * 4;
                    tensor[0, 0, y, x] = pixel[2] / 255f; // R
                    tensor[0, 1, y, x] = pixel[1] / 255f; // G
                    tensor[0, 2, y, x] = pixel[0] / 255f; // B
                }
            }
        }
        canvas.UnlockBits(data);

        return (tensor, scale, padX, padY);
    }

    private readonly struct RawDetection
    {
        public required float CenterX { get; init; }
        public required float CenterY { get; init; }
        public required float Width { get; init; }
        public required float Height { get; init; }
        public required float Confidence { get; init; }
    }

    // output0 layout: [1, 5, 8400] - for each of 8400 anchors: box (cx,cy,w,h) in
    // 640-space, then 1 confidence score (single "target" class).
    private static List<RawDetection> Decode(Tensor<float> output, float confThreshold)
    {
        var anchors = output.Dimensions[2];
        var result = new List<RawDetection>();

        for (var a = 0; a < anchors; a++)
        {
            var conf = output[0, 4, a];
            if (conf < confThreshold)
                continue;

            result.Add(new RawDetection
            {
                CenterX = output[0, 0, a],
                CenterY = output[0, 1, a],
                Width = output[0, 2, a],
                Height = output[0, 3, a],
                Confidence = conf,
            });
        }

        return result;
    }

    // Standard greedy NMS: keep the highest-confidence box, drop the rest that overlap
    // it beyond iouThreshold, repeat.
    private static List<RawDetection> NonMaxSuppress(List<RawDetection> detections, float iouThreshold)
    {
        var sorted = detections.OrderByDescending(d => d.Confidence).ToList();
        var kept = new List<RawDetection>();

        while (sorted.Count > 0)
        {
            var top = sorted[0];
            kept.Add(top);
            sorted.RemoveAt(0);
            sorted.RemoveAll(d => Iou(top, d) > iouThreshold);
        }

        return kept;
    }

    private static float Iou(RawDetection a, RawDetection b)
    {
        var ax1 = a.CenterX - a.Width / 2; var ay1 = a.CenterY - a.Height / 2;
        var ax2 = a.CenterX + a.Width / 2; var ay2 = a.CenterY + a.Height / 2;
        var bx1 = b.CenterX - b.Width / 2; var by1 = b.CenterY - b.Height / 2;
        var bx2 = b.CenterX + b.Width / 2; var by2 = b.CenterY + b.Height / 2;

        var interX1 = Math.Max(ax1, bx1); var interY1 = Math.Max(ay1, by1);
        var interX2 = Math.Min(ax2, bx2); var interY2 = Math.Min(ay2, by2);
        var interArea = Math.Max(0, interX2 - interX1) * Math.Max(0, interY2 - interY1);
        if (interArea <= 0)
            return 0f;

        var areaA = a.Width * a.Height;
        var areaB = b.Width * b.Height;
        return interArea / (areaA + areaB - interArea);
    }

    // Maps a detection from 640-space back to the original bitmap's pixel coordinates.
    // The box IS the chest/torso (that's what the training data was labeled around), so
    // its center is used directly as the aim point.
    private static ObjectMatch ToObjectMatch(RawDetection det, double scale, int padX, int padY)
    {
        double Unmap(float v, int pad) => (v - pad) / scale;

        var boxX1 = Unmap(det.CenterX - det.Width / 2, padX);
        var boxY1 = Unmap(det.CenterY - det.Height / 2, padY);
        var boxX2 = Unmap(det.CenterX + det.Width / 2, padX);
        var boxY2 = Unmap(det.CenterY + det.Height / 2, padY);
        var box = Rectangle.FromLTRB(
            (int)Math.Round(boxX1), (int)Math.Round(boxY1), (int)Math.Round(boxX2), (int)Math.Round(boxY2));

        return new ObjectMatch
        {
            ChestPoint = new Point(box.Left + box.Width / 2, box.Top + box.Height / 2),
            Confidence = det.Confidence,
            Box = box,
        };
    }
}
