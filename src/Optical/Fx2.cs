using System;
using System.Collections.Generic;
using ZXing.Common.ReedSolomon;

namespace Xfer
{
    /// <summary>FX2, the full-screen optical code. The picture is a grid of square cells, dark or light. Markers sit
    /// on a square lattice every Pitch cells: a solid 3 x 3 core, a light guard ring, and around that a ring of 24
    /// cells carrying the marker's ID (lattice column and row modulo 32 and 16, tier, error-correction level) as an
    /// extended Golay codeword. The square between four neighbouring markers is a patch: one fountain frame and its
    /// CRC-32, protected by Reed-Solomon, whitened, one bit per cell. Optional chroma adds two bits per 2x2 cells,
    /// with its own RS/CRC and a checksum binding both layers to the same fountain frame. Patches are independent: the receiver gains
    /// from every patch it sees whole and sharp, whatever part of the screen is in view and wherever a rolling
    /// shutter cut the picture. A layout may hold several tiers (lattices of different cell sizes, for example a
    /// coarse centre for a distant or blurred camera); the tier is in the marker ID, the cell size is not, so the
    /// receiver needs no setting for it.</summary>
    internal static class Fx2
    {
        /// <summary>Cells between neighbouring marker centres; a patch is Pitch x Pitch cells.</summary>
        public const int Pitch = 48;
        /// <summary>Distance in cells from a marker centre to its ID ring (core 0..1, guard 2, ID 3).</summary>
        public const int Reach = 3;
        /// <summary>Light cells between the outermost ID rings and the edge of a tier.</summary>
        public const int Margin = Reach + 1;
        public const int IdColumns = 32, IdRows = 16, Tiers = 2;
        /// <summary>Patch-local cells (y * Pitch + x, marker centre of the top-left corner at 0,0) in bit order.</summary>
        public static readonly int[] DataCells = BuildDataCells();
        public static readonly int PatchBytes = DataCells.Length / 8;
        // Two chroma bits per 2x2 luminance cells. Keep every marker and its guard achromatic.
        public static readonly int[] ColorCells = BuildColorCells();
        public static readonly uint[] Palette = BuildPalette();
        public const byte ColorTag = 0xd2;

        private static int[] BuildColorCells()
        {
            var cells = new List<int>();
            for (int y = 0; y < Pitch; y += 2)
                for (int x = 0; x < Pitch; x += 2)
                    if (!InMarker(x, y) && !InMarker(x + 1, y) && !InMarker(x, y + 1) && !InMarker(x + 1, y + 1))
                        cells.Add(y * Pitch + x);
            return cells.ToArray();
        }

        private static uint[] BuildPalette()
        {
            var colors = new uint[10];
            colors[0] = 0xffffffff; colors[1] = 0xff000000;
            for (int symbol = 0; symbol < 4; symbol++)
                for (int dark = 0; dark < 2; dark++)
                {
                    int y = dark == 1 ? 48 : 207, v = (symbol & 2) != 0 ? 1 : -1, u = (symbol & 1) != 0 ? 1 : -1;
                    colors[2 + symbol * 2 + dark] = 0xff000000u | (uint)(y + 36 * v) << 16 | (uint)(y - 18 * v - 10 * u) << 8 | (uint)(y + 46 * u);
                }
            return colors;
        }
        /// <summary>ID ring offsets from the marker centre, clockwise from the top-left corner.</summary>
        public static readonly int[] RingX, RingY;
        /// <summary>The eight ways the screen's axes can lie on the picture's (rotations, mirror images): screen
        /// offset = (A x + B y, C x + D y) for picture-basis offset (x, y).</summary>
        public static readonly int[] TA = { 1, 0, -1, 0, -1, 1, 0, 0 }, TB = { 0, -1, 0, 1, 0, 0, 1, -1 },
            TC = { 0, 1, 0, -1, 0, 0, 1, -1 }, TD = { 1, 0, -1, 0, 1, -1, 0, 0 };
        /// <summary>[transform][screen ring index] = picture ring index.</summary>
        private static readonly int[][] RingFromPicture;
        private static readonly byte[] Whitener;
        private const int RingMask = 0x9A3D56;
        private const int GolayPolynomial = 0xC75; // x^11 + x^10 + x^6 + x^5 + x^4 + x^2 + 1
        private static readonly int[] GolayErrors = BuildGolayTable();

