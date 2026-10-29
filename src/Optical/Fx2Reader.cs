using System;
using System.Collections.Generic;

namespace Xfer
{
    /// <summary>Reads FX2 patches from a camera picture (8-bit gray): local threshold, marker cores (isolated
    /// compact dark blobs), each core's lattice axes from its neighbours, the ID ring, the screen's orientation by
    /// majority, then for every patch whose corner markers are seen a projective map from the four marker centres,
    /// one sample per cell centre, Reed-Solomon and the CRC. Nothing is tuned to a camera or a cell size: the scale
    /// comes from each core's size and its neighbours. One reader per thread (it keeps its buffers).</summary>
    internal sealed class Fx2Reader
    {
        private const int Block = 16, Window = 4, MinimumContrast = 24, MaximumCandidates = 4000, Bucket = 64;
        /// <summary>Guard ring check (Fx2Lab varies these to compare).</summary>
        internal static double GuardRadius = 1.8, GuardDiagonal = 2.5; internal static int GuardDark = 2;
        private static readonly double[] Cos8 = { 1, 0.7071, 0, -0.7071, -1, -0.7071, 0, 0.7071 }, Sin8 = { 0, 0.7071, 1, 0.7071, 0, -0.7071, -1, -0.7071 };

        public sealed class Marker
        {
            public double X, Y, Ux, Uy, Vx, Vy, Dark, Light;
            public int Area, BoxWidth, BoxHeight, Transform = -1, Column, Row, Tier, Level, Errors;
            public int[] Transforms = new int[8];
            /// <summary>Fewest corrected ID bits over the axes tried (int.MaxValue: no axes or no reading).</summary>
            public int Best = int.MaxValue;
        }

        public sealed class Patch
        {
            public int Tier, Column, Row, Corrected, Guessed;
            public double Sharpening;
            public bool Decoded, LumaDecoded, Color, ColorDecoded;
            public string Failure;
            public double[] Corners;
            public uint Sequence;
        }

        public sealed class Result
        {
            public readonly List<byte[]> Frames = new List<byte[]>();
            public readonly List<Marker> Markers = new List<Marker>();
            public readonly List<Patch> Patches = new List<Patch>();
            public int Candidates, Transform = -1, Corrected;
            /// <summary>Patches read without the blur correction: most of them when the picture is sharp.</summary>
            public int Plain, LumaDecoded, ColorSeen, ColorDecoded;
            /// <summary>Every marker core candidate (for tools).</summary>
            public List<Marker> CandidateList;
            /// <summary>Milliseconds: total, threshold, blobs, IDs, patches.</summary>
            public double Ms, ThresholdMs, BlobMs, IdMs, PatchMs;
            public bool Tracked;
        }

        private int[] _blockSum = new int[0], _blockMin = new int[0], _blockMax = new int[0], _threshold = new int[0];
        private int[] _runStart = new int[1024], _runEnd = new int[1024], _runRow = new int[1024], _parent = new int[1024];
        private int[] _rowFirst = new int[0], _area = new int[0], _minX = new int[0], _maxX = new int[0], _minY = new int[0], _maxY = new int[0], _blob = new int[0];
        private readonly byte[] _cells = new byte[Fx2.DataCells.Length];
        private readonly double[] _samples = new double[Fx2.DataCells.Length], _grid = new double[Fx2.Pitch * Fx2.Pitch];
        private readonly double[] _chromaR = new double[Fx2.ColorCells.Length], _chromaB = new double[Fx2.ColorCells.Length];
        private readonly byte[] _colorBits = new byte[Fx2.ColorCells.Length * 2];
        private Result _tracked;
        private int _trackedWidth, _trackedHeight, _trackedAge;

