using LiveSplit.Model;
using System;
using System.Collections.Generic;
using System.Drawing;
using Sly4BHLoadDetector;
using System.IO;

namespace LiveSplit.UI.Components
{
    class Sly4BHLoadRemovalComponent : IComponent
    {
        public string ComponentName
        {
            get { return "Sly 4 / Hackpack Load Remover"; }
        }
        public float PaddingBottom { get { return 0; } }
        public float PaddingTop { get { return 0; } }
        public float PaddingLeft { get { return 0; } }
        public float PaddingRight { get { return 0; } }

        public IDictionary<string, Action> ContextMenuControls { get; protected set; }

        public Sly4BHLoadRemovalSettings settings { get; set; }

        // How often the per-frame detection state gets written to the detection log.
        private const int DebugLogEveryNFrames = 60;

        private bool isLoading = false;
        private bool rawMatchPrev = false;
        private int consecutiveMatchFrames = 0;

        // Per-load state for the autosplitter, reset at every load boundary and on timer reset.
        //
        // The game has two loading screens and only one of them is worth splitting on: an *area* load
        // shows the statistics along the bottom and happens at a consistent point in a run, a *plain*
        // load shows the mask alone and does not. Both still pause the timer - this only decides which
        // ones advance NumberOfLoadsPerSplit.
        //
        // Sticky rather than a single look at the moment the load starts. Measured on the fixtures the
        // statistics are already fully up before detection confirms the load (sequence\f00956), so a
        // rising-edge test would work today - but a load whose statistics appeared a few frames late
        // would silently stop counting, and two bools are cheaper than that failure mode.
        // countedThisLoad is what stops it counting the same load repeatedly.
        private bool sawStatsThisLoad = false;
        private bool countedThisLoad = false;

        private string lastDetectionDebug = "";
        private int debugLogFrameCounter = 0;

        // See MeasureUpdateRate. Milliseconds, -1 until the first pair of updates has been seen.
        private readonly System.Diagnostics.Stopwatch updateRateClock = System.Diagnostics.Stopwatch.StartNew();
        private long lastUpdateMs = -1;
        private long averageUpdateMs = -1;

        private TimerModel timer;
        private bool timerStarted = false;
        StreamWriter log_file_writer = null;

        private string GameName = "";
        private string GameCategory = "";
        private List<string> SplitNames;
        private LiveSplitState liveSplitState;
        private int framesSinceLastManualSplit = 0;
        private bool LastSplitSkip = false;

        private List<int> NumberOfLoadsPerSplit;

        public Sly4BHLoadRemovalComponent(LiveSplitState state)
        {

            GameName = state.Run.GameName;
            GameCategory = state.Run.CategoryName;
            SplitNames = new List<string>();

            foreach (var split in state.Run)
            {
                SplitNames.Add(split.Name);
            }

            liveSplitState = state;
            NumberOfLoadsPerSplit = new List<int>();
            InitNumberOfLoadsFromState();
            settings = new Sly4BHLoadRemovalSettings(state);
            timer = new TimerModel { CurrentState = state };
            timer.CurrentState.OnStart += timer_OnStart;
            timer.CurrentState.OnReset += timer_OnReset;
            timer.CurrentState.OnSkipSplit += timer_OnSkipSplit;
            timer.CurrentState.OnSplit += timer_OnSplit;
            timer.CurrentState.OnUndoSplit += timer_OnUndoSplit;
            timer.CurrentState.OnPause += timer_OnPause;
            timer.CurrentState.OnResume += timer_OnResume;
        }

        private void timer_OnResume(object sender, EventArgs e)
        {
            timerStarted = true;
        }

        private void timer_OnPause(object sender, EventArgs e)
        {
            timerStarted = false;
        }

        private void InitNumberOfLoadsFromState()
        {
            NumberOfLoadsPerSplit = new List<int>();
            NumberOfLoadsPerSplit.Clear();

            if (liveSplitState == null)
            {
                return;
            }

            foreach (var split in liveSplitState.Run)
            {
                NumberOfLoadsPerSplit.Add(0);
            }

            //Quicker way to prevent OOB on last split as I'm not sure if the index will go over if the run finishes
            NumberOfLoadsPerSplit.Add(99999);
        }