        static Fx2()
        {
            RingX = new int[24]; RingY = new int[24];
            int n = 0;
            for (int x = -Reach; x <= Reach; x++) { RingX[n] = x; RingY[n] = -Reach; n++; }
            for (int y = -Reach + 1; y <= Reach; y++) { RingX[n] = Reach; RingY[n] = y; n++; }
            for (int x = Reach - 1; x >= -Reach; x--) { RingX[n] = x; RingY[n] = Reach; n++; }
            for (int y = Reach - 1; y > -Reach; y--) { RingX[n] = -Reach; RingY[n] = y; n++; }
            RingFromPicture = new int[8][];
            for (int t = 0; t < 8; t++)
            {
                RingFromPicture[t] = new int[24];
                for (int k = 0; k < 24; k++)
                {
                    // Orthogonal transform: the inverse is the transpose.
                    int px = TA[t] * RingX[k] + TC[t] * RingY[k], py = TB[t] * RingX[k] + TD[t] * RingY[k];
                    RingFromPicture[t][k] = RingIndex(px, py);
                }
            }
            Whitener = new byte[PatchBytes + 1];
            uint state = 0x46583221;
            for (int i = 0; i < Whitener.Length; i++)
            {
                state = unchecked(state * 1664525u + 1013904223u);
                Whitener[i] = (byte)(state >> 24);
            }
        }

        private static int[] BuildDataCells()
        {
            var cells = new List<int>(Pitch * Pitch);
            for (int y = 0; y < Pitch; y++)
                for (int x = 0; x < Pitch; x++)
                    if (!InMarker(x, y)) cells.Add(y * Pitch + x);
            return cells.ToArray();
        }

        /// <summary>Whether a patch-local cell belongs to one of the patch's four corner markers.</summary>
        public static bool InMarker(int x, int y)
        {
            bool left = x <= Reach, right = x >= Pitch - Reach, top = y <= Reach, bottom = y >= Pitch - Reach;
            return (left || right) && (top || bottom);
        }

        public static int RingIndex(int x, int y)
        {
            for (int k = 0; k < 24; k++) if (RingX[k] == x && RingY[k] == y) return k;
            throw new ArgumentException("Not an ID ring cell");
        }

        /// <summary>Marker cell value (true = dark) at offset dx, dy (|dx|, |dy| at most Reach) for this ID.</summary>
        public static bool MarkerCell(int ring, int dx, int dy)
        {
            int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (d <= 1) return true;
            if (d == 2) return false;
            return ((ring >> (23 - RingIndex(dx, dy))) & 1) != 0;
        }

        /// <summary>The 24 ring bits (bit 23 = ring index 0) of a marker.</summary>
        public static int MarkerRing(int column, int row, int tier, int level)
        {
            int info = (column & (IdColumns - 1)) | (row & (IdRows - 1)) << 5 | (tier & 1) << 9 | (level & 3) << 10;
            return GolayEncode(info) ^ RingMask;
        }

        /// <summary>Reads a ring sampled in the picture's basis (bit 23 = picture ring index 0) as seen through
        /// transform t: the ID fields and the number of corrected bits, or false.</summary>
        public static bool TryReadRing(int pictureBits, int t, out int column, out int row, out int tier, out int level, out int errors)
        {
            int screen = 0;
            var map = RingFromPicture[t];
            for (int k = 0; k < 24; k++) screen |= ((pictureBits >> (23 - map[k])) & 1) << (23 - k);
            column = row = tier = level = 0;
            int info;
            if (!GolayDecode(screen ^ RingMask, out info, out errors)) return false;
            column = info & 31; row = info >> 5 & 15; tier = info >> 9 & 1; level = info >> 10 & 3;
            return true;
        }

        public static int GolayEncode(int info)
        {
            int word = (info & 0xFFF) << 11;
            word |= Remainder(word);
            return word << 1 | Parity(word);
        }

        public static bool GolayDecode(int word, out int info, out int errors)
        {
            int error = GolayErrors[Syndrome(word)];
            info = errors = 0;
            if (error < 0) return false;
            info = (word ^ error) >> 12 & 0xFFF;
            errors = Bits(error);
            return true;
        }

        private static int Syndrome(int word) { return Remainder(word >> 1 & 0x7FFFFF) | Parity(word & 0xFFFFFF) << 11; }

        private static int Remainder(int word)
        {
            for (int bit = 22; bit >= 11; bit--) if ((word >> bit & 1) != 0) word ^= GolayPolynomial << (bit - 11);
            return word & 0x7FF;
        }

        private static int Parity(int word) { return Bits(word) & 1; }

