using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using ZXing;
using ZXing.Common;

namespace Xfer.Lab;

internal static partial class CameraLab
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = false };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static volatile bool cancelled;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help") { Help(); return 0; }
            if (args[0] == "selftest") return SelfTest();
            if (args[0] == "list")
            {
                foreach (var camera in MediaFoundationCamera.List())
                    Console.WriteLine(Diagnostics.Id(camera.Id) + "  " + camera.Name);
                return 0;
            }
            if (args[0] != "capture" && args[0] != "formats" && args[0] != "focus-hold") throw new ArgumentException("Unknown command: " + args[0]);
            var options = args[0] == "capture" ? Options.Parse(args) : null;
            using var mutex = new Mutex(false, @"Local\Xfer.CameraLab.Capture");
            bool owned;
            try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new InvalidOperationException("Another CameraLab is using the camera.");
            try { return args[0] == "focus-hold" ? FocusHoldProbe(args) : options == null ? Formats(args) : Capture(options); }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 2; }
    }

    private static void Help()
    {
        Console.WriteLine("""
CameraLab list | selftest
CameraLab formats [CAMERA] [--out FILE.json]  (opens camera: coordinate first!)
CameraLab focus-hold OUT [CAMERA]  (no window; tests product hold/release and shutdown restoration)
CameraLab capture OUT WIDTH FPS SECONDS [CAMERA] [options]
  OUT must be new or empty. Default camera: unique name containing C920.
  --height N                 exact height (width/fps are always exact)
  --focus keep|auto|N        default keep; N is a manual device value
  --exposure keep|auto|N     N is log2 seconds, e.g. -7 = 1/128 s
  --gain keep|auto|N         auto may be unsupported by the device
  --warmup S                record warmup separately (default 2 seconds)
  --modules auto|N          QR side excluding quiet zone; default auto
  --queue N                 max queued gray frames (default 8)
  --save-all N              save first N frames as PGM (default 0)
  --max-misses N            maximum saved miss frames (default 5)
  --save-every N            save every Nth analyzed frame (default 0/off)
  --probe                   echo control readbacks (always saved to JSONL)
  --no-restore              leave requested hardware settings after capture
  --roi X,Y,W,H             fixed QR region for row differences; otherwise track decoded QR
  --row-diffs               save row-differences.jsonl profiles as well as CSV boundary columns
  --af-lock-after S         start focus=auto, then lock on first decoded QR at/after S
  --af-lock-settle S        exclude this interval after lock from AF comparison (default 0.5)
SECONDS is the measurement window after warmup, starting from first frame.
Ctrl+C stops and writes partial results. Exit 0=measured, 2=error,
3=requested settings rejected/drifted or restore failed, 130=interrupted.
""");
    }

    private sealed class Options
    {
        public string Output, Camera, Focus = "keep", Exposure = "keep", Gain = "keep";
        public int Width, Height, Modules, Queue = 8, SaveAll, MaxMisses = 5, SaveEvery;
        public double Fps, Seconds, Warmup = 2;
        public bool Probe, Restore = true, RowDiffs;
        public Roi Region;
        public double? AfLockAfter;
        public double AfLockSettle = .5;
        public static Options Parse(string[] a)
        {
            if (a.Length < 5) throw new ArgumentException("Expected capture OUT WIDTH FPS SECONDS. See --help.");
            var o = new Options { Output = Path.GetFullPath(a[1]), Width = Int(a[2], 1, 4096),
                Fps = Number(a[3], 1, 240), Seconds = Number(a[4], 0.1, 86400) };
            for (int i = 5; i < a.Length; i++)
            {
                string Next() { if (++i >= a.Length) throw new ArgumentException("Missing option value."); return a[i]; }
                switch (a[i])
                {
                    case "--height": o.Height = Int(Next(), 1, 4096); break;
                    case "--focus": o.Focus = Control(Next()); break;
                    case "--exposure": o.Exposure = Control(Next()); break;
                    case "--gain": o.Gain = Control(Next()); break;
                    case "--warmup": o.Warmup = Number(Next(), 0, 3600); break;
                    case "--queue": o.Queue = Int(Next(), 1, 128); break;
                    case "--save-all": o.SaveAll = Int(Next(), 0, 100000); break;
                    case "--max-misses": o.MaxMisses = Int(Next(), 0, 100000); break;
                    case "--save-every": o.SaveEvery = Int(Next(), 0, 100000); break;
                    case "--modules":
                        string m = Next();
                        o.Modules = m == "auto" ? 0 : Int(m, 21, 177);
                        if (o.Modules != 0 && (o.Modules - 17) % 4 != 0) throw new ArgumentException("QR modules must be 21,25,...,177.");
                        break;
                    case "--probe": o.Probe = true; break;
                    case "--no-restore": o.Restore = false; break;
                    case "--roi": o.Region = Roi.Parse(Next()); break;
                    case "--row-diffs": o.RowDiffs = true; break;
                    case "--af-lock-after": o.AfLockAfter = Number(Next(), 0, 86400); break;
                    case "--af-lock-settle": o.AfLockSettle = Number(Next(), 0, 60); break;
                    default:
                        if (a[i].StartsWith("-") || o.Camera != null) throw new ArgumentException("Unknown argument: " + a[i]);
                        o.Camera = a[i]; break;
                }
            }
            if (o.AfLockAfter.HasValue)
            {
                if (o.Focus != "keep" && o.Focus != "auto") throw new ArgumentException("AF lock requires focus=auto.");
                if (o.AfLockAfter >= o.Warmup + o.Seconds) throw new ArgumentException("AF lock delay must be within capture duration.");
                o.Focus = "auto";
            }
            if (o.Region != null && (o.Region.X + o.Region.Width > o.Width || (o.Height > 0 && o.Region.Y + o.Region.Height > o.Height)))
                throw new ArgumentException("ROI exceeds requested image dimensions.");
            return o;
        }
        private static int Int(string s, int min, int max)
        {
            if (!int.TryParse(s, NumberStyles.Integer, Inv, out int v) || v < min || v > max)
                throw new ArgumentException("Integer out of range: " + s);
            return v;
        }
        private static double Number(string s, double min, double max)
        {
            if (!double.TryParse(s, NumberStyles.Float, Inv, out double v) || !double.IsFinite(v) || v < min || v > max)
                throw new ArgumentException("Number out of range: " + s);
            return v;
        }
        private static string Control(string s)
        {
            if (s != "auto" && s != "keep") Int(s, int.MinValue, int.MaxValue);
            return s;
        }
    }

    private sealed class Frame
    {
        public byte[] Gray;
        public int Width, Height, Index;
        public double At, SinceFirst, Interval;
        public string Phase, FocusPhase;
    }

    private sealed class Measurement
    {
        public bool Qr, Valid;
        public string Hash, SessionKey, Error, GeometryError;
        public uint Sequence;
        public int Session, Blocks, Modules;
        public double DecodeMs, GeometryMs;
        public double? ModuleX, ModuleY, CenterX, CenterY;
        public Roi Region;
    }

    private static Measurement Analyze(Frame f, BarcodeReaderGeneric reader, int modules)
    {
        var m = new Measurement();
        var luminance = new RGBLuminanceSource(f.Gray, f.Width, f.Height, RGBLuminanceSource.BitmapFormat.Gray8);
        var watch = Stopwatch.StartNew();
        Result result = null;
        try { result = reader.Decode(luminance); }
        catch (Exception e) { m.Error = e.GetType().Name + ": " + e.Message; }
        m.DecodeMs = watch.Elapsed.TotalMilliseconds;
        m.Qr = result != null;
        byte[] bytes = Bytes(result);
        if (OpticalFrame.TryParse(bytes, out var packet))
        {
            m.Valid = true; m.Sequence = packet.Sequence; m.Session = packet.SessionId; m.Blocks = packet.BlockCount;
            m.SessionKey = $"{packet.SessionId}:{packet.PayloadFnv}:{packet.TotalLength}:{packet.BlockLength}:{packet.BlockCount}";
            m.Hash = Convert.ToHexString(SHA256.HashData(bytes));
        }
        if (result?.ResultPoints?.Length >= 3)
        {
            watch.Restart();
            try
            {
                m.Modules = modules != 0 ? modules :
                    new ZXing.QrCode.Internal.Detector(new BinaryBitmap(new HybridBinarizer(luminance)).BlackMatrix).detect(null).Bits.Width;
                var p = result.ResultPoints;
                m.ModuleX = Distance(p[1], p[2]) / (m.Modules - 7);
                m.ModuleY = Distance(p[1], p[0]) / (m.Modules - 7);
                m.CenterX = (p[0].X + p[2].X) / 2.0;
                m.CenterY = (p[0].Y + p[2].Y) / 2.0;
                double fourthX = p[0].X + p[2].X - p[1].X, fourthY = p[0].Y + p[2].Y - p[1].Y;
                double pad = 3.5 * Math.Max(m.ModuleX.Value, m.ModuleY.Value);
                int left = Math.Max(0, (int)Math.Floor(Math.Min(p.Take(3).Min(q => q.X), fourthX) - pad));
                int top = Math.Max(0, (int)Math.Floor(Math.Min(p.Take(3).Min(q => q.Y), fourthY) - pad));
                int right = Math.Min(f.Width, (int)Math.Ceiling(Math.Max(p.Take(3).Max(q => q.X), fourthX) + pad));
                int bottom = Math.Min(f.Height, (int)Math.Ceiling(Math.Max(p.Take(3).Max(q => q.Y), fourthY) + pad));
                m.Region = new Roi { X = left, Y = top, Width = right - left, Height = bottom - top };
            }
            catch (Exception e) { m.GeometryError = e.GetType().Name; }
            m.GeometryMs = watch.Elapsed.TotalMilliseconds;
        }
        return m;
    }

    private sealed class Stats
    {
        public int Analyzed, QrDecoded, XferDecoded, UniqueQr, DuplicateQr, UniqueSequences, RepeatedSequences, DecodeErrors;
        public double DecodeMsTotal, GeometryMsTotal;
        public double? FirstValidAtMs;
        private readonly HashSet<string> hashes = new(), sequences = new();
        private readonly List<double> moduleX = new(), moduleY = new();
        public bool Add(Measurement m, double at)
        {
            Analyzed++; DecodeMsTotal += m.DecodeMs; GeometryMsTotal += m.GeometryMs;
            if (m.Qr) QrDecoded++;
            if (m.Error != null) DecodeErrors++;
            if (m.Valid && m.ModuleX.HasValue) { moduleX.Add(m.ModuleX.Value); moduleY.Add(m.ModuleY.Value); }
            if (!m.Valid) return false;
            XferDecoded++; FirstValidAtMs ??= at;
            bool fresh = hashes.Add(m.Hash);
            if (fresh) UniqueQr++; else DuplicateQr++;
            if (sequences.Add(m.SessionKey + ":" + m.Sequence)) UniqueSequences++; else RepeatedSequences++;
            return fresh;
        }
        public object Report(double seconds) => new
        {
            Analyzed, QrDecoded, XferDecoded, UniqueQr, DuplicateQr, UniqueSequences, RepeatedSequences, DecodeErrors,
            DecodeRate = Analyzed == 0 ? (double?)null : (double)XferDecoded / Analyzed,
            QrDecodeRate = Analyzed == 0 ? (double?)null : (double)QrDecoded / Analyzed,
            UniqueQrPerSecond = seconds > 0 ? (double?) (UniqueQr / seconds) : null,
            MeanDecodeMs = Analyzed == 0 ? (double?)null : DecodeMsTotal / Analyzed,
            MeanGeometryMs = Analyzed == 0 ? (double?)null : GeometryMsTotal / Analyzed,
            FirstValidAtMs, ModuleXSamples = moduleX.Count, ModuleXMedian = Median(moduleX), ModuleYMedian = Median(moduleY)
        };
    }

    // Device calls run on the camera's COM-initialized capture thread, including restoration.
    private sealed class Controls
    {
        private readonly Func<int, bool, CameraControlState> readControl;
        private readonly Func<bool, int, int?, string, bool> setControl;
        private readonly Options options;
        private readonly StreamWriter log;
        private readonly Stopwatch clock;
        private readonly List<(string name, int property, bool cameraControl, string request)> controls;
        private readonly Dictionary<string, CameraControlState> original = new();
        public bool RequestedSettingsVerified = true, RestorationSucceeded = true;
        public bool AfLockAttempted, AfLockAccepted;
        public int? LockedFocusValue;
        public double? AfLockAtMs;
        public int? AfTriggerIndex;
        private string lockedFocusRequest;
        public Controls(MediaFoundationCamera c, Options o, StreamWriter l, Stopwatch time)
            : this(o, l, time, c.ReadControl, c.SetControl) { }
        public Controls(Options o, StreamWriter l, Stopwatch time, Func<int, bool, CameraControlState> read,
            Func<bool, int, int?, string, bool> set)
        {
            readControl = read; setControl = set; options = o; log = l; clock = time;
            controls = new() { ("exposure", 4, true, o.Exposure), ("focus", 6, true, o.Focus), ("gain", 9, false, o.Gain) };
        }
        public void Apply()
        {
            // Snapshot all controls before changing any: automatic exposure can change gain immediately.
            foreach (var c in controls)
            {
                var before = readControl(c.property, c.cameraControl);
                original[c.name] = before;
                Write(c.name, "before", c.request, before, null, null);
            }
            foreach (var c in controls)
            {
                var before = original[c.name];
                if (c.request == "keep") continue;
                bool automatic = c.request == "auto";
                int? value = automatic ? null : int.Parse(c.request, Inv);
                string reason = !before.Supported ? "unsupported control" :
                    (before.Capabilities & (automatic ? 1 : 2)) == 0 ? "unsupported mode" :
                    value.HasValue && (value < before.Minimum || value > before.Maximum ||
                        (before.Step > 0 && (value.Value - before.Minimum) % before.Step != 0)) ? "value outside range/step" : null;
                bool accepted = reason == null && setControl(c.cameraControl, c.property, value, c.name);
                var after = readControl(c.property, c.cameraControl);
                bool matches = Matches(after, c.request);
                if (!accepted || !matches) RequestedSettingsVerified = false;
                Write(c.name, "apply", c.request, after, accepted, reason ?? (!matches ? "readback mismatch" : null));
            }
        }
        public void Sample()
        {
            foreach (var c in controls)
            {
                var state = readControl(c.property, c.cameraControl);
                string request = c.name == "focus" && lockedFocusRequest != null ? lockedFocusRequest : c.request;
                bool matches = request == "keep" || Matches(state, request);
                if (!matches) RequestedSettingsVerified = false;
                Write(c.name, "sample", request, state, null, matches ? null : "readback mismatch");
                if (options.Probe) Console.WriteLine($"{clock.Elapsed.TotalMilliseconds:0} ms {c.name}: {state}");
            }
        }
        public void LockFocus(LockRequest trigger)
        {
            AfLockAttempted = true; AfTriggerIndex = trigger.Index;
            var before = readControl(6, true);
            bool accepted = before.Supported && before.Automatic && (before.Capabilities & 2) != 0 &&
                setControl(true, 6, before.Value, "focus");
            var after = readControl(6, true);
            AfLockAccepted = accepted && after.Supported && !after.Automatic && after.Value == before.Value;
            AfLockAtMs = clock.Elapsed.TotalMilliseconds;
            LockedFocusValue = before.Supported ? before.Value : null;
            if (accepted) lockedFocusRequest = before.Value.ToString(Inv);
            if (!AfLockAccepted) RequestedSettingsVerified = false;
            log.WriteLine(JsonSerializer.Serialize(new { AtMs = AfLockAtMs, Name = "focus", Stage = "af_lock",
                TriggerFrameIndex = trigger.Index, TriggerFrameAtMs = trigger.At, TriggerDecodedAtMs = trigger.DecodedAt,
                Before = before, State = after, SetAccepted = accepted, Verified = AfLockAccepted,
                Error = AfLockAccepted ? null : "AF value could not be locked exactly" }, Json));
            log.Flush();
        }
        public string FocusPhase(double at) => !options.AfLockAfter.HasValue ? "not_requested" :
            !AfLockAttempted ? "auto" : !AfLockAccepted ? "lock_failed" :
            at - AfLockAtMs.Value < options.AfLockSettle * 1000 ? "lock_settling" : "locked";
        public void Restore()
        {
            if (!options.Restore || controls.All(c => c.request == "keep")) return;
            foreach (var c in controls)
            {
                // Restore even keep controls: another requested automatic control may have changed them.
                if (!original.TryGetValue(c.name, out var state) || !state.Supported) continue;
                bool accepted = setControl(c.cameraControl, c.property, state.Automatic ? null : state.Value, c.name);
                var after = readControl(c.property, c.cameraControl);
                bool matches = after.Supported && after.Automatic == state.Automatic && (state.Automatic || state.Value == after.Value);
                if (!accepted || !matches) RestorationSucceeded = false;
                Write(c.name, "restore", state.Automatic ? "auto" : state.Value.ToString(Inv), after, accepted, matches ? null : "restore mismatch");
            }
        }
        private static bool Matches(CameraControlState s, string request) =>
            s.Supported && (request == "auto" ? s.Automatic : !s.Automatic && s.Value == int.Parse(request, Inv));
        private void Write(string name, string stage, string request, CameraControlState state, bool? accepted, string error)
        {
            log.WriteLine(JsonSerializer.Serialize(new { AtMs = clock.Elapsed.TotalMilliseconds, Name = name, Stage = stage,
                Request = request, State = state, SetAccepted = accepted, Error = error }, Json));
            log.Flush();
        }
    }

    private static int Capture(Options o)
    {
        if (Directory.Exists(o.Output) && Directory.EnumerateFileSystemEntries(o.Output).Any())
            throw new ArgumentException("Output directory must be empty: " + o.Output);
        var selected = SelectCamera(o.Camera);
        Directory.CreateDirectory(o.Output);
        Environment.SetEnvironmentVariable("Xfer_LOG_DIR", Path.Combine(o.Output, "trace"));
        File.WriteAllText(Path.Combine(o.Output, "run.json"), JsonSerializer.Serialize(new
        {
            Schema = 1, StartedUtc = DateTimeOffset.UtcNow, Options = o, Camera = selected.Name,
            CameraId = Diagnostics.Id(selected.Id), Decoder = "ZXing full frame, TryHarder=true, AutoRotate=false",
            ProductReceiverEquivalent = false, Build = typeof(CameraLab).Assembly.GetName().Version?.ToString(),
            AssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CameraLab).Assembly.Location))),
            ZxingSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(BarcodeReaderGeneric).Assembly.Location))),
            Runtime = Environment.Version.ToString(), OS = Environment.OSVersion.ToString()
        }, Json));
        using var frames = Writer(o.Output, "frames.csv");
        using var captures = Writer(o.Output, "captures.csv");
        using var controlsLog = Writer(o.Output, "controls.jsonl");
        using var rowDiffs = o.RowDiffs ? Writer(o.Output, "row-differences.jsonl") : null;
        frames.WriteLine("index,at_ms,since_first_ms,phase,interval_ms,width,height,decode_ms,geometry_ms,qr_ok,xfer_ok,session,sequence,blocks,payload_sha256,new_qr,duplicate_qr,modules,module_x_px,module_y_px,cx,cy,mean,p5,p95,decode_error,geometry_error,focus_phase,pair_previous_index,pair_consecutive,roi_x,roi_y,roi_width,roi_height,boundary_candidate_row,boundary_row,boundary_confidence,boundary_delta,row_diff_mean,boundary_reason");
        captures.WriteLine("index,at_ms,since_first_ms,phase,interval_ms,width,height,queued,queue_depth");
        var clock = Stopwatch.StartNew();
        using var camera = new MediaFoundationCamera(selected.Id, o.Width, o.Fps, null)
            { RequestedHeight = o.Height, RequireExactFormat = true };
        var controls = new Controls(camera, o, controlsLog, clock);
        camera.BeforeShutdown += controls.Restore;
        var queue = new Queue<Frame>();
        var gate = new object();
        var all = new Stats(); var measured = new Stats(); var warmup = new Stats();
        var autoFocus = new Stats(); var lockedFocus = new Stats();
        LockRequest lockRequest = null;
        bool done = false, initialized = false;
        int captured = 0, dropped = 0, measuredCaptured = 0, measuredDropped = 0, saved = 0, missSaved = 0;
        double firstAt = -1, lastAt = -1, nextProbe = 0, endedAt = 0;
        Exception decoderError = null;
        var decoder = new Thread(() =>
        {
            try
            {
                var reader = Reader();
                Frame previous = null;
                Roi region = o.Region;
                while (true)
                {
                    Frame f;
                    lock (gate)
                    {
                        while (queue.Count == 0 && !done) Monitor.Wait(gate);
                        if (queue.Count == 0) return;
                        f = queue.Dequeue();
                    }
                    var m = Analyze(f, reader, o.Modules);
                    bool fresh = all.Add(m, f.At);
                    if (f.Phase == "measurement") measured.Add(m, f.At);
                    else warmup.Add(m, f.At);
                    if (f.Phase == "measurement" && f.FocusPhase == "auto") autoFocus.Add(m, f.At);
                    if (f.Phase == "measurement" && f.FocusPhase == "locked") lockedFocus.Add(m, f.At);
                    if (o.AfLockAfter.HasValue && m.Valid && f.FocusPhase == "auto" && f.SinceFirst >= o.AfLockAfter.Value * 1000)
                        Interlocked.CompareExchange(ref lockRequest, new LockRequest { Index = f.Index, At = f.At,
                            DecodedAt = clock.Elapsed.TotalMilliseconds }, null);
                    if (region == null && m.Valid) region = m.Region;
                    var boundary = Difference(previous, f, region);
                    if (rowDiffs != null && boundary.Rows != null)
                        rowDiffs.WriteLine(JsonSerializer.Serialize(new { f.Index, f.At, PreviousIndex = previous.Index,
                            Region = region, boundary.CandidateRow, boundary.Row, boundary.Confidence, boundary.Delta,
                            boundary.Reason, RowMeanAbsoluteDifference = boundary.Rows }, Json));
                    previous = f;
                    var hist = new int[256]; int count = 0;
                    for (int i = 0; i < f.Gray.Length; i += 97) { hist[f.Gray[i]]++; count++; }
                    double mean = Enumerable.Range(0, 256).Sum(v => (double)v * hist[v]) / count;
                    frames.WriteLine(Csv(f.Index, f.At, f.SinceFirst, f.Phase, f.Interval, f.Width, f.Height,
                        m.DecodeMs, m.GeometryMs, m.Qr ? 1 : 0, m.Valid ? 1 : 0,
                        m.Valid ? m.Session : null, m.Valid ? m.Sequence : null, m.Valid ? m.Blocks : null,
                        m.Hash, fresh ? 1 : 0, m.Valid && !fresh ? 1 : 0, m.Modules == 0 ? null : m.Modules,
                        m.ModuleX, m.ModuleY, m.CenterX, m.CenterY, mean, Percentile(hist, count, .05), Percentile(hist, count, .95),
                        m.Error, m.GeometryError, f.FocusPhase, boundary.PreviousIndex, boundary.Consecutive ? 1 : 0,
                        region?.X, region?.Y, region?.Width, region?.Height, boundary.CandidateRow, boundary.Row,
                        boundary.Confidence, boundary.Delta, boundary.Mean, boundary.Reason));
                    bool saveMiss = !m.Valid && missSaved < o.MaxMisses;
                    if (f.Index < o.SaveAll || saveMiss || (o.SaveEvery > 0 && f.Index % o.SaveEvery == 0))
                    {
                        if (saveMiss) missSaved++;
                        Pgm(Path.Combine(o.Output, (m.Valid ? "frame-" : "miss-") + f.Index.ToString("D6") + ".pgm"), f);
                        saved++;
                    }
                }
            }
            catch (Exception e) { lock (gate) { decoderError = e; done = true; Monitor.PulseAll(gate); } }
        }) { IsBackground = true, Name = "CameraLab decoder" };
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancelled = true; };
        Console.CancelKeyPress += cancel;
        bool released;
        try
        {
            decoder.Start();
            camera.Start((bgra, w, h) =>
            {
                double at = clock.Elapsed.TotalMilliseconds;
                bool preControls = !initialized;
                if (!initialized) { controls.Apply(); initialized = true; Volatile.Write(ref firstAt, at); }
                string focusPhase = controls.FocusPhase(at);
                var pending = Volatile.Read(ref lockRequest);
                if (pending != null && !controls.AfLockAttempted) { controls.LockFocus(pending); focusPhase = "lock_transition"; }
                if (at >= nextProbe) { controls.Sample(); nextProbe = at + 500; }
                double sinceFirst = at - firstAt;
                if (sinceFirst >= (o.Warmup + o.Seconds) * 1000) return;
                string phase = preControls ? "pre_controls" : sinceFirst < o.Warmup * 1000 ? "warmup" : "measurement";
                var f = new Frame { Index = captured++, At = at, SinceFirst = sinceFirst,
                    Interval = lastAt < 0 ? 0 : at - lastAt, Phase = phase, FocusPhase = focusPhase, Width = w, Height = h };
                lastAt = at;
                if (phase == "measurement") measuredCaptured++;
                lock (gate)
                {
                    bool accept = queue.Count < o.Queue && !done;
                    captures.WriteLine(Csv(f.Index, at, sinceFirst, phase, f.Interval, w, h, accept ? 1 : 0, queue.Count));
                    if (!accept) { dropped++; if (phase == "measurement") measuredDropped++; return; }
                    f.Gray = new byte[w * h];
                    for (int i = 0, j = 0; i < f.Gray.Length; i++, j += 4)
                        f.Gray[i] = (byte)((bgra[j] * 29 + bgra[j + 1] * 150 + bgra[j + 2] * 77) >> 8);
                    queue.Enqueue(f); Monitor.Pulse(gate);
                }
            });
            while (camera.Running && !cancelled)
            {
                lock (gate) { if (decoderError != null) break; }
                double first = Volatile.Read(ref firstAt);
                if (first >= 0 && clock.Elapsed.TotalMilliseconds - first >= (o.Warmup + o.Seconds) * 1000) break;
                if (first < 0 && clock.Elapsed.TotalSeconds > 20) break;
                Thread.Sleep(25);
            }
        }
        finally
        {
            endedAt = clock.Elapsed.TotalMilliseconds;
            camera.Dispose();
            released = camera.WaitForStop(10000);
            lock (gate) { done = true; Monitor.PulseAll(gate); }
            decoder.Join();
            Console.CancelKeyPress -= cancel;
        }
        frames.Flush(); captures.Flush(); controlsLog.Flush(); rowDiffs?.Flush();
        double measurementSeconds = firstAt < 0 ? 0 : Math.Clamp((endedAt - firstAt) / 1000 - o.Warmup, 0, o.Seconds);
        string error = camera.Error ?? decoderError?.ToString() ?? (!released ? "Camera shutdown timed out" :
            captured == 0 ? "No frames within startup timeout" : !cancelled && measured.Analyzed == 0 ? "No analyzed frames in measurement window" : null);
        bool afCompleted = controls.AfLockAccepted && autoFocus.Analyzed > 0 && lockedFocus.Analyzed > 0;
        int exit = error != null ? 2 : cancelled ? 130 : !controls.RequestedSettingsVerified || !controls.RestorationSucceeded ||
            (o.AfLockAfter.HasValue && !afCompleted) ? 3 : 0;
        double autoSeconds = o.AfLockAfter.HasValue && firstAt >= 0 ?
            Math.Max(0, (Math.Min(controls.AfLockAtMs ?? endedAt, endedAt) - firstAt) / 1000 - o.Warmup) : 0;
        double lockedSeconds = controls.AfLockAtMs.HasValue && firstAt >= 0 ?
            Math.Max(0, (endedAt - Math.Max(firstAt + o.Warmup * 1000, controls.AfLockAtMs.Value + o.AfLockSettle * 1000)) / 1000) : 0;
        var summary = new
        {
            Schema = 1, ExitCode = exit, Error = error, Interrupted = cancelled, DeviceReleased = released,
            RequestedSettingsVerified = controls.RequestedSettingsVerified, RestorationSucceeded = controls.RestorationSucceeded,
            RestoreRequested = o.Restore, Width = camera.Width, Height = camera.Height, NegotiatedFps = camera.FramesPerSecond,
            Captured = captured, DroppedBeforeDecode = dropped, MeasuredCaptured = measuredCaptured, MeasuredDropped = measuredDropped,
            FirstFrameAtMs = firstAt, CaptureEndedAtMs = endedAt, MeasurementSeconds = measurementSeconds,
            DeliveredFps = captured > 1 && lastAt > firstAt ? (double?)((captured - 1) * 1000 / (lastAt - firstAt)) : null,
            SavedImages = saved, All = all.Report(firstAt < 0 ? 0 : (endedAt - firstAt) / 1000),
            WarmupIncludingPreControls = warmup.Report(o.Warmup), Measurement = measured.Report(measurementSeconds),
            AfTrial = new { Requested = o.AfLockAfter.HasValue, Completed = afCompleted, controls.AfLockAttempted,
                controls.AfLockAccepted, controls.LockedFocusValue, controls.AfLockAtMs, controls.AfTriggerIndex,
                AutoMeasurement = autoFocus.Report(autoSeconds), LockedMeasurement = lockedFocus.Report(lockedSeconds) },
            Caveats = new[] { "DecodeRate denominator is analyzed frames, not all camera/sender frames.",
                "Unique QR uses full Xfer packet SHA256; sequence identity also includes session and payload metadata.",
                "Module size is estimated only on decoded QR, excluding quiet zone; no geometry on misses.",
                "Control readback confirms driver reporting, not physical exposure/focus; samples every >=500ms.",
                "Full-frame single-worker lab decoder differs from product ROI/multi-worker receiver.",
                "Boundary is a two-frame row-difference candidate, not proof of two distinct QR payloads; only consecutive delivered frames.",
                "AF lock is triggered by a decoded Xfer QR; it reads focus on the next camera callback, not at the successful exposure.",
                "Restore success is immediate driver readback only; delayed driver changes can still occur." }
        };
        File.WriteAllText(Path.Combine(o.Output, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions(Json) { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(summary, Json));
        Console.WriteLine("Results: " + o.Output);
        return exit;
    }

    private static BarcodeReaderGeneric Reader() => new() { AutoRotate = false,
        Options = new DecodingOptions { TryHarder = true, PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE } } };
    private static byte[] Bytes(Result result) => result?.ResultMetadata != null &&
        result.ResultMetadata.TryGetValue(ResultMetadataType.BYTE_SEGMENTS, out var segments) && segments is IEnumerable<byte[]> list ?
        list.SelectMany(s => s).ToArray() : null;
    private static double Distance(ResultPoint a, ResultPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(x => x).ToArray();
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }
    private static int Percentile(int[] hist, int count, double fraction)
    {
        int sum = 0;
        for (int i = 0; i < hist.Length; i++) { sum += hist[i]; if (sum >= count * fraction) return i; }
        return 255;
    }
    private static string Csv(params object[] values) => string.Join(",", values.Select(v =>
    {
        string s = v is double d ? d.ToString("0.###", Inv) : Convert.ToString(v, Inv) ?? "";
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
    }));
    private static StreamWriter Writer(string path, string name) => new(Path.Combine(path, name), false, new UTF8Encoding(false));
    private static void Pgm(string path, Frame f)
    {
        using var file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes($"P5\n{f.Width} {f.Height}\n255\n"));
        file.Write(f.Gray);
    }

    private static int SelfTest()
    {
        // Exercise the actual decoder/geometry and packet identity at two QR versions/scales.
        var bytes = new byte[24];
        bytes[0] = 0xd1; bytes[1] = 0x0c; bytes[2] = 1; bytes[8] = 1; bytes[10] = 4; bytes[12] = 4;
        bytes[20] = 0x80; bytes[21] = 0xff; bytes[22] = 0x10; bytes[23] = 0x7f;
        var stats = new Stats();
        BoundarySelfTest();
        ControlsSelfTest();
        foreach (var version in new[] { 2, 22 })
        {
            int scale = version == 2 ? 5 : 7;
            var qr = ZXing.QrCode.Internal.Encoder.encode(Encoding.Latin1.GetString(bytes),
                ZXing.QrCode.Internal.ErrorCorrectionLevel.L, new Dictionary<EncodeHintType, object>
                { [EncodeHintType.CHARACTER_SET] = "ISO-8859-1", [EncodeHintType.DISABLE_ECI] = true, [EncodeHintType.QR_VERSION] = version });
            int side = qr.Matrix.Width, width = (side + 8) * scale;
            var f = new Frame { Width = width, Height = width, Gray = Enumerable.Repeat((byte)255, width * width).ToArray() };
            for (int y = 0; y < side * scale; y++)
                for (int x = 0; x < side * scale; x++)
                    f.Gray[(y + 4 * scale) * width + x + 4 * scale] = qr.Matrix[x / scale, y / scale] == 1 ? (byte)0 : (byte)255;
            var m = Analyze(f, Reader(), 0);
            Require(m.Valid && m.Modules == side && Math.Abs(m.ModuleX.Value - scale) < .01 && Math.Abs(m.ModuleY.Value - scale) < .01, "decode/geometry");
            stats.Add(m, 0); stats.Add(m, 1);
        }
        Require(stats.Analyzed == 4 && stats.UniqueQr == 1 && stats.DuplicateQr == 3 && stats.UniqueSequences == 1, "duplicate identity across QR versions");
        bytes[4] = 1;
        Require(OpticalFrame.TryParse(bytes, out var packet) && packet.Sequence == 1, "sequence parsing");
        var changed = new Measurement { Valid = true, Qr = true, Hash = Convert.ToHexString(SHA256.HashData(bytes)),
            SessionKey = "new-session", Sequence = 1 };
        stats.Add(changed, 2);
        stats.Add(new Measurement(), 3);
        Require(stats.UniqueQr == 2 && stats.UniqueSequences == 2 && stats.Analyzed == 6 && stats.XferDecoded == 5, "new QR and miss denominator");
        bytes[10] = 5;
        Require(!OpticalFrame.TryParse(bytes, out _), "invalid packet rejected");
        try { Options.Parse(new[] { "capture", "unused", "1920", "30", "5", "--bogus" }); throw new Exception("bad option accepted"); }
        catch (ArgumentException) { }
        Console.WriteLine("PASS: QR versions 2/22, 5/7 px modules, duplicates/new QR/misses, packet validation, row boundary/gap, AF lock/restore/drift simulation, format rational fps/FourCC, CLI rejection; no camera used.");
        return 0;
    }
    private static void Require(bool ok, string label) { if (!ok) throw new Exception("Selftest failed: " + label); }
}
