using System;

namespace Xfer
{
    /// <summary>Owns only the temporary change from autofocus to manual focus. The supplied gate also protects
    /// the device's control handle, so a decoder cannot change focus while the camera is being released.</summary>
    internal sealed class CameraFocusHold
    {
        private readonly object _gate;
        private readonly Func<CameraControlState> _read;
        private readonly Func<int?, bool> _set;
        private bool _restoreAuto, _closed;

        public CameraFocusHold(object gate, Func<CameraControlState> read, Func<int?, bool> set)
        {
            _gate = gate; _read = read; _set = set;
        }

        public void Hold()
        {
            lock (_gate)
            {
                if (_closed || _restoreAuto) return;
                var before = _read();
                // Fixed-focus devices and an explicitly manual setting must remain untouched.
                if (!before.Supported || !before.Automatic || (before.Capabilities & 3) != 3) return;
                // A driver may disable AF before failing to set the absolute value. Own the restoration first.
                _restoreAuto = true;
                try
                {
                    bool set = _set(before.Value);
                    var after = _read();
                    bool held = set && after.Supported && !after.Automatic && after.Value == before.Value;
                    Diagnostics.Event("camera.focus.applied", "hold",
                        new { Success = held, Requested = before.Value, Value = after.Value, Automatic = after.Automatic, Supported = after.Supported });
                    if (!held) Release();
                }
                catch
                {
                    Release();
                    throw;
                }
            }
        }

        public void Release()
        {
            lock (_gate)
            {
                if (!_restoreAuto) return;
                bool set = _set(null);
                var after = _read();
                bool released = set && after.Supported && after.Automatic;
                Diagnostics.Event("camera.focus.applied", "release",
                    new { Success = released, Value = after.Value, Automatic = after.Automatic, Supported = after.Supported });
                if (released) _restoreAuto = false; // Keep a failed restoration pending for shutdown/retry.
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _closed = true;
                Release();
            }
        }
    }
}
