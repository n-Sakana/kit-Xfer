using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using ZXing;
using ZXing.Common;

namespace Xfer
{
    /// <summary>A camera that delivers opaque BGRA frames on its own thread until disposed.</summary>
    internal interface ICameraSource : IDisposable
    {
        string Name { get; }
        int Width { get; }
        int Height { get; }
        double FramesPerSecond { get; }
        /// <summary>Why the camera stopped, in words for the user; null while it runs.</summary>
        string Error { get; }
        bool Running { get; }
        void Start(Action<byte[], int, int> frame);
    }

    /// <summary>A camera with temporary focus locking. Changing screen patterns can cause autofocus hunting;
    /// the receiver holds focus while decoding succeeds and releases it when reception degrades.
    /// Cameras without this interface retain their own focus control.</summary>
    internal interface IFocusHold
    {
        void HoldFocus();
        void ReleaseFocus();
    }

    internal sealed class CameraInfo
    {
        public CameraInfo(string id, string name) { Id = id; Name = name; }
        public string Id { get; private set; }
        public string Name { get; private set; }
    }

    /// <summary>Reads Xfer frames from a camera: keeps the newest frame for the preview, and on worker threads
    /// reads every FX2 patch in the picture, or when no FX2 markers are in view, a QR code around the last QR
    /// position (with a periodic full search); every frame read goes to the fountain decoder. The receiver needs no
    /// setting for which the sender shows.</summary>
    internal sealed class CameraReceiver : IDisposable
    {
        public const string ReceiverId = "desktop-camera";
        private const int PreviewWidth = 960;
        private readonly ICameraSource _camera;
        private readonly OpticalReceiveService _receiver;
        private readonly bool _openWhenDone;
        private readonly int _workers;
        private readonly object _gate = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<Thread> _threads = new List<Thread>();
        private byte[] _frame; private int _frameWidth, _frameHeight; private long _frameNumber, _takenNumber;
        private NativeBitmap _preview; private int _previewSequence;
        private OpticalReceiveResult _result;
        private string _error;
        private bool _stopped, _complete;
        private long _captured, _decoded, _recognized, _patches;
        private long _patchAttempts, _lumaDecoded, _colorSeen, _colorDecoded;
        private int _markers, _patchesSeen;
        private string _layout = "";
        private double _lastRecognizedMs = -1, _startedMs;
        // Focus hold. A picture is sharp when most of its FX2 patches read without the blur correction (or a QR
        // code reads); its quality is the patches read (1 for a QR code). The focus is held after three sharp
        // pictures in a row whose quality is near the best seen (which fades by 1 % a second), not in the first
        // seconds (the camera may open with the last session's focus); it is handed back to autofocus when for
        // ReleaseAfterMs no picture keeps 60 % of the quality it was held at.
        private const int HoldAfterGood = 3;
        private const double ReleaseAfterMs = 2000, SettleMs = 2000;
        private int _goodRun;
        private bool _focusHeld;
        private double _lastGoodMs, _bestQuality, _bestAt, _heldQuality;
        private readonly double[] _recent = new double[3];
        private int _recentCount;
        private double _lastCaptureMs = -1, _lastDecodeMs = -1;
        private Tracking _tracking;

        public CameraReceiver(ICameraSource camera, OpticalReceiveService receiver, int workers, bool openWhenDone)
        {
            _camera = camera; _receiver = receiver; _openWhenDone = openWhenDone; _workers = Math.Max(1, Math.Min(3, workers));
        }

        public void Start()
        {
            Diagnostics.Event("camera.start", "native", new { Workers = _workers, Camera = Diagnostics.Id(_camera.Name) });
            _receiver.Reset(ReceiverId);
            _startedMs = _clock.Elapsed.TotalMilliseconds;
            for (int i = 0; i < _workers; i++)
            {
                var thread = new Thread(Decode) { IsBackground = true, Name = "Xfer QR decoder " + (i + 1) };
                _threads.Add(thread); thread.Start();
            }
            try { _camera.Start(OnFrame); }
            catch (Exception error) { Diagnostics.Failure("camera.start", "native", error); throw; }
        }

