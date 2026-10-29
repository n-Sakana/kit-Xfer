using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Xfer
{
    internal sealed partial class NativeApp
    {
        private static List<CameraInfo> Cameras() { return MediaFoundationCamera.List(); }
        private static ICameraSource OpenCamera(string id, int width, int fps) { return new MediaFoundationCamera(id, width, fps); }

        private static nint _presentationWindow, _presentationStyle;
        private static WindowPlacement _presentationPlacement;

        // Called by the page on its UI thread. Only this thread's Xfer window is changed.
        static partial void SetPresentationFullscreen(bool fullscreen)
        {
            if (fullscreen == (_presentationWindow != 0)) return;
            if (fullscreen)
            {
                nint found = 0;
                unsafe { EnumThreadWindows(GetCurrentThreadId(), &FirstVisible, (nint)(&found)); }
                if (found == 0) return;
                var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
                if (GetMonitorInfoW(MonitorFromWindow(found, 2), ref monitor) == 0 || GetWindowPlacement(found, ref placement) == 0) return;
                _presentationWindow = found; _presentationPlacement = placement;
                _presentationStyle = GetWindowLongPtrW(found, -16);
                SetWindowLongPtrW(found, -16, _presentationStyle & ~(nint)0x00c40000); // caption and sizing frame
                SetWindowPos(found, 0, monitor.Monitor.Left, monitor.Monitor.Top,
                    monitor.Monitor.Right - monitor.Monitor.Left, monitor.Monitor.Bottom - monitor.Monitor.Top, 0x24);
            }
            else
            {
                SetWindowLongPtrW(_presentationWindow, -16, _presentationStyle);
                SetWindowPlacement(_presentationWindow, ref _presentationPlacement);
                SetWindowPos(_presentationWindow, 0, 0, 0, 0, 0, 0x37);
                _presentationWindow = 0;
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public int Flags; }
        [StructLayout(LayoutKind.Sequential)] private struct WindowPlacement { public int Length, Flags, Show; public Point Min, Max; public Rect Normal; }
        [DllImport("user32.dll", ExactSpelling = true)] private static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int GetMonitorInfoW(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int GetWindowPlacement(nint window, ref WindowPlacement placement);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int SetWindowPlacement(nint window, ref WindowPlacement placement);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern nint GetWindowLongPtrW(nint window, int index);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

        // Runs on the window's UI thread (miniui_call), so the window is this thread's visible top-level window.
        static partial void RaiseWindow()
        {
            unsafe
            {
                nint found = 0;
                EnumThreadWindows(GetCurrentThreadId(), &FirstVisible, (nint)(&found));
                if (found == 0) return;
                if (IsIconic(found) != 0) ShowWindow(found, 9); // SW_RESTORE
                SetForegroundWindow(found);
            }
        }

        [UnmanagedCallersOnly]
        private static unsafe int FirstVisible(nint window, nint found)
        {
            if (IsWindowVisible(window) == 0) return 1;
            *(nint*)found = window;
            return 0;
        }

        [DllImport("user32.dll", ExactSpelling = true)] private static extern unsafe int EnumThreadWindows(uint thread, delegate* unmanaged<nint, nint, int> callback, nint state);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int IsWindowVisible(nint window);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int IsIconic(nint window);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int ShowWindow(nint window, int command);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int SetForegroundWindow(nint window);
    }
}
