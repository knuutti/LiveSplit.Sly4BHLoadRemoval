# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A LiveSplit component (classic .NET Framework class library, C#) that automatically detects loading
screens for *Sly Cooper: Thieves in Time* from a screen/window capture, pauses game time during loads,
and can optionally auto-split. It's a fork of `LiveSplit.CrashTWoCLoadRemoval` (itself based on
`thomasneff/LiveSplit.CrashNSTLoadRemoval` and `Maschell/LiveSplit.PokemonRedBlue`): the capture, crop
UI, and autosplitter machinery carried over largely unchanged, but the load *detection* was replaced -
this game's loading screen shows a pulsing raccoon-mask animation against a solid black backdrop rather
than a "LOADING" text string, so OCR was replaced with a black-patch gate followed by a binarize /
median-blur / bounding-box measurement of the mask (no Tesseract dependency). Full detail on the
detection algorithm is in `README.md` ("How does it work?").

## Build

This is an old-style (`ToolsVersion="12.0"`, non-SDK) `.csproj`, built with MSBuild / Visual Studio, not
the `dotnet` CLI. The offline test suites are built separately by `run-tests.cmd`, not by this project -
see "Testing detection changes" below.

```
msbuild LiveSplit.Sly4BHLoadRemoval.csproj /p:Configuration=Release
```

`msbuild` is not on `PATH` from an ordinary shell - use the Developer Command Prompt, or call it by
full path (`C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`,
or locate it with `vswhere -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`).

`TargetFrameworkVersion` is `v4.8.1` - it must match (or exceed) whatever framework the `LiveSplit.Core.dll`
you're building against was itself built for (check via its `TargetFrameworkAttribute`), or MSBuild
refuses to resolve the reference at all (`MSB3274`/`MSB3275`), which then cascades into unrelated-looking
`CS0246`/`CS0234` "type not found" errors for every LiveSplit type. If you hit that, the actual fix is
either bumping `TargetFrameworkVersion` to match, or installing the matching ".NET Framework targeting
pack" via Visual Studio Installer → Modify → Individual Components.

Two external things must be in place for a build to succeed, since the project isn't self-contained:

- **The matching .NET Framework targeting pack** for whatever `TargetFrameworkVersion` is set to (see
  above) - install via Visual Studio Installer if missing.
- **A LiveSplit installation two directories up**: both `<Reference>` `HintPath`s point at
  `..\..\LiveSplit\LiveSplit.Core.dll` and `..\..\LiveSplit\UpdateManager.dll`. That resolves to a real
  LiveSplit install (the same one the built component gets dropped into), *not* a built-from-source
  LiveSplit tree - there is no `bin\Release` layout involved. Both references are `Private=False`, so
  they aren't copied to the output.

