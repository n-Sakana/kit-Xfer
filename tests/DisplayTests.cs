using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ZXing;

namespace Xfer;

// Tests the native image endpoint (not just Fx2Layout), the bundled renderer's
// limits, and file recovery from actual screenshots converted to 8-bit PGM.
internal static class DisplayTests
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "selftest") SelfTest(Path.GetFullPath(args[1]));
            else if (args.Length == 4 && args[0] == "decode") Decode(args[1], args[2], args[3]);
            else throw new ArgumentException("selftest WORK | decode PGM_DIR SOURCE_DIR OUTPUT_DIR");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void SelfTest(string work)
    {
        string input = Path.Combine(work, "input");
        Directory.CreateDirectory(input);
        var random = new Random(20261030);
        var bytes = new byte[24000]; random.NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(input, "sample.bin"), bytes);
        File.WriteAllText(Path.Combine(input, "日本語.txt"), "Xfer display round trip\n");
        foreach (bool customLimits in new[] { false, true })
        {
            using var app = new NativeApp();
            if (customLimits) app.SetImageLimits(8192, 128L * 1024 * 1024 / 4);
            long pixels = 128L * 1024 * 1024 / 4;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            Call(app, new { op = "init", args = new[] { "--home", work, "--path", input, "--port", port.ToString(), "--output", Path.Combine(work, "output") } });
            Call(app, new { op = "selectAll", on = true });
            var session = Start(app, "fx2");
            string token = (string)session["token"];
            foreach (var size in new[] { (1440, 2560), (3072, 1728), (1920, 1080), (3840, 2160), (6144, 3456),
                (7680, 4320), (8192, 2048), (8192, 4096), (8192, 4320), (16384, 2160), (16384, 16384) })
            {
                int cell = size.Item1 >= 3840 ? 6 : 3;
                var layout = Call(app, new { op = "send.fx2layout", width = size.Item1, height = size.Item2, cell, coarse = 0 });
                int w = Convert.ToInt32(layout["width"]), h = Convert.ToInt32(layout["height"]);
                Require(w <= 8192 && h <= 8192 && (long)w * h <= pixels, "layout exceeds host budget");
                if (size.Item1 <= 8192 && size.Item2 <= 8192 && (long)size.Item1 * size.Item2 <= pixels)
                    Require(w == size.Item1 && h == size.Item2, "supported screen size changed");
                var image = app.Image($"fx2?t={token}&s=0&w={w}&h={h}&m={cell}&k=0");
                Require(image != null && image.Width == w && image.Height == h, "advertised image missing");
                // Exercise the full BGRA allocation and the app bitmap constructor
                // on the failing screen and on both exact host-budget boundaries.
                if (size == (6144, 3456) || size == (8192, 4096))
                {
                    var bgra = new byte[w * h * 4]; image!.CopyTo(bgra);
                    var rendered = new NativeBitmap(w, h, bgra);
                    Require(rendered.Width == w && bgra[3] == 255, "invalid renderer image");
                    Recover(new[] { (ToGray(bgra), w, h) }, input, Path.Combine(work, $"roundtrip-{customLimits}-{w}x{h}"));
                }
                Console.WriteLine($"PASS {(customLimits ? "Explicit host budget" : "Bundled Core default")} {size.Item1}x{size.Item2} -> {w}x{h}, cell={cell}, patches={layout["patches"]}");
            }
            int boundaryHeight = (int)(pixels / 8192);
            Require(app.Image($"fx2?t={token}&s=0&w=8192&h={boundaryHeight + 1}&m=6") == null, "pixel budget overrun accepted");
            Require(app.Image($"fx2?t={token}&s=0&w=8193&h=1000&m=6") == null, "side budget overrun accepted");
            Call(app, new { op = "send.stop" });
            session = Start(app, "qr"); token = (string)session["token"];
            var qr = app.Image($"qr?t={token}&s=0&m=3&c=2&r=1");
            Require(qr != null && qr.Width > qr.Height, "QR grid changed");
            Console.WriteLine("PASS QR grid after Tile stop");
            Call(app, new { op = "send.stop" });
        }
    }

    private static Dictionary<string, object> Start(NativeApp app, string layout)
    {
        var job = Call(app, new { op = "send.start", layout, fps = 15, ec = "L", color = false, frameBytes = 1000, columns = 2, rows = 1 });
        for (int n = 0; n < 500; n++)
        {
            var reply = Call(app, new { op = "job", id = job["job"] });
            if (reply.TryGetValue("result", out var result)) return (Dictionary<string, object>)result;
            Thread.Sleep(10);
        }
        throw new TimeoutException("send.start");
    }

    private static Dictionary<string, object> Call(NativeApp app, object request)
    {
        var reply = JsonCodec.ParseObject(Encoding.UTF8.GetBytes(app.Call(Encoding.UTF8.GetString(JsonCodec.Serialize(request)))));
        if (reply.TryGetValue("error", out var error)) throw new Exception(Convert.ToString(error));
        return reply;
    }

    private static byte[] ToGray(byte[] bgra)
    {
        var gray = new byte[bgra.Length / 4];
        for (int i = 0; i < gray.Length; i++) gray[i] = (byte)((bgra[i * 4] + 2 * bgra[i * 4 + 1] + bgra[i * 4 + 2]) / 4);
        return gray;
    }

    private static void Decode(string captures, string source, string output)
    {
        var pictures = Directory.GetFiles(captures, "*.pgm").OrderBy(p => p).Select(path =>
        {
            using var file = File.OpenRead(path);
            string Line() { var b = new List<byte>(); int c; while ((c = file.ReadByte()) != -1 && c != '\n') b.Add((byte)c); return Encoding.ASCII.GetString(b.ToArray()); }
            Require(Line() == "P5", "not an 8-bit PGM");
            var size = Line().Split(' '); int w = int.Parse(size[0]), h = int.Parse(size[1]);
            Require(Line() == "255", "not an 8-bit PGM");
            var gray = new byte[w * h]; file.ReadExactly(gray);
            return (gray, w, h);
        });
        Recover(pictures, source, output);
    }

    private static void Recover(IEnumerable<(byte[] Gray, int Width, int Height)> pictures, string source, string output)
    {
        using var receiver = new OpticalReceiveService(output);
        var reader = new Fx2Reader();
        OpticalReceiveResult? completed = null;
        foreach (var picture in pictures)
        {
            var decoded = reader.Read(picture.Gray, picture.Width, picture.Height);
            var frames = decoded.Frames;
            if (frames.Count == 0)
            {
                var qr = new BarcodeReaderGeneric { AutoRotate = true, Options = new ZXing.Common.DecodingOptions { PossibleFormats = new[] { BarcodeFormat.QR_CODE } } };
                var found = qr.DecodeMultiple(new RGBLuminanceSource(picture.Gray, picture.Width, picture.Height, RGBLuminanceSource.BitmapFormat.Gray8));
                if (found != null) foreach (var result in found)
                    if (result.ResultMetadata != null && result.ResultMetadata.TryGetValue(ResultMetadataType.BYTE_SEGMENTS, out var segments))
                        foreach (var frame in (IEnumerable<byte[]>)segments) frames.Add(frame);
            }
            Console.WriteLine($"DECODE {picture.Width}x{picture.Height}: {frames.Count} frames, {decoded.Markers.Count} markers");
            foreach (var frame in frames)
            {
                var result = receiver.AddFrame("display-test", frame, false);
                if (result.Complete) { completed = result; break; }
            }
            if (completed != null) break;
        }
        Require(completed != null, "screenshots did not reconstruct the transfer");
        foreach (string original in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, original), restored = Path.Combine(completed!.OutputPath, relative);
            string expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)));
            Require(File.Exists(restored) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(restored))) == expected, "restored bytes differ: " + relative);
            Console.WriteLine($"PASS SHA256 {relative}: {expected}");
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
