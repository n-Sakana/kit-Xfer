using System;

namespace Xfer
{
    /// <summary>An app: image for the page: its size and its pixels as BGRA, premultiplied alpha, top-down rows.</summary>
    internal interface IAppImage
    {
        int Width { get; }
        int Height { get; }
        /// <summary>Write Width * Height * 4 bytes.</summary>
        void CopyTo(Span<byte> destination);
    }

    /// <summary>Pixels kept for the page (the camera preview): BGRA, opaque.</summary>
    internal sealed class NativeBitmap : IAppImage
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public readonly byte[] Pixels;

        public NativeBitmap(int width, int height, byte[] pixels)
        {
            if (width <= 0 || height <= 0 || pixels == null || pixels.Length < width * height * 4) throw new ArgumentException("Invalid bitmap");
            Width = width; Height = height; Pixels = pixels;
        }

        public void CopyTo(Span<byte> destination) { Pixels.AsSpan(0, Width * Height * 4).CopyTo(destination); }

        /// <summary>Box-filtered downscale of opaque BGRA to at most maxWidth (aspect kept); the source when already small.</summary>
        public static NativeBitmap Downscale(byte[] bgra, int width, int height, int maxWidth)
        {
            if (width <= maxWidth) { var copy = new byte[width * height * 4]; Buffer.BlockCopy(bgra, 0, copy, 0, copy.Length); return new NativeBitmap(width, height, copy); }
            int factor = (width + maxWidth - 1) / maxWidth;
            int w = width / factor, h = height / factor;
            var pixels = new byte[w * h * 4];
            // Full HD preview: average four pixels at once, keeping the blue/red sums in separate
            // 16-bit lanes. This is the same box filter as below without the per-channel inner loops.
            if (factor == 2)
            {
                var source = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bgra.AsSpan());
                var target = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int at = y * 2 * width + x * 2;
                        uint a = source[at], b = source[at + 1], c = source[at + width], d = source[at + width + 1];
                        uint br = (a & 0x00FF00FF) + (b & 0x00FF00FF) + (c & 0x00FF00FF) + (d & 0x00FF00FF);
                        uint green = (a & 0x0000FF00) + (b & 0x0000FF00) + (c & 0x0000FF00) + (d & 0x0000FF00);
                        target[y * w + x] = 0xFF000000 | ((br >> 2) & 0x00FF00FF) | ((green >> 2) & 0x0000FF00);
                    }
                return new NativeBitmap(w, h, pixels);
            }
            int area = factor * factor;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int b = 0, g = 0, r = 0;
                    for (int dy = 0; dy < factor; dy++)
                    {
                        int row = ((y * factor + dy) * width + x * factor) * 4;
                        for (int dx = 0; dx < factor; dx++) { int s = row + dx * 4; b += bgra[s]; g += bgra[s + 1]; r += bgra[s + 2]; }
                    }
                    int o = (y * w + x) * 4;
                    pixels[o] = (byte)(b / area); pixels[o + 1] = (byte)(g / area); pixels[o + 2] = (byte)(r / area); pixels[o + 3] = 255;
                }
            return new NativeBitmap(w, h, pixels);
        }
    }

    /// <summary>A QR code (quiet zone included) drawn with Scale x Scale pixels per module. The page shows it at one
    /// image pixel per device pixel, so every module keeps the same sharp size on screen.</summary>
    internal sealed class ScaledQr : IAppImage
    {
        private readonly byte[] _bits; // row-major, most significant bit first, dark = 1
        private readonly int _offset;
        public readonly int Side, Scale;

        private ScaledQr(byte[] bits, int offset, int side, int scale)
        {
            if (side <= 0 || scale < 1 || (long)side * scale > 8192) throw new ArgumentException("Invalid QR size");
            _bits = bits; _offset = offset; Side = side; Scale = scale;
        }

        public int Width { get { return Side * Scale; } }
        public int Height { get { return Side * Scale; } }

        /// <summary>The first frame of a FQR1 packed batch.</summary>
        public static ScaledQr FromPacked(byte[] packed, int scale)
        {
            if (packed == null || packed.Length < 12 || packed[0] != 0x46 || packed[1] != 0x51 || packed[2] != 0x52 || packed[3] != 0x31)
                throw new ArgumentException("Invalid packed QR batch");
            int side = packed[4] | packed[5] << 8;
            if (packed.Length < 12 + (side * side + 7) / 8) throw new ArgumentException("Invalid packed QR batch");
            return new ScaledQr(packed, 12, side, scale);
        }

        /// <summary>A QR matrix (dark = true) with a white quiet zone around it.</summary>
        public static ScaledQr FromMatrix(int width, int height, Func<int, int, bool> dark, int quietZone, int scale)
        {
            int side = Math.Max(width, height) + quietZone * 2;
            var bits = new byte[(side * side + 7) / 8];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (dark(x, y))
                    {
                        int bit = (y + quietZone) * side + x + quietZone;
                        bits[bit >> 3] |= (byte)(0x80 >> (bit & 7));
                    }
            return new ScaledQr(bits, 0, side, scale);
        }

        public ScaledQr WithScale(int scale) { return scale == Scale ? this : new ScaledQr(_bits, _offset, Side, scale); }

        public void CopyTo(Span<byte> destination) { Blit(destination, Side * Scale * 4); }

        /// <summary>Draw into a larger BGRA image: destination starts at this code's top-left pixel, rows are
        /// stride bytes apart.</summary>
        public void Blit(Span<byte> destination, int stride)
        {
            int width = Side * Scale * 4;
            for (int my = 0; my < Side; my++)
            {
                // Build the first pixel row of this module row, then repeat it for the module's height.
                var first = destination.Slice(my * Scale * stride, width);
                for (int mx = 0; mx < Side; mx++)
                {
                    int bit = my * Side + mx;
                    byte v = (_bits[_offset + (bit >> 3)] & (0x80 >> (bit & 7))) != 0 ? (byte)0 : (byte)255;
                    var module = first.Slice(mx * Scale * 4, Scale * 4);
                    for (int k = 0; k < module.Length; k += 4) { module[k] = v; module[k + 1] = v; module[k + 2] = v; module[k + 3] = 255; }
                }
                for (int k = 1; k < Scale; k++) first.CopyTo(destination.Slice((my * Scale + k) * stride, width));
            }
        }
    }

    /// <summary>Several QR codes of the same size and scale in one image, columns x rows, with Gap white modules
    /// between them (each code already carries its own quiet zone). The page shows the image one pixel per device
    /// pixel, like a single ScaledQr.</summary>
    internal sealed class TiledQr : IAppImage
    {
        public const int MaximumColumns = 3, MaximumRows = 2, MaximumTiles = MaximumColumns * MaximumRows;
        /// <summary>Modules of white between neighbouring codes, on top of their quiet zones.</summary>
        public const int Gap = 4;
        private readonly ScaledQr[] _tiles;
        private readonly int _columns, _rows, _side, _scale;

        public TiledQr(ScaledQr[] tiles, int columns, int rows)
        {
            if (tiles == null || columns < 1 || rows < 1 || tiles.Length != columns * rows) throw new ArgumentException("Invalid QR layout");
            _side = tiles[0].Side; _scale = tiles[0].Scale;
            foreach (var tile in tiles) if (tile.Side != _side || tile.Scale != _scale) throw new ArgumentException("Tiles differ in size");
            if ((long)Width * Height > 64L * 1024 * 1024 / 4) throw new ArgumentException("QR layout too large");
            _tiles = tiles; _columns = columns; _rows = rows;
        }

        public int Width { get { return (_columns * _side + (_columns - 1) * Gap) * _scale; } }
        public int Height { get { return (_rows * _side + (_rows - 1) * Gap) * _scale; } }

        public void CopyTo(Span<byte> destination)
        {
            int stride = Width * 4;
            destination.Slice(0, stride * Height).Fill(255);
            int pitch = (_side + Gap) * _scale;
            for (int row = 0; row < _rows; row++)
                for (int column = 0; column < _columns; column++)
                    _tiles[row * _columns + column].Blit(destination.Slice(row * pitch * stride + column * pitch * 4), stride);
        }
    }
}