        private int CumulativeNumberOfLoadsForSplitIndex(int splitIndex)
        {
            int numberOfLoads = 0;

            for (int idx = 0; (idx < NumberOfLoadsPerSplit.Count && idx <= splitIndex); idx++)
            {
                numberOfLoads += NumberOfLoadsPerSplit[idx];
            }
            return numberOfLoads;
        }

        // The per-frame loading-screen test. The decision itself lives in LoadDetector so that the
        // offline test runner drives exactly this code rather than a copy of it; all that happens here
        // is fetching the calibration and reporting what came back.
        private DetectionResult DetectMask(Bitmap capture)
        {
            FramePixels frame = new FramePixels(capture);

            // Fetched through a method rather than read field by field - see GetCalibration.
            DetectionResult result = LoadDetector.Detect(frame, settings.GetCalibration());

            ReportDebug(result.Describe());
            return result;
        }

        // Mirrors the per-frame detection state to the settings panel (visible while the layout settings
        // dialog is open) and keeps a copy for the detection log, which is the only way to see what
        // happened during an actual run with that dialog closed.
        private void ReportDebug(string text)
        {
            lastDetectionDebug = text + "\r\n" + UpdateRateInfo();
            settings.SetDebugText(lastDetectionDebug);
        }

        private void CaptureLoads()
        {
            try
            {
                if (!timerStarted || settings.isCalibrating || !settings.hasCalibration)
                {
                    return;
                }

                framesSinceLastManualSplit++;
                MeasureUpdateRate();

                DetectionResult result;
                Bitmap capture = settings.CaptureImage();
                try
                {
                    result = DetectMask(capture);
                }
                finally
                {
                    capture.Dispose();
                }

                UpdateDebouncedState(result);

                // Periodic trace so the detection log shows what was happening even across frames where
                // nothing changed - without this a run that never detects anything logs nothing at all.
                debugLogFrameCounter++;
                if (debugLogFrameCounter >= DebugLogEveryNFrames)
                {
                    debugLogFrameCounter = 0;
                    Console.WriteLine("[trace] rawMatch=" + result.IsLoading + " isLoading=" + isLoading +
                                      " sawStats=" + sawStatsThisLoad + "\r\n" + lastDetectionDebug);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.ToString());
            }
        }

        // Rolling average of how often this component actually gets to run.
        //
        // Worth reporting because the debounce is counted in *these* updates, not in game frames, and
        // they are not the same thing: each one pays for a screen grab, a resize and a detection pass.
        // The user-visible pause latency is AutoSplitterJitterToleranceFrames divided by this rate, so
        // without it there is no way to tell a debounce that costs 50ms from one that costs 400ms.
        private void MeasureUpdateRate()
        {
            long now = updateRateClock.ElapsedMilliseconds;

            if (lastUpdateMs >= 0)
            {
                long delta = now - lastUpdateMs;
                // Exponential moving average - cheap, and it tracks a rate that changes when the
                // capture target is resized or another window starts competing for the GPU.
                averageUpdateMs = averageUpdateMs < 0 ? delta : (averageUpdateMs * 7 + delta) / 8;
            }

            lastUpdateMs = now;
        }

        private string UpdateRateInfo()
        {
            if (averageUpdateMs <= 0)
            {
                return "update rate: measuring...";
            }

            double fps = 1000.0 / averageUpdateMs;
            long lagMs = averageUpdateMs * settings.AutoSplitterJitterToleranceFrames;

            return "update rate: " + fps.ToString("0.0") + "/s (" + averageUpdateMs + "ms per frame)" +
                   "  ->  debounce costs " + lagMs + "ms at " +
                   settings.AutoSplitterJitterToleranceFrames + " frames";
        }

