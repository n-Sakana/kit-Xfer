using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using MiniUI.Core;
using MiniUI.Core.Engine;
using MiniUI.Platform.Linux;

namespace Xfer;

/// <summary>Linux desktop host. Renders ui/index.html through GTK3/X11 and handles native calls and images
/// in this process. With --window-at, fullscreen waits for the requested window position.</summary>
internal static class MiniUIHost
{
    public static int Main(string[] args)
    {
        Diagnostics.Event("host.start", "native-linux", null);
        try { return Run(args); }
        catch (Exception e) { Diagnostics.Failure("host.run", "native-linux", e); Console.Error.WriteLine("Xfer: " + e.Message); return 1; }
        finally { Diagnostics.Event("host.exit", "native-linux", null); }
    }

    private static int Run(string[] args)
    {
        string? home = null, fontDir = null, stateDir = null, screenshot = null;
        bool mcp = false, fullscreen = false;
        double duration = 0;
        int[]? windowAt = null, windowSize = null;
        var appArgs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing value for " + args[i - 1]);
            switch (args[i])
            {
                case "--home": home = Path.GetFullPath(Next()); break;
                case "--font-dir": fontDir = Path.GetFullPath(Next()); break;
                case "--state-dir": stateDir = Path.GetFullPath(Next()); break;
                case "--screenshot": screenshot = Path.GetFullPath(Next()); break;
                case "--duration-ms": duration = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--mcp": mcp = true; break;
                case "--fullscreen": fullscreen = true; break;
                case "--window-at": windowAt = Pair(Next(), "--window-at", ','); break;
                case "--window": windowSize = Pair(Next(), "--window", 'x'); break;
                default: appArgs.Add(args[i]); break;
            }
        }
        home ??= DefaultHome();
        if (appArgs.Contains("--no-browser") || appArgs.Contains("--help") || appArgs.Contains("-h"))
            return Program.Run(home, appArgs.ToArray());