namespace Xfer
{
    /// <summary>An FX2 picture drawn CellPixels pixels per cell, fine tier first, the coarse middle over it. The page
    /// shows it one image pixel per device pixel, so every cell keeps the same sharp size on screen.</summary>
    internal sealed class Fx2Image : IAppImage
    {
        private const uint Dark = 0xFF000000, Light = 0xFFFFFFFF;
        private readonly Fx2Picture _picture;

        public Fx2Image(Fx2Picture picture) { _picture = picture; }

        public int Width { get { return _picture.Layout.Width; } }
        public int Height { get { return _picture.Layout.Height; } }

        public void CopyTo(Span<byte> destination)
        {
            var layout = _picture.Layout;
            int width = layout.Width, height = layout.Height;
            var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(destination.Slice(0, width * height * 4));
            pixels.Fill(Light);
            for (int t = layout.Tiers.Length - 1; t >= 0; t--)
            {
                var tier = layout.Tiers[t];
                var cells = _picture.Cells[t];
                int s = tier.CellPixels, wide = tier.CellsWide;
                int x0 = Math.Max(0, tier.Left), x1 = Math.Min(width, tier.Left + tier.Width);
                if (x1 <= x0) continue;
                for (int cy = 0; cy < tier.CellsHigh; cy++)
                {
                    int top = tier.Top + cy * s, y0 = Math.Max(0, top), y1 = Math.Min(height, top + s);
                    if (y1 <= y0) continue;
                    // The first pixel row of this cell row, then copies of it.
                    var row = pixels.Slice(y0 * width, width);
                    for (int cx = 0; cx < wide; cx++)
                    {
                        int left = tier.Left + cx * s, a = Math.Max(x0, left), b = Math.Min(x1, left + s);
                        if (b > a) row.Slice(a, b - a).Fill(Fx2.Palette[cells[cy * wide + cx]]);
                    }
                    var span = row.Slice(x0, x1 - x0);
                    for (int y = y0 + 1; y < y1; y++) span.CopyTo(pixels.Slice(y * width + x0, x1 - x0));
                }
            }
        }
    }