        public Result Read(byte[] gray, int width, int height, byte[] bgra = null)
        {
            var stamp = Diagnostics.Stamp();
            var result = new Result();
            // The marker grid stays still while the data changes. Validate its IDs in this picture before
            // reusing geometry; CRC-checked patches decide whether it is still accurate enough. Reacquire
            // periodically, or immediately after movement, a layout change, or a loss of readable patches.
            if (_tracked != null && _trackedWidth == width && _trackedHeight == height && _trackedAge++ < 15)
            {
                var transforms = new int[8];
                foreach (var m in _tracked.Markers)
                {
                    double dark, light;
                    if (ReadRing(gray, width, height, m.X, m.Y, m.Ux, m.Uy, m.Vx, m.Vy, transforms, out dark, out light) > 1) continue;
                    int value = transforms[_tracked.Transform];
                    int id = m.Column | m.Row << 5 | m.Tier << 9 | m.Level << 10;
                    if (value < 0 || value >> 12 > 1 || (value & 4095) != id) continue;
                    m.Dark = dark; m.Light = light;
                    result.Markers.Add(m);
                }
                // Full discovery also keeps plausible IDs in the changing data. Only matching IDs are
                // needed here; the successfully decoded patch count below is the measure of usable geometry.
                if (result.Markers.Count >= 4)
                {
                    result.Transform = _tracked.Transform;
                    result.IdMs = Diagnostics.Milliseconds(stamp);
                    Assemble(gray, width, height, bgra, result);
                    if (result.LumaDecoded >= Math.Max(1, _tracked.LumaDecoded / 2))
                    {
                        result.Tracked = true;
                        result.Ms = Diagnostics.Milliseconds(stamp);
                        result.PatchMs = result.Ms - result.IdMs;
                        return result;
                    }
                }
                result = new Result();
            }
            _tracked = null;
            Threshold(gray, width, height);
            result.ThresholdMs = Diagnostics.Milliseconds(stamp);
            var candidates = Candidates(gray, width, height, result);
            result.BlobMs = Diagnostics.Milliseconds(stamp) - result.ThresholdMs;
            Orient(gray, width, height, candidates, result);
            result.IdMs = Diagnostics.Milliseconds(stamp) - result.ThresholdMs - result.BlobMs;
            Assemble(gray, width, height, bgra, result);
            result.Ms = Diagnostics.Milliseconds(stamp);
            result.PatchMs = result.Ms - result.ThresholdMs - result.BlobMs - result.IdMs;
            if (result.LumaDecoded >= 4)
            {
                _tracked = result; _trackedWidth = width; _trackedHeight = height; _trackedAge = 0;
            }
            return result;
        }

        // ---------------------------------------------------------------- threshold

        private int _bw, _bh;

        /// <summary>Per 16 x 16 block: the mean of the 9 x 9 blocks around it, or int.MinValue where the 5 x 5
        /// blocks around show no contrast (background, a uniform area).</summary>
        private void Threshold(byte[] gray, int width, int height)
        {
            _bw = (width + Block - 1) / Block; _bh = (height + Block - 1) / Block;
            int blocks = _bw * _bh;
            if (_blockSum.Length < blocks) { _blockSum = new int[blocks]; _blockMin = new int[blocks]; _blockMax = new int[blocks]; _threshold = new int[blocks]; }
            for (int by = 0; by < _bh; by++)
                for (int bx = 0; bx < _bw; bx++)
                {
                    // Every other pixel of every other row: the statistics of a block need no more.
                    int x0 = bx * Block, y0 = by * Block, x1 = Math.Min(width, x0 + Block), y1 = Math.Min(height, y0 + Block);
                    int sum = 0, min = 255, max = 0, n = 0;
                    for (int y = y0; y < y1; y += 2)
                    {
                        int row = y * width;
                        for (int x = x0; x < x1; x += 2)
                        {
                            int g = gray[row + x];
                            sum += g; n++; if (g < min) min = g; if (g > max) max = g;
                        }
                    }
                    int i = by * _bw + bx;
                    _blockSum[i] = sum / n; _blockMin[i] = min; _blockMax[i] = max;
                }
            // Prefix sums of the block means for the 9 x 9 window, over blocks that show contrast: a plain light area
            // (a coarse tier's margin, a white window edge) would otherwise lift the threshold next to it until a
            // blurred guard ring counts as dark.
            var prefix = new int[(_bw + 1) * (_bh + 1)];
            var counts = new int[(_bw + 1) * (_bh + 1)];
            for (int by = 0; by < _bh; by++)
                for (int bx = 0; bx < _bw; bx++)
                {
                    int i = by * _bw + bx;
                    bool contrast = _blockMax[i] - _blockMin[i] >= MinimumContrast;
                    prefix[(by + 1) * (_bw + 1) + bx + 1] = (contrast ? _blockSum[i] : 0) + prefix[by * (_bw + 1) + bx + 1]
                        + prefix[(by + 1) * (_bw + 1) + bx] - prefix[by * (_bw + 1) + bx];
                    counts[(by + 1) * (_bw + 1) + bx + 1] = (contrast ? 1 : 0) + counts[by * (_bw + 1) + bx + 1]
                        + counts[(by + 1) * (_bw + 1) + bx] - counts[by * (_bw + 1) + bx];
                }
            for (int by = 0; by < _bh; by++)
                for (int bx = 0; bx < _bw; bx++)
                {
                    int min = 255, max = 0;
                    for (int y = Math.Max(0, by - 2); y <= Math.Min(_bh - 1, by + 2); y++)
                        for (int x = Math.Max(0, bx - 2); x <= Math.Min(_bw - 1, bx + 2); x++)
                        {
                            int i = y * _bw + x;
                            if (_blockMin[i] < min) min = _blockMin[i];
                            if (_blockMax[i] > max) max = _blockMax[i];
                        }
                    if (max - min < MinimumContrast) { _threshold[by * _bw + bx] = int.MinValue; continue; }
                    int x0 = Math.Max(0, bx - Window), y0 = Math.Max(0, by - Window), x1 = Math.Min(_bw, bx + Window + 1), y1 = Math.Min(_bh, by + Window + 1);
                    int total = prefix[y1 * (_bw + 1) + x1] - prefix[y0 * (_bw + 1) + x1] - prefix[y1 * (_bw + 1) + x0] + prefix[y0 * (_bw + 1) + x0];
                    int n = counts[y1 * (_bw + 1) + x1] - counts[y0 * (_bw + 1) + x1] - counts[y1 * (_bw + 1) + x0] + counts[y0 * (_bw + 1) + x0];
                    _threshold[by * _bw + bx] = n > 0 ? total / n : (min + max) / 2;
                }
        }

