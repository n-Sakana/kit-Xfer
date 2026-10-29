using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Xfer
{
    internal static class StreamingTests
    {
        private static long _peak, _minimumAvailable = long.MaxValue;
        private static bool _stopMonitor;
        private static string _root;
        public static int Main(string[] args)
        {
            if (args.Length < 2) { Console.Error.WriteLine("StreamingTests WORK selftest|fx2|picture BYTES"); return 2; }
            _root = Path.GetFullPath(args[0]); Directory.CreateDirectory(_root);
            var monitor = new Thread(delegate()
            {
                while (!_stopMonitor)
                {
                    _peak = Math.Max(_peak, Process.GetCurrentProcess().WorkingSet64);
                    if (File.Exists("/proc/meminfo"))
                    {
                        string line = File.ReadLines("/proc/meminfo").First(s => s.StartsWith("MemAvailable:"));
                        long available = long.Parse(line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
                        _minimumAvailable = Math.Min(_minimumAvailable, available);
                        if (available < 9L * 1024 * 1024 * 1024) { Console.Error.WriteLine("STOP: MemAvailable below 9 GiB"); Environment.Exit(3); }
                    }
                    Thread.Sleep(250);
                }
            }) { IsBackground = true };
            monitor.Start();
            try
            {
                if (args[1] == "selftest") SelfTest();
                else if (args[1] == "small-stream") SmallStreamingBenchmark();
                else if (args[1] == "picture") PictureTransfer(long.Parse(args[2]));
                else Transfer(long.Parse(args[2]), true);
                _peak = Math.Max(_peak, Process.GetCurrentProcess().PeakWorkingSet64);
                Console.WriteLine("PASS peak_RSS_bytes=" + _peak + " min_MemAvailable_bytes=" + _minimumAvailable);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { _stopMonitor = true; monitor.Join(); }
        }
        private static void Check(bool b, string text) { if (!b) throw new Exception(text); }
        private static string Hash(string path) { using (var f = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(f)); }
        private static void Make(string path, long size)
        {
            var bytes = new byte[65536]; var random = new Random(20261024);
            using (var file = File.Create(path)) while (size > 0) { random.NextBytes(bytes); int n = (int)Math.Min(size, bytes.Length); file.Write(bytes, 0, n); size -= n; }
        }
        private static void Transfer(long size, bool cells)
        {
            var input = Path.Combine(_root, "input"); var output = Path.Combine(_root, "output"); Directory.CreateDirectory(input);
            var path = Path.Combine(input, "random.bin"); Make(path, size);
            try
            {
                var folder = FolderCatalog.Inspect(input); var names = new[] { "random.bin" };
                using (var sender = new OpticalService())
                using (var receiver = new OpticalReceiveService(output))
                {
                    var watch = Stopwatch.StartNew(); var started = sender.StartFx2(folder, names, 30, "L");
                    Console.WriteLine("prepared bytes=" + size + " wire=" + started.TransmittedBytes + " blocks=" + started.SourceBlocks + " seconds=" + watch.Elapsed.TotalSeconds);
                    var code = Fx2Code.ForLevel(0); var bits = new byte[Fx2.DataCells.Length];
                    int fullBlocks = StreamingWire.Blocks(StreamingWire.ChunkBytes, code.FrameBytes - StreamingWire.Header);
                    long chunks = (started.TransmittedBytes - 1) / StreamingWire.ChunkBytes + 1;
                    OpticalReceiveResult result = null; long frames = 0; double report = watch.Elapsed.TotalSeconds;
                    ulong sequence = 0;
                    while (result == null || !result.Complete)
                    {
                        // A batch is an ideal screen's data cells, with four CPU workers only for the
                        // expensive pixel-codec test. Product sender and receiver are the same instances.
                        int batchSize = size > 64 * 1024 * 1024 ? 512 : 1;
                        var batch = new byte[batchSize][];
                        for (int k = 0; k < batch.Length; k++)
                        {
                            Check(sender.TryFrame(started.Token, sequence, out batch[k]), "sender stopped");
                            if (batch[k][1] == 0x0d && (sequence % (ulong)(fullBlocks * 2)) == (ulong)(fullBlocks - 1)) sequence += (ulong)fullBlocks;
                            sequence++;
                        }
                        if (cells)
                        {
                            Parallel.For(0, batch.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, delegate(int k)
                            {
                                var screenCells = new byte[Fx2.DataCells.Length];
                                code.Encode(batch[k], screenCells);
                                byte[] recovered; int corrected;
                                Check(code.TryDecode(screenCells, out recovered, out corrected), "FX2 screen-cell decode"); batch[k] = recovered;
                            });
                        }
                        foreach (var frame in batch)
                        {
                            result = receiver.AddFrame("test", frame, false); frames++;
                            if (result.Complete) break;
                        }
                        if (watch.Elapsed.TotalSeconds - report >= 20)
                        { Console.WriteLine("progress=" + result.Progress.ToString("F4") + " frames=" + frames + " elapsed=" + watch.Elapsed.TotalSeconds.ToString("F1") + " RSS=" + Process.GetCurrentProcess().WorkingSet64); report = watch.Elapsed.TotalSeconds; }
                        if (sequence > (ulong)(chunks * fullBlocks * 6 + 1000000)) throw new Exception("Transfer did not finish");
                    }
                    double seconds = watch.Elapsed.TotalSeconds;
                    Check(Hash(path) == Hash(Path.Combine(result.OutputPath, "random.bin")), "SHA256 mismatch");
                    Console.WriteLine("MATCH bytes=" + size + " frames=" + frames + " seconds=" + seconds.ToString("F3") + " MiB_s=" + (size / 1048576.0 / seconds).ToString("F3") + " SHA256=" + Hash(path));
                    Check(!Directory.GetDirectories(output, ".xfer-*").Any(), "temporary receiver files leaked");
                }
            }
            finally { if (Directory.Exists(input)) Directory.Delete(input, true); if (Directory.Exists(output)) Directory.Delete(output, true); }
        }
        private static void SmallStreamingBenchmark()
        {
            string input = Path.Combine(_root, "input"), output = Path.Combine(_root, "output"); Directory.CreateDirectory(input);
            var content = new byte[5976883]; new Random(20261024).NextBytes(content);
            string path = Path.Combine(input, "sample.bin"); File.WriteAllBytes(path, content);
            try
            {
                for (int run = 0; run < 3; run++)
                {
                    var source = FolderCatalog.Inspect(input); var clock = Stopwatch.StartNew();
                    using (var bundle = new StreamingBundle(source, new[] { "sample.bin" }))
                    using (var sender = new StreamingEncoder(bundle, Fx2Code.ForLevel(0).FrameBytes))
                    using (var receiver = new OpticalReceiveService(output))
                    {
                        double prep = clock.Elapsed.TotalSeconds;
                        var code = Fx2Code.ForLevel(0); var cells = new byte[Fx2.DataCells.Length];
                        OpticalReceiveResult result = null; ulong sequence;
                        for (sequence = 0; sequence < 200000 && (result == null || !result.Complete); sequence++)
                        {
                            byte[] decoded; int corrected; code.Encode(sender.Frame(sequence), cells);
                            Check(code.TryDecode(cells, out decoded, out corrected), "small streaming decode");
                            result = receiver.AddFrame("small-stream", decoded, false);
                        }
                        double seconds = clock.Elapsed.TotalSeconds;
                        Check(result != null && result.Complete && content.SequenceEqual(File.ReadAllBytes(Path.Combine(result.OutputPath, "sample.bin"))), "small streaming mismatch");
                        Console.WriteLine("STREAM_MATCH run=" + run + " bytes=" + content.Length + " frames=" + sequence + " prep_s=" + prep.ToString("F4") + " total_s=" + seconds.ToString("F4"));
                        Directory.Delete(result.OutputPath, true);
                    }
                    GC.Collect();
                }
            }
            finally { if (Directory.Exists(input)) Directory.Delete(input, true); if (Directory.Exists(output)) Directory.Delete(output, true); }
        }
        private static void PictureTransfer(long size)
        {
            string input = Path.Combine(_root, "input"), output = Path.Combine(_root, "output"); Directory.CreateDirectory(input);
            string path = Path.Combine(input, "random.bin"); Make(path, size);
            try
            {
                using (var sender = new OpticalService())
                using (var receiver = new OpticalReceiveService(output))
                {
                    var watch = Stopwatch.StartNew();
                    var start = sender.StartFx2(FolderCatalog.Inspect(input), new[] { "random.bin" }, 30, "L");
                    var layout = Fx2Layout.Create(1920, 1020, 3, 2);
                    var reader = new Fx2Reader(); var code = Fx2Code.ForLevel(0);
                    OpticalReceiveResult result = null; long pictures = 0; double report = 0;
                    for (ulong sequence = 0; result == null || !result.Complete; sequence += (ulong)layout.Patches)
                    {
                        var picture = new Fx2Picture(layout, code, sequence, delegate(ulong s)
                        { byte[] f; Check(sender.TryFrame(start.Token, s, out f), "picture sender stopped"); return f; });
                        var recovered = reader.Read(picture.Render(), layout.Width, layout.Height); pictures++;
                        Check(recovered.Frames.Count == layout.Patches, "clean synthetic picture lost patches");
                        foreach (var frame in recovered.Frames) { result = receiver.AddFrame("picture", frame, false); if (result.Complete) break; }
                        if (watch.Elapsed.TotalSeconds - report >= 20)
                        { Console.WriteLine("picture_progress=" + result.Progress.ToString("F4") + " pictures=" + pictures + " elapsed=" + watch.Elapsed.TotalSeconds.ToString("F1")); report = watch.Elapsed.TotalSeconds; }
                        if (sequence > (ulong)start.SourceBlocks * 6 + 1000000) throw new Exception("picture transfer did not finish");
                    }
                    double seconds = watch.Elapsed.TotalSeconds;
                    Check(Hash(path) == Hash(Path.Combine(result.OutputPath, "random.bin")), "picture SHA256 mismatch");
                    Console.WriteLine("PICTURE_MATCH bytes=" + size + " pictures=" + pictures + " seconds=" + seconds.ToString("F3") + " SHA256=" + Hash(path));
                }
            }
            finally { if (Directory.Exists(input)) Directory.Delete(input, true); if (Directory.Exists(output)) Directory.Delete(output, true); }
        }
        private static void SelfTest()
        {
            var input = Path.Combine(_root, "cases"); var output = Path.Combine(_root, "saved"); Directory.CreateDirectory(input);
            try
            {
                for (int i = 0; i < 2001; i++) File.WriteAllText(Path.Combine(input, "f" + i.ToString("D4")), (i == 0 ? "" : new string('x', 137) + "file " + i));
                var source = FolderCatalog.Inspect(input); var names = source.Files.Select(f => f.Name).ToArray();
                using (var sender = new OpticalService())
                using (var receiver = new OpticalReceiveService(output))
                {
                    var start = sender.Start(source, names, 1000, 30, "L"); byte[] first;
                    Check(sender.TryFrame(start.Token, 0, out first) && first[1] == 0x0d, "2001 files did not select streaming");
                    int block = 1000 - StreamingWire.Header, full = StreamingWire.Blocks(StreamingWire.ChunkBytes, block);
                    long chunks = (start.TransmittedBytes - 1) / StreamingWire.ChunkBytes + 1;
                    Check(chunks > 4, "test must evict live decoder windows");
                    OpticalReceiveResult result = null;
                    // Force cache eviction with one missing systematic block in every window, then repair on
                    // the next sweep. Reverse each window and duplicate packets.
                    for (int pass = 0; pass < 2; pass++)
                    for (long chunk = 0; chunk < chunks; chunk++)
                    for (int j = full - 1; j >= 0; j--)
                    {
                        if (pass == 0 && j == 0) continue;
                        ulong seq = (ulong)((pass * chunks + chunk) * full * 2 + j);
                        byte[] frame; sender.TryFrame(start.Token, seq, out frame);
                        result = receiver.AddFrame("many", frame, false);
                        receiver.AddFrame("many", frame, false);
                        if (result.Complete) break;
                    }
                    Check(result != null && result.Complete && result.FileCount == 2001, "reordered/resumed transfer incomplete");
                    foreach (string name in names) Check(File.ReadAllText(Path.Combine(result.OutputPath, name)) == File.ReadAllText(Path.Combine(input, name)), "many-file mismatch");
                    Console.WriteLine("PASS 2001 files, duplicates, reordered windows, disk checkpoint and later sweep");
                    receiver.Reset("many"); receiver.AddFrame("many", first, false); receiver.Stop("many");
                    Check(!Directory.GetDirectories(output, ".xfer-*").Any(), "stop leaked staging");
                    Check(!receiver.AddFrame("many", first, false).Recognized, "stopped receiver accepted");
                    var closing = new OpticalReceiveService(output);
                    closing.AddFrame("closing", first, false); closing.Dispose();
                    Check(!closing.AddFrame("closing", first, false).Recognized && !Directory.GetDirectories(output, ".xfer-*").Any(), "disposed receiver recreated staging");
                    receiver.Reset("many"); var corrupt = (byte[])first.Clone(); corrupt[16] = 0; for (int i = 17; i < 24; i++) corrupt[i] = 0;
                    Check(!receiver.AddFrame("many", corrupt, false).Recognized, "invalid 64-bit total accepted");
                    byte[] high; Check(sender.TryFrame(start.Token, (1UL << 32) + 5, out high), "64-bit sender sequence failed");
                    StreamingFrame parsed; Check(StreamingFrame.TryParse(high, out parsed), "high sequence malformed");
                    var huge = (byte[])first.Clone();
                    StreamingWire.Put(huge, 16, BitConverter.GetBytes(8L * 1024 * 1024 * 1024 * 1024));
                    var hugeProgress = receiver.AddFrame("huge", huge, false);
                    Check(hugeProgress.TotalBytes == 8L * 1024 * 1024 * 1024 * 1024 && hugeProgress.SourceBlocks > int.MaxValue, "64-bit receive totals truncated");
                    receiver.Stop("huge");
                    var damaged = (byte[])first.Clone(); damaged[StreamingWire.Header + 100] ^= 1;
                    bool rejected = false;
                    try
                    {
                        receiver.AddFrame("bad", damaged, false);
                        for (ulong seq = 1; seq < (ulong)full; seq++) { byte[] f; sender.TryFrame(start.Token, seq, out f); receiver.AddFrame("bad", f, false); }
                    }
                    catch (InvalidDataException) { rejected = true; }
                    Check(rejected && !Directory.GetDirectories(output, ".xfer-*").Any(), "corrupt window was published or leaked staging");
                    Console.WriteLine("PASS 8 TiB metadata, >int.MaxValue blocks, corrupt window rejection and cleanup");
                    Console.WriteLine("PASS cancellation, malformed lengths and sequence > 2^32");
                    // Real QR encoding/decoding of a streaming frame, without a window.
                    byte[] rgba; sender.TryRenderRasterFrame(start.Token, 0, out rgba);
                    int side = (int)Math.Sqrt(rgba.Length / 4);
                    var reader = new ZXing.BarcodeReaderGeneric { AutoRotate = false, Options = new ZXing.Common.DecodingOptions { PureBarcode = true } };
                    var qr = reader.Decode(new ZXing.RGBLuminanceSource(rgba, side, side, ZXing.RGBLuminanceSource.BitmapFormat.RGBA32));
                    Check(qr != null, "streaming QR decode failed");
                    Console.WriteLine("PASS streaming QR raster roundtrip");
                }
                // Fountain parity recovers missing systematic frames inside a bounded window.
                using (var bundle = new StreamingBundle(source, names))
                using (var encoder = new StreamingEncoder(bundle, 1000))
                using (var receiver = new OpticalReceiveService(Path.Combine(output, "parity")))
                {
                    int full = StreamingWire.Blocks(StreamingWire.ChunkBytes, 1000 - StreamingWire.Header);
                    long chunks = (bundle.Length - 1) / StreamingWire.ChunkBytes + 1;
                    OpticalReceiveResult p = null;
                    for (ulong seq = 0; seq < (ulong)(chunks * full * 8); seq++)
                    { if (seq % 13 == 0) continue; p = receiver.AddFrame("parity", encoder.Frame(seq), false); if (p.Complete) break; }
                    Check(p != null && p.Complete, "loss recovery failed"); Console.WriteLine("PASS fountain loss recovery");
                }
            }
            finally { if (Directory.Exists(input)) Directory.Delete(input, true); if (Directory.Exists(output)) Directory.Delete(output, true); }
            BundleValidation();
            Transfer(9 * 1024 * 1024, false);
        }
        private static void BundleValidation()
        {
            string output = Path.Combine(_root, "invalid"); Directory.CreateDirectory(output);
            try
            {
                foreach (string path in new[] { "../outside", "/absolute", "a/b" })
                {
                    using (var stream = new MemoryStream())
                    using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                    {
                        writer.Write(new byte[] { 70, 88, 66, 51 }); writer.Write("test"); writer.Write(1L);
                        writer.Write(path); writer.Write(1L); writer.Write(new byte[32]); writer.Write((byte)17);
                        stream.Position = 0;
                        bool rejected = false;
                        try { StreamingBundleReader.Save(stream, stream.Length, output); }
                        catch (ArgumentException) { rejected = true; }
                        catch (InvalidDataException) { rejected = true; }
                        Check(rejected && Directory.GetFileSystemEntries(output).Length == 0, "unsafe path or corrupt SHA256 accepted");
                    }
                }
                Console.WriteLine("PASS unsafe paths and SHA256 failure leave no published or temporary output");
            }
            finally { Directory.Delete(output, true); }
        }
    }
}