        // Only flips the loading state once `rawMatch` has agreed for AutoSplitterJitterToleranceFrames
        // consecutive frames, to reject single-frame noise - the same idea as the 3-frame debounce in
        // the Python proof of concept, generalized to a configurable frame count.
        private void UpdateDebouncedState(DetectionResult result)
        {
            bool rawMatch = result.IsLoading;

            if (rawMatch == rawMatchPrev)
            {
                consecutiveMatchFrames++;
            }
            else
            {
                consecutiveMatchFrames = 1;
                rawMatchPrev = rawMatch;
            }

            if (rawMatch != isLoading && consecutiveMatchFrames >= settings.AutoSplitterJitterToleranceFrames)
            {
                isLoading = rawMatch;
                timer.CurrentState.IsGameTimePaused = isLoading;

                sawStatsThisLoad = false;
                countedThisLoad = false;

                Console.WriteLine("[state] isLoading -> " + isLoading + " (IsGameTimePaused set)\r\n" +
                                  lastDetectionDebug);
            }

            if (!isLoading)
            {
                return;
            }

            // Note this reads the *current* frame, not only the one that flipped the state, so an area
            // load whose statistics appear a few frames in is still recognised.
            if (result.HasStats && !sawStatsThisLoad)
            {
                sawStatsThisLoad = true;
                Console.WriteLine("[state] this load is an AREA load - " + result.LoadTypeInfo);
            }

            // Only area loads advance the split's load count. Plain loads have already had their time
            // removed above; they just are not a landmark worth splitting on.
            if (!sawStatsThisLoad || countedThisLoad)
            {
                return;
            }

            if (settings.AutoSplitterEnabled && !(settings.AutoSplitterDisableOnSkipUntilSplit && LastSplitSkip)
                && framesSinceLastManualSplit >= settings.AutoSplitterManualSplitDelayFrames)
            {
                countedThisLoad = true;
                NumberOfLoadsPerSplit[liveSplitState.CurrentSplitIndex]++;

                if (CumulativeNumberOfLoadsForSplitIndex(liveSplitState.CurrentSplitIndex) >= settings.GetCumulativeNumberOfLoadsForSplit(liveSplitState.CurrentSplit.Name))
                {
                    timer.Split();
                }
            }
        }

        private void timer_OnUndoSplit(object sender, EventArgs e)
        {
            //If we undo a split that already has met the required number of loads, we probably want the number to reset.
            if (NumberOfLoadsPerSplit[liveSplitState.CurrentSplitIndex] >= settings.GetAutoSplitNumberOfLoadsForSplit(liveSplitState.CurrentSplit.Name))
            {
                NumberOfLoadsPerSplit[liveSplitState.CurrentSplitIndex] = 0;
            }

            //Otherwise - we're fine. If it is a split that was skipped earlier, we still keep track of how we're standing.
        }

        private void timer_OnSplit(object sender, EventArgs e)
        {
            framesSinceLastManualSplit = 0;
            //If we split, we add all remaining loads to the last split.
            //This means that the autosplitter now starts at 0 loads on the next split.
            //This is just necessary for manual splits, as automatic splits will always have a difference of 0.
            var loadsRequiredTotal = settings.GetCumulativeNumberOfLoadsForSplit(liveSplitState.Run[liveSplitState.CurrentSplitIndex - 1].Name);
            var loadsCurrentTotal = CumulativeNumberOfLoadsForSplitIndex(liveSplitState.CurrentSplitIndex - 1);
            NumberOfLoadsPerSplit[liveSplitState.CurrentSplitIndex - 1] += loadsRequiredTotal - loadsCurrentTotal;

            LastSplitSkip = false;
        }

        private void timer_OnSkipSplit(object sender, EventArgs e)
        {
            //We don't need to do anything here - we just keep track of loads per split now.
            LastSplitSkip = true;
        }

        private void timer_OnReset(object sender, TimerPhase value)
        {
            timerStarted = false;
            framesSinceLastManualSplit = 0;
            LastSplitSkip = false;
            isLoading = false;
            rawMatchPrev = false;
            consecutiveMatchFrames = 0;
            sawStatsThisLoad = false;
            countedThisLoad = false;

            InitNumberOfLoadsFromState();

            if (log_file_writer != null)
            {
                if (log_file_writer.BaseStream != null)
                {
                    log_file_writer.Flush();
                    log_file_writer.Close();
                    log_file_writer.Dispose();
                }
                log_file_writer = null;
            }

        }

        void timer_OnStart(object sender, EventArgs e)
        {
            InitNumberOfLoadsFromState();
            timer.InitializeGameTime();
            framesSinceLastManualSplit = 0;
            timerStarted = true;

            ReloadLogFile();
        }

