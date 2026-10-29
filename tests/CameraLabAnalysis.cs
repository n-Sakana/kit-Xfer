using System;
using System.Globalization;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Xfer.Lab;

internal static partial class CameraLab
{
    private sealed class Roi
    {
        public int X, Y, Width, Height;
        public static Roi Parse(string value)
        {
            var parts = value.Split(',');
            if (parts.Length != 4 || parts.Any(s => !int.TryParse(s, NumberStyles.None, Inv, out _)))
                throw new ArgumentException("ROI must be X,Y,W,H (nonnegative integers).");
            var p = parts.Select(s => int.Parse(s, Inv)).ToArray();
            if (p[0] > 4096 || p[1] > 4096 || p[2] < 16 || p[3] < 32 || p[2] > 4096 || p[3] > 4096)
                throw new ArgumentException("ROI must be at least 16x32, within a 4096x4096 image.");
            return new Roi { X = p[0], Y = p[1], Width = p[2], Height = p[3] };
        }
    }

    private sealed class LockRequest { public int Index; public double At, DecodedAt; }

    private sealed class Boundary
    {
        public int? PreviousIndex, CandidateRow, Row;
        public bool Consecutive;
        public double? Confidence, Delta, Mean;
        public string Reason;
        public double[] Rows;
    }

    // A reproducible change-point estimate on raw gray pixels, independent of QR decoding success.
    // Fixed first-detected (or explicit) ROI keeps the row coordinate comparable over time.
    private static Boundary Difference(Frame previous, Frame current, Roi roi)
    {
        var b = new Boundary { PreviousIndex = previous?.Index };
        if (previous == null) { b.Reason = "first_frame"; return b; }
        b.Consecutive = current.Index == previous.Index + 1;
        if (!b.Consecutive) { b.Reason = "frame_gap"; return b; }
        if (current.Width != previous.Width || current.Height != previous.Height) { b.Reason = "size_changed"; return b; }
        if (roi == null) { b.Reason = "no_qr_roi"; return b; }
        if (roi.X < 0 || roi.Y < 0 || roi.Width < 16 || roi.Height < 32 ||
            roi.X + roi.Width > current.Width || roi.Y + roi.Height > current.Height)
        { b.Reason = "roi_out_of_bounds"; return b; }
        int n = roi.Height;
        var rows = new double[n];
        // Ignore the outer 10% horizontally; sample every second pixel to limit lab overhead.
        int x0 = roi.X + roi.Width / 10, x1 = roi.X + roi.Width - roi.Width / 10;
        double total = 0, totalSquares = 0;
        for (int y = 0; y < n; y++)
        {
            long sum = 0; int samples = 0;
            for (int x = x0; x < x1; x += 2)
            {
                int offset = (roi.Y + y) * current.Width + x;
                sum += Math.Abs(current.Gray[offset] - previous.Gray[offset]); samples++;
            }
            rows[y] = (double)sum / samples;
            total += rows[y]; totalSquares += rows[y] * rows[y];
        }
        b.Rows = rows; b.Mean = total / n;
        int margin = Math.Max(8, n / 10), best = margin;
        double prefix = 0, bestBetween = -1, bestDelta = 0;
        for (int k = 1; k < n; k++)
        {
            prefix += rows[k - 1];
            if (k < margin || k > n - margin) continue;
            double top = prefix / k, bottom = (total - prefix) / (n - k);
            double delta = bottom - top;
            double between = (double)k * (n - k) / n * delta * delta;
            if (between > bestBetween) { bestBetween = between; best = k; bestDelta = delta; }
        }
        double variance = Math.Max(0, totalSquares - total * total / n);
        b.CandidateRow = roi.Y + best;
        b.Delta = bestDelta; // signed: positive means the bottom changed more
        b.Confidence = variance > 1e-9 ? Math.Clamp(bestBetween / variance, 0, 1) : 0;
        if (Math.Abs(bestDelta) < 3) b.Reason = "low_contrast";
        // A best split at the search limit is censored: its location was imposed by our margin,
        // not located inside the image data. Preserve CandidateRow, but do not accept it as Row.
        else if (best <= margin + 2 || best >= n - margin - 2) b.Reason = "edge_limited";
        else if (b.Confidence < .15) b.Reason = "weak_split";
        else { b.Row = b.CandidateRow; b.Reason = "candidate_only"; }
        return b;
    }

