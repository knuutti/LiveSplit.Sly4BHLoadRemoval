using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Sly4BHLoadDetector;

// End-to-end detection test over labelled 300x300 frames.
//
// Calibrates over <set>\calibrate, then runs detection over <set>\loading (must all detect) and
// <set>\notloading (must none detect). <set>\ambiguous is reported but never fails - those are the
// frames either side of a load boundary, where a frame or two of disagreement is meaningless.
//
// This drives LoadDetector/CalibrationRun, the same code the LiveSplit component calls, so a
// threshold change can be evaluated here without building or deploying the DLL. What it deliberately
// does not cover is the capture path and the LiveSplit wiring - the fixtures start life already
// cropped and resized, so a bug in ImageCapture is invisible here.
//
//   csc /out:tests\DetectionTests.exe tests\DetectionTests.cs
//       LoadDetector.cs FeatureDetector.cs MaskDetector.cs /r:System.Drawing.dll
//
//   DetectionTests.exe [testdataRoot] [--verbose] [--dump] [--measure]
static class DetectionTests
{
    static bool verbose;
    static bool dump;
    static bool measure;
    static string dumpDir;

    struct Frame
    {
        public string Path;
        public string Name;
        public int Number;
    }

    static int Main(string[] args)
    {
        string root = "testdata";
        foreach (string a in args)
        {
            if (a == "--verbose") verbose = true;
            else if (a == "--dump") dump = true;
            else if (a == "--measure") measure = true;
            else root = a;
        }

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine("no testdata at " + Path.GetFullPath(root));
            return 2;
        }

        dumpDir = Path.Combine(root, "_dump");

        // Each subdirectory holding a calibrate\ folder is an independent set: one capture pipeline,
        // calibrated and tested on its own. Keeping them separate is the point - a threshold tuned on
        // one capture can easily break another, and a single merged set would hide that by averaging.
        var sets = new List<string>();
        foreach (string d in Directory.GetDirectories(root))
        {
            if (Directory.Exists(Path.Combine(d, "calibrate"))) sets.Add(d);
        }
        sets.Sort(StringComparer.Ordinal);

        if (sets.Count == 0)
        {
            sets.Add(root);   // a bare root with calibrate\ directly inside it
        }

        if (measure)
        {
            foreach (string set in sets)
            {
                Console.WriteLine("=== " + Path.GetFileName(set) + " ===");
                MeasureSet(set);
                Console.WriteLine();
            }
            return 0;
        }

        int failures = 0;
        foreach (string set in sets)
        {
            Console.WriteLine("=== " + Path.GetFileName(set) + " ===");
            failures += RunSet(set);
            Console.WriteLine();
        }

