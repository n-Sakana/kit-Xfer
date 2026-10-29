using System;
using System.IO;

namespace Xfer
{
    internal sealed class AppOptions
    {
        public const int DefaultPort = 18422;

        private AppOptions(
            int port,
            bool noBrowser,
            bool showHelp,
            bool cli,
            string initialPath,
            string initialMode,
            AutoSendOptions autoSend,
            AutoReceiveOptions autoReceive)
        {
            Port = port;
            NoBrowser = noBrowser;
            ShowHelp = showHelp;
            Cli = cli;
            InitialPath = initialPath;
            InitialMode = initialMode;
            AutoSend = autoSend;
            AutoReceive = autoReceive;
        }

        public int Port { get; private set; }
        public bool NoBrowser { get; private set; }
        public bool ShowHelp { get; private set; }
        public bool Cli { get; private set; }
        public string InitialPath { get; private set; }
        public string InitialMode { get; private set; }
        /// <summary>Measurement start-up (--send): show the QR for --path at once, with these settings. Null in
        /// normal use, where the window starts on the file list.</summary>
        public AutoSendOptions AutoSend { get; private set; }
        /// <summary>Measurement start-up (--receive): open the receive view and start reading with the camera
        /// settings given here (the saved ones otherwise). Null in normal use.</summary>
        public AutoReceiveOptions AutoReceive { get; private set; }

        public static AppOptions Parse(string[] args)
        {
            var port = DefaultPort;
            var noBrowser = false;
            var showHelp = false;
            var cli = false;
            string initialPath = null;
            string initialMode = "optical";
            var send = false;
            var auto = new AutoSendOptions();
            var receive = false;
            var autoReceive = new AutoReceiveOptions();

            if (args == null)
            {
                args = new string[0];
            }

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (argument == "--help" || argument == "-h")
                {
                    showHelp = true;
                }
                else if (argument == "--no-browser")
                {
                    noBrowser = true;
                }
                else if (argument == "--cli")
                {
                    cli = true;
                }
                else if (argument == "--port")
                {
                    port = ParsePort(NextValue(args, ref index, argument));
                }
                else if (argument == "--path")
                {
                    initialPath = NextValue(args, ref index, argument);
                }
                else if (argument == "--mode")
                {
                    initialMode = ParseMode(NextValue(args, ref index, argument));
                }
                else if (argument.StartsWith("--port=", StringComparison.Ordinal))
                {
                    port = ParsePort(argument.Substring(7));
                }
                else if (argument.StartsWith("--path=", StringComparison.Ordinal))
                {
                    initialPath = argument.Substring(7);
                }
                else if (argument.StartsWith("--mode=", StringComparison.Ordinal))
                {
                    initialMode = ParseMode(argument.Substring(7));
                }
                else if (argument == "--send")
                {
                    send = true;
                }
                else if (argument == "--fps")
                {
                    auto.FramesPerSecond = ParseNumber(NextValue(args, ref index, argument), argument, 1, 60);
                }
                else if (argument == "--frame-bytes")
                {
                    auto.FrameBytes = ParseNumber(NextValue(args, ref index, argument), argument, 64, 2953);
                }
                else if (argument == "--ec")
                {
                    auto.ErrorCorrection = ParseCorrection(NextValue(args, ref index, argument));
                }
                else if (argument == "--qr-size")
                {
                    auto.QrSize = ParseNumber(NextValue(args, ref index, argument), argument, 100, 4000);
                }
                else if (argument == "--columns")
                {
                    auto.Columns = ParseNumber(NextValue(args, ref index, argument), argument, 1, 3);
                }
                else if (argument == "--rows")
                {
                    auto.Rows = ParseNumber(NextValue(args, ref index, argument), argument, 1, 2);
                }
                else if (argument == "--receive")
                {
                    receive = true;
                }
                else if (argument == "--camera")
                {
                    autoReceive.Camera = NextValue(args, ref index, argument);
                }
                else if (argument == "--camera-width")
                {
                    autoReceive.Width = ParseNumber(NextValue(args, ref index, argument), argument, 960, 1920);
                }
                else if (argument == "--camera-fps")
                {
                    autoReceive.FramesPerSecond = ParseNumber(NextValue(args, ref index, argument), argument, 30, 60);
                }
                else if (argument == "--workers")
                {
                    autoReceive.Workers = ParseNumber(NextValue(args, ref index, argument), argument, 1, 3);
                }
                else if (argument == "--layout")
                {
                    auto.Layout = ParseLayout(NextValue(args, ref index, argument));
                }
                else if (argument == "--cell")
                {
                    auto.Cell = ParseNumber(NextValue(args, ref index, argument), argument, 1, 32);
                }
                else if (argument == "--color") { auto.Color = true; }
                else if (argument == "--mono") { auto.Color = false; }
                else if (argument == "--coarse")
                {
                    auto.Coarse = ParseNumber(NextValue(args, ref index, argument), argument, 0, 4);
                }
                else if (argument == "--keystone-x")
                {
                    auto.KeystoneX = ParseNumber(NextValue(args, ref index, argument), argument, -90, 90);
                }
                else if (argument == "--keystone-y")
                {
                    auto.KeystoneY = ParseNumber(NextValue(args, ref index, argument), argument, -90, 90);
                }
                else if (argument.StartsWith("--fps=", StringComparison.Ordinal))
                {
                    auto.FramesPerSecond = ParseNumber(argument.Substring(6), "--fps", 1, 60);
                }
                else if (argument.StartsWith("--frame-bytes=", StringComparison.Ordinal))
                {
                    auto.FrameBytes = ParseNumber(argument.Substring(14), "--frame-bytes", 64, 2953);
                }
                else if (argument.StartsWith("--ec=", StringComparison.Ordinal))
                {
                    auto.ErrorCorrection = ParseCorrection(argument.Substring(5));
                }
                else if (argument.StartsWith("--qr-size=", StringComparison.Ordinal))
                {
                    auto.QrSize = ParseNumber(argument.Substring(10), "--qr-size", 100, 4000);
                }
                else if (argument.StartsWith("--columns=", StringComparison.Ordinal))
                {
                    auto.Columns = ParseNumber(argument.Substring(10), "--columns", 1, 3);
                }
                else if (argument.StartsWith("--rows=", StringComparison.Ordinal))
                {
                    auto.Rows = ParseNumber(argument.Substring(7), "--rows", 1, 2);
                }
                else
                {
                    throw new ArgumentException(string.Format("Unknown option: {0}", argument));
                }
            }
            if (send && string.IsNullOrWhiteSpace(initialPath))
            {
                throw new ArgumentException("--send requires --path.");
            }
            if (send && receive)
            {
                throw new ArgumentException("--send and --receive cannot be used together.");
            }

