using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace Sly4BHLoadDetector
{
    // Capture from a video capture device (capture card, webcam) via DirectShow.
    //
    // Hand-rolled COM interop rather than a library, for the same reason DLLImportStuff.cs exists: the
    // component ships as a single DLL dropped into LiveSplit's Components folder, and every managed
    // wrapper for this (AForge, DirectShowLib, OpenCvSharp) turns that into several files the user has
    // to place correctly. The interop surface needed here is small because of one shortcut - see
    // VideoCaptureSource below, which polls the sample grabber instead of implementing a callback.
    //
    // Only the prefix of each COM interface that is actually called is declared. That is safe as long
    // as the declared methods stay in vtable order and nothing below the last one is ever invoked;
    // it is much less error-prone than transcribing interfaces in full.

    // One device as offered to the user. MonikerName is the persistable identity (it survives
    // restarts); Name is what the dropdown shows.
    public sealed class VideoCaptureDeviceInfo
    {
        public string Name;
        public string MonikerName;

        public override string ToString()
        {
            return Name;
        }
    }

    internal static class DirectShow
    {
        public static readonly Guid CLSID_SystemDeviceEnum = new Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
        public static readonly Guid CLSID_VideoInputDeviceCategory = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        public static readonly Guid CLSID_FilterGraph = new Guid("e436ebb3-524f-11ce-9f53-0020af0ba770");
        public static readonly Guid CLSID_CaptureGraphBuilder2 = new Guid("BF87B6E1-8C27-11d0-B3F0-00AA003761C5");
        public static readonly Guid CLSID_SampleGrabber = new Guid("C1F400A0-3F08-11D3-9F0B-006008039E37");
        public static readonly Guid CLSID_NullRenderer = new Guid("C1F400A4-3F08-11D3-9F0B-006008039E37");

        public static readonly Guid MEDIATYPE_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MEDIASUBTYPE_RGB24 = new Guid("e436eb7d-524f-11ce-9f53-0020af0ba770");
        public static readonly Guid MEDIASUBTYPE_RGB32 = new Guid("e436eb7e-524f-11ce-9f53-0020af0ba770");

        // FourCC subtypes follow the pattern {'2','1','V','N'}-0000-0010-8000-00AA00389B71.
        public static readonly Guid MEDIASUBTYPE_NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
        public static readonly Guid MEDIASUBTYPE_YUY2 = new Guid("32595559-0000-0010-8000-00AA00389B71");
        public static readonly Guid MEDIASUBTYPE_UYVY = new Guid("59565955-0000-0010-8000-00AA00389B71");
        public static readonly Guid FORMAT_VideoInfo = new Guid("05589f80-c356-11ce-bf01-00aa0055595a");

        public static readonly Guid PIN_CATEGORY_CAPTURE = new Guid("fb6c4281-0353-11d1-905f-0000c0cc16ba");
        public static readonly Guid PIN_CATEGORY_PREVIEW = new Guid("fb6c4282-0353-11d1-905f-0000c0cc16ba");

        public static readonly Guid IID_IBaseFilter = new Guid("56a86895-0ad4-11ce-b03a-0020af0ba770");
        public static readonly Guid IID_IPropertyBag = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");
        public static readonly Guid IID_IAMStreamConfig = new Guid("C6E13340-30AC-11d0-A18C-00A0C9118956");

        public const int S_OK = 0;
        public const int VFW_E_WRONG_STATE = unchecked((int)0x80040227);

        public static object CreateComObject(Guid clsid)
        {
            Type t = Type.GetTypeFromCLSID(clsid, false);
            return t == null ? null : Activator.CreateInstance(t);
        }

        public static void Release(object comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject))
            {
                try { Marshal.ReleaseComObject(comObject); }
                catch (Exception) { }
            }
        }
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator([In] ref Guid deviceClass, out IEnumMoniker enumMoniker, int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyBag
    {
        [PreserveSig]
        int Read([In, MarshalAs(UnmanagedType.LPWStr)] string propertyName,
                 [In, Out, MarshalAs(UnmanagedType.Struct)] ref object value,
                 IntPtr errorLog);

        [PreserveSig]
        int Write([In, MarshalAs(UnmanagedType.LPWStr)] string propertyName,
                  [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    // Opaque on purpose - it is only ever passed to other DirectShow calls, never invoked.
    [ComImport, Guid("56a86895-0ad4-11ce-b03a-0020af0ba770"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IBaseFilter
    {
    }

    // Prefix of IFilterGraph; AddFilter is its first method, so nothing else needs declaring.
    [ComImport, Guid("56a868a9-0ad4-11ce-b03a-0020af0ba770"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IGraphBuilder
    {
        [PreserveSig]
        int AddFilter([In] IBaseFilter filter, [In, MarshalAs(UnmanagedType.LPWStr)] string name);
    }

    // IMediaFilter rather than IMediaControl: same Run/Stop, but a plain IUnknown interface instead of
    // a dispinterface, so there are no IDispatch slots to pad the vtable with.
    [ComImport, Guid("56a86899-0ad4-11ce-b03a-0020af0ba770"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMediaFilter
    {
        [PreserveSig] int GetClassID(out Guid classID);
        [PreserveSig] int Stop();
        [PreserveSig] int Pause();
        [PreserveSig] int Run(long start);
        [PreserveSig] int GetState(int millisecondsTimeout, out int filterState);

        // IntPtr rather than an IReferenceClock interface so NULL can be passed - running with no
        // clock is the point of having this. See VideoCaptureSource.RunGraph.
        [PreserveSig] int SetSyncSource(IntPtr clock);
    }

    [ComImport, Guid("93E5A4E0-2D50-11d2-ABFA-00A0C9C6E38D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICaptureGraphBuilder2
    {
        [PreserveSig] int SetFiltergraph([In] IGraphBuilder graph);
        [PreserveSig] int GetFiltergraph(out IGraphBuilder graph);

        [PreserveSig]
        int SetOutputFileName([In] ref Guid type, [In, MarshalAs(UnmanagedType.LPWStr)] string fileName,
                              out IBaseFilter filter, out IntPtr sink);

        [PreserveSig]
        int FindInterface([In] ref Guid category, [In] ref Guid type, [In] IBaseFilter filter,
                          [In] ref Guid iid, [Out, MarshalAs(UnmanagedType.IUnknown)] out object result);

        // Category and type are IntPtr rather than `ref Guid` because both are legitimately NULL:
        // "any pin category" and "any media type" are the last fallbacks when a device does not
        // present a pin the usual way. See VideoCaptureSource.RenderCaptureStream.
        [PreserveSig]
        int RenderStream(IntPtr category, IntPtr type,
                         [In, MarshalAs(UnmanagedType.IUnknown)] object source,
                         [In] IBaseFilter compressor, [In] IBaseFilter renderer);
    }

    // Lets the capture pin's output format be chosen before it connects.
    [ComImport, Guid("C6E13340-30AC-11d0-A18C-00A0C9118956"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAMStreamConfig
    {
        [PreserveSig] int SetFormat([In] AmMediaType mediaType);
        [PreserveSig] int GetFormat(out IntPtr mediaType);
        [PreserveSig] int GetNumberOfCapabilities(out int count, out int configSize);
        [PreserveSig] int GetStreamCaps(int index, out IntPtr mediaType, IntPtr config);
    }

    [ComImport, Guid("6B652FFF-11FE-4fce-92AD-0266B5D7C78F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISampleGrabber
    {
        [PreserveSig] int SetOneShot([In, MarshalAs(UnmanagedType.Bool)] bool oneShot);
        [PreserveSig] int SetMediaType([In] AmMediaType type);
        [PreserveSig] int GetConnectedMediaType([Out] AmMediaType type);
        [PreserveSig] int SetBufferSamples([In, MarshalAs(UnmanagedType.Bool)] bool bufferThem);
        [PreserveSig] int GetCurrentBuffer(ref int bufferSize, IntPtr buffer);
    }

    // A class rather than a struct so it marshals as a pointer, which is what the two SampleGrabber
    // methods above expect.
    [StructLayout(LayoutKind.Sequential), ComVisible(false)]
    internal class AmMediaType
    {
        public Guid MajorType;
        public Guid SubType;
        [MarshalAs(UnmanagedType.Bool)] public bool FixedSizeSamples;
        [MarshalAs(UnmanagedType.Bool)] public bool TemporalCompression;
        public int SampleSize;
        public Guid FormatType;
        public IntPtr UnkPtr;
        public int FormatSize;
        public IntPtr FormatPtr;

        // The format block and the optional IUnknown are allocated by the filter that filled this in,
        // so they have to be handed back rather than garbage collected.
        public void Free()
        {
            if (FormatSize != 0 && FormatPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(FormatPtr);
                FormatPtr = IntPtr.Zero;
                FormatSize = 0;
            }

            if (UnkPtr != IntPtr.Zero)
            {
                Marshal.Release(UnkPtr);
                UnkPtr = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int ImageSize;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoInfoHeader
    {
        public int SourceLeft, SourceTop, SourceRight, SourceBottom;
        public int TargetLeft, TargetTop, TargetRight, TargetBottom;
        public int BitRate;
        public int BitErrorRate;
        public long AvgTimePerFrame;
        public BitmapInfoHeader BmiHeader;
    }

    // Enumerates the video capture devices Windows knows about.
    public static class VideoCaptureDevices
    {
        // Never throws: a machine with no devices, a broken driver or a denied privacy setting should
        // leave the dropdown empty rather than take the settings dialog down with it.
        public static List<VideoCaptureDeviceInfo> Enumerate()
        {
            var devices = new List<VideoCaptureDeviceInfo>();
            object devEnumObject = null;
            IEnumMoniker enumMoniker = null;

            try
            {
                devEnumObject = DirectShow.CreateComObject(DirectShow.CLSID_SystemDeviceEnum);
                ICreateDevEnum devEnum = devEnumObject as ICreateDevEnum;
                if (devEnum == null)
                {
                    return devices;
                }

                Guid category = DirectShow.CLSID_VideoInputDeviceCategory;

                // S_FALSE (1) means the category exists but is empty - no capture devices attached.
                if (devEnum.CreateClassEnumerator(ref category, out enumMoniker, 0) != DirectShow.S_OK ||
                    enumMoniker == null)
                {
                    return devices;
                }

                var monikers = new IMoniker[1];
                while (enumMoniker.Next(1, monikers, IntPtr.Zero) == DirectShow.S_OK)
                {
                    IMoniker moniker = monikers[0];
                    if (moniker == null)
                    {
                        continue;
                    }

                    try
                    {
                        string displayName;
                        moniker.GetDisplayName(null, null, out displayName);

                        var info = new VideoCaptureDeviceInfo
                        {
                            Name = ReadFriendlyName(moniker) ?? displayName,
                            MonikerName = displayName
                        };
                        devices.Add(info);
                    }
                    catch (Exception)
                    {
                        // One unreadable device must not hide the rest.
                    }
                    finally
                    {
                        DirectShow.Release(moniker);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Video capture device enumeration failed: " + ex.Message);
            }
            finally
            {
                DirectShow.Release(enumMoniker);
                DirectShow.Release(devEnumObject);
            }

            return devices;
        }

        private static string ReadFriendlyName(IMoniker moniker)
        {
            object bagObject = null;
            try
            {
                Guid iid = DirectShow.IID_IPropertyBag;
                moniker.BindToStorage(null, null, ref iid, out bagObject);

                IPropertyBag bag = bagObject as IPropertyBag;
                if (bag == null)
                {
                    return null;
                }

                object value = null;
                return bag.Read("FriendlyName", ref value, IntPtr.Zero) == DirectShow.S_OK
                    ? value as string
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                DirectShow.Release(bagObject);
            }
        }

        // Resolves a saved selection back to a live device.
        //
        // Moniker display names embed the USB port path, so the same capture card plugged into a
        // different socket comes back with a different one. The friendly name is the fallback, which is
        // what makes a saved layout keep working after the card is moved.
        public static VideoCaptureDeviceInfo Resolve(List<VideoCaptureDeviceInfo> devices,
                                                     string monikerName, string friendlyName)
        {
            if (devices == null || devices.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(monikerName))
            {
                foreach (VideoCaptureDeviceInfo d in devices)
                {
                    if (d.MonikerName == monikerName) return d;
                }
            }

            if (!string.IsNullOrEmpty(friendlyName))
            {
                foreach (VideoCaptureDeviceInfo d in devices)
                {
                    if (d.Name == friendlyName) return d;
                }
            }

            return null;
        }
    }

    // A running capture graph for one device, with the most recent frame available to any thread.
    //
    // All COM lives on one dedicated worker thread: it builds the graph, polls the sample grabber, and
    // tears everything down again. Callers only ever touch `latestFrame` under a lock. That matters
    // because the two callers are on different threads - LiveSplit's timer thread drives detection
    // while the settings dialog draws its preview on the UI thread - and DirectShow graphs do not
    // appreciate being built on one and released on another.
    //
    // Frames are pulled with GetCurrentBuffer rather than pushed through ISampleGrabberCB. Polling
    // suits a component that is asked for a frame ~30 times a second anyway, and it removes the entire
    // callback interface plus its threading and lifetime problems from the interop surface.
    public sealed class VideoCaptureSource : IDisposable
    {
        // How often the newest sample is taken off the grabber.
        //
        // This is latency, directly: whatever is asked for a frame gets one up to this old. It can be
        // this short because the poll no longer decodes anything - it is a buffer copy - so the cost is
        // memory bandwidth rather than colour conversion.
        private const int PollIntervalMs = 10;

        private readonly string monikerName;
        private readonly object frameLock = new object();

        private Thread worker;
        private volatile bool stopRequested;
        private volatile string status = "starting...";
        private volatile bool running;

        // How the device's samples are laid out. RGB is a straight copy; the YUV layouts have to be
        // converted here because Windows has no stock DirectShow filter that converts them - which is
        // why intelligent connect cannot do it for us and the graph would otherwise deliver nothing.
        private enum FrameLayout
        {
            Rgb24,
            Rgb32,
            Nv12,
            Yuy2,
            Uyvy
        }

        // The most recent sample, kept as the device's own raw bytes rather than as a Bitmap.
        //
        // Converting on the poll thread was the obvious design and the wrong one. It decoded every
        // frame the device produced whether or not anything wanted it, and then the consumer still had
        // to copy the whole 1080p result before cropping it - 11.8ms per frame, measured, purely to
        // hand over ownership. Keeping the bytes and decoding just the region asked for removes both.
        private byte[] latestBuffer;
        private bool hasFrame;

        // A frame the poll thread has already decoded, waiting to be collected, plus the region it was
        // decoded for.
        //
        // Decoding purely on demand is simple but puts ~23ms of colour conversion (at 1080p) directly
        // between the component asking for a frame and getting one. Decoding eagerly for every sample
        // instead wastes that on frames nobody collects. So: decode exactly one frame ahead, for
        // whatever region was asked for last, and only once the previous one has been taken. The rate
        // then matches consumption on its own, and the conversion overlaps the caller's resize rather
        // than delaying it.
        private Bitmap pendingFrame;
        private Rectangle pendingRegion;

        private int frameWidth;
        private int frameHeight;
        private int frameBitCount;
        private int frameBufferSize;
        private Guid frameSubtype;
        private FrameLayout frameLayout;

        public string MonikerName { get { return monikerName; } }

        // Human-readable state for the settings dialog: resolution once running, the reason otherwise.
        public string Status { get { return status; } }

        public bool IsRunning { get { return running; } }

        public VideoCaptureSource(string monikerName)
        {
            this.monikerName = monikerName;

            worker = new Thread(Run);
            worker.IsBackground = true;   // never keep LiveSplit alive
            worker.Name = "Sly4BH video capture";
            worker.Start();
        }

        // The device's frame size, or 0 until the first frame arrives.
        public int FrameWidth { get { return hasFrame ? frameWidth : 0; } }
        public int FrameHeight { get { return hasFrame ? frameHeight : 0; } }

        // Decodes `region` of the most recent frame into a new Bitmap the caller owns, or null if no
        // frame has arrived yet.
        //
        // Decoding happens under the lock. The poll thread can therefore be held up briefly, which
        // only means the next frame it stores is a few milliseconds newer - far cheaper than the
        // whole-frame copy this replaced.
        public Bitmap CaptureRegion(Rectangle region)
        {
            lock (frameLock)
            {
                if (!hasFrame)
                {
                    return null;
                }

                Rectangle clamped = Rectangle.Intersect(region, new Rectangle(0, 0, frameWidth, frameHeight));
                if (clamped.Width <= 0 || clamped.Height <= 0)
                {
                    clamped = new Rectangle(0, 0, frameWidth, frameHeight);
                }

                // The usual case: the poll thread decoded this exact region while the caller was busy
                // with the previous frame, so this hands it over and returns immediately.
                if (pendingFrame != null && pendingRegion == clamped)
                {
                    Bitmap ready = pendingFrame;
                    pendingFrame = null;
                    return ready;
                }

                // A different region (the settings preview asking for the whole frame, or the crop
                // just changed). Decode it here and record it, so the next one is ready in advance.
                if (pendingFrame != null)
                {
                    pendingFrame.Dispose();
                    pendingFrame = null;
                }

                pendingRegion = clamped;
                return BufferToBitmap(latestBuffer, clamped);
            }
        }

        // The whole frame. Used by the settings preview and the diagnostic tools.
        public Bitmap CloneLatestFrame()
        {
            return CaptureRegion(new Rectangle(0, 0, int.MaxValue, int.MaxValue));
        }

        public void Dispose()
        {
            stopRequested = true;

            Thread t = worker;
            worker = null;
            if (t != null && t.IsAlive)
            {
                // Generous relative to the poll interval, but the graph teardown itself can block on a
                // driver. Failing to join is not fatal - the thread is a background thread.
                t.Join(2000);
            }

            lock (frameLock)
            {
                latestBuffer = null;
                hasFrame = false;

                if (pendingFrame != null)
                {
                    pendingFrame.Dispose();
                    pendingFrame = null;
                }
            }
        }

        // Formats to ask the sample grabber for, in order of preference.
        //
        // RGB24 is what the rest of this file is written around. RGB32 is the fallback for devices
        // whose driver will produce one but not the other. Guid.Empty means "no constraint at all" -
        // the last resort for devices that refuse to negotiate when asked for anything specific,
        // where whatever comes out is checked afterwards and rejected if it is not RGB.
        private static readonly Guid[] PreferredSubtypes =
        {
            DirectShow.MEDIASUBTYPE_RGB24,
            DirectShow.MEDIASUBTYPE_RGB32,
            Guid.Empty
        };

        private enum GraphResult
        {
            Started,          // ran until Dispose
            TryNextFormat,    // this pixel format did not connect; another might
            Failed            // no format will help (no such device, no DirectShow, device busy)
        }

        // How long to wait before rebuilding the graph after it failed to connect or stopped
        // delivering.
        //
        // Retrying at all is the point. An analog capture card with nothing plugged into it cannot
        // describe a video format, so the graph refuses to connect (E_FAIL) - and that is the *normal*
        // state when LiveSplit starts before the console is switched on. Without this the component
        // would sit dead until the user thought to reselect the device.
        private const int RetryDelayMs = 3000;

        // Consecutive empty polls before a *running* graph is given up on as never going to deliver in
        // this format, and the next one is tried. At 25ms a poll this is five seconds.
        //
        // Deliberately generous. Falling through early would be actively harmful on a real capture
        // card, which can take a few seconds to lock onto an incoming signal: bailing at two seconds
        // would walk the whole format chain, find nothing, and start again - thrashing forever on a
        // device that was about to work.
        private const int FirstFrameTimeoutPolls = 200;

        // Once frames have been seen, a shorter gap is enough to report a stall.
        private const int NoFrameWarningPolls = 80;

        private void Run()
        {
            while (!stopRequested)
            {
                // Each attempt gets a completely fresh graph. Retrying a different grabber media type
                // on a graph whose RenderStream already failed leaves half-connected pins behind and
                // then fails for reasons that have nothing to do with the format.
                foreach (Guid subtype in PreferredSubtypes)
                {
                    if (stopRequested)
                    {
                        return;
                    }

                    GraphResult result = RunGraph(subtype);
                    if (result == GraphResult.Started)
                    {
                        return;   // only returns once Dispose has asked it to stop
                    }

                    if (result == GraphResult.Failed)
                    {
                        break;
                    }
                }

                Sleep(RetryDelayMs);
            }
        }

        // Sleeps in short slices so Dispose does not have to wait out the whole delay.
        private void Sleep(int totalMs)
        {
            for (int slept = 0; slept < totalMs && !stopRequested; slept += 100)
            {
                Thread.Sleep(100);
            }
        }

        // Builds and runs one graph. `status` is left describing the outcome either way.
        private GraphResult RunGraph(Guid subtype)
        {
            object graphObject = null;
            object builderObject = null;
            object grabberObject = null;
            object rendererObject = null;
            object sourceObject = null;
            IMediaFilter mediaFilter = null;

            try
            {
                graphObject = DirectShow.CreateComObject(DirectShow.CLSID_FilterGraph);
                builderObject = DirectShow.CreateComObject(DirectShow.CLSID_CaptureGraphBuilder2);
                grabberObject = DirectShow.CreateComObject(DirectShow.CLSID_SampleGrabber);
                rendererObject = DirectShow.CreateComObject(DirectShow.CLSID_NullRenderer);

                if (graphObject == null || builderObject == null || grabberObject == null || rendererObject == null)
                {
                    status = "DirectShow is not available on this system";
                    return GraphResult.Failed;
                }

                IGraphBuilder graph = (IGraphBuilder)graphObject;
                ICaptureGraphBuilder2 builder = (ICaptureGraphBuilder2)builderObject;
                ISampleGrabber grabber = (ISampleGrabber)grabberObject;
                mediaFilter = graphObject as IMediaFilter;

                if (mediaFilter == null)
                {
                    status = "filter graph does not support IMediaFilter";
                    return GraphResult.Failed;
                }

                sourceObject = BindDevice();
                if (sourceObject == null)
                {
                    status = "device not found - is it plugged in?";
                    return GraphResult.Failed;
                }

                int hr = builder.SetFiltergraph(graph);
                if (hr < 0)
                {
                    status = "SetFiltergraph failed (0x" + hr.ToString("X8") + ")";
                    return GraphResult.Failed;
                }

                // Ask for RGB rather than whatever the device emits natively, and let DirectShow insert
                // a colour converter. That is what keeps YUV decoding out of this file entirely.
                //
                // Major type and subtype only: leaving FormatType unset (GUID_NULL) means "any format
                // block". Naming FORMAT_VideoInfo here looks harmless and is not - it constrains the
                // connection enough that intelligent connect gives up on some devices rather than
                // inserting the converter.
                if (subtype != Guid.Empty)
                {
                    AmMediaType wanted = new AmMediaType
                    {
                        MajorType = DirectShow.MEDIATYPE_Video,
                        SubType = subtype
                    };
                    grabber.SetMediaType(wanted);
                }

                grabber.SetOneShot(false);
                grabber.SetBufferSamples(true);

                int hrSource = graph.AddFilter((IBaseFilter)sourceObject, "Source");
                int hrGrabber = graph.AddFilter((IBaseFilter)grabberObject, "Sample Grabber");
                int hrRenderer = graph.AddFilter((IBaseFilter)rendererObject, "Null Renderer");

                if (hrSource < 0 || hrGrabber < 0 || hrRenderer < 0)
                {
                    status = "AddFilter failed (source 0x" + hrSource.ToString("X8") +
                             ", grabber 0x" + hrGrabber.ToString("X8") +
                             ", renderer 0x" + hrRenderer.ToString("X8") + ")";
                    return GraphResult.Failed;
                }

                // Ask the device for the smallest resolution that still resolves the mask, before the
                // pin connects. Everything downstream is proportional to the source size and the
                // result is squashed to 300x300 regardless, so capturing 1080p to detect a 46px mask
                // is pure cost - measured at 1080p the decode and the resize were ~23ms each, against
                // 0.15ms for the detection itself.
                ChooseCaptureFormat(builder, sourceObject);

                int renderResult = RenderCaptureStream(builder, sourceObject,
                                                       (IBaseFilter)grabberObject, (IBaseFilter)rendererObject);
                if (renderResult < 0)
                {
                    // The HRESULT is worth surfacing: 0x80040217 (VFW_E_CANNOT_CONNECT) means no
                    // common format could be negotiated, while 0x80004005 (E_FAIL) from an analog card
                    // usually means it has no input signal to describe a format from.
                    status = "could not build a capture graph (0x" + renderResult.ToString("X8") + ")";
                    return GraphResult.TryNextFormat;
                }

                if (!ReadConnectedFormat(grabber))
                {
                    return GraphResult.TryNextFormat;
                }

                // Run with no reference clock.
                //
                // With a clock, the null renderer schedules each sample against it and blocks until
                // that sample is due - which back-pressures the whole chain, so the sample grabber
                // upstream stops seeing frames. Nothing here cares when a frame was meant to be shown;
                // the component just wants the most recent one, as fast as it arrives.
                mediaFilter.SetSyncSource(IntPtr.Zero);

                // Pause before Run, and do not skip it.
                //
                // Running straight from Stopped appears to work - the call succeeds - but a source
                // filter that starts its streaming thread on the transition *into Paused* never pushes
                // anything, and the sample grabber then answers every GetCurrentBuffer with
                // VFW_E_WRONG_STATE forever. Webcams tend to start on Run and hide this; OBS's virtual
                // camera does not, which is how it surfaced.
                //
                // IMediaControl::Run would do this transition itself, but it is a dispinterface, so
                // driving IMediaFilter through the states by hand keeps the interop plain IUnknown.
                int hrPause = mediaFilter.Pause();
                if (hrPause < 0)
                {
                    status = "device could not be started (pause failed 0x" + hrPause.ToString("X8") + ")";
                    return GraphResult.Failed;
                }

                int hrRun = mediaFilter.Run(0);
                if (hrRun < 0)
                {
                    status = "device could not be started - it may be in use by another program" +
                             " (0x" + hrRun.ToString("X8") + ")";
                    return GraphResult.Failed;
                }

                running = true;

                // A graph that connects but never delivers is not success. A device can accept a
                // pixel format it does not actually produce - OBS's virtual camera accepts an RGB24
                // connection, reports Running, and then pushes nothing - so "connected" has to be
                // confirmed by a frame before this format is believed.
                if (!PollLoop(grabber, mediaFilter))
                {
                    return GraphResult.TryNextFormat;
                }

                return GraphResult.Started;
            }
            catch (Exception ex)
            {
                status = "capture failed: " + ex.Message;
                Console.WriteLine("Video capture failed: " + ex);
                return GraphResult.Failed;
            }
            finally
            {
                running = false;

                try { if (mediaFilter != null) mediaFilter.Stop(); }
                catch (Exception) { }

                DirectShow.Release(sourceObject);
                DirectShow.Release(rendererObject);
                DirectShow.Release(grabberObject);
                DirectShow.Release(builderObject);
                DirectShow.Release(graphObject);
            }
        }

        private object BindDevice()
        {
            List<VideoCaptureDeviceInfo> devices = VideoCaptureDevices.Enumerate();
            foreach (VideoCaptureDeviceInfo d in devices)
            {
                if (d.MonikerName != monikerName)
                {
                    continue;
                }

                object devEnumObject = null;
                IEnumMoniker enumMoniker = null;
                try
                {
                    // Re-enumerate rather than hold a moniker across threads: monikers bound on the
                    // caller's thread are not safe to use here.
                    devEnumObject = DirectShow.CreateComObject(DirectShow.CLSID_SystemDeviceEnum);
                    ICreateDevEnum devEnum = (ICreateDevEnum)devEnumObject;
                    Guid category = DirectShow.CLSID_VideoInputDeviceCategory;
                    if (devEnum.CreateClassEnumerator(ref category, out enumMoniker, 0) != DirectShow.S_OK)
                    {
                        return null;
                    }

                    var monikers = new IMoniker[1];
                    while (enumMoniker.Next(1, monikers, IntPtr.Zero) == DirectShow.S_OK)
                    {
                        IMoniker moniker = monikers[0];
                        try
                        {
                            string displayName;
                            moniker.GetDisplayName(null, null, out displayName);
                            if (displayName != monikerName)
                            {
                                continue;
                            }

                            object filter;
                            Guid iid = DirectShow.IID_IBaseFilter;
                            moniker.BindToObject(null, null, ref iid, out filter);
                            return filter;
                        }
                        finally
                        {
                            DirectShow.Release(moniker);
                        }
                    }
                }
                finally
                {
                    DirectShow.Release(enumMoniker);
                    DirectShow.Release(devEnumObject);
                }
            }

            return null;
        }

        // The smallest capture the detector is still happy with.
        //
        // Detection squashes whatever it gets to 300x300, so anything above that is thrown away - but
        // not quite: the mask is about 15% of the frame width, so at 640 wide it is still ~95px of
        // real detail before the squash, and the chroma the hue gate reads is half that again. Going
        // much below this starts eating into the measurement rather than just the cost.
        private const int MinCaptureWidth = 640;
        private const int MinCaptureHeight = 360;

        // Picks the smallest offered format at or above the minimum above and applies it.
        //
        // Best-effort throughout: a device that offers no choice, refuses the format, or does not
        // expose IAMStreamConfig at all just keeps its default, which is what happened before this
        // existed. Never fails the graph.
        private void ChooseCaptureFormat(ICaptureGraphBuilder2 builder, object source)
        {
            object configObject = null;

            try
            {
                Guid category = DirectShow.PIN_CATEGORY_CAPTURE;
                Guid mediaType = DirectShow.MEDIATYPE_Video;
                Guid iid = DirectShow.IID_IAMStreamConfig;

                if (builder.FindInterface(ref category, ref mediaType, (IBaseFilter)source,
                                          ref iid, out configObject) < 0 || configObject == null)
                {
                    return;
                }

                IAMStreamConfig config = configObject as IAMStreamConfig;
                if (config == null)
                {
                    return;
                }

                int count, configSize;
                if (config.GetNumberOfCapabilities(out count, out configSize) < 0 || count <= 0)
                {
                    return;
                }

                IntPtr configBuffer = Marshal.AllocCoTaskMem(configSize);
                AmMediaType best = null;
                long bestArea = long.MaxValue;

                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr typePtr;
                        if (config.GetStreamCaps(i, out typePtr, configBuffer) < 0 || typePtr == IntPtr.Zero)
                        {
                            continue;
                        }

                        AmMediaType candidate =
                            (AmMediaType)Marshal.PtrToStructure(typePtr, typeof(AmMediaType));

                        int width, height;
                        if (TryReadSize(candidate, out width, out height) &&
                            width >= MinCaptureWidth && height >= MinCaptureHeight)
                        {
                            long area = (long)width * height;
                            if (area < bestArea)
                            {
                                if (best != null) FreeMediaType(best);
                                bestArea = area;
                                best = candidate;
                                Marshal.FreeCoTaskMem(typePtr);
                                continue;
                            }
                        }

                        candidate.Free();
                        Marshal.FreeCoTaskMem(typePtr);
                    }

                    if (best != null)
                    {
                        config.SetFormat(best);
                        FreeMediaType(best);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(configBuffer);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Capture format selection skipped: " + ex.Message);
            }
            finally
            {
                DirectShow.Release(configObject);
            }
        }

        private static bool TryReadSize(AmMediaType type, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (type.FormatPtr == IntPtr.Zero ||
                type.FormatSize < Marshal.SizeOf(typeof(VideoInfoHeader)))
            {
                return false;
            }

            VideoInfoHeader header =
                (VideoInfoHeader)Marshal.PtrToStructure(type.FormatPtr, typeof(VideoInfoHeader));

            width = header.BmiHeader.Width;
            height = Math.Abs(header.BmiHeader.Height);
            return width > 0 && height > 0;
        }

        private static void FreeMediaType(AmMediaType type)
        {
            if (type != null)
            {
                type.Free();
            }
        }

        // Connects the device to the sample grabber, trying progressively looser requests.
        //
        // Devices vary more than the documentation suggests. Most expose a capture pin; some webcams
        // only offer preview; analog cards behind a WDM crossbar can present neither category in the
        // usual way and only connect when asked for "any pin, any media type", which is what the last
        // attempt does. Each step is strictly weaker than the one before, so the first that succeeds
        // is the most specific connection available.
        //
        // Note >= 0 rather than == S_OK: RenderStream reports partial successes such as
        // VFW_S_NOPREVIEWPIN (0x00040273), which are still working graphs.
        private int RenderCaptureStream(ICaptureGraphBuilder2 builder, object source,
                                        IBaseFilter grabber, IBaseFilter renderer)
        {
            GCHandle capture = GCHandle.Alloc(DirectShow.PIN_CATEGORY_CAPTURE, GCHandleType.Pinned);
            GCHandle preview = GCHandle.Alloc(DirectShow.PIN_CATEGORY_PREVIEW, GCHandleType.Pinned);
            GCHandle video = GCHandle.Alloc(DirectShow.MEDIATYPE_Video, GCHandleType.Pinned);

            try
            {
                IntPtr capturePtr = capture.AddrOfPinnedObject();
                IntPtr previewPtr = preview.AddrOfPinnedObject();
                IntPtr videoPtr = video.AddrOfPinnedObject();

                int hr = builder.RenderStream(capturePtr, videoPtr, source, grabber, renderer);
                if (hr >= 0) return hr;

                hr = builder.RenderStream(previewPtr, videoPtr, source, grabber, renderer);
                if (hr >= 0) return hr;

                hr = builder.RenderStream(IntPtr.Zero, videoPtr, source, grabber, renderer);
                if (hr >= 0) return hr;

                return builder.RenderStream(IntPtr.Zero, IntPtr.Zero, source, grabber, renderer);
            }
            finally
            {
                capture.Free();
                preview.Free();
                video.Free();
            }
        }

        private bool ReadConnectedFormat(ISampleGrabber grabber)
        {
            AmMediaType connected = new AmMediaType();
            try
            {
                if (grabber.GetConnectedMediaType(connected) < 0 ||
                    connected.FormatPtr == IntPtr.Zero ||
                    connected.FormatSize < Marshal.SizeOf(typeof(VideoInfoHeader)))
                {
                    status = "device did not report a usable video format";
                    return false;
                }

                VideoInfoHeader header =
                    (VideoInfoHeader)Marshal.PtrToStructure(connected.FormatPtr, typeof(VideoInfoHeader));

                frameWidth = header.BmiHeader.Width;
                frameHeight = Math.Abs(header.BmiHeader.Height);
                frameBitCount = header.BmiHeader.BitCount;

                if (frameWidth <= 0 || frameHeight <= 0)
                {
                    status = "device reported an empty video format";
                    return false;
                }

                // The last connection attempt asks for no particular format, so whatever came back has
                // to be identified here rather than assumed.
                frameSubtype = connected.SubType;
                frameBufferSize = header.BmiHeader.ImageSize;

                if (frameSubtype == DirectShow.MEDIASUBTYPE_RGB24) frameLayout = FrameLayout.Rgb24;
                else if (frameSubtype == DirectShow.MEDIASUBTYPE_RGB32) frameLayout = FrameLayout.Rgb32;
                else if (frameSubtype == DirectShow.MEDIASUBTYPE_NV12) frameLayout = FrameLayout.Nv12;
                else if (frameSubtype == DirectShow.MEDIASUBTYPE_YUY2) frameLayout = FrameLayout.Yuy2;
                else if (frameSubtype == DirectShow.MEDIASUBTYPE_UYVY) frameLayout = FrameLayout.Uyvy;
                else if (frameBitCount == 24) frameLayout = FrameLayout.Rgb24;
                else if (frameBitCount == 32) frameLayout = FrameLayout.Rgb32;
                else
                {
                    status = "device only offers " + DescribeSubtype(frameSubtype) +
                             " (" + frameBitCount + " bpp), which this component cannot decode";
                    return false;
                }

                if (frameBufferSize <= 0)
                {
                    frameBufferSize = DefaultBufferSize(frameLayout, frameWidth, frameHeight);
                }

                status = frameWidth + "x" + frameHeight + " " + DescribeSubtype(frameSubtype);
                return true;
            }
            finally
            {
                connected.Free();
            }
        }

        // Polls until Dispose. Returns false if the graph never produced a single frame, so the caller
        // can try a different pixel format; true once at least one has arrived.
        private bool PollLoop(ISampleGrabber grabber, IMediaFilter mediaFilter)
        {
            // A little slack over the reported size: some filters round their sample size up, and
            // GetCurrentBuffer fails outright if the buffer it is handed is smaller than the sample.
            byte[] buffer = new byte[frameBufferSize + 64];
            GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);

            string resolution = frameWidth + "x" + frameHeight;
            bool everDelivered = false;
            int pollsSinceFrame = 0;

            try
            {
                IntPtr bufferPtr = pinned.AddrOfPinnedObject();

                while (!stopRequested)
                {
                    int size = buffer.Length;
                    int hr = grabber.GetCurrentBuffer(ref size, bufferPtr);

                    if (hr == DirectShow.S_OK && size > 0)
                    {
                        Rectangle decodeRegion = Rectangle.Empty;

                        lock (frameLock)
                        {
                            if (latestBuffer == null || latestBuffer.Length != buffer.Length)
                            {
                                latestBuffer = new byte[buffer.Length];
                            }

                            Buffer.BlockCopy(buffer, 0, latestBuffer, 0, buffer.Length);
                            hasFrame = true;

                            // Decode one frame ahead, but only once the last one has been collected -
                            // that keeps the decode rate at the consumption rate instead of the
                            // device's, without ever making the consumer wait for it.
                            if (pendingFrame == null && pendingRegion.Width > 0 && pendingRegion.Height > 0)
                            {
                                decodeRegion = pendingRegion;
                            }
                        }

                        if (decodeRegion.Width > 0)
                        {
                            // Outside the lock, and from this thread's own buffer rather than the
                            // shared one, so a caller collecting a frame never blocks behind it.
                            Bitmap decoded = BufferToBitmap(buffer, decodeRegion);
                            lock (frameLock)
                            {
                                if (pendingFrame == null && pendingRegion == decodeRegion)
                                {
                                    pendingFrame = decoded;
                                }
                                else
                                {
                                    decoded.Dispose();
                                }
                            }
                        }

                        everDelivered = true;
                        pollsSinceFrame = 0;
                        status = resolution;
                    }
                    else if (!everDelivered)
                    {
                        // A connected graph that delivers nothing has to say so. Reporting just the
                        // resolution here reads as "working" next to a blank preview, which is exactly
                        // the wrong impression - a device can accept a pixel format it never actually
                        // produces, which is precisely what OBS's virtual camera does with RGB.
                        if (++pollsSinceFrame > FirstFrameTimeoutPolls)
                        {
                            // Let the caller try another format rather than sitting here forever on a
                            // connection that does not work.
                            status = resolution + " - connected, but no frames";
                            return false;
                        }
                    }
                    else if (++pollsSinceFrame > NoFrameWarningPolls)
                    {
                        // Frames were arriving and have stopped: a card unplugged, a driver reset, a
                        // signal drop. Keep the last frame and say so rather than spinning silently.
                        int graphState;
                        mediaFilter.GetState(0, out graphState);
                        status = resolution + " - frames stopped (signal lost?)" +
                                 (graphState == 2 ? "" : " state=" + graphState);
                    }

                    Thread.Sleep(PollIntervalMs);
                }

                return true;
            }
            finally
            {
                pinned.Free();
            }
        }

        // FourCC media subtypes are {'2''1''V''N'}-0000-0010-8000-00AA00389B71 and friends, so the
        // first four bytes of the GUID spell the format name. Anything else is reported as a raw GUID.
        private static string DescribeSubtype(Guid subtype)
        {
            if (subtype == DirectShow.MEDIASUBTYPE_RGB24) return "RGB24";
            if (subtype == DirectShow.MEDIASUBTYPE_RGB32) return "RGB32";

            byte[] bytes = subtype.ToByteArray();
            bool printable = true;
            for (int i = 0; i < 4; i++)
            {
                if (bytes[i] < 32 || bytes[i] > 126) printable = false;
            }

            if (!printable)
            {
                return subtype.ToString();
            }

            return new string(new[] { (char)bytes[0], (char)bytes[1], (char)bytes[2], (char)bytes[3] });
        }

        private static int DefaultBufferSize(FrameLayout layout, int width, int height)
        {
            switch (layout)
            {
                case FrameLayout.Rgb32: return (((width * 4) + 3) & ~3) * height;
                case FrameLayout.Nv12: return width * height * 3 / 2;
                case FrameLayout.Yuy2:
                case FrameLayout.Uyvy: return width * 2 * height;
                default: return (((width * 3) + 3) & ~3) * height;
            }
        }

        private Bitmap BufferToBitmap(byte[] buffer, Rectangle region)
        {
            switch (frameLayout)
            {
                case FrameLayout.Nv12:
                    return Nv12ToBitmap(buffer, frameWidth, frameHeight, region);
                case FrameLayout.Yuy2:
                    return PackedYuvToBitmap(buffer, frameWidth, frameHeight, region, lumaFirst: true);
                case FrameLayout.Uyvy:
                    return PackedYuvToBitmap(buffer, frameWidth, frameHeight, region, lumaFirst: false);
                default:
                    return RgbToBitmap(buffer, frameWidth, frameHeight, region,
                                       frameLayout == FrameLayout.Rgb32 ? 32 : 24);
            }
        }

        // RGB sample grabber buffers are bottom-up DIBs, so the rows are copied in reverse.
        //
        // 24bpp and 32bpp are both plain BGR(A) in the channel order the rest of the detector assumes,
        // so the copy is a straight memcpy per row either way - only the stride differs.
        private static Bitmap RgbToBitmap(byte[] buffer, int width, int height, Rectangle region, int bitCount)
        {
            PixelFormat format = bitCount == 32 ? PixelFormat.Format32bppRgb : PixelFormat.Format24bppRgb;
            int bytesPerPixel = bitCount / 8;
            int stride = ((width * bytesPerPixel) + 3) & ~3;

            Bitmap bmp = new Bitmap(region.Width, region.Height, format);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, region.Width, region.Height),
                                           ImageLockMode.WriteOnly, format);
            try
            {
                int rowBytes = region.Width * bytesPerPixel;
                for (int y = 0; y < region.Height; y++)
                {
                    int sourceRow = height - 1 - (region.Top + y);
                    int sourceOffset = sourceRow * stride + region.Left * bytesPerPixel;
                    IntPtr destination = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);
                    Marshal.Copy(buffer, sourceOffset, destination, rowBytes);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return bmp;
        }

        // NV12: a full-resolution luma plane, then one half-resolution plane of interleaved U,V - so
        // each chroma pair covers a 2x2 block of pixels.
        //
        // Unlike the RGB layouts these are stored top-down, so the rows are not reversed.
        // Decoded output is 32bpp, not 24bpp. Writing the extra byte costs nothing in a loop that is
        // already doing the colour maths per pixel, and it is what GDI+ works in natively - handing
        // ResizeImage a 24bpp source makes it convert first, which measured slower than the decode.
        private static Bitmap Nv12ToBitmap(byte[] buffer, int width, int height, Rectangle region)
        {
            int lumaSize = width * height;
            Bitmap bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppRgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, region.Width, region.Height),
                                           ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                var row = new byte[data.Stride];
                bool hd = height >= 720;

                for (int y = 0; y < region.Height; y++)
                {
                    int sourceY = region.Top + y;
                    int lumaRow = sourceY * width;
                    int chromaRow = lumaSize + (sourceY / 2) * width;

                    for (int x = 0; x < region.Width; x++)
                    {
                        int sourceX = region.Left + x;
                        int chroma = chromaRow + (sourceX & ~1);
                        WriteRgb(row, x * 4, buffer[lumaRow + sourceX], buffer[chroma], buffer[chroma + 1], hd);
                    }

                    Marshal.Copy(row, 0, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), data.Stride);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return bmp;
        }

        // YUY2 is Y0 U Y1 V, UYVY is U Y0 V Y1 - one chroma pair per two horizontally adjacent pixels.
        private static Bitmap PackedYuvToBitmap(byte[] buffer, int width, int height, Rectangle region,
                                                bool lumaFirst)
        {
            int stride = width * 2;
            Bitmap bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppRgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, region.Width, region.Height),
                                           ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                var row = new byte[data.Stride];
                bool hd = height >= 720;

                for (int y = 0; y < region.Height; y++)
                {
                    int sourceRow = (region.Top + y) * stride;

                    for (int x = 0; x < region.Width; x++)
                    {
                        int sourceX = region.Left + x;
                        int pair = sourceRow + (sourceX >> 1) * 4;
                        int luma = lumaFirst ? pair + ((sourceX & 1) << 1) : pair + 1 + ((sourceX & 1) << 1);
                        int u = lumaFirst ? pair + 1 : pair;
                        int v = lumaFirst ? pair + 3 : pair + 2;

                        WriteRgb(row, x * 4, buffer[luma], buffer[u], buffer[v], hd);
                    }

                    Marshal.Copy(row, 0, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), data.Stride);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return bmp;
        }

        // Studio-swing YUV to full-range RGB, written BGR to match Format24bppRgb's byte order.
        //
        // The matrix is picked by frame height: BT.709 at 720p and above, BT.601 below. Capture
        // formats carry no reliable colorimetry flag, and this is the same heuristic every decoder
        // falls back on. It matters here beyond looking right - the detector gates on median hue in a
        // 10-unit window, so the wrong matrix would shift the mask's blue out of band.
        private static void WriteRgb(byte[] row, int offset, byte luma, byte u, byte v, bool bt709)
        {
            int c = luma - 16;
            int d = u - 128;
            int e = v - 128;

            int r, g, b;
            if (bt709)
            {
                r = (298 * c + 459 * e + 128) >> 8;
                g = (298 * c - 55 * d - 136 * e + 128) >> 8;
                b = (298 * c + 541 * d + 128) >> 8;
            }
            else
            {
                r = (298 * c + 409 * e + 128) >> 8;
                g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                b = (298 * c + 516 * d + 128) >> 8;
            }

            row[offset] = Clamp8(b);
            row[offset + 1] = Clamp8(g);
            row[offset + 2] = Clamp8(r);
        }

        private static byte Clamp8(int value)
        {
            return value < 0 ? (byte)0 : (value > 255 ? (byte)255 : (byte)value);
        }
    }
}

