using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
namespace Xfer
{
    internal static class SmallTransferBenchmark
    {
        public static int Main(string[] args)
        {
            string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            string input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
            var content = new byte[5976883]; new Random(20261024).NextBytes(content);
            File.WriteAllBytes(Path.Combine(input, "sample.bin"), content);
            try
            {
                for (int run = 0; run < 3; run++)
                {
                    var source = FolderCatalog.Inspect(input); var sender = new OpticalService();
                    var receiver = new OpticalReceiveService(Path.Combine(root, "output"));
                    var clock = Stopwatch.StartNew(); var start = sender.StartFx2(source, new[] { "sample.bin" }, 30, "L");
                    double preparation = clock.Elapsed.TotalSeconds;
                    var code = Fx2Code.ForLevel(0); var cells = new byte[Fx2.DataCells.Length];
                    OpticalReceiveResult result = null; uint seq;
                    for (seq = 0; seq < 200000 && (result == null || !result.Complete); seq++)
                    {
                        byte[] frame, decoded; int repaired;
                        if (!sender.TryFrame(start.Token, seq, out frame)) throw new Exception("missing sender");
                        code.Encode(frame, cells); if (!code.TryDecode(cells, out decoded, out repaired)) throw new Exception("decode failed");
                        result = receiver.AddFrame("bench", decoded, false);
                    }
                    double seconds = clock.Elapsed.TotalSeconds;
                    if (result == null || !result.Complete || !content.SequenceEqual(File.ReadAllBytes(Path.Combine(result.OutputPath, "sample.bin")))) throw new Exception("mismatch");
                    Console.WriteLine("MATCH run=" + run + " bytes=" + content.Length + " frames=" + seq + " prep_s=" + preparation.ToString("F4") + " total_s=" + seconds.ToString("F4") + " peak_RSS=" + Process.GetCurrentProcess().PeakWorkingSet64);
                    sender.Stop(start.Token); receiver.Stop("bench"); Directory.Delete(result.OutputPath, true); GC.Collect();
                }
                return 0;
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
