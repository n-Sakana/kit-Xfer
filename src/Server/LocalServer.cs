using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Xfer
{
    internal sealed class LocalServer : IDisposable
    {
        private const long MaximumRequestBytes = 64 * 1024;

        private readonly SemaphoreSlim _requestSlots = new SemaphoreSlim(16, 16);
        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
        private readonly HttpListener _listener = new HttpListener();
        private readonly AppState _state;
        private readonly int _port;
        private readonly string _initialMode;
        private readonly string _outputRoot;
        private readonly Action _requestShutdown;
        private readonly OpticalService _optical = new OpticalService();
        private readonly OpticalReceiveService _opticalReceiver;
        private readonly RemoteControlRegistry _remotes = new RemoteControlRegistry();
        private readonly TailnetDiscovery _tailnet = new TailnetDiscovery();
        private LoopbackRelay _relay;

        public LocalServer(
            int port,
            AppState state,
            string initialMode,
            string outputRoot,
            Action requestShutdown)
        {
            _port = port;
            _state = state;
            _initialMode = initialMode;
            _outputRoot = Path.GetFullPath(outputRoot);
            _opticalReceiver = new OpticalReceiveService(_outputRoot);
            _requestShutdown = requestShutdown;
        }

        internal RemoteControlRegistry Remotes
        {
            get { return _remotes; }
        }

        internal TailnetDiscovery Tailnet
        {
            get { return _tailnet; }
        }

        public Uri Start()
        {
            Diagnostics.Event("server.start", "http", new { Port = _port });
            _listener.IgnoreWriteExceptions = true;
            if (PlatformInfo.IsWindows)
            {
                // http.sys takes a request for http://127.0.0.1:port/ by the address it arrives on, whatever its
                // Host header, so the phone's requests from tailscale serve (tailnet Host) arrive here directly.
                _listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", _port));
                _listener.Prefixes.Add(string.Format("http://localhost:{0}/", _port));
                _listener.Start();
            }
            else
            {
                // The managed listener answers only Host headers that name a prefix: a loopback relay owns the port
                // and hands requests to the listener under its own name (LoopbackRelay).
                int inner = LoopbackRelay.FreePort();
                _listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", inner));
                _listener.Start();
                var relay = new LoopbackRelay(_port);
                try
                {
                    relay.Start(inner);
                }
                catch (SocketException exception)
                {
                    _listener.Stop();
                    throw new HttpListenerException(exception.ErrorCode, exception.Message);
                }
                _relay = relay;
            }
            return new Uri(string.Format("http://localhost:{0}/", _port));
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(delegate
            {
                try
                {
                    _listener.Close();
                }
                catch (ObjectDisposedException)
                {
                }
            }))
            {
                var relay = _relay == null ? null : _relay.RunAsync(cancellationToken);
                var pending = new List<Task>();
                while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (HttpListenerException)
                    {
                        if (cancellationToken.IsCancellationRequested || !_listener.IsListening)
                        {
                            break;
                        }

                        throw;
                    }
                    catch (ObjectDisposedException)
                    {
                        if (cancellationToken.IsCancellationRequested || !_listener.IsListening)
                        {
                            break;
                        }

                        throw;
                    }

                    try
                    {
                        await _requestSlots.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        context.Response.Abort();
                        break;
                    }
                    var accepted = context;
                    pending.RemoveAll(delegate(Task task) { return task.IsCompleted; });
                    pending.Add(Task.Run(async delegate
                    {
                        try { await HandleSafelyAsync(accepted); }
                        catch (Exception exception) { Diagnostics.Failure("http.transport", "", exception); }
                        finally { _requestSlots.Release(); }
                    }));
                }
                if (relay != null)
                {
                    pending.Add(relay);
                }
                await Task.WhenAny(Task.WhenAll(pending), Task.Delay(2000));
            }
        }

        public void Dispose()
        {
            Diagnostics.Event("server.stop", "http", null);
            if (_relay != null)
            {
                _relay.Dispose();
            }

            if (_listener.IsListening)
            {
                _listener.Stop();
            }

            _listener.Close();
            _optical.Dispose();
            _opticalReceiver.Dispose();
        }

        private async Task HandleSafelyAsync(HttpListenerContext context)
        {
            // Do not create a feedback loop by recording the log shipment itself.
            if (context.Request.Url != null && context.Request.Url.AbsolutePath == "/api/diagnostics") {
                await HandleObservedAsync(context); return;
            }
            var stamp = Diagnostics.Stamp();
            string route = DiagnosticRoute(context.Request.Url == null ? "" : context.Request.Url.AbsolutePath);
            string client = Diagnostics.Id(context.Request.Headers["X-Xfer-Trace"]);
            Diagnostics.Sample("http.begin", route, 0, new { Client = client });
            try { await HandleObservedAsync(context); }
            finally { Diagnostics.Sample("http.end", route, Diagnostics.Milliseconds(stamp),
                new { Client = client, Status = context.Response.StatusCode }); }
        }

        private static string DiagnosticRoute(string path)
        {
            switch (path) {
                case "/api/status": case "/api/folder": case "/api/pick-folder": case "/api/pick-files":
                case "/api/optical/start": case "/api/optical/stop": case "/api/optical/frames": case "/api/optical/frame":
                case "/api/optical/receive/start": case "/api/optical/receive/stop": case "/api/optical/receive/frames": case "/api/optical/receive/frame":
                case "/api/remotes/heartbeat": case "/api/remotes/command": case "/api/remotes/disconnect":
                case "/api/remote-entry": case "/api/remote-entry/qr": case "/api/remotes": case "/api/devices":
                case "/api/shutdown": return path;
                default: return "asset_or_unknown";
            }
        }

        private async Task HandleObservedAsync(HttpListenerContext context)
        {
            AddSecurityHeaders(context.Response);
            ErrorBody error = null;
            var errorStatus = HttpStatusCode.InternalServerError;

            try
            {
                var path = context.Request.Url == null ? "/" : context.Request.Url.AbsolutePath;
                var exclusive = path == "/api/pick-folder" || path == "/api/pick-files"
                    || path == "/api/optical/start";
                if (exclusive) await _operationGate.WaitAsync();
                try { await HandleAsync(context); }
                finally { if (exclusive) _operationGate.Release(); }
            }
            catch (DirectoryNotFoundException exception)
            {
                Diagnostics.Failure("http", DiagnosticRoute(context.Request.Url.AbsolutePath), exception);
                errorStatus = HttpStatusCode.BadRequest;
                error = new ErrorBody(exception.Message);
            }
            catch (FileNotFoundException exception)
            {
                Diagnostics.Failure("http", DiagnosticRoute(context.Request.Url.AbsolutePath), exception);
                errorStatus = HttpStatusCode.BadRequest;
                error = new ErrorBody(exception.Message);
            }
            catch (ArgumentException exception)
            {
                Diagnostics.Failure("http", DiagnosticRoute(context.Request.Url.AbsolutePath), exception);
                errorStatus = HttpStatusCode.BadRequest;
                error = new ErrorBody(exception.Message);
            }
            catch (InvalidDataException exception)
            {
                Diagnostics.Failure("http", DiagnosticRoute(context.Request.Url.AbsolutePath), exception);
                errorStatus = HttpStatusCode.BadRequest;
                error = new ErrorBody(exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                Diagnostics.Failure("http", DiagnosticRoute(context.Request.Url.AbsolutePath), exception);
                errorStatus = HttpStatusCode.Forbidden;
                error = new ErrorBody(string.Format(
                    "Could not open this folder: {0}",
                    exception.Message));
            }
            catch (IOException exception)
            {
                Diagnostics.Failure("http", DiagnosticRoute(context.Request.Url.AbsolutePath), exception);
                errorStatus = HttpStatusCode.BadRequest;
                error = new ErrorBody(string.Format(
                    "Could not read this folder: {0}",
                    exception.Message));
            }
            catch (Exception exception)
            {
                Diagnostics.Failure("http", DiagnosticRoute(context.Request.Url.AbsolutePath), exception);
                error = new ErrorBody("An Xfer operation failed.");
            }

            if (error != null)
            {
                try
                {
                    if (context.Response.OutputStream.CanWrite)
                    {
                        await WriteJsonAsync(context, errorStatus, error);
                    }
                }
                catch (ObjectDisposedException)
                {
                    // The client disconnected while Xfer was reporting the error.
                }
                catch (HttpListenerException)
                {
                    // The client disconnected while a native picker was open.
                }
                catch (InvalidOperationException)
                {
                    // The response had already started before the disconnect.
                }
            }

            try
            {
                context.Response.OutputStream.Close();
            }
            catch (ObjectDisposedException)
            {
                // The client disconnected after the response was written.
            }
            catch (HttpListenerException)
            {
                // The client disconnected after the response was written.
            }
            catch (InvalidOperationException)
            {
                // The response was already closed.
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var path = request.Url == null ? "/" : request.Url.AbsolutePath;
            var role = GetRequestRole(request, _relay != null);

            if (request.HttpMethod == "POST" && path == "/api/diagnostics")
            {
                // Same loopback/Tailscale listener and authentication boundary as the application.
                // Custom header + JSON prevent simple cross-origin posts; no CORS permission is granted.
                Uri origin;
                var host = _relay != null ? request.Headers[LoopbackRelay.OriginalHostHeader] : request.Headers["Host"];
                if (request.Headers["X-Xfer-Diagnostics"] != "1"
                    || !string.Equals((request.ContentType ?? "").Split(';')[0], "application/json", StringComparison.OrdinalIgnoreCase)
                    || !Uri.TryCreate(request.Headers["Origin"], UriKind.Absolute, out origin)
                    || (origin.Scheme != "http" && origin.Scheme != "https")
                    || !string.Equals(origin.Authority, host, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("Diagnostic origin rejected");
                if (!BrowserDiagnostics.TryEnter()) {
                    await WriteJsonAsync(context, (HttpStatusCode)429, new { Saved = 0 }); return;
                }
                try {
                    byte[] body = await ReadBodyAsync(request);
                    int saved = await Task.Run(delegate { return BrowserDiagnostics.Save(body); });
                    await WriteJsonAsync(context, HttpStatusCode.OK, new { Saved = saved });
                } finally { BrowserDiagnostics.Leave(); }
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/status")
            {
                await WriteJsonAsync(context, HttpStatusCode.OK, BuildStatus(role));
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/devices")
            {
                var devices = await _tailnet.ReadAsync();
                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new { Devices = devices });
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/remote-entry")
            {
                RequireLocal(role, "View phone connection details on this PC.");
                var device = await ReadCurrentTailnetDeviceAsync();
                if (device == null)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.ServiceUnavailable,
                        new ErrorBody("Could not obtain the Tailscale phone connection URL."));
                    return;
                }
                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new { Url = device.Url });
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/remote-entry/qr")
            {
                RequireLocal(role, "Display the phone pairing QR code on this PC.");
                var device = await ReadCurrentTailnetDeviceAsync();
                if (device == null)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.ServiceUnavailable,
                        new ErrorBody("Could not obtain the Tailscale phone connection URL."));
                    return;
                }
                await WriteBytesAsync(
                    context,
                    "image/svg+xml; charset=utf-8",
                    OpticalService.RenderTextQr(device.Url));
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/remotes")
            {
                RequireLocal(role, "View connected remotes on this PC.");
                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new { Remotes = _remotes.ReadActive() });
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/remotes/heartbeat")
            {
                RequireRemote(role, "Connect from the remote control page.");
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var id = ReadString(body, "id");
                object nameValue;
                var name = body.TryGetValue("name", out nameValue) ? nameValue as string : null;
                var heartbeat = _remotes.Heartbeat(id, name);
                await WriteJsonAsync(context, HttpStatusCode.OK, heartbeat);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/remotes/disconnect")
            {
                RequireRemote(role, "Disconnect from the remote control page.");
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                _remotes.Disconnect(ReadString(body, "id"));
                await WriteJsonAsync(context, HttpStatusCode.OK, new { Disconnected = true });
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/remotes/command")
            {
                RequireLocal(role, "Control remotes from this PC.");
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request, SelectionBodyLimit(_state.ReadFolder("optical"))));
                var action = ReadString(body, "action");
                List<string> files = null;
                string format = null;
                var frameBytes = 0;
                var framesPerSecond = 0;
                string errorCorrection = null;
                var displaySize = 0;
                if (action == "showQr")
                {
                    files = ReadStringList(body, "files");
                    format = ReadString(body, "format");
                    frameBytes = ReadInt(body, "frameBytes");
                    framesPerSecond = ReadInt(body, "framesPerSecond");
                    errorCorrection = body.ContainsKey("errorCorrection")
                        ? ReadString(body, "errorCorrection")
                        : "L";
                    displaySize = body.ContainsKey("displaySize")
                        ? ReadInt(body, "displaySize")
                        : 900;
                }
                var count = _remotes.SendCommand(
                    action,
                    files,
                    format,
                    frameBytes,
                    framesPerSecond,
                    errorCorrection,
                    displaySize);
                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new { Sent = count });
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/shutdown")
            {
                if (role == "remote")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Forbidden,
                        new ErrorBody("Xfer can only be stopped from this PC."));
                    return;
                }

                int expectedProcessId;
                var currentProcessId = GetCurrentProcessId();
                if (!int.TryParse(
                        request.Headers["X-Xfer-Process-Id"],
                        out expectedProcessId)
                    || expectedProcessId != currentProcessId)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Conflict,
                        new ErrorBody("The running Xfer process changed before it could be stopped."));
                    return;
                }

                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new { Stopping = true, ProcessId = currentProcessId });
                context.Response.OutputStream.Close();
                _requestShutdown();
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/activate")
            {
                RequireLocal(role, "Open context menu selections on this PC.");

                int expectedProcessId;
                var currentProcessId = GetCurrentProcessId();
                if (!int.TryParse(
                        request.Headers["X-Xfer-Process-Id"],
                        out expectedProcessId)
                    || expectedProcessId != currentProcessId)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Conflict,
                        new ErrorBody("The running Xfer process changed before it could be activated."));
                    return;
                }

                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                string mode = null;
                string selectedPath = null;
                if (body.ContainsKey("mode") || body.ContainsKey("path"))
                {
                    if (!body.ContainsKey("mode") || !body.ContainsKey("path"))
                    {
                        throw new ArgumentException(
                            "Specify both the context menu target and action.");
                    }
                    mode = ReadMode(ReadString(body, "mode"));
                    selectedPath = ReadString(body, "path");
                }

                var folder = mode == null
                    ? null
                    : _state.SelectPath(mode, selectedPath);
                if (!DesktopActivation.Request(mode))
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.ServiceUnavailable,
                        new ErrorBody("The Xfer desktop window is not available yet."));
                    return;
                }

                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new { Activated = true, Folder = folder });
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/folder")
            {
                var mode = ReadMode(request.QueryString["mode"]);
                if (role == "remote" && mode != "optical")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Forbidden,
                        new ErrorBody("Only optical transfer is available through the remote control."));
                    return;
                }
                await WriteJsonAsync(context, HttpStatusCode.OK, _state.ReadFolder(mode));
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/folder")
            {
                if (role == "remote")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Forbidden,
                        new ErrorBody("Select the folder on the PC."));
                    return;
                }

                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var mode = ReadMode(body);
                object pathValue;
                var selectedPath = body.TryGetValue("path", out pathValue) ? pathValue as string : null;
                if (string.IsNullOrWhiteSpace(selectedPath))
                {
                    throw new ArgumentException("No folder was specified.");
                }

                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    _state.SelectFolder(mode, selectedPath));
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/pick-folder")
            {
                if (role == "remote")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Forbidden,
                        new ErrorBody("Select the folder on the PC."));
                    return;
                }

                if (!NativePicker.IsAvailable)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.NotImplemented,
                        new ErrorBody("The system folder picker is unavailable in this environment."));
                    return;
                }

                var mode = ReadMode(request.QueryString["mode"]);
                var selectedPath = await NativePicker.PickFolderAsync(_state.FolderPath(mode));
                if (selectedPath == null)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.OK,
                        new { Cancelled = true, Folder = (FolderSnapshot)null });
                    return;
                }

                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new
                    {
                        Cancelled = false,
                        Folder = _state.SelectFolder(mode, selectedPath)
                    });
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/pick-files")
            {
                if (role == "remote")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Forbidden,
                        new ErrorBody("Select files on the PC."));
                    return;
                }

                if (!NativePicker.IsAvailable)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.NotImplemented,
                        new ErrorBody("The system file picker is unavailable in this environment."));
                    return;
                }

                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var mode = ReadMode(body);
                var selectedPaths = await NativePicker.PickFilesAsync(_state.FolderPath(mode), mode);
                if (selectedPaths == null || selectedPaths.Count == 0)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.OK,
                        new { Cancelled = true, Folder = (FolderSnapshot)null });
                    return;
                }

                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new
                    {
                        Cancelled = false,
                        Folder = _state.SelectFiles(mode, selectedPaths)
                    });
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/optical/start")
            {
                var snapshot = _state.ReadFolder("optical");
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request, SelectionBodyLimit(snapshot)));
                var format = ReadString(body, "format");
                var selectedNames = ReadStringList(body, "files");
                var frameBytes = ReadInt(body, "frameBytes");
                var framesPerSecond = ReadInt(body, "framesPerSecond");
                var errorCorrection = body.ContainsKey("errorCorrection")
                    ? ReadString(body, "errorCorrection")
                    : "L";
                var result = await Task.Run(delegate
                {
                    return StartOptical(
                        snapshot,
                        selectedNames,
                        format,
                        frameBytes,
                        framesPerSecond,
                        errorCorrection);
                });
                await WriteJsonAsync(context, HttpStatusCode.OK, result);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/optical/receive/start")
            {
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                _opticalReceiver.Reset(ReadString(body, "receiverId"));
                await WriteJsonAsync(context, HttpStatusCode.OK, new { Started = true });
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/optical/receive/frames")
            {
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var receiverId = ReadString(body, "receiverId");
                var encodedFrames = ReadStringList(body, "frames");
                if (encodedFrames.Count < 1 || encodedFrames.Count > 8)
                    throw new ArgumentException("Submit between 1 and 8 QR frames at a time.");
                var frames = new List<byte[]>();
                foreach (var encoded in encodedFrames)
                {
                    try { frames.Add(Convert.FromBase64String(encoded)); }
                    catch (System.FormatException exception)
                    { throw new ArgumentException("The scanned QR data is corrupt.", exception); }
                }
                var openWhenDone = ReadOptionalBool(body, "openWhenDone", true);
                var result = OpticalReceiveResult.NotRecognized();
                foreach (var frame in frames)
                {
                    var current = _opticalReceiver.AddFrame(receiverId, frame, openWhenDone);
                    if (current.Recognized) result = current;
                    if (result.Complete) break;
                }
                await WriteJsonAsync(context, HttpStatusCode.OK, result);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/optical/receive/frame")
            {
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var receiverId = ReadString(body, "receiverId");
                var encodedFrame = ReadString(body, "frame");
                byte[] frame;
                try
                {
                    frame = Convert.FromBase64String(encodedFrame);
                }
                catch (System.FormatException exception)
                {
                    throw new ArgumentException("The scanned QR data is corrupt.", exception);
                }
                var result = _opticalReceiver.AddFrame(
                    receiverId,
                    frame,
                    ReadOptionalBool(body, "openWhenDone", true));
                await WriteJsonAsync(context, HttpStatusCode.OK, result);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/optical/receive/stop")
            {
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                _opticalReceiver.Stop(ReadString(body, "receiverId"));
                await WriteJsonAsync(context, HttpStatusCode.OK, new { Stopped = true });
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/optical/frames")
            {
                ulong sequence;
                int count;
                if (!ulong.TryParse(request.QueryString["seq"],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out sequence)
                    || !int.TryParse(request.QueryString["count"], out count))
                    throw new ArgumentException("Invalid QR prefetch request.");
                byte[] packed;
                if (!_optical.TryRenderPackedFrames(request.QueryString["token"], sequence, count, out packed))
                {
                    await WriteJsonAsync(context, HttpStatusCode.NotFound,
                        new ErrorBody("The transfer display has stopped."));
                    return;
                }
                await WriteBytesAsync(context, "application/octet-stream", packed);
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/optical/frame")
            {
                var token = request.QueryString["token"];
                ulong sequence;
                byte[] frame = null;
                var raster = string.Equals(
                    request.QueryString["format"],
                    "raster",
                    StringComparison.OrdinalIgnoreCase);
                var validSequence = ulong.TryParse(
                        request.QueryString["seq"],
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out sequence);
                var rendered = validSequence && (raster
                    ? _optical.TryRenderRasterFrame(token, sequence, out frame)
                    : _optical.TryRenderFrame(token, sequence, out frame));
                if (!rendered)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.NotFound,
                        new ErrorBody("The transfer display has stopped."));
                    return;
                }
                await WriteBytesAsync(
                    context,
                    raster ? "application/octet-stream" : "image/svg+xml; charset=utf-8",
                    frame);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/optical/stop")
            {
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                _optical.Stop(ReadString(body, "token"));
                await WriteJsonAsync(context, HttpStatusCode.OK, new { Stopped = true });
                return;
            }

            Asset asset;
            if ((request.HttpMethod == "GET" || request.HttpMethod == "HEAD")
                && StaticAssets.TryGet(path, out asset))
            {
                await WriteAssetAsync(context, asset, request.HttpMethod == "HEAD");
                return;
            }

            await WriteJsonAsync(
                context,
                HttpStatusCode.NotFound,
                new ErrorBody("Not found"));
        }

        private OpticalStartResult StartOptical(
            FolderSnapshot source, List<string> selectedNames, string format,
            int frameBytes, int framesPerSecond, string errorCorrection)
        {
            if (format != "original") throw new ArgumentException("Invalid transfer format.", "format");
            return _optical.Start(source, selectedNames, frameBytes, framesPerSecond, errorCorrection);
        }

        private object BuildStatus(string role)
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return new
            {
                Application = "Xfer",
                Device = Environment.MachineName,
                ProcessId = GetCurrentProcessId(),
                Platform = PlatformInfo.Name,
                Role = role,
                Version = version == null ? "0.0.0" : version.ToString(3),
                InitialMode = _initialMode,
                RemoteCount = _remotes.ReadActive().Count,
                Capabilities = new
                {
                    NativePicker = NativePicker.IsAvailable,
                    Camera = "browser"
                }
            };
        }

        private async Task<TailnetDevice> ReadCurrentTailnetDeviceAsync()
        {
            var devices = await _tailnet.ReadAsync();
            foreach (var device in devices)
            {
                if (device.Current && !string.IsNullOrWhiteSpace(device.Url))
                {
                    return device;
                }
            }
            return null;
        }

        private static string ReadMode(Dictionary<string, object> body)
        {
            object value;
            var mode = body.TryGetValue("mode", out value) ? value as string : "optical";
            return ReadMode(mode);
        }

        private static string ReadMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
            {
                mode = "optical";
            }
            if (mode != "optical")
            {
                throw new ArgumentException("Invalid file selection mode.");
            }
            return mode;
        }

        private static List<string> ReadStringList(
            Dictionary<string, object> body,
            string propertyName)
        {
            object value;
            var raw = body.TryGetValue(propertyName, out value)
                ? value as List<object>
                : null;
            if (raw == null)
            {
                throw new ArgumentException("No file list was specified.");
            }

            var result = new List<string>();
            foreach (var item in raw)
            {
                var text = item as string;
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new ArgumentException("Invalid file name.");
                }
                result.Add(text);
            }
            return result;
        }

        private static string ReadString(
            Dictionary<string, object> body,
            string propertyName)
        {
            object value;
            var result = body.TryGetValue(propertyName, out value) ? value as string : null;
            if (string.IsNullOrWhiteSpace(result))
            {
                throw new ArgumentException("Missing required field: " + propertyName);
            }
            return result;
        }

        private static int ReadInt(
            Dictionary<string, object> body,
            string propertyName)
        {
            object value;
            if (!body.TryGetValue(propertyName, out value) || value == null)
            {
                throw new ArgumentException("Missing required field: " + propertyName);
            }

            try
            {
                return Convert.ToInt32(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception exception)
            {
                throw new ArgumentException(
                    "Invalid numeric value: " + propertyName,
                    exception);
            }
        }

        private static bool ReadOptionalBool(
            Dictionary<string, object> body,
            string propertyName,
            bool fallback)
        {
            object value;
            return body.TryGetValue(propertyName, out value) && value is bool
                ? (bool)value
                : fallback;
        }

        private static void RequireLocal(string role, string message)
        {
            if (role == "remote")
            {
                throw new UnauthorizedAccessException(message);
            }
        }

        private static void RequireRemote(string role, string message)
        {
            if (role != "remote")
            {
                throw new UnauthorizedAccessException(message);
            }
        }

        private static int GetCurrentProcessId()
        {
            using (var process = Process.GetCurrentProcess())
            {
                return process.Id;
            }
        }

        private static string GetRequestRole(HttpListenerRequest request, bool relayed)
        {
            // Behind the relay the listener sees its own name; the Host the request came with is in the relay's
            // header (which the relay always sets, so a phone cannot choose it).
            var host = relayed && request.Headers[LoopbackRelay.OriginalHostHeader] != null
                ? request.Headers[LoopbackRelay.OriginalHostHeader]
                : request.Headers["Host"];
            if (string.IsNullOrWhiteSpace(host))
            {
                return "remote";
            }

            host = host.Trim();
            if (host.Length > 0 && host[0] == '[')
            {
                var closingBracket = host.IndexOf(']');
                if (closingBracket > 0)
                {
                    host = host.Substring(1, closingBracket - 1);
                }
            }
            else
            {
                var colon = host.LastIndexOf(':');
                if (colon > 0 && host.IndexOf(':') == colon)
                {
                    host = host.Substring(0, colon);
                }
            }

            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
                ? "local"
                : "remote";
        }

        private static long SelectionBodyLimit(FolderSnapshot source)
        {
            // Every selectable path may appear, including worst-case JSON Unicode escaping. The
            // ordinary 64 KiB request guard must not impose another file-count limit on a selection.
            long bytes = MaximumRequestBytes;
            foreach (var file in source.Files) bytes = checked(bytes + 6L * file.Name.Length + 3);
            return bytes;
        }

        private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest request, long maximumBytes = MaximumRequestBytes)
        {
            if (request.ContentLength64 > maximumBytes)
            {
                throw new ArgumentException("Request body is too large.");
            }

            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[4096];
                while (true)
                {
                    var read = await request.InputStream.ReadAsync(chunk, 0, chunk.Length);
                    if (read == 0)
                    {
                        return buffer.ToArray();
                    }

                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > maximumBytes)
                    {
                        throw new ArgumentException("Request body is too large.");
                    }
                }
            }
        }

        private static async Task WriteAssetAsync(
            HttpListenerContext context,
            Asset asset,
            bool headOnly)
        {
            var response = context.Response;
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = asset.ContentType;
            response.ContentLength64 = asset.Bytes.LongLength;
            response.Headers[HttpResponseHeader.CacheControl] = asset.Cacheable
                ? "public, max-age=3600"
                : "no-cache";

            if (!headOnly)
            {
                await response.OutputStream.WriteAsync(asset.Bytes, 0, asset.Bytes.Length);
            }
        }

        private static async Task WriteJsonAsync(
            HttpListenerContext context,
            HttpStatusCode status,
            object body)
        {
            var bytes = JsonCodec.Serialize(body);
            var response = context.Response;
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = bytes.LongLength;
            response.Headers[HttpResponseHeader.CacheControl] = "no-store";
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        private static async Task WriteBytesAsync(
            HttpListenerContext context,
            string contentType,
            byte[] bytes)
        {
            var response = context.Response;
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.LongLength;
            response.Headers[HttpResponseHeader.CacheControl] = "no-store";
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        private static void AddSecurityHeaders(HttpListenerResponse response)
        {
            response.Headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; worker-src 'self'; " +
                "style-src 'self'; img-src 'self' data:; media-src 'self' blob:; " +
                "connect-src 'self'; frame-ancestors 'none'; " +
                "base-uri 'none'; form-action 'self'";
            response.Headers["Permissions-Policy"] =
                "camera=(self), microphone=(), geolocation=()";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-Frame-Options"] = "DENY";
        }

        private sealed class ErrorBody
        {
            public ErrorBody(string error)
            {
                Error = error;
            }

            public string Error { get; private set; }
        }
    }
}
