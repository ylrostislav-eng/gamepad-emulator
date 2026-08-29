using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GamepadEmulator.Vision;

// A detected person's chest aim point, in the same bitmap-local coordinate space
// TargetDetector's TargetMatch uses - so it plugs into the same downstream pipeline
// (crosshair math, snap-on-release, velocity/lead prediction) with no changes there.
internal readonly struct PoseMatch
{
    public required Point ChestPoint { get; init; }
    public required float Confidence { get; init; }
    public required Rectangle Box { get; init; }
}

// Locates a person directly from pixels (pretrained YOLOv8-pose, COCO keypoints) and
// estimates their chest position from the shoulder/hip keypoints, instead of relying
// on a color-matched UI marker - immune to the marker's own false positives (other
// red HUD elements) and its lack of a real distance signal (a fixed-screen-size icon
// doesn't track how close the target actually is; a detected person's silhouette does).
internal sealed class PoseDetector : IDisposable
{
    private const int InputSize = 640;

    // COCO keypoint indices (17 total) relevant to estimating the chest.
    private const int LeftShoulder = 5, RightShoulder = 6, LeftHip = 11, RightHip = 12;

    private readonly InferenceSession _session;

    public PoseDetector(string modelPath)
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
            // No DirectML-capable adapter found - falls back to CPU automatically
            // (slower, but still functional; the session below still loads fine).
        }

        _session = new InferenceSession(modelPath, options);
    }

    public void Dispose() => _session.Dispose();

    // Runs pose detection on the whole bitmap and returns the detection whose chest
    // point is nearest to the bitmap's center (i.e. nearest the crosshair), mirroring
    // TargetDetector.FindNearestMatch's approach to picking among multiple targets.
    // Detections taller than maxBoxHeightFraction of the bitmap are rejected outright -
    // this is what filters out the player's own visible arm/torso (a first/third-person
    // view model held close to the camera fills a huge fraction of the frame, unlike
    // even a close enemy), which would otherwise usually win "nearest to center" since
    // it's often right in the middle of the screen, and - being attached to the camera
    // rather than the game world - never gets any closer as the camera turns toward it,
    // causing the aim to pull the same direction forever instead of converging.
    public PoseMatch? DetectNearestChest(Bitmap bitmap, float confThreshold, float iouThreshold,
        float keypointConfThreshold, double chestHipRatio, double maxBoxHeightFraction)
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
        var maxBoxHeight = bitmap.Height * Math.Clamp(maxBoxHeightFraction, 0.05, 1.0);

        PoseMatch? best = null;
        var bestDistSq = double.MaxValue;

        foreach (var det in detections)
        {
            var match = ToPoseMatch(det, scale, padX, padY, keypointConfThreshold, chestHipRatio);
            if (match.Box.Height > maxBoxHeight)
                continue;

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
        public required (float X, float Y, float Conf)[] Keypoints { get; init; }
    }

    // output0 layout: [1, 56, 8400] - for each of 8400 anchors: box (cx,cy,w,h) in
    // 640-space, 1 confidence score (single "person" class), then 17 keypoints as
    // (x, y, visibility), all in 640-space pixel coordinates.
    private static List<RawDetection> Decode(Tensor<float> output, float confThreshold)
    {
        var anchors = output.Dimensions[2];
        var result = new List<RawDetection>();

        for (var a = 0; a < anchors; a++)
        {
            var conf = output[0, 4, a];
            if (conf < confThreshold)
                continue;

            var keypoints = new (float X, float Y, float Conf)[17];
            for (var k = 0; k < 17; k++)
            {
                keypoints[k] = (output[0, 5 + k * 3, a], output[0, 6 + k * 3, a], output[0, 7 + k * 3, a]);
            }

            result.Add(new RawDetection
            {
                CenterX = output[0, 0, a],
                CenterY = output[0, 1, a],
                Width = output[0, 2, a],
                Height = output[0, 3, a],
                Confidence = conf,
                Keypoints = keypoints,
            });
        }

        return result;
    }

    // Standard greedy NMS: keep the highest-confidence box, drop the rest that overlap
    // it beyond iouThreshold, repeat - collapses duplicate detections of the same person.
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

    // Maps a detection from 640-space back to the original bitmap's pixel coordinates,
    // and estimates the chest point from the shoulder/hip keypoints: shoulder midpoint,
    // nudged down toward the hip midpoint by chestHipRatio. Falls back to a fixed
    // fraction of the box height below its top when keypoints aren't confident enough
    // (e.g. the person is partly occluded).
    private static PoseMatch ToPoseMatch(RawDetection det, double scale, int padX, int padY,
        float keypointConfThreshold, double chestHipRatio)
    {
        double Unmap(float v, int pad) => (v - pad) / scale;

        var boxX1 = Unmap(det.CenterX - det.Width / 2, padX);
        var boxY1 = Unmap(det.CenterY - det.Height / 2, padY);
        var boxX2 = Unmap(det.CenterX + det.Width / 2, padX);
        var boxY2 = Unmap(det.CenterY + det.Height / 2, padY);
        var box = Rectangle.FromLTRB(
            (int)Math.Round(boxX1), (int)Math.Round(boxY1), (int)Math.Round(boxX2), (int)Math.Round(boxY2));

        var lShoulder = det.Keypoints[LeftShoulder];
        var rShoulder = det.Keypoints[RightShoulder];
        var lHip = det.Keypoints[LeftHip];
        var rHip = det.Keypoints[RightHip];

        double chestX, chestY;
        if (lShoulder.Conf >= keypointConfThreshold && rShoulder.Conf >= keypointConfThreshold)
        {
            var shoulderMidX = Unmap((lShoulder.X + rShoulder.X) / 2, padX);
            var shoulderMidY = Unmap((lShoulder.Y + rShoulder.Y) / 2, padY);

            if (lHip.Conf >= keypointConfThreshold && rHip.Conf >= keypointConfThreshold)
            {
                var hipMidX = Unmap((lHip.X + rHip.X) / 2, padX);
                var hipMidY = Unmap((lHip.Y + rHip.Y) / 2, padY);
                chestX = shoulderMidX + (hipMidX - shoulderMidX) * chestHipRatio * 0.5;
                chestY = shoulderMidY + (hipMidY - shoulderMidY) * chestHipRatio;
            }
            else
            {
                // No confident hip reading - nudge down a modest fraction of the box
                // height instead of using the (unreliable) hip position.
                chestX = shoulderMidX;
                chestY = shoulderMidY + box.Height * 0.15;
            }
        }
        else
        {
            // No confident shoulder reading either - fall back to a fixed fraction of
            // the way down the bounding box, roughly where a chest sits on an upright
            // humanoid silhouette.
            chestX = box.Left + box.Width / 2.0;
            chestY = box.Top + box.Height * 0.35;
        }

        return new PoseMatch
        {
            ChestPoint = new Point((int)Math.Round(chestX), (int)Math.Round(chestY)),
            Confidence = det.Confidence,
            Box = box,
        };
    }
}
