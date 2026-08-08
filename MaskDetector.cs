using System;
using System.Drawing;

namespace Sly4BHLoadDetector
{
    // The bounding box of the mask's foreground pixels, in pixels of the 300x300 processing image.
    public struct MaskBounds
    {
        public int MinCol, MaxCol, MinRow, MaxRow;

        // Inclusive extent - MaxCol is the last lit column, so a one-pixel box is 1 wide.
        public int Width { get { return MaxCol - MinCol + 1; } }
        public int Height { get { return MaxRow - MinRow + 1; } }

        // Exclusive extent, i.e. plain max-min. This is what the reference implementation measures:
        // it slices [min_y:max_y, min_x:max_x], which drops the last row and column. One pixel either
        // way is immaterial on a ~45px box, but the two conventions must not be mixed within a single
        // metric, so every measurement below is taken over the Crop rectangle.
        public int SpanX { get { return MaxCol - MinCol; } }
        public int SpanY { get { return MaxRow - MinRow; } }

        // The full inclusive box, as a half-open Rectangle.
        public Rectangle ToRectangle()
        {
            return Rectangle.FromLTRB(MinCol, MinRow, MaxCol + 1, MaxRow + 1);
        }

        // The box the metrics are measured over - the reference's [min:max] slice, so the last row and
        // column are excluded. Empty when the box is a single pixel wide or tall, which is the
        // degenerate case MaskMetrics.HasCrop reports.
        public Rectangle Crop()
        {
            return Rectangle.FromLTRB(MinCol, MinRow, MaxCol, MaxRow);
        }

        public override string ToString()
        {
            return "rows " + MinRow + ":" + MaxRow + ", cols " + MinCol + ":" + MaxCol +
                   " (" + Width + "x" + Height + ")";
        }
    }

    // Everything one frame's mask measurement produced. Populated as far as the frame allows: a frame
    // with no foreground at all stops at HasForeground, and one whose box is a single pixel wide or
    // tall stops at HasCrop, because the reference's [min:max] slice is empty there and a median over
    // no pixels is not a number.
    public struct MaskMetrics
    {
        public bool HasForeground;
        public bool HasCrop;
        public MaskBounds Bounds;

        // Foreground pixels inside the crop, and their share of it.
        public int ForegroundPixels;
        public int CropPixels;
        public float Fill;

        // Width over height, per the reference. Note this is the reciprocal of the ratio the previous
        // implementation used - a mask taller than it is wide scores below 1 here, above 1 there.
        public float AspectRatio;

        // Medians over every pixel of the crop in the *original* frame, backdrop included - not over
        // the foreground pixels alone. Hue is on OpenCV's 0-179 scale.
        public float MedianHue;
        public float MedianSaturation;
        public float MedianValue;

        // The same three over the foreground pixels only. Diagnostic: the crop is mostly backdrop on a
        // real mask, so the medians above can be dominated by black pixels that carry no colour
        // information at all. These say what the mask itself is coloured, independent of how much of
        // its box it happens to fill.
        public float LitMedianHue;
        public float LitMedianSaturation;
        public float LitMedianValue;

        public override string ToString()
        {
            if (!HasForeground)
            {
                return "no foreground pixels";
            }

            string text = Bounds + " fill=" + Fill.ToString("0.000") +
                          " aspect=" + AspectRatio.ToString("0.000");

            if (!HasCrop)
            {
                return text + " (degenerate box - no crop to measure colour over)";
            }

            return text +
                   " hsv=(" + MedianHue.ToString("0.0") + "," + MedianSaturation.ToString("0.0") +
                   "," + MedianValue.ToString("0.0") + ")" +
                   " lit-hsv=(" + LitMedianHue.ToString("0.0") + "," +
                   LitMedianSaturation.ToString("0.0") + "," + LitMedianValue.ToString("0.0") + ")";
        }
    }

