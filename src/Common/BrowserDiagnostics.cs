using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Xfer
{
    // Separate bounded sink: an untrusted browser cannot enqueue arbitrary objects in host logs.
    internal static class BrowserDiagnostics
    {
        private static readonly SemaphoreSlim Slot = new SemaphoreSlim(1, 1);
        private static readonly string Run = Guid.NewGuid().ToString("N");
        private static readonly string[] Files = new string[2];
        private static readonly int[] Parts = new int[2];
        private static DateTime Window = DateTime.UtcNow;
        private static int Requests;
        private static readonly HashSet<string> Events = Words(
            "page.start page.heartbeat page.visibility page.online page.offline page.pagehide page.error page.unhandled_rejection " +
            "logging.loss logging.sampler_failed ui.action ui.setting send.status send.display.start send.display.stop send.queue_empty send.draw send.batch.retry send.batch.begin send.batch.end send.batch.failed " +
            "camera.status camera.request camera.open camera.open.failed camera.running camera.track_ended camera.stop camera.failed camera.video_callback camera.no_worker camera.capture " +
            "worker.start worker.failed worker.ready decode.read decode.miss decode.foreign decode.non_transfer decode.failed " +
            "relay.retry relay.failed relay.accepted relay.rejected relay.duplicate relay.full relay.foreign relay.invalid relay.stopped relay.progress receive.complete " +
            "http.headers.begin http.headers.end http.headers.failed http.body.begin http.body.end http.body.failed");
        private static readonly HashSet<string> Fields = Words(
            "count totalMs maxMs last status dropped storageFailures intervalMs visible online waiting environment device build browser os cpus dpr width height standalone " +
            "drawn queued pending nextSequence starting running captureAgeMs readAgeMs frames queue inFlight retries workers ready failed busy ageMs fps frameBytes " +
            "sourceBlocks totalBytes errorCorrection sequence lateMs failures type code site stage line column failure initTimeout wasReady symbols invalid foreign " +
            "receiver cropWidth cropHeight full recognized complete solved blocks received duplicates elapsedSeconds files bytes clearServer value correlation wasmStatus wasmNetworkFailed");
        private static readonly HashSet<string> Stages = Words("http.headers http.body worker.boot worker.import worker.wasm_initialize worker.script worker.init_timeout decode.image decode.normal decode.harder decode.histogram decode.unknown");
        private static readonly HashSet<string> Types = Words("Error TypeError RangeError ReferenceError SyntaxError RuntimeError CompileError LinkError NetworkError AbortError SecurityError InvalidStateError OtherError WorkerError Timeout NotAllowedError NotFoundError NotReadableError OverconstrainedError");
        private static readonly HashSet<string> Controls = Words("showQr closeQr readLocalCamera receiveOpenFolder pickFiles pickFolder selectAll cameraNoSignalHelp cameraHelpClose direction selection control frameAmount frameRate errorCorrection cameraWidth cameraFps cameraWorkers asset_or_unknown");
        private static HashSet<string> Words(string value) { return new HashSet<string>(value.Split(' '), StringComparer.Ordinal); }
        private static bool Match(string value, string expression) { return value != null && Regex.IsMatch(value, expression, RegexOptions.CultureInvariant); }
        private static bool Correlation(string value)
        {
            return value == "" || Controls.Contains(value) || Match(value, "^[a-f0-9]{8}$")
                || Match(value, "^[0-9]{1,10}:[0-9]{1,10}:[0-9]{1,10}:[0-9]{1,10}$")
                || Match(value, "^/api/(status|folder|pick-files|pick-folder|devices|shutdown|remote-entry(/qr)?|remotes(/(heartbeat|command|disconnect))?|optical/(start|stop|frames?|receive/(start|stop|frames?)))$");
        }
        private static object Clean(object value, string key, int depth)
        {
            if (depth > 8) throw new ArgumentException("Diagnostic nesting limit");
            if (value == null || value is bool || value is long || value is double || value is decimal) return value;
            var text = value as string;
            if (text != null) {
                bool safe = key == "stage" ? Stages.Contains(text)
                    : key == "type" ? Types.Contains(text)
                    : key == "site" ? text == "qr-worker" || text == "app.startCameraWorkers" || text == "app.workerMessage" || text == "app.workerError"
                    : key == "correlation" ? Correlation(text)
                    : key == "receiver" ? Match(text, "^[a-f0-9]{8}$")
                    : key == "device" ? Match(text, "^[a-f0-9.-]{8,80}$")
                    : key == "build" ? Match(text, "^[a-f0-9]{8,64}$")
                    : key == "browser" ? text == "unknown" || Match(text, "^(Firefox|Edg|Chrome|Version)/[0-9.]{1,32}$")
                    : key == "os" ? Words("Android iOS Windows macOS other").Contains(text)
                    : (key == "value" || key == "errorCorrection") && Match(text, "^[LMQH]$");
                return safe ? text : "redacted";
            }
            var map = value as Dictionary<string, object>;
            if (map != null) {
                var result = new Dictionary<string, object>();
                foreach (var pair in map) if (Fields.Contains(pair.Key)) result[pair.Key] = Clean(pair.Value, pair.Key, depth + 1);
                return result;
            }
            var list = value as List<object>;
            if (list != null && list.Count <= 32) {
                var result = new List<object>();
                foreach (var item in list) result.Add(Clean(item, key, depth + 1));
                return result;
            }
            throw new ArgumentException("Diagnostic field type");
        }
        public static bool TryEnter()
        {
            if (!Slot.Wait(0)) return false;
            if ((DateTime.UtcNow - Window).TotalMinutes >= 1) { Window = DateTime.UtcNow; Requests = 0; }
            if (++Requests > 120) { Slot.Release(); return false; }
            return true;
        }
        public static void Leave() { Slot.Release(); }
        public static int Save(byte[] body)
        {
            // Bound recursion before the common JSON parser sees untrusted nested data.
            bool quoted = false, escape = false; int depth = 0;
            foreach (byte b in body) {
                if (quoted) { if (escape) escape = false; else if (b == 92) escape = true; else if (b == 34) quoted = false; }
                else if (b == 34) quoted = true;
                else if (b == 123 || b == 91) { if (++depth > 12) throw new ArgumentException("Diagnostic nesting limit"); }
                else if (b == 125 || b == 93) depth--;
            }
            var envelope = JsonCodec.ParseObject(body);
            object raw;
            var records = envelope.TryGetValue("records", out raw) ? raw as List<object> : null;
            if (records == null || records.Count < 1 || records.Count > 48) throw new ArgumentException("Diagnostic batch limit");
            int archive = envelope.TryGetValue("archive", out raw) && raw is bool && (bool)raw ? 1 : 0;
            var output = new MemoryStream();
            foreach (var item in records) {
                var record = item as Dictionary<string, object>;
                if (record == null) throw new ArgumentException("Diagnostic record");
                string name = Text(record, "event"), run = Text(record, "run"), utc = Text(record, "utc"), correlation = Text(record, "correlation");
                DateTime time;
                if (!Match(run, "^[a-f0-9.-]{8,80}$")
                    || utc.Length > 40 || !DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time))
                    throw new ArgumentException("Diagnostic metadata");
                // A record from a newer page must not prevent recovery of the rest of an old backlog.
                if (!Events.Contains(name)) name = "logging.unsupported_event";
                if (!Correlation(correlation)) correlation = "redacted";
                var safe = new Dictionary<string, object> { { "schema", 1 }, { "route", "browser" }, { "event", name },
                    { "run", run }, { "utc", time.ToUniversalTime().ToString("O") }, { "correlation", correlation },
                    { "seq", Number(record, "seq") }, { "monoMs", Number(record, "monoMs") },
                    { "data", record.TryGetValue("data", out raw) ? Clean(raw, "data", 0) : null } };
                byte[] bytes = JsonCodec.Serialize(safe);
                if (bytes.Length > 8192) throw new ArgumentException("Diagnostic record limit");
                output.Write(bytes, 0, bytes.Length); output.WriteByte(10);
            }
            string directory = Diagnostics.LogDirectory();
            Directory.CreateDirectory(directory);
            string prefix = archive == 1 ? "browser-incident-" : "browser-trace-";
            if (Files[archive] == null || !File.Exists(Files[archive]) || new FileInfo(Files[archive]).Length + output.Length > 2 * 1024 * 1024) {
                var files = new List<FileInfo>(new DirectoryInfo(directory).GetFiles(prefix + "*.jsonl"));
                files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
                for (int i = (archive == 1 ? 4 : 8) - 1; i < files.Count; i++) files[i].Delete();
                Files[archive] = Path.Combine(directory, prefix + Run + "-" + (++Parts[archive]).ToString("D4") + ".jsonl");
            }
            using (var file = new FileStream(Files[archive], FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete)) {
                output.Position = 0; output.CopyTo(file); file.Flush(true);
            }
            return records.Count; // acknowledgement only after durable append; lost acknowledgements may duplicate run/seq
        }
        private static string Text(Dictionary<string, object> map, string key)
        { object value; return map.TryGetValue(key, out value) ? value as string ?? "" : ""; }
        private static object Number(Dictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value) || !(value is long || value is double || value is decimal)) throw new ArgumentException("Diagnostic number");
            return value;
        }
    }
}
