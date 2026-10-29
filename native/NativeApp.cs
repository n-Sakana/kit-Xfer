using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZXing;
using ZXing.QrCode.Internal;

namespace Xfer
{
    /// <summary>Xfer's native component behind the MiniUI page (uiHost.call and app: images). One JSON request,
    /// one JSON reply; work that takes time returns a job the page polls. Shared by the Windows DLL and the Linux host.</summary>
    internal sealed partial class NativeApp : IDisposable
    {
        public static readonly NativeApp Instance = new NativeApp();
        private const int FolderPage = 250;

        private readonly object _gate = new object();
        private bool _initialized;
        private string _home, _output;
        private int _port = AppOptions.DefaultPort;
        private AppState _state;
        private LocalServer _server;
        private CancellationTokenSource _serverStop;
        private string _serverError;
        private readonly OpticalService _optical = new OpticalService();
        private OpticalReceiveService _receiver;
        private QrStream _qr;
        private Fx2Stream _fx2;
        private CameraReceiver _camera;
        private FolderSnapshot _folder;
        private readonly HashSet<string> _selected = new HashSet<string>(StringComparer.Ordinal);
        private int _folderVersion, _activation;
        private volatile bool _shutdownRequested;
        private readonly Dictionary<int, Task<object>> _jobs = new Dictionary<int, Task<object>>();
        private int _nextJob;
        private ScaledQr _pairing;
        private int _raised;
        private string _tailnet = "";
        private AutoSendOptions _autoSend;
        private AutoReceiveOptions _autoReceive;
        public bool PresentationFullscreen { get; private set; }

        public string Call(string input)
        {
            var op = "unknown";
            var stamp = Diagnostics.Stamp();
            try
            {
                var request = JsonCodec.ParseObject(Encoding.UTF8.GetBytes(input));
                op = SafeOperation(Text(request, "op"));
                return Json(Dispatch(Text(request, "op"), request));
            }
            catch (Exception error) { Diagnostics.Failure("native.call", op, error); return Json(new { Error = Message(error) }); }
            finally { Diagnostics.Sample("native.call", op, Diagnostics.Milliseconds(stamp), null); }
        }

        private object Dispatch(string op, Dictionary<string, object> r)
        {
            if (op != "poll" && op != "job" && op != "receive.status" && op != "diagnostics")
                Diagnostics.Sample("native.operation", SafeOperation(op), 0, null);
            if (op == "diagnostics") {
                var qr = _qr; var fx2 = _fx2;
                Diagnostics.Sample("send.ui_tick", qr != null ? qr.TransferId : fx2 != null ? fx2.TransferId : "", 0,
                    new { Sequence = Int(r, "sequence"), Scale = Int(r, "scale"), Frames = Int(r, "frames") });
                return new { Ok = true };
            }
            if (op == "init") return Init(r);
            if (!_initialized) throw new InvalidOperationException("Xfer is not initialised.");
            switch (op)
            {
                case "poll": return Poll();
                case "job": return Job(Int(r, "id"));
                case "folder": return Folder(Int(r, "offset", 0));
                case "folder.reload": lock (_gate) LoadFolder(true); return Folder(0);
                case "select": return Select(Text(r, "name"), Bool(r, "on"));
                case "selectAll": return SelectAll(Bool(r, "on"));
                case "pickFiles": return StartJob("pickFiles", () => Pick(false));
                case "pickFolder": return StartJob("pickFolder", () => Pick(true));
                case "send.start":
                    if (OptionalText(r, "layout") == "fx2") return StartJob("send.start", () => StartFx2(Int(r, "fps"), Text(r, "ec"), Bool(r, "color")));
                    return StartJob("send.start", () => StartSending(Int(r, "frameBytes"), Int(r, "fps"), Text(r, "ec"), Int(r, "columns", 1), Int(r, "rows", 1)));
                case "send.fx2layout": return DescribeFx2Layout(Int(r, "width"), Int(r, "height"), Int(r, "cell"), Int(r, "coarse", 0));
                case "send.presentation":
                    PresentationFullscreen = Bool(r, "fullscreen");
                    SetPresentationFullscreen(PresentationFullscreen);
                    return new { Ok = true };
                case "send.stop": StopSending(); return new { Stopped = true };
                case "cameras": return new { Cameras = Cameras() };
                case "receive.start": return StartReceiving(OptionalText(r, "camera"), Int(r, "width"), Int(r, "fps"), Int(r, "workers"), Bool(r, "openWhenDone"));
                case "receive.status": { var camera = _camera; return camera == null ? (object)new { Running = false } : camera.Status(); }
                case "receive.stop": StopReceiving(); return new { Stopped = true };
                case "openFolder": return new { Opened = OutputLauncher.OpenFolder(Text(r, "path")) };
                case "remote.entry": return StartJob("remote.entry", RemoteEntry);
                case "remote.list": return new { Remotes = _server == null ? new List<RemoteControlInfo>() : _server.Remotes.ReadActive() };
                case "remote.command": return RemoteCommand(Text(r, "action"), Int(r, "frameBytes", 1000), Int(r, "fps", 30), OptionalText(r, "ec") ?? "L", Int(r, "displaySize", 900));
                default: throw new ArgumentException("Unknown request: " + op);
            }
        }

