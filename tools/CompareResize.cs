using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using Sly4BHLoadDetector;

// Compares the two ways of getting a 300x300 detection frame out of a capture device.
//
//   A: CaptureRegion at full resolution, then ImageCapture.ResizeImage  (GDI+ HighQualityBicubic)
//   B: CaptureScaled                                                    (area average, in-decoder)
//
// B is far cheaper, but it feeds the detector different pixels, and the gates in MaskGates were
// measured on A. This exists to decide whether that difference matters: it prints the per-channel
// pixel delta and, more importantly, both paths' measured detection numbers side by side.
//
// The three that are actually gated are fill, aspect and median hue. Run this on a **loading screen**
// - on gameplay the mask numbers are meaningless because there is no mask.
//
//   csc /out:tools\CompareResize.exe tools\CompareResize.cs VideoCaptureDevice.cs ImageCapture.cs
//       DLLImportStuff.cs LoadDetector.cs FeatureDetector.cs MaskDetector.cs
//       /r:System.Drawing.dll /r:System.Windows.Forms.dll
//
//   CompareResize.exe <deviceIndex> [blacklevel] [dumpDir]
static class CompareResize
{
    static int Main(string[] args)
    {
        List<VideoCaptureDeviceInfo> devices = VideoCaptureDevices.Enumerate();
        int index = args.Length > 0 ? int.Parse(args[0]) : 0;
        int blackLevel = args.Length > 1 ? int.Parse(args[1]) : 0;
        string dumpDir = args.Length > 2 ? args[2] : null;

        if (index < 0 || index >= devices.Count)
        {
            Console.Error.WriteLine("bad device index");
            return 2;
        }

        Console.WriteLine("Device: " + devices[index].Name);

        using (var source = new VideoCaptureSource(devices[index].MonikerName))
        {
            Bitmap full = null;
            for (int i = 0; i < 250 && full == null; i++)
            {
                Thread.Sleep(100);
                full = source.CloneLatestFrame();
            }

            if (full == null)
            {
                Console.Error.WriteLine("no frames: " + source.Status);
                return 1;
            }

            Console.WriteLine("Format: " + source.Status);
            Console.WriteLine();

            Rectangle whole = new Rectangle(0, 0, full.Width, full.Height);

            Bitmap a, b;
            using (full)
            {
                a = ImageCapture.ResizeImage(full, 300, 300);
            }

            b = source.CaptureScaled(whole, 300, 300);
            if (b == null)
            {
                Console.Error.WriteLine("scaled capture returned nothing");
                return 1;
            }

            using (a)
            using (b)
            {
                ReportPixelDelta(a, b);
                Console.WriteLine();

                Calibration calibration = default(Calibration);
                calibration.BlackLevel = blackLevel;
                calibration.HasCalibration = true;

                DetectionResult ra = LoadDetector.Detect(new FramePixels(a), calibration);
                DetectionResult rb = LoadDetector.Detect(new FramePixels(b), calibration);

                Console.WriteLine("A  GDI+ bicubic : " + ra.Describe().Replace("\r\n", "\r\n                  "));
                Console.WriteLine();
                Console.WriteLine("B  area average : " + rb.Describe().Replace("\r\n", "\r\n                  "));
                Console.WriteLine();

                Console.WriteLine("Gated quantities (A -> B, delta):");
                Delta("black level", ra.FrameBlackLevel, rb.FrameBlackLevel, 2);
                if (ra.Mask.HasCrop && rb.Mask.HasCrop)
                {
                    Delta("fill       ", ra.Mask.Fill, rb.Mask.Fill, 0.02);
                    Delta("aspect     ", ra.Mask.AspectRatio, rb.Mask.AspectRatio, 0.02);
                    Delta("median hue ", ra.Mask.MedianHue, rb.Mask.MedianHue, 1.0);
                }
                else
                {
                    Console.WriteLine("  no mask in this frame - run this on a loading screen to " +
                                      "compare fill/aspect/hue");
                }

                if (dumpDir != null)
                {
                    System.IO.Directory.CreateDirectory(dumpDir);
                    a.Save(System.IO.Path.Combine(dumpDir, "a_bicubic.png"), ImageFormat.Png);
                    b.Save(System.IO.Path.Combine(dumpDir, "b_area.png"), ImageFormat.Png);
                    Console.WriteLine();
                    Console.WriteLine("wrote a_bicubic.png / b_area.png to " + dumpDir);
                }
            }
        }

        return 0;
    }

    static void Delta(string name, double a, double b, double tolerance)
    {
        double d = Math.Abs(a - b);
        Console.WriteLine("  " + name + " " + a.ToString("0.000").PadLeft(9) +
                          " -> " + b.ToString("0.000").PadLeft(9) +
                          "   delta " + d.ToString("0.000").PadLeft(7) +
                          (d <= tolerance ? "   ok" : "   OVER TOLERANCE (" + tolerance + ")"));
    }

    static void ReportPixelDelta(Bitmap a, Bitmap b)
    {
        long total = 0;
        int max = 0;
        int count = 0;

        var pa = new FramePixels(a);
        var pb = new FramePixels(b);

        for (int y = 0; y < 300; y++)
        {
            for (int x = 0; x < 300; x++)
            {
                int[] da =
                {
                    Math.Abs(pa.BlueAt(x, y) - pb.BlueAt(x, y)),
                    Math.Abs(pa.GreenAt(x, y) - pb.GreenAt(x, y)),
                    Math.Abs(pa.RedAt(x, y) - pb.RedAt(x, y))
                };

                foreach (int d in da)
                {
                    total += d;
                    if (d > max) max = d;
                    count++;
                }
            }
        }

        Console.WriteLine("Pixel difference A vs B: mean " + ((double)total / count).ToString("0.00") +
                          ", max " + max + " (per channel, 0-255)");
    }
}
