using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace Xfer
{
    /// <summary>A V4L2 camera read through ffmpeg: raw BGRA frames on its standard output, one new buffer per frame.</summary>
    internal sealed class FfmpegCamera : ICameraSource, IFocusHold
    {
        private readonly string _device;
        private readonly int _fps;
        private readonly object _gate = new object();
        private readonly Stopwatch _clock = new Stopwatch();
        private Process _process;
        private Thread _reader, _errors;
        private volatile bool _stopped;
        private string _error, _errorTail = "";
        private long _frames;
        private readonly V4l2FocusControl _focus;

        public FfmpegCamera(string device, int width, int fps)
        {
            if (device == null || !Regex.IsMatch(device, @"^/dev/video[0-9]+$")) throw new ArgumentException("カメラは /dev/videoN で指定してください。");
            _device = device; _fps = fps;
            _focus = new V4l2FocusControl(device);
            Width = width; Height = width * 9 / 16;
            Name = DeviceName(device);
        }

        public string Name { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        public double FramesPerSecond
        {
            get
            {
                double seconds = _clock.Elapsed.TotalSeconds;
                return seconds > 1 ? Interlocked.Read(ref _frames) / seconds : _fps;
            }
        }

        public string Error { get { lock (_gate) return _error; } }
        public bool Running { get { return !_stopped && Error == null; } }

        public void HoldFocus() { if (!_stopped && _process != null) _focus.Hold(); }
        public void ReleaseFocus() { _focus.Release(); }

        public void Start(Action<byte[], int, int> frame)
        {
            var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            string size = Width.ToString(CultureInfo.InvariantCulture) + "x" + Height.ToString(CultureInfo.InvariantCulture);
            string fit = string.Format(CultureInfo.InvariantCulture, "scale={0}:{1}:force_original_aspect_ratio=decrease,pad={0}:{1}:(ow-iw)/2:(oh-ih)/2", Width, Height);
            foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "v4l2", "-framerate", _fps.ToString(CultureInfo.InvariantCulture),
                "-video_size", size, "-i", _device, "-an", "-vf", fit, "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1" })
                info.ArgumentList.Add(arg);
            try { _process = Process.Start(info); }
            catch (Exception error) { Diagnostics.Failure("camera.ffmpeg.start", "native", error); throw new InvalidOperationException("ffmpeg を起動できません。ffmpeg が入っているか確かめてください。"); }
            _clock.Start();
            _errors = new Thread(ReadErrors) { IsBackground = true, Name = "Xfer camera errors" };
            _errors.Start();
            _reader = new Thread(() => ReadFrames(frame)) { IsBackground = true, Name = "Xfer camera" };
            _reader.Start();
        }

        private void ReadFrames(Action<byte[], int, int> deliver)
        {
            var output = _process.StandardOutput.BaseStream;
            try
            {
                while (!_stopped)
                {
                    var bgra = new byte[Width * Height * 4];
                    int read = 0;
                    while (read < bgra.Length)
                    {
                        int n = output.Read(bgra, read, bgra.Length - read);
                        if (n <= 0) throw new EndOfStreamException();
                        read += n;
                    }
                    Interlocked.Increment(ref _frames);
                    deliver(bgra, Width, Height);
                }
            }
            catch (Exception error)
            {
                if (_stopped) return;
                Diagnostics.Failure("camera.ffmpeg.read", "native", error);
                try { _process.WaitForExit(1000); } catch (Exception) { }
                string tail;
                lock (_gate) tail = _errorTail.Trim();
                lock (_gate) _error = "カメラの映像が止まりました。カメラと撮影幅・fps を確かめてください。" + (tail.Length > 0 ? " (" + tail + ")" : "");
            }
            finally { _focus.Dispose(); }
        }

        private void ReadErrors()
        {
            try
            {
                string line;
                while ((line = _process.StandardError.ReadLine()) != null)
                {
                    // Keep device paths and driver-provided text out of persistent diagnostics.
                    string reason = line.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0 ? "permission"
                        : line.IndexOf("busy", StringComparison.OrdinalIgnoreCase) >= 0 ? "busy"
                        : line.IndexOf("No such file", StringComparison.OrdinalIgnoreCase) >= 0 ? "missing_device"
                        : line.IndexOf("Invalid argument", StringComparison.OrdinalIgnoreCase) >= 0 ? "configuration"
                        : "driver_error";
                    Diagnostics.Sample("camera.ffmpeg.stderr", reason, 0, null);
                    lock (_gate) { _errorTail = (_errorTail + " " + line).Trim(); if (_errorTail.Length > 400) _errorTail = _errorTail.Substring(_errorTail.Length - 400); }
                }
            }
            catch (Exception) { }
        }

        /// <summary>The device's own name (/sys/class/video4linux/videoN/name), or its path.</summary>
        public static string DeviceName(string device)
        {
            string name = ReadLine(Path.Combine("/sys/class/video4linux", Path.GetFileName(device), "name"));
            return string.IsNullOrEmpty(name) ? device : name;
        }

        public static string ReadLine(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
            catch (Exception) { return null; }
        }

        public void Dispose()
        {
            _stopped = true;
            _focus.Dispose();
            var process = _process;
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(true); } catch (Exception) { }
            if (_reader != null) _reader.Join(2000);
            if (_errors != null) _errors.Join(1000);
            process.Dispose();
        }
    }
}
