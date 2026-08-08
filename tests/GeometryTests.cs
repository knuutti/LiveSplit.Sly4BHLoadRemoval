using System;
using System.Drawing;
using Sly4BHLoadDetector;

// Standalone checks for the geometry/threshold logic in FeatureDetector.cs + MaskDetector.cs.
// Compiled against those source files directly (they only need System.Drawing).
//
// These are the properties the end-to-end suite cannot pin down: it can tell you a frame was
// classified wrongly, but not whether the bounding box is inclusive, whether the median follows
// numpy's even-count rule, or whether the blur erases a speck. Synthetic frames can.
static class DetectorTests
{
    static int failures = 0;

    static void Check(string name, bool condition, string detail = "")
    {
        if (condition) { Console.WriteLine("  PASS  " + name); }
        else { Console.WriteLine("  FAIL  " + name + (detail == "" ? "" : "  -> " + detail)); failures++; }
    }

    static Bitmap Black(int w = 300, int h = 300)
    {
        var b = new Bitmap(w, h);
        using (var g = Graphics.FromImage(b)) g.Clear(Color.Black);
        return b;
    }

    static void Fill(Bitmap b, Rectangle r, int level)
    {
        FillColour(b, r, Color.FromArgb(level, level, level));
    }

    // Grey fills say nothing about hue or saturation, which is most of what the detector measures.
    static void FillColour(Bitmap b, Rectangle r, Color c)
    {
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
                b.SetPixel(x, y, c);
    }

    static MaskMetrics Measure(Bitmap b, int blacknessLevel)
    {
        return MaskDetector.Measure(new FramePixels(b), blacknessLevel);
    }