        // Debug builds only. The checkbox that turns the detection log on and off is Debug-only, so a
        // Release build would have no way to stop it once a layout came in with the setting on - and a
        // log nobody can read or disable is exactly the debug output that has no business shipping.
        // settings.SaveDetectionLog still round-trips through the layout XML either way.
        private void ReloadLogFile()
        {
#if DEBUG
            if (settings.SaveDetectionLog == false)
                return;


            System.IO.Directory.CreateDirectory(settings.DetectionLogFolderName);

            string fileName = Path.Combine(settings.DetectionLogFolderName + "/", "Sly4BHLoadRemoval_Log_" + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss_") + settings.removeInvalidXMLCharacters(GameName) + "_" + settings.removeInvalidXMLCharacters(GameCategory) + ".txt");

            if (log_file_writer != null)
            {
                if (log_file_writer.BaseStream != null)
                {
                    log_file_writer.Flush();
                    log_file_writer.Close();
                    log_file_writer.Dispose();
                }
                log_file_writer = null;
            }


            log_file_writer = new StreamWriter(new FileStream(fileName, FileMode.Create));
            log_file_writer.AutoFlush = true;
            Console.SetOut(log_file_writer);
            Console.SetError(log_file_writer);
#endif
        }

        private bool SplitsAreDifferent(LiveSplitState newState)
        {
            //If GameName / Category is different
            if (GameName != newState.Run.GameName || GameCategory != newState.Run.CategoryName)
            {
                GameName = newState.Run.GameName;
                GameCategory = newState.Run.CategoryName;
                return true;
            }

            //If number of splits is different
            if (newState.Run.Count != liveSplitState.Run.Count)
            {
                return true;
            }

            //Check if any split name is different.
            for (int splitIdx = 0; splitIdx < newState.Run.Count; splitIdx++)
            {
                if (newState.Run[splitIdx].Name != SplitNames[splitIdx])
                {

                    SplitNames = new List<string>();

                    foreach (var split in newState.Run)
                    {
                        SplitNames.Add(split.Name);
                    }

                    return true;
                }

            }

            return false;
        }

        public void Update(IInvalidator invalidator, LiveSplitState state, float width, float height, LayoutMode mode)
        {
            if (SplitsAreDifferent(state))
            {
                settings.ChangeAutoSplitSettingsToGameName(GameName, GameCategory);

                ReloadLogFile();
            }
            liveSplitState = state;

            if (settings.isCalibrating)
            {
                settings.CalibrationTick();
            }

            CaptureLoads();
        }

        public void DrawHorizontal(Graphics g, LiveSplitState state, float height, Region clipRegion)
        {

        }

        public void DrawVertical(Graphics g, LiveSplitState state, float width, Region clipRegion)
        {

        }

        public float VerticalHeight
        {
            get { return 0; }
        }

        public float MinimumWidth
        {
            get { return 0; }
        }

        public float HorizontalWidth
        {
            get { return 0; }
        }

        public float MinimumHeight
        {
            get { return 0; }
        }

        public System.Xml.XmlNode GetSettings(System.Xml.XmlDocument document)
        {
            return settings.GetSettings(document);
        }

        public System.Windows.Forms.Control GetSettingsControl(UI.LayoutMode mode)
        {
            return settings;
        }

        public void SetSettings(System.Xml.XmlNode settings)
        {
            this.settings.SetSettings(settings);
        }

        public void Dispose()
        {
            timer.CurrentState.OnStart -= timer_OnStart;
            timer.CurrentState.OnReset -= timer_OnReset;
            timer.CurrentState.OnSkipSplit -= timer_OnSkipSplit;
            timer.CurrentState.OnSplit -= timer_OnSplit;
            timer.CurrentState.OnUndoSplit -= timer_OnUndoSplit;
            timer.CurrentState.OnPause -= timer_OnPause;
            timer.CurrentState.OnResume -= timer_OnResume;

            settings.StopVideoCapture();

            if (log_file_writer != null)
            {
                if (log_file_writer.BaseStream != null)
                {
                    log_file_writer.Flush();
                    log_file_writer.Close();
                    log_file_writer.Dispose();
                }
                log_file_writer = null;
            }

        }
    }
}