**NuGet packages** (`packages.config`, not `PackageReference`) restore into a `packages\` folder *inside
this project directory* - `packages\System.Drawing.Common.5.0.1\lib\net461\System.Drawing.Common.dll`.
The `EnsureNuGetPackageBuildImports` target hard-fails the build if that exact path is missing, so
`nuget restore` must have run (the folder is currently present). The `<Reference>` has
`SpecificVersion=False`, so the assembly's own version doesn't have to match, but the `5.0.1` folder name
in the path does.

`OutputPath` is `bin\{Debug,Release}\Components\` - local to this project, so a build already produces a
correctly-shaped `Components` folder, but it does **not** install anything. Copy
`bin\Release\Components\LiveSplit.Sly4BHLoadRemoval.dll` into `..\..\LiveSplit\Components\` to test it.

Detection logic is covered offline by `run-tests.cmd` (see the next section). What that cannot reach is
the **capture path and the LiveSplit wiring** - those still need testing by hand: build the component,
drop it into a real LiveSplit `Components` folder, and run it against actual gameplay footage (see
`README.md` "How to use").

## Testing detection changes

`run-tests.cmd` builds and runs all three offline suites against `testdata\` - **no LiveSplit, no DLL
deploy**. This is the loop for any change to thresholds or detection logic; only build and install the
DLL once it passes.

- **`tests\GeometryTests.cs`** - pure geometry and arithmetic on synthetic frames: region coordinates,
  inclusive-bounds vs exclusive-crop, the binarization formula, the 5x5 median blur's exact behaviour,
  OpenCV's HSV conversion, numpy's even-count median rule, and the gate boundaries. No fixtures.
- **`tests\DetectionTests.cs`** - end-to-end over labelled 300x300 frames. Every subdirectory of
  `testdata\` holding a `calibrate\` folder is an **independent set**, calibrated and tested on its
  own; within a set it calibrates over `calibrate\`, then requires every frame in `loading\` to detect
  and none in `notloading\` to. `ambiguous\` (frames either side of a load boundary) is reported but
  never fails, and `sequence\` holds *contiguous* frames spanning each end of a load, replayed through
  the debounce - the only way to see how late the timer actually pauses, since a subsampled folder
  overstates that lag by the sampling stride.

  **Keep the sets separate, and add one whenever a new capture pipeline appears.** They exist to catch
  overfitting, which has already happened once for real: thresholds fitted to `recording` (frames from
  an OBS file) rejected *every* frame of `live` (cutouts from a screen capture of the same game),
  because the recording's encoder crushes near-black to zero while a live capture preserves the mask's
  faint halo. Merging the sets would have averaged that away instead of exposing it.

  A set may **pin its black level** with a `calibration.txt` (`blacklevel = N`) copied from a real
  layout's saved settings, instead of deriving one from `calibrate\`. `live` does this, and it
  matters: a level derived from a handful of frames lands lower than one accumulated over a full run,
  the binarization threshold is measured from it, and so borderline frames flip. A false positive the
  user hit in a real run was scored as *correctly rejected* by the suite until this existed.

  `--verbose` prints every frame, `--dump` writes failing frames plus their binarized foreground to
  `testdata\_dump`, and **`--measure`** prints the range each measured quantity takes over each
  labelled class. `--measure` is how the gates in `MaskGates` were chosen and how any change to them
  should be argued - it shows both what a loading screen scores and how close the nearest non-loading
  frame gets, which is the only thing that says whether a gate has margin or is fitted to these
  frames.
- **`tests\GateScenarios.cs`** - synthetic frames asserting what each gate can and cannot catch,
  including the two blind spots documented below. The labelled frames decide whether the detector
  works; this decides whether it fails the way it is documented to.

All three runners compile `LoadDetector.cs`/`FeatureDetector.cs`/`MaskDetector.cs` **directly**, so
they exercise the same code the component calls rather than a reimplementation. That is the whole
reason the decision logic was pulled out of `Sly4BHLoadRemovalComponent`/`ComponentSettings` in the
first place - keep it that way, and keep those two files free of detection logic.

`tools\MakeTestData.cs` regenerates `testdata\` from full-resolution frames extracted with ffmpeg. It
downscales through `ImageCapture.ResizeImage`, the shipped capture path, **not** through ffmpeg's
scaler: the black-level gates measure the capture's noise floor, and a different resampler produces a
different noise floor. Its `--clip` flag shaves a percentage off every edge before the resize, which
simulates a hand-drawn crop that sits slightly inside the game frame.

What the offline tests deliberately do **not** cover: the capture path and the LiveSplit wiring. The
fixtures begin life already cropped and resized, so a bug in `ImageCapture` or in the crop UI is
invisible here and still needs a real run.

## Debugging detection changes

Beyond the test suites, these are the three ways to see what the detector did on a live run:

**If the timer pauses visibly late, suspect the debounce before the detector.** `IsGameTimePaused` is
set the instant `UpdateDebouncedState` flips, so there is no lag between detection and LiveSplit - but
that flip needs `AutoSplitterJitterToleranceFrames` consecutive agreeing updates, and those are
*component updates*, not game frames. Each one pays for a screen grab, a resize and a detection pass,
so the wall-clock cost is that count divided by the real update rate, which `ReportDebug` now measures
and prints on every line ("update rate: 34.2/s (29ms per frame) -> debounce costs 87ms at 3 frames").
Calibration has no debounce at all, which is why detection looks instant there and lagged in a run -
that difference is expected, not a symptom.

- **The live debug label** in the settings panel - `ReportDebug` mirrors every frame's full decision
  there, and it names the gate that rejected the frame ("Not a loading screen" for the black patch,
  "No mask" for an empty or degenerate region, otherwise the specific measurement: "Not the loading
  mask (median hue 115.0 outside 104-114)"), with the measured numbers next to the allowed ones. Only
  visible while the layout settings dialog is open.
- **The detection log** - enable `SaveDetectionLog`; on timer start the component redirects
  `Console.Out`/`Console.Error` into `Sly4BHLoadRemovalLog/Sly4BHLoadRemoval_Log_<timestamp>_<game>_<category>.txt`.
  This is the only way to see decisions during an actual run with the settings dialog closed. It records
  every `isLoading` transition plus a periodic trace every `DebugLogEveryNFrames` (60) frames - the
  periodic trace exists so a run that never detects anything still logs something.
- **The "save Cutout" button** - dumps the exact detection input to `Sly4BHDebugCaptures/` next to the
  DLL: the full 300x300 capture, plus the black patch and the mask region cropped out of it. Fastest
  way to confirm the two fixed regions land where they should on a given crop - the patch must be
  solid backdrop and the region must contain the whole mask.

Detection only runs at all when `timerStarted && !isCalibrating && hasCalibration` - a component that
appears totally inert is usually missing a calibration, not broken.

## Architecture

- **`Sly4BHLoadRemovalFactory.cs`** - `IComponentFactory` entry point, registered via the
  `[assembly: ComponentFactory(...)]` attribute LiveSplit scans for. Declares update metadata
  (`UpdateURL`/`XMLURL`, currently a placeholder - see the `TODO` in the file) and version.

- **`Sly4BHLoadRemovalComponent.cs`** - the `IComponent` implementation and the per-frame state machine.
  LiveSplit calls `Update(...)` roughly every frame; that calls `CaptureLoads()`, which grabs a capture
  via `settings.CaptureImage()`, runs `DetectMask()` on it, and feeds the resulting boolean through
  `UpdateDebouncedState()` - a consecutive-frame-agreement debounce (`AutoSplitterJitterToleranceFrames`)
  that only flips `IsGameTimePaused` once the raw per-frame result has been stable for several frames,
  rejecting single-frame noise. There's no black-screen-based pausing and no pre/post-load timing
  correction (unlike the TWoC base this was forked from) - a "load" is exactly the span of frames where
  the mask is detected. The component also hooks LiveSplit's timer events (`OnStart`, `OnReset`,
  `OnSplit`, `OnUndoSplit`, `OnSkipSplit`, `OnPause`, `OnResume`) to reset internal state and drive the
  optional autosplitter (`NumberOfLoadsPerSplit`, compared against per-split thresholds from `settings`).
  While `settings.isCalibrating` is true, `Update()` instead drives `settings.CalibrationTick()` each
  frame and skips normal detection.

  **All detection geometry is absolute pixel coordinates in a 300x300 image**, not fractions. That works
  only because `CaptureImage()` always resizes the user's crop to exactly 300x300 (`resizeSize`), and the
  user's crop is the *entire* 16:9 game feed. If that size ever changes, every constant in
  `FeatureDetector` and `MaskDetector` has to change with it. Two fixed regions matter:

  - **Black reference patch** (`FeatureDetector.BlackRegion`) - columns `[40, 80)`, rows `[120, 160)`,
    i.e. **40x40**. To the left of the mask and level with it. Solid backdrop for the whole duration of
    a loading screen.
  - **Mask region** (`MaskDetector.MaskRegion`) - columns `[110, 190)`, rows `[100, 180)`, i.e.
    **80x80**. Where the mask is looked for; generous enough to absorb an imperfect crop.
  - **Statistics region** (`MaskDetector.StatsRegion`) - columns `[60, 250)`, rows `[190, 270)`. The
    row of collectible icons an *area* load draws along the bottom. Ten rows clear of the mask region.
    Used only to tell the two loading screens apart - see below.

  The user is assumed not to include anything *outside* the game frame in their crop, but may well cut
  off some of its edges. That asymmetry is why both regions sit comfortably inside the frame. The two
  must never overlap, or the mask itself would raise the level meant to measure backdrop -
  `GeometryTests` asserts this.

  **The pipeline** (`LoadDetector.Detect`), a direct conversion of a reference Python/OpenCV snippet:

  1. **Black patch is black** - its max intensity is `<= blacklevel + BlackLevelTolerance` (10).
     Rejects essentially every gameplay frame on its own (64 of 95 in `recording\notloading`), and is
     what makes the naive binarization below safe: nothing downstream can tell a mask from scenery.
  2. **Binarize** the mask region at `(frameBlackLevel + 1) * 2`, on **luma** (`GrayAt`, OpenCV's
     BGR2GRAY weights), not on max-channel intensity.
  3. **Median-blur** 5x5, which on a binary image is a majority vote - at least 13 of 25 neighbours
     lit. Erases speckle and single-pixel bridges without touching the mask body.
  4. **Bounding box** of what survives, then measure over it: fill, aspect ratio, and the median hue,
     saturation and value.
  5. **Three gates** (`MaskGates`), *all* of which must pass.

  **The threshold is `(level + 1) * 2`, measured from the current frame's own patch.** The `+1` puts
  it strictly clear of the measured maximum; the doubling makes the allowance proportional rather
  than fixed, which is what lets one constant serve both capture pipelines. An OBS recording whose
  encoder crushes near-black to 0 thresholds at 2; a live screen capture reading 3 thresholds at 8,
  which is enough to clear the mask halo that survives there. That halo is not hypothetical - under a
  fixed threshold of 0 the bounding box swelled to nearly the whole region on live captures while
  working fine on the recording, and it failed *intermittently*, because frames that happened to be
  perfectly black still worked.

  Using the *calibrated* level here instead would mean a frame whose noise floor sits a level or two
  above the calibrated minimum lights up entirely. The calibrated level is used for exactly one thing:
  gate 1. Grep for `CalibratedBlackLevel` before adding a comparison.

  **The gates, and where they came from.** All three were read off `DetectionTests.exe --measure`.
  Across 89 frames from two capture pipelines a settled loading screen is remarkably tight:

  | quantity | loading | gate | why |
  |---|---|---|---|
  | fill (foreground / bbox) | 0.739-0.803 | 0.60-0.90 | the mask fills three quarters of its own box |
  | aspect (width / height) | 1.051-1.114 | 1.00-1.20 | measurably wider than tall throughout the pulse |
  | median hue (0-179) | 110-112 | 104-114 | the blue the mask is rendered in |
  | median saturation | 146-155 | *not gated* | measured and logged only |
  | median value | 79-83 | *not gated* | measured and logged only |

  What has to be rejected is not gameplay but the **gameplay-to-load transition**, where the mask
  animates into place over a fading background. Those frames form a near-continuum and only five of
  them clear fill and aspect at all - hue then rejects all five, and on more than one channel each:

  |  frame   | fill  | aspect | hsv |
  |---|---|---|---|
  | `f00504`   | 0.707 | 1.043 | (116.0, 145, 71.5) |
  | `f00954`   | 0.676 | 1.043 | (116.0, 146, 73.0) |
  | `f00930`   | 0.701 | 1.047 | ( 63.0, 169, 90.0) |
  | `f00480`   | 0.695 | 1.023 | ( 96.5, 183, 96.0) |
  | `13_19_08` | 0.686 | 1.023 | ( 61.0, 184, 97.0) |

  That redundancy is the point: no single gate is threading a narrow gap on its own, which is the
  failure mode that broke this detector before (see the black-band note below). These numbers are a
  snapshot of the current fixtures - regenerate them with `--measure` rather than trusting them after
  `testdata\` changes.

  **Saturation and value are deliberately not gated.** Both were tried and both rejected *nothing*:
  with either or both opened right up, every frame in both sets still classifies correctly and the
  debounced pause lands on the same frame. They looked like useful redundancy on the two hardest
  frames (`f00504`, `f00954`, hue 116 and value 71.5-73), but those are the mask part-way through
  fading in, so hue and value move together along one trajectory rather than saying independent
  things.

  The reasoning generalises: **removing a gate can only cause a false positive** - a few frames of
  early pause - **while every gate kept is another way to lose a whole load** to a screen that renders
  slightly off from these 89 frames. A value floor of 76 against a measured minimum of 79 is exactly
  that kind of hostage. Don't add a gate because a quantity happens to be measurable; add it when
  `--measure` shows it rejecting something nothing else does.

  **The fill floor is deliberately far below the loading range.** The transition runs continuously
  from 0.125 up to 0.707, so wherever the floor goes it lands close to one of those frames; putting it
  at 0.72 to just clear the worst one (`f00504`, 0.707) would leave genuine loads only 0.019 of
  headroom. At 0.60 the loading class has 0.139 and the frames that slip through are dealt with on
  colour instead. A false positive costs a few frames of early pause; a false negative costs the whole
  load.

  **The medians are over the whole bounding box, backdrop included** - not over the lit pixels alone.
  That is only meaningful because a settled mask fills three quarters of its box; when it does not,
  the median collapses towards the backdrop's black. So the hue gate and the fill gate are *not*
  independent, and `GateScenarios` pins that. `MaskMetrics` also carries `LitMedian*` over the
  foreground alone, reported but not acted on - it says what the mask is coloured independently of how
  much of its box it fills, which is what to reach for if the fill/colour coupling ever becomes a
  problem.

  **Two kinds of loading screen, and only one of them is a split.** An *area* load shows a tip line at
  the top and a row of four collectible icons along the bottom; a *plain* load shows the mask alone on
  black. Area loads happen at consistent points in a run, plain ones do not, so the autosplitter counts
  area loads only.

  **This is not a detection gate.** Both are loads, both set `IsGameTimePaused`, and `IsLoading` is
  true for both - `MaskDetector.MeasureStatsFill` only populates `DetectionResult.HasStats`, which
  nothing in `LoadDetector` acts on. The distinction is consumed in
  `Sly4BHLoadRemovalComponent.UpdateDebouncedState`. Breaking this would silently stop removing load
  time from half the loads, which is why `GateScenarios` asserts that a plain load is still accepted.

  Like the colour check, it is a category rather than a tuned threshold: measured over all 89 loading
  frames a plain load fills **exactly 0.000** of the region and an area load **0.386-0.412**.
  `MinStatsFill` is 0.05, so an area load could lose 87% of its icon row and still register while a
  plain load would need ~760 stray lit pixels to trip it. No median blur - it is a fill fraction over
  ~15k pixels, so speckle cannot move it.

  **The autosplitter counts the first frame of a load where the statistics have been seen**, not the
  rising edge, tracked by `sawStatsThisLoad` / `countedThisLoad` (reset at each load boundary and in
  `timer_OnReset`). Measured, the statistics are already fully up *before* detection confirms the load
  (`sequence\f00956` has them while the mask is still mid-transition), so a rising-edge test would work
  today and the split fires at the same moment either way. The sticky form costs two bools and means a
  load whose statistics appear late still counts.

  Labelled in the fixtures by `testdata\<set>\arealoads.txt` (`all`, a frame number, or `from-to`;
  anything unlisted is a plain load, and a set without the file is not checked). `DetectionTests`
  reports this as its own `load types` line, separate from loading/notloading, because getting it
  wrong costs a split rather than a pause.

  **Two blind spots**, both asserted in `tests\GateScenarios.cs` so they stay visible:

  - **Nothing outside the mask region is looked at**, beyond the 40x40 reference patch. The previous
    detector checked a black band around the mask, which is how it rejected the transition's scattered
    masks - that band is gone. It was a deliberate trade: the band had to thread a gap between the
    transition's masks and the loading screen's own loot icons, whose vertical extent moves between
    loads because the tip text wraps to one or two lines, and threading that gap **broke real runs**.
    Colour rejects the transition directly and does not care what else is on screen.
  - **Junk lit inside the mask region is absorbed into the bounding box.** The box is the extent of all
    foreground in the region, so nothing lit inside it can fall outside the box. Stray light next to
    the mask inflates the box rather than being flagged; only the fill dropping and the aspect drifting
    notice, and only once it is far enough out.

- **`LoadDetector.cs`** (same namespace) - the actual decisions, kept free of any LiveSplit or WinForms
  type so the offline runners can drive them. `LoadDetector.Detect` runs the pipeline above and returns
  a `DetectionResult` carrying every measured number plus a `DetectionStage` saying which gate rejected
  the frame; `MaskGates` holds the three gates and `FirstFailure` applies them; `CalibrationRun.Observe`
  is one calibration frame, returning a `CalibrationSample`; `CalibrationRun.TryFinish` commits to a
  `Calibration`. All the debug strings are built by `Describe()` on those result types, so the settings
  label, the detection log and the test output cannot drift apart.

  `Sly4BHLoadRemovalComponent.DetectMask` and `ComponentSettings.CalibrationTick` are now just
  capture-fetch plus a call plus reporting. **Keep detection logic out of both** - the moment a decision
  moves back into a `UserControl` or an `IComponent`, `tests\DetectionTests.cs` stops testing it.

- **`MaskDetector.cs`** (namespace `Sly4BHLoadDetector`) - `MaskRegion`, `BinarizationThreshold`, and
  `Measure` (binarize, median-blur, bounding box, then fill/aspect/HSV medians over it), plus
  `MaskBounds` and `MaskMetrics`.

  **Two extent conventions, and they must not be mixed.** `MaskBounds` edges are **inclusive**
  (`MaxCol` is the last lit column, so `Width == MaxCol-MinCol+1`), and `ToRectangle()` converts to a
  half-open `Rectangle`. But every *metric* is measured over `Crop()`, the reference's `[min:max]`
  slice, which drops the last row and column - so `SpanX`/`SpanY` (plain `max-min`) are what feed the
  aspect ratio. One pixel either way is immaterial on a ~45px box; mixing the two within one metric is
  not.

  `Measure` handles two degenerate outcomes explicitly, and callers must check both before reading any
  number: `HasForeground` is false when nothing cleared the threshold (ordinary on an all-backdrop
  frame, not an error), and `HasCrop` is false when the box has no extent in one axis, where the
  reference's slice is empty, the aspect ratio would divide by zero and a median would be taken over
  nothing.

  `Binarize` thresholds `MaskRegion` inflated by the blur radius and writes out only the interior, so
  the result is identical to binarizing the whole frame and cropping, at a fraction of the work.

  There is **no shape matching** - no reference silhouette, no canonical resize, no pixel-agreement
  score. An earlier version had all of that; it was removed, along with the calibrated
  size-fraction/intensity-sum ranges. Any `Sly4BHMaskReference.png` still sitting in a `Components`
  folder is a leftover and is not read.

- **`FeatureDetector.cs`** (same namespace) - `FramePixels` (a frame's bytes copied out once, so the
  several region queries per frame don't each pay for their own `LockBits`; assumes 32bpp, which
  `ImageCapture.ResizeImage` always produces) plus `GetBlackLevel` / `IsBackgroundBlack` over the black
  patch above.

  **`FramePixels` exposes three different notions of brightness, used for different things.**
  `IntensityAt` is `max(R,G,B)` and answers "how black is this?", matching `np.max` over a colour
  image - it is what the black level is measured with. `GrayAt` is luma with OpenCV's BGR2GRAY
  weights and is what foreground is *thresholded* on. `HsvAt` is OpenCV's 8-bit HSV (hue 0-179) and is
  what the colour gates measure. A saturated blue pixel reads 255, 29 and (120, 255, 255) respectively;
  they are not interchangeable.

  `GetBlackLevel` is a **strict maximum**, deliberately. Calibration takes the minimum of it across
  frames, so one clean frame establishes the level and a frame with something bright in the patch simply
  isn't the minimum; softening it to a percentile would let real content leak into the reading. (An
  earlier version *did* use a 99th percentile, because back then the same value also served as a
  loading-screen gate and a strict max made that gate flicker. It no longer plays that role.)

  Earlier versions also tried to infer the black level from the mask region itself - first requiring the
  whole region to be black, then a 4-sided border ring, then left/right margins only. All three broke,
  because they coupled the black level to assumptions about where the mask *isn't*. Don't reintroduce
  anything along those lines - the black level must stay measured somewhere the mask can never reach.

- **`ImageCapture.cs`** - screen/window capture and the crop/resize math, carried over from the TWoC
  base essentially unchanged (still generic, not detection-method-specific). Supports capturing the
  whole primary display (`Graphics.CopyFromScreen`) or a specific window (`PrintWindow`, via GDI
  `BitBlt` through `DLLImportStuff`). `ImageCaptureInfo` holds the user-configured crop rectangle plus
  derived fields; `SizeAdjustedCropAndOffset` rescales that crop for the actual captured
  resolution/aspect ratio/DPI so the same settings work across different capture sizes.

  **Three capture sources, chosen by the Display / Video capture radio buttons** (`CaptureSource`).
  Display mode covers both whole-screen and single-window capture - the dropdown lists screens then
  windows, and `processCaptureIndex` distinguishes them by sign. Video capture mode fills the same
  dropdown with DirectShow devices instead, so `processCaptureIndex` and `captureIDs` are meaningless
  there; `GetSettings` guards on the mode before indexing `captureIDs`, or it would save whichever
  screen happened to share the selected position. Each mode keeps its own selection so switching back
  and forth loses neither.

  The video path in `CaptureFromVideoDevice` is much shorter than the other two because the frame
  arrives as a Bitmap at the device's own resolution: no DC to blit, no DPI to undo, and the crop is a
  plain sub-rectangle. `scalingValueFloat` deliberately does not apply (it undoes Windows display
  scaling, which a capture card knows nothing about), and neither does the aspect-ratio correction in
  `SizeAdjustedCropAndOffset` (it finds a 16:9 region inside a differently-shaped desktop; a capture
  device delivers the signal it is given). The crop is intersected with the frame before use - a crop
  drawn against a different source resolution would otherwise throw straight out of the detection loop.

  **`ComponentSettings.CaptureImage()` must return the user's entire crop, unshifted, at 300x300** -
  detection indexes into it with absolute pixel coordinates (see the two fixed regions above), so any
  offset or size change silently breaks every region at once. It therefore delegates to
  `CaptureImageFullPreview(useCrop: true)`, the
  same path the settings preview uses, rather than duplicating the capture math. It previously *did*
  duplicate it, and applied a pair of `cropOffsetX/Y` fields (TWoC leftovers, `100`/`-115`, which aimed
  the capture at that game's "LOADING" text band) plus a different `captureSize`. Because the preview
  path zeroed those offsets and the detection path didn't, the preview showed a correct image while
  detection silently received a shifted, differently-sized one - so every region read the wrong pixels
  while everything *looked* right on screen. Those fields are **gone** now rather than merely zeroed, so
  the two paths cannot diverge again; `actual_offset_x/y` survives in `ImageCaptureInfo` but
  `SizeAdjustedCropAndOffset` always sets it to 0. The "save Cutout" button dumps the exact
  detection input (the full 300x300 capture, plus each fixed region cropped out of it) to
  `Sly4BHDebugCaptures/` next to the DLL, which is the fastest way to confirm the regions are landing
  where they should on a given user's crop.

- **`DLLImportStuff.cs`** - raw Win32 P/Invoke declarations (`gdi32.dll`/`user32.dll`) backing the
  window-capture path in `ImageCapture.cs`.

- **`VideoCaptureDevice.cs`** - the third capture source: a capture card or webcam, via hand-rolled
  DirectShow COM interop. `VideoCaptureDevices.Enumerate/Resolve` list and re-find devices;
  `VideoCaptureSource` owns one running filter graph.

  **Hand-rolled rather than AForge/DirectShowLib/OpenCvSharp** for the same reason `DLLImportStuff.cs`
  exists: the component ships as a single DLL dropped into `Components\`, and every wrapper turns that
  into several files the user has to place correctly.

  Things worth knowing before changing it:

  - **Only the prefix of each COM interface that is called is declared.** Safe as long as the declared
    methods stay in vtable order and nothing past the last one is invoked. Adding a call means
    declaring every method above it too.
  - **Frames are pulled, not pushed.** `SetBufferSamples(true)` plus polling `GetCurrentBuffer`
    replaces implementing `ISampleGrabberCB`, which removes the callback interface and its threading
    and lifetime problems entirely. A component asked for a frame ~30x a second loses nothing by it.
  - **All COM lives on one worker thread**, which builds the graph, polls, and tears it down. Callers
    only touch `latestFrame` under a lock. The two callers really are on different threads: LiveSplit's
    timer drives detection while the settings dialog draws previews on the UI thread.
  - **`SetMediaType` takes major type and subtype only.** Also naming `FORMAT_VideoInfo` looks harmless
    and is not - it constrains the connection enough that intelligent connect stops inserting the
    colour converter on some devices.
  - **A device can accept a pixel format it never actually produces.** OBS's virtual camera accepts an
    RGB24 *and* an RGB32 connection, reports the graph Running, and then pushes nothing at all - every
    `GetCurrentBuffer` answers `VFW_E_WRONG_STATE` forever. It only really emits **NV12**. So
    "connected" is not success: `PollLoop` returns false if no frame arrives, and the caller moves to
    the next format. Verified end to end - the decoded frame is a correct 1920x1080 title screen.
  - **YUV is decoded here, not by Windows.** There is no stock DirectShow filter that converts NV12 to
    RGB, which is why intelligent connect could not do it and the graph fell silent. `Nv12ToBitmap` /
    `PackedYuvToBitmap` (YUY2, UYVY) handle it. Note the RGB layouts are bottom-up DIBs and the YUV
    ones are top-down - only the RGB path reverses rows.
  - **The YUV matrix is chosen by frame height**: BT.709 at 720p and above, BT.601 below, both
    studio-swing. Capture formats carry no reliable colorimetry flag and this is the usual fallback,
    but it is a *guess* and it matters more here than it looks: the detector gates on median hue in a
    10-unit window, so the wrong matrix would shift the mask's blue out of band. If loads stop being
    detected specifically on a YUV source, check the reported hue before touching the gates.
  - **First-frame timeout is deliberately generous (2s** - `FirstFrameTimeoutPolls` 200 at
    `PollIntervalMs` 10; note the two move together, so changing the poll interval silently changes
    the timeout**).** Falling through quickly would thrash a real capture card, which can take seconds
    to lock onto an incoming signal: it would walk the whole format chain, find nothing, and start over
    on a device that was about to work. The cost is that a device which falsely advertises RGB takes a
    few seconds to reach its native format, once, with the status label showing progress throughout.
  - **The graph is retried, forever, every 3s.** An analog capture card with nothing plugged in cannot
    describe a video format, so `RenderStream` returns `E_FAIL` - and that is the *normal* state when
    LiveSplit starts before the console is on. Measured on a CY3014 USB analog card with no signal:
    fails for every pin category and every pixel format, including "any pin, any type". Without the
    retry the component would sit dead until the user thought to reselect the device.
  - `RenderStream` is tried at four decreasing specificities (capture pin, preview pin, any pin, any
    pin + any type), and the pixel format at three (RGB24, RGB32, unconstrained). `>= 0` rather than
    `== S_OK`, because partial successes like `VFW_S_NOPREVIEWPIN` are still working graphs.

  **Latency, and where it actually goes.** `tools\BenchCapture.cs` times the per-frame path against a
  real device. Measured at 1080p, before any of the work below:

  | stage | ms/frame |
  |---|---|
  | `ResizeImage` 1080p -> 300x300 | 24.9 |
  | whole-frame decode + copy | 11.8 |
  | **detection itself** | **0.15** |

  37ms per frame, so ~27 updates/s, so the 3-frame debounce alone cost 112ms. **Detection is free;
  this is all capture overhead**, and the debounce multiplies it - `AutoSplitterJitterToleranceFrames`
  is counted in updates, so anything that slows a frame costs three times that at each end of a load.

  This matters more than it looks, because **most users end up on the slow path whether they want to
  or not**: capture cards are exclusive, so anyone with OBS open cannot also open the card here
  (`RenderStream` returns `E_FAIL`) and has to use OBS Virtual Camera instead - which only ever emits
  OBS's canvas size.

  Four things came out of that, in increasing order of value:

  - **The crop and the downscale happen inside the decode** (`CaptureScaled`), so no full-resolution
    bitmap is ever built and `ImageCapture.ResizeImage` is not involved on this path at all. Measured
    at 720p: **2.56ms per frame, 391 updates/s**, against 41ms at 1080p for the two-step version.
    The averaging is done in YUV with the colour conversion running once per *destination* pixel -
    that is exact, not an approximation, because the YUV->RGB matrix is linear in Y, U and V, so
    averaging before converting equals averaging after it up to clamping.

    **The two capture paths therefore resample differently**: video capture area-averages, display and
    window capture still go through GDI+ `HighQualityBicubic`. That is deliberate and safe - they are
    separate capture pipelines, calibrated separately and fixtured separately (`testdata\recording`
    vs `testdata\live`), which is exactly the convention that already exists here. Do not "unify"
    them without regenerating fixtures for whichever one changes.
  - **The device is asked for the smallest format at or above 640x360** (`ChooseCaptureFormat`, via
    `IAMStreamConfig`, before the pin connects). Everything downstream is proportional to source size
    and the result is squashed to 300x300 anyway, so capturing 1080p to find a 46px mask is pure cost.
    Measured on a webcam: **47ms -> 5.0ms per frame, 200/s.** The floor is not arbitrary - the mask is
    ~15% of frame width, so 640 wide still leaves ~95px of mask before the squash, and the chroma the
    hue gate reads is half that again.
  - **Only the requested region is decoded** (`CaptureRegion`), straight from the raw sample. The
    obvious design - decode the whole frame, hand over a copy, then crop - spent 11.8ms per frame
    copying pixels that were about to be thrown away.
  - **One frame is decoded ahead, and only once the last was collected.** Decoding purely on demand
    puts the colour conversion between the component asking for a frame and getting one; decoding
    every sample eagerly wastes it on frames nobody collects. Decoding one ahead makes the rate match
    consumption on its own and overlaps the caller's resize. `PollIntervalMs` is 10 because the poll
    itself is only a buffer copy, and that interval is latency directly.

  **Watch the interaction, not the pieces.** Region-only decode plus fast polling, on their own, moved
  the conversion onto the consumer's critical path and made 1080p *worse* - 37ms to 46ms. They only
  pay off combined with format selection, which makes the conversion cheap enough not to matter. The
  decode-ahead then recovered most of the rest (46 -> 41ms at 1080p).

  **`tools\CompareResize.cs` is the gate on any change to this.** It takes one frame from a device and
  produces 300x300 both ways - full-resolution decode + `ResizeImage` against `CaptureScaled` - then
  prints the pixel delta and, more usefully, both paths' `DetectionResult`. The three numbers that
  matter are the gated ones: fill, aspect, median hue. **Run it on a loading screen**; on gameplay
  there is no mask and the mask numbers mean nothing.

  A capture device that only offers one large format is no longer a problem for cost, but it is still
  worth lowering an OBS canvas: the source resolution sets how much real detail the mask has before
  the squash to 300x300, not just how much work it is.

  `tools\ListVideoDevices.cs` drives all of this without LiveSplit - it lists devices, and given an
  index opens one and saves a frame. First thing to run against "my capture card isn't in the
  dropdown" or "the preview is black".

- **`ComponentSettings.cs` / `.designer.cs` / `.resx`** - a WinForms `UserControl` that is *both* the
  settings UI shown in LiveSplit's layout editor *and* the holder of essentially all mutable component
  state (capture target, crop rectangle, calibration results, autosplitter per-split load counts,
  logging options). Persists to/from LiveSplit's layout XML via `GetSettings`/`SetSettings`. The
  "Calibrate" button (`CalibrateBlacklevelButton_Click`) toggles calibration on/off.

  **Calibration measures exactly one thing: the black level.** `CalibrationTick()` (called every frame
  by the component) keeps the running **minimum**, across frames, of the black patch's maximum. The
  minimum is the point of it - a frame landing on it is one where the patch really was showing
  loading-screen backdrop, so the value measures the capture's noise floor rather than whatever
  happened to be on screen. Nothing about the mask is calibrated: it is looked for in a fixed region
  and judged on measured properties, so there are no calibration gates, no accumulated box, and
  nothing that can make calibration "fail" other than never seeing a frame.

  The mask measurement shown alongside it in the debug label feeds into nothing. It is there so the
  user can watch the detector react to the load screen and confirm the fixed regions land on the right
  part of their crop - which used to require reading tea leaves from the accumulated box.

  On stop, `FinishCalibration()` just commits the level - no post-processing, no margins, nothing
  written to disk. It persists in the layout XML as `blacklevel` alongside `HasCalibration`.

  **The black level is only read back when `HasCalibration` is present**, which only layouts saved by
  this version carry. Older layouts do have a `blacklevel`, but it is not the same measurement - the
  reference patch moved from above the mask to beside it, and the two read differently on the same
  capture (3 against 0 on the `live` set). Reading a stale value back would apply a wrong threshold
  silently, so those layouts come back as uncalibrated and the user recalibrates once. Per-split autosplit
  thresholds are keyed by `GameName + Category` in `AllGameAutoSplitSettings` and swapped in via
  `ChangeAutoSplitSettingsToGameName` whenever the active run's game/category/split list changes
  (`AutoSplitData` / `AutoSplitEntry`). Note the same limitation as the TWoC base: the autosplitter
  identifies splits by name, so **splits within a run must have unique names**.