        private int LocalThreshold(double x, double y)
        {
            int bx = Math.Max(0, Math.Min(_bw - 1, (int)x / Block)), by = Math.Max(0, Math.Min(_bh - 1, (int)y / Block));
            return _threshold[by * _bw + bx];
        }

        // ---------------------------------------------------------------- marker cores

        private int Find(int i)
        {
            while (_parent[i] != i) { _parent[i] = _parent[_parent[i]]; i = _parent[i]; }
            return i;
        }

        private void Grow(int runs)
        {
            if (runs < _runStart.Length) return;
            int size = _runStart.Length * 2;
            Array.Resize(ref _runStart, size); Array.Resize(ref _runEnd, size); Array.Resize(ref _runRow, size); Array.Resize(ref _parent, size);
        }

        /// <summary>Records dark run [start, end) of row y and joins it to the runs of the previous row it touches
        /// (8-connected; p walks the previous row's runs, which end at previousEnd). Returns the run count.</summary>
        private int AddRun(int start, int end, int y, int runs, int previousEnd, ref int p)
        {
            Grow(runs + 1);
            _runStart[runs] = start; _runEnd[runs] = end; _runRow[runs] = y; _parent[runs] = runs;
            if (y > 0)
            {
                // The smallest run index of a blob is its root.
                int root = runs;
                while (p < previousEnd && _runEnd[p] < start) p++;
                for (int q = p; q < previousEnd && _runStart[q] <= end; q++)
                {
                    int a = Find(q);
                    if (a == root) continue;
                    if (a < root) { _parent[root] = a; root = a; } else _parent[a] = root;
                }
            }
            return runs + 1;
        }

