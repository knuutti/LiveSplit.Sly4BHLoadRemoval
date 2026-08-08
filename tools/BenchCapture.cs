using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using Sly4BHLoadDetector;

// Times the whole per-frame path against a real capture device, stage by stage.
//
// The point is to find out where the wall-clock between "the load screen appeared" and "the timer
// paused" actually goes. That latency is the sum of three very different things - the device's own
// pipeline, the per-frame work here, and the debounce (which costs frames, so it is only as fast as
// the update rate this measures) - and they are not fixable by the same means.
//
//   csc /out:tools\BenchCapture.exe tools\BenchCapture.cs VideoCaptureDevice.cs ImageCapture.cs
//       DLLImportStuff.cs LoadDetector.cs FeatureDetector.cs MaskDetector.cs
//       /r:System.Drawing.dll /r:System.Windows.Forms.dll
//
//   BenchCapture.exe <deviceIndex> [seconds]
static class BenchCapture
{
    static int Main(string[] args)
    {
        List<VideoCaptureDeviceInfo> devices = VideoCaptureDevices.Enumerate();
        int index = args.Length > 0 ? int.Parse(args[0]) : 0;
        int seconds = args.Length > 1 ? int.Parse(args[1]) : 5;

        if (index < 0 || index >= devices.Count)
        {
            Console.Error.WriteLine("bad device index");
            return 2;
        }

        Console.WriteLine("Device: " + devices[index].Name);

        using (var source = new VideoCaptureSource(devices[index].MonikerName))
        {
            Bitmap first = null;
            for (int i = 0; i < 250 && first == null; i++)
            {
                Thread.Sleep(100);
                first = source.CloneLatestFrame();
            }

            if (first == null)
            {
                Console.Error.WriteLine("no frames: " + source.Status);
                return 1;
            }

            int width = first.Width, height = first.Height;
            first.Dispose();
            Console.WriteLine("Format: " + source.Status);
            Console.WriteLine();

            // The whole game feed, which is what the README tells users to crop to. Cropping tighter
            // would make the numbers look better than a real setup does.
            Rectangle crop = new Rectangle(0, 0, width, height);

            var grab = new Stopwatch();
            var resize = new Stopwatch();
            var detect = new Stopwatch();
            var total = Stopwatch.StartNew();

            Calibration calibration = default(Calibration);
            calibration.BlackLevel = 0;
            calibration.HasCalibration = true;

            int frames = 0;
            while (total.Elapsed.TotalSeconds < seconds)
            {
                // Exactly what ComponentSettings.CaptureFromVideoDevice does per frame.
                grab.Start();
                Bitmap region = source.CaptureRegion(crop);
                grab.Stop();

                if (region == null) continue;

                resize.Start();
                Bitmap small = ImageCapture.ResizeImage(region, 300, 300);
                resize.Stop();

                detect.Start();
                LoadDetector.Detect(new FramePixels(small), calibration);
                detect.Stop();

                small.Dispose();
                region.Dispose();
                frames++;
            }

            total.Stop();

            Console.WriteLine("frames processed : " + frames +
                              " in " + total.Elapsed.TotalSeconds.ToString("0.0") + "s" +
                              "  -> " + (frames / total.Elapsed.TotalSeconds).ToString("0.0") + "/s");
            Console.WriteLine();
            Report("CaptureRegion   ", grab, frames);
            Report("ResizeImage     ", resize, frames);
            Report("Detect          ", detect, frames);

            double perFrame = total.Elapsed.TotalMilliseconds / Math.Max(1, frames);
            Console.WriteLine();
            Console.WriteLine("per frame        : " + perFrame.ToString("0.00") + " ms");
            Console.WriteLine("debounce at 3    : " + (perFrame * 3).ToString("0") + " ms");
            Console.WriteLine("debounce at 2    : " + (perFrame * 2).ToString("0") + " ms");
            Console.WriteLine("debounce at 1    : " + (perFrame * 1).ToString("0") + " ms");
        }

        return 0;
    }

    static void Report(string name, Stopwatch watch, int frames)
    {
        double ms = watch.Elapsed.TotalMilliseconds / Math.Max(1, frames);
        Console.WriteLine("  " + name + " " + ms.ToString("0.00").PadLeft(7) + " ms/frame");
    }
}