        private static string SafeOperation(string op) {
            switch (op) {
                case "init": case "folder": case "folder.reload": case "select": case "selectAll":
                case "pickFiles": case "pickFolder": case "send.start": case "send.stop": case "send.fx2layout": case "send.presentation": case "cameras":
                case "receive.start": case "receive.stop": case "openFolder": case "remote.entry":
                case "remote.list": case "remote.command": case "poll": case "job": case "receive.status": case "diagnostics": return op;
                default: return "unknown";
            }
        }

        // ---------------------------------------------------------------- start-up

        private object Init(Dictionary<string, object> r)
        {
            lock (_gate)
            {
                if (_initialized) return Describe();
                Diagnostics.Event("app.start", "native", null);
                var args = new List<string>();
                object raw;
                if (r.TryGetValue("args", out raw) && raw is List<object>) foreach (var item in (List<object>)raw) if (item is string) args.Add((string)item);
                var rest = new List<string>();
                for (int i = 0; i < args.Count; i++)
                {
                    if (args[i] == "--home" && i + 1 < args.Count) _home = Path.GetFullPath(args[++i]);
                    else if (args[i] == "--output" && i + 1 < args.Count) _output = Path.GetFullPath(args[++i]);
                    else rest.Add(args[i]);
                }
                var options = AppOptions.Parse(rest.ToArray());
                _home = _home ?? Path.GetFullPath(AppContext.BaseDirectory);
                _output = _output ?? Path.Combine(_home, "output");
                _port = options.Port;
                _autoSend = options.AutoSend;
                _autoReceive = options.AutoReceive;
                _receiver = new OpticalReceiveService(_output);
                string pathError = null;
                try { _state = new AppState(options.InitialPath); }
                catch (Exception error) { Diagnostics.Failure("app.path", "native", error); pathError = Message(error); _state = new AppState(null); }
                DesktopActivation.Register(mode => { Interlocked.Increment(ref _activation); lock (_gate) LoadFolder(true); });
                StartServer();
                LoadFolder(true);
                _initialized = true;
                var result = Describe();
                if (pathError != null) return new { result.Device, result.Platform, result.Version, result.OutputRoot, result.Port, result.Server, result.ServerError, result.AutoSend, result.AutoReceive, PathError = pathError };
                return result;
            }
        }

        private Description Describe()
        {
            var version = typeof(NativeApp).Assembly.GetName().Version;
            return new Description
            {
                Device = Environment.MachineName,
                Platform = PlatformInfo.Name,
                Version = version == null ? "0.0.0" : version.ToString(3),
                OutputRoot = _output,
                Port = _port,
                Server = _server != null,
                ServerError = _serverError,
                AutoSend = _autoSend == null ? null : new { Fps = _autoSend.FramesPerSecond, FrameBytes = _autoSend.FrameBytes,
                    Ec = _autoSend.ErrorCorrection, QrSize = _autoSend.QrSize, Columns = _autoSend.Columns, Rows = _autoSend.Rows,
                    Layout = _autoSend.Layout, Cell = _autoSend.Cell, Coarse = _autoSend.Coarse, Color = _autoSend.Color,
                    KeystoneX = _autoSend.KeystoneX, KeystoneY = _autoSend.KeystoneY },
                AutoReceive = _autoReceive == null ? null : new { Camera = _autoReceive.Camera, Width = _autoReceive.Width, Fps = _autoReceive.FramesPerSecond, Workers = _autoReceive.Workers }
            };
        }

        private sealed class Description
        {
            public string Device { get; set; }
            public string Platform { get; set; }
            public string Version { get; set; }
            public string OutputRoot { get; set; }
            public int Port { get; set; }
            public bool Server { get; set; }
            public string ServerError { get; set; }
            /// <summary>The --send settings, or null when the window starts normally.</summary>
            public object AutoSend { get; set; }
            /// <summary>The --receive settings, or null when the window starts normally.</summary>
            public object AutoReceive { get; set; }
        }

