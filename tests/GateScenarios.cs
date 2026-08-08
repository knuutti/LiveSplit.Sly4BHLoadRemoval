using System;
using System.Drawing;
using Sly4BHLoadDetector;

// Synthetic frames asserting what each gate can and cannot catch, including the blind spots.
//
// The real frames in testdata\ decide whether the detector works; this decides whether it fails the
// way it is documented to. Every scenario states the outcome it requires - including the two blind
// spots - so a change that silently alters one of them fails here rather than being rediscovered
// from a bad run six months later.
//
//   csc /out:tests\GateScenarios.exe tests\GateScenarios.cs
//       LoadDetector.cs FeatureDetector.cs MaskDetector.cs /r:System.Drawing.dll
static class GateScenarios
{
    // HSV (110, 149, 82) - the middle of the measured loading band, built backwards from the gates:
    // value 82 is the max channel, saturation 149 sets the min channel to 34, and hue 110 (220
    // degrees, blue) puts red 16 below green.
    static readonly Color MaskBlue = Color.FromArgb(34, 50, 82);

    // The same mask with its eyes lit, as the gameplay-to-load transition draws them. Warm, so it
    // lands nowhere near the hue band.
    static readonly Color TransitionWarm = Color.FromArgb(200, 190, 40);

    static Bitmap Black(int w = 300, int h = 300)
    {
        var b = new Bitmap(w, h);
        using (var g = Graphics.FromImage(b)) g.Clear(Color.Black);
        return b;
    }

    static void Fill(Bitmap b, Rectangle r, Color c)
    {
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
                b.SetPixel(x, y, c);
    }

    // Draws a mask-shaped blob: a wide upper block over a narrower lower one, sized to land inside
    // the fill and aspect bands.
    static void DrawMask(Bitmap b, Color c)
    {
        Fill(b, Rectangle.FromLTRB(128, 120, 171, 145), c);
        Fill(b, Rectangle.FromLTRB(138, 145, 161, 160), c);
    }

    static DetectionResult Report(string label, Bitmap b)
    {
        Calibration calibration = default(Calibration);
        calibration.BlackLevel = 0;
        calibration.HasCalibration = true;

        DetectionResult r = LoadDetector.Detect(new FramePixels(b), calibration);

        Console.WriteLine(label);
        Console.WriteLine("  " + r.Describe().Replace("\r\n", "\r\n  "));
        Console.WriteLine();
        return r;
    }

    static int failures = 0;

    static void Expect(string label, Bitmap b, DetectionStage expected)
    {
        DetectionResult r = Report(label, b);
        if (r.RejectedAt != expected)
        {
            Console.WriteLine("  ^^ WRONG: expected " + expected + ", got " + r.RejectedAt + "\r\n");
            failures++;
        }
    }

