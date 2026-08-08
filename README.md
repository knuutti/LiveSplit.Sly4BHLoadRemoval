# LiveSplit.Sly4BHLoadRemoval

LiveSplit component to automatically detect and remove loads from Sly Cooper: Thieves in Time and Bentley's Hackpack.

The component is based on [LiveSplit.CrashTWoCLoadRemoval](https://github.com/thomasneff/LiveSplit.CrashNSTLoadRemoval),
reusing lot of the background code and the UI. 

# How to use

1. [Download](https://github.com/knuutti/LiveSplit.Sly4BHLoadRemoval/releases/latest) the component DLL and drop it into the `Components` folder of your LiveSplit install.
2. Open LiveSplit -> Edit Layout -> Click the plus icon -> Control -> Sly 4 / Hackpack Load Remover.
3. Pick a capture source with the **Display / Video Capture** buttons at the top.
    - **Display** lists your screens and open windows
      - If you use Display as the capture method, the recommended way is to select one of your screens and by having the preview of your game feed always on top. 
    - **Video Capture** lists your capture cards and webcams. 
      - If you use Video Capture as the capture method, the recommended way for using the tool is with **OBS Virtual Camera**, since using most capture cards won't work if they are already being used by your recording/streaming software.
      - It usually takes a few seconds for the capture device to connect after selecting it, so don't panic if you don't instantly see the capture feed.
      - The preview is drawn once when the device starts sending frames. 

4. Under preview, crop the capture in order to capture the **game feed only**, you don't need to be pixel perfect but try to crop the area as accurately as you can. 
    - Left click sets the upper-left corner, right click sets the bottom-right corner.
    - The important part is not to include anything *outside* the game picture - no desktop, no capture-card
  borders, no stream overlay. Clipping a little off the edges of the game picture is fine.
5. Click "Calibrate", then enter a loading screen. Click "Stop Calibrating" once you've held a load for
  a moment. Calibration is used to measure the background intensity of your capture method, which helps when extracting the background during load screen detection.
6. If you want to track the loadless time in LiveSplit, set your timing method to Game Time. If you want to keep your main timing method as Real Time but want to also display the loadless time, download and add [Livesplit.AlternateTimingMethod](https://github.com/Dalet/LiveSplit.AlternateTimingMethod/releases/latest) to your `Components` folder and add it to your layout.

**If you change your capture method, you must calibrate again.** It is also recommended to calibrate again any time an update of the component is released.

# How does it work?

The load screen detection uses a rule-based deterministic algorithm. The captured game feed is resized to 300x300 image which is then processed and used for detection.

- During calibration, the intensity of the black background is measured. A 40x40 sized area that should be entirely black during a load screen is monitored during calibration, and the highest intensity value during every detected frame is stored. The smallest of these high intensity values is then stored as the calibrated blackness level.
- During detection, we go through multiple steps (gates) to determine if a frame is a valid load screen or not:
1. The brightest pixel of a 40x40 area that should be completely black during a loading screen is compared to the blackness level. If the highest pixel intensity is too high compared to the blackness level, the frame is not a load screen.
2. The black background is removed and the foreground is smoothened with median blur. The foreground pixels at the center of the capture region are then analysed and a bounding box for the Sly mask is tried to fit. If the aspect ratio or the fill (amount of foreground pixels inside the bounding box) is not within a specified range, the frame is not a loading screen.
3. Final step is a color check. The hue inside the accepted bounding box is analysed, if the median value is above a certain threshold, the frame is accepted as a loading screen.
4. If the bottom of the screen has foreground pixels, the load is classified as an area load (in Sly 4 there are the collectible statistics, in BH there are the token/gift counts). This information is only relevant for the AutoSplitter since it uses the area loads for counting the loads per split.

# Known Issues

- If you want to use the AutoSplitter functionality, **all your Splits need to have different names!**. If
you have Splits that share the same name, the AutoSplitter is not able to differentiate between them.
- If you get a corrupted load screen (background is not black), the load remover most likely won't work and you need to fix the in-game time manually afterwards.
