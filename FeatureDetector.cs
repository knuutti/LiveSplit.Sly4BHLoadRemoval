using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Sly4BHLoadDetector
{
    // A frame's pixels copied out of a Bitmap once, so the several region queries detection makes per
    // frame (black patch, binarization, HSV medians) don't each pay for their own LockBits.
    //
    // Every capture reaching detection has been through ImageCapture.ResizeImage, which always produces
    // a 32bpp Bitmap, so 4 bytes per pixel is safe here.
    internal sealed class FramePixels
    {
        private readonly byte[] rgb;
        private readonly int stride;

        public int Width { get; private set; }
        public int Height { get; private set; }

        public FramePixels(Bitmap frame)
        {
            Width = frame.Width;
            Height = frame.Height;

            Rectangle rect = new Rectangle(0, 0, Width, Height);
            BitmapData data = frame.LockBits(rect, ImageLockMode.ReadOnly, frame.PixelFormat);
            try
            {
                stride = Math.Abs(data.Stride);
                rgb = new byte[stride * Height];
                Marshal.Copy(data.Scan0, rgb, 0, rgb.Length);
            }
            finally
            {
                frame.UnlockBits(data);
            }
        }

        // Intensity is the brightest of the three colour channels. This is what "how black is it?"
        // means throughout: np.max over a colour image is a max over channels as well as pixels, so
        // the black level the Python reference measures is exactly this.
        public int IntensityAt(int x, int y)
        {
            int i = y * stride + x * 4;
            return Math.Max(rgb[i], Math.Max(rgb[i + 1], rgb[i + 2]));
        }

        // 32bpp little-endian stores BGRA, so these offsets are not a guess - they match the three
        // bytes IntensityAt takes its max over.
        public int BlueAt(int x, int y)
        {
            return rgb[y * stride + x * 4];
        }

        public int GreenAt(int x, int y)
        {
            return rgb[y * stride + x * 4 + 1];
        }

        public int RedAt(int x, int y)
        {
            return rgb[y * stride + x * 4 + 2];
        }

        // Luma, matching cv.cvtColor(..., COLOR_BGR2GRAY): the same fixed-point weights OpenCV uses
        // (0.299R + 0.587G + 0.114B in Q14), so a frame binarizes here exactly as it does there.
        //
        // Note this is a different quantity from IntensityAt, and deliberately so - the reference
        // measures the black level as a max over channels but thresholds against luma, so a saturated
        // blue pixel reads far dimmer here than its intensity suggests.
        public int GrayAt(int x, int y)
        {
            int i = y * stride + x * 4;
            int b = rgb[i], g = rgb[i + 1], r = rgb[i + 2];
            return (r * 4899 + g * 9617 + b * 1868 + 8192) >> 14;
        }

        // HSV in OpenCV's 8-bit convention: H in [0,179] (degrees halved to fit a byte), S and V in
        // [0,255]. Returned as ints because every consumer histograms them.
        public void HsvAt(int x, int y, out int hue, out int saturation, out int value)
        {
            int i = y * stride + x * 4;
            int b = rgb[i], g = rgb[i + 1], r = rgb[i + 2];

            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            int delta = max - min;

            value = max;
            saturation = max == 0 ? 0 : (delta * 255 + max / 2) / max;

            if (delta == 0)
            {
                hue = 0;
                return;
            }

            double h;
            if (max == r) h = 60.0 * (g - b) / delta;
            else if (max == g) h = 120.0 + 60.0 * (b - r) / delta;
            else h = 240.0 + 60.0 * (r - g) / delta;

            if (h < 0) h += 360.0;

            hue = (int)Math.Round(h / 2.0);
            if (hue > 179) hue = 179;
        }

        // Clamps to the frame so callers can pass regions that spill over the edge without checking
        // first.
        public Rectangle Clamp(Rectangle region)
        {
            int left = Math.Max(0, region.Left);
            int top = Math.Max(0, region.Top);
            int right = Math.Min(Width, region.Right);
            int bottom = Math.Min(Height, region.Bottom);

            return Rectangle.FromLTRB(left, top, Math.Max(left, right), Math.Max(top, bottom));
        }

        public int MaxIntensity(Rectangle region)
        {
            Rectangle r = Clamp(region);
            int max = 0;

            for (int y = r.Top; y < r.Bottom; y++)
            {
                for (int x = r.Left; x < r.Right; x++)
                {
                    int intensity = IntensityAt(x, y);
                    if (intensity > max)
                    {
                        max = intensity;
                    }
                }
            }

            return max;
        }
    }

    // Measures the loading screen's black level from a fixed patch of the processing image.
    internal static class FeatureDetector
    {
        // The reference patch, in pixels of the 300x300 processing image: columns [40, 80), rows
        // [120, 160), i.e. 40x40. Sits to the left of the mask and level with it, well inside the game
        // frame, and is solid backdrop for the whole duration of a loading screen.
        //
        // These are absolute pixel coordinates, not fractions of the capture. That is only valid
        // because ComponentSettings.CaptureImage() always resizes the user's crop to exactly 300x300 -
        // if that size ever changes, every constant here and in MaskDetector has to change with it.
        public static readonly Rectangle BlackRegion = Rectangle.FromLTRB(40, 120, 80, 160);

        // How far above the calibrated black level the patch may read and still count as black.
        // Absorbs the frame-to-frame capture/compression noise that the calibrated minimum, by
        // construction, sits at the bottom of.
        public const int BlackLevelTolerance = 10;

        // The *maximum* intensity in the reference patch. Deliberately a strict max rather than a
        // percentile: calibration takes the minimum of this across frames, so a single frame whose
        // patch is genuinely clean establishes the black level, and any frame with something bright in
        // the patch is simply not that minimum. Softening this to a percentile would let real content
        // leak into the reading.
        public static int GetBlackLevel(FramePixels frame)
        {
            return frame.MaxIntensity(BlackRegion);
        }
    }
}