        public static int Bits(int value)
        {
            int n = 0;
            for (uint v = (uint)value; v != 0; v &= v - 1) n++;
            return n;
        }

        /// <summary>Syndrome to error pattern for up to three wrong bits; -1 for four (detected, not corrected).</summary>
        private static int[] BuildGolayTable()
        {
            var table = new int[4096];
            for (int i = 0; i < table.Length; i++) table[i] = -1;
            table[0] = 0;
            for (int a = 0; a < 24; a++)
            {
                Note(table, 1 << a);
                for (int b = a + 1; b < 24; b++)
                {
                    Note(table, 1 << a | 1 << b);
                    for (int c = b + 1; c < 24; c++) Note(table, 1 << a | 1 << b | 1 << c);
                }
            }
            return table;
        }

        private static void Note(int[] table, int error)
        {
            int s = Syndrome(error);
            if (table[s] < 0) table[s] = error;
        }

        public static byte Whiten(int index) { return Whitener[index]; }

        public static uint Crc32(byte[] bytes, int offset, int count)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++) crc = CrcTable[(crc ^ bytes[i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();
        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }

    /// <summary>The patch contents for one error-correction level: the fountain frame, a CRC-32, Reed-Solomon
    /// parity over two interleaved codewords (patch byte b belongs to codeword b % 2), whitened.</summary>
    internal sealed class Fx2Code
    {
        public const int Codewords = 2;
        public static readonly string[] LevelNames = { "L", "M", "Q", "H" };
        /// <summary>Parity bytes per codeword; each codeword corrects half as many wrong bytes.</summary>
        private static readonly int[] LevelParity = { 16, 24, 32, 48 };
        private static readonly GenericGF Field = GenericGF.QR_CODE_FIELD_256;
        private static readonly Fx2Code[] Levels = { new Fx2Code(0), new Fx2Code(1), new Fx2Code(2), new Fx2Code(3) };
        private static readonly Fx2Code[] ColorLevels = { new Fx2Code(0, true), new Fx2Code(1, true), new Fx2Code(2, true), new Fx2Code(3, true) };
        private readonly byte[] _parityProducts;
        private readonly int _patchBytes, _bits, _codewords;

        public readonly int Level, Parity, DataBytes, FrameBytes;

        private Fx2Code(int level, bool color = false)
        {
            // Chroma is more blurred by MJPEG; always use 48 parity bytes (24 correctable bytes).
            Level = level; Parity = color ? 48 : LevelParity[level];
            _bits = color ? Fx2.ColorCells.Length * 2 : Fx2.DataCells.Length;
            _patchBytes = _bits / 8; _codewords = color ? 1 : Codewords;
            DataBytes = _patchBytes - _codewords * Parity;
            FrameBytes = DataBytes - 4;
            // Remainders of each possible leading byte times x^Parity. Reuse the reference encoder's
            // generator for a byte-for-byte identical shift register, without polynomial allocations per patch.
            _parityProducts = new byte[256 * Parity];
            var encoder = new ReedSolomonEncoder(Field);
            for (int value = 1; value < 256; value++)
            {
                var word = new int[Parity + 1];
                word[0] = value;
                encoder.encode(word, Parity);
                for (int i = 0; i < Parity; i++) _parityProducts[value * Parity + i] = (byte)word[i + 1];
            }
        }

        public static Fx2Code ForLevel(int level) { return Levels[level]; }
        public static Fx2Code ForColor(int level) { return ColorLevels[level]; }

        public static int ParseLevel(string name)
        {
            int level = Array.IndexOf(LevelNames, name);
            if (level < 0) throw new ArgumentException("Invalid error correction level.");
            return level;
        }

        private int CodewordLength(int k) { return (_patchBytes - k + _codewords - 1) / _codewords; }

        /// <summary>Writes the patch's cells (one byte per data cell, 1 = dark) for a frame of FrameBytes.</summary>
        public void Encode(byte[] frame, byte[] cells)
        {
            if (frame == null || frame.Length != FrameBytes) throw new ArgumentException("A Tile patch carries " + FrameBytes + " frame bytes.");
            var patch = new byte[_patchBytes];
            Buffer.BlockCopy(frame, 0, patch, 0, FrameBytes);
            uint crc = Fx2.Crc32(frame, 0, FrameBytes);
            patch[FrameBytes] = (byte)(crc >> 24); patch[FrameBytes + 1] = (byte)(crc >> 16);
            patch[FrameBytes + 2] = (byte)(crc >> 8); patch[FrameBytes + 3] = (byte)crc;
            for (int k = 0; k < _codewords; k++)
            {
                int length = CodewordLength(k);
                int start = (length - Parity) * _codewords + k;
                for (int i = 0; i < length - Parity; i++)
                {
                    int product = (patch[i * _codewords + k] ^ patch[start]) * Parity;
                    for (int j = 0; j < Parity - 1; j++)
                        patch[start + j * _codewords] = (byte)(patch[start + (j + 1) * _codewords] ^ _parityProducts[product + j]);
                    patch[start + (Parity - 1) * _codewords] = _parityProducts[product + Parity - 1];
                }
            }
            int bits = _bits;
            for (int i = 0; i < bits; i++)
            {
                int index = i >> 3;
                int value = index < _patchBytes ? patch[index] ^ Fx2.Whiten(index) : Fx2.Whiten(index);
                cells[i] = (byte)((value >> (7 - (i & 7))) & 1);
            }
        }

        /// <summary>The frame from a patch read off the picture (one byte per data cell, 1 = dark), or false when
        /// Reed-Solomon cannot correct it or the CRC does not match. corrected counts the repaired bytes.</summary>
        public bool TryDecode(byte[] cells, out byte[] frame, out int corrected)
        {
            frame = null; corrected = 0;
            var patch = new byte[_patchBytes];
            for (int i = 0; i < _patchBytes * 8; i++) if (cells[i] != 0) patch[i >> 3] |= (byte)(0x80 >> (i & 7));
            for (int i = 0; i < patch.Length; i++) patch[i] ^= Fx2.Whiten(i);
            // Skip Reed-Solomon syndrome calculation for a CRC-valid payload. Damaged payloads require
            // correction and a matching CRC before they reach the fountain decoder.
            if (!CrcMatches(patch))
            {
                var decoder = new ReedSolomonDecoder(Field);
                for (int k = 0; k < _codewords; k++)
                {
                    int length = CodewordLength(k);
                    var codeword = new int[length];
                    for (int i = 0; i < length; i++) codeword[i] = patch[i * _codewords + k];
                    int errors;
                    if (!decoder.decodeWithECCount(codeword, Parity, out errors)) return false;
                    corrected += errors;
                    for (int i = 0; i < length - Parity; i++) patch[i * _codewords + k] = (byte)codeword[i];
                }
                if (!CrcMatches(patch)) return false;
            }
            frame = new byte[FrameBytes];
            Buffer.BlockCopy(patch, 0, frame, 0, FrameBytes);
            return true;
        }

        private bool CrcMatches(byte[] patch)
        {
            uint crc = Fx2.Crc32(patch, 0, FrameBytes);
            return patch[FrameBytes] == (byte)(crc >> 24) && patch[FrameBytes + 1] == (byte)(crc >> 16)
                && patch[FrameBytes + 2] == (byte)(crc >> 8) && patch[FrameBytes + 3] == (byte)crc;
        }
    }

    /// <summary>One lattice of the picture: cells of CellPixels pixels from (Left, Top), Columns x Rows patches; the
    /// marker of lattice point (i, j) is centred on cell (Margin + i * Pitch, Margin + j * Pitch). Used says which
    /// patches are drawn (a coarse tier cuts a hole in the fine one).</summary>
    internal sealed class Fx2Tier
    {
        public int Tier, CellPixels, Left, Top, Columns, Rows;
        public bool[] Used;
        public int CellsWide { get { return Columns * Fx2.Pitch + 2 * Fx2.Margin + 1; } }
        public int CellsHigh { get { return Rows * Fx2.Pitch + 2 * Fx2.Margin + 1; } }
        public int Width { get { return CellsWide * CellPixels; } }
        public int Height { get { return CellsHigh * CellPixels; } }
        public int Patches { get { int n = 0; foreach (var used in Used) if (used) n++; return n; } }

        public static Fx2Tier Fit(int tier, int width, int height, int cellPixels)
        {
            int columns = (width / cellPixels - 2 * Fx2.Margin - 1) / Fx2.Pitch, rows = (height / cellPixels - 2 * Fx2.Margin - 1) / Fx2.Pitch;
            return Make(tier, Math.Min(columns, 255), Math.Min(rows, 255), cellPixels, width, height);
        }

        public static Fx2Tier Make(int tier, int columns, int rows, int cellPixels, int width, int height)
        {
            if (columns < 1 || rows < 1) return null;
            var result = new Fx2Tier { Tier = tier, CellPixels = cellPixels, Columns = columns, Rows = rows, Used = new bool[columns * rows] };
            for (int i = 0; i < result.Used.Length; i++) result.Used[i] = true;
            result.Left = (width - result.Width) / 2; result.Top = (height - result.Height) / 2;
            return result;
        }

        /// <summary>Whether marker (i, j) is a corner of a drawn patch.</summary>
        public bool MarkerUsed(int i, int j)
        {
            for (int dj = -1; dj <= 0; dj++)
                for (int di = -1; di <= 0; di++)
                {
                    int c = i + di, r = j + dj;
                    if (c >= 0 && r >= 0 && c < Columns && r < Rows && Used[r * Columns + c]) return true;
                }
            return false;
        }
    }

    /// <summary>Where the patches of one picture are: a fine tier filling the picture, and optionally a coarse tier
    /// (Coarse times larger cells) in the middle, cut out of the fine one. Patch numbers run over the coarse tier
    /// first, then the fine tier, row by row.</summary>
    internal sealed class Fx2Layout
    {
        public const int MinimumCell = 1, MaximumCell = 32;
        public readonly int Width, Height, CellPixels, Coarse;
        public readonly Fx2Tier[] Tiers;
        public readonly int Patches;

        private Fx2Layout(int width, int height, int cellPixels, int coarse, Fx2Tier[] tiers)
        {
            Width = width; Height = height; CellPixels = cellPixels; Coarse = coarse; Tiers = tiers;
            foreach (var tier in tiers) Patches += tier.Patches;
        }

        /// <summary>The layout for a picture of width x height pixels, or null when not one patch fits. coarse is
        /// 0 for one tier, else the cell size factor of the middle tier.</summary>
        public static Fx2Layout Create(int width, int height, int cellPixels, int coarse)
        {
            if (width < 1 || height < 1 || cellPixels < MinimumCell || cellPixels > MaximumCell) throw new ArgumentException("Invalid Tile dimensions.");
            var fine = Fx2Tier.Fit(0, width, height, cellPixels);
            if (fine == null) return null;
            if (coarse < 2) return new Fx2Layout(width, height, cellPixels, 0, new[] { fine });
            // The middle tier: at most 2 x 2 patches, within half the width and two thirds of the height. A
            // camera near the screen sees the middle, so the middle tier takes no more of it than needed to keep
            // a far or blurred camera going.
            int big = cellPixels * coarse;
            var middle = Fx2Tier.Fit(1, width / 2, height * 2 / 3, big);
            if (middle == null) return new Fx2Layout(width, height, cellPixels, 0, new[] { fine });
            if (middle.Columns > 2 || middle.Rows > 2) middle = Fx2Tier.Make(1, Math.Min(2, middle.Columns), Math.Min(2, middle.Rows), big, width, height);
            // It sits in a hole of whole fine patches, in the middle of the fine lattice: the fewest columns and
            // rows that hold it clear of the fine corner markers reaching into the hole (and one quiet cell).
            int pitch = Fx2.Pitch * cellPixels, clear = 2 * (Fx2.Reach + 1) * cellPixels;
            int columns = Math.Min(fine.Columns, (middle.Width + clear + pitch - 1) / pitch), rows = Math.Min(fine.Rows, (middle.Height + clear + pitch - 1) / pitch);
            int c0 = (fine.Columns - columns) / 2, r0 = (fine.Rows - rows) / 2;
            int holeLeft = fine.Left + (Fx2.Margin + c0 * Fx2.Pitch) * cellPixels, holeTop = fine.Top + (Fx2.Margin + r0 * Fx2.Pitch) * cellPixels;
            middle.Left = holeLeft + (columns * pitch - middle.Width) / 2;
            middle.Top = holeTop + (rows * pitch - middle.Height) / 2;
            for (int r = r0; r < r0 + rows; r++)
                for (int c = c0; c < c0 + columns; c++) fine.Used[r * fine.Columns + c] = false;
            return new Fx2Layout(width, height, cellPixels, coarse, new[] { middle, fine });
        }
    }

    /// <summary>The cells of one FX2 picture, one byte per cell (1 = dark) for each tier of the layout. Patch k of
    /// the picture carries the fountain frame for sequence first + k.</summary>
    internal sealed class Fx2Picture
    {
        public readonly Fx2Layout Layout;
        public readonly byte[][] Cells;

        public Fx2Picture(Fx2Layout layout, Fx2Code code, ulong first, Func<ulong, byte[]> frame, bool color = false)
        {
            Layout = layout;
            Cells = new byte[layout.Tiers.Length][];
            var bits = new byte[Fx2.DataCells.Length];
            var colorCode = Fx2Code.ForColor(code.Level);
            var colorBits = color ? new byte[Fx2.ColorCells.Length * 2] : null;
            ulong sequence = first;
            for (int t = 0; t < layout.Tiers.Length; t++)
            {
                var tier = layout.Tiers[t];
                int wide = tier.CellsWide;
                var cells = Cells[t] = new byte[wide * tier.CellsHigh];
                for (int j = 0; j <= tier.Rows; j++)
                    for (int i = 0; i <= tier.Columns; i++)
                    {
                        if (!tier.MarkerUsed(i, j)) continue;
                        int ring = Fx2.MarkerRing(i, j, tier.Tier, code.Level);
                        int cx = Fx2.Margin + i * Fx2.Pitch, cy = Fx2.Margin + j * Fx2.Pitch;
                        for (int dy = -Fx2.Reach; dy <= Fx2.Reach; dy++)
                            for (int dx = -Fx2.Reach; dx <= Fx2.Reach; dx++)
                                cells[(cy + dy) * wide + cx + dx] = Fx2.MarkerCell(ring, dx, dy) ? (byte)1 : (byte)0;
                    }
                for (int r = 0; r < tier.Rows; r++)
                    for (int c = 0; c < tier.Columns; c++)
                    {
                        if (!tier.Used[r * tier.Columns + c]) continue;
                        var payload = frame(sequence);
                        if (color)
                        {
                            var luma = new byte[code.FrameBytes];
                            Buffer.BlockCopy(payload, 0, luma, 0, luma.Length);
                            luma[0] = Fx2.ColorTag;
                            code.Encode(luma, bits);
                            var chroma = new byte[colorCode.FrameBytes];
                            uint checksum = Fx2.Crc32(payload, 0, payload.Length);
                            chroma[0] = (byte)(checksum >> 24); chroma[1] = (byte)(checksum >> 16);
                            chroma[2] = (byte)(checksum >> 8); chroma[3] = (byte)checksum;
                            Buffer.BlockCopy(payload, luma.Length, chroma, 4, chroma.Length - 4);
                            colorCode.Encode(chroma, colorBits);
                        }
                        else code.Encode(payload, bits);
                        sequence = unchecked(sequence + 1);
                        int left = Fx2.Margin + c * Fx2.Pitch, top = Fx2.Margin + r * Fx2.Pitch;
                        var order = Fx2.DataCells;
                        for (int b = 0; b < order.Length; b++)
                            cells[(top + order[b] / Fx2.Pitch) * wide + left + order[b] % Fx2.Pitch] = bits[b];
                        if (color)
                            for (int i = 0; i < Fx2.ColorCells.Length; i++)
                            {
                                int at = Fx2.ColorCells[i], symbol = colorBits[i * 2] * 2 + colorBits[i * 2 + 1];
                                for (int dy = 0; dy < 2; dy++)
                                    for (int dx = 0; dx < 2; dx++)
                                    {
                                        int index = (top + at / Fx2.Pitch + dy) * wide + left + at % Fx2.Pitch + dx;
                                        cells[index] += (byte)(2 + symbol * 2);
                                    }
                            }
                    }
            }
        }

        /// <summary>Luminance pixels of the whole picture, for tests and tools.</summary>
        public byte[] Render()
        {
            var pixels = new byte[Layout.Width * Layout.Height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;
            // The fine tier (last) first: the middle tier is drawn over its hole.
            for (int t = Layout.Tiers.Length - 1; t >= 0; t--)
            {
                var tier = Layout.Tiers[t];
                int s = tier.CellPixels, wide = tier.CellsWide;
                for (int y = 0; y < tier.Height; y++)
                {
                    int py = tier.Top + y;
                    if (py < 0 || py >= Layout.Height) continue;
                    int row = (y / s) * wide;
                    for (int x = 0; x < tier.Width; x++)
                    {
                        int px = tier.Left + x;
                        if (px < 0 || px >= Layout.Width) continue;
                        uint color = Fx2.Palette[Cells[t][row + x / s]];
                        pixels[py * Layout.Width + px] = (byte)(((color & 255) * 29 + ((color >> 8) & 255) * 150 + ((color >> 16) & 255) * 77) >> 8);
                    }
                }
            }
            return pixels;
        }
    }
}
