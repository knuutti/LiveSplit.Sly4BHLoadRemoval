using System;
using System.Drawing;

namespace Sly4BHLoadDetector
{
    // Everything calibration produces: the capture's black level, and nothing else.
    //
    // The mask is no longer calibrated at all - it is looked for in a fixed region and judged by
    // measured properties, so a run only has to establish where this capture's black bottoms out.
    public struct Calibration
    {
        public int BlackLevel;
        public bool HasCalibration;

        public override string ToString()
        {
            return HasCalibration ? "black level " + BlackLevel : "uncalibrated";
        }
    }

    // Which check rejected a frame, or Accepted if none did. Named rather than boolean because the
    // failures mean very different things: a bright reference patch is ordinary gameplay, an empty
    // region is a black screen with nothing on it, and a box that measures wrong is the interesting
    // case worth looking at.
    public enum DetectionStage
    {
        Accepted,
        NotCalibrated,
        BlackPatch,
        NoForeground,
        DegenerateBox,
        Fill,
        AspectRatio,
        Hue
    }

    // Everything one frame's detection pass measured. Carries the numbers as well as the verdict so
    // the settings label, the detection log and the test runner all report the same thing without any
    // of them recomputing it.
    public struct DetectionResult
    {
        public bool IsLoading;
        public DetectionStage RejectedAt;

        public int FrameBlackLevel;
        public int AllowedBlackLevel;
        public int CalibratedBlackLevel;
        public int BinarizationThreshold;

        public MaskMetrics Mask;

        // Which of the two loading screens this is: an area load shows the statistics along the
        // bottom, a plain load shows the mask alone.
        //
        // Reported, never gated on. Both kinds are loads and both must pause the timer; this exists
        // only so the autosplitter can count the area ones. See MaskDetector.StatsRegion.
        public float StatsFill;
        public bool HasStats;

        public string LoadTypeInfo
        {
            get
            {
                return (HasStats ? "area load" : "plain load") +
                       " (stats fill " + StatsFill.ToString("0.000") +
                       ", area needs >= " + MaskDetector.MinStatsFill.ToString("0.00") + ")";
            }
        }

        public string BlackInfo
        {
            get
            {
                return "black patch max=" + FrameBlackLevel + " (allowed <= " + AllowedBlackLevel +
                       ", calibrated " + CalibratedBlackLevel + ")" +
                       ", binarized at >" + BinarizationThreshold;
            }
        }

        public string MaskInfo
        {
            get { return "mask: " + Mask; }
        }

        // Names the gate that rejected the frame together with what it measured against what it
        // allows, so a log line explains itself without the reader having to know the constants.
        public string GateInfo
        {
            get
            {
                switch (RejectedAt)
                {
                    case DetectionStage.Fill:
                        return "fill " + Mask.Fill.ToString("0.000") + " outside " +
                               MaskGates.MinFill.ToString("0.00") + "-" + MaskGates.MaxFill.ToString("0.00");
                    case DetectionStage.AspectRatio:
                        return "aspect " + Mask.AspectRatio.ToString("0.000") + " outside " +
                               MaskGates.MinAspectRatio.ToString("0.00") + "-" +
                               MaskGates.MaxAspectRatio.ToString("0.00");
                    case DetectionStage.Hue:
                        return "median hue " + Mask.MedianHue.ToString("0.0") + " outside " +
                               MaskGates.MinHue + "-" + MaskGates.MaxHue;
                    default:
                        return "";
                }
            }
        }

        // The full per-frame decision, in the form the settings label and the detection log show it.
        public string Describe()
        {
            switch (RejectedAt)
            {
                case DetectionStage.NotCalibrated:
                    return "Not calibrated";
                case DetectionStage.BlackPatch:
                    return "Not a loading screen: " + BlackInfo;
                case DetectionStage.NoForeground:
                    return "No mask: " + BlackInfo + "\r\n" + MaskInfo;
                case DetectionStage.DegenerateBox:
                    return "No mask: " + BlackInfo + "\r\n" + MaskInfo;
                case DetectionStage.Accepted:
                    return "LOADING, " + LoadTypeInfo + ": " + BlackInfo + "\r\n" + MaskInfo;
                default:
                    return "Not the loading mask (" + GateInfo + "): " + BlackInfo + "\r\n" + MaskInfo;
            }
        }
    }

    // What one frame contributed to a calibration run. Only the black level is calibrated now, so a
    // sample is the frame's reading and the running minimum it fed into.
    public struct CalibrationSample
    {
        public int FrameBlackLevel;
        public int CalibratedBlackLevel;
        public bool Improved;