        /// <summary>The local server is what the phone talks to. A Xfer already running on the port is replaced,
        /// as the launcher did; anything else on the port leaves this window working without the phone.</summary>
        private void StartServer()
        {
            try
            {
                StaticAssets.Initialize(_home, null);
                int running; string discovery;
                if (Program.TryFindRunningXfer(_port, _home, out running, out discovery))
                {
                    string stopError;
                    if (discovery != null || !Program.TryStopRunningXfer(_port, running, out stopError))
                        throw new InvalidOperationException("前に起動した Xfer を止められませんでした。");
                }
                _serverStop = new CancellationTokenSource();
                var server = new LocalServer(_port, _state, "optical", _output, delegate { _shutdownRequested = true; });
                server.Start();
                _server = server;
                var stop = _serverStop;
                Task.Run(async delegate
                {
                    try { await server.RunAsync(stop.Token); }
                    catch (Exception error) { Diagnostics.Failure("server.run", "native", error); _serverError = Message(error); }
                });
                int port = _port;
                // The phone opens https://<device>.<tailnet>.ts.net:10000/, which tailscale serve forwards here
                // (only for the standard port, as Program.Run does).
                if (port == AppOptions.DefaultPort)
                    Task.Run(delegate
                    {
                        string url, error;
                        bool ok = TailnetDiscovery.TryConfigureServe(port, out url, out error);
                        Diagnostics.Event("remote.serve", "native", new { Ready = ok, HasError = !string.IsNullOrEmpty(error) });
                        _tailnet = ok ? (error ?? "") : (error ?? "Tailscale を使えません。");
                    });
            }
            catch (Exception error)
            {
                Diagnostics.Failure("server.start", "native", error);
                _serverError = Message(error);
                _server = null;
            }
        }

        private object Poll()
        {
            // Opened again from Explorer: bring the window forward, as the WPF window did.
            int activation = _activation;
            if (activation != _raised) { _raised = activation; RaiseWindow(); }
            return new
            {
                Activation = _activation,
                Shutdown = _shutdownRequested,
                Remotes = _server == null ? 0 : _server.Remotes.ReadActive().Count,
                FolderVersion = _folderVersion,
                Tailnet = _tailnet
            };
        }

        // ---------------------------------------------------------------- jobs

        private object StartJob(string operation, Func<object> work)
        {
            lock (_gate)
            {
                if (_jobs.Count >= 32) throw new InvalidOperationException("処理中の操作が多すぎます。");
                int id = ++_nextJob;
                Diagnostics.Event("native.job.start", id.ToString(), new { Operation = operation });
                _jobs[id] = Task.Run(() => {
                    var stamp = Diagnostics.Stamp();
                    try { var result = work(); Diagnostics.Event("native.job.ok", id.ToString(), new { Operation = operation, Ms = Diagnostics.Milliseconds(stamp) }); return result; }
                    catch (Exception error) { Diagnostics.Failure("native.job", id.ToString(), error); throw; }
                });
                return new { Job = id };
            }
        }

        private object Job(int id)
        {
            Task<object> job;
            lock (_gate)
            {
                if (!_jobs.TryGetValue(id, out job)) throw new ArgumentException("Unknown job " + id);
                if (!job.IsCompleted) return new { Pending = true };
                _jobs.Remove(id);
            }
            if (job.IsFaulted) return new { Done = true, Error = Message(job.Exception.GetBaseException()) };
            return new { Done = true, Result = job.Result };
        }

        // ---------------------------------------------------------------- selection

        private void LoadFolder(bool selectAll)
        {
            _folder = _state.ReadFolder("optical");
            _selected.Clear();
            if (selectAll) foreach (var file in _folder.Files) _selected.Add(file.Name);
            _folderVersion++;
        }

        private object Folder(int offset)
        {
            lock (_gate)
            {
                var files = _folder.Files;
                long total = 0, selectedBytes = 0; int selected = 0;
                foreach (var file in files) { total += file.Size; if (_selected.Contains(file.Name)) { selected++; selectedBytes += file.Size; } }
                var page = new List<object>();
                for (int i = Math.Max(0, offset); i < files.Count && page.Count < FolderPage; i++)
                {
                    var f = files[i];
                    page.Add(new { f.Name, f.Size, Modified = f.ModifiedUtc, f.Kind, Selected = _selected.Contains(f.Name) });
                }
                return new
                {
                    Version = _folderVersion,
                    Path = _folder.Path,
                    Kind = _folder.SourceKind,
                    Count = files.Count,
                    TotalBytes = total,
                    SelectedCount = selected,
                    SelectedBytes = selectedBytes,
                    Offset = Math.Max(0, offset),
                    Files = page
                };
            }
        }