            if (cli)
            {
                throw new ArgumentException("Xfer does not support --cli.");
            }
            if (cli && string.IsNullOrWhiteSpace(initialPath))
            {
                throw new ArgumentException("--cli requires --path.");
            }
            return new AppOptions(
                port,
                noBrowser,
                showHelp,
                cli,
                initialPath,
                initialMode,
                send ? auto : null,
                receive ? autoReceive : null);
        }

        public static void WriteHelp(TextWriter writer)
        {
            writer.WriteLine("Xfer");
            writer.WriteLine();
            writer.WriteLine("Usage: Xfer [options]");
            writer.WriteLine();
            writer.WriteLine("  --port <number>             Local HTTP port (default: {0})", DefaultPort);
            writer.WriteLine("  --path <file-or-folder>     Input to show when Xfer starts");
            writer.WriteLine("  --mode optical             Page to show when Xfer starts");
            writer.WriteLine("  --no-browser                Run the HTTP server without the desktop window");
            writer.WriteLine("  --help                      Show this help");
            writer.WriteLine();
            writer.WriteLine("Measurement (desktop window only): show the QR for --path at once");
            writer.WriteLine("  --send                      Start showing the QR without the file list");
            writer.WriteLine("  --fps <n>                   10, 15, 20, 24, 30 or 60 (the saved setting otherwise)");
            writer.WriteLine("  --frame-bytes <n>           500, 1000, 1465, 1850, 2331 or 2953");
            writer.WriteLine("  --ec <L|M|Q|H>              Error correction");
            writer.WriteLine("  --qr-size <px>              Size of one QR on screen (300-1200)");
            writer.WriteLine("  --columns <n>               QRs side by side (1-3)");
            writer.WriteLine("  --rows <n>                  Rows of QRs (1-2)");
            writer.WriteLine("  --color / --mono            Tile chroma layer on / off");
            writer.WriteLine("  --layout <qr|fx2>           QR codes, or Tile (fx2): cells over the whole window");
            writer.WriteLine("  --cell <px>                 Tile cell size in device pixels (1-32)");
            writer.WriteLine("  --coarse <n>                Tile middle tier with n times larger cells (0 = none, 2-4)");
            writer.WriteLine("  --keystone-x <percent>      Draw the picture as a trapezoid: right edge shorter (-: left)");
            writer.WriteLine("  --keystone-y <percent>      Draw the picture as a trapezoid: top edge narrower (-: bottom)");
            writer.WriteLine();
            writer.WriteLine("Measurement (desktop window only): start reading with this PC's camera at once");
            writer.WriteLine("  --receive                   Open the receive view and start the camera");
            writer.WriteLine("  --camera <name>             The camera whose name contains this (the saved one otherwise)");
            writer.WriteLine("  --camera-width <px>         960, 1280 or 1920 (the saved setting otherwise)");
            writer.WriteLine("  --camera-fps <n>            30 or 60");
            writer.WriteLine("  --workers <n>               Reading threads (1-3)");
        }