        // What the mask pipeline made of this frame. Nothing in calibration depends on it - it is
        // shown so the user can see the detector reacting to the load screen while they hold it on
        // screen, which is the only feedback that the crop and the fixed regions line up.
        public MaskMetrics Mask;

        public string Describe()
        {
            return "Black level this frame: " + FrameBlackLevel +
                   " (calibrated minimum so far: " + CalibratedBlackLevel + ")" +
                   "\r\n" + Mask;
        }
    }

    // A calibration in progress: the running minimum of the black patch's maximum.
    //
    // The minimum is the point of it. A frame landing on the minimum is one where the patch really
    // was showing loading-screen backdrop, so the value measures the capture's noise floor rather
    // than whatever happened to be on screen.
    internal sealed class CalibrationRun
    {
        private int blackLevel = -1;
        private int frameCount;

        public int BlackLevel { get { return blackLevel; } }
        public int FrameCount { get { return frameCount; } }
        public bool HasSamples { get { return blackLevel != -1; } }

        // Feeds one frame into the run and reports what it contributed.
        public CalibrationSample Observe(FramePixels frame)
        {
            CalibrationSample sample = default(CalibrationSample);

            sample.FrameBlackLevel = FeatureDetector.GetBlackLevel(frame);
            if (blackLevel == -1 || sample.FrameBlackLevel < blackLevel)
            {
                blackLevel = sample.FrameBlackLevel;
                sample.Improved = true;
            }

            sample.CalibratedBlackLevel = blackLevel;
            sample.Mask = MaskDetector.Measure(frame, MaskDetector.BinarizationThreshold(sample.FrameBlackLevel));

            frameCount++;
            return sample;
        }

        // Commits the run. Fails if it never saw a frame at all, so the caller can leave any previous
        // calibration in place rather than replacing it with nothing.
        public bool TryFinish(out Calibration calibration)
        {
            calibration = default(Calibration);

            if (blackLevel == -1)
            {
                return false;
            }

            calibration.BlackLevel = blackLevel;
            calibration.HasCalibration = true;
            return true;
        }
    }

    // The per-frame loading-screen test. Every gate must pass; the first that fails names itself in
    // the result.
    //
    // Pure with respect to the frame and the calibration, so the test runner drives exactly the code
    // the component runs rather than a reimplementation of it.
    internal static class LoadDetector
    {
        public static DetectionResult Detect(FramePixels frame, Calibration calibration)
        {
            DetectionResult result = default(DetectionResult);
            result.CalibratedBlackLevel = calibration.BlackLevel;
            result.AllowedBlackLevel = calibration.BlackLevel + FeatureDetector.BlackLevelTolerance;

            if (!calibration.HasCalibration)
            {
                result.RejectedAt = DetectionStage.NotCalibrated;
                return result;
            }

            // 1. Is the always-black reference patch actually black? On a gameplay frame it is not,
            //    and this alone rejects the overwhelming majority of frames - which is what keeps the
            //    binarization below safe, since it has no way to tell a mask from scenery.
            result.FrameBlackLevel = FeatureDetector.GetBlackLevel(frame);
            if (result.FrameBlackLevel > result.AllowedBlackLevel)
            {
                result.RejectedAt = DetectionStage.BlackPatch;
                return result;
            }

            // 2. Measure whatever is lit inside the fixed mask region.
            result.BinarizationThreshold = MaskDetector.BinarizationThreshold(result.FrameBlackLevel);
            result.Mask = MaskDetector.Measure(frame, result.BinarizationThreshold);

            // Which loading screen this is. Measured here, after the black patch gate so ordinary
            // gameplay never pays for it, and reported for rejected frames too so the log shows the
            // statistics coming up during the transition into a load.
            result.StatsFill = MaskDetector.MeasureStatsFill(frame, result.BinarizationThreshold);
            result.HasStats = MaskDetector.HasStats(result.StatsFill);

            if (!result.Mask.HasForeground)
            {
                result.RejectedAt = DetectionStage.NoForeground;
                return result;
            }

            if (!result.Mask.HasCrop)
            {
                result.RejectedAt = DetectionStage.DegenerateBox;
                return result;
            }

            // 3. Does what is lit measure like the loading mask? All four must hold.
            result.RejectedAt = MaskGates.FirstFailure(result.Mask);
            result.IsLoading = result.RejectedAt == DetectionStage.Accepted;
            return result;
        }
    }