        /// <summary>Dark runs joined into 8-connected blobs; the isolated compact ones are marker core candidates.</summary>
        private List<Marker> Candidates(byte[] gray, int width, int height, Result result)
        {
            if (_rowFirst.Length < height + 1) _rowFirst = new int[height + 1];
            int runs = 0;
            for (int y = 0; y < height; y++)
            {
                _rowFirst[y] = runs;
                int row = y * width, thresholds = (y / Block) * _bw, previousEnd = runs, p = y > 0 ? _rowFirst[y - 1] : 0;
                int start = -1;
                // Block by block, each with its own threshold; a run may continue across blocks.
                for (int bx = 0, x = 0; bx < _bw; bx++)
                {
                    int t = _threshold[thresholds + bx], end = Math.Min(width, x + Block);
                    for (; x < end; x++)
                    {
                        if (gray[row + x] < t) { if (start < 0) start = x; }
                        else if (start >= 0) { runs = AddRun(start, x, y, runs, previousEnd, ref p); start = -1; }
                    }
                }
                if (start >= 0) runs = AddRun(start, width, y, runs, previousEnd, ref p);
            }
            _rowFirst[height] = runs;
            // Accumulate per blob (roots come first: a root has the smallest run index of its blob).
            if (_area.Length < runs) { int size = Math.Max(runs, _area.Length * 2); _area = new int[size]; _minX = new int[size]; _maxX = new int[size]; _minY = new int[size]; _maxY = new int[size]; _blob = new int[size]; }
            int blobs = 0;
            int[] area = _area, minX = _minX, maxX = _maxX, minY = _minY, maxY = _maxY;
            for (int r = 0; r < runs; r++)
            {
                int root = Find(r), k;
                if (root == r) { k = _blob[r] = blobs++; area[k] = 0; minX[k] = int.MaxValue; maxX[k] = -1; minY[k] = int.MaxValue; maxY[k] = -1; }
                else k = _blob[root];
                area[k] += _runEnd[r] - _runStart[r];
                if (_runStart[r] < minX[k]) minX[k] = _runStart[r];
                if (_runEnd[r] - 1 > maxX[k]) maxX[k] = _runEnd[r] - 1;
                if (_runRow[r] < minY[k]) minY[k] = _runRow[r];
                if (_runRow[r] > maxY[k]) maxY[k] = _runRow[r];
            }
            var candidates = new List<Marker>();
            for (int k = 0; k < blobs && candidates.Count < MaximumCandidates; k++)
            {
                int w = maxX[k] - minX[k] + 1, h = maxY[k] - minY[k] + 1;
                if (area[k] < 9 || w < 3 || h < 3 || w > width / 4 || h > height / 4) continue;
                if (minX[k] == 0 || minY[k] == 0 || maxX[k] == width - 1 || maxY[k] == height - 1) continue;
                if (Math.Max(w, h) > 2 * Math.Min(w, h) || area[k] * 2 < w * h) continue;
                // Gray-weighted centre over the box and one pixel around it.
                double sw = 0, sx = 0, sy = 0;
                int t = LocalThreshold((minX[k] + maxX[k]) / 2.0, (minY[k] + maxY[k]) / 2.0);
                for (int y = minY[k] - 1; y <= maxY[k] + 1; y++)
                    for (int x = minX[k] - 1; x <= maxX[k] + 1; x++)
                    {
                        int weight = t - gray[y * width + x];
                        if (weight <= 0) continue;
                        sw += weight; sx += weight * x; sy += weight * y;
                    }
                if (sw <= 0) continue;
                double cx = sx / sw, cy = sy / sw, e = Math.Sqrt(area[k]) / 3;
                // A core sits in a light guard ring one cell wide: in the guard (1.8 cells from the centre across and
                // down, 2.5 on the diagonals past the core's corners) at most one of eight directions may be dark.
                // This guard-ring test rejects most data-region blobs.
                int dark = 0;
                for (int k8 = 0; k8 < 8 && dark < GuardDark; k8++)
                {
                    double r = (k8 & 1) == 0 ? GuardRadius : GuardDiagonal;
                    double px = cx + r * e * Cos8[k8], py = cy + r * e * Sin8[k8];
                    if (px < 0 || py < 0 || px >= width - 1 || py >= height - 1 || Sample(gray, width, height, px, py) < t) dark++;
                }
                if (dark >= GuardDark) continue;
                candidates.Add(new Marker { X = cx, Y = cy, Area = area[k], BoxWidth = w, BoxHeight = h });
            }
            result.Candidates = candidates.Count;
            result.CandidateList = candidates;
            return candidates;
        }

        // ---------------------------------------------------------------- lattice axes and IDs

