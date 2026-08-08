# LiveSplit.Sly4BHLoadRemoval

LiveSplit component to automatically detect and remove loads from Sly Cooper: Thieves in Time, based
on the pulsing raccoon-mask loading animation.

This started as a fork of [LiveSplit.CrashTWoCLoadRemoval](https://github.com/thomasneff/LiveSplit.CrashNSTLoadRemoval),
reusing its screen/window capture, crop/preview UI, and autosplitter machinery. The actual load
*detection* is different: instead of OCR'ing a "LOADING" text string, it looks for the pulsing mask
against the solid black backdrop that surrounds it. See "How does it work?" below.

# How to use

- Build the component (see the root `CLAUDE.md` for build prerequisites) and drop the output DLL into
  the `Components` folder of your LiveSplit install.
- Open LiveSplit -> Edit Layout -> Click the plus icon -> Control -> Sly 4 / Hackpack Load Remover.
- Pick a capture source with the **Display / Video capture** buttons at the top.
  - **Display** lists your screens and then your open windows - use this when the game is running on
    this PC, in an emulator or a window.
  - **Video capture** lists your capture cards and webcams, for playing on original hardware. It also
    picks up OBS's virtual camera, if you would rather send the feed through OBS. Pick a device in the
    dropdown; the preview updates by itself and the resolution appears next to the buttons.
    - Some devices take up to ten seconds to show a picture the first time they are selected. That is
      normal - the component is working out which pixel format the device actually produces, and the
      status text next to the buttons says what it is trying.
    - The preview is drawn once, when the device starts sending frames. It is not a live view; press
      **Update Preview** whenever you want a fresh picture to draw the crop on.
    - "could not build a capture graph" means the device is not receiving a picture. Switch the console
      on and it will connect by itself within a few seconds; there is no need to reselect anything.
    - The device is asked for the smallest resolution it offers at 640x360 or above, which is plenty to
      find the mask and keeps the timer reacting quickly.
    - **A capture card can only be opened by one program at a time.** If OBS is using it, this
      component cannot, and you will get "could not build a capture graph". Either deactivate the
      source in OBS (right-click it -> Deactivate), or leave OBS running and select **OBS Virtual
      Camera** here instead - that works fine and costs only a few milliseconds a frame.
- You can specify to capture either the full primary Display (default) or an open window. This window
  has to be open (not minimized) but does not have to be in the foreground.
- Under preview, crop the capture down to **the entire game feed** (the whole 16:9 picture), not just the
  area around the mask. Left click sets the upper-left corner, right click sets the bottom-right corner.
  The important part is not to include anything *outside* the game picture - no desktop, no capture-card
  borders, no stream overlay. Clipping a little off the edges of the game picture is fine; the regions
  the detector reads sit well inside it.
- Click "Calibrate", then get a real loading screen on screen while it runs (the label shows the black
  level and how many frames have been seen so far). Click "Stop Calibrating" once you've held a load for
  a moment. This measures one thing: how black "black" actually is on your capture.
- Set your timing method to gametime.

**If you are upgrading from an earlier version, you must calibrate again.** The patch the black level is
measured from has moved, so a level stored by an older layout no longer means the same thing and is
ignored - the component will show "NOT SET" until you recalibrate.

# How does it work?

Your crop is resized to a fixed 300x300 working image, so the detector can look at the same few regions
of it regardless of your capture resolution.

**Calibration measures one thing: the black level.** A 40x40 patch of the picture to the left of the
mask, which is solid backdrop for the whole of a loading screen, is checked for its brightest pixel. The
*smallest* such value seen across the whole run is kept - that frame is one where the patch really was
showing the loading screen's backdrop, so the value captures how black your particular capture setup
gets, noise and compression included. Nothing about the mask is stored.

Then, while the timer runs, each frame is measured and has to pass every check below to count as
loading:

1. **The patch is still black**, within a small tolerance of the calibrated level. Almost every gameplay
   frame fails here, and it is what makes the rest safe - the steps below have no way to tell a mask from
   scenery.
2. The picture is **converted to black and white** at a threshold just above that frame's own black
   level, and specks are cleaned up with a median blur. Whatever is left inside an 80x80 region around
   the middle of the picture is the mask, and its bounding box is taken.
3. That box then has to look right on three counts: it is **mostly filled** (0.60-0.90 of the box is
   mask), it is **slightly wider than tall** (width/height 1.00-1.20), and its **median hue** is the
   blue the mask is drawn in (104-114 on OpenCV's 0-179 scale).

The hue check is what separates a real loading screen from the animation the game plays on the way into
one, which draws a mask at the same place and the same size but lights its eyes green and yellow.

Median saturation and brightness are measured and written to the detection log, but nothing is rejected
on them - tested against every labelled frame, neither rejected anything the other checks did not.

A frame only flips the state once the answer has agreed for several consecutive frames, to reject
single-frame noise; at that point game time is paused, and it resumes the same way once the mask stops
being detected.

Unlike the TWoC load remover this is based on, there's no black-screen-based pausing and no pre/post-load
timing correction - loading time here is defined as exactly the frames where the mask is detected.

# Known Issues

If you want to use the AutoSplitter functionality, **all your Splits need to have different names!**. If
you have Splits that share the same name, the AutoSplitter is not able to differentiate between them.
