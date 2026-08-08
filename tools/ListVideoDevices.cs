using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Threading;
using Sly4BHLoadDetector;

// Standalone check of the DirectShow path in VideoCaptureDevice.cs, without LiveSplit in the way.
//
// This is the first thing to run when a user reports "my capture card isn't in the dropdown" or "the
// preview is black": it says whether Windows lists the device at all, whether a graph can be built on
// it, and what resolution it negotiates - which is the whole of what the component needs from it.
//
//   csc /out:tools\ListVideoDevices.exe tools\ListVideoDevices.cs VideoCaptureDevice.cs
//       /r:System.Drawing.dll
//
//   ListVideoDevices.exe            list devices only
//   ListVideoDevices.exe <index>    also open that device and grab a frame
//   ListVideoDevices.exe <index> out.png   ...and save it
static class ListVideoDevices
{
    static int Main(string[] args)
    {
        List<VideoCaptureDeviceInfo> devices = VideoCaptureDevices.Enumerate();

        Console.WriteLine(devices.Count + " video capture device(s):");
        for (int i = 0; i < devices.Count; i++)
        {
            Console.WriteLine("  [" + i + "] " + devices[i].Name);
            Console.WriteLine("      " + devices[i].MonikerName);
        }

        if (devices.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Nothing found. Either no device is attached, or Windows' camera privacy");
            Console.WriteLine("setting is blocking desktop apps (Settings -> Privacy -> Camera).");
            return 0;
        }

        if (args.Length == 0)
        {
            return 0;
        }

        int index;
        if (!int.TryParse(args[0], out index) || index < 0 || index >= devices.Count)
        {
            Console.Error.WriteLine("bad device index");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine("Opening [" + index + "] " + devices[index].Name + " ...");

        using (var source = new VideoCaptureSource(devices[index].MonikerName))
        {
            // Long enough for the whole fallback chain: a capture card takes a second or two to lock
            // onto its signal, and each pixel format that connects but stays silent costs another
            // couple of seconds before the next one is tried.
            System.Drawing.Bitmap frame = null;
            string lastStatus = null;

            for (int attempt = 0; attempt < 250 && frame == null; attempt++)
            {
                Thread.Sleep(100);
                frame = source.CloneLatestFrame();

                if (source.Status != lastStatus)
                {
                    lastStatus = source.Status;
                    Console.WriteLine("  status: " + lastStatus);
                }
            }

            Console.WriteLine("  status: " + source.Status);

            if (frame == null)
            {
                Console.WriteLine("  no frame after 25s");
                return 1;
            }

            using (frame)
            {
                Console.WriteLine("  got a frame: " + frame.Width + "x" + frame.Height);
                if (args.Length > 1)
                {
                    frame.Save(args[1], ImageFormat.Png);
                    Console.WriteLine("  saved to " + args[1]);
                }
            }
        }

        return 0;
    }
}
