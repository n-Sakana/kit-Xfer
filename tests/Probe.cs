using System;
using System.IO;

namespace Xfer
{
    internal static class Probe
    {
        private static void Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "Xfer-probe-" + Guid.NewGuid().ToString("N"));
            string source = Path.Combine(root, "src");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "sample.bin"), "hello");
            File.WriteAllText(Path.Combine(source, "note.txt"), "Xfer regression");
            File.WriteAllText(Path.Combine(root, "outside.bin"), "SECRET OUTSIDE");
            var snap = FolderCatalog.Inspect(source);

            Check("重複したファイル名", snap, new[] { "sample.bin", "sample.bin" });
            Check("親へ抜ける相対パス", snap, new[] { "../outside.bin" });
            Check("絶対パス", snap, new[] { Path.Combine(root, "outside.bin") });
            Check("深い親抜け", snap, new[] { "../../../../../../Windows/win.ini" });
        }

        private static void Check(string label, FolderSnapshot snap, string[] names)
        {
            try
            {
                var payload = OpticalPayload.Build(snap, names);
                Console.WriteLine("!! 通った        " + label + "  bytes=" + payload.Bytes.Length);
            }
            catch (Exception e)
            {
                Console.WriteLine("   弾いた " + e.GetType().Name + "   " + label);
            }
        }
    }
}
