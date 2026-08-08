using Sly4BHLoadDetector;
using LiveSplit.Model;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace LiveSplit.UI.Components
{
    // Where frames are grabbed from. Persisted by name, so adding a source later does not renumber
    // the existing ones in saved layouts.
    public enum CaptureSource
    {
        Display,
        VideoCapture
    }

    public partial class Sly4BHLoadRemovalSettings : UserControl
    {
        #region Public Fields

        public bool AutoSplitterEnabled = false;

        public bool AutoSplitterDisableOnSkipUntilSplit = false;

        // Calibration state/result, derived by CalibrateBlacklevelButton_Click / CalibrationTick /
        // FinishCalibration from frames captured while the user holds the real load screen on screen.
        // A run produces one number; see CalibrationRun for what it means. hasCalibration is what gates
        // detection - without a black level there is nothing to measure against.
        public bool isCalibrating = false;

        public int blacklevel = -1;

        public bool hasCalibration = false;

        // Where frames come from. Display covers both whole-screen and single-window capture (the
        // dropdown lists screens then windows); VideoCapture takes them straight off a capture card or
        // webcam via DirectShow, for people playing on original hardware.
        public CaptureSource captureSource = CaptureSource.Display;

        // Round-trips through the layout XML in both configurations, so switching between a Debug and a
        // Release build doesn't quietly discard the user's choice. Only a Debug build acts on it - the
        // checkbox that controls it is Debug-only, so a Release build has no way to turn logging off
        // again and therefore never starts it (see Sly4BHLoadRemovalComponent.ReloadLogFile).
        public bool SaveDetectionLog = true;

        public string DetectionLogFolderName = "Sly4BHLoadRemovalLog";

        // How many consecutive updates the raw detection must agree for before the timer is actually
        // paused or resumed. Counted in *component updates*, not game frames - each one costs a screen
        // grab, a resize and a detection pass, so the wall-clock latency is this number divided by the
        // measured update rate the debug label reports.
        //
        // 3, down from the 8 inherited from the fork: over testdata\recording\sequence the raw verdict
        // changes exactly twice, once per boundary, with no spurious flips, so the extra five frames
        // were pure latency at both ends of every load. Raise it only if a real capture shows the raw
        // verdict flickering - the detection log records every transition, so that would be visible
        // rather than a guess.
        public int AutoSplitterJitterToleranceFrames = 3;

        //If you split manually during "AutoSplitter" mode, I ignore AutoSplitter-splits for 50 frames. (A little less than 2 seconds)
        //This means that if a split would happen during these frames, it is ignored.
        public int AutoSplitterManualSplitDelayFrames = 50;

        #endregion Public Fields

        #region Private Fields

        private AutoSplitData autoSplitData = null;

        private float captureAspectRatioX = 16.0f;

        private float captureAspectRatioY = 9.0f;

        private List<string> captureIDs = null;

        // Capture is resized to this canonical size before detection runs on it, so calibration and
        // thresholds stay consistent regardless of the source resolution/crop the user picked.
        private Size resizeSize = new Size(300, 300);

        private bool drawingPreview = false;

        private List<Control> dynamicAutoSplitterControls;

        private float featureVectorResolutionX = 1920.0f;

        private float featureVectorResolutionY = 1080.0f;

        private ImageCaptureInfo imageCaptureInfo;

        private LiveSplitState liveSplitState = null;

        private int numScreens = 1;

        private Dictionary<string, XmlElement> AllGameAutoSplitSettings;

        private Bitmap previewImage = null;

        //-1 -> full screen, otherwise index process list
        private int processCaptureIndex = -1;

        private Process[] processList;
        private int scalingValue = 100;
        private float scalingValueFloat = 1.0f;
        private string selectedCaptureID = "";
        private Point selectionBottomRight = new Point(0, 0);
        private Rectangle selectionRectanglePreviewBox;
        private Point selectionTopLeft = new Point(0, 0);

        // Accumulated while isCalibrating is true - see CalibrationTick/FinishCalibration.
        private CalibrationRun calibrationRun = new CalibrationRun();

        // Video capture device state. The list is refreshed whenever the dropdown opens; the moniker
        // is the identity that gets saved, with the friendly name as a fallback for when the device
        // moves to a different USB port (see VideoCaptureDevices.Resolve).
        private List<VideoCaptureDeviceInfo> videoDevices = new List<VideoCaptureDeviceInfo>();
        private string selectedVideoMoniker = "";
        private string selectedVideoName = "";

        // The running graph, started lazily on the first capture and torn down whenever the selection
        // or the source mode changes. Guarded because CaptureImage() is called from LiveSplit's timer
        // thread while the settings dialog drives previews from the UI thread.
        private VideoCaptureSource videoSource;
        private readonly object videoSourceLock = new object();

        // Refreshes the preview and the status label while a capture device is selected.
        //
        // The preview is pull-based for screens and windows - you press Update Preview and it grabs
        // one. That does not work for a device: it is a live source and takes a moment to come up, so a
        // single grab at selection time never shows anything but "starting...".
        private Timer livePreviewTimer;

        // Set whenever the selected device changes, and cleared by the first preview drawn after it
        // starts delivering frames.
        private bool videoPreviewPending;

        private const int LivePreviewIntervalMs = 500;

        #endregion Private Fields

        #region Public Constructors

        public Sly4BHLoadRemovalSettings(LiveSplitState state)
        {
            InitializeComponent();

#if DEBUG
            chkSaveDetectionLog.Checked = SaveDetectionLog;
#endif
            UpdateBlacklevelLabel();
            AllGameAutoSplitSettings = new Dictionary<string, XmlElement>();
            dynamicAutoSplitterControls = new List<Control>();
            CreateAutoSplitControls(state);
            liveSplitState = state;
            initImageCaptureInfo();
            lblVersion.Text = "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);


            RefreshCaptureWindowList();
            DrawPreview();

            livePreviewTimer = new Timer();
            livePreviewTimer.Interval = LivePreviewIntervalMs;
            livePreviewTimer.Tick += LivePreviewTimer_Tick;
            UpdateLivePreviewTimer();
        }

        // Only runs for a live source. Screens and windows keep their existing behaviour, where the
        // preview updates when the user asks for it.
        private void UpdateLivePreviewTimer()
        {
            if (livePreviewTimer != null)
            {
                livePreviewTimer.Enabled = captureSource == CaptureSource.VideoCapture;
            }

            if (captureSource == CaptureSource.VideoCapture)
            {
                videoPreviewPending = true;
            }
        }

        private void LivePreviewTimer_Tick(object sender, EventArgs e)
        {
            // LiveSplit keeps this control alive after the layout editor is closed, so without this
            // the timer would go on working for a panel nobody is looking at.
            if (!Visible)
            {
                return;
            }

            // Cheap - reads a string the capture thread maintains.
            UpdateCaptureStatusLabel();

            // Drawn *once*, when the device starts delivering, then left alone. Redrawing every tick
            // is a full decode plus two resizes competing with detection, for a picture that is only
            // there to draw a crop on - and the crop does not move. Use Update Preview for a fresh one.
            if (videoPreviewPending && HasVideoFrame())
            {
                videoPreviewPending = false;
                DrawPreview();
            }
        }

        private bool HasVideoFrame()
        {
            lock (videoSourceLock)
            {
                return videoSource != null && videoSource.FrameWidth > 0;
            }
        }

        #endregion Public Constructors

        #region Public Methods

        // Returns the user's whole selected crop (i.e. the full game feed), scaled to resizeSize.
        // Detection subdivides this by fraction internally, so it must be the *entire* crop with no
        // extra offset applied - which is exactly what CaptureImageFullPreview(useCrop: true) produces,
        // and why this reuses that path rather than duplicating the capture math.
        public Bitmap CaptureImage()
        {
            ImageCaptureInfo captureInfo = imageCaptureInfo;
            captureInfo.captureSizeX = resizeSize.Width;
            captureInfo.captureSizeY = resizeSize.Height;

            return CaptureImageFullPreview(ref captureInfo, useCrop: true);
        }

        public Bitmap CaptureImageFullPreview(ref ImageCaptureInfo imageCaptureInfo, bool useCrop = false)
        {
            Bitmap b = new Bitmap(1, 1);

            if (captureSource == CaptureSource.VideoCapture)
            {
                return CaptureFromVideoDevice(ref imageCaptureInfo, useCrop) ?? b;
            }

            // Negative index means a screen, non-negative a window - see RefreshCaptureWindowList.
            if (processCaptureIndex < 0)
            {
                Screen selected_screen = Screen.AllScreens[-processCaptureIndex - 1];
                Rectangle screenRect = selected_screen.Bounds;

                screenRect.Width = (int)(screenRect.Width * scalingValueFloat);
                screenRect.Height = (int)(screenRect.Height * scalingValueFloat);

                Point screenCenter = new Point((int)(screenRect.Width / 2.0f), (int)(screenRect.Height / 2.0f));

                if (useCrop)
                {
                    screenRect.Width = (int)(imageCaptureInfo.crop_coordinate_right - imageCaptureInfo.crop_coordinate_left);
                    screenRect.Height = (int)(imageCaptureInfo.crop_coordinate_bottom - imageCaptureInfo.crop_coordinate_top);
                }

                ImageCapture.SizeAdjustedCropAndOffset(screenRect.Width, screenRect.Height, ref imageCaptureInfo);

                imageCaptureInfo.actual_crop_size_x = 2 * imageCaptureInfo.center_of_frame_x;
                imageCaptureInfo.actual_crop_size_y = 2 * imageCaptureInfo.center_of_frame_y;

                if (useCrop)
                {
                    imageCaptureInfo.center_of_frame_x += imageCaptureInfo.crop_coordinate_left;
                    imageCaptureInfo.center_of_frame_y += imageCaptureInfo.crop_coordinate_top;
                }

                // Screen bounds are desktop-absolute, so a secondary monitor needs its origin added.
                imageCaptureInfo.center_of_frame_x += selected_screen.Bounds.X;
                imageCaptureInfo.center_of_frame_y += selected_screen.Bounds.Y;

                b = ImageCapture.CaptureFromDisplay(ref imageCaptureInfo);
            }
            else
            {
                IntPtr handle = new IntPtr(0);

                if (processCaptureIndex >= processList.Length)
                    return b;

                if (processCaptureIndex != -1)
                {
                    handle = processList[processCaptureIndex].MainWindowHandle;
                }
                //Capture from specific process
                processList[processCaptureIndex].Refresh();
                if ((int)handle == 0)
                    return b;

                b = ImageCapture.PrintWindow(handle, ref imageCaptureInfo, full: true, useCrop: useCrop, scalingValueFloat: scalingValueFloat);
            }

            return b;
        }

        // Capture from the selected video device.
        //
        // Far simpler than the screen and window paths because the frame arrives as a Bitmap at the
        // device's own resolution: there is no DC to blit and no DPI or scaling to undo, so the crop is
        // a plain sub-rectangle. `scalingValueFloat` deliberately does not apply here - it exists to
        // undo Windows display scaling, which a capture card knows nothing about.
        //
        // The aspect-ratio correction SizeAdjustedCropAndOffset does for screens is also skipped. It
        // exists to find a 16:9 region inside a differently-shaped desktop; a capture device delivers
        // the signal it is given, and the user crops to the game feed by hand exactly as for the other
        // sources.
        private Bitmap CaptureFromVideoDevice(ref ImageCaptureInfo info, bool useCrop)
        {
            VideoCaptureSource source = EnsureVideoSource();
            if (source == null)
            {
                return null;
            }

            int frameWidth = source.FrameWidth;
            int frameHeight = source.FrameHeight;
            if (frameWidth <= 0 || frameHeight <= 0)
            {
                return null;    // graph still coming up
            }

            // DrawPreview scales the user's selection rectangle by these, so they have to describe
            // the frame the *uncropped* preview showed.
            info.actual_crop_size_x = frameWidth;
            info.actual_crop_size_y = frameHeight;
            info.center_of_frame_x = frameWidth / 2.0f;
            info.center_of_frame_y = frameHeight / 2.0f;
            info.actual_offset_x = 0;
            info.actual_offset_y = 0;

            Rectangle wanted = new Rectangle(0, 0, frameWidth, frameHeight);

            if (useCrop)
            {
                Rectangle crop = Rectangle.FromLTRB(
                    (int)info.crop_coordinate_left, (int)info.crop_coordinate_top,
                    (int)info.crop_coordinate_right, (int)info.crop_coordinate_bottom);

                // A crop that has never been drawn, or one left over from a source with a different
                // resolution, can fall outside this frame entirely - fall back to the whole frame
                // rather than throwing out of the detection loop.
                crop.Intersect(wanted);
                if (crop.Width > 0 && crop.Height > 0)
                {
                    wanted = crop;
                    info.actual_crop_size_x = crop.Width;
                    info.actual_crop_size_y = crop.Height;
                }
            }

            // Crop and downscale in one pass; see VideoCaptureSource.CaptureScaled for why.
            //
            // Note this means the video path and the display path resample differently. That is safe
            // because they are separate capture pipelines, calibrated and fixtured separately anyway,
            // and it is why the display path was left on GDI+.
            return source.CaptureScaled(wanted, info.captureSizeX, info.captureSizeY);
        }

        // Starts the selected device if it isn't already running, and hands back the running source.
        // Null when nothing is selected; the source itself reports null frames while the graph is
        // still coming up, which callers treat as "nothing to show yet" rather than an error - a
        // capture card can take a second or two to deliver its first frame.
        private VideoCaptureSource EnsureVideoSource()
        {
            lock (videoSourceLock)
            {
                if (string.IsNullOrEmpty(selectedVideoMoniker))
                {
                    return null;
                }

                if (videoSource != null && videoSource.MonikerName != selectedVideoMoniker)
                {
                    videoSource.Dispose();
                    videoSource = null;
                }

                if (videoSource == null)
                {
                    videoSource = new VideoCaptureSource(selectedVideoMoniker);
                }

                return videoSource;
            }
        }

        // Releases the device. Called when the source mode changes and when the component is disposed -
        // holding a capture card open would stop OBS or anything else from using it.
        public void StopVideoCapture()
        {
            lock (videoSourceLock)
            {
                if (videoSource != null)
                {
                    videoSource.Dispose();
                    videoSource = null;
                }
            }
        }

        private string VideoCaptureStatus()
        {
            lock (videoSourceLock)
            {
                if (string.IsNullOrEmpty(selectedVideoMoniker))
                {
                    return videoDevices.Count == 0 ? "no capture devices found" : "no device selected";
                }

                return videoSource == null ? "not started" : videoSource.Status;
            }
        }

        public void ChangeAutoSplitSettingsToGameName(string gameName, string category)
        {
            gameName = removeInvalidXMLCharacters(gameName);
            category = removeInvalidXMLCharacters(category);

            //TODO: go through gameSettings to see if the game matches, enter info based on that.
            foreach (var control in dynamicAutoSplitterControls)
            {
                tabPage2.Controls.Remove(control);
            }

            dynamicAutoSplitterControls.Clear();

            XmlDocument document = new XmlDocument();

            var gameNode = document.CreateElement(autoSplitData.GameName + autoSplitData.Category);

            foreach (AutoSplitEntry splitEntry in autoSplitData.SplitData)
            {
                gameNode.AppendChild(ToElement(document, splitEntry.SplitName, splitEntry.NumberOfLoads));
            }


            AllGameAutoSplitSettings[autoSplitData.GameName + autoSplitData.Category] = gameNode;

            CreateAutoSplitControls(liveSplitState);

            foreach (var gameSettings in AllGameAutoSplitSettings)
            {
                if (gameSettings.Key == gameName + category)
                {
                    var game_element = gameSettings.Value;

                    Dictionary<string, int> usedSplitNames = new Dictionary<string, int>();
                    foreach (XmlElement number_of_loads in game_element)
                    {
                        var up_down_controls = tabPage2.Controls.Find(number_of_loads.LocalName, true);

                        if (usedSplitNames.ContainsKey(number_of_loads.LocalName) == false)
                        {
                            usedSplitNames[number_of_loads.LocalName] = 0;
                        }
                        else
                        {
                            usedSplitNames[number_of_loads.LocalName]++;
                        }

                        NumericUpDown up_down = (NumericUpDown)up_down_controls[usedSplitNames[number_of_loads.LocalName]];

                        if (up_down != null)
                        {
                            up_down.Value = Convert.ToInt32(number_of_loads.InnerText);
                        }
                    }

                }
            }
        }
        public int GetCumulativeNumberOfLoadsForSplit(string splitName)
        {
            int numberOfLoads = 0;
            splitName = removeInvalidXMLCharacters(splitName);
            foreach (AutoSplitEntry entry in autoSplitData.SplitData)
            {
                numberOfLoads += entry.NumberOfLoads;
                if (entry.SplitName == splitName)
                {
                    return numberOfLoads;
                }
            }
            return numberOfLoads;
        }

        public int GetAutoSplitNumberOfLoadsForSplit(string splitName)
        {
            splitName = removeInvalidXMLCharacters(splitName);
            foreach (AutoSplitEntry entry in autoSplitData.SplitData)
            {
                if (entry.SplitName == splitName)
                {
                    return entry.NumberOfLoads;
                }
            }

            //This should never happen, but might if the splits are changed without reloading the component...
            return 2;
        }

        public XmlNode GetSettings(XmlDocument document)
        {
            var settingsNode = document.CreateElement("Settings");

            settingsNode.AppendChild(ToElement(document, "Version", Assembly.GetExecutingAssembly().GetName().Version.ToString(3)));


            // The screen/window selection. Read from the dropdown only in Display mode - in
            // VideoCapture mode the dropdown holds devices, so indexing captureIDs by it would save
            // whichever screen or window happened to sit at that position. The remembered value is
            // written out instead, so switching back later restores the right one.
            string captureTitle = selectedCaptureID;
            if (captureSource == CaptureSource.Display && captureIDs != null &&
                processListComboBox.SelectedIndex >= 0 && processListComboBox.SelectedIndex < captureIDs.Count)
            {
                captureTitle = captureIDs[processListComboBox.SelectedIndex];
            }

            if (!string.IsNullOrEmpty(captureTitle))
            {
                settingsNode.AppendChild(ToElement(document, "SelectedCaptureTitle", captureTitle));
            }

            settingsNode.AppendChild(ToElement(document, "CaptureSource", captureSource.ToString()));
            settingsNode.AppendChild(ToElement(document, "SelectedVideoDevice", selectedVideoName ?? ""));
            settingsNode.AppendChild(ToElement(document, "SelectedVideoDeviceMoniker", selectedVideoMoniker ?? ""));

            settingsNode.AppendChild(ToElement(document, "blacklevel", blacklevel));
            settingsNode.AppendChild(ToElement(document, "HasCalibration", hasCalibration));

            settingsNode.AppendChild(ToElement(document, "ScalingPercent", trackBar1.Value));

            var captureRegionNode = document.CreateElement("CaptureRegion");

            captureRegionNode.AppendChild(ToElement(document, "X", selectionRectanglePreviewBox.X));
            captureRegionNode.AppendChild(ToElement(document, "Y", selectionRectanglePreviewBox.Y));
            captureRegionNode.AppendChild(ToElement(document, "Width", selectionRectanglePreviewBox.Width));
            captureRegionNode.AppendChild(ToElement(document, "Height", selectionRectanglePreviewBox.Height));

            settingsNode.AppendChild(captureRegionNode);

            settingsNode.AppendChild(ToElement(document, "AutoSplitEnabled", enableAutoSplitterChk.Checked));
            settingsNode.AppendChild(ToElement(document, "AutoSplitDisableOnSkipUntilSplit", chkAutoSplitterDisableOnSkip.Checked));
            // The field, not the checkbox - a Release build has no checkbox but must still write back
            // whatever the layout came in with.
            settingsNode.AppendChild(ToElement(document, "SaveDetectionLog", SaveDetectionLog));

            var splitsNode = document.CreateElement("AutoSplitGames");

            foreach (var gameSettings in AllGameAutoSplitSettings)
            {
                if (gameSettings.Key != autoSplitData.GameName + autoSplitData.Category)
                {
                    XmlNode node = document.ImportNode(gameSettings.Value, true);
                    splitsNode.AppendChild(node);
                }
            }

            var gameNode = document.CreateElement(autoSplitData.GameName + autoSplitData.Category);

            foreach (AutoSplitEntry splitEntry in autoSplitData.SplitData)
            {
                gameNode.AppendChild(ToElement(document, splitEntry.SplitName, splitEntry.NumberOfLoads));
            }
            AllGameAutoSplitSettings[autoSplitData.GameName + autoSplitData.Category] = gameNode;
            splitsNode.AppendChild(gameNode);
            settingsNode.AppendChild(splitsNode);

            return settingsNode;
        }

        public void SetSettings(XmlNode settings)
        {
            var element = (XmlElement)settings;
            if (!element.IsEmpty)
            {
                // Read the video selection before the source mode, so that when the mode turns out to
                // be VideoCapture the refresh below already knows which device to re-select.
                if (element["SelectedVideoDevice"] != null)
                {
                    selectedVideoName = element["SelectedVideoDevice"].InnerText;
                }

                if (element["SelectedVideoDeviceMoniker"] != null)
                {
                    selectedVideoMoniker = element["SelectedVideoDeviceMoniker"].InnerText;
                }

                if (element["CaptureSource"] != null &&
                    element["CaptureSource"].InnerText == CaptureSource.VideoCapture.ToString())
                {
                    captureSource = CaptureSource.VideoCapture;
                    radioVideoCapture.Checked = true;
                }
                else
                {
                    captureSource = CaptureSource.Display;
                    radioDisplay.Checked = true;
                }

                if (element["SelectedCaptureTitle"] != null)
                {
                    String selectedCaptureTitle = element["SelectedCaptureTitle"].InnerText;
                    selectedCaptureID = selectedCaptureTitle;
                    UpdateIndexToCaptureID();
                }

                UpdateLivePreviewTimer();
                RefreshCaptureWindowList();

                // Deliberately gated on HasCalibration, which only layouts saved by this version carry.
                //
                // Older layouts do have a blacklevel, but it is not the same measurement: the
                // reference patch moved from above the mask (cols [120,160) x rows [30,50)) to beside
                // it (cols [40,80) x rows [120,160)), and the two read differently on the same
                // capture - 3 against 0 on the live test set. Reading a stale value back would apply a
                // wrong threshold silently, so those layouts correctly come back as uncalibrated and
                // the user recalibrates once.
                if (element["HasCalibration"] != null)
                {
                    hasCalibration = Convert.ToBoolean(element["HasCalibration"].InnerText);

                    if (hasCalibration && element["blacklevel"] != null)
                    {
                        blacklevel = Convert.ToInt32(element["blacklevel"].InnerText);
                    }
                }

                if (element["ScalingPercent"] != null)
                {
                    trackBar1.Value = Convert.ToInt32(element["ScalingPercent"].InnerText);
                }

                if (element["CaptureRegion"] != null)
                {
                    var element_region = element["CaptureRegion"];
                    if (element_region["X"] != null && element_region["Y"] != null && element_region["Width"] != null && element_region["Height"] != null)
                    {
                        int captureRegionX = Convert.ToInt32(element_region["X"].InnerText);
                        int captureRegionY = Convert.ToInt32(element_region["Y"].InnerText);
                        int captureRegionWidth = Convert.ToInt32(element_region["Width"].InnerText);
                        int captureRegionHeight = Convert.ToInt32(element_region["Height"].InnerText);

                        selectionRectanglePreviewBox = new Rectangle(captureRegionX, captureRegionY, captureRegionWidth, captureRegionHeight);
                        selectionTopLeft = new Point(captureRegionX, captureRegionY);
                        selectionBottomRight = new Point(captureRegionX + captureRegionWidth, captureRegionY + captureRegionHeight);
                    }
                }

                if (element["AutoSplitEnabled"] != null)
                {
                    enableAutoSplitterChk.Checked = Convert.ToBoolean(element["AutoSplitEnabled"].InnerText);
                }

                if (element["AutoSplitDisableOnSkipUntilSplit"] != null)
                {
                    chkAutoSplitterDisableOnSkip.Checked = Convert.ToBoolean(element["AutoSplitDisableOnSkipUntilSplit"].InnerText);
                }

                if (element["SaveDetectionLog"] != null)
                {
                    SaveDetectionLog = Convert.ToBoolean(element["SaveDetectionLog"].InnerText);
#if DEBUG
                    chkSaveDetectionLog.Checked = SaveDetectionLog;
#endif
                }

                if (element["AutoSplitGames"] != null)
                {
                    var auto_split_element = element["AutoSplitGames"];

                    foreach (XmlElement game in auto_split_element)
                    {
                        if (game.LocalName != autoSplitData.GameName)
                        {
                            AllGameAutoSplitSettings[game.LocalName] = game;
                        }
                    }

                    if (auto_split_element[autoSplitData.GameName + autoSplitData.Category] != null)
                    {
                        var game_element = auto_split_element[autoSplitData.GameName + autoSplitData.Category];
                        AllGameAutoSplitSettings[autoSplitData.GameName + autoSplitData.Category] = game_element;
                        Dictionary<string, int> usedSplitNames = new Dictionary<string, int>();
                        foreach (XmlElement number_of_loads in game_element)
                        {
                            var up_down_controls = tabPage2.Controls.Find(number_of_loads.LocalName, true);

                            //This can happen if the layout was not saved and contains old splits.
                            if (up_down_controls == null || up_down_controls.Length == 0)
                            {
                                continue;
                            }

                            if (usedSplitNames.ContainsKey(number_of_loads.LocalName) == false)
                            {
                                usedSplitNames[number_of_loads.LocalName] = 0;
                            }
                            else
                            {
                                usedSplitNames[number_of_loads.LocalName]++;
                            }

                            NumericUpDown up_down = (NumericUpDown)up_down_controls[usedSplitNames[number_of_loads.LocalName]];

                            if (up_down != null)
                            {
                                up_down.Value = Convert.ToInt32(number_of_loads.InnerText);
                            }
                        }
                    }
                }

                DrawPreview();
            }
            UpdateBlacklevelLabel();
        }

        #endregion Public Methods

        #region Private Methods

        private void AutoSplitUpDown_ValueChanged(object sender, EventArgs e, string splitName)
        {
            foreach (AutoSplitEntry entry in autoSplitData.SplitData)
            {
                if (entry.SplitName == splitName)
                {
                    entry.NumberOfLoads = (int)((NumericUpDown)sender).Value;
                    return;
                }
            }
        }


        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (captureSource == CaptureSource.VideoCapture)
            {
                int index = processListComboBox.SelectedIndex;
                if (index >= 0 && index < videoDevices.Count)
                {
                    VideoCaptureDeviceInfo device = videoDevices[index];
                    if (device.MonikerName != selectedVideoMoniker)
                    {
                        // EnsureVideoSource restarts the graph on the next capture; stopping here
                        // means the previously selected card is released straight away.
                        StopVideoCapture();
                        selectedVideoMoniker = device.MonikerName;
                        selectedVideoName = device.Name;
                        videoPreviewPending = true;
                    }
                }

                UpdateCaptureStatusLabel();
                DrawPreview();
                return;
            }

            if (processListComboBox.SelectedIndex < numScreens)
            {
                processCaptureIndex = -processListComboBox.SelectedIndex - 1;
            }
            else
            {
                processCaptureIndex = processListComboBox.SelectedIndex - numScreens;
            }

            selectionRectanglePreviewBox = new Rectangle(selectionTopLeft.X, selectionTopLeft.Y, selectionBottomRight.X - selectionTopLeft.X, selectionBottomRight.Y - selectionTopLeft.Y);

            DrawPreview();
        }

        private void CreateAutoSplitControls(LiveSplitState state)
        {
            autoSplitCategoryLbl.Text = "Category: " + state.Run.CategoryName;
            autoSplitNameLbl.Text = "Game: " + state.Run.GameName;

            int splitOffsetY = 95;
            int splitSpacing = 50;

            int splitCounter = 0;
            autoSplitData = new AutoSplitData(removeInvalidXMLCharacters(state.Run.GameName), removeInvalidXMLCharacters(state.Run.CategoryName));

            foreach (var split in state.Run)
            {
                var autoSplitPanel = new System.Windows.Forms.Panel();
                var autoSplitLbl = new System.Windows.Forms.Label();
                var autoSplitUpDown = new System.Windows.Forms.NumericUpDown();

                autoSplitUpDown.Value = 2;
                autoSplitPanel.BorderStyle = System.Windows.Forms.BorderStyle.Fixed3D;
                autoSplitPanel.Controls.Add(autoSplitUpDown);
                autoSplitPanel.Controls.Add(autoSplitLbl);
                autoSplitPanel.Location = new System.Drawing.Point(28, splitOffsetY + splitSpacing * splitCounter);
                autoSplitPanel.Size = new System.Drawing.Size(409, 39);

                autoSplitLbl.AutoSize = true;
                autoSplitLbl.Location = new System.Drawing.Point(3, 10);
                autoSplitLbl.Size = new System.Drawing.Size(199, 13);
                autoSplitLbl.TabIndex = 0;
                autoSplitLbl.Text = split.Name;

                autoSplitUpDown.Location = new System.Drawing.Point(367, 8);
                autoSplitUpDown.Size = new System.Drawing.Size(35, 20);
                autoSplitUpDown.TabIndex = 1;

                //Sanitize to a legal XML element name so SetSettings can find the control by name.
                autoSplitUpDown.Name = removeInvalidXMLCharacters(split.Name);

                autoSplitUpDown.ValueChanged += (s, e) => AutoSplitUpDown_ValueChanged(autoSplitUpDown, e, removeInvalidXMLCharacters(split.Name));

                tabPage2.Controls.Add(autoSplitPanel);

                autoSplitData.SplitData.Add(new AutoSplitEntry(removeInvalidXMLCharacters(split.Name), 2));
                dynamicAutoSplitterControls.Add(autoSplitPanel);
                splitCounter++;
            }
        }

        private void DrawCaptureRectangleBitmap()
        {
            Bitmap capture_image = (Bitmap)previewImage.Clone();
            using (Graphics g = Graphics.FromImage(capture_image))
            {
                Pen drawing_pen = new Pen(Color.Magenta, 8.0f);
                drawing_pen.Alignment = PenAlignment.Inset;
                g.DrawRectangle(drawing_pen, selectionRectanglePreviewBox);
            }

            // Each of these bitmaps holds a GDI handle, and the process only gets about 10,000 of
            // them. That did not matter while previews were only redrawn when the user clicked
            // something; with the live timer running at 2Hz it would exhaust them.
            Image previous = previewPictureBox.Image;
            previewPictureBox.Image = capture_image;
            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private void DrawPreview()
        {
            try
            {
                ImageCaptureInfo copy = imageCaptureInfo;
                copy.captureSizeX = previewPictureBox.Width;
                copy.captureSizeY = previewPictureBox.Height;

                Bitmap previousPreview = previewImage;
                previewImage = CaptureImageFullPreview(ref copy);
                if (previousPreview != null)
                {
                    // Safe to release here: DrawCaptureRectangleBitmap clones it rather than handing
                    // it to the PictureBox, so nothing else is still holding this one.
                    previousPreview.Dispose();
                }

                float crop_size_x = copy.actual_crop_size_x;
                float crop_size_y = copy.actual_crop_size_y;

                DrawCaptureRectangleBitmap();

                // Selection rectangle is in preview-box pixels; scale it back up to raw capture
                // pixels, which is what actual_crop_size reports.
                imageCaptureInfo.crop_coordinate_left = selectionRectanglePreviewBox.Left * (crop_size_x / previewPictureBox.Width);
                imageCaptureInfo.crop_coordinate_right = selectionRectanglePreviewBox.Right * (crop_size_x / previewPictureBox.Width);
                imageCaptureInfo.crop_coordinate_top = selectionRectanglePreviewBox.Top * (crop_size_y / previewPictureBox.Height);
                imageCaptureInfo.crop_coordinate_bottom = selectionRectanglePreviewBox.Bottom * (crop_size_y / previewPictureBox.Height);

                copy.crop_coordinate_left = selectionRectanglePreviewBox.Left * (crop_size_x / previewPictureBox.Width);
                copy.crop_coordinate_right = selectionRectanglePreviewBox.Right * (crop_size_x / previewPictureBox.Width);
                copy.crop_coordinate_top = selectionRectanglePreviewBox.Top * (crop_size_y / previewPictureBox.Height);
                copy.crop_coordinate_bottom = selectionRectanglePreviewBox.Bottom * (crop_size_y / previewPictureBox.Height);

                Bitmap full_cropped_capture = CaptureImageFullPreview(ref copy, useCrop: true);
                Image previousCropped = croppedPreviewPictureBox.Image;
                croppedPreviewPictureBox.Image = full_cropped_capture;
                if (previousCropped != null)
                {
                    previousCropped.Dispose();
                }

                // The device reports its resolution only once the graph is up, which is a moment after
                // it is selected, so the label is refreshed on every preview rather than once on
                // selection.
                UpdateCaptureStatusLabel();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.ToString());
            }
        }

        private void enableAutoSplitterChk_CheckedChanged(object sender, EventArgs e)
        {
            AutoSplitterEnabled = enableAutoSplitterChk.Checked;
        }

        private void initImageCaptureInfo()
        {
            imageCaptureInfo = new ImageCaptureInfo();

            selectionTopLeft = new Point(0, 0);
            selectionBottomRight = new Point(previewPictureBox.Width, previewPictureBox.Height);
            selectionRectanglePreviewBox = new Rectangle(selectionTopLeft.X, selectionTopLeft.Y, selectionBottomRight.X - selectionTopLeft.X, selectionBottomRight.Y - selectionTopLeft.Y);

            imageCaptureInfo.featureVectorResolutionX = featureVectorResolutionX;
            imageCaptureInfo.featureVectorResolutionY = featureVectorResolutionY;
            // Every caller sets captureSizeX/Y before use - CaptureImage to resizeSize, DrawPreview to
            // the preview box - so the value here only has to be non-degenerate.
            imageCaptureInfo.captureSizeX = resizeSize.Width;
            imageCaptureInfo.captureSizeY = resizeSize.Height;
            imageCaptureInfo.captureAspectRatio = captureAspectRatioX / captureAspectRatioY;
        }

        private void previewPictureBox_MouseDown(object sender, MouseEventArgs e)
        {
            SetRectangleFromMouse(e);
            DrawPreview();
        }

        private void previewPictureBox_MouseMove(object sender, MouseEventArgs e)
        {
            SetRectangleFromMouse(e);
            if (drawingPreview == false)
            {
                drawingPreview = true;
                DrawCaptureRectangleBitmap();
                drawingPreview = false;
            }
        }

        private void previewPictureBox_MouseUp(object sender, MouseEventArgs e)
        {
            SetRectangleFromMouse(e);
            DrawPreview();
        }

        private void processListComboBox_DropDown(object sender, EventArgs e)
        {
            RefreshCaptureWindowList();
        }

        // Swaps what the dropdown lists. The two modes are entirely separate selections - each keeps
        // its own, so flipping back and forth doesn't lose the other one.
        private void CaptureSourceRadio_CheckedChanged(object sender, EventArgs e)
        {
            CaptureSource wanted = radioVideoCapture.Checked
                ? CaptureSource.VideoCapture
                : CaptureSource.Display;

            if (wanted == captureSource)
            {
                return;
            }

            captureSource = wanted;

            if (captureSource != CaptureSource.VideoCapture)
            {
                StopVideoCapture();
            }

            UpdateLivePreviewTimer();
            RefreshCaptureWindowList();
            DrawPreview();
        }

        private void RefreshCaptureWindowList()
        {
            if (captureSource == CaptureSource.VideoCapture)
            {
                RefreshVideoDeviceList();
                return;
            }

            try
            {
                Process[] processListtmp = Process.GetProcesses();
                List<Process> processes_with_name = new List<Process>();

                if (captureIDs != null)
                {
                    if (processListComboBox.SelectedIndex < captureIDs.Count && processListComboBox.SelectedIndex >= 0)
                    {
                        selectedCaptureID = processListComboBox.SelectedItem.ToString();
                    }
                }

                captureIDs = new List<string>();

                processListComboBox.Items.Clear();
                numScreens = 0;
                foreach (var screen in Screen.AllScreens)
                {
                    processListComboBox.Items.Add("Screen: " + screen.DeviceName + ", " + screen.Bounds.ToString());
                    captureIDs.Add("Screen: " + screen.DeviceName);
                    numScreens++;
                }
                foreach (Process process in processListtmp)
                {
                    if (!String.IsNullOrEmpty(process.MainWindowTitle))
                    {
                        processListComboBox.Items.Add(process.ProcessName + ": " + process.MainWindowTitle);
                        captureIDs.Add(process.ProcessName);
                        processes_with_name.Add(process);
                    }
                }

                UpdateIndexToCaptureID();

                processList = processes_with_name.ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.ToString());
            }
        }

        // Fills the dropdown with the video capture devices Windows currently reports, and re-selects
        // whatever was saved. Devices come and go (a card is unplugged, a virtual camera appears), so
        // this runs every time the dropdown is opened as well as on load.
        private void RefreshVideoDeviceList()
        {
            try
            {
                videoDevices = VideoCaptureDevices.Enumerate();

                processListComboBox.Items.Clear();
                foreach (VideoCaptureDeviceInfo device in videoDevices)
                {
                    processListComboBox.Items.Add(device.Name);
                }

                VideoCaptureDeviceInfo selected =
                    VideoCaptureDevices.Resolve(videoDevices, selectedVideoMoniker, selectedVideoName);

                if (selected != null)
                {
                    // Resolve may have matched on the friendly name after the device moved ports, in
                    // which case the saved moniker is stale and has to be replaced.
                    selectedVideoMoniker = selected.MonikerName;
                    selectedVideoName = selected.Name;
                    processListComboBox.SelectedIndex = videoDevices.IndexOf(selected);
                }
                else if (videoDevices.Count > 0 && string.IsNullOrEmpty(selectedVideoMoniker))
                {
                    processListComboBox.SelectedIndex = 0;
                }

                UpdateCaptureStatusLabel();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.ToString());
            }
        }

        private void UpdateCaptureStatusLabel()
        {
            if (captureSource != CaptureSource.VideoCapture)
            {
                captureStatusLabel.Text = "";
                return;
            }

            captureStatusLabel.Text = VideoCaptureStatus();
        }

        public string removeInvalidXMLCharacters(string in_string)
        {
            if (in_string == null) return null;

            StringBuilder sbOutput = new StringBuilder();
            char ch;

            bool was_other_char = false;

            for (int i = 0; i < in_string.Length; i++)
            {
                ch = in_string[i];

                if ((ch >= 0x0 && ch <= 0x2F) ||
                    (ch >= 0x3A && ch <= 0x40) ||
                    (ch >= 0x5B && ch <= 0x60) ||
                    (ch >= 0x7B)
                    )
                {
                    continue;
                }

                //Can't start with a number.
                if (was_other_char == false && ch >= '0' && ch <= '9')
                {
                    continue;
                }

                sbOutput.Append(ch);
                was_other_char = true;
            }

            if (sbOutput.Length == 0)
            {
                sbOutput.Append("NULL");
            }

            return sbOutput.ToString();
        }

        private void SetRectangleFromMouse(MouseEventArgs e)
        {
            int x = Math.Min(Math.Max(0, e.Location.X), previewPictureBox.Width);
            int y = Math.Min(Math.Max(0, e.Location.Y), previewPictureBox.Height);

            if (e.Button == MouseButtons.Left
                && (selectionRectanglePreviewBox.Left + selectionRectanglePreviewBox.Width) - x > 0
                && (selectionRectanglePreviewBox.Top + selectionRectanglePreviewBox.Height) - y > 0)
            {
                selectionTopLeft = new Point(x, y);
            }
            else if (e.Button == MouseButtons.Right && x - selectionRectanglePreviewBox.Left > 0 && y - selectionRectanglePreviewBox.Top > 0)
            {
                selectionBottomRight = new Point(x, y);
            }

            selectionRectanglePreviewBox = new Rectangle(selectionTopLeft.X, selectionTopLeft.Y, selectionBottomRight.X - selectionTopLeft.X, selectionBottomRight.Y - selectionTopLeft.Y);
        }

        private XmlElement ToElement<T>(XmlDocument document, String name, T value)
        {
            var element = document.CreateElement(name);
            IFormattable formattable = value as IFormattable;
            if (formattable != null)
            {
                element.InnerText = formattable.ToString(null, CultureInfo.InvariantCulture);
            }
            else
            {
                element.InnerText = value.ToString();
            }
            return element;
        }

        private void trackBar1_ValueChanged(object sender, EventArgs e)
        {
            scalingValue = trackBar1.Value;

            if (scalingValue % trackBar1.SmallChange != 0)
            {
                scalingValue = (scalingValue / trackBar1.SmallChange) * trackBar1.SmallChange;

                trackBar1.Value = scalingValue;
            }

            scalingValueFloat = ((float)scalingValue) / 100.0f;

            scalingLabel.Text = "Scaling: " + trackBar1.Value.ToString() + "%";

            DrawPreview();
        }

        private void UpdateIndexToCaptureID()
        {
            int item_index = 0;
            for (item_index = 0; item_index < processListComboBox.Items.Count; item_index++)
            {
                String item = processListComboBox.Items[item_index].ToString();
                if (item.Contains(selectedCaptureID))
                {
                    processListComboBox.SelectedIndex = item_index;

                    break;
                }
            }
        }

        private void updatePreviewButton_Click(object sender, EventArgs e)
        {
            DrawPreview();
        }

        #endregion Private Methods

        private void chkAutoSplitterDisableOnSkip_CheckedChanged(object sender, EventArgs e)
        {
            AutoSplitterDisableOnSkipUntilSplit = chkAutoSplitterDisableOnSkip.Checked;
        }

