using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Xfer
{
    // Only call with fixed event/field names and deliberately selected metadata. Never pass
    // request bodies, URLs, paths, exception messages, QR bytes or object snapshots here.
    internal static class Diagnostics
    {
        private static readonly object Gate = new object();
        private static readonly Queue<object> Pending = new Queue<object>();
        private static readonly Dictionary<string, Aggregate> Samples = new Dictionary<string, Aggregate>();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly string Run = Guid.NewGuid().ToString("N");
        private static readonly Timer Timer;
        private static long _sequence, _dropped, _writeFailures;
        private static int _writing;
        private static string _directory, _file;
        private static int _part;
        private static object _environment;
        private const int MaximumQueue = 256, MaximumSamples = 256, MaximumFiles = 8;
        private const long MaximumFileBytes = 2 * 1024 * 1024;

        static Diagnostics()
        {
            // All filesystem work, including directory creation, runs on this background timer.
            Timer = new Timer(delegate { Flush(); }, null, 1000, 5000);
            AppDomain.CurrentDomain.ProcessExit += delegate { Event("process.exit", "", null); Flush(); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) {
                Failure("process.unhandled", "", e.ExceptionObject as Exception); Flush();
            };
            try { _environment = new {
                Device = Id(Environment.MachineName), Os = Environment.OSVersion.ToString(),
                Runtime = Environment.Version.ToString(), Cpus = Environment.ProcessorCount,
                Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(),
                Build = Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString("N"),
                Pid = Process.GetCurrentProcess().Id
            }; }
            catch { _environment = new { MetadataUnavailable = true }; }
            Event("process.start", "", _environment);
        }

        public static string Id(string value)
        {
            // Correlation pseudonym, not an authentication token or a content hash.
            uint h = 2166136261;
            foreach (char c in value ?? "") h = unchecked((h ^ c) * 16777619);
            return h.ToString("x8", CultureInfo.InvariantCulture);
        }
        public static string Transfer(int session, int blocks, int length, int total)
        { return session + ":" + blocks + ":" + length + ":" + total; }
        public static long Stamp() { return Stopwatch.GetTimestamp(); }
        internal static string LogDirectory()
        {
            var directory = Environment.GetEnvironmentVariable("Xfer_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(directory)) return directory;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrWhiteSpace(local)) throw new IOException("No local diagnostic directory");
            return Path.Combine(local, "Xfer", "logs");
        }
        public static double Milliseconds(long stamp) { return (Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency; }
        public static void Failure(string stage, string correlation, Exception error,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Event(stage + ".failed", correlation, new { Type = error == null ? "Unknown" : error.GetType().Name,
                Code = error == null ? 0 : error.HResult, Site = Path.GetFileName(file) + ":" + member, Line = line,
                Origin = ErrorOrigin(error) });
        }
        private static object ErrorOrigin(Exception error)
        {
            // One product frame only. Never persist stack text, arguments or absolute source paths.
            try {
                if (error == null) return null;
                var cause = error.GetBaseException();
                var frames = new StackTrace(cause, true).GetFrames();
                if (frames != null) foreach (var frame in frames) {
                    var method = frame.GetMethod();
                    if (method == null || method.DeclaringType == null || method.DeclaringType.Namespace != "Xfer") continue;
                    return new { Type = cause.GetType().Name, Code = cause.HResult,
                        Site = method.DeclaringType.Name + "." + method.Name,
                        Line = frame.GetFileLineNumber(), Il = frame.GetILOffset() };
                }
            } catch { }
            return null;
        }
        public static void Event(string name, string correlation, object data)
        {
            lock (Gate) Enqueue(name, correlation, data);
        }
        private static void Enqueue(string name, string correlation, object data)
        {
            if (Pending.Count >= MaximumQueue) { _dropped++; return; }
            Pending.Enqueue(new { Schema = 1, Utc = DateTime.UtcNow.ToString("O"), MonoMs = Clock.ElapsedMilliseconds,
                Run = Run, Seq = ++_sequence, Route = "host", Event = name, Correlation = correlation, Data = data });
        }
        // One bounded accumulator per stage/correlation; per-frame calls perform no serialization or I/O.
        public static void Sample(string name, string correlation, double ms, object last)
        {
            lock (Gate)
            {
                string key = name + "|" + correlation;
                Aggregate a;
                if (!Samples.TryGetValue(key, out a))
                {
                    if (Samples.Count >= MaximumSamples) { _dropped++; return; }
                    a = new Aggregate { Name = name, Correlation = correlation };
                    Samples.Add(key, a);
                }
                a.Count++; a.TotalMs += ms; a.MaxMs = Math.Max(a.MaxMs, ms); a.Last = last;
            }
        }
        private sealed class Aggregate
        {
            public string Name, Correlation;
            public long Count;
            public double TotalMs, MaxMs;
            public object Last;
        }
        public static void Flush()
        {
            TryFlush();
        }
        // Called on the bounded shutdown worker, not on the transfer/UI thread. A write already
        // in progress must finish before the final queue snapshot, otherwise its tail would be lost.
        internal static bool FlushFinal()
        {
            Timer.Dispose();
            while (!TryFlush()) Thread.Sleep(1);
            return Interlocked.Read(ref _writeFailures) == 0;
        }
        private static bool TryFlush()
        {
            if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0) return false;
            try
            {
                var records = new List<object>();
                lock (Gate)
                {
                    Enqueue("process.heartbeat", "", _environment);
                    foreach (var a in Samples.Values)
                        Enqueue(a.Name, a.Correlation, new { Count = a.Count, TotalMs = a.TotalMs, MaxMs = a.MaxMs, Last = a.Last });
                    Samples.Clear();
                    // Reserve space for loss accounting even when the producer queue was full.
                    while (Pending.Count > 0) records.Add(Pending.Dequeue());
                    if (_dropped > 0 || _writeFailures > 0)
                        Enqueue("logging.loss", "", new { Dropped = _dropped, WriteFailures = _writeFailures });
                    _dropped = 0;
                    while (Pending.Count > 0) records.Add(Pending.Dequeue());
                }
                if (records.Count == 0) return true;
                if (_directory == null)
                {
                    _directory = LogDirectory();
                }
                Directory.CreateDirectory(_directory);
                foreach (var record in records)
                {
                    if (_file == null || (File.Exists(_file) && new FileInfo(_file).Length >= MaximumFileBytes))
                    {
                        _file = Path.Combine(_directory, "trace-" + Run + "-" + (++_part).ToString("D4") + ".jsonl");
                        Prune();
                    }
                    byte[] bytes = JsonCodec.Serialize(record);
                    using (var stream = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete))
                    { stream.Write(bytes, 0, bytes.Length); stream.WriteByte(10); }
                }
                _writeFailures = 0;
            }
            catch { Interlocked.Increment(ref _writeFailures); /* logging must never fail the transfer */ }
            finally { Interlocked.Exchange(ref _writing, 0); }
            return true;
        }
        private static void Prune()
        {
            var files = new List<FileInfo>(new DirectoryInfo(_directory).GetFiles("trace-*.jsonl"));
            files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
            for (int i = MaximumFiles - 1; i < files.Count; i++)
                try { files[i].Delete(); } catch { }
        }
    }
}
