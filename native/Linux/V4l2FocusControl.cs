using System;
using System.Runtime.InteropServices;

namespace Xfer
{
    /// <summary>Focus controls on the same V4L2 device ffmpeg captures. No v4l2-ctl dependency.
    /// Camera-class controls use G/S_EXT_CTRLS; layout/request numbers below are for Linux x64/arm64.</summary>
    internal sealed unsafe class V4l2FocusControl : IDisposable
    {
        private const uint FocusAbsolute = 0x009a090a, FocusAuto = 0x009a090c;
        private const uint GetControls = 0xc0205647, SetControls = 0xc0205648;
        private readonly string _device;
        private readonly object _gate = new object();
        private readonly CameraFocusHold _hold;
        private int _fd = -1;
        private bool _disposed;

        public V4l2FocusControl(string device)
        {
            _device = device;
            _hold = new CameraFocusHold(_gate, Read, Set);
        }

        public void Hold() { _hold.Hold(); }
        public void Release() { _hold.Release(); }

        private CameraControlState Read()
        {
            int automatic = 0, value = 0;
            if (!Control(GetControls, FocusAuto, ref automatic) || !Control(GetControls, FocusAbsolute, ref value))
                return new CameraControlState();
            return new CameraControlState { Supported = true, Automatic = automatic != 0, Value = value, Capabilities = 3 };
        }

        private bool Set(int? value)
        {
            int automatic = value.HasValue ? 0 : 1;
            // FOCUS_ABSOLUTE while AF is on is undefined; turn AF off first, then apply the sampled value.
            if (!Control(SetControls, FocusAuto, ref automatic)) return false;
            if (value.HasValue)
            {
                int absolute = value.Value;
                if (!Control(SetControls, FocusAbsolute, ref absolute)) return false; // The shared owner restores AF.
            }
            Diagnostics.Event("camera.control.set", "focus", new { Value = value, Automatic = !value.HasValue, Code = 0 });
            return true;
        }

        private bool Control(uint request, uint id, ref int value)
        {
            if (_disposed || !OperatingSystem.IsLinux()
                || (RuntimeInformation.ProcessArchitecture != Architecture.X64 && RuntimeInformation.ProcessArchitecture != Architecture.Arm64))
                return false;
            if (_fd < 0)
            {
                _fd = Open(_device, 2 | 0x800 | 0x80000); // O_RDWR | O_NONBLOCK | O_CLOEXEC; no stream ownership.
                if (_fd < 0) { Failed("open"); return false; }
            }
            var control = new ExtendedControl { Id = id, Value = value };
            var controls = new ExtendedControls { Count = 1, Controls = (nint)(&control) }; // which=CUR_VAL (0)
            if (Ioctl(_fd, request, ref controls) < 0)
            {
                Failed(request == GetControls ? "get" : "set");
                return false;
            }
            value = control.Value;
            return true;
        }

        private static void Failed(string operation)
        {
            Diagnostics.Sample("camera.focus.v4l2.failed", operation, 0, new { Code = Marshal.GetLastPInvokeError() });
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                try { _hold.Stop(); }
                catch (Exception error) { Diagnostics.Failure("camera.focus.cleanup", "native", error); }
                finally
                {
                    _disposed = true;
                    if (_fd >= 0) { Close(_fd); _fd = -1; }
                }
            }
        }

        // include/uapi/linux/videodev2.h: packed v4l2_ext_control and pointer-aligned v4l2_ext_controls.
        [StructLayout(LayoutKind.Explicit, Size = 20, Pack = 1)]
        private struct ExtendedControl
        {
            [FieldOffset(0)] public uint Id;
            [FieldOffset(4)] public uint Size;
            [FieldOffset(8)] public uint Reserved;
            [FieldOffset(12)] public int Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedControls
        {
            public uint Which, Count, ErrorIndex;
            public int RequestFd;
            public uint Reserved;
            public nint Controls;
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags);
        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)] private static extern int Ioctl(int fd, nuint request, ref ExtendedControls controls);
        [DllImport("libc", EntryPoint = "close")] private static extern int Close(int fd);
    }
}