#if DEBUG
        private void chkSaveDetectionLog_CheckedChanged(object sender, EventArgs e)
        {
            SaveDetectionLog = chkSaveDetectionLog.Checked;
        }


        // Dumps exactly what the detector sees, so a mismatch between the settings preview and the
        // detection input is immediately visible rather than having to be inferred from numbers.
        private void saveCutout_Click(object sender, EventArgs e)
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sly4BHDebugCaptures");
            Directory.CreateDirectory(folder);
            string stamp = DateTime.Now.ToString("HH_mm_ss");

            using (Bitmap fullCapture = CaptureImage())
            {
                fullCapture.Save(Path.Combine(folder, stamp + "_full.png"), ImageFormat.Png);

                // Also dump the two fixed regions detection actually reads, so it's immediately visible
                // whether they're landing where they should on this user's crop. The black patch must
                // be solid backdrop and the mask region must contain the whole mask.
                SaveRegion(fullCapture, FeatureDetector.BlackRegion, Path.Combine(folder, stamp + "_blackregion.png"));
                SaveRegion(fullCapture, MaskDetector.MaskRegion, Path.Combine(folder, stamp + "_maskregion.png"));
            }

            debugLabel.Text = "Saved debug captures to:\r\n" + folder;
        }

        private static void SaveRegion(Bitmap capture, Rectangle region, string path)
        {
            Rectangle clamped = Rectangle.Intersect(region, new Rectangle(0, 0, capture.Width, capture.Height));
            if (clamped.Width <= 0 || clamped.Height <= 0)
            {
                return;
            }

            using (Bitmap cropped = capture.Clone(clamped, capture.PixelFormat))
            {
                cropped.Save(path, ImageFormat.Png);
            }
        }