    // Finds the pulsing mask against the loading screen's black backdrop, and measures it.
    //
    // The pipeline is a direct conversion of the reference Python: measure the frame's black level,
    // binarize luma at twice that level, median-blur away the speckle, take the bounding box of what
    // survives inside a fixed region, and report that box's fill, aspect ratio and colour. No
    // calibration beyond the black level is involved - the region is fixed, and nothing is matched
    // against a stored shape.
    internal static class MaskDetector
    {
        // Where the mask is looked for, in pixels of the 300x300 processing image: columns [110, 190),
        // rows [100, 180). Generous enough to cover the mask wherever it lands given an imperfect crop.
        public static readonly Rectangle MaskRegion = Rectangle.FromLTRB(110, 100, 190, 180);

        // The binarization threshold, from this frame's own black patch reading.
        //
        // Two things are going on in (level + 1) * 2. The +1 makes the threshold strictly clear of the
        // measured maximum, so a patch reading 0 still excludes 0. The doubling makes the allowance
        // proportional rather than fixed, which is what lets one constant serve both capture
        // pipelines: an OBS recording whose encoder crushes near-black to 0 thresholds at 2, while a
        // live screen capture reading 3 thresholds at 8 - enough to clear the mask halo that survives
        // there and would otherwise blow the bounding box out to the whole region.
        //
        // Measured against the *current frame's* patch, never the calibrated level. A frame whose
        // noise floor sits a level or two above the calibrated minimum would otherwise light up
        // entirely.
        public static int BinarizationThreshold(int frameBlackLevel)
        {
            return (frameBlackLevel + 1) * 2;
        }

        // Median blur kernel, matching cv.medianBlur(..., 5). On a binary image a median is just a
        // majority vote, so this is "at least 13 of the 25 neighbours are foreground" - it erases
        // isolated speckle and single-pixel bridges without touching the mask's body.
        public const int MedianKernel = 5;
        private const int MedianRadius = MedianKernel / 2;
        private const int MedianMajority = (MedianKernel * MedianKernel) / 2 + 1;

        // Binarizes MaskRegion, median-blurs it, and measures the bounding box of what survives.
        //
        // `blacknessLevel` is the value from BinarizationThreshold; a pixel is foreground when its
        // luma is strictly greater, matching cv.threshold(..., THRESH_BINARY).
        public static MaskMetrics Measure(FramePixels frame, int blacknessLevel)
        {
            MaskMetrics metrics = default(MaskMetrics);

            bool[] foreground = Binarize(frame, blacknessLevel);
            int regionWidth = MaskRegion.Width;

            int minCol = int.MaxValue, maxCol = int.MinValue;
            int minRow = int.MaxValue, maxRow = int.MinValue;

            for (int row = 0; row < MaskRegion.Height; row++)
            {
                for (int col = 0; col < regionWidth; col++)
                {
                    if (!foreground[row * regionWidth + col])
                    {
                        continue;
                    }

                    if (col < minCol) minCol = col;
                    if (col > maxCol) maxCol = col;
                    if (row < minRow) minRow = row;
                    if (row > maxRow) maxRow = row;
                }
            }

            if (minCol > maxCol)
            {
                // Nothing cleared the threshold anywhere in the region. Ordinary on a frame that is
                // genuinely all backdrop, so this is a normal outcome and not an error - but every
                // metric below is undefined, so the caller must check HasForeground before reading
                // any of them.
                return metrics;
            }

            metrics.HasForeground = true;
            metrics.Bounds = new MaskBounds
            {
                MinCol = MaskRegion.Left + minCol,
                MaxCol = MaskRegion.Left + maxCol,
                MinRow = MaskRegion.Top + minRow,
                MaxRow = MaskRegion.Top + maxRow
            };

            // A box with no extent in one axis leaves the reference's [min:max] slice empty: its
            // aspect ratio divides by zero and its medians are taken over nothing. Report it rather
            // than inventing a value - a single lit row is not a mask under any threshold.
            if (metrics.Bounds.SpanX <= 0 || metrics.Bounds.SpanY <= 0)
            {
                return metrics;
            }

            metrics.HasCrop = true;
            metrics.AspectRatio = (float)metrics.Bounds.SpanX / metrics.Bounds.SpanY;

            var hue = new int[180];
            var saturation = new int[256];
            var value = new int[256];
            var litHue = new int[180];
            var litSaturation = new int[256];
            var litValue = new int[256];
            int litCount = 0;

            Rectangle crop = metrics.Bounds.Crop();
            for (int y = crop.Top; y < crop.Bottom; y++)
            {
                for (int x = crop.Left; x < crop.Right; x++)
                {
                    int h, s, v;
                    frame.HsvAt(x, y, out h, out s, out v);
                    hue[h]++;
                    saturation[s]++;
                    value[v]++;

                    if (foreground[(y - MaskRegion.Top) * regionWidth + (x - MaskRegion.Left)])
                    {
                        metrics.ForegroundPixels++;
                        litHue[h]++;
                        litSaturation[s]++;
                        litValue[v]++;
                        litCount++;
                    }
                }
            }

            metrics.CropPixels = crop.Width * crop.Height;
            metrics.Fill = (float)metrics.ForegroundPixels / metrics.CropPixels;

            metrics.MedianHue = Median(hue, metrics.CropPixels);
            metrics.MedianSaturation = Median(saturation, metrics.CropPixels);
            metrics.MedianValue = Median(value, metrics.CropPixels);

            metrics.LitMedianHue = Median(litHue, litCount);
            metrics.LitMedianSaturation = Median(litSaturation, litCount);
            metrics.LitMedianValue = Median(litValue, litCount);

            return metrics;
        }