    static void Main()
    {
        Console.WriteLine("Gates: fill " + MaskGates.MinFill.ToString("0.00") + "-" +
                          MaskGates.MaxFill.ToString("0.00") +
                          ", aspect " + MaskGates.MinAspectRatio.ToString("0.00") + "-" +
                          MaskGates.MaxAspectRatio.ToString("0.00") +
                          ", hue " + MaskGates.MinHue + "-" + MaskGates.MaxHue +
                          "  (saturation and value are measured but not gated)\n");

        // The baseline everything else is a variation on.
        using (var b = Black())
        {
            DrawMask(b, MaskBlue);
            Expect("Genuine loading screen (must be ACCEPTED):", b, DetectionStage.Accepted);
        }

        // Ordinary gameplay. The reference patch is not black, and nothing downstream even runs -
        // which is what makes the naive binarization safe, since it has no way to tell a mask from
        // scenery.
        using (var b = Black())
        {
            Fill(b, new Rectangle(0, 0, 300, 300), Color.FromArgb(90, 110, 70));
            Expect("Ordinary gameplay (must be rejected at the black patch):", b, DetectionStage.BlackPatch);
        }

        // The transition animation: same position, same size, same aspect ratio, wrong colour. This
        // is the case the geometric gates cannot separate and the hue gate can.
        using (var b = Black())
        {
            DrawMask(b, TransitionWarm);
            Expect("Transition animation, warm mask (must be rejected on hue):", b, DetectionStage.Hue);
        }

        // A dim scene whose reference patch happens to read black - a letterbox, a fade, a dark room.
        // The patch gate lets it through, so the mask gates have to do the work. The lit area covers
        // the whole region, so the box blows out and the fill collapses.
        using (var b = Black())
        {
            Fill(b, new Rectangle(0, 60, 300, 200), Color.FromArgb(60, 70, 55));
            Fill(b, FeatureDetector.BlackRegion, Color.Black);
            Expect("Dim non-loading frame with a black patch (must be rejected):", b, DetectionStage.Fill);
        }

        // ---- Blind spot 1: nothing outside the mask region is looked at ----
        //
        // The previous detector checked a band around the mask box, which is how it rejected the
        // transition animation's scattered masks. That band is gone: the only pixels outside the mask
        // region that matter now are the 40x40 reference patch. Anything else on screen is invisible.
        //
        // That is a deliberate trade, not an oversight. The band was measured against the loading
        // screen's own tip text and loot icons, whose vertical extent moves between loads, and
        // threading a gap between them broke real runs. Colour rejects the transition directly and
        // does not care what else is on screen.
        using (var b = Black())
        {
            DrawMask(b, MaskBlue);
            Fill(b, new Rectangle(220, 120, 40, 40), Color.White);   // bright, outside both regions
            Expect("Mask + something bright elsewhere on screen (ACCEPTED - blind spot):",
                b, DetectionStage.Accepted);
        }

        // ---- Blind spot 2: junk inside the mask region is absorbed into the box ----
        //
        // The box is the extent of all foreground in the region, so nothing lit inside the region can
        // ever fall outside it. Stray light next to the mask therefore inflates the box rather than
        // being flagged, and the only thing that notices is the fill dropping and the aspect ratio
        // drifting - indirect, and only once the junk is far enough out.
        //
        // Close junk: absorbed, and the frame is still accepted. The box stretches from 43 wide to 46
        // and the fill drops from 0.835 to about 0.77, both still inside their bands - so nothing
        // reports it, and on a calibration-free detector nothing accumulates it either. The cost is
        // limited to the box being slightly wrong on that frame.
        using (var b = Black())
        {
            DrawMask(b, MaskBlue);
            Fill(b, new Rectangle(172, 128, 3, 9), MaskBlue);
            Expect("Mask + junk 2px outside the box (ACCEPTED - absorbed, blind spot):",
                b, DetectionStage.Accepted);
        }

        // Distant junk: far enough to stretch the box across the region, which the fill does catch.
        using (var b = Black())
        {
            DrawMask(b, MaskBlue);
            Fill(b, new Rectangle(182, 168, 6, 6), MaskBlue);
            Expect("Mask + junk at the region corner (rejected on fill):", b, DetectionStage.Fill);
        }

        // ---- The median is over the whole box, so it needs the mask to be most of it ----
        //
        // Hue, saturation and value are medians over every pixel of the bounding box, backdrop
        // included. That is only meaningful because a settled mask fills three quarters of its own
        // box; when it does not, the median falls to the backdrop's black and the value gate catches
        // it. Worth knowing that the colour gates and the fill gate are not independent.
        using (var b = Black())
        {
            Fill(b, Rectangle.FromLTRB(128, 120, 171, 128), MaskBlue);
            Fill(b, Rectangle.FromLTRB(128, 152, 171, 160), MaskBlue);
            Expect("A hollow box - mask colour, too little of it (rejected):", b, DetectionStage.Fill);
        }

        Console.WriteLine(failures == 0
            ? "All scenarios behaved as required."
            : failures + " SCENARIO(S) WRONG.");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