        private object Select(string name, bool on)
        {
            lock (_gate)
            {
                bool known = false;
                foreach (var file in _folder.Files) if (file.Name == name) { known = true; break; }
                if (!known) throw new ArgumentException("そのファイルはありません。");
                if (on) _selected.Add(name); else _selected.Remove(name);
                return Totals();
            }
        }

        private object SelectAll(bool on)
        {
            lock (_gate)
            {
                _selected.Clear();
                if (on) foreach (var file in _folder.Files) _selected.Add(file.Name);
                return Totals();
            }
        }

        private object Totals()
        {
            long bytes = 0; int count = 0;
            foreach (var file in _folder.Files) if (_selected.Contains(file.Name)) { count++; bytes += file.Size; }
            return new { SelectedCount = count, SelectedBytes = bytes, Count = _folder.Files.Count };
        }

        private List<string> SelectedNames()
        {
            var names = new List<string>();
            foreach (var file in _folder.Files) if (_selected.Contains(file.Name)) names.Add(file.Name);
            return names;
        }

        private object Pick(bool folder)
        {
            string initial;
            lock (_gate) initial = _state.FolderPath("optical");
            if (folder)
            {
                string path = NativePicker.PickFolderAsync(initial).GetAwaiter().GetResult();
                if (path == null) { Diagnostics.Event("picker.cancelled", "folder", null); return new { Cancelled = true }; }
                _state.SelectFolder("optical", path);
            }
            else
            {
                var paths = NativePicker.PickFilesAsync(initial, "optical").GetAwaiter().GetResult();
                if (paths == null || paths.Count == 0) { Diagnostics.Event("picker.cancelled", "files", null); return new { Cancelled = true }; }
                _state.SelectFiles("optical", paths);
            }
            lock (_gate) LoadFolder(true);
            Diagnostics.Event("picker.selected", folder ? "folder" : "files", new { Files = _folder.Files.Count });
            return new { Cancelled = false };
        }

        // ---------------------------------------------------------------- sending

        /// <summary>Starts a display session. columns x rows QR codes are shown at once (each one an independent
        /// fountain frame, so the receiver needs nothing new in the protocol); the page advances the sequence by
        /// that many per display tick.</summary>
        private object StartSending(int frameBytes, int fps, string ec, int columns, int rows)
        {
            FolderSnapshot folder; List<string> names;
            lock (_gate) { folder = _folder; names = SelectedNames(); }
            if (names.Count == 0) throw new ArgumentException("送るファイルを選んでください。");
            if (columns < 1 || columns > TiledQr.MaximumColumns || rows < 1 || rows > TiledQr.MaximumRows) throw new ArgumentException("並べる枚数が正しくありません。");
            StopSending();
            var result = _optical.Start(folder, names, frameBytes, fps, ec);
            lock (_gate) _qr = new QrStream(_optical, result.Token);
            Diagnostics.Event("send.layout", result.TransferId, new { Columns = columns, Rows = rows, Gap = TiledQr.Gap });
            return new
            {
                result.TransferId, result.Token, result.Label, result.FileCount, result.OriginalBytes, result.FrameBytes, result.FramesPerSecond,
                result.ErrorCorrection, result.SourceBlocks, result.QrVersion, result.MinimumSeconds,
                Side = 17 + 4 * result.QrVersion + 8,
                Columns = columns, Rows = rows, Gap = TiledQr.Gap
            };
        }

        /// <summary>Starts an FX2 display session. The page asks for the layout of its window (send.fx2layout) and
        /// advances the sequence by that layout's patch count per display tick.</summary>
        private object StartFx2(int fps, string ec, bool color)
        {
            FolderSnapshot folder; List<string> names;
            lock (_gate) { folder = _folder; names = SelectedNames(); }
            if (names.Count == 0) throw new ArgumentException("送るファイルを選んでください。");
            StopSending();
            var result = _optical.StartFx2(folder, names, fps, ec, color);
            lock (_gate) _fx2 = new Fx2Stream(_optical, result.Token, ec, color);
            return new
            {
                result.TransferId, result.Token, result.Label, result.FileCount, result.OriginalBytes, result.FrameBytes, result.FramesPerSecond,
                result.ErrorCorrection, result.SourceBlocks, Layout = "fx2"
            };
        }