        Console.WriteLine(failures == 0 ? "PASS" : "FAIL - " + failures + " frame(s) misclassified");
        return failures == 0 ? 0 : 1;
    }

    static int RunSet(string root)
    {
        Calibration calibration;
        if (!Calibrate(Path.Combine(root, "calibrate"), out calibration))
        {
            return 1;
        }

        int failures = 0;
        failures += Check(Path.Combine(root, "loading"), calibration, expectLoading: true, fatal: true);
        failures += Check(Path.Combine(root, "notloading"), calibration, expectLoading: false, fatal: true);
        Check(Path.Combine(root, "ambiguous"), calibration, expectLoading: true, fatal: false);

        Sequence(Path.Combine(root, "sequence"), calibration);
        return failures;
    }

    // ---------------------------------------------------------------- measurement

    // Prints the range each measured quantity takes over each labelled class, which is how the gates
    // in MaskGates were chosen. A gate is only worth having where the loading range and the
    // notloading range do not overlap, and the printed spread says by how much - a gate threaded
    // through a one-unit gap is fitted to these frames rather than to the game.
    class Spread
    {
        public string Name;
        public int Count;
        public List<double> Values = new List<double>();

        public void Add(double v) { Values.Add(v); Count++; }

        public double Min { get { double m = double.MaxValue; foreach (double v in Values) if (v < m) m = v; return m; } }
        public double Max { get { double m = double.MinValue; foreach (double v in Values) if (v > m) m = v; return m; } }

        public override string ToString()
        {
            if (Count == 0) return "(none)";
            return Min.ToString("0.000") + " .. " + Max.ToString("0.000");
        }
    }

    static void MeasureSet(string root)
    {
        Calibration calibration;
        if (!Calibrate(Path.Combine(root, "calibrate"), out calibration))
        {
            return;
        }

        foreach (string label in new[] { "loading", "notloading", "ambiguous", "calibrate" })
        {
            string dir = Path.Combine(root, label);
            List<Frame> frames = Load(dir);
            if (frames.Count == 0) continue;

            var black = new Spread();
            var fill = new Spread();
            var aspect = new Spread();
            var hue = new Spread();
            var sat = new Spread();
            var val = new Spread();
            var litHue = new Spread();
            var litSat = new Spread();
            var litVal = new Spread();
            int noForeground = 0, degenerate = 0, blackPatchRejected = 0;

            foreach (Frame f in frames)
            {
                using (Bitmap bmp = new Bitmap(f.Path))
                {
                    FramePixels pixels = new FramePixels(bmp);
                    int level = FeatureDetector.GetBlackLevel(pixels);
                    black.Add(level);

                    if (level > calibration.BlackLevel + FeatureDetector.BlackLevelTolerance)
                    {
                        blackPatchRejected++;
                        continue;
                    }

                    MaskMetrics m = MaskDetector.Measure(pixels, MaskDetector.BinarizationThreshold(level));
                    if (!m.HasForeground) { noForeground++; continue; }
                    if (!m.HasCrop) { degenerate++; continue; }

                    fill.Add(m.Fill);
                    aspect.Add(m.AspectRatio);
                    hue.Add(m.MedianHue);
                    sat.Add(m.MedianSaturation);
                    val.Add(m.MedianValue);
                    litHue.Add(m.LitMedianHue);
                    litSat.Add(m.LitMedianSaturation);
                    litVal.Add(m.LitMedianValue);

                    if (verbose)
                    {
                        Console.WriteLine("    [" + label + "] " + f.Name + "  blk=" + level + "  " + m);
                    }
                }
            }

            Console.WriteLine("  " + label + " (" + frames.Count + " frames)");
            Console.WriteLine("    black patch     " + black);
            Console.WriteLine("    rejected early  black-patch=" + blackPatchRejected +
                              " no-foreground=" + noForeground + " degenerate=" + degenerate);
            Console.WriteLine("    reached gates   " + fill.Count);
            if (fill.Count == 0) continue;
            Console.WriteLine("    fill            " + fill);
            Console.WriteLine("    aspect          " + aspect);
            Console.WriteLine("    median hue      " + hue);
            Console.WriteLine("    median sat      " + sat);
            Console.WriteLine("    median value    " + val);
            Console.WriteLine("    lit median hue  " + litHue);
            Console.WriteLine("    lit median sat  " + litSat);
            Console.WriteLine("    lit median val  " + litVal);
        }
    }

    // ---------------------------------------------------------------- calibration

    static List<Frame> Load(string dir)
    {
        var frames = new List<Frame>();
        if (!Directory.Exists(dir))
        {
            return frames;
        }

        string[] files = Directory.GetFiles(dir, "*.png");
        Array.Sort(files, StringComparer.Ordinal);

        foreach (string f in files)
        {
            Frame frame = new Frame();
            frame.Path = f;
            frame.Name = Path.GetFileNameWithoutExtension(f);
            int n;
            frame.Number = int.TryParse(frame.Name.TrimStart('f'), out n) ? n : 0;
            frames.Add(frame);
        }

        return frames;
    }

    // A set may pin its black level instead of deriving one, via a calibration.txt holding a
    // `blacklevel=N` line copied out of a real layout's saved settings.
    //
    // This matters more than it looks. Deriving a black level from a handful of frames does not
    // reproduce one accumulated over a full calibration run: a short set lands on a lower value, and
    // the binarization threshold is measured from it, so the whole detector shifts. A frame that a
    // real installation accepts can be rejected by the test and vice versa.
    static bool TryLoadPinnedCalibration(string setRoot, out Calibration calibration)
    {
        calibration = default(Calibration);
        string path = Path.Combine(setRoot, "calibration.txt");
        if (!File.Exists(path))
        {
            return false;
        }

        bool found = false;
        foreach (string line in File.ReadAllLines(path))
        {
            string s = line.Trim();
            if (s.Length == 0 || s.StartsWith("#")) continue;

            int eq = s.IndexOf('=');
            if (eq < 0) continue;

            string key = s.Substring(0, eq).Trim().ToLowerInvariant();
            if (key != "blacklevel") continue;      // mask-box keys from the old format are ignored

            calibration.BlackLevel = int.Parse(s.Substring(eq + 1).Trim());
            found = true;
        }

        calibration.HasCalibration = found;
        return found;
    }

    static bool Calibrate(string dir, out Calibration calibration)
    {
        string setRoot = Path.GetDirectoryName(dir);
        if (TryLoadPinnedCalibration(setRoot, out calibration))
        {
            Console.WriteLine("Pinned calibration from " + Path.GetFileName(setRoot) + "\\calibration.txt");
            Console.WriteLine("  -> " + calibration);
            Console.WriteLine();
            return true;
        }

        calibration = default(Calibration);
        List<Frame> frames = Load(dir);

        if (frames.Count == 0)
        {
            Console.Error.WriteLine("no frames in " + dir);
            return false;
        }

        var run = new CalibrationRun();
        foreach (Frame f in frames)
        {
            using (Bitmap bmp = new Bitmap(f.Path))
            {
                CalibrationSample sample = run.Observe(new FramePixels(bmp));
                if (verbose)
                {
                    Console.WriteLine("  " + f.Name + "  " + sample.Describe().Replace("\r\n", "  "));
                }
            }
        }

        Console.WriteLine("Calibration over " + dir + " (" + frames.Count + " frames)");

        if (!run.TryFinish(out calibration))
        {
            Console.Error.WriteLine("  CALIBRATION FAILED - no frames observed");
            return false;
        }

        Console.WriteLine("  -> " + calibration);
        Console.WriteLine();
        return true;
    }

    // ---------------------------------------------------------------- checks

    static int Check(string dir, Calibration calibration, bool expectLoading, bool fatal)
    {
        List<Frame> frames = Load(dir);
        if (frames.Count == 0)
        {
            return 0;
        }

        int passed = 0;
        var failed = new List<string>();
        var stageCounts = new SortedDictionary<string, int>();

        foreach (Frame f in frames)
        {
            using (Bitmap bmp = new Bitmap(f.Path))
            {
                FramePixels pixels = new FramePixels(bmp);
                DetectionResult result = LoadDetector.Detect(pixels, calibration);

                int c;
                stageCounts.TryGetValue(result.RejectedAt.ToString(), out c);
                stageCounts[result.RejectedAt.ToString()] = c + 1;

                if (result.IsLoading == expectLoading)
                {
                    passed++;
                }
                else
                {
                    failed.Add(f.Name + "\r\n    " + result.Describe().Replace("\r\n", "\r\n    "));
                    if (dump) Dump(bmp, pixels, result, f.Name);
                }

                if (verbose)
                {
                    Console.WriteLine("  " + f.Name + "  " + result.Describe().Replace("\r\n", "  "));
                }
            }
        }

        string label = Path.GetFileName(dir);
        Console.WriteLine(label.PadRight(12) + passed + "/" + frames.Count + " as expected" +
                          (fatal ? "" : "   (advisory - not failed)"));
        foreach (var kv in stageCounts)
        {
            Console.WriteLine("    " + kv.Value + "x " + kv.Key);
        }

        foreach (string f in failed)
        {
            Console.WriteLine((fatal ? "  MISCLASSIFIED " : "  differs ") + f);
        }
        Console.WriteLine();

        return fatal ? failed.Count : 0;
    }

    // Replays contiguous frames through the same consecutive-agreement debounce the component uses,
    // and reports where the timer would actually pause and resume. The raw per-frame verdict is not
    // what the timer sees; this is, and the gap between the two is time added to or removed from the
    // run. Requires contiguous frames at the source frame rate - a subsampled folder would overstate
    // the lag by the sampling stride.
    static void Sequence(string dir, Calibration calibration)
    {
        List<Frame> frames = Load(dir);
        if (frames.Count < 2)
        {
            return;
        }

        // Decode once - the tolerance sweep below replays the same verdicts many times over.
        var numbers = new List<int>();
        var raws = new List<bool>();
        foreach (Frame f in frames)
        {
            using (Bitmap bmp = new Bitmap(f.Path))
            {
                numbers.Add(f.Number);
                raws.Add(LoadDetector.Detect(new FramePixels(bmp), calibration).IsLoading);
            }
        }

        Console.WriteLine("Debounced sequence over " + Path.GetFileName(dir) +
                          " (" + frames.Count + " contiguous frames)");

        // How jittery the raw signal is decides how much debounce is actually needed. A signal that
        // never flips spuriously needs almost none, and every frame of tolerance is latency paid at
        // both ends of every load.
        int flips = 0;
        for (int i = 1; i < raws.Count; i++)
        {
            if (numbers[i] == numbers[i - 1] + 1 && raws[i] != raws[i - 1]) flips++;
        }
        Console.WriteLine("  raw verdict changes " + flips + "x across the sequence" +
                          " (a clean signal changes once per boundary)");

        foreach (int tolerance in new int[] { 1, 2, 3, 4, 6, 8 })
        {
            bool state = false, candidate = false;
            int agreement = 0;
            var events = new List<string>();

            for (int i = 0; i < raws.Count; i++)
            {
                if (i == 0 || numbers[i] != numbers[i - 1] + 1)
                {
                    state = false; candidate = false; agreement = 0;
                }

                if (raws[i] != candidate) { candidate = raws[i]; agreement = 1; }
                else agreement++;

                if (candidate != state && agreement >= tolerance)
                {
                    state = candidate;
                    events.Add((state ? "pause f" : "resume f") + numbers[i]);
                }
            }

            Console.WriteLine("  tolerance " + tolerance.ToString().PadLeft(2) +
                              " (" + (tolerance / 60.0 * 1000).ToString("0").PadLeft(3) + " ms at 60fps): " +
                              string.Join(", ", events.ToArray()));
        }
    }

    // Writes what the detector saw for a failing frame: the frame itself, and the binarized,
    // median-blurred foreground with the mask region outlined. A wrong bounding box is much easier to
    // understand looking at than reasoning about from numbers.
    static void Dump(Bitmap source, FramePixels pixels, DetectionResult result, string name)
    {
        Directory.CreateDirectory(dumpDir);
        source.Save(Path.Combine(dumpDir, name + ".png"), ImageFormat.Png);

        using (Bitmap mask = new Bitmap(source.Width, source.Height))
        {
            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    bool lit = pixels.GrayAt(x, y) > result.BinarizationThreshold;
                    mask.SetPixel(x, y, lit ? Color.White : Color.Black);
                }
            }

            using (Graphics g = Graphics.FromImage(mask))
            {
                using (Pen pen = new Pen(Color.Red))
                {
                    Rectangle region = MaskDetector.MaskRegion;
                    g.DrawRectangle(pen, region.Left, region.Top, region.Width - 1, region.Height - 1);
                }

                if (result.Mask.HasForeground)
                {
                    using (Pen pen = new Pen(Color.Lime))
                    {
                        Rectangle box = result.Mask.Bounds.ToRectangle();
                        g.DrawRectangle(pen, box.Left, box.Top, box.Width - 1, box.Height - 1);
                    }
                }
            }

            mask.Save(Path.Combine(dumpDir, name + "_foreground.png"), ImageFormat.Png);
        }
    }
}