    /// <summary>Another image drawn into a trapezoid of the same size (white around it), as a screen tilted away
    /// from the camera would look: Vertical &gt; 0 narrows the top edge by that share of the width (&lt; 0 the
    /// bottom), Horizontal &gt; 0 shortens the right edge by that share of the height (&lt; 0 the left). For
    /// measuring how far the reader copes with perspective without moving the camera.</summary>
    internal sealed class KeystoneImage : IAppImage
    {
        private static readonly object CacheGate = new object();
        private static int[] _cachedMap;
        private static string _cachedKey;
        private readonly IAppImage _source;
        private readonly double _horizontal, _vertical;

        public KeystoneImage(IAppImage source, double horizontal, double vertical)
        {
            if (Math.Abs(horizontal) > 0.9 || Math.Abs(vertical) > 0.9) throw new ArgumentException("Keystone out of range");
            _source = source; _horizontal = horizontal; _vertical = vertical;
        }

        public int Width { get { return _source.Width; } }
        public int Height { get { return _source.Height; } }

        public void CopyTo(Span<byte> destination)
        {
            int width = Width, height = Height;
            var source = new byte[width * height * 4];
            _source.CopyTo(source);
            var from = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(source.AsSpan());
            var to = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(destination.Slice(0, width * height * 4));
            var map = Map(width, height);
            for (int i = 0; i < map.Length; i++) to[i] = map[i] < 0 ? 0xFFFFFFFF : from[map[i]];
        }

        /// <summary>For every destination pixel, the source pixel it shows (nearest), or -1 outside the trapezoid.</summary>
        private int[] Map(int width, int height)
        {
            string key = width + "x" + height + ":" + _horizontal + ":" + _vertical;
            lock (CacheGate) if (_cachedKey == key) return _cachedMap;
            // Corners TL, TR, BR, BL of the trapezoid.
            double[] x = { 0, width, width, 0 }, y = { 0, 0, height, height };
            if (_vertical > 0) { x[0] += _vertical * width / 2; x[1] -= _vertical * width / 2; }
            if (_vertical < 0) { x[3] -= _vertical * width / 2; x[2] += _vertical * width / 2; }
            if (_horizontal > 0) { y[1] += _horizontal * height / 2; y[2] -= _horizontal * height / 2; }
            if (_horizontal < 0) { y[0] -= _horizontal * height / 2; y[3] += _horizontal * height / 2; }
            // Unit square to trapezoid (Heckbert), inverted by its adjugate.
            double sx = x[0] - x[1] + x[2] - x[3], sy = y[0] - y[1] + y[2] - y[3];
            double dx1 = x[1] - x[2], dx2 = x[3] - x[2], dy1 = y[1] - y[2], dy2 = y[3] - y[2], den = dx1 * dy2 - dx2 * dy1;
            double g = (sx * dy2 - dx2 * sy) / den, h = (dx1 * sy - sx * dy1) / den;
            double a = x[1] - x[0] + g * x[1], b = x[3] - x[0] + h * x[3], c = x[0];
            double d = y[1] - y[0] + g * y[1], e = y[3] - y[0] + h * y[3], f = y[0];
            double A = e - f * h, B = c * h - b, C = b * f - c * e, D = f * g - d, E = a - c * g, F = c * d - a * f, G = d * h - e * g, H = b * g - a * h, I = a * e - b * d;
            var map = new int[width * height];
            for (int py = 0; py < height; py++)
                for (int px = 0; px < width; px++)
                {
                    double X = px + 0.5, Y = py + 0.5, w = G * X + H * Y + I;
                    double u = (A * X + B * Y + C) / w, v = (D * X + E * Y + F) / w;
                    map[py * width + px] = u < 0 || v < 0 || u >= 1 || v >= 1 ? -1 : (int)(v * height) * width + (int)(u * width);
                }
            lock (CacheGate) { _cachedKey = key; _cachedMap = map; }
            return map;
        }
    }
}