        private object DescribeFx2Layout(int width, int height, int cell, int coarse)
        {
            Fx2Stream fx2;
            lock (_gate) fx2 = _fx2;
            if (fx2 == null) throw new InvalidOperationException("Tile を表示していません。");
            var layout = fx2.Layout(width, height, cell, coarse);
            return new { layout.Width, layout.Height, layout.Patches, Coarse = layout.Coarse, Tiers = layout.Tiers.Length };
        }

        private void StopSending()
        {
            QrStream qr; Fx2Stream fx2;
            lock (_gate) { qr = _qr; _qr = null; fx2 = _fx2; _fx2 = null; }
            if (qr != null) { qr.Dispose(); _optical.Stop(qr.Token); }
            if (fx2 != null) { fx2.Dispose(); _optical.Stop(fx2.Token); }
        }

        // ---------------------------------------------------------------- receiving

        private object StartReceiving(string camera, int width, int fps, int workers, bool openWhenDone)
        {
            StopReceiving();
            if (width != 960 && width != 1280 && width != 1920) throw new ArgumentException("撮影幅が正しくありません。");
            if (fps != 30 && fps != 60) throw new ArgumentException("撮影 fps が正しくありません。");
            Diagnostics.Event("camera.request", "native", new { Camera = Diagnostics.Id(camera), Width = width, Fps = fps, Workers = workers });
            var receiver = new CameraReceiver(OpenCamera(camera, width, fps), _receiver, workers, openWhenDone);
            lock (_gate) _camera = receiver;
            receiver.Start();
            return new { Started = true };
        }

        private void StopReceiving()
        {
            CameraReceiver camera;
            lock (_gate) { camera = _camera; _camera = null; }
            if (camera != null) camera.Dispose();
        }

        // ---------------------------------------------------------------- phone

        /// <summary>Bring this window forward (Windows); nothing elsewhere.</summary>
        static partial void RaiseWindow();
        static partial void SetPresentationFullscreen(bool fullscreen);

        private object RemoteEntry()
        {
            var server = _server;
            if (server == null) throw new InvalidOperationException(_serverError ?? "Xfer のサーバーが動いていません。");
            string url = null;
            foreach (var device in server.Tailnet.ReadAsync().GetAwaiter().GetResult())
                if (device.Current && !string.IsNullOrWhiteSpace(device.Url)) { url = device.Url; break; }
            if (url == null) throw new InvalidOperationException("Tailscale につながっていません。");
            var hints = new Dictionary<EncodeHintType, object>();
            hints[EncodeHintType.CHARACTER_SET] = "ISO-8859-1";
            hints[EncodeHintType.DISABLE_ECI] = true;
            var qr = ZXing.QrCode.Internal.Encoder.encode(Encoding.GetEncoding(28591).GetString(Encoding.UTF8.GetBytes(url)), ErrorCorrectionLevel.M, hints);
            var matrix = qr.Matrix;
            var pairing = ScaledQr.FromMatrix(matrix.Width, matrix.Height, (x, y) => matrix[x, y] != 0, 4, 1);
            lock (_gate) _pairing = pairing;
            return new { Url = url, Side = pairing.Side };
        }

        private object RemoteCommand(string action, int frameBytes, int fps, string ec, int displaySize)
        {
            if (_server == null) throw new InvalidOperationException(_serverError ?? "Xfer のサーバーが動いていません。");
            List<string> names = null;
            if (action == "showQr")
            {
                lock (_gate) names = SelectedNames();
                if (names.Count == 0) throw new ArgumentException("送るファイルを選んでください。");
            }
            return new { Sent = _server.Remotes.SendCommand(action, names, "original", frameBytes, fps, ec, displaySize) };
        }

        // ---------------------------------------------------------------- app: images