        private static string NextValue(string[] args, ref int index, string option)
        {
            index++;
            if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException(string.Format("{0} requires a value.", option));
            }

            return args[index];
        }

        private static int ParsePort(string value)
        {
            int port;
            if (!int.TryParse(value, out port) || port < 1 || port > 65535)
            {
                throw new ArgumentException(string.Format("Invalid port: {0}", value));
            }

            return port;
        }

        private static string ParseMode(string value)
        {
            var mode = value.ToLowerInvariant();
            if (mode != "optical")
            {
                throw new ArgumentException(string.Format("Invalid mode: {0}", value));
            }

            return mode;
        }

        private static int ParseNumber(string value, string option, int minimum, int maximum)
        {
            int number;
            if (!int.TryParse(value, out number) || number < minimum || number > maximum)
            {
                throw new ArgumentException(string.Format("{0} must be a number from {1} to {2}.", option, minimum, maximum));
            }
            return number;
        }

        private static string ParseLayout(string value)
        {
            var layout = (value ?? "").ToLowerInvariant();
            if (layout != "qr" && layout != "fx2")
            {
                throw new ArgumentException("--layout must be qr or fx2.");
            }
            return layout;
        }

        private static string ParseCorrection(string value)
        {
            var ec = (value ?? "").ToUpperInvariant();
            if (ec != "L" && ec != "M" && ec != "Q" && ec != "H")
            {
                throw new ArgumentException("--ec must be L, M, Q or H.");
            }
            return ec;
        }
    }

    /// <summary>Settings for --receive. A null member means "use the setting saved in the window".</summary>
    internal sealed class AutoReceiveOptions
    {
        public string Camera { get; set; }
        public int? Width { get; set; }
        public int? FramesPerSecond { get; set; }
        public int? Workers { get; set; }
    }

    /// <summary>Settings for --send. A null member means "use the setting saved in the window".</summary>
    internal sealed class AutoSendOptions
    {
        public int? FramesPerSecond { get; set; }
        public int? FrameBytes { get; set; }
        public string ErrorCorrection { get; set; }
        public int? QrSize { get; set; }
        public int? Columns { get; set; }
        public int? Rows { get; set; }
        public string Layout { get; set; }
        public int? Cell { get; set; }
        public int? Coarse { get; set; }
        public bool? Color { get; set; }
        /// <summary>Percent the picture is narrowed into a trapezoid (measurement of perspective).</summary>
        public int? KeystoneX { get; set; }
        public int? KeystoneY { get; set; }
    }
}