        // Foreground flags for MaskRegion, median-blurred.
        //
        // The blur needs a two-pixel skirt of context around the region, so the raw threshold is taken
        // over MaskRegion inflated by the kernel radius and only the interior is written out. That
        // makes the result identical to binarizing and blurring the whole frame and then cropping, at
        // a fraction of the work - and MaskRegion sits far enough inside a 300x300 frame that the
        // skirt never needs the frame's own edges.
        private static bool[] Binarize(FramePixels frame, int blacknessLevel)
        {
            Rectangle padded = Rectangle.FromLTRB(
                MaskRegion.Left - MedianRadius, MaskRegion.Top - MedianRadius,
                MaskRegion.Right + MedianRadius, MaskRegion.Bottom + MedianRadius);

            int paddedWidth = padded.Width;
            var raw = new bool[paddedWidth * padded.Height];

            for (int y = 0; y < padded.Height; y++)
            {
                int sourceY = Clamp(padded.Top + y, 0, frame.Height - 1);
                for (int x = 0; x < paddedWidth; x++)
                {
                    int sourceX = Clamp(padded.Left + x, 0, frame.Width - 1);
                    raw[y * paddedWidth + x] = frame.GrayAt(sourceX, sourceY) > blacknessLevel;
                }
            }

            int regionWidth = MaskRegion.Width;
            var blurred = new bool[regionWidth * MaskRegion.Height];

            for (int row = 0; row < MaskRegion.Height; row++)
            {
                for (int col = 0; col < regionWidth; col++)
                {
                    int lit = 0;
                    for (int dy = 0; dy < MedianKernel; dy++)
                    {
                        int offset = (row + dy) * paddedWidth + col;
                        for (int dx = 0; dx < MedianKernel; dx++)
                        {
                            if (raw[offset + dx]) lit++;
                        }
                    }

                    blurred[row * regionWidth + col] = lit >= MedianMajority;
                }
            }

            return blurred;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        // Median of a histogram, following numpy: for an even count it is the mean of the two middle
        // values rather than either one of them, so the result can land on a half.
        private static float Median(int[] histogram, int count)
        {
            if (count <= 0)
            {
                return 0f;
            }

            int lowerIndex = (count - 1) / 2;
            int upperIndex = count / 2;
            int seen = 0, lower = -1, upper = -1;

            for (int v = 0; v < histogram.Length; v++)
            {
                if (histogram[v] == 0)
                {
                    continue;
                }

                int last = seen + histogram[v] - 1;
                if (lower < 0 && lowerIndex <= last) lower = v;
                if (upper < 0 && upperIndex <= last) upper = v;
                seen = last + 1;

                if (lower >= 0 && upper >= 0)
                {
                    break;
                }
            }

            return (lower + upper) / 2f;
        }
    }
}