        private void OnFrame(byte[] bgra, int width, int height)
        {
            var stamp = Diagnostics.Stamp();
            var preview = NativeBitmap.Downscale(bgra, width, height, PreviewWidth);
            lock (_gate)
            {
                if (_stopped) return;
                Diagnostics.Sample("camera.capture", "native", Diagnostics.Milliseconds(stamp), new { Width = width, Height = height, Replaced = _frameNumber != _takenNumber });
                _captured++;
                _lastCaptureMs = _clock.Elapsed.TotalMilliseconds;
                _frame = bgra; _frameWidth = width; _frameHeight = height; _frameNumber++;
                _preview = preview; _previewSequence++;
                Monitor.Pulse(_gate);
            }
        }

        public NativeBitmap Preview { get { lock (_gate) return _preview; } }

        public bool Running { get { lock (_gate) return !_stopped && !_complete && _error == null && _camera.Running; } }

        private void Decode()
        {
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = false,
                Options = new DecodingOptions { TryHarder = true, PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE } }
            };
            var fx2 = new Fx2Reader();
            byte[] gray = new byte[0], full = new byte[0];
            while (true)
            {
                byte[] frame; int width, height; long number; Tracking tracking;
                lock (_gate)
                {
                    while (!_stopped && !_complete && (_frame == null || _frameNumber == _takenNumber)) Monitor.Wait(_gate, 250);
                    if (_stopped || _complete) return;
                    frame = _frame; width = _frameWidth; height = _frameHeight; number = _frameNumber; _takenNumber = number;
                    tracking = _tracking;
                }
                if (full.Length < width * height) full = new byte[width * height];
                for (int i = 0, src = 0; i < width * height; i++, src += 4) full[i] = (byte)((frame[src] * 29 + frame[src + 1] * 150 + frame[src + 2] * 77) >> 8);
                if (ReadFx2(fx2, full, width, height, frame)) continue;
                // Search near the last QR; every sixth frame (or after misses) the whole image, like the browser reader.
                var region = Region(width, height, tracking, number, _clock.Elapsed.TotalMilliseconds);
                int size = region.Width * region.Height;
                if (gray.Length < size) gray = new byte[size];
                for (int y = 0; y < region.Height; y++) Buffer.BlockCopy(full, (region.Y + y) * width + region.X, gray, y * region.Width, region.Width);
                var stamp = Diagnostics.Stamp();
                Result result = null;
                try { result = reader.Decode(new RGBLuminanceSource(gray, region.Width, region.Height, RGBLuminanceSource.BitmapFormat.Gray8)); }
                catch (Exception error) { Diagnostics.Sample("camera.decode_exception", "native", 0,
                    new { Type = error.GetType().Name, Code = error.HResult, Site = "CameraReceiver.Decode:ZXing.Decode" }); result = null; }
                byte[] bytes = Bytes(result);
                Diagnostics.Sample(result == null ? "camera.decode_miss" : "camera.qr_read", "native",
                    Diagnostics.Milliseconds(stamp), new { CropWidth = region.Width, CropHeight = region.Height });
                if (result != null && bytes == null) Diagnostics.Sample("camera.non_binary_qr", "native", 0, null);
                OpticalReceiveResult progress = null;
                string failure = null;
                if (bytes != null)
                {
                    try { progress = _receiver.AddFrame(ReceiverId, bytes, _openWhenDone); }
                    catch (Exception error) { Diagnostics.Failure("camera.receive", "native", error); failure = error.Message; }
                }
                lock (_gate)
                {
                    _decoded++;
                    _lastDecodeMs = _clock.Elapsed.TotalMilliseconds;
                    if (failure != null) { _error = failure; Monitor.PulseAll(_gate); return; }
                    if (progress != null && progress.Recognized)
                    {
                        _recognized++;
                        _layout = "qr";
                        _lastRecognizedMs = _clock.Elapsed.TotalMilliseconds;
                        _result = progress;
                        var points = result.ResultPoints;
                        if (points != null && points.Length >= 3)
                        {
                            float left = points.Min(p => p.X) + region.X, right = points.Max(p => p.X) + region.X;
                            float top = points.Min(p => p.Y) + region.Y, bottom = points.Max(p => p.Y) + region.Y;
                            _tracking = new Tracking { Left = left, Right = right, Top = top, Bottom = bottom, Width = width, Height = height, At = _lastRecognizedMs };
                        }
                        if (progress.Complete) { _complete = true; Monitor.PulseAll(_gate); }
                    }
                    else if (_tracking != null) _tracking.Misses++;
                }
                bool readQr = progress != null && progress.Recognized;
                Focus(readQr ? 1 : 0, readQr);
            }
        }

        /// <summary>Holds or releases the camera's focus after a picture (see the fields above).</summary>
        private void Focus(double quality, bool sharp)
        {
            var camera = _camera as IFocusHold;
            if (camera == null) return;
            bool hold = false, release = false;
            double average;
            lock (_gate)
            {
                double now = _clock.Elapsed.TotalMilliseconds;
                _recent[_recentCount++ % _recent.Length] = quality;
                average = 0;
                int n = Math.Min(_recentCount, _recent.Length);
                for (int i = 0; i < n; i++) average += _recent[i];
                average /= n;
                _bestQuality *= Math.Pow(0.99, (now - _bestAt) / 1000); _bestAt = now;
                if (average > _bestQuality) _bestQuality = average;
                if (sharp && average >= 0.85 * _bestQuality) _goodRun++; else _goodRun = 0;
                if (_focusHeld)
                {
                    if (quality >= 0.6 * _heldQuality) _lastGoodMs = now;
                    else if (now - _lastGoodMs > ReleaseAfterMs) { _focusHeld = false; release = true; _goodRun = 0; }
                }
                else if (_goodRun >= HoldAfterGood && now - _startedMs >= SettleMs)
                {
                    _focusHeld = hold = true; _heldQuality = average; _lastGoodMs = now;
                }
            }
            try
            {
                if (hold) { camera.HoldFocus(); Diagnostics.Event("camera.focus_hold", "native", new { Quality = average, Best = _bestQuality }); }
                if (release) { camera.ReleaseFocus(); Diagnostics.Event("camera.focus_release", "native", new { Quality = quality, Held = _heldQuality }); }
            }
            catch (Exception error) { Diagnostics.Failure("camera.focus", "native", error); }
        }

        /// <summary>Reads the FX2 patches of one picture; false when no FX2 code is in view (the QR reader takes the
        /// picture then).</summary>
        private bool ReadFx2(Fx2Reader fx2, byte[] gray, int width, int height, byte[] bgra)
        {
            Fx2Reader.Result read;
            try { read = fx2.Read(gray, width, height, bgra); }
            catch (Exception error)
            {
                Diagnostics.Sample("camera.decode_exception", "native", 0, new { Type = error.GetType().Name, Code = error.HResult, Site = "CameraReceiver.ReadFx2" });
                return false;
            }
            // FX2 is on screen when a patch reads, or when markers with matching IDs sit where neighbours should be
            // (two patches' corners), even if no patch reads (focus, distance). Readable marker IDs alone are not
            // enough: a QR code's finder and alignment patterns look like markers.
            if (read.Frames.Count == 0 && read.Patches.Count < 2) return false;
            // A majority of cleanly sampled patches indicates useful focus; a stricter threshold can
            // prolong autofocus hunting despite CRC-valid reception.
            Focus(read.LumaDecoded, read.LumaDecoded >= 3 && read.Plain * 10 >= read.LumaDecoded * 6);
            Diagnostics.Sample("camera.fx2_read", "native", read.Ms, new { Markers = read.Markers.Count, Patches = read.Patches.Count, Decoded = read.Frames.Count,
                LumaDecoded = read.LumaDecoded, Plain = read.Plain, ColorSeen = read.ColorSeen, ColorDecoded = read.ColorDecoded,
                Corrected = read.Corrected, Candidates = read.Candidates, Tracked = read.Tracked, ThresholdMs = read.ThresholdMs, BlobMs = read.BlobMs, IdMs = read.IdMs, PatchMs = read.PatchMs });
            OpticalReceiveResult progress = null;
            string failure = null;
            int recognized = 0;
            foreach (var bytes in read.Frames)
            {
                try
                {
                    var result = _receiver.AddFrame(ReceiverId, bytes, _openWhenDone);
                    if (result != null && result.Recognized) { recognized++; progress = result; if (result.Complete) break; }
                }
                catch (Exception error) { Diagnostics.Failure("camera.receive", "native", error); failure = error.Message; break; }
            }
            lock (_gate)
            {
                _decoded++;
                _lastDecodeMs = _clock.Elapsed.TotalMilliseconds;
                _layout = "fx2"; _markers = read.Markers.Count; _patchesSeen = read.Patches.Count; _patches += recognized;
                _patchAttempts += read.Patches.Count; _lumaDecoded += read.LumaDecoded;
                _colorSeen += read.ColorSeen; _colorDecoded += read.ColorDecoded;
                if (failure != null) { _error = failure; Monitor.PulseAll(_gate); return true; }
                if (progress != null)
                {
                    _recognized++;
                    _lastRecognizedMs = _clock.Elapsed.TotalMilliseconds;
                    if (_result == null || !_result.Complete) _result = progress;
                    if (progress.Complete) { _complete = true; Monitor.PulseAll(_gate); }
                }
            }
            return true;
        }

        private static byte[] Bytes(Result result)
        {
            object value;
            if (result == null || result.ResultMetadata == null || !result.ResultMetadata.TryGetValue(ResultMetadataType.BYTE_SEGMENTS, out value)) return null;
            var segments = value as IEnumerable<byte[]>;
            return segments == null ? null : segments.SelectMany(segment => segment).ToArray(); // binary segments, never decoded text
        }

        private sealed class Tracking { public float Left, Right, Top, Bottom; public int Width, Height, Misses; public double At; }
        private struct Rect { public int X, Y, Width, Height; }

        private static Rect Region(int width, int height, Tracking t, long frame, double now)
        {
            var full = new Rect { X = 0, Y = 0, Width = width, Height = height };
            // Finder-pattern centres sit inside the symbol: pad generously so the quiet zone is in the crop.
            if (t == null || t.Width != width || t.Height != height || now - t.At > 1200 || t.Misses >= 3 || frame % 6 == 0) return full;
            float span = Math.Max(t.Right - t.Left, t.Bottom - t.Top);
            float pad = Math.Max(48, span * 0.45f);
            int left = Math.Max(0, (int)(t.Left - pad)), top = Math.Max(0, (int)(t.Top - pad));
            int right = Math.Min(width, (int)Math.Ceiling(t.Right + pad)), bottom = Math.Min(height, (int)Math.Ceiling(t.Bottom + pad));
            if (right - left < 64 || bottom - top < 64) return full;
            return new Rect { X = left, Y = top, Width = right - left, Height = bottom - top };
        }

        public object Status()
        {
            lock (_gate)
            {
                double now = _clock.Elapsed.TotalMilliseconds;
                Diagnostics.Sample("camera.status", "native", 0, new { Captured = _captured, Decoded = _decoded,
                    Recognized = _recognized, Running = _camera.Running, Failed = _error != null || _camera.Error != null,
                    Width = _camera.Width, Height = _camera.Height, Fps = _camera.FramesPerSecond,
                    PatchAttempts = _patchAttempts, LumaDecoded = _lumaDecoded, ColorSeen = _colorSeen, ColorDecoded = _colorDecoded,
                    CaptureAgeMs = now - (_lastCaptureMs >= 0 ? _lastCaptureMs : _startedMs),
                    DecodeAgeMs = now - (_lastDecodeMs >= 0 ? _lastDecodeMs : _startedMs),
                    ReadAgeMs = now - (_lastRecognizedMs >= 0 ? _lastRecognizedMs : _startedMs) });
                return new
                {
                    Running = !_stopped && !_complete && _error == null && _camera.Running,
                    Complete = _complete,
                    Error = _error ?? _camera.Error,
                    Camera = _camera.Name,
                    Width = _camera.Width,
                    Height = _camera.Height,
                    Fps = Math.Round(_camera.FramesPerSecond, 1),
                    Captured = _captured,
                    Decoded = _decoded,
                    Recognized = _recognized,
                    // What the camera last saw: "fx2" (markers, patches read in total, patches in the last picture) or "qr".
                    Layout = _layout,
                    Markers = _markers,
                    Patches = _patches,
                    PatchesSeen = _patchesSeen,
                    PatchAttempts = _patchAttempts, LumaDecoded = _lumaDecoded, ColorSeen = _colorSeen, ColorDecoded = _colorDecoded,
                    PreviewSequence = _previewSequence,
                    // Milliseconds since a Xfer QR was last read (since start when none was read yet).
                    QuietMs = (long)(now - (_lastRecognizedMs >= 0 ? _lastRecognizedMs : _startedMs)),
                    Result = _result
                };
            }
        }

        public void Dispose()
        {
            lock (_gate) { if (_stopped) return; _stopped = true; Monitor.PulseAll(_gate); }
            Diagnostics.Event("camera.stop", "native", new { Complete = _complete, Failed = _error != null || _camera.Error != null, Captured = _captured, Decoded = _decoded, Recognized = _recognized });
            try { _camera.Dispose(); } catch (Exception) { }
            foreach (var thread in _threads) thread.Join(2000);
            if (!_complete) _receiver.Stop(ReceiverId);
        }
    }
}
