using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Xfer.Probe;

/// <summary>Sends a folder through Xfer's own optical path with no screen and no camera: the QR frames the page
/// shows (QrStream, drawn as ScaledQr images) are placed in camera-sized pictures and given to the desktop camera
/// receiver, whose decoded files must equal the originals. Then the same with FX2 (Fx2Stream, Fx2Image), the camera
/// seeing only the middle of the window and a new picture every second camera frame.</summary>
internal static class NativeLoopbackProbe
{
    private static int _failures;

    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "xfer-loopback-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        // Unicode paths and content verify lossless transfer across platforms.
        string source = Path.Combine(root, "送る資料"), output = Path.Combine(root, "output");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "写真"));
            var random = new Random(20261017);
            foreach (int previewWidth in new[] { 7, 32, 33 })
            {
                int previewHeight = 19;
                var input = Bytes(random, previewWidth * previewHeight * 4);
                var preview = NativeBitmap.Downscale(input, previewWidth, previewHeight, (previewWidth + 1) / 2);
                bool exact = true;
                for (int y = 0; y < preview.Height; y++)
                    for (int x = 0; x < preview.Width; x++)
                    {
                        for (int channel = 0; channel < 3; channel++)
                        {
                            int at = (y * 2 * previewWidth + x * 2) * 4 + channel;
                            int expected = (input[at] + input[at + 4] + input[at + previewWidth * 4] + input[at + previewWidth * 4 + 4]) / 4;
                            if (preview.Pixels[(y * preview.Width + x) * 4 + channel] != expected) exact = false;
                        }
                        if (preview.Pixels[(y * preview.Width + x) * 4 + 3] != 255) exact = false;
                    }
                Check(exact, "preview box filter equals channel averages at width " + previewWidth);
            }
            var originals = new Dictionary<string, byte[]>
            {
                ["メモ.txt"] = Encoding.UTF8.GetBytes("光学転送のテストです。\n"),
                ["data.csv"] = Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Range(0, 400).Select(i => i + "," + i * i))),
                ["写真/IMG_0001.jpg"] = Bytes(random, 48_000),
                ["見積書.pdf"] = Bytes(random, 21_000),
            };
            foreach (var file in originals) File.WriteAllBytes(Path.Combine(source, file.Key.Replace('/', Path.DirectorySeparatorChar)), file.Value);

            var folder = new AppState(source).ReadFolder("optical");
            Check(folder.Files.Count == originals.Count, "the folder lists " + originals.Count + " files");
            var optical = new OpticalService();
            var started = optical.Start(folder, folder.Files.Select(f => f.Name).ToList(), 1000, 30, "L");
            using var frames = new QrStream(optical, started.Token);

            var first = frames.Frame(0, 3);
            Check(first.Width == first.Side * 3 && first.Height == first.Width, "a QR frame is drawn 3 pixels per module");
            var pixels = new byte[first.Width * first.Height * 4];
            first.CopyTo(pixels);
            Check(pixels[0] == 255 && pixels[3] == 255, "the quiet zone is white and opaque");

            var receiver = new OpticalReceiveService(output);
            var camera = new SyntheticCamera(frames, 1280, 720, 3);
            using var reader = new CameraReceiver(camera, receiver, 2, false);
            reader.Start();
            var clock = Stopwatch.StartNew();
            object status = reader.Status();
            while (clock.Elapsed < TimeSpan.FromSeconds(120))
            {
                status = reader.Status();
                if ((bool)Read(status, "Complete") || Read(status, "Error") != null) break;
                Thread.Sleep(100);
            }
            camera.Dispose();
            Check((bool)Read(status, "Complete"), "the receiver completes in " + clock.Elapsed.TotalSeconds.ToString("0.0") + " s (" + Read(status, "Captured") + " pictures, error: " + (Read(status, "Error") ?? "none") + ")");
            var result = Read(status, "Result") as OpticalReceiveResult;
            if (result != null && result.Complete)
            {
                foreach (var file in originals)
                {
                    string received = Path.Combine(result.OutputPath, file.Key.Replace('/', Path.DirectorySeparatorChar));
                    Check(File.Exists(received) && File.ReadAllBytes(received).SequenceEqual(file.Value), "received " + file.Key + " equals the original");
                }
            }
            optical.Stop(started.Token);

            // FX2: a 1600 x 900 window of 3-pixel cells with a coarse middle; the camera sees its middle 1280 x 720.
            var fx2Started = optical.StartFx2(folder, folder.Files.Select(f => f.Name).ToList(), 15, "M");
            using var pictures = new Fx2Stream(optical, fx2Started.Token, "M");
            var layout = pictures.Layout(1600, 900, 3, 2);
            Check(layout.Patches > 20 && layout.Tiers.Length == 2, "the FX2 layout has a coarse middle (" + layout.Patches + " patches)");
            var fx2Output = Path.Combine(root, "output-fx2");
            var fx2Receiver = new OpticalReceiveService(fx2Output);
            var fx2Camera = new Fx2Camera(pictures, layout, 1280, 720);
            using (var fx2Reader = new CameraReceiver(fx2Camera, fx2Receiver, 2, false))
            {
                fx2Reader.Start();
                clock.Restart();
                status = fx2Reader.Status();
                while (clock.Elapsed < TimeSpan.FromSeconds(120))
                {
                    status = fx2Reader.Status();
                    if ((bool)Read(status, "Complete") || Read(status, "Error") != null) break;
                    Thread.Sleep(100);
                }
                fx2Camera.Dispose();
            }
            Check((bool)Read(status, "Complete") && (string)Read(status, "Layout") == "fx2", "the receiver completes from FX2 in " + clock.Elapsed.TotalSeconds.ToString("0.0") + " s (" + Read(status, "Captured") +
                " pictures, " + Read(status, "Patches") + " patches, error: " + (Read(status, "Error") ?? "none") + ")");
            result = Read(status, "Result") as OpticalReceiveResult;
            if (result != null && result.Complete)
            {
                foreach (var file in originals)
                {
                    string received = Path.Combine(result.OutputPath, file.Key.Replace('/', Path.DirectorySeparatorChar));
                    Check(File.Exists(received) && File.ReadAllBytes(received).SequenceEqual(file.Value), "FX2: received " + file.Key + " equals the original");
                }
            }
            optical.Stop(fx2Started.Token);

            // Focus hold: a camera that can hold its focus, sharp for 4 s, blurred for 4 s, sharp again (the file
            // outlasts the schedule). Held after the first seconds, released in the blur, held again after it.
            string large = Path.Combine(root, "大きい資料");
            Directory.CreateDirectory(large);
            File.WriteAllBytes(Path.Combine(large, "data.bin"), Bytes(random, 3_000_000));
            var largeFolder = new AppState(large).ReadFolder("optical");
            var focusStarted = optical.StartFx2(largeFolder, largeFolder.Files.Select(f => f.Name).ToList(), 15, "M");
            using (var focusPictures = new Fx2Stream(optical, focusStarted.Token, "M"))
            {
                var focusLayout = focusPictures.Layout(1600, 900, 3, 0);
                var focusCamera = new Fx2Camera(focusPictures, focusLayout, 1280, 720);
                var schedule = Stopwatch.StartNew();
                focusCamera.Blurred = () => schedule.Elapsed.TotalSeconds >= 4 && schedule.Elapsed.TotalSeconds < 8;
                using (var focusReader = new CameraReceiver(focusCamera, new OpticalReceiveService(Path.Combine(root, "output-focus")), 2, false))
                {
                    focusReader.Start();
                    Thread.Sleep(12000);
                    focusCamera.Dispose();
                }
                var events = focusCamera.FocusEvents;
                Console.WriteLine("     focus events: " + string.Join(", ", events.Select(e => e.Item2 + " " + e.Item1.ToString("0.0") + " s")));
                Check(events.Count > 0 && events[0].Item2 == "hold" && events[0].Item1 >= 2 && events[0].Item1 < 4, "the focus is held once the first seconds are past");
                Check(events.Any(e => e.Item2 == "release" && e.Item1 >= 4 && e.Item1 < 7.5), "a blurred picture hands the focus back to autofocus");
                Check(events.Any(e => e.Item2 == "hold" && e.Item1 >= 8), "sharp again, the focus is held again");
            }
            optical.Stop(focusStarted.Token);
        }
        catch (Exception error)
        {
            Check(false, "no exception: " + error);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine(_failures == 0 ? "PASS" : "FAIL " + _failures);
        return _failures == 0 ? 0 : 1;
    }

    private static byte[] Bytes(Random random, int length) { var data = new byte[length]; random.NextBytes(data); return data; }

    private static object? Read(object status, string name) => status.GetType().GetProperty(name)?.GetValue(status);

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok) _failures++;
    }

    /// <summary>A camera that films the middle of an FX2 window: a new picture every second camera frame (the
    /// sender's 15 per second against 30), as the page advances the sequence by the layout's patches.</summary>
    private sealed class Fx2Camera : ICameraSource, IFocusHold
    {
        private readonly Fx2Stream _pictures;
        private readonly Fx2Layout _layout;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Thread? _thread;
        private volatile bool _stopped;
        /// <summary>While true the pictures are blurred (a box of 9 pixels), as with the focus far off.</summary>
        public Func<bool> Blurred = () => false;
        public readonly List<Tuple<double, string>> FocusEvents = new List<Tuple<double, string>>();
        public void HoldFocus() { lock (FocusEvents) FocusEvents.Add(Tuple.Create(_clock.Elapsed.TotalSeconds, "hold")); }
        public void ReleaseFocus() { lock (FocusEvents) FocusEvents.Add(Tuple.Create(_clock.Elapsed.TotalSeconds, "release")); }

        public Fx2Camera(Fx2Stream pictures, Fx2Layout layout, int width, int height) { _pictures = pictures; _layout = layout; Width = width; Height = height; }

        public string Name => "synthetic fx2";
        public int Width { get; }
        public int Height { get; }
        public double FramesPerSecond => 30;
        public string? Error => null;
        public bool Running => !_stopped;

        public void Start(Action<byte[], int, int> deliver)
        {
            _thread = new Thread(() =>
            {
                int n = 0;
                var screen = new byte[_layout.Width * _layout.Height * 4];
                while (!_stopped)
                {
                    if (n % 2 == 0) new Fx2Image(_pictures.Picture((uint)(n / 2 * _layout.Patches), _layout)).CopyTo(screen);
                    n++;
                    var picture = new byte[Width * Height * 4];
                    int left = (_layout.Width - Width) / 2, top = (_layout.Height - Height) / 2;
                    for (int y = 0; y < Height; y++)
                        Buffer.BlockCopy(screen, ((top + y) * _layout.Width + left) * 4, picture, y * Width * 4, Width * 4);
                    if (Blurred()) BoxBlur(picture, Width, Height, 4);
                    deliver(picture, Width, Height);
                    Thread.Sleep(1000 / 30);
                }
            }) { IsBackground = true, Name = "synthetic fx2 camera" };
            _thread.Start();
        }

        public void Dispose() { _stopped = true; _thread?.Join(2000); }

        private static void BoxBlur(byte[] bgra, int width, int height, int radius)
        {
            var gray = new int[width * height];
            for (int i = 0; i < gray.Length; i++) gray[i] = bgra[i * 4 + 1];
            var temp = new int[gray.Length];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int sum = 0, n = 0;
                    for (int k = Math.Max(0, x - radius); k <= Math.Min(width - 1, x + radius); k++) { sum += gray[y * width + k]; n++; }
                    temp[y * width + x] = sum / n;
                }
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int sum = 0, n = 0;
                    for (int k = Math.Max(0, y - radius); k <= Math.Min(height - 1, y + radius); k++) { sum += temp[k * width + x]; n++; }
                    byte v = (byte)(sum / n);
                    int o = (y * width + x) * 4;
                    bgra[o] = bgra[o + 1] = bgra[o + 2] = v;
                }
        }
    }

    /// <summary>A camera that films the sender's screen: each picture is one QR frame, centred on white.</summary>
    private sealed class SyntheticCamera : ICameraSource
    {
        private readonly QrStream _frames;
        private readonly int _scale;
        private Thread? _thread;
        private volatile bool _stopped;

        public SyntheticCamera(QrStream frames, int width, int height, int scale) { _frames = frames; Width = width; Height = height; _scale = scale; }

        public string Name => "synthetic";
        public int Width { get; }
        public int Height { get; }
        public double FramesPerSecond => 30;
        public string? Error => null;
        public bool Running => !_stopped;

        public void Start(Action<byte[], int, int> deliver)
        {
            _thread = new Thread(() =>
            {
                uint sequence = 0;
                while (!_stopped)
                {
                    var qr = _frames.Frame(sequence++, _scale);
                    var square = new byte[qr.Width * qr.Height * 4];
                    qr.CopyTo(square);
                    var picture = new byte[Width * Height * 4];
                    Array.Fill(picture, (byte)255);
                    int left = (Width - qr.Width) / 2, top = (Height - qr.Height) / 2;
                    for (int y = 0; y < qr.Height; y++)
                        Buffer.BlockCopy(square, y * qr.Width * 4, picture, ((top + y) * Width + left) * 4, qr.Width * 4);
                    deliver(picture, Width, Height);
                    Thread.Sleep(1000 / 30);
                }
            }) { IsBackground = true, Name = "synthetic camera" };
            _thread.Start();
        }

        public void Dispose() { _stopped = true; _thread?.Join(2000); }
    }
}