        string entry = Path.Combine(home, "ui", "index.html");
        if (!File.Exists(entry)) throw new FileNotFoundException("ui/index.html was not found: " + entry);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            throw new InvalidOperationException("DISPLAY is not set. Start Xfer from an X11 desktop session.");
        stateDir ??= Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "Xfer", "miniui");
        Directory.CreateDirectory(stateDir);

        var native = NativeApp.Instance;
        var arguments = new List<string> { "--home", home };
        arguments.AddRange(appArgs);
        var options = new EngineOptions
        {
            Root = Path.GetDirectoryName(entry)!, StateDir = stateDir, Width = 1180, Height = 800, Title = "Xfer", Mcp = mcp,
            HostCall = native.Call, HostImage = name => Image(native.Image(name)), Arguments = arguments.ToArray()
        };

        int stopping = 0;
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; Interlocked.Exchange(ref stopping, 1); });
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; Interlocked.Exchange(ref stopping, 1); });
        try
        {
            using var fonts = new LinuxFonts(fontDir);
            using var window = new LinuxWindow(windowSize?[0] ?? options.Width, windowSize?[1] ?? options.Height, options.Title, fonts);
            // --window-at is in X11 pixels (as xrandr and wmctrl give them); GTK places windows in its own units, the
            // X11 pixels divided by the window scale. Wait for placement before choosing the fullscreen monitor;
            // a timeout leaves the window at its current size. Keep it visible during placement because hiding
            // it can leave it minimized under Mutter.
            int[]? target = null;
            if (windowAt != null)
            {
                double scale = Math.Max(1, window.Scale);
                target = new[] { (int)Math.Round(windowAt[0] / scale), (int)Math.Round(windowAt[1] / scale) };
                window.MoveTo(target[0], target[1]);
            }
            if (windowSize != null) window.ResizeTo(windowSize[0], windowSize[1]);
            bool pendingFullscreen = fullscreen && target != null;
            bool presentationFullscreen = false;
            if (fullscreen && target == null) window.SetFullscreen(true);
            using var engine = new Engine(window, options);
            engine.ConsoleSink = Console.Error.WriteLine;
            engine.LoadControl(entry);
            var clock = Stopwatch.StartNew();
            void SyncIme()
            {
                var el = (engine.KeyboardDoc ?? engine.Control).Doc.FocusedElement;
                bool editable = el != null && el.IsTextEditable && !el.HasAttribute("readonly") && !el.HasAttribute("disabled");
                window.SyncTextInput(editable ? el : null, editable ? el!.EditableText : "", editable ? el!.SelectionEnd : 0, editable && el!.InputType == "password");
            }
            bool Pump()
            {
                window.Pump(input => { engine.HandleInput(input); SyncIme(); });
                if (pendingFullscreen)
                {
                    var bounds = window.GetBounds();
                    if (Math.Abs(bounds.X - target![0]) <= 64 && Math.Abs(bounds.Y - target[1]) <= 64)
                    {
                        pendingFullscreen = false;
                        if (!FullscreenAt(target[0], target[1])) window.SetFullscreen(true);
                    }
                    else if (clock.ElapsedMilliseconds > 5000)
                    {
                        pendingFullscreen = false;
                        Console.Error.WriteLine("--window-at: the window stayed at " + bounds.X + "," + bounds.Y + " (asked " + target[0] + "," + target[1] +
                            " in window units); not going fullscreen.");
                    }
                }
                if (Volatile.Read(ref stopping) != 0 || (duration > 0 && clock.Elapsed.TotalMilliseconds >= duration)) engine.Closed = true;
                if (!pendingFullscreen && native.PresentationFullscreen != presentationFullscreen)
                {
                    if (native.PresentationFullscreen)
                    {
                        var bounds = window.GetBounds();
                        if (target == null || (Math.Abs(bounds.X - target[0]) <= 64 && Math.Abs(bounds.Y - target[1]) <= 64))
                        {
                            presentationFullscreen = true;
                            if (!FullscreenAt((int)Math.Round(bounds.X), (int)Math.Round(bounds.Y))) window.SetFullscreen(true);
                        }
                    }
                    else
                    {
                        presentationFullscreen = false;
                        if (!fullscreen) window.SetFullscreen(false);
                    }
                }
                if (!engine.Closed && !window.Closed) { engine.RunFrame(); SyncIme(); }
                return !engine.Closed && !window.Closed;
            }
            using var stdin = mcp ? Console.OpenStandardInput() : null;
            using var stdout = mcp ? Console.OpenStandardOutput() : null;
            var rpc = mcp ? new McpServer(engine, Pump, stdin!, stdout!) : null;
            rpc?.Start();
            SyncIme();
            while (Pump())
            {
                rpc?.Process();
                window.WaitForInput(Math.Min(engine.NextWakeMs(), mcp ? 8 : 1000));
            }
            if (screenshot != null)
            {
                engine.EnsureRendered();
                using var output = new FileStream(screenshot, FileMode.CreateNew);
                output.Write(engine.CapturePng(out _, out _));
            }
            window.RequestClose();
        }
        finally
        {
            native.Dispose();
        }
        return 0;
    }

    // Scaled XWayland can choose a different fullscreen monitor from the one containing the window.
    // Specify the monitor through gtk_window_fullscreen_on_monitor. Coordinates are in GTK units;
    // this host owns a single GTK toplevel.
    [DllImport("libgtk-3.so.0")] private static extern nint gtk_window_list_toplevels();
    [DllImport("libgtk-3.so.0")] private static extern void gtk_window_get_position(nint window, out int x, out int y);
    [DllImport("libgtk-3.so.0")] private static extern int gtk_widget_get_visible(nint widget);
    [DllImport("libgtk-3.so.0")] private static extern void gtk_window_fullscreen_on_monitor(nint window, nint screen, int monitor);
    [DllImport("libgtk-3.so.0")] private static extern void gtk_window_deiconify(nint window);
    [DllImport("libgtk-3.so.0")] private static extern void gtk_window_present(nint window);
    [DllImport("libgdk-3.so.0")] private static extern nint gdk_screen_get_default();
    [DllImport("libgdk-3.so.0")] private static extern int gdk_screen_get_monitor_at_point(nint screen, int x, int y);
    [DllImport("libgdk-3.so.0")] private static extern void gdk_screen_get_monitor_geometry(nint screen, int monitor, out GdkRectangle geometry);
    [StructLayout(LayoutKind.Sequential)] private struct GdkRectangle { public int X, Y, Width, Height; }
    [DllImport("libglib-2.0.so.0")] private static extern void g_list_free(nint list);

    private static bool FullscreenAt(int x, int y)
    {
        try
        {
            nint list = gtk_window_list_toplevels(), found = 0;
            for (nint node = list; node != 0; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                nint candidate = Marshal.ReadIntPtr(node);
                if (candidate == 0 || gtk_widget_get_visible(candidate) == 0) continue;
                gtk_window_get_position(candidate, out int wx, out int wy);
                if (Math.Abs(wx - x) <= 64 && Math.Abs(wy - y) <= 64) { found = candidate; break; }
            }
            if (list != 0) g_list_free(list);
            if (found == 0) return false;
            nint screen = gdk_screen_get_default();
            int monitor = gdk_screen_get_monitor_at_point(screen, x + 8, y + 8);
            gdk_screen_get_monitor_geometry(screen, monitor, out var geometry);
            gtk_window_fullscreen_on_monitor(found, screen, monitor);
            gtk_window_deiconify(found);
            gtk_window_present(found);
            Console.Error.WriteLine("--fullscreen: monitor " + monitor + " (" + geometry.X + "," + geometry.Y + " " + geometry.Width + "x" + geometry.Height +
                ") for the window at " + x + "," + y + " (window units)");
            return true;
        }
        catch (Exception error) when (error is DllNotFoundException || error is EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static int[] Pair(string value, string option, char separator)
    {
        var parts = value.Split(separator);
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int a)
            || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int b))
            throw new ArgumentException(option + " needs two numbers, e.g. " + (separator == ',' ? "0,0" : "1920x1080"));
        return new[] { a, b };
    }

    /// <summary>The Xfer folder: two levels above artifacts/miniui-linux when that is where this host runs.</summary>
    private static string DefaultHome()
    {
        string here = AppContext.BaseDirectory;
        string up = Path.GetFullPath(Path.Combine(here, "..", ".."));
        return File.Exists(Path.Combine(up, "ui", "index.html")) ? up : Path.GetFullPath(here);
    }

    private static ImageData? Image(IAppImage? image)
    {
        if (image == null) return null;
        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyTo(pixels);
        return new ImageData(image.Width, image.Height, pixels);
    }
}
