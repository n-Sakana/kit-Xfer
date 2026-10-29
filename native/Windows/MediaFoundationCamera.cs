using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Xfer
{
    /// <summary>Video capture through Media Foundation's source reader (raw COM vtables, Native AOT safe).
    /// The reader converts the camera's format to RGB32; frames are delivered as opaque BGRA.</summary>
    internal sealed unsafe class MediaFoundationCamera : ICameraSource, IFocusHold
    {
        private readonly string _id;
        private readonly int _wantedWidth;
        private readonly double _wantedFps;
        private readonly CameraTuning _tuning;
        private readonly object _controlGate = new object();
        private readonly CameraFocusHold _focusHold;
        private nint _source; // the live IMFMediaSource while capturing, for the camera controls
        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _running;
        private volatile string _error;
        private string _name = "";
        private int _width, _height;
        private double _fps;
        // Opt-in laboratory hooks. Normal callers retain closest-format selection.
        public int RequestedHeight { get; set; }
        public bool RequireExactFormat { get; set; }
        public event Action BeforeShutdown;

        public MediaFoundationCamera(string id, int width, int fps) : this(id, width, fps, null) { }
        public MediaFoundationCamera(string id, int width, int fps, CameraTuning tuning) : this(id, width, (double)fps, tuning) { }
        public MediaFoundationCamera(string id, int width, double fps, CameraTuning tuning)
        {
            _id = id ?? ""; _wantedWidth = width; _wantedFps = fps; _tuning = tuning;
            _focusHold = new CameraFocusHold(_controlGate,
                () => ReadControl(CameraControlFocus, true),
                value => SetControl(true, CameraControlFocus, value, "focus"));
        }

        public void HoldFocus() { _focusHold.Hold(); }
        public void ReleaseFocus() { _focusHold.Release(); }

        public string Name { get { return _name; } }
        public int Width { get { return _width; } }
        public int Height { get { return _height; } }
        public double FramesPerSecond { get { return _fps; } }
        public string Error { get { return _error; } }
        public bool Running { get { return _running; } }

        public static List<CameraInfo> List()
        {
            var result = new List<CameraInfo>();
            bool com = CoInitializeEx(0, COINIT_MULTITHREADED) >= 0;
            try
            {
                if (MFStartup(MF_VERSION, MFSTARTUP_LITE) < 0) return result;
                try
                {
                    void** devices = null; uint count = 0;
                    if (EnumDevices(&devices, &count) < 0) return result;
                    try
                    {
                        for (uint i = 0; i < count; i++)
                            result.Add(new CameraInfo(ReadString(devices[i], ref MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK), ReadString(devices[i], ref MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME)));
                    }
                    finally { for (uint i = 0; i < count; i++) Release(devices[i]); CoTaskMemFree(devices); }
                }
                finally { MFShutdown(); }
            }
            finally { if (com) CoUninitialize(); }
            return result;
        }

        /// <summary>Opens the selected device briefly; enumerate advertised native types without starting capture.</summary>
        public static List<CameraNativeFormat> ListFormats(string id)
        {
            var result = new List<CameraNativeFormat>();
            bool com = CoInitializeEx(0, COINIT_MULTITHREADED) >= 0, mf = false;
            void* source = null, reader = null;
            try
            {
                Check(MFStartup(MF_VERSION, MFSTARTUP_LITE), "MFStartup"); mf = true;
                void** devices = null; uint count = 0;
                Check(EnumDevices(&devices, &count), "MFEnumDeviceSources");
                try
                {
                    for (uint i = 0; i < count; i++)
                    {
                        if (ReadString(devices[i], ref MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK) != id) continue;
                        Guid iid = IID_IMFMediaSource;
                        Check(((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)V(devices[i])[33])(devices[i], &iid, &source), "ActivateObject");
                        break;
                    }
                }
                finally { for (uint i = 0; i < count; i++) Release(devices[i]); CoTaskMemFree(devices); }
                if (source == null) throw new CameraException("Selected camera was not found.");
                Check(MFCreateSourceReaderFromMediaSource(source, null, &reader), "MFCreateSourceReaderFromMediaSource");
                for (uint index = 0; ; index++)
                {
                    void* type = null;
                    int hr = ((delegate* unmanaged[Stdcall]<void*, uint, uint, void**, int>)V(reader)[5])(reader, FIRST_VIDEO_STREAM, index, &type);
                    if (hr == unchecked((int)0xC00D36B9)) break; // MF_E_NO_MORE_TYPES
                    Check(hr, "GetNativeMediaType");
                    try
                    {
                        ulong size, rate; Guid subtype;
                        Check(GetUInt64(type, ref MF_MT_FRAME_SIZE, out size), "Get frame size");
                        Check(GetUInt64(type, ref MF_MT_FRAME_RATE, out rate), "Get frame rate");
                        Check(GetGuid(type, ref MF_MT_SUBTYPE, out subtype), "Get subtype");
                        result.Add(new CameraNativeFormat { Index = (int)index, Width = (int)(size >> 32), Height = (int)(uint)size,
                            FpsNumerator = (uint)(rate >> 32), FpsDenominator = (uint)rate, Subtype = subtype.ToString() });
                    }
                    finally { Release(type); }
                }
            }
            finally
            {
                Release(reader);
                if (source != null) { ((delegate* unmanaged[Stdcall]<void*, int>)V(source)[12])(source); Release(source); }
                if (mf) MFShutdown();
                if (com) CoUninitialize();
            }
            return result;
        }

        public void Start(Action<byte[], int, int> frame)
        {
            _running = true;
            _thread = new Thread(() => Capture(frame)) { IsBackground = true, Name = "Xfer camera" };
            _thread.Start();
        }

        private void Capture(Action<byte[], int, int> deliver)
        {
            bool com = CoInitializeEx(0, COINIT_MULTITHREADED) >= 0;
            bool mf = false;
            void* source = null, reader = null, attributes = null;
            try
            {
                Check(MFStartup(MF_VERSION, MFSTARTUP_LITE), "MFStartup"); mf = true;
                void** devices = null; uint count = 0;
                Check(EnumDevices(&devices, &count), "MFEnumDeviceSources");
                try
                {
                    if (count == 0) throw new CameraException("No camera was found.");
                    uint pick = 0;
                    for (uint i = 0; i < count; i++) if (_id.Length > 0 && ReadString(devices[i], ref MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK) == _id) pick = i;
                    _name = ReadString(devices[pick], ref MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
                    Guid iid = IID_IMFMediaSource;
                    int hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)V(devices[pick])[33])(devices[pick], &iid, &source); // IMFActivate::ActivateObject
                    if (hr < 0) Diagnostics.Event("camera.mf.activate.failed", "native", new { Code = hr });
                    if (hr == unchecked((int)0x80070005)) throw new CameraException("Camera access is denied. In Windows Settings, open Privacy & security > Camera and allow desktop apps to access the camera.");
                    if (hr == unchecked((int)0xC00D3704) || hr == unchecked((int)0x800700AA)) throw new CameraException("Could not open the camera. Check whether another app is using it.");
                    Check(hr, "ActivateObject");
                    lock (_controlGate)
                    {
                        _source = (nint)source;
                        ApplyTuning(source);
                    }
                }
                finally { for (uint i = 0; i < count; i++) Release(devices[i]); CoTaskMemFree(devices); }

                Check(MFCreateAttributes(&attributes, 2), "MFCreateAttributes");
                SetUInt32(attributes, ref MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
                SetUInt32(attributes, ref MF_LOW_LATENCY, 1);
                Check(MFCreateSourceReaderFromMediaSource(source, attributes, &reader), "MFCreateSourceReaderFromMediaSource");
                ChooseFormat(reader);
                Diagnostics.Event("camera.opened", "native", new { Camera = Diagnostics.Id(_name), Width = _width, Height = _height, Fps = _fps,
                    Exposure = ReadControl(CameraControlExposure, true).ToString(), Focus = ReadControl(CameraControlFocus, true).ToString(), Gain = ReadControl(VideoProcAmpGain, false).ToString() });
                while (!_stop)
                {
                    uint stream, flags; long time; void* sample = null;
                    int hr = ((delegate* unmanaged[Stdcall]<void*, uint, uint, uint*, uint*, long*, void**, int>)V(reader)[9])(reader, FIRST_VIDEO_STREAM, 0, &stream, &flags, &time, &sample); // ReadSample
                    if (hr < 0 || (flags & 3) != 0) Diagnostics.Event("camera.mf.read.failed", "native", new { Code = hr, Flags = flags });
                    if (hr < 0) throw new CameraException(hr == unchecked((int)0xC00D3EA2) || hr == unchecked((int)0x80070491) ? "The camera was disconnected." : "Could not read a camera frame (0x" + hr.ToString("x8") + ").");
                    if ((flags & 0x2) != 0) throw new CameraException("The camera stream ended."); // END OF STREAM
                    if ((flags & 0x1) != 0) throw new CameraException("A camera error occurred.");
                    if (sample == null) continue;
                    try { var pixels = Copy(sample); if (pixels != null && !_stop) deliver(pixels, _width, _height); }
                    finally { Release(sample); }
                }
            }
            catch (CameraException error) { Diagnostics.Failure("camera.mf", "native", error); _error = error.Message; }
            catch (Exception error) { Diagnostics.Failure("camera.mf", "native", error); _error = "Camera unavailable: " + error.Message; }
            finally
            {
                // Restore our temporary hold before a laboratory hook restores its original settings.
                StopFocus();
                try { if (BeforeShutdown != null) BeforeShutdown(); }
                catch (Exception error) { _error = "Camera cleanup: " + error.Message; }
                _running = false;
                lock (_controlGate) _source = 0;
                if (reader != null) Release(reader);
                if (attributes != null) Release(attributes);
                if (source != null) { ((delegate* unmanaged[Stdcall]<void*, int>)V(source)[12])(source); Release(source); } // IMFMediaSource::Shutdown
                if (mf) MFShutdown();
                if (com) CoUninitialize();
            }
        }

        /// <summary>The native format closest to the requested width and frame rate, then RGB32 output.</summary>
        private void ChooseFormat(void* reader)
        {
            int bestIndex = -1; double bestScore = double.MaxValue; int bw = 0, bh = 0; double bfps = 0;
            for (uint i = 0; ; i++)
            {
                void* type = null;
                int hr = ((delegate* unmanaged[Stdcall]<void*, uint, uint, void**, int>)V(reader)[5])(reader, FIRST_VIDEO_STREAM, i, &type); // GetNativeMediaType
                if (hr < 0) break;
                try
                {
                    ulong size, rate; Guid subtype;
                    if (GetUInt64(type, ref MF_MT_FRAME_SIZE, out size) < 0 || GetUInt64(type, ref MF_MT_FRAME_RATE, out rate) < 0 || GetGuid(type, ref MF_MT_SUBTYPE, out subtype) < 0) continue;
                    int w = (int)(size >> 32), h = (int)(size & 0xffffffff);
                    double fps = (uint)(rate & 0xffffffff) == 0 ? 0 : (double)(uint)(rate >> 32) / (uint)(rate & 0xffffffff);
                    if (w <= 0 || h <= 0 || w > 4096 || h > 4096) continue;
                    if (RequireExactFormat && (w != _wantedWidth || (RequestedHeight > 0 && h != RequestedHeight) || Math.Abs(fps - _wantedFps) > 0.05)) continue;
                    double score = Math.Abs(w - _wantedWidth) * 10 + Math.Max(0, _wantedFps - fps) * 40 + Math.Max(0, fps - _wantedFps) + (w * 9 != h * 16 ? 30 : 0);
                    if (subtype == MFVideoFormat_MJPG) score += 5;
                    if (score < bestScore) { bestScore = score; bestIndex = (int)i; bw = w; bh = h; bfps = fps; }
                }
                finally { Release(type); }
            }
            if (RequireExactFormat && bestIndex < 0) throw new CameraException("Requested camera resolution/fps is not supported.");
            if (bestIndex >= 0)
            {
                void* native = null;
                if (((delegate* unmanaged[Stdcall]<void*, uint, uint, void**, int>)V(reader)[5])(reader, FIRST_VIDEO_STREAM, (uint)bestIndex, &native) >= 0)
                {
                    int setHr = ((delegate* unmanaged[Stdcall]<void*, uint, uint*, void*, int>)V(reader)[7])(reader, FIRST_VIDEO_STREAM, null, native); // SetCurrentMediaType
                    Release(native);
                    if (RequireExactFormat) Check(setHr, "SetCurrentMediaType native");
                }
            }
            void* output = null;
            Check(MFCreateMediaType(&output), "MFCreateMediaType");
            try
            {
                SetGuid(output, ref MF_MT_MAJOR_TYPE, ref MFMediaType_Video);
                SetGuid(output, ref MF_MT_SUBTYPE, ref MFVideoFormat_RGB32);
                Check(((delegate* unmanaged[Stdcall]<void*, uint, uint*, void*, int>)V(reader)[7])(reader, FIRST_VIDEO_STREAM, null, output), "SetCurrentMediaType RGB32");
            }
            finally { Release(output); }
            void* current = null;
            Check(((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)V(reader)[6])(reader, FIRST_VIDEO_STREAM, &current), "GetCurrentMediaType");
            try
            {
                ulong size, rate;
                if (GetUInt64(current, ref MF_MT_FRAME_SIZE, out size) >= 0) { _width = (int)(size >> 32); _height = (int)(size & 0xffffffff); }
                else { _width = bw; _height = bh; }
                _fps = GetUInt64(current, ref MF_MT_FRAME_RATE, out rate) >= 0 && (uint)(rate & 0xffffffff) != 0 ? (double)(uint)(rate >> 32) / (uint)(rate & 0xffffffff) : bfps;
            }
            finally { Release(current); }
            if (_width <= 0 || _height <= 0) throw new CameraException("Could not select a camera resolution.");
            if (RequireExactFormat && (_width != _wantedWidth || (RequestedHeight > 0 && _height != RequestedHeight) || Math.Abs(_fps - _wantedFps) > 0.05))
                throw new CameraException("Camera output differs from the requested resolution/fps.");
        }

        private byte[] Copy(void* sample)
        {
            void* buffer = null, buffer2d = null;
            Check(((delegate* unmanaged[Stdcall]<void*, void**, int>)V(sample)[41])(sample, &buffer), "ConvertToContiguousBuffer");
            try
            {
                int w = _width, h = _height, row = w * 4;
                var pixels = new byte[row * h];
                Guid iid = IID_IMF2DBuffer;
                if (((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)V(buffer)[0])(buffer, &iid, &buffer2d) >= 0 && buffer2d != null)
                {
                    byte* scan0; int pitch;
                    Check(((delegate* unmanaged[Stdcall]<void*, byte**, int*, int>)V(buffer2d)[3])(buffer2d, &scan0, &pitch), "Lock2D");
                    try { for (int y = 0; y < h; y++) Marshal.Copy((nint)(scan0 + (long)y * pitch), pixels, y * row, row); }
                    finally { ((delegate* unmanaged[Stdcall]<void*, int>)V(buffer2d)[4])(buffer2d); }
                }
                else
                {
                    byte* data; uint max, length;
                    Check(((delegate* unmanaged[Stdcall]<void*, byte**, uint*, uint*, int>)V(buffer)[3])(buffer, &data, &max, &length), "Lock");
                    try { if (length < row * h) return null; Marshal.Copy((nint)data, pixels, 0, row * h); }
                    finally { ((delegate* unmanaged[Stdcall]<void*, int>)V(buffer)[4])(buffer); }
                }
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                return pixels;
            }
            finally { if (buffer2d != null) Release(buffer2d); Release(buffer); }
        }

        public void Dispose()
        {
            _stop = true;
            StopFocus();
            var thread = _thread;
            if (thread != null && thread != Thread.CurrentThread) thread.Join(3000);
        }

        /// <summary>Wait for capture, shutdown hooks and device release to finish.</summary>
        public bool WaitForStop(int milliseconds) { return _thread == null || _thread.Join(milliseconds); }

        private void StopFocus()
        {
            try { _focusHold.Stop(); }
            catch (Exception error) { Diagnostics.Failure("camera.focus.cleanup", "native", error); }
        }

        private sealed class CameraException : Exception { public CameraException(string message) : base(message) { } }


        // ---------------------------------------------------------------- camera controls (IAMCameraControl / IAMVideoProcAmp on the device source)
        public const int CameraControlExposure = 4, CameraControlFocus = 6, VideoProcAmpBrightness = 0, VideoProcAmpGain = 9;
        private const int ControlAuto = 1, ControlManual = 2;

        private void ApplyTuning(void* source)
        {
            var tuning = _tuning;
            if (tuning == null) return;
            if (tuning.ExposureLog2.HasValue) SetControl(source, true, CameraControlExposure, tuning.ExposureLog2.Value, "exposure");
            if (tuning.Focus.HasValue) SetControl(source, true, CameraControlFocus, tuning.Focus.Value, "focus");
            if (tuning.Gain.HasValue) SetControl(source, false, VideoProcAmpGain, tuning.Gain.Value, "gain");
        }

        /// <summary>Set one control to a manual value (clamped to the device's range), or back to automatic when value is null.</summary>
        public bool SetControl(bool cameraControl, int property, int? value, string what)
        {
            lock (_controlGate)
            {
                if (_source == 0) return false;
                // Hold/Release run on decoder workers, not the capture thread that initialized COM.
                bool com = CoInitializeEx(0, COINIT_MULTITHREADED) >= 0;
                try { return SetControl((void*)_source, cameraControl, property, value, what); }
                finally { if (com) CoUninitialize(); }
            }
        }

        private static bool SetControl(void* source, bool cameraControl, int property, int? value, string what)
        {
            void* control = QueryControl(source, cameraControl);
            if (control == null) { Diagnostics.Event("camera.control.unavailable", what, null); return false; }
            try
            {
                int min, max, step, fallback, caps;
                int hr = ((delegate* unmanaged[Stdcall]<void*, int, int*, int*, int*, int*, int*, int>)V(control)[3])(control, property, &min, &max, &step, &fallback, &caps); // GetRange
                if (hr < 0) { Diagnostics.Event("camera.control.unsupported", what, new { Code = hr }); return false; }
                int target = value.HasValue ? Math.Max(min, Math.Min(max, value.Value)) : fallback;
                hr = ((delegate* unmanaged[Stdcall]<void*, int, int, int, int>)V(control)[4])(control, property, target, value.HasValue ? ControlManual : ControlAuto); // Set
                Diagnostics.Event("camera.control.set", what, new { Value = target, Automatic = !value.HasValue, Code = hr });
                return hr >= 0;
            }
            finally { Release(control); }
        }

        /// <summary>The device's current range, value and mode for one control (while the camera runs).</summary>
        public CameraControlState ReadControl(int property, bool cameraControl)
        {
            lock (_controlGate)
            {
                if (_source == 0) return new CameraControlState();
                bool com = CoInitializeEx(0, COINIT_MULTITHREADED) >= 0;
                try { return ReadControl((void*)_source, property, cameraControl); }
                finally { if (com) CoUninitialize(); }
            }
        }

        private static CameraControlState ReadControl(void* source, int property, bool cameraControl)
        {
            var state = new CameraControlState();
            void* control = QueryControl(source, cameraControl);
            if (control == null) return state;
            try
            {
                int min, max, step, fallback, caps, value, flags;
                if (((delegate* unmanaged[Stdcall]<void*, int, int*, int*, int*, int*, int*, int>)V(control)[3])(control, property, &min, &max, &step, &fallback, &caps) < 0) return state;
                if (((delegate* unmanaged[Stdcall]<void*, int, int*, int*, int>)V(control)[5])(control, property, &value, &flags) < 0) return state; // Get
                state.Supported = true; state.Minimum = min; state.Maximum = max; state.Step = step; state.Default = fallback; state.Value = value; state.Automatic = (flags & ControlAuto) != 0; state.Capabilities = caps; state.Flags = flags;
                return state;
            }
            finally { Release(control); }
        }

        private static void* QueryControl(void* source, bool cameraControl)
        {
            void* control = null;
            Guid iid = cameraControl ? IID_IAMCameraControl : IID_IAMVideoProcAmp;
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)V(source)[0])(source, &iid, &control) >= 0 ? control : null;
        }

        private static readonly Guid IID_IAMCameraControl = new Guid("c6e13370-30ac-11d0-a18c-00a0c9118956");
        private static readonly Guid IID_IAMVideoProcAmp = new Guid("c6e13360-30ac-11d0-a18c-00a0c9118956");

        // ---------------------------------------------------------------- Media Foundation (mfplat, mf, mfreadwrite)
        private const uint MF_VERSION = 0x00020070, MFSTARTUP_LITE = 1, COINIT_MULTITHREADED = 0, FIRST_VIDEO_STREAM = 0xFFFFFFFC;
        private static Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new Guid("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
        private static Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID = new Guid("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
        private static Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME = new Guid("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
        private static Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK = new Guid("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
        private static Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new Guid("0f81da2c-b537-4672-a8b2-a681b17307a3");
        private static Guid MF_LOW_LATENCY = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        private static Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        private static Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00aa00389b71");
        private static Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_MJPG = new Guid("47504a4d-0000-0010-8000-00aa00389b71");
        private static readonly Guid IID_IMFMediaSource = new Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66");
        private static readonly Guid IID_IMF2DBuffer = new Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");

        private static void** V(void* p) { return *(void***)p; }
        private static void Release(void* p) { if (p != null) ((delegate* unmanaged[Stdcall]<void*, uint>)V(p)[2])(p); }
        private static void Check(int hr, string what) {
            if (hr < 0) {
                // 'what' is a fixed API name at each call site, never driver-provided text.
                Diagnostics.Event("camera.mf.api.failed", what, new { Code = hr });
                throw new CameraException("Camera unavailable (" + what + " 0x" + hr.ToString("x8") + ").");
            }
        }

        private static int EnumDevices(void*** devices, uint* count)
        {
            void* attributes = null;
            int hr = MFCreateAttributes(&attributes, 1);
            if (hr < 0) return hr;
            try
            {
                SetGuid(attributes, ref MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, ref MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
                return MFEnumDeviceSources(attributes, devices, count);
            }
            finally { Release(attributes); }
        }

        // IMFAttributes slots: GetUINT64 8, GetGUID 10, GetAllocatedString 13, SetUINT32 21, SetGUID 24.
        private static int GetUInt64(void* attributes, ref Guid key, out ulong value)
        {
            ulong v = 0; int hr;
            fixed (Guid* k = &key) hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, ulong*, int>)V(attributes)[8])(attributes, k, &v);
            value = v; return hr;
        }
        private static int GetGuid(void* attributes, ref Guid key, out Guid value)
        {
            Guid v; int hr;
            fixed (Guid* k = &key) hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, int>)V(attributes)[10])(attributes, k, &v);
            value = v; return hr;
        }
        private static string ReadString(void* attributes, ref Guid key)
        {
            char* text = null; uint length = 0; int hr;
            fixed (Guid* k = &key) hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, char**, uint*, int>)V(attributes)[13])(attributes, k, &text, &length);
            if (hr < 0 || text == null) return "";
            try { return new string(text, 0, (int)length); } finally { CoTaskMemFree(text); }
        }
        private static void SetUInt32(void* attributes, ref Guid key, uint value)
        {
            fixed (Guid* k = &key) ((delegate* unmanaged[Stdcall]<void*, Guid*, uint, int>)V(attributes)[21])(attributes, k, value);
        }
        private static void SetGuid(void* attributes, ref Guid key, ref Guid value)
        {
            fixed (Guid* k = &key) fixed (Guid* v = &value) ((delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, int>)V(attributes)[24])(attributes, k, v);
        }

        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateAttributes(void** attributes, uint initialSize);
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(void** type);
        [DllImport("mf.dll", ExactSpelling = true)] private static extern int MFEnumDeviceSources(void* attributes, void*** sources, uint* count);
        [DllImport("mfreadwrite.dll", ExactSpelling = true)] private static extern int MFCreateSourceReaderFromMediaSource(void* source, void* attributes, void** reader);
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(nint reserved, uint coinit);
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoTaskMemFree(void* memory);
    }

    internal sealed class CameraNativeFormat
    {
        public int Index, Width, Height;
        public uint FpsNumerator, FpsDenominator;
        public string Subtype;
        public double Fps { get { return FpsDenominator == 0 ? 0 : (double)FpsNumerator / FpsDenominator; } }
        public string SubtypeCode
        {
            get
            {
                var bytes = new Guid(Subtype).ToByteArray();
                for (int i = 0; i < 4; i++) if (bytes[i] < 32 || bytes[i] > 126) return Subtype;
                return System.Text.Encoding.ASCII.GetString(bytes, 0, 4);
            }
        }
    }
}
