// Backend regression suite. No NuGet test runner required.
// dotnet run --project tests/Xfer.Regression.csproj -c Release
// Keep C# 5 syntax so the source also compiles with Windows PowerShell 5.1.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Xfer
{
    internal static class RegressionTests
    {
        private static string _root;
        private static FolderSnapshot _source;
        private static byte[] _sample;
        private static int _passed;

        public static int Main()
        {
            _root = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), "artifacts"), "Xfer-regression-" + Guid.NewGuid().ToString("N"));
            try
            {
                var input = Path.Combine(_root, "input");
                Directory.CreateDirectory(input);
                _sample = new byte[24000]; new Random(1776).NextBytes(_sample);
                File.WriteAllBytes(Path.Combine(input, "sample.bin"), _sample);
                File.WriteAllText(Path.Combine(input, "note.txt"), "Xfer regression 日本語\n");
                _source = FolderCatalog.Inspect(input);
                Run("CLI argument validation", TestOptions);
                Run("Fountain loss, reorder and duplicates", TestFountain);
                Run("packed QR pixels equal legacy RGBA including quiet zone", TestPacked);
                Run("independent display tokens and bounded sessions", TestSessions);
                Run("error-correction byte capacities", TestCapacities);
                Run("tiny transfer gets a smaller QR", TestTiny);
                Run("receiver completion, file integrity, pin and cancellation", TestReceiver);
                Run("receiver moves to a new sender only when the old one is gone", TestSwitch);
                Run("protected container rejects corruption", TestCorruption);
                Run("file selection rejects duplicates and traversal", TestSelection);
                Run("parallel output directories are unique", TestOutputDirectories);
                Console.WriteLine("PASS: " + _passed + " backend tests");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }
        }

        private static void Run(string name, Action action)
        { action(); _passed++; Console.WriteLine("PASS " + name); }
        private static void Require(bool condition, string message)
        { if (!condition) throw new Exception(message); }
        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }
        private static IList<string> Names() { return new[] { "sample.bin", "note.txt" }; }
        private static OpticalPayload Payload() { return OpticalPayload.Build(_source, Names()); }
        private static OpticalStartResult Start(OpticalService service)
        { return service.Start(_source, Names(), 1000, 30, "L"); }
        private static void TestOptions()
        {
            Require(AppOptions.Parse(new[] { "--no-browser", "--port", "18423" }).Port == 18423, "port");
            Require(AppOptions.Parse(new[] { "--help" }).ShowHelp, "help");
            Throws<ArgumentException>(delegate { AppOptions.Parse(new[] { "--port", "0" }); });
            Throws<ArgumentException>(delegate { AppOptions.Parse(new[] { "--cli" }); });
            Throws<ArgumentException>(delegate { AppOptions.Parse(new[] { "--mode", "bad" }); });
            Require(AppOptions.Parse(new string[0]).InitialMode == "optical", "default mode");
            Throws<ArgumentException>(delegate { AppOptions.Parse(new[] { "--mode", "vba" }); });
            Throws<ArgumentException>(delegate { AppOptions.Parse(new[] { "--mode", "markdown" }); });
        }
        private static void TestFountain()
        {
            var encoder = new FountainEncoder(_sample, 980, 1776);
            var decoder = new FountainDecoder(encoder.BlockCount, 980, 1776, _sample.Length);
            for (uint start = 0; start < 4000 && !decoder.IsComplete; start += 32)
                for (var index = 31; index >= 0 && !decoder.IsComplete; index--)
                {
                    var sequence = start + (uint)index;
                    if (sequence % 3 == 0) continue;
                    var block = encoder.Encode(sequence);
                    decoder.AddFrame(sequence, block); decoder.AddFrame(sequence, block);
                }
            Require(decoder.IsComplete && decoder.Assemble().SequenceEqual(_sample), "fountain payload mismatch");
            Require(decoder.FramesDuplicate > 0, "duplicates were not detected");
        }
        private static void TestPacked()
        {
            var service = new OpticalService(); var session = Start(service);
            foreach (var first in new[] { 0u, uint.MaxValue })
            {
                byte[] packed; Require(service.TryRenderPackedFrames(session.Token, first, 2, out packed), "packed missing");
                var side = 17 + 4 * session.QrVersion + 8; var stride = (side * side + 7) / 8;
                Require(packed.Length == 12 + stride * 2 && packed[0] == 0x46 && packed[3] == 0x31, "packed header/length");
                Require(BitConverter.ToUInt16(packed, 4) == side && BitConverter.ToUInt16(packed, 6) == 2 &&
                    BitConverter.ToUInt32(packed, 8) == first, "packed metadata");
                for (var frame = 0; frame < 2; frame++)
                {
                    byte[] rgba; Require(service.TryRenderRasterFrame(session.Token, unchecked(first + (uint)frame), out rgba), "raster missing");
                    for (var bit = 0; bit < side * side; bit++)
                    {
                        var dark = (packed[12 + stride * frame + bit / 8] & (0x80 >> (bit % 8))) != 0;
                        Require(rgba[bit * 4] == (dark ? 0 : 255) && rgba[bit * 4 + 3] == 255, "pixel mismatch");
                    }
                }
            }
            Throws<ArgumentException>(delegate { byte[] b; service.TryRenderPackedFrames(session.Token, 0, 0, out b); });
            Throws<ArgumentException>(delegate { byte[] b; service.TryRenderPackedFrames(session.Token, 0, 17, out b); });
        }
        private static void TestSessions()
        {
            var service = new OpticalService(); var sessions = new List<OpticalStartResult>();
            for (var n = 0; n < 4; n++) sessions.Add(Start(service));
            Throws<ArgumentException>(delegate { Start(service); });
            byte[] bytes; service.Stop(sessions[0].Token);
            Require(!service.TryRenderPackedFrames(sessions[0].Token, 0, 1, out bytes), "stopped token survived");
            foreach (var session in sessions.Skip(1))
                Require(service.TryRenderPackedFrames(session.Token, 0, 1, out bytes), "other display invalidated");
            Require(Start(service) != null, "released slot was not reused");
        }
        private static void TestCapacities()
        {
            var service = new OpticalService(); var levels = new[] { "L", "M", "Q", "H" }; var limits = new[] { 2953, 2331, 1663, 1273 };
            for (var i = 0; i < levels.Length; i++)
            {
                var session = service.Start(_source, Names(), 2953, 30, levels[i]);
                Require(session.FrameBytes == limits[i], "QR capacity mismatch: " + levels[i]);
                service.Stop(session.Token);
            }
        }
        private static void TestTiny()
        {
            var session = new OpticalService().Start(_source, new[] { "note.txt" }, 2953, 30, "L");
            Require(session.FrameBytes < 1000 && session.QrVersion < 40, "tiny transfer needlessly dense");
        }
        private static uint Fnv(byte[] bytes)
        { var hash = 0x811c9dc5u; foreach (var b in bytes) { hash ^= b; hash = unchecked(hash * 0x01000193u); } return hash; }
        private static byte[] Wire(byte[] payload, uint sequence, ushort sessionId)
        {
            var encoder = new FountainEncoder(payload, 980, sessionId); var block = encoder.Encode(sequence);
            using (var memory = new MemoryStream())
            {
                using (var writer = new BinaryWriter(memory))
                {
                    writer.Write((byte)0xd1); writer.Write((byte)0x0c); writer.Write(sessionId); writer.Write(sequence);
                    writer.Write((ushort)encoder.BlockCount); writer.Write((ushort)encoder.BlockLength);
                    writer.Write((uint)payload.Length); writer.Write(Fnv(payload)); writer.Write(block);
                }
                return memory.ToArray();
            }
        }
        private static void TestReceiver()
        {
            var payload = Payload().Bytes; var receiver = new OpticalReceiveService(Path.Combine(_root, "received"));
            receiver.Reset("first"); var first = receiver.AddFrame("first", Wire(payload, 0, 1776), false);
            Require(first.Recognized && !first.Complete, "first frame rejected");
            Require(!receiver.AddFrame("first", Wire(payload, 1, 1777), false).Recognized, "foreign stream replaced progress");
            OpticalReceiveResult complete = null;
            for (uint sequence = 1; sequence < 4000; sequence++)
            {
                if (sequence % 3 == 0) continue;
                var progress = receiver.AddFrame("first", Wire(payload, sequence, 1776), false);
                if (progress.Complete) { complete = progress; break; }
            }
            Require(complete != null && complete.FileCount == 2, "receiver did not finish");
            Require(File.ReadAllBytes(Path.Combine(complete.OutputPath, "sample.bin")).SequenceEqual(_sample), "saved bytes differ");
            var again = receiver.AddFrame("first", Wire(payload, 0, 1776), false);
            Require(again.Complete && again.OutputPath == complete.OutputPath, "completion retry created another output");
            receiver.Stop("first"); Require(!receiver.AddFrame("first", Wire(payload, 1, 1776), false).Recognized, "late POST resurrected stop");
            receiver.Reset("first"); Require(receiver.AddFrame("first", Wire(payload, 1, 1776), false).Recognized, "explicit restart rejected");
        }
        private static void TestSwitch()
        {
            var payload = Payload().Bytes; var receiver = new OpticalReceiveService(Path.Combine(_root, "switch"));
            var saved = OpticalReceiveService.SwitchAfter;
            OpticalReceiveService.SwitchAfter = TimeSpan.FromMilliseconds(300);
            try
            {
                // Both senders in view: the first transfer keeps the receiver however long it lasts.
                receiver.Reset("both");
                Require(receiver.AddFrame("both", Wire(payload, 0, 1776), false).Recognized, "first frame rejected");
                var until = DateTime.UtcNow.AddMilliseconds(800);
                for (uint sequence = 1; DateTime.UtcNow < until; sequence++)
                {
                    Require(!receiver.AddFrame("both", Wire(payload, sequence, 1777), false).Recognized, "switched while both were in view");
                    receiver.AddFrame("both", Wire(payload, sequence, 1776), false);
                    System.Threading.Thread.Sleep(20);
                }
                // The first sender is gone: after SwitchAfter of the other one alone, the receiver moves to it.
                receiver.Reset("gone");
                Require(receiver.AddFrame("gone", Wire(payload, 0, 1776), false).Recognized, "first frame rejected");
                OpticalReceiveResult moved = null;
                uint next = 1;
                for (until = DateTime.UtcNow.AddSeconds(3); DateTime.UtcNow < until && moved == null; next++)
                {
                    var result = receiver.AddFrame("gone", Wire(payload, next, 1777), false);
                    if (result.Recognized) moved = result;
                    System.Threading.Thread.Sleep(20);
                }
                Require(moved != null && moved.Switches == 1, "did not move to the new sender");
                OpticalReceiveResult complete = null;
                for (; next < 6000 && complete == null; next++)
                {
                    var result = receiver.AddFrame("gone", Wire(payload, next, 1777), false);
                    if (result.Complete) complete = result;
                }
                Require(complete != null && complete.Switches == 1, "the new transfer did not finish");
                Require(File.ReadAllBytes(Path.Combine(complete.OutputPath, "sample.bin")).SequenceEqual(_sample), "saved bytes differ after the switch");
            }
            finally { OpticalReceiveService.SwitchAfter = saved; }
        }
        private static void TestCorruption()
        {
            var payload = Payload().Bytes; payload[17] ^= 1;
            Throws<InvalidDataException>(delegate { OpticalPayloadReader.Save(payload, Path.Combine(_root, "corrupted")); });
        }
        private static void TestSelection()
        {
            // Resolve() looks names up in the snapshot taken from the folder, so anything
            // outside it simply is not there: the failure is FileNotFoundException, not
            // ArgumentException. Duplicates are folded by the seen set rather than rejected.
            // Verified on Windows 2026-09-08: "../outside.bin", an absolute path and a deep
            // "../../.." traversal are all rejected this way.
            Throws<FileNotFoundException>(delegate { OpticalPayload.Build(_source, new[] { "../outside.bin" }); });
            Throws<FileNotFoundException>(delegate { OpticalPayload.Build(_source, new[] { Path.Combine(_root, "outside.bin") }); });
            Throws<FileNotFoundException>(delegate { OpticalPayload.Build(_source, new[] { "../../../../../../Windows/win.ini" }); });
            Require(OpticalPayload.Build(_source, new[] { "sample.bin", "sample.bin" })
                .Bytes.Length == OpticalPayload.Build(_source, new[] { "sample.bin" }).Bytes.Length,
                "duplicate selection was not folded");
        }
        private static void TestOutputDirectories()
        {
            var directories = new string[32];
            Parallel.For(0, directories.Length, delegate(int i) { directories[i] = OutputLayout.CreateRunDirectory(Path.Combine(_root, "parallel"), "same"); });
            Require(directories.Distinct(StringComparer.Ordinal).Count() == directories.Length, "parallel output collision");
        }
    }
}