    private static CameraInfo SelectCamera(string wanted)
    {
        var matches = MediaFoundationCamera.List().Where(c => wanted == null ?
            c.Name.Contains("C920", StringComparison.OrdinalIgnoreCase) :
            c.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase) || Diagnostics.Id(c.Id) == wanted).ToList();
        if (matches.Count != 1) throw new ArgumentException("Camera selection must match exactly one device. Use list.");
        return matches[0];
    }

    private static int Formats(string[] args)
    {
        string wanted = null, output = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) output = Path.GetFullPath(args[++i]);
            else if (!args[i].StartsWith("-") && wanted == null) wanted = args[i];
            else throw new ArgumentException("Expected formats [CAMERA] [--out FILE.json].");
        }
        if (output != null && File.Exists(output)) throw new ArgumentException("Formats output already exists.");
        var camera = SelectCamera(wanted);
        var formats = MediaFoundationCamera.ListFormats(camera.Id);
        var report = new { Schema = 1, AtUtc = DateTimeOffset.UtcNow, Camera = camera.Name, CameraId = Diagnostics.Id(camera.Id),
            Advertises60Fps = formats.Any(f => Math.Abs(f.Fps - 60) < .1),
            Formats = formats, Caveat = "Advertised native types only; throughput at a selected format requires capture." };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions(Json) { WriteIndented = true });
        if (output != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllText(output, json);
        }
        Console.WriteLine(json);
        return 0;
    }

    private static void BoundarySelfTest()
    {
        const int w = 100, h = 120;
        var a = new Frame { Index = 10, Width = w, Height = h, Gray = new byte[w * h] };
        var b = new Frame { Index = 11, Width = w, Height = h, Gray = new byte[w * h] };
        for (int y = 73; y < h; y++) Array.Fill(b.Gray, (byte)90, y * w, w);
        var roi = new Roi { X = 10, Y = 10, Width = 80, Height = 100 };
        var result = Difference(a, b, roi);
        Require(result.Row == 73 && result.Confidence > .999 && result.Delta == 90, "row boundary location");
        Require(Difference(b, b, roi).Reason == "frame_gap", "nonconsecutive frames rejected");
        var identical = new Frame { Index = 12, Width = w, Height = h, Gray = b.Gray };
        Require(Difference(b, identical, roi).Row == null, "identical images have no boundary");
        Require(Difference(a, b, null).Reason == "no_qr_roi", "missing ROI does not guess full image");
        var edge = new Frame { Index = 11, Width = w, Height = h, Gray = new byte[w * h] };
        for (int y = 12; y < h; y++) Array.Fill(edge.Gray, (byte)90, y * w, w);
        var censored = Difference(a, edge, roi);
        Require(censored.Reason == "edge_limited" && censored.CandidateRow.HasValue && censored.Row == null,
            "search-limit candidates are not reported as located boundaries");
        Require(Options.Parse(new[] { "capture", "unused", "1920", "30", "10", "--af-lock-after", "3" }).Focus == "auto", "AF-lock enables auto");
        var format = new CameraNativeFormat { FpsNumerator = 60000, FpsDenominator = 1001, Subtype = "47504a4d-0000-0010-8000-00aa00389b71" };
        Require(format.SubtypeCode == "MJPG" && format.Fps > 59.9 && format.Fps < 60, "native format rational fps/FourCC");
    }

    private static void ControlsSelfTest()
    {
        var states = new Dictionary<int, CameraControlState>
        {
            [4] = new() { Supported = true, Minimum = -11, Maximum = -2, Step = 1, Value = -7, Capabilities = 3, Flags = 2 },
            [6] = new() { Supported = true, Minimum = 0, Maximum = 250, Step = 1, Value = 20, Capabilities = 3, Flags = 2 },
            [9] = new() { Supported = true, Minimum = 0, Maximum = 255, Step = 1, Value = 150, Capabilities = 2, Flags = 2 }
        };
        using var bytes = new MemoryStream();
        using var log = new StreamWriter(bytes);
        var options = new Options { Exposure = "auto", Focus = "auto", AfLockAfter = 3 };
        var controls = new Controls(options, log, Stopwatch.StartNew(),
            (property, _) => states[property],
            (_, property, value, _) =>
            {
                var state = states[property]; state.Automatic = !value.HasValue;
                state.Flags = value.HasValue ? 2 : 1; state.Value = value ?? state.Value;
                states[property] = state;
                if (property == 4 && !value.HasValue)
                {
                    var gain = states[9]; gain.Value = 144; states[9] = gain;
                }
                return true;
            });
        controls.Apply();
        Require(states[4].Automatic && states[6].Automatic && states[9].Value == 144, "simulated coupled controls");
        var focused = states[6]; focused.Value = 34; states[6] = focused;
        controls.LockFocus(new LockRequest { Index = 90, At = 3000, DecodedAt = 3010 });
        Require(controls.AfLockAccepted && controls.LockedFocusValue == 34 && !states[6].Automatic && states[6].Value == 34, "AF locks current readback");
        controls.Sample();
        Require(controls.RequestedSettingsVerified, "AF sample compares locked value, not old auto mode");
        focused = states[6]; focused.Value = 35; states[6] = focused;
        controls.Sample();
        Require(!controls.RequestedSettingsVerified, "manual drift is reported");
        controls.Restore();
        Require(states[4].Value == -7 && !states[4].Automatic && states[6].Value == 20 && states[9].Value == 150 &&
            controls.RestorationSucceeded, "restore uses all pre-change snapshots, including coupled keep gain");
    }
}