#endif

        private void CalibrateBlacklevelButton_Click(object sender, EventArgs e)
        {
            if (!isCalibrating)
            {
                // Start calibrating: the user should now get a real loading screen on screen while
                // this runs, so the reference patch is measured against genuine backdrop.
                calibrationRun = new CalibrationRun();
                isCalibrating = true;
                calibrateBlacklevelButton.Text = "Stop Calibrating";
            }
            else
            {
                isCalibrating = false;
                calibrateBlacklevelButton.Text = "Calibrate";
                FinishCalibration();
            }
            UpdateBlacklevelLabel();
        }

        // Called every frame by the component while isCalibrating is true.
        //
        // The mask measurement shown alongside the black level feeds into nothing - it is there so the
        // user can watch the detector react to the load screen and confirm the fixed regions are
        // landing on the right part of their crop.
        public void CalibrationTick()
        {
            using (Bitmap capture = CaptureImage())
            {
                FramePixels frame = new FramePixels(capture);
                CalibrationSample sample = calibrationRun.Observe(frame);

#if DEBUG
                debugLabel.Text =
                    "Calibrating - hold a loading screen on screen, then click Stop Calibrating.\r\n" +
                    "Capture: " + capture.Width + "x" + capture.Height + "\r\n" +
                    "Black patch " + FeatureDetector.BlackRegion + ": " + sample.FrameBlackLevel +
                    " this frame (lowest seen: " + sample.CalibratedBlackLevel + ")" +
                    (sample.Improved ? "  <- new minimum" : "") + "\r\n" +
                    "Mask region " + MaskDetector.MaskRegion + " (not calibrated - shown to check the crop)\r\n" +
                    sample.Mask;
#endif

                UpdateBlacklevelLabel();
            }
        }

        // The calibration detection runs against. Handed out as a copy rather than read field by field
        // by callers: this is a UserControl and so a MarshalByRefObject, where accessing members of a
        // struct field is unreliable (CS1690). The copy also keeps every check in a frame on one
        // consistent set of values even if the user finishes a calibration mid-frame.
        public Calibration GetCalibration()
        {
            Calibration calibration = default(Calibration);
            calibration.BlackLevel = blacklevel;
            calibration.HasCalibration = hasCalibration;
            return calibration;
        }

        private void FinishCalibration()
        {
            Calibration calibration;
            if (!calibrationRun.TryFinish(out calibration))
            {
                // Never saw a single frame - leave any previous calibration in place rather than
                // replacing it with nothing.
                return;
            }

            blacklevel = calibration.BlackLevel;
            hasCalibration = true;
        }

        // No-op in Release - there is no debug label to mirror the per-frame decision into. The method
        // itself stays so the component's reporting path doesn't have to be conditional as well.
        public void SetDebugText(string text)
        {
#if DEBUG
            debugLabel.Text = text;
#endif
        }

        public void UpdateBlacklevelLabel()
        {
            // Kept short on purpose - this label sits in a ~190px gap between its caption and the
            // Calibrate button, and AutoSize would otherwise grow it straight over the button. The
            // full instructions go to debugLabel, which has room for them.
            if (isCalibrating)
            {
                string blackLevelText = calibrationRun.BlackLevel == -1 ? "?" : calibrationRun.BlackLevel.ToString();
                blacklevelLabel.Text = "measuring... " + blackLevelText +
                                       " (" + calibrationRun.FrameCount + " frames)";
                // A black level that never comes down usually means the crop is including something
                // outside the game feed, so the reference patch isn't landing on the backdrop.
                blacklevelLabel.ForeColor = calibrationRun.BlackLevel > 60 ? Color.OrangeRed : Color.Black;
            }
            else if (!hasCalibration)
            {
                blacklevelLabel.Text = "NOT SET - calibrate first";
                blacklevelLabel.ForeColor = Color.Red;
            }
            else
            {
                blacklevelLabel.Text = "OK (black level " + blacklevel + ")";
                blacklevelLabel.ForeColor = Color.Black;
            }
        }

    }
    public class AutoSplitData
    {
        #region Public Fields

        public string Category;
        public string GameName;
        public List<AutoSplitEntry> SplitData;

        #endregion Public Fields

        #region Public Constructors

        public AutoSplitData(string gameName, string category)
        {
            SplitData = new List<AutoSplitEntry>();
            GameName = gameName;
            Category = category;
        }

        #endregion Public Constructors
    }

    public class AutoSplitEntry
    {
        #region Public Fields

        public int NumberOfLoads = 2;
        public string SplitName = "";

        #endregion Public Fields

        #region Public Constructors

        public AutoSplitEntry(string splitName, int numberOfLoads)
        {
            SplitName = splitName;
            NumberOfLoads = numberOfLoads;
        }

        #endregion Public Constructors
    }
}
