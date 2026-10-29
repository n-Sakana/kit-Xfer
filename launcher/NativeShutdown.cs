using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Xfer.Launcher
{
    public static class NativeShutdown
    {
        // Use only the already loaded Xfer DLL. Never load a new native runtime at shutdown.
        public static int Finish(string componentPath, int timeoutMilliseconds)
        {
            IntPtr module = GetModuleHandleW(Path.GetFullPath(componentPath));
            if (module == IntPtr.Zero) return -1;
            IntPtr entry = GetProcAddress(module, "xfer_shutdown");
            if (entry == IntPtr.Zero) return -2;
            var finish = (FinishCallback)Marshal.GetDelegateForFunctionPointer(entry, typeof(FinishCallback));
            return finish(Math.Max(0, Math.Min(2000, timeoutMilliseconds)));
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FinishCallback(int timeoutMilliseconds);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
