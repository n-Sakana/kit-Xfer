using System;
using System.Threading.Tasks;

namespace Xfer
{
    internal static class NativeShutdown
    {
        private static readonly object Gate = new object();
        private static Task<int> _finish;

        // Native AOT DLLs do not receive the launcher's managed ProcessExit event.
        // The launcher calls this after MiniUI.Run returns; normal transfer calls never wait here.
        public static int Finish(int timeoutMilliseconds)
        {
            Task<int> finish;
            lock (Gate) {
                if (_finish == null) _finish = Task.Run(delegate {
                    int code = 0;
                    Diagnostics.Event("native.shutdown.start", "launcher", null);
                    try { NativeApp.Instance.Dispose(); }
                    catch (Exception error) { Diagnostics.Failure("native.shutdown", "launcher", error); code = 2; }
                    Diagnostics.Event("native.shutdown.end", "launcher", new { Code = code });
                    return Diagnostics.FlushFinal() ? code : 3;
                });
                finish = _finish;
            }
            // A slow or unavailable log destination must not keep the window's process alive indefinitely.
            return finish.Wait(Math.Max(0, Math.Min(2000, timeoutMilliseconds))) ? finish.Result : 1;
        }
    }
}
