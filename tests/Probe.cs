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

            Check("duplicate file name", snap, new[] { "sample.bin", "sample.bin" });
            Check("parent traversal", snap, new[] { "../outside.bin" });
            Check("absolute path", snap, new[] { Path.Combine(root, "outside.bin") });
            Check("nested parent traversal", snap, new[] { "../../../../../../Windows/win.ini" });
        }

        private static void Check(string label, FolderSnapshot snap, string[] names)
        {
            try
            {
                var payload = OpticalPayload.Build(snap, names);
                Console.WriteLine("Accepted: " + label + "  bytes=" + payload.Bytes.Length);
            }
            catch (Exception e)
            {
                Console.WriteLine("Rejected: " + e.GetType().Name + "   " + label);
            }
        }
    }
}