    static void Main()
    {
        // ---- 1. Black reference patch: cols [40,80), rows [120,160) - 40x40 ----
        Console.WriteLine("Black reference patch geometry:");
        Check("region is cols 40:80, rows 120:160",
            FeatureDetector.BlackRegion.Left == 40 && FeatureDetector.BlackRegion.Right == 80 &&
            FeatureDetector.BlackRegion.Top == 120 && FeatureDetector.BlackRegion.Bottom == 160,
            FeatureDetector.BlackRegion.ToString());
        Check("region is 40 wide x 40 tall",
            FeatureDetector.BlackRegion.Width == 40 && FeatureDetector.BlackRegion.Height == 40);
        // The two must not touch, or the mask itself would raise the level meant to measure backdrop.
        Check("does not overlap the mask region",
            !FeatureDetector.BlackRegion.IntersectsWith(MaskDetector.MaskRegion));

        using (var b = Black())
        {
            Fill(b, new Rectangle(40, 120, 1, 1), 77);
            Check("reads a pixel at the top-left corner (40,120)",
                FeatureDetector.GetBlackLevel(new FramePixels(b)) == 77);
        }
        using (var b = Black())
        {
            Fill(b, new Rectangle(79, 159, 1, 1), 77);
            Check("reads a pixel at the bottom-right corner (79,159)",
                FeatureDetector.GetBlackLevel(new FramePixels(b)) == 77);
        }
        using (var b = Black())
        {
            Fill(b, new Rectangle(80, 140, 1, 1), 200);  // one column past the right edge
            Fill(b, new Rectangle(39, 140, 1, 1), 200);  // one column before the left edge
            Fill(b, new Rectangle(60, 160, 1, 1), 200);  // one row past the bottom edge
            Fill(b, new Rectangle(60, 119, 1, 1), 200);  // one row before the top edge
            Check("ignores pixels just outside the patch",
                FeatureDetector.GetBlackLevel(new FramePixels(b)) == 0);
        }
        using (var b = Black())
        {
            Fill(b, FeatureDetector.BlackRegion, 4);
            Fill(b, new Rectangle(55, 140, 1, 1), 9);
            Check("takes the MAX, not a percentile (one bright pixel wins)",
                FeatureDetector.GetBlackLevel(new FramePixels(b)) == 9);
        }
        using (var b = Black())
        {
            // Intensity is a max over channels, so a saturated single channel reads full.
            FillColour(b, new Rectangle(55, 140, 1, 1), Color.FromArgb(0, 0, 255));
            Check("black level is a max over channels, not luma",
                FeatureDetector.GetBlackLevel(new FramePixels(b)) == 255);
        }

        // ---- 2. Mask region: cols [110,190), rows [100,180) - 80x80 ----
        Console.WriteLine("\nMask region geometry:");
        Check("region is cols 110:190, rows 100:180",
            MaskDetector.MaskRegion.Left == 110 && MaskDetector.MaskRegion.Right == 190 &&
            MaskDetector.MaskRegion.Top == 100 && MaskDetector.MaskRegion.Bottom == 180,
            MaskDetector.MaskRegion.ToString());
        Check("region is 80 wide x 80 tall",
            MaskDetector.MaskRegion.Width == 80 && MaskDetector.MaskRegion.Height == 80);

        // ---- 3. The binarization threshold: (level + 1) * 2 ----
        Console.WriteLine("\nBinarization threshold:");
        Check("a black level of 0 thresholds at 2", MaskDetector.BinarizationThreshold(0) == 2);
        Check("a black level of 3 thresholds at 8", MaskDetector.BinarizationThreshold(3) == 8);
        Check("it scales with the level, it is not a fixed margin",
            MaskDetector.BinarizationThreshold(10) == 22);

        // ---- 4. Foreground rule: luma, strictly greater ----
        Console.WriteLine("\nForeground rule:");
        using (var b = Black())
        {
            Fill(b, Rectangle.FromLTRB(130, 120, 170, 160), 7);
            Check("luma == threshold is background (strictly greater is foreground)",
                !Measure(b, 7).HasForeground);
            Check("luma one above the threshold is foreground",
                Measure(b, 6).HasForeground);
        }
        using (var b = Black())
        {
            // Pure blue: intensity 255 but luma only 29. The reference binarizes on luma, so a
            // saturated blue is far dimmer here than "how black is it?" would suggest.
            FillColour(b, Rectangle.FromLTRB(130, 120, 170, 160), Color.FromArgb(0, 0, 255));
            Check("thresholds on luma, not on max channel", !Measure(b, 30).HasForeground);
            Check("the same pixels are foreground below their luma", Measure(b, 28).HasForeground);
        }

        // ---- 5. Median blur (5x5 majority) ----
        Console.WriteLine("\nMedian blur:");
        using (var b = Black())
        {
            // 3x3 speck: no pixel can see more than 9 of 25 neighbours lit, so nothing survives.
            Fill(b, new Rectangle(149, 139, 3, 3), 200);
            Check("a 3x3 speck is erased entirely", !Measure(b, 5).HasForeground);
        }
        using (var b = Black())
        {
            // 5x5 speck: the blur takes the corners off but not the extent. A pixel offset (dx,dy)
            // from the centre sees (5-|dx|)*(5-|dy|) lit neighbours, so the edge midpoints keep 15 of
            // 25 and survive while the corners see 9 and do not.
            Fill(b, new Rectangle(148, 138, 5, 5), 200);
            var m = Measure(b, 5);
            Check("a 5x5 speck keeps its full extent",
                m.HasForeground && m.Bounds.MinCol == 148 && m.Bounds.MaxCol == 152 &&
                m.Bounds.MinRow == 138 && m.Bounds.MaxRow == 142, m.ToString());
            // Crop is the 4x4 at rows 138..141 x cols 148..151; 11 of those 16 survive the blur.
            Check("but loses its corners (11 of the crop's 16 pixels survive)",
                m.ForegroundPixels == 11 && m.CropPixels == 16, m.ToString());
        }
        using (var b = Black())
        {
            // A solid blob keeps its bounding box - the blur only rounds the corners off, and the
            // extreme rows/columns still have a lit majority at their midpoints.
            Fill(b, Rectangle.FromLTRB(130, 120, 170, 160), 200);
            var m = Measure(b, 5);
            Check("a solid 40x40 blob keeps its exact bounding box",
                m.Bounds.MinCol == 130 && m.Bounds.MaxCol == 169 &&
                m.Bounds.MinRow == 120 && m.Bounds.MaxRow == 159, m.ToString());
        }
        using (var b = Black())
        {
            // Blob straddling the region edge: only the part inside is measured.
            Fill(b, Rectangle.FromLTRB(90, 120, 130, 160), 200);
            var m = Measure(b, 5);
            Check("clips to the mask region", m.Bounds.MinCol == 110, m.ToString());
        }

        // ---- 6. Bounds conventions: inclusive edges, exclusive crop ----
        Console.WriteLine("\nBounds conventions:");
        var box = new MaskBounds { MinCol = 130, MaxCol = 169, MinRow = 120, MaxRow = 159 };
        Check("Width/Height count both endpoints", box.Width == 40 && box.Height == 40);
        Check("SpanX/SpanY are plain max-min", box.SpanX == 39 && box.SpanY == 39);
        Check("ToRectangle is half-open over the inclusive box",
            box.ToRectangle() == Rectangle.FromLTRB(130, 120, 170, 160), box.ToRectangle().ToString());
        Check("Crop drops the last row and column, as the reference slice does",
            box.Crop() == Rectangle.FromLTRB(130, 120, 169, 159), box.Crop().ToString());

        // ---- 7. Metrics over a fully lit region ----
        Console.WriteLine("\nMetrics:");
        using (var b = Black())
        {
            // Whole frame lit, so the blur erodes nothing and the box is the region exactly.
            Fill(b, new Rectangle(0, 0, 300, 300), 200);
            var m = Measure(b, 5);
            Check("box is the whole mask region",
                m.Bounds.MinCol == 110 && m.Bounds.MaxCol == 189 &&
                m.Bounds.MinRow == 100 && m.Bounds.MaxRow == 179, m.ToString());
            Check("crop is 79x79, one short of the box on each axis", m.CropPixels == 79 * 79);
            Check("a completely lit crop fills 1.000", Math.Abs(m.Fill - 1.0f) < 1e-6, m.Fill.ToString());
            Check("a square box has aspect 1.000", Math.Abs(m.AspectRatio - 1.0f) < 1e-6);
        }
        using (var b = Black())
        {
            // Aspect is width over height - a wide box scores above 1.
            Fill(b, Rectangle.FromLTRB(120, 120, 180, 150), 200);
            var m = Measure(b, 5);
            Check("aspect is width/height (a wide blob scores above 1)",
                Math.Abs(m.AspectRatio - 59.0f / 29.0f) < 1e-4, m.AspectRatio.ToString());
        }
        using (var b = Black())
        {
            // Two separated blobs: the box spans both, so the fill drops well below 1.
            Fill(b, new Rectangle(120, 110, 20, 20), 200);
            Fill(b, new Rectangle(160, 150, 20, 20), 200);
            var m = Measure(b, 5);
            Check("fill is measured over the box, not the region",
                m.Fill > 0.1f && m.Fill < 0.35f, m.Fill.ToString("0.000"));
            Check("box spans both blobs",
                m.Bounds.MinCol == 120 && m.Bounds.MaxCol == 179, m.ToString());
        }

        // ---- 8. HSV, on OpenCV's 8-bit scale ----
        Console.WriteLine("\nHSV medians:");
        using (var b = Black())
        {
            // r=60 g=130 b=200 -> max=blue so hue = 240 - 30 = 210 degrees -> 105 halved.
            // saturation = 140*255/200 = 179, value = 200.
            FillColour(b, new Rectangle(0, 0, 300, 300), Color.FromArgb(60, 130, 200));
            var m = Measure(b, 5);
            Check("median hue is degrees halved to fit 0-179",
                Math.Abs(m.MedianHue - 105f) < 1e-6, m.MedianHue.ToString());
            Check("median saturation matches OpenCV's formula",
                Math.Abs(m.MedianSaturation - 179f) < 1e-6, m.MedianSaturation.ToString());
            Check("median value is the max channel",
                Math.Abs(m.MedianValue - 200f) < 1e-6, m.MedianValue.ToString());
            Check("lit medians agree when every crop pixel is lit",
                m.LitMedianHue == m.MedianHue && m.LitMedianValue == m.MedianValue);
        }
        using (var b = Black())
        {
            // Even pixel count: numpy averages the two middle values rather than picking one.
            //
            // Rows 177-179 are blacked out, which the blur widens to 177-179 exactly (row 176 still
            // sees 3 lit rows out of 5), leaving a box of rows 100..176 and a crop 79 wide x 76 tall
            // = 6004 pixels. Split evenly between grey 100 and grey 200, the two middle values differ
            // and the median must land between them.
            Fill(b, new Rectangle(0, 0, 300, 300), 100);
            Fill(b, Rectangle.FromLTRB(0, 100, 300, 138), 200);
            Fill(b, Rectangle.FromLTRB(0, 177, 300, 180), 0);
            var m = Measure(b, 5);
            Check("crop has an even pixel count", m.CropPixels == 79 * 76 && m.CropPixels % 2 == 0,
                m.CropPixels.ToString());
            Check("even count averages the two middle values (100 and 200 -> 150)",
                Math.Abs(m.MedianValue - 150f) < 1e-6, m.MedianValue.ToString());
        }

        // ---- 9. Degenerate and empty frames ----
        Console.WriteLine("\nDegenerate frames:");
        using (var b = Black())
        {
            var m = Measure(b, 5);
            Check("an all-black region reports no foreground", !m.HasForeground);
            Check("no foreground means no crop either", !m.HasCrop);
        }
        using (var b = Black())
        {
            // A 3-wide, 5-tall block blurs down to a single row. Counting lit neighbours as
            // (lit columns in window) * (lit rows in window): the middle row scores 3*5 = 15 and
            // survives, the rows either side 3*4 = 12 and do not. That leaves a box with no vertical
            // extent, so the reference's [min:max] slice is empty and both the aspect ratio and the
            // medians are undefined. Must be reported, not invented.
            Fill(b, Rectangle.FromLTRB(149, 138, 152, 143), 200);
            var m = Measure(b, 5);
            Check("a 3x5 block collapses to a single row", m.HasForeground && m.Bounds.SpanY == 0,
                m.ToString());
            Check("a one-row box has no crop", !m.HasCrop);
            Check("its aspect ratio is left at zero rather than dividing by zero",
                m.AspectRatio == 0f);
        }

        // ---- 10. Gate boundaries are inclusive ----
        Console.WriteLine("\nGate boundaries:");
        var good = new MaskMetrics
        {
            HasForeground = true,
            HasCrop = true,
            Fill = 0.77f,
            AspectRatio = 1.08f,
            MedianHue = 111f,
            MedianSaturation = 149f,
            MedianValue = 81f
        };
        Check("a settled loading screen passes every gate",
            MaskGates.FirstFailure(good) == DetectionStage.Accepted,
            MaskGates.FirstFailure(good).ToString());

        var atEdges = good;
        atEdges.Fill = MaskGates.MinFill;
        atEdges.AspectRatio = MaskGates.MaxAspectRatio;
        atEdges.MedianHue = MaskGates.MaxHue;
        Check("values exactly on the bounds are accepted",
            MaskGates.FirstFailure(atEdges) == DetectionStage.Accepted,
            MaskGates.FirstFailure(atEdges).ToString());

        // Saturation and value are measured and logged but deliberately not gated - see MaskGates.
        // Wild readings on either must not reject a frame that is otherwise right.
        var oddColour = good;
        oddColour.MedianSaturation = 250f;
        oddColour.MedianValue = 5f;
        Check("saturation and value are not gated",
            MaskGates.FirstFailure(oddColour) == DetectionStage.Accepted,
            MaskGates.FirstFailure(oddColour).ToString());

        var lowFill = good; lowFill.Fill = MaskGates.MinFill - 0.001f;
        Check("fill just under the floor is rejected as Fill",
            MaskGates.FirstFailure(lowFill) == DetectionStage.Fill);

        var tall = good; tall.AspectRatio = MaskGates.MinAspectRatio - 0.001f;
        Check("aspect just under the floor is rejected as AspectRatio",
            MaskGates.FirstFailure(tall) == DetectionStage.AspectRatio);

        var warm = good; warm.MedianHue = MaskGates.MaxHue + 1;
        Check("hue just over the ceiling is rejected as Hue",
            MaskGates.FirstFailure(warm) == DetectionStage.Hue);

        // Geometry is reported before colour, so a frame wrong on both names the structural fault.
        var wrongOnBoth = good;
        wrongOnBoth.Fill = 0.1f;
        wrongOnBoth.MedianHue = 5f;
        Check("geometry is reported ahead of colour",
            MaskGates.FirstFailure(wrongOnBoth) == DetectionStage.Fill);

        Console.WriteLine(failures == 0 ? "\nAll checks passed." : "\n" + failures + " CHECK(S) FAILED.");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
