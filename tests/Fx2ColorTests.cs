using System;
using System.Linq;

namespace Xfer
{
    internal static class Fx2ColorTests
    {
        private static int Main()
        {
            var random = new Random(20261029);
            var layout = Fx2Layout.Create(900, 600, 3, 0);
            for (int level = 0; level < 4; level++)
            {
                var code = Fx2Code.ForLevel(level);
                var chroma = Fx2Code.ForColor(level);
                var tail = new byte[chroma.FrameBytes]; random.NextBytes(tail);
                var coded = new byte[Fx2.ColorCells.Length * 2]; chroma.Encode(tail, coded);
                for (int i = 0; i < chroma.Parity / 2; i++) coded[i * 8] ^= 1;
                byte[] repaired; int corrected;
                if (!chroma.TryDecode(coded, out repaired, out corrected) || corrected != chroma.Parity / 2 || !tail.SequenceEqual(repaired))
                    throw new Exception("Chroma correction limit failed");
                coded[chroma.Parity / 2 * 8] ^= 1;
                if (chroma.TryDecode(coded, out repaired, out corrected)) throw new Exception("Chroma accepted excess corruption");
                var payloads = new byte[layout.Patches * 2][];
                for (int i = 0; i < payloads.Length; i++)
                {
                    var frame = payloads[i] = new byte[code.FrameBytes + chroma.FrameBytes - 4];
                    random.NextBytes(frame); frame[0] = 0xd1; frame[1] = i % 2 == 0 ? (byte)0x0c : (byte)0x0d;
                }
                var first = new Fx2Picture(layout, code, 0, n => payloads[(int)n], true);
                var second = new Fx2Picture(layout, code, (ulong)layout.Patches, n => payloads[(int)n], true);
                var pixels = new byte[layout.Width * layout.Height * 4];
                new Fx2Image(first).CopyTo(pixels);
                // JPEG-style 2x2 chroma subsampling, preserving each pixel's luminance.
                for (int y = 0; y < layout.Height; y += 2)
                    for (int x = 0; x < layout.Width; x += 2)
                    {
                        int[] indices = { (y * layout.Width + x) * 4, (y * layout.Width + x + 1) * 4,
                            ((y + 1) * layout.Width + x) * 4, ((y + 1) * layout.Width + x + 1) * 4 };
                        double cb = 0, cr = 0;
                        foreach (int at in indices)
                        {
                            double luma = (29 * pixels[at] + 150 * pixels[at + 1] + 77 * pixels[at + 2]) / 256.0;
                            cb += pixels[at] - luma; cr += pixels[at + 2] - luma;
                        }
                        cb /= 4; cr /= 4;
                        foreach (int at in indices)
                        {
                            double luma = (29 * pixels[at] + 150 * pixels[at + 1] + 77 * pixels[at + 2]) / 256.0;
                            pixels[at] = (byte)Math.Clamp(luma + cb, 0, 255);
                            pixels[at + 2] = (byte)Math.Clamp(luma + cr, 0, 255);
                            pixels[at + 1] = (byte)Math.Clamp(luma - (29 * cb + 77 * cr) / 150, 0, 255);
                        }
                    }
                var gray = new byte[layout.Width * layout.Height];
                for (int i = 0; i < gray.Length; i++) gray[i] = (byte)((29 * pixels[i * 4] + 150 * pixels[i * 4 + 1] + 77 * pixels[i * 4 + 2]) >> 8);
                var reader = new Fx2Reader();
                var result = reader.Read(gray, layout.Width, layout.Height, pixels);
                if (result.Frames.Count != layout.Patches || result.Frames.Any(f => !payloads.Take(layout.Patches).Any(p => p.SequenceEqual(f))))
                    throw new Exception("Color round trip failed at " + level + ": " + result.Frames.Count + "/" + layout.Patches);
                if (reader.Read(gray, layout.Width, layout.Height).Frames.Count != 0) throw new Exception("Missing color accepted");
                // Whole, valid chroma codewords from the next picture must not be joined to this luminance frame.
                for (int t = 0; t < first.Cells.Length; t++)
                    for (int i = 0; i < first.Cells[t].Length; i++)
                        if (first.Cells[t][i] >= 2) second.Cells[t][i] = (byte)((second.Cells[t][i] & ~1) | (first.Cells[t][i] & 1));
                new Fx2Image(second).CopyTo(pixels);
                for (int i = 0; i < gray.Length; i++) gray[i] = (byte)((29 * pixels[i * 4] + 150 * pixels[i * 4 + 1] + 77 * pixels[i * 4 + 2]) >> 8);
                result = reader.Read(gray, layout.Width, layout.Height, pixels);
                if (result.Frames.Count != 0 || !result.Patches.Any(p => p.Failure == "color-mixed")) throw new Exception("Mixed pictures accepted");
                Console.WriteLine("PASS " + Fx2Code.LevelNames[level] + ": " + layout.Patches + " patches, subsampled color, correction limit, missing color, mixed pictures; " + code.FrameBytes + "+" + (chroma.FrameBytes - 4) + " bytes");
            }
            return 0;
        }
    }
}