    // The gates every measurement must pass for a frame to count as a loading screen.
    //
    // All five were read off `DetectionTests.exe testdata --measure`, which prints the range each
    // quantity takes over each labelled class. Across both capture sets a settled loading screen is
    // remarkably tight - fill 0.739-0.803, aspect 1.051-1.114, hue 110-112, saturation 146-155, value
    // 79-83, over 89 frames from two different capture pipelines - so the gates below are placed
    // around that cluster with the widest margin each measurement allows.
    //
    // What has to be rejected is not gameplay (the black patch check disposes of that) but the
    // gameplay-to-load transition, where the mask animates into place over a fading background. Those
    // frames form a near-continuum, and only five of them clear fill and aspect at all:
    //
    //     f00504   fill 0.707  aspect 1.043  hsv (116.0, 145, 71.5)
    //     f00954   fill 0.676  aspect 1.043  hsv (116.0, 146, 73.0)
    //     f00930   fill 0.701  aspect 1.047  hsv ( 63.0, 169, 90.0)
    //     f00480   fill 0.695  aspect 1.023  hsv ( 96.5, 183, 96.0)
    //     13_19_08 fill 0.686  aspect 1.023  hsv ( 61.0, 184, 97.0)
    //
    // Every one is wrong on colour, and on more than one channel. That redundancy is the point: no
    // single gate here is threading a narrow gap on its own, which is the failure mode that broke
    // this detector before (see the surround-band note in CLAUDE.md).
    //
    // Mutable so tests\DetectionTests.cs can vary them while searching for a setting; nothing at run
    // time writes them.
    internal static class MaskGates
    {
        // Share of the bounding box that is foreground. A settled mask fills three quarters of its own
        // box; a mask still fading in sits inside a box blown out by the background it has not
        // finished covering, so this drops away sharply.
        //
        // The floor is well below the loading range on purpose. The transition frames run continuously
        // from 0.125 up to 0.707, so wherever it is put it lands close to one of them - and putting it
        // at 0.72 to just clear f00504 would leave genuine loads only 0.019 of headroom. At 0.60 the
        // loading class has 0.139, the nearest rejected frame has 0.013, and the frames that slip
        // through are dealt with on colour instead. A false positive costs a few frames of early
        // pause; a false negative costs the whole load.
        public static float MinFill = 0.60f;
        public static float MaxFill = 0.90f;

        // Width over height. The mask is measurably wider than tall through its whole pulse.
        public static float MinAspectRatio = 1.00f;
        public static float MaxAspectRatio = 1.20f;

        // Median hue over the bounding box, on OpenCV's 0-179 scale - so 110 is the blue the mask is
        // rendered in. This is the single most stable thing about a loading screen: 110-112 across
        // every frame of both capture sets, and it rejects all five of the frames listed above.
        //
        // There is deliberately no gate on median saturation or median value, though both are
        // measured and logged. Each was tried and each rejected *nothing*: with either or both opened
        // right up, every frame in both sets still classifies correctly and the debounced pause lands
        // on the same frame. They looked like useful redundancy on f00504/f00954, but those two are
        // the mask part-way through fading in, so hue (116 -> 112) and value (72 -> 80) are moving
        // together along one trajectory rather than saying independent things.
        //
        // Redundant gates are not free. Removing a gate can only ever cause a false positive - a few
        // frames of early pause - while every gate kept is another way to lose a whole load to a
        // loading screen that renders slightly off from these 89 frames. A value floor of 76 against
        // a measured minimum of 79 is exactly that kind of hostage. If a false positive ever does
        // turn up, the medians are in the detection log already, so a gate can be added back with
        // evidence instead of on principle.
        public static int MinHue = 104;
        public static int MaxHue = 114;

        // Returns the first gate that rejects these metrics, or Accepted. Ordered cheapest-to-explain
        // first: geometry before colour, so a log line names the structural problem when there is one.
        public static DetectionStage FirstFailure(MaskMetrics mask)
        {
            if (mask.Fill < MinFill || mask.Fill > MaxFill)
            {
                return DetectionStage.Fill;
            }

            if (mask.AspectRatio < MinAspectRatio || mask.AspectRatio > MaxAspectRatio)
            {
                return DetectionStage.AspectRatio;
            }

            if (mask.MedianHue < MinHue || mask.MedianHue > MaxHue)
            {
                return DetectionStage.Hue;
            }

            return DetectionStage.Accepted;
        }
    }
}
