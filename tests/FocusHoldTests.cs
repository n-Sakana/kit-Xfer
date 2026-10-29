using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Xfer
{
    internal static class FocusHoldTests
    {
        private sealed class Device
        {
            public CameraControlState State = new CameraControlState { Supported = true, Automatic = true, Value = 44, Capabilities = 3 };
            public readonly List<int?> Writes = new List<int?>();
            public bool FailManual, IgnoreManual, FailAuto, ThrowManual;
            public CameraFocusHold Hold;
            public Device()
            {
                Hold = new CameraFocusHold(new object(), () => State, value =>
                {
                    Writes.Add(value);
                    if (!value.HasValue && FailAuto) return false;
                    if (value.HasValue && IgnoreManual) return true;
                    State.Automatic = !value.HasValue;
                    if (value.HasValue) State.Value = value.Value;
                    if (value.HasValue && ThrowManual) throw new InvalidOperationException("driver failure after AF disabled");
                    return !(value.HasValue && FailManual);
                });
            }
        }

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }

        public static int Main()
        {
            try
            {
                var d = new Device();
                d.Hold.Hold(); d.Hold.Hold();
                Check(!d.State.Automatic && d.State.Value == 44 && d.Writes.Count == 1, "holds measured value; repeated hold does not resample");
                d.Hold.Release(); d.Hold.Release();
                Check(d.State.Automatic && d.Writes.Count == 2, "AF restored; repeated release does not write");
                d.State.Value = 19; d.Hold.Hold();
                Check(!d.State.Automatic && d.State.Value == 19, "next hold samples the new focus");
                d.Hold.Stop(); d.Hold.Hold();
                Check(d.State.Automatic && d.Writes.Count == 4, "shutdown restores AF and prevents later hold");

                d = new Device(); d.State.Automatic = false;
                d.Hold.Hold(); d.Hold.Release(); d.Hold.Stop();
                Check(!d.State.Automatic && d.Writes.Count == 0, "pre-existing manual focus remains unchanged");
                d = new Device(); d.State.Supported = false;
                d.Hold.Hold(); d.Hold.Release(); d.Hold.Stop();
                Check(d.Writes.Count == 0, "unsupported focus is a no-op");
                d = new Device(); d.State.Capabilities = 1;
                d.Hold.Hold(); d.Hold.Stop();
                Check(d.Writes.Count == 0, "missing manual capability is a no-op");

                d = new Device { FailManual = true };
                d.Hold.Hold();
                Check(d.State.Automatic && d.Writes.Count == 2, "partial manual-set failure rolls AF back");
                d = new Device { IgnoreManual = true };
                d.Hold.Hold();
                Check(d.State.Automatic && d.Writes.Count == 2, "successful Set with wrong readback rolls AF back");
                d = new Device { ThrowManual = true };
                try { d.Hold.Hold(); } catch (InvalidOperationException) { }
                Check(d.State.Automatic && d.Writes.Count == 2, "exception after AF disabled rolls AF back");
                d = new Device(); d.Hold.Hold(); d.FailAuto = true;
                d.Hold.Release();
                Check(!d.State.Automatic, "failed AF restore is retained");
                d.FailAuto = false; d.Hold.Stop();
                Check(d.State.Automatic, "shutdown retries failed AF restore");

                d = new Device();
                var racing = d;
                Parallel.For(0, 300, i =>
                {
                    if (i == 150) racing.Hold.Stop();
                    else if (i % 2 == 0) racing.Hold.Hold();
                    else racing.Hold.Release();
                });
                d.Hold.Stop();
                int writes = d.Writes.Count;
                Parallel.For(0, 100, i => racing.Hold.Hold());
                Check(d.State.Automatic && d.Writes.Count == writes, "concurrent decoder requests cannot hold after shutdown");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
    }
}
