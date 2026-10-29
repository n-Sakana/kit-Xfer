using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Xfer
{
    internal static class DiagnosticsTests
    {
        private const string Secret = "PRIVATE_FILENAME_AND_CONTENT_91af";
        public static int Main(string[] args)
        {
            if (args.Length != 1) throw new ArgumentException("Pass an isolated test output directory.");
            var root = Path.GetFullPath(args[0]);
            var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            Environment.SetEnvironmentVariable("Xfer_LOG_DIR", logs);
            // Exercise global file-count retention before the logger opens its first part.
            for (int i = 0; i < 12; i++) File.WriteAllText(Path.Combine(logs, "trace-fixture-" + i + ".jsonl"), "");
            var input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
            var sourceBytes = new byte[12000]; new Random(2468).NextBytes(sourceBytes);
            Encoding.UTF8.GetBytes(Secret).CopyTo(sourceBytes, 0);
            File.WriteAllBytes(Path.Combine(input, Secret), sourceBytes);
            var source = FolderCatalog.Inspect(input);
            var sender = new OpticalService();
            var session = sender.Start(source, new[] { Secret }, 1000, 30, "L");
            byte[] rendered; Require(sender.TryRenderPackedFrames(session.Token, 0, 1, out rendered), "real QR generation");
            var payload = OpticalPayload.Build(source, new[] { Secret }).Bytes;
            var receiver = new OpticalReceiveService(Path.Combine(root, "received"));
            receiver.Reset("test-receiver");
            receiver.AddFrame("test-receiver", new byte[] { 1, 2, 3 }, false);
            receiver.AddFrame("test-receiver", Wire(payload, 0, 120), false);
            receiver.AddFrame("test-receiver", Wire(payload, 0, 120), false);
            receiver.AddFrame("test-receiver", Wire(payload, 1, 121), false);
            var complete = Finish(receiver, "test-receiver", payload, 120);
            Require(complete.Complete && File.ReadAllBytes(Path.Combine(complete.OutputPath, Secret)).SequenceEqual(sourceBytes), "saved bytes unchanged");
            receiver.Stop("test-receiver");
            Require(!receiver.AddFrame("test-receiver", Wire(payload, 0, 120), false).Recognized, "cancel rejects delayed data");
            receiver.Reset("cancelled"); receiver.AddFrame("cancelled", Wire(payload, 0, 122), false); receiver.Stop("cancelled");
            var corrupt = (byte[])payload.Clone(); corrupt[17] ^= 1;
            try { Finish(receiver, "corrupt", corrupt, 123); throw new Exception("corruption accepted"); }
            catch (InvalidDataException) { }
            var blockedPath = Path.Combine(root, "blocked-output"); File.WriteAllText(blockedPath, "fixture");
            try { Finish(new OpticalReceiveService(blockedPath), "save-failure", payload, 124); throw new Exception("save failure missing"); }
            catch (IOException) { }
            sender.Stop(session.Token);
            Diagnostics.Failure("test.exception", "", new IOException(Secret));
            Diagnostics.Flush();
            Thread.Sleep(100);
            var text = Read(logs);
            foreach (var name in new[] { "send.ready", "send.qr", "receive.reject.invalid", "receive.duplicate", "receive.accepted",
                "receive.reject.session", "receive.verify.container.ok", "receive.save.ok", "receive.complete", "receive.stopped",
                "receive.reject.stopped", "receive.finalize.failed", "send.stopped" })
                Require(text.Contains("\"event\":\"" + name + "\""), "missing event " + name);
            Require(!text.Contains(Secret) && !text.Contains(session.Token) && !text.Contains(Convert.ToBase64String(payload)), "private data leaked");
            Require(text.Contains("DiagnosticsTests.cs:Main") && text.Contains("OutputLayout.CreateRunDirectory") && text.Contains("\"origin\""), "failure source site and product origin");
            foreach (var line in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)) JsonCodec.ParseObject(Encoding.UTF8.GetBytes(line));
            Require(Directory.GetFiles(logs, "trace-*.jsonl").Length <= 8, "retention file cap");
            Console.WriteLine("PASS real sender/receiver paths: success, duplicate, foreign, cancel, corrupt container, save failure, JSONL and privacy");
            // Hot-loop overhead and bounded queue, with no per-sample serialization or writes.
            var last = new { Sequence = 1 }; var clock = Stopwatch.StartNew();
            for (int i = 0; i < 100000; i++) Diagnostics.Sample("test.hotloop", "", 0, last);
            Console.WriteLine("100000 aggregate calls: " + clock.Elapsed.TotalMilliseconds.ToString("F2") + " ms");
            long allocated = GC.GetAllocatedBytesForCurrentThread(); clock.Restart();
            for (int i = 0; i < 100000; i++) Diagnostics.Sample("test.metadata", "sample-transfer", 0, new { Sequence = i, Width = 1280, Height = 720 });
            Console.WriteLine("100000 calls with fresh metadata: " + clock.Elapsed.TotalMilliseconds.ToString("F2") + " ms, "
                + (GC.GetAllocatedBytesForCurrentThread() - allocated) + " allocated bytes");
            Diagnostics.Flush(); Thread.Sleep(50);
            for (int i = 0; i < 1000; i++) Diagnostics.Event("test.burst", "", null);
            Diagnostics.Flush(); Diagnostics.Flush(); Thread.Sleep(50);
            Require(Read(logs).Contains("logging.loss"), "overflow must be visible");
            Console.WriteLine("PASS bounded logging overflow is reported");
            // Rotate actual 2-MiB parts, then verify the global file and byte bounds.
            var padding = new { Fixture = new string('x', 1600) };
            for (int round = 0; round < 100; round++) {
                for (int i = 0; i < 100; i++) Diagnostics.Event("test.rotation", "", padding);
                Diagnostics.Flush();
            }
            var files = new DirectoryInfo(logs).GetFiles("trace-*.jsonl");
            Require(files.Length <= 8 && files.Sum(f => f.Length) < 17 * 1024 * 1024, "rotation bounds");
            Console.WriteLine("PASS rotation: " + files.Length + " files, " + files.Sum(f => f.Length) + " bytes");
            return 0;
        }
        private static string Read(string directory) { return string.Join("\n", Directory.GetFiles(directory, "trace-*.jsonl").Select(File.ReadAllText)); }
        private static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        private static OpticalReceiveResult Finish(OpticalReceiveService receiver, string id, byte[] payload, ushort session)
        {
            for (uint n = 1; n < 4000; n++) { var result = receiver.AddFrame(id, Wire(payload, n, session), false); if (result.Complete) return result; }
            throw new Exception("did not complete");
        }
        private static byte[] Wire(byte[] payload, uint sequence, ushort session)
        {
            var encoder = new FountainEncoder(payload, 980, session);
            uint fnv = 2166136261; foreach (byte b in payload) fnv = unchecked((fnv ^ b) * 16777619);
            using (var memory = new MemoryStream()) {
                using (var writer = new BinaryWriter(memory, Encoding.UTF8, true)) {
                    writer.Write((byte)0xd1); writer.Write((byte)0x0c); writer.Write(session); writer.Write(sequence);
                    writer.Write((ushort)encoder.BlockCount); writer.Write((ushort)980); writer.Write((uint)payload.Length); writer.Write(fnv);
                    writer.Write(encoder.Encode(sequence));
                }
                return memory.ToArray();
            }
        }
    }
}
