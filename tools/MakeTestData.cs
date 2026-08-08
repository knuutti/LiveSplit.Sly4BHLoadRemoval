using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Sly4BHLoadDetector;

// Turns full-resolution frames extracted from a gameplay recording into the 300x300 images the
// detector actually sees, and files them into the labelled testdata folders.
//
// The downscale goes through ImageCapture.ResizeImage - the same code the live capture path uses -
// rather than through ffmpeg's scaler. That matters: the black-level gates measure the capture's
// noise floor, and a different resampler produces a different noise floor, so fixtures built any
// other way would be testing pixels the component never produces.
//
// Assumes the source frames are the whole 16:9 game feed, i.e. a perfect crop. Real crops are drawn
// by hand and clip some edges, which is what --clip simulates.
//
//   csc /out:tools\MakeTestData.exe tools\MakeTestData.cs ImageCapture.cs DLLImportStuff.cs
//       /r:System.Drawing.dll /r:System.Windows.Forms.dll
//
//   MakeTestData.exe <framesRoot> <testdataRoot> [--clip <percent>]
static class MakeTestData
{
    const int Size = 300;

    // Source group -> destination folder. Frame numbers are recovered from the extraction step's
    // ordering: files come out sequentially numbered, so the Nth file is startFrame + N*step.
    struct Group
    {
        public string SourceDir;
        public string DestDir;
        public int StartFrame;
        public int Step;

        public Group(string sourceDir, string destDir, int startFrame, int step)
        {
            SourceDir = sourceDir; DestDir = destDir; StartFrame = startFrame; Step = step;
        }
    }

    static readonly Group[] Groups = new Group[]
    {
        new Group("calib2_0",   "calibrate",  960, 4),
        new Group("loadA2_0",   "loading",    510, 2),
        new Group("loadB2_0",   "loading",    962, 4),
        new Group("ambig_0",    "ambiguous",  507, 1),
        new Group("ambig_1",    "ambiguous",  560, 1),
        new Group("ambig_2",    "ambiguous",  957, 1),
        new Group("ambig_3",    "ambiguous", 1205, 1),
        new Group("transit_0",  "notloading", 480, 3),
        new Group("transit_1",  "notloading", 563, 3),
        new Group("transit_2",  "notloading", 930, 3),
        new Group("transit_3",  "notloading",1208, 3),
        new Group("gameplay_0", "notloading",   0, 20),
        new Group("gameplay_1", "notloading", 591, 20),
        new Group("gameplay_2", "notloading",1236, 20),

        // Contiguous, every frame, spanning each end of the long load. The debounce is a
        // consecutive-frame count, so measuring how late the timer pauses only means anything at the
        // real frame rate - a subsampled folder would overstate the lag by the sampling stride.
        new Group("seqstart_0", "sequence",   940, 1),
        new Group("seqend_0",   "sequence",  1180, 1),
    };

    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: MakeTestData <framesRoot> <testdataRoot> [--clip <percent>]");
            return 2;
        }

        string framesRoot = args[0];
        string testdataRoot = args[1];
        float clipPercent = 0f;
        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--clip") clipPercent = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
        }

        string suffix = clipPercent > 0f ? "_clip" + ((int)clipPercent) : "";
        var counts = new Dictionary<string, int>();

        foreach (Group g in Groups)
        {
            string src = Path.Combine(framesRoot, g.SourceDir);
            if (!Directory.Exists(src))
            {
                Console.Error.WriteLine("missing source group: " + src);
                return 1;
            }

            // Everything this tool produces belongs to the "recording" set - frames extracted from an
            // OBS file. Other capture pipelines get their own sibling set; see tests\DetectionTests.cs.
            string dest = Path.Combine(testdataRoot, "recording", g.DestDir + suffix);
            Directory.CreateDirectory(dest);

            string[] files = Directory.GetFiles(src, "*.png");
            Array.Sort(files, StringComparer.Ordinal);

            for (int i = 0; i < files.Length; i++)
            {
                int frameNumber = g.StartFrame + i * g.Step;
                using (Bitmap source = new Bitmap(files[i]))
                using (Bitmap cropped = Clip(source, clipPercent))
                using (Bitmap resized = ImageCapture.ResizeImage(cropped, Size, Size))
                {
                    // Frame number in the name, zero padded, so lexical order is temporal order -
                    // the sequence replay depends on that.
                    string name = "f" + frameNumber.ToString("00000") + ".png";
                    resized.Save(Path.Combine(dest, name), ImageFormat.Png);
                }
            }

            int existing;
            counts.TryGetValue(g.DestDir + suffix, out existing);
            counts[g.DestDir + suffix] = existing + files.Length;
        }

        foreach (var kv in counts)
        {
            Console.WriteLine(kv.Key.PadRight(18) + kv.Value + " images");
        }
        return 0;
    }

    // Shaves `percent` off every edge before the resize, simulating a crop the user drew slightly
    // inside the game frame. The detector's regions are absolute pixels in the 300x300, so this is
    // what decides how much hand-drawing error it tolerates.
    static Bitmap Clip(Bitmap source, float percent)
    {
        if (percent <= 0f)
        {
            return (Bitmap)source.Clone();
        }

        int dx = (int)(source.Width * percent / 100f);
        int dy = (int)(source.Height * percent / 100f);
        Rectangle r = Rectangle.FromLTRB(dx, dy, source.Width - dx, source.Height - dy);
        return source.Clone(r, source.PixelFormat);
    }
}