        private void Orient(byte[] gray, int width, int height, List<Marker> candidates, Result result)
        {
            var votes = new int[8];
            var across = new List<double[]>(); var down = new List<double[]>();
            var transforms = new int[8];
            // Candidates by 64-pixel bucket for the neighbour search.
            int gw = width / Bucket + 1, gh = height / Bucket + 1;
            var buckets = new List<Marker>[gw * gh];
            foreach (var m in candidates)
            {
                int b = (int)(m.Y / Bucket) * gw + (int)(m.X / Bucket);
                if (buckets[b] == null) buckets[b] = new List<Marker>();
                buckets[b].Add(m);
            }
            foreach (var m in candidates)
            {
                // Neighbouring cores of a similar size about Pitch core-cells away (a core is 3 cells wide), on
                // either lattice axis; the closest fits first. The core's box gives the cell size across and down
                // apart, so a screen seen at a slant (cells narrower one way) still finds its neighbours.
                double ex = m.BoxWidth / 3.0, ey = m.BoxHeight / 3.0, spacing = Fx2.Pitch * Math.Max(ex, ey), reach = 1.5 * spacing;
                across.Clear(); down.Clear();
                int bx0 = Math.Max(0, (int)((m.X - reach) / Bucket)), bx1 = Math.Min(gw - 1, (int)((m.X + reach) / Bucket));
                int by0 = Math.Max(0, (int)((m.Y - reach) / Bucket)), by1 = Math.Min(gh - 1, (int)((m.Y + reach) / Bucket));
                for (int by = by0; by <= by1; by++)
                    for (int bx = bx0; bx <= bx1; bx++)
                    {
                        var bucket = buckets[by * gw + bx];
                        if (bucket == null) continue;
                        foreach (var n in bucket)
                        {
                            if (n == m || n.Area * 2.2 < m.Area || m.Area * 2.2 < n.Area) continue;
                            double dx = n.X - m.X, dy = n.Y - m.Y, d = Math.Sqrt(dx * dx + dy * dy);
                            bool horizontal = Math.Abs(dx) >= Math.Abs(dy);
                            double fit = Math.Abs(d / (Fx2.Pitch * (horizontal ? ex : ey)) - 1);
                            if (fit > 0.4) continue;
                            if (horizontal) Keep(across, dx < 0 ? -dx : dx, dx < 0 ? -dy : dy, fit);
                            else Keep(down, dy < 0 ? -dx : dx, dy < 0 ? -dy : dy, fit);
                        }
                    }
                // The pair of axes under which the ID ring reads best. Lattice axes are about square to each
                // other (perspective bends that only a little over one marker spacing); a slanted screen may
                // shorten one of them.
                int best = int.MaxValue;
                foreach (var u in across)
                {
                    if (best == 0) break;
                    foreach (var v in down)
                    {
                        double lu = Math.Sqrt(u[0] * u[0] + u[1] * u[1]), lv = Math.Sqrt(v[0] * v[0] + v[1] * v[1]);
                        if (Math.Abs(u[0] * v[0] + u[1] * v[1]) > 0.35 * lu * lv || lu > 2 * lv || lv > 2 * lu) continue;
                        double ux = u[0] / Fx2.Pitch, uy = u[1] / Fx2.Pitch, vx = v[0] / Fx2.Pitch, vy = v[1] / Fx2.Pitch, dark, light;
                        int errors = ReadRing(gray, width, height, m.X, m.Y, ux, uy, vx, vy, transforms, out dark, out light);
                        if (errors >= best) continue;
                        best = errors;
                        m.Ux = ux; m.Uy = uy; m.Vx = vx; m.Vy = vy; m.Dark = dark; m.Light = light;
                        Array.Copy(transforms, m.Transforms, 8);
                        if (errors == 0) break;
                    }
                }
                m.Best = best;
                if (best > 3) continue;
                // An exact ID weighs four times a corrected one: a blurred picture yields many chance one-bit
                // readings in random orientations, and those must not outvote the few real markers.
                for (int t = 0; t < 8; t++) if (m.Transforms[t] >= 0 && m.Transforms[t] >> 12 == best && best <= 1) votes[t] += best == 0 ? 4 : 1;
                result.Markers.Add(m);
            }
            int chosen = 0;
            for (int t = 1; t < 8; t++) if (votes[t] > votes[chosen]) chosen = t;
            if (votes[chosen] == 0) { result.Markers.Clear(); return; }
            result.Transform = chosen;
            var kept = new List<Marker>();
            foreach (var m in result.Markers)
            {
                int value = m.Transforms[chosen];
                if (value < 0 || value >> 12 > 2) continue;
                m.Transform = chosen; m.Errors = value >> 12;
                m.Column = value & 31; m.Row = value >> 5 & 15; m.Tier = value >> 9 & 1; m.Level = value >> 10 & 3;
                kept.Add(m);
            }
            result.Markers.Clear();
            result.Markers.AddRange(kept);
        }

        /// <summary>Keeps the three best-fitting neighbour offsets.</summary>
        private static void Keep(List<double[]> list, double x, double y, double fit)
        {
            int at = list.Count;
            while (at > 0 && list[at - 1][2] > fit) at--;
            if (at >= 3) return;
            list.Insert(at, new[] { x, y, fit });
            if (list.Count > 3) list.RemoveAt(3);
        }

