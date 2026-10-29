using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Xfer
{
    /// <summary>FX2 bench without a camera. selftest: codes and layouts. sim: a synthetic camera (perspective, lens
    /// distortion, blur, noise, exposure across display switches with both rolling shutters, partial view) reads a
    /// random payload through the product's FX2 reader and fountain decoder. pgm: the reader over saved camera
    /// frames (8-bit PGM). render: one FX2 picture as PGM.</summary>
    internal static class Fx2Lab
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0) { Console.WriteLine("Fx2Lab selftest | sim [options] | pgm DIR [options] | render [options] OUT.pgm"); return 2; }
                var options = Options.Parse(args.Skip(1).ToArray());
                Fx2Reader.GuardRadius = options.Number("guard-radius", Fx2Reader.GuardRadius); Fx2Reader.GuardDark = options.Int("guard-dark", 2); Fx2Reader.GuardDiagonal = options.Number("guard-diagonal", Fx2Reader.GuardDiagonal);
                switch (args[0])
                {
                    case "selftest": return SelfTest();
                    case "sim": return Simulate(options);
                    case "pgm": return ReadSaved(options);
                    case "render": return RenderOne(options);
                    default: Console.WriteLine("unknown command"); return 2;
                }
            }
            catch (Exception error) { Console.WriteLine(error); return 1; }
        }

        // ------------------------------------------------------------------ options

        internal sealed class Options
        {
            public readonly List<string> Positional = new List<string>();
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public static Options Parse(string[] args)
            {
                var o = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i].StartsWith("--"))
                    {
                        string key = args[i].Substring(2), value = "1";
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) value = args[++i];
                        o.Values[key] = value;
                    }
                    else o.Positional.Add(args[i]);
                }
                return o;
            }
            public double Number(string key, double fallback) { string v; return Values.TryGetValue(key, out v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback; }
            public int Int(string key, int fallback) { return (int)Number(key, fallback); }
            public string Text(string key, string fallback) { string v; return Values.TryGetValue(key, out v) ? v : fallback; }
            public bool Flag(string key) { return Values.ContainsKey(key); }
            public int[] Pair(string key, int a, int b)
            {
                string v; if (!Values.TryGetValue(key, out v)) return new[] { a, b };
                var parts = v.Split('x', ',');
                return new[] { int.Parse(parts[0]), int.Parse(parts[1]) };
            }
        }

        // ------------------------------------------------------------------ selftest

        private static int SelfTest()
        {
            int failures = 0;
            // Golay: minimum distance 8, every pattern of up to 3 wrong bits corrected.
            int minimum = 24;
            for (int info = 1; info < 4096; info++) minimum = Math.Min(minimum, Fx2.Bits(Fx2.GolayEncode(info)));
            Check(ref failures, minimum == 8, "Golay minimum weight " + minimum);
            var random = new Random(1);
            for (int n = 0; n < 20000; n++)
            {
                int info = random.Next(4096), word = Fx2.GolayEncode(info), flips = random.Next(4);
                for (int k = 0; k < flips; k++) word ^= 1 << random.Next(24);
                int decoded, errors;
                if (!Fx2.GolayDecode(word, out decoded, out errors) || decoded != info) { Check(ref failures, false, "Golay correction"); break; }
            }
            // Ring transforms: a marker read through the right transform gives its ID.
            for (int t = 0; t < 8; t++)
            {
                int ring = Fx2.MarkerRing(7, 3, 1, 2);
                // Picture basis seen through transform t: picture offset p shows screen offset T(p).
                int picture = 0;
                for (int k = 0; k < 24; k++)
                {
                    int sx = Fx2.TA[t] * Fx2.RingX[k] + Fx2.TB[t] * Fx2.RingY[k], sy = Fx2.TC[t] * Fx2.RingX[k] + Fx2.TD[t] * Fx2.RingY[k];
                    if (((ring >> (23 - Fx2.RingIndex(sx, sy))) & 1) != 0) picture |= 1 << (23 - k);
                }
                int c, r, tier, level, errors;
                Check(ref failures, Fx2.TryReadRing(picture, t, out c, out r, out tier, out level, out errors) && c == 7 && r == 3 && tier == 1 && level == 2 && errors == 0, "ring transform " + t);
            }
            // Patch codec: round trip, correction up to the limit, rejection past it.
            for (int level = 0; level < 4; level++)
            {
                var code = Fx2Code.ForLevel(level);
                var frame = new byte[code.FrameBytes];
                random.NextBytes(frame);
                var cells = new byte[Fx2.DataCells.Length];
                code.Encode(frame, cells);
                byte[] back; int corrected;
                Check(ref failures, code.TryDecode(cells, out back, out corrected) && back.SequenceEqual(frame) && corrected == 0, "patch round trip " + level);
                // The table encoder must keep the existing optical wire format, including both interleaved
                // codewords of unequal length. Compare randomized payloads against ZXing's polynomial encoder.
                bool identical = true;
                var reference = new ZXing.Common.ReedSolomon.ReedSolomonEncoder(ZXing.Common.ReedSolomon.GenericGF.QR_CODE_FIELD_256);
                for (int trial = 0; trial < 100; trial++)
                {
                    var input = new byte[code.FrameBytes]; random.NextBytes(input);
                    var encoded = new byte[Fx2.DataCells.Length]; code.Encode(input, encoded);
                    var patch = new byte[Fx2.PatchBytes]; Buffer.BlockCopy(input, 0, patch, 0, input.Length);
                    uint crc = Fx2.Crc32(input, 0, input.Length);
                    for (int b = 0; b < 4; b++) patch[input.Length + b] = (byte)(crc >> (24 - b * 8));
                    for (int k = 0; k < Fx2Code.Codewords; k++)
                    {
                        int length = (Fx2.PatchBytes - k + Fx2Code.Codewords - 1) / Fx2Code.Codewords;
                        var word = new int[length];
                        for (int i = 0; i < length - code.Parity; i++) word[i] = patch[i * Fx2Code.Codewords + k];
                        reference.encode(word, code.Parity);
                        for (int i = length - code.Parity; i < length; i++) patch[i * Fx2Code.Codewords + k] = (byte)word[i];
                    }
                    for (int bit = 0; bit < encoded.Length; bit++)
                    {
                        int index = bit >> 3, value = (index < patch.Length ? patch[index] : 0) ^ Fx2.Whiten(index);
                        if (encoded[bit] != ((value >> (7 - (bit & 7))) & 1)) identical = false;
                    }
                }
                Check(ref failures, identical, "reference parity, 100 payloads, level " + level);
                var badCrc = (byte[])cells.Clone(); badCrc[code.FrameBytes * 8] ^= 1;
                Check(ref failures, code.TryDecode(badCrc, out back, out corrected) && back.SequenceEqual(frame) && corrected > 0, "CRC damage takes correction path " + level);
                // Wrong bytes: the codeword with the most damage decides.
                var damaged = (byte[])cells.Clone();
                int wrong = code.Parity / 2 * Fx2Code.Codewords - 2;
                for (int b = 0; b < wrong; b++) damaged[(b * 7 % Fx2.PatchBytes) * 8 + b % 8] ^= 1;
                Check(ref failures, code.TryDecode(damaged, out back, out corrected) && back.SequenceEqual(frame), "patch correction " + level + " (" + corrected + ")");
                var ruined = (byte[])cells.Clone();
                for (int b = 0; b < ruined.Length; b += 3) ruined[b] ^= 1;
                Check(ref failures, !code.TryDecode(ruined, out back, out corrected), "patch rejection " + level);
                Console.WriteLine("level " + Fx2Code.LevelNames[level] + ": patch " + Fx2.PatchBytes + " B, frame " + code.FrameBytes + " B, block " + (code.FrameBytes - 20) + " B");
            }
            // Layouts.
            foreach (var size in new[] { new[] { 3840, 2040, 6, 0 }, new[] { 3840, 2040, 6, 2 }, new[] { 1920, 1020, 3, 2 }, new[] { 1280, 700, 2, 3 } })
            {
                var layout = Fx2Layout.Create(size[0], size[1], size[2], size[3]);
                Console.WriteLine(size[0] + "x" + size[1] + " cell " + size[2] + " coarse " + size[3] + ": " + layout.Patches + " patches, " +
                    string.Join(", ", layout.Tiers.Select(t => "tier " + t.Tier + " " + t.Columns + "x" + t.Rows + " (" + t.Patches + ") at " + t.Left + "," + t.Top + " " + t.Width + "x" + t.Height)));
                var picture = new Fx2Picture(layout, Fx2Code.ForLevel(1), 0, s => new byte[Fx2Code.ForLevel(1).FrameBytes]);
                var reader = new Fx2Reader();
                var result = reader.Read(picture.Render(), layout.Width, layout.Height);
                Check(ref failures, result.Frames.Count == layout.Patches, "clean picture " + result.Frames.Count + "/" + layout.Patches + " patches, " + result.Markers.Count + " markers, " + result.Ms.ToString("0.0") + " ms");
                var changed = new Fx2Picture(layout, Fx2Code.ForLevel(1), 0, s => {
                    var bytes = new byte[Fx2Code.ForLevel(1).FrameBytes]; bytes[bytes.Length - 1] = 123; return bytes;
                }).Render();
                var tracked = reader.Read(changed, layout.Width, layout.Height);
                Check(ref failures, tracked.Tracked && tracked.Frames.Count == layout.Patches && tracked.Frames.All(f => f[f.Length - 1] == 123), "tracked geometry reads fresh payloads");
                var moved = Enumerable.Repeat((byte)255, changed.Length).ToArray();
                for (int y = 0; y < layout.Height; y++) Buffer.BlockCopy(changed, y * layout.Width, moved, y * layout.Width + 17, layout.Width - 17);
                var reacquired = reader.Read(moved, layout.Width, layout.Height);
                Check(ref failures, !reacquired.Tracked && reacquired.Frames.Count > 0 && reacquired.Frames.All(f => f[f.Length - 1] == 123), "movement reacquires geometry");
                var blank = reader.Read(Enumerable.Repeat((byte)255, changed.Length).ToArray(), layout.Width, layout.Height);
                Check(ref failures, !blank.Tracked && blank.Frames.Count == 0, "removed screen never replays tracked data");
            }
            // A QR code is not FX2: its finder and alignment patterns may read as markers, but no patch forms.
            {
                var qr = ZXing.QrCode.Internal.Encoder.encode(new string('x', 900), ZXing.QrCode.Internal.ErrorCorrectionLevel.L, null);
                int side = qr.Matrix.Width + 8;
                foreach (int scale in new[] { 3, 5, 8 })
                {
                    int width = side * scale + 200, height = side * scale + 100;
                    var gray = Enumerable.Repeat((byte)255, width * height).ToArray();
                    for (int y = 0; y < qr.Matrix.Height; y++)
                        for (int x = 0; x < qr.Matrix.Width; x++)
                            if (qr.Matrix[x, y] != 0)
                                for (int dy = 0; dy < scale; dy++)
                                    for (int dx = 0; dx < scale; dx++) gray[(50 + (y + 4) * scale + dy) * width + 100 + (x + 4) * scale + dx] = 0;
                    var read = new Fx2Reader().Read(gray, width, height);
                    Check(ref failures, read.Patches.Count < 2 && read.Frames.Count == 0, "a QR code (" + scale + " px per module) is not taken for FX2: " + read.Markers.Count + " markers, " + read.Patches.Count + " patches");
                }
            }
            Console.WriteLine(failures == 0 ? "PASS" : "FAIL " + failures);
            return failures == 0 ? 0 : 1;
        }

        private static void Check(ref int failures, bool ok, string what)
        {
            if (!ok) failures++;
            Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        }

        // ------------------------------------------------------------------ frames

        /// <summary>Fountain frames of a random payload, as OpticalSession builds them.</summary>
        internal sealed class FrameSource
        {
            public readonly byte[] Payload;
            public readonly int FrameBytes;
            private readonly FountainEncoder _encoder;
            private readonly ushort _session = 0x2f31;
            private readonly uint _fnv;
            public FrameSource(int payloadBytes, int frameBytes, int seed)
            {
                Payload = new byte[payloadBytes];
                new Random(seed).NextBytes(Payload);
                FrameBytes = frameBytes;
                _encoder = new FountainEncoder(Payload, frameBytes - 20, _session);
                uint h = 0x811c9dc5u; foreach (var b in Payload) { h ^= b; h = unchecked(h * 0x01000193u); }
                _fnv = h;
            }
            public int Blocks { get { return _encoder.BlockCount; } }
            public byte[] Frame(uint sequence)
            {
                var block = _encoder.Encode(sequence);
                var frame = new byte[20 + block.Length];
                frame[0] = 0xd1; frame[1] = 0x0c;
                Put(frame, 2, _session, 2); Put(frame, 4, sequence, 4); Put(frame, 8, (uint)_encoder.BlockCount & 0xFFFF, 2);
                Put(frame, 10, (uint)_encoder.BlockLength, 2); Put(frame, 12, (uint)Payload.Length, 4); Put(frame, 16, _fnv, 4);
                Buffer.BlockCopy(block, 0, frame, 20, block.Length);
                return frame;
            }
            private static void Put(byte[] target, int offset, uint value, int bytes) { for (int i = 0; i < bytes; i++) target[offset + i] = (byte)(value >> (8 * i)); }
        }

        // ------------------------------------------------------------------ synthetic camera

        internal sealed class Camera
        {
            public int Width = 1920, Height = 1080;
            /// <summary>Camera pixels per screen pixel at the centre of the picture.</summary>
            public double Scale = 1.26;
            /// <summary>Screen point at the camera's centre (screen pixels).</summary>
            public double CenterX, CenterY;
            public double RotateDegrees, TiltX, TiltY, K1, Blur, Noise, Black = 22, White = 228, Gamma = 1;
            /// <summary>Timing: camera frame period, sensor readout, exposure, display frame period and scan-out (s).</summary>
            public double Period = 1 / 30.0, Readout = 1 / 32.0, Exposure = 1 / 120.0, DisplayPeriod = 1 / 15.0, ScanOut = 1 / 60.0;
            public int Supersample = 2;
            private double _cos = 1, _sin = 0;

            /// <summary>Screen position of camera pixel (x, y).</summary>
            public void Map(double x, double y, out double sx, out double sy)
            {
                double cx = Width / 2.0, cy = Height / 2.0, f = Width * 0.7;
                double nx = (x - cx) / f, ny = (y - cy) / f, r2 = nx * nx + ny * ny;
                // Pincushion/barrel: the lens moves a point at radius r to r (1 + K1 r^2); invert approximately.
                double k = 1 - K1 * r2;
                nx *= k; ny *= k;
                double u = nx * f / Scale, v = ny * f / Scale;
                double ru = _cos * u - _sin * v, rv = _sin * u + _cos * v;
                double w = 1 + TiltX * ru / 1000 + TiltY * rv / 1000;
                sx = CenterX + ru / w; sy = CenterY + rv / w;
            }

            public byte[] Capture(Func<int, byte[]> picture, int screenWidth, int screenHeight, double start, Random random)
            {
                var image = new double[Width * Height];
                int ss = Supersample;
                _cos = Math.Cos(RotateDegrees * Math.PI / 180); _sin = Math.Sin(RotateDegrees * Math.PI / 180);
                // Pictures needed by this capture, made before the parallel loop.
                for (int f = (int)Math.Floor((start - ScanOut) / DisplayPeriod) - 1; f <= (int)Math.Floor((start + Readout + Exposure) / DisplayPeriod) + 1; f++) picture(f);
                System.Threading.Tasks.Parallel.For(0, Height, y =>
                {
                    double rowStart = start + Readout * y / Height;
                    for (int x = 0; x < Width; x++)
                    {
                        double sum = 0;
                        for (int j = 0; j < ss; j++)
                            for (int i = 0; i < ss; i++)
                            {
                                double sx, sy;
                                Map(x + (i + 0.5) / ss - 0.5, y + (j + 0.5) / ss - 0.5, out sx, out sy);
                                int px = (int)Math.Floor(sx), py = (int)Math.Floor(sy);
                                double value;
                                if (px < 0 || py < 0 || px >= screenWidth || py >= screenHeight) value = 0.08;
                                else
                                {
                                    // The display switches row py at frame * period + scan-out share.
                                    double shift = ScanOut * py / screenHeight, local = rowStart - shift;
                                    int frame = (int)Math.Floor(local / DisplayPeriod);
                                    double first = Math.Min(1, Math.Max(0, ((frame + 1) * DisplayPeriod - local) / Exposure));
                                    double a0 = picture(frame)[py * screenWidth + px] / 255.0;
                                    double a1 = first < 1 ? picture(frame + 1)[py * screenWidth + px] / 255.0 : 0;
                                    double level = first * a0 + (1 - first) * a1;
                                    value = (Black + (White - Black) * (Gamma == 1 ? level : Math.Pow(level, Gamma))) / 255.0;
                                }
                                sum += value;
                            }
                        image[y * Width + x] = sum / (ss * ss) * 255;
                    }
                });
                if (Blur > 0) GaussianBlur(image, Width, Height, Blur);
                var gray = new byte[Width * Height];
                for (int i = 0; i < gray.Length; i++)
                {
                    double v = image[i];
                    if (Noise > 0) v += Noise * Gaussian(random);
                    gray[i] = (byte)Math.Max(0, Math.Min(255, Math.Round(v)));
                }
                return gray;
            }
        }

        private static double Gaussian(Random random)
        {
            double u1 = 1 - random.NextDouble(), u2 = random.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        private static void GaussianBlur(double[] image, int width, int height, double sigma)
        {
            int radius = (int)Math.Ceiling(sigma * 3);
            var kernel = new double[2 * radius + 1];
            double total = 0;
            for (int i = -radius; i <= radius; i++) total += kernel[i + radius] = Math.Exp(-i * i / (2 * sigma * sigma));
            for (int i = 0; i < kernel.Length; i++) kernel[i] /= total;
            var temp = new double[image.Length];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    double s = 0;
                    for (int k = -radius; k <= radius; k++) s += kernel[k + radius] * image[y * width + Math.Max(0, Math.Min(width - 1, x + k))];
                    temp[y * width + x] = s;
                }
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    double s = 0;
                    for (int k = -radius; k <= radius; k++) s += kernel[k + radius] * temp[Math.Max(0, Math.Min(height - 1, y + k)) * width + x];
                    image[y * width + x] = s;
                }
        }

        // ------------------------------------------------------------------ sim

        private static int Simulate(Options o)
        {
            var screen = o.Pair("screen", 1920, 1080);
            int cell = o.Int("cell", 3), coarse = o.Int("coarse", 0), level = Fx2Code.ParseLevel(o.Text("level", "M"));
            var code = Fx2Code.ForLevel(level);
            var layout = Fx2Layout.Create(screen[0], screen[1], cell, coarse);
            var camera = new Camera
            {
                Scale = o.Number("scale", 1.26), RotateDegrees = o.Number("rotate", 0), TiltX = o.Number("tilt-x", 0), TiltY = o.Number("tilt-y", 0),
                K1 = o.Number("k1", 0), Blur = o.Number("blur", 0), Noise = o.Number("noise", 0), Gamma = o.Number("gamma", 1),
                Exposure = 1 / o.Number("exposure", 120), DisplayPeriod = 1 / o.Number("fps", 15), Supersample = o.Int("supersample", 2)
            };
            var cam = o.Pair("camera", 1920, 1080);
            camera.Width = cam[0]; camera.Height = cam[1];
            camera.CenterX = o.Number("center-x", screen[0] / 2.0); camera.CenterY = o.Number("center-y", screen[1] / 2.0);
            var source = new FrameSource(o.Int("bytes", 200000), code.FrameBytes, o.Int("seed", 7));
            int captures = o.Int("frames", 120);
            string outDir = o.Text("out", null);
            if (outDir != null) Directory.CreateDirectory(outDir);
            Console.WriteLine("layout " + layout.Width + "x" + layout.Height + " cell " + cell + " coarse " + coarse + ": " + layout.Patches + " patches/picture, frame " + code.FrameBytes + " B, payload " + source.Payload.Length + " B = " + source.Blocks + " blocks");
            var pictures = new Dictionary<int, byte[]>();
            Func<int, byte[]> picture = f =>
            {
                byte[] p;
                if (f < 0) f = 0;
                lock (pictures)
                {
                    if (!pictures.TryGetValue(f, out p))
                    {
                        if (pictures.Count > 8) pictures.Remove(pictures.Keys.Min());
                        p = pictures[f] = new Fx2Picture(layout, code, (uint)(f * layout.Patches), s => source.Frame(unchecked((uint)s))).Render();
                    }
                }
                return p;
            };
            var decoder = new FountainDecoder(source.Blocks, source.FrameBytes - 20, 0x2f31, source.Payload.Length);
            var reader = new Fx2Reader();
            var random = new Random(o.Int("seed", 7));
            double phase = random.NextDouble() * camera.Period;
            int totalDecoded = 0, totalPatches = 0, sumMarkers = 0; double readMs = 0;
            var failures = new Dictionary<string, int>();
            for (int n = 0; n < captures; n++)
            {
                double start = n * camera.Period + phase;
                var gray = camera.Capture(picture, layout.Width, layout.Height, start, random);
                if (outDir != null && n < o.Int("save", 4)) WritePgm(Path.Combine(outDir, "sim-" + n.ToString("D4") + ".pgm"), gray, camera.Width, camera.Height);
                var result = reader.Read(gray, camera.Width, camera.Height);
                readMs += result.Ms; sumMarkers += result.Markers.Count; totalPatches += result.Patches.Count; totalDecoded += result.Frames.Count;
                foreach (var p in result.Patches) if (!p.Decoded) { int c; failures.TryGetValue(p.Failure ?? "?", out c); failures[p.Failure ?? "?"] = c + 1; }
                foreach (var bytes in result.Frames)
                {
                    OpticalFrame frame;
                    if (!OpticalFrame.TryParse(bytes, out frame)) { Console.WriteLine("frame did not parse"); return 1; }
                    decoder.AddFrame(frame.Sequence, frame.Block);
                }
                if (o.Flag("verbose")) Console.WriteLine("capture " + n + ": candidates " + result.Candidates + ", markers " + result.Markers.Count + ", patches " + result.Frames.Count + "/" + result.Patches.Count + ", corrected " + result.Corrected + ", " + result.Ms.ToString("0.0") + " ms, solved " + decoder.SolvedCount + "/" + source.Blocks);
                if (decoder.IsComplete)
                {
                    bool same = decoder.Assemble().SequenceEqual(source.Payload);
                    double seconds = (n + 1) * camera.Period;
                    Console.WriteLine("COMPLETE after " + (n + 1) + " captures (" + seconds.ToString("0.00") + " s): " + (same ? "payload matches" : "PAYLOAD DIFFERS") +
                        ", " + (source.Payload.Length / 1024.0 / seconds).ToString("0.0") + " KiB/s, new " + decoder.FramesNew + " dup " + decoder.FramesDuplicate);
                    Summary(n + 1, totalDecoded, totalPatches, sumMarkers, readMs, failures);
                    return same ? 0 : 1;
                }
            }
            Console.WriteLine("INCOMPLETE: solved " + decoder.SolvedCount + "/" + source.Blocks + ", new " + decoder.FramesNew);
            Summary(captures, totalDecoded, totalPatches, sumMarkers, readMs, failures);
            return 3;
        }

        private static void Summary(int captures, int decoded, int patches, int markers, double ms, Dictionary<string, int> failures)
        {
            Console.WriteLine("per capture: markers " + (markers / (double)captures).ToString("0.0") + ", patches decoded " + (decoded / (double)captures).ToString("0.0") + " of " + (patches / (double)captures).ToString("0.0") +
                ", read " + (ms / captures).ToString("0.0") + " ms; failures " + string.Join(" ", failures.Select(f => f.Key + "=" + f.Value)));
        }

        // ------------------------------------------------------------------ saved frames

        private static int ReadSaved(Options o)
        {
            string dir = o.Positional.Count > 0 ? o.Positional[0] : ".";
            var files = Directory.GetFiles(dir, "*.pgm").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            var reader = new Fx2Reader();
            var sequences = new HashSet<uint>();
            var cellSizes = new List<double>();
            int decoded = 0, patches = 0;
            string outDir = o.Text("out", null);
            if (outDir != null) Directory.CreateDirectory(outDir);
            foreach (var file in files)
            {
                int width, height;
                var gray = ReadPgm(file, out width, out height);
                var result = reader.Read(gray, width, height);
                int fresh = 0;
                foreach (var p in result.Patches) if (p.Decoded && sequences.Add(p.Sequence)) fresh++;
                decoded += result.Frames.Count; patches += result.Patches.Count;
                foreach (var m in result.Markers) if (m.Errors == 0 && m.Tier == 0) cellSizes.Add((Math.Sqrt(m.Ux * m.Ux + m.Uy * m.Uy) + Math.Sqrt(m.Vx * m.Vx + m.Vy * m.Vy)) / 2);
                var errors = result.Markers.GroupBy(m => m.Errors).OrderBy(g => g.Key).Select(g => g.Key + ":" + g.Count());
                var fails = result.Patches.Where(p => !p.Decoded).GroupBy(p => p.Failure).Select(g => g.Key + "=" + g.Count());
                var tiers = result.Patches.GroupBy(p => p.Tier).OrderBy(g => g.Key).Select(g => "t" + g.Key + " " + g.Count(p => p.Decoded) + "/" + g.Count());
                int plain = result.Patches.Count(p => p.Decoded && p.Sharpening == 0);
                Console.WriteLine(Path.GetFileName(file) + ": candidates " + result.Candidates + ", markers " + result.Markers.Count + " (errors " + string.Join(" ", errors) + ", transform " + result.Transform +
                    "), patches " + result.Frames.Count + "/" + result.Patches.Count + " (" + string.Join(", ", tiers) + ") plain " + plain + " new " + fresh + ", corrected " + result.Corrected + " [" + string.Join(" ", fails) + "], " + result.Ms.ToString("0.0") + " ms (" +
                    result.ThresholdMs.ToString("0.0") + " " + result.BlobMs.ToString("0.0") + " " + result.IdMs.ToString("0.0") + " " + result.PatchMs.ToString("0.0") + ")");
                if (o.Values.ContainsKey("probe"))
                {
                    var at = o.Pair("probe", 0, 0);
                    foreach (var m in result.CandidateList.Where(m => Math.Abs(m.X - at[0]) < 60 && Math.Abs(m.Y - at[1]) < 60))
                        Console.WriteLine("  candidate " + m.X.ToString("0.0") + "," + m.Y.ToString("0.0") + " area " + m.Area + " box " + m.BoxWidth + "x" + m.BoxHeight +
                            " best " + (m.Best == int.MaxValue ? "-" : m.Best.ToString()) + " dark " + m.Dark.ToString("0") + " light " + m.Light.ToString("0") + " u " + m.Ux.ToString("0.00") + "," + m.Uy.ToString("0.00") + " v " + m.Vx.ToString("0.00") + "," + m.Vy.ToString("0.00"));
                }
                if (outDir != null) WriteOverlay(Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ".ppm"), gray, width, height, result);
            }
            cellSizes.Sort();
            Console.WriteLine("cell " + (cellSizes.Count > 0 ? cellSizes[cellSizes.Count / 2].ToString("0.00") : "-") + " camera px (fine tier, median over exact markers)");
            Console.WriteLine("files " + files.Length + ", patches decoded " + decoded + " of " + patches + ", distinct sequences " + sequences.Count);
            return 0;
        }

        // ------------------------------------------------------------------ render

        private static int RenderOne(Options o)
        {
            var screen = o.Pair("screen", 1920, 1080);
            var layout = Fx2Layout.Create(screen[0], screen[1], o.Int("cell", 3), o.Int("coarse", 0));
            var code = Fx2Code.ForLevel(Fx2Code.ParseLevel(o.Text("level", "M")));
            var source = new FrameSource(o.Int("bytes", 200000), code.FrameBytes, 7);
            var picture = new Fx2Picture(layout, code, 0, s => source.Frame(unchecked((uint)s)));
            WritePgm(o.Positional[0], picture.Render(), layout.Width, layout.Height);
            Console.WriteLine(layout.Width + "x" + layout.Height + ", " + layout.Patches + " patches");
            return 0;
        }

        // ------------------------------------------------------------------ files

        internal static byte[] ReadPgm(string path, out int width, out int height)
        {
            var bytes = File.ReadAllBytes(path);
            int position = 0;
            Func<string> token = () =>
            {
                while (position < bytes.Length)
                {
                    if (bytes[position] == '#') { while (position < bytes.Length && bytes[position] != '\n') position++; }
                    else if (char.IsWhiteSpace((char)bytes[position])) position++;
                    else break;
                }
                int start = position;
                while (position < bytes.Length && !char.IsWhiteSpace((char)bytes[position])) position++;
                return Encoding.ASCII.GetString(bytes, start, position - start);
            };
            if (token() != "P5") throw new InvalidDataException("not a binary PGM: " + path);
            width = int.Parse(token()); height = int.Parse(token());
            int max = int.Parse(token());
            position++;
            if (max != 255) throw new InvalidDataException("16-bit PGM not supported");
            var gray = new byte[width * height];
            Buffer.BlockCopy(bytes, position, gray, 0, gray.Length);
            return gray;
        }

        internal static void WritePgm(string path, byte[] gray, int width, int height)
        {
            using (var stream = File.Create(path))
            {
                var header = Encoding.ASCII.GetBytes("P5\n" + width + " " + height + "\n255\n");
                stream.Write(header, 0, header.Length);
                stream.Write(gray, 0, width * height);
            }
        }

        /// <summary>The picture with markers (green: ID read, yellow: candidate) and patch outlines (blue: decoded,
        /// red: failed).</summary>
        private static void WriteOverlay(string path, byte[] gray, int width, int height, Fx2Reader.Result result)
        {
            var rgb = new byte[width * height * 3];
            for (int i = 0; i < gray.Length; i++) rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = (byte)(gray[i] / 2 + 64);
            Action<double, double, byte, byte, byte> dot = (x, y, r, g, b) =>
            {
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int px = (int)x + dx, py = (int)y + dy;
                        if (px < 0 || py < 0 || px >= width || py >= height) continue;
                        int i = (py * width + px) * 3; rgb[i] = r; rgb[i + 1] = g; rgb[i + 2] = b;
                    }
            };
            Action<double, double, double, double, byte, byte, byte> line = (x0, y0, x1, y1, r, g, b) =>
            {
                int steps = (int)Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) + 1;
                for (int s = 0; s <= steps; s++)
                {
                    int px = (int)(x0 + (x1 - x0) * s / steps), py = (int)(y0 + (y1 - y0) * s / steps);
                    if (px < 0 || py < 0 || px >= width || py >= height) continue;
                    int i = (py * width + px) * 3; rgb[i] = r; rgb[i + 1] = g; rgb[i + 2] = b;
                }
            };
            foreach (var p in result.Patches)
            {
                var c = p.Corners;
                byte r = p.Decoded ? (byte)40 : (byte)255, g = p.Decoded ? (byte)120 : (byte)40, b = p.Decoded ? (byte)255 : (byte)40;
                line(c[0], c[1], c[2], c[3], r, g, b); line(c[2], c[3], c[6], c[7], r, g, b); line(c[6], c[7], c[4], c[5], r, g, b); line(c[4], c[5], c[0], c[1], r, g, b);
            }
            foreach (var m in result.Markers) dot(m.X, m.Y, 0, 255, 0);
            using (var stream = File.Create(path))
            {
                var header = Encoding.ASCII.GetBytes("P6\n" + width + " " + height + "\n255\n");
                stream.Write(header, 0, header.Length);
                stream.Write(rgb, 0, rgb.Length);
            }
        }
    }
}