        /// <summary>Pixels for &lt;img src="app:NAME"&gt;: qr?t=TOKEN&amp;s=SEQ&amp;m=SCALE[&amp;c=COLS&amp;r=ROWS],
        /// fx2?t=TOKEN&amp;s=SEQ&amp;w=WIDTH&amp;h=HEIGHT&amp;m=CELL[&amp;k=COARSE], camera?n=N, pairing?m=SCALE. m is the
        /// number of image pixels per QR module or FX2 cell; c and r lay out consecutive QR frames SEQ, SEQ+1, ... side
        /// by side; an FX2 picture's patches carry SEQ, SEQ+1, ... qr and fx2 take kx and ky (thousandths) to draw
        /// the picture as a trapezoid for measurements. Null when absent.</summary>
        public IAppImage Image(string name)
        {
            string path = name, query = "";
            int q = name.IndexOf('?');
            if (q >= 0) { path = name.Substring(0, q); query = name.Substring(q + 1); }
            switch (path)
            {
                case "qr":
                    {
                        QrStream qr;
                        lock (_gate) qr = _qr;
                        ulong seq;
                        if (qr == null || Query(query, "t") != qr.Token || !ulong.TryParse(Query(query, "s"), NumberStyles.None, CultureInfo.InvariantCulture, out seq)) return null;
                        return Keystone(qr.Tiles(seq, Scale(query), Count(query, "c", TiledQr.MaximumColumns), Count(query, "r", TiledQr.MaximumRows)), query);
                    }
                case "fx2":
                    {
                        Fx2Stream fx2;
                        lock (_gate) fx2 = _fx2;
                        ulong seq;
                        if (fx2 == null || Query(query, "t") != fx2.Token || !ulong.TryParse(Query(query, "s"), NumberStyles.None, CultureInfo.InvariantCulture, out seq)) return null;
                        int width = Count(query, "w", 16384), height = Count(query, "h", 16384), cell = Count(query, "m", Fx2Layout.MaximumCell), coarse = Count(query, "k", 4);
                        if ((long)width * height > 64L * 1024 * 1024 / 4) return null;
                        var layout = fx2.Layout(width, height, cell, coarse);
                        return Keystone(new Fx2Image(fx2.Picture(seq, layout)), query);
                    }
                case "camera": { var camera = _camera; return camera == null ? null : camera.Preview; }
                case "pairing": { ScaledQr pairing; lock (_gate) pairing = _pairing; return pairing == null ? null : pairing.WithScale(Scale(query)); }
                default: return null;
            }
        }

        private static int Scale(string query)
        {
            int scale;
            return int.TryParse(Query(query, "m"), NumberStyles.None, CultureInfo.InvariantCulture, out scale) ? Math.Max(1, Math.Min(64, scale)) : 1;
        }

        private static IAppImage Keystone(IAppImage image, string query)
        {
            int kx, ky;
            int.TryParse(Query(query, "kx"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out kx);
            int.TryParse(Query(query, "ky"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out ky);
            if (kx == 0 && ky == 0) return image;
            return new KeystoneImage(image, Math.Max(-900, Math.Min(900, kx)) / 1000.0, Math.Max(-900, Math.Min(900, ky)) / 1000.0);
        }

        private static int Count(string query, string key, int maximum)
        {
            int count;
            return int.TryParse(Query(query, key), NumberStyles.None, CultureInfo.InvariantCulture, out count) ? Math.Max(1, Math.Min(maximum, count)) : 1;
        }

        private static string Query(string query, string key)
        {
            foreach (var part in query.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq) == key) return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return null;
        }

        // ---------------------------------------------------------------- helpers

        private static string Json(object value) { return Encoding.UTF8.GetString(JsonCodec.Serialize(value)); }

        private static string Message(Exception error)
        {
            if (error is AggregateException && error.InnerException != null) error = error.InnerException;
            if (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            return string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message;
        }

        private static string Text(Dictionary<string, object> r, string key)
        {
            object v;
            if (!r.TryGetValue(key, out v) || !(v is string) || ((string)v).Length == 0) throw new ArgumentException("必要な指定がありません: " + key);
            return (string)v;
        }

        private static string OptionalText(Dictionary<string, object> r, string key)
        {
            object v;
            return r.TryGetValue(key, out v) && v is string && ((string)v).Length > 0 ? (string)v : null;
        }

        private static int Int(Dictionary<string, object> r, string key, int? fallback = null)
        {
            object v;
            if (r.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
                catch (Exception) { throw new ArgumentException("数値の指定が正しくありません: " + key); }
            }
            if (fallback.HasValue) return fallback.Value;
            throw new ArgumentException("必要な指定がありません: " + key);
        }

        private static bool Bool(Dictionary<string, object> r, string key)
        {
            object v;
            return r.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        public void Dispose()
        {
            Diagnostics.Event("app.stop", "native", null);
            StopReceiving();
            StopSending();
            _optical.Dispose();
            if (_receiver != null) _receiver.Dispose();
            if (_serverStop != null) _serverStop.Cancel();
            if (_server != null) _server.Dispose();
        }
    }
}