        /// <summary>Samples a marker's core, guard and ID ring along per-cell axes u and v; the fewest corrected
        /// bits over the eight transforms (int.MaxValue when none reads) and each transform's reading.</summary>
        private static int ReadRing(byte[] gray, int width, int height, double x, double y, double ux, double uy, double vx, double vy,
            int[] transforms, out double dark, out double light)
        {
            dark = light = 0;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    double g = Sample(gray, width, height, x + dx * ux + dy * vx, y + dx * uy + dy * vy);
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) <= 1) dark += g; else light += g;
                }
            dark /= 9; light /= 16;
            if (light - dark < MinimumContrast / 2) return int.MaxValue;
            double level = (dark + light) / 2;
            int bits = 0;
            for (int k = 0; k < 24; k++)
            {
                double g = Sample(gray, width, height, x + Fx2.RingX[k] * ux + Fx2.RingY[k] * vx, y + Fx2.RingX[k] * uy + Fx2.RingY[k] * vy);
                if (g < level) bits |= 1 << (23 - k);
            }
            int best = int.MaxValue;
            for (int t = 0; t < 8; t++)
            {
                int column, row, tier, lvl, errors;
                transforms[t] = Fx2.TryReadRing(bits, t, out column, out row, out tier, out lvl, out errors)
                    ? (errors << 12 | column | row << 5 | tier << 9 | lvl << 10) : -1;
                if (transforms[t] >= 0 && errors < best) best = errors;
            }
            return best;
        }

        // ---------------------------------------------------------------- patches

        private void Assemble(byte[] gray, int width, int height, byte[] bgra, Result result)
        {
            int t = result.Transform;
            if (t < 0) return;
            var byId = new Dictionary<int, List<Marker>>();
            foreach (var m in result.Markers)
            {
                int key = Key(m.Tier, m.Column, m.Row);
                List<Marker> list;
                if (!byId.TryGetValue(key, out list)) byId[key] = list = new List<Marker>();
                list.Add(m);
            }
            foreach (var m in result.Markers)
            {
                // Screen right and down in the picture, per cell, from this marker's axes.
                double rx = Fx2.TA[t] * m.Ux + Fx2.TB[t] * m.Vx, ry = Fx2.TA[t] * m.Uy + Fx2.TB[t] * m.Vy;
                double dx = Fx2.TC[t] * m.Ux + Fx2.TD[t] * m.Vx, dy = Fx2.TC[t] * m.Uy + Fx2.TD[t] * m.Vy;
                double reach = 0.3 * Fx2.Pitch * Math.Sqrt(Math.Min(rx * rx + ry * ry, dx * dx + dy * dy));
                var tr = Near(byId, m.Tier, m.Column + 1, m.Row, m.X + Fx2.Pitch * rx, m.Y + Fx2.Pitch * ry, reach);
                var bl = Near(byId, m.Tier, m.Column, m.Row + 1, m.X + Fx2.Pitch * dx, m.Y + Fx2.Pitch * dy, reach);
                var br = Near(byId, m.Tier, m.Column + 1, m.Row + 1, m.X + Fx2.Pitch * (rx + dx), m.Y + Fx2.Pitch * (ry + dy), reach);
                int found = (tr != null ? 1 : 0) + (bl != null ? 1 : 0) + (br != null ? 1 : 0);
                if (found < 2) continue;
                var patch = new Patch { Tier = m.Tier, Column = m.Column, Row = m.Row, Guessed = 3 - found };
                var c = new double[8];
                c[0] = m.X; c[1] = m.Y;
                if (tr != null) { c[2] = tr.X; c[3] = tr.Y; }
                if (bl != null) { c[4] = bl.X; c[5] = bl.Y; }
                if (br != null) { c[6] = br.X; c[7] = br.Y; }
                // A missing corner completes the parallelogram of the other three.
                if (tr == null) { c[2] = m.X + c[6] - c[4]; c[3] = m.Y + c[7] - c[5]; }
                if (bl == null) { c[4] = m.X + c[6] - c[2]; c[5] = m.Y + c[7] - c[3]; }
                if (br == null) { c[6] = c[2] + c[4] - m.X; c[7] = c[3] + c[5] - m.Y; }
                patch.Corners = c;
                result.Patches.Add(patch);
                var corners = new[] { m, tr, bl, br };
                byte[] frame;
                int corrected;
                if (ReadPatch(gray, width, height, bgra, c, corners, m.Level, out frame, out corrected, patch))
                {
                    patch.Decoded = true; patch.Corrected = corrected; result.Corrected += corrected;
                    patch.Sequence = frame.Length >= 8 ? (uint)(frame[4] | frame[5] << 8 | frame[6] << 16 | frame[7] << 24) : 0;
                    result.Frames.Add(frame);
                }
                if (patch.LumaDecoded) { result.LumaDecoded++; if (patch.Sharpening == 0) result.Plain++; }
                if (patch.Color) result.ColorSeen++;
                if (patch.ColorDecoded) result.ColorDecoded++;
            }
        }

        private static int Key(int tier, int column, int row)
        {
            return tier << 16 | (column & (Fx2.IdColumns - 1)) << 8 | (row & (Fx2.IdRows - 1));
        }

        private static Marker Near(Dictionary<int, List<Marker>> byId, int tier, int column, int row, double x, double y, double reach)
        {
            List<Marker> list;
            if (!byId.TryGetValue(Key(tier, column, row), out list)) return null;
            Marker best = null;
            double bestDistance = reach * reach;
            foreach (var m in list)
            {
                double d = (m.X - x) * (m.X - x) + (m.Y - y) * (m.Y - y);
                if (d <= bestDistance) { bestDistance = d; best = m; }
            }
            return best;
        }

        private bool ReadPatch(byte[] gray, int width, int height, byte[] bgra, double[] c, Marker[] corners, int level, out byte[] frame, out int corrected, Patch patch)
        {
            frame = null; corrected = 0;
            // Projective map of the unit square onto TL, TR, BR, BL (Heckbert).
            double x0 = c[0], y0 = c[1], x1 = c[2], y1 = c[3], x2 = c[6], y2 = c[7], x3 = c[4], y3 = c[5];
            double sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3, a, b, d, e, g, h;
            if (Math.Abs(sx) < 1e-9 && Math.Abs(sy) < 1e-9) { a = x1 - x0; b = x3 - x0; d = y1 - y0; e = y3 - y0; g = h = 0; }
            else
            {
                double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2, den = dx1 * dy2 - dx2 * dy1;
                if (Math.Abs(den) < 1e-9) { patch.Failure = "geometry"; return false; }
                g = (sx * dy2 - dx2 * sy) / den; h = (dx1 * sy - sx * dy1) / den;
                a = x1 - x0 + g * x1; b = x3 - x0 + h * x3; d = y1 - y0 + g * y1; e = y3 - y0 + h * y3;
            }
            // Dark and light levels at the corners (a guessed corner takes the others' mean), to even out uneven
            // lighting across the patch.
            double[] darkAt = new double[4], lightAt = new double[4];
            double darkSum = 0, lightSum = 0; int known = 0;
            for (int k = 0; k < 4; k++) if (corners[k] != null) { darkSum += corners[k].Dark; lightSum += corners[k].Light; known++; }
            for (int k = 0; k < 4; k++)
            {
                darkAt[k] = corners[k] != null ? corners[k].Dark : darkSum / known;
                lightAt[k] = corners[k] != null ? corners[k].Light : lightSum / known;
            }
            // One sample per cell centre of the whole patch (marker cells too: the sharpening below uses them).
            const int M = Fx2.Pitch;
            const double step = 1.0 / M;
            for (int y = 0; y < M; y++)
                for (int x = 0; x < M; x++)
                {
                    double s = x * step, t = y * step;
                    double w = g * s + h * t + 1;
                    double px = (a * s + b * t + x0) / w, py = (d * s + e * t + y0) / w;
                    if (px < 0 || py < 0 || px > width - 1 || py > height - 1) { patch.Failure = "outside"; return false; }
                    double dark = (1 - s) * (1 - t) * darkAt[0] + s * (1 - t) * darkAt[1] + (1 - s) * t * darkAt[2] + s * t * darkAt[3];
                    double light = (1 - s) * (1 - t) * lightAt[0] + s * (1 - t) * lightAt[1] + (1 - s) * t * lightAt[2] + s * t * lightAt[3];
                    _grid[y * M + x] = (Sample(gray, width, height, px, py) - dark) / Math.Max(1, light - dark);
                }
            // Blur pulls a cell towards its neighbours; when the plain reading fails, take back a share of that
            // (cell minus its four neighbours' mean). The threshold is midway between the dark and the light
            // cells' mean levels (two-means from the markers' midpoint): measured on C920 pictures it reads more
            // patches than the markers' midpoint or the patch median.
            var code = Fx2Code.ForLevel(level);
            var order = Fx2.DataCells;
            for (int pass = 0; pass < Sharpening.Length; pass++)
            {
                double k = Sharpening[pass];
                for (int i = 0; i < order.Length; i++)
                {
                    int cell = order[i], x = cell % M, y = cell / M;
                    double v = _grid[cell];
                    if (k > 0)
                    {
                        double sum = 0; int n = 0;
                        if (x > 0) { sum += _grid[cell - 1]; n++; }
                        if (x < M - 1) { sum += _grid[cell + 1]; n++; }
                        if (y > 0) { sum += _grid[cell - M]; n++; }
                        if (y < M - 1) { sum += _grid[cell + M]; n++; }
                        v += k * (v - sum / n);
                    }
                    _samples[i] = v;
                }
                double threshold = TwoMeans(_samples, order.Length);
                for (int i = 0; i < order.Length; i++) _cells[i] = _samples[i] < threshold ? (byte)1 : (byte)0;
                if (code.TryDecode(_cells, out frame, out corrected)) { patch.Sharpening = k; patch.LumaDecoded = true; break; }
            }
            if (!patch.LumaDecoded) { patch.Failure = "rs"; return false; }
            if (frame[0] != Fx2.ColorTag || (frame[1] != 0x0c && frame[1] != 0x0d)) return true;
            patch.Color = true;
            if (bgra == null) { patch.Failure = "color-missing"; return false; }
            // Average chroma around each 2x2-cell centre. R-G and B-G remove the luminance data;
            // local two-means thresholds absorb white balance and illumination shifts.
            for (int i = 0; i < Fx2.ColorCells.Length; i++)
            {
                int at = Fx2.ColorCells[i];
                double red = 0, blue = 0;
                for (int dy = -1; dy <= 1; dy += 2)
                    for (int dx = -1; dx <= 1; dx += 2)
                    {
                        double s = (at % M + 0.5 + dx * 0.3) * step, t = (at / M + 0.5 + dy * 0.3) * step;
                        double w = g * s + h * t + 1;
                        double px = (a * s + b * t + x0) / w, py = (d * s + e * t + y0) / w;
                        red += Chroma(bgra, width, height, px, py, 2);
                        blue += Chroma(bgra, width, height, px, py, 0);
                    }
                _chromaR[i] = red / 4; _chromaB[i] = blue / 4;
            }
            double redThreshold = TwoMeans(_chromaR, _chromaR.Length), blueThreshold = TwoMeans(_chromaB, _chromaB.Length);
            for (int i = 0; i < Fx2.ColorCells.Length; i++)
            {
                _colorBits[i * 2] = _chromaR[i] > redThreshold ? (byte)1 : (byte)0;
                _colorBits[i * 2 + 1] = _chromaB[i] > blueThreshold ? (byte)1 : (byte)0;
            }
            byte[] tail; int colorCorrected;
            if (!Fx2Code.ForColor(level).TryDecode(_colorBits, out tail, out colorCorrected)) { patch.Failure = "color-rs"; return false; }
            var combined = new byte[frame.Length + tail.Length - 4];
            Buffer.BlockCopy(frame, 0, combined, 0, frame.Length); combined[0] = 0xd1;
            Buffer.BlockCopy(tail, 4, combined, frame.Length, tail.Length - 4);
            uint checksum = (uint)tail[0] << 24 | (uint)tail[1] << 16 | (uint)tail[2] << 8 | tail[3];
            if (Fx2.Crc32(combined, 0, combined.Length) != checksum) { patch.Failure = "color-mixed"; return false; }
            frame = combined; corrected += colorCorrected; patch.ColorDecoded = true;
            return true;
        }

        private static double Chroma(byte[] bgra, int width, int height, double x, double y, int channel)
        {
            x = Math.Max(0, Math.Min(width - 1.001, x)); y = Math.Max(0, Math.Min(height - 1.001, y));
            int ix = (int)x, iy = (int)y, i = (iy * width + ix) * 4, below = i + width * 4;
            double fx = x - ix, fy = y - iy;
            return ((bgra[i + channel] - bgra[i + 1]) * (1 - fx) + (bgra[i + 4 + channel] - bgra[i + 5]) * fx) * (1 - fy)
                + ((bgra[below + channel] - bgra[below + 1]) * (1 - fx) + (bgra[below + 4 + channel] - bgra[below + 5]) * fx) * fy;
        }

        private static readonly double[] Sharpening = { 0, 0.6, 1.2 };

        /// <summary>Two-means threshold of normalized samples (0 = marker core, 1 = guard ring), from 0.5.</summary>
        private static double TwoMeans(double[] values, int count)
        {
            double t = 0.5;
            for (int iteration = 0; iteration < 4; iteration++)
            {
                double a = 0, b = 0; int na = 0, nb = 0;
                for (int i = 0; i < count; i++) if (values[i] < t) { a += values[i]; na++; } else { b += values[i]; nb++; }
                if (na == 0 || nb == 0) break;
                t = (a / na + b / nb) / 2;
            }
            return t;
        }

        private static double Sample(byte[] gray, int width, int height, double x, double y)
        {
            if (x < 0) x = 0; if (y < 0) y = 0;
            if (x > width - 1.001) x = width - 1.001; if (y > height - 1.001) y = height - 1.001;
            int ix = (int)x, iy = (int)y;
            double fx = x - ix, fy = y - iy;
            int i = iy * width + ix;
            return (gray[i] * (1 - fx) + gray[i + 1] * fx) * (1 - fy) + (gray[i + width] * (1 - fx) + gray[i + width + 1] * fx) * fy;
        }
    }
}
