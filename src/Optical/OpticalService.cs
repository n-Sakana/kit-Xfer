using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ZXing;
using ZXing.Common;
using ZXing.QrCode.Internal;

namespace Xfer
{
    internal sealed class OpticalService : IDisposable
    {
        private const int MinimumFrameBytes = 64;
        private const int MaximumFrameBytes = 2953;
        private const int FrameHeaderLength = 20;
        private static readonly int[] FrameBytesOptions =
            { 500, 1000, 1465, 1850, 2331, 2953 };

        public void Dispose()
        {
            lock (_gate) {
                _disposed = true;
                foreach (var session in _sessions.Values) session.Dispose();
                _sessions.Clear();
            }
        }

        private bool _disposed;
        private readonly object _gate = new object();
        // A remote command can reach more than one display. Do not invalidate
        // another display's token when a new display starts.
        private readonly Dictionary<string, OpticalSession> _sessions =
            new Dictionary<string, OpticalSession>(StringComparer.Ordinal);

        public OpticalStartResult Start(
            FolderSnapshot source,
            IList<string> selectedNames,
            int frameBytes,
            int framesPerSecond,
            string errorCorrection)
        {
            var stamp = Diagnostics.Stamp();
            var preparation = Guid.NewGuid().ToString("N");
            Diagnostics.Event("send.prepare.start", preparation, new { FrameBytes = frameBytes, Fps = framesPerSecond,
                Files = selectedNames == null ? 0 : selectedNames.Count });
            try { return StartCore(source, selectedNames, frameBytes, framesPerSecond, errorCorrection, preparation); }
            catch (Exception error) { Diagnostics.Failure("send.prepare", preparation, error); throw; }
            finally { Diagnostics.Event("send.prepare.end", preparation, new { Ms = Diagnostics.Milliseconds(stamp) }); }
        }

        private OpticalStartResult StartCore(FolderSnapshot source, IList<string> selectedNames,
            int frameBytes, int framesPerSecond, string errorCorrection, string preparation)
        {
            if (frameBytes < MinimumFrameBytes || frameBytes > MaximumFrameBytes)
            {
                throw new ArgumentException("Invalid bytes per frame.", "frameBytes");
            }
            if (framesPerSecond != 10
                && framesPerSecond != 15
                && framesPerSecond != 20
                && framesPerSecond != 24
                && framesPerSecond != 30
                && framesPerSecond != 60)
            {
                throw new ArgumentException("Invalid frame rate.", "framesPerSecond");
            }
            if (errorCorrection != "L"
                && errorCorrection != "M"
                && errorCorrection != "Q"
                && errorCorrection != "H")
            {
                throw new ArgumentException("Invalid error correction level.", "errorCorrection");
            }

            var payload = OpticalPayload.Build(
                source,
                selectedNames,
                preparation);
            frameBytes = Math.Min(frameBytes, MaximumBytesForCorrection(errorCorrection));
            // Tiny transfers need not be padded into a version-40 QR.
            frameBytes = Math.Max(MinimumFrameBytes,
                (int)Math.Min(frameBytes, payload.Length + FrameHeaderLength));
            if (payload.Streaming == null) frameBytes = RaiseFrameBytesIfNeeded((int)payload.Length, frameBytes,
                MaximumBytesForCorrection(errorCorrection));
            var session = new OpticalSession(
                payload,
                frameBytes,
                framesPerSecond,
                errorCorrection);
            try { session.ValidateFirstFrame(); } catch { session.Dispose(); throw; }

            lock (_gate)
            {
                if (_disposed) { session.Dispose(); throw new ObjectDisposedException("OpticalService"); }
                PruneSessions();
                if (_sessions.Count >= 4)
                {
                    session.Dispose();
                    throw new ArgumentException("Too many QR displays are active. Close an unused display.");
                }
                _sessions.Add(session.Describe().Token, session);
            }

            Diagnostics.Event("send.ready", session.TransferId, new { Preparation = preparation, Files = payload.FileCount,
                OriginalBytes = payload.OriginalBytes, TransmittedBytes = payload.Length,
                FrameBytes = frameBytes, Fps = framesPerSecond, Ec = errorCorrection, QrVersion = session.Describe().QrVersion });
            return session.Describe();
        }

        /// <summary>Starts an FX2 display session: every patch carries one fountain frame of the size the
        /// error-correction level leaves (see Fx2Code). FX2 frames are small, so a session may have more blocks than
        /// a QR transfer (OpticalFrame.MaximumBlocks).</summary>
        public OpticalStartResult StartFx2(FolderSnapshot source, IList<string> selectedNames, int framesPerSecond, string errorCorrection, bool color = false)
        {
            var stamp = Diagnostics.Stamp();
            var preparation = Guid.NewGuid().ToString("N");
            Diagnostics.Event("send.prepare.start", preparation, new { Layout = "fx2", Fps = framesPerSecond, Ec = errorCorrection,
                Files = selectedNames == null ? 0 : selectedNames.Count });
            try
            {
                if (framesPerSecond < 1 || framesPerSecond > 60) throw new ArgumentException("Invalid frame rate.", "framesPerSecond");
                var code = Fx2Code.ForLevel(Fx2Code.ParseLevel(errorCorrection));
                var payload = OpticalPayload.Build(source, selectedNames, preparation);
                var session = new OpticalSession(payload, code.FrameBytes + (color ? Fx2Code.ForColor(code.Level).FrameBytes - 4 : 0), framesPerSecond, errorCorrection, true);
                lock (_gate)
                {
                    if (_disposed) { session.Dispose(); throw new ObjectDisposedException("OpticalService"); }
                    PruneSessions();
                    if (_sessions.Count >= 4) { session.Dispose(); throw new ArgumentException("Too many QR displays are active. Close an unused display."); }
                    _sessions.Add(session.Describe().Token, session);
                }
                Diagnostics.Event("send.ready", session.TransferId, new { Preparation = preparation, Layout = "fx2", Files = payload.FileCount,
                    OriginalBytes = payload.OriginalBytes, TransmittedBytes = payload.Length, FrameBytes = session.Describe().FrameBytes, Color = color,
                    Fps = framesPerSecond, Ec = errorCorrection });
                return session.Describe();
            }
            catch (Exception error) { Diagnostics.Failure("send.prepare", preparation, error); throw; }
            finally { Diagnostics.Event("send.prepare.end", preparation, new { Ms = Diagnostics.Milliseconds(stamp) }); }
        }

        /// <summary>The fountain frame (header and block) for a sequence number, as an FX2 patch carries it.</summary>
        public bool TryFrame(string token, ulong sequence, out byte[] frame)
        {
            OpticalSession session;
            lock (_gate) { session = FindSession(token); }
            frame = session == null ? null : session.Frame(sequence);
            return frame != null;
        }

        private static int MaximumBytesForCorrection(string correction)
        {
            // QR version 40, byte mode, without ECI (same as CreateQr).
            return correction == "H" ? 1273 : correction == "Q" ? 1663
                : correction == "M" ? 2331 : 2953;
        }

        private static int RaiseFrameBytesIfNeeded(int payloadLength, int requestedFrameBytes, int maximum)
        {
            if (FitsInOneStream(payloadLength, requestedFrameBytes))
            {
                return requestedFrameBytes;
            }

            foreach (var offered in FrameBytesOptions)
            {
                if (offered >= requestedFrameBytes && offered <= maximum && FitsInOneStream(payloadLength, offered))
                {
                    return offered;
                }
            }

            if (FitsInOneStream(payloadLength, maximum))
            {
                return maximum;
            }
            throw new ArgumentException(
                "The selection exceeds the transfer capacity at this error correction level. Split it into smaller selections.");
        }

        private static bool FitsInOneStream(int payloadLength, int frameBytes)
        {
            var blockLength = frameBytes - FrameHeaderLength;
            if (blockLength <= 0)
            {
                return false;
            }
            var blockCount = (payloadLength + (long)blockLength - 1L) / blockLength;
            return blockCount <= ushort.MaxValue;
        }

        public static byte[] RenderTextQr(string value)
        {
            return OpticalSession.RenderTextQr(value);
        }

        public bool TryRenderFrame(
            string token,
            ulong sequence,
            out byte[] svg)
        {
            OpticalSession session;
            lock (_gate)
            {
                session = FindSession(token);
            }

            if (session == null || !session.Matches(token))
            {
                svg = null;
                return false;
            }

            svg = session.RenderFrame(sequence);
            return true;
        }

        public bool TryRenderRasterFrame(
            string token,
            ulong sequence,
            out byte[] rgba)
        {
            OpticalSession session;
            lock (_gate)
            {
                session = FindSession(token);
            }

            if (session == null || !session.Matches(token))
            {
                rgba = null;
                return false;
            }

            rgba = session.RenderRasterFrame(sequence);
            return true;
        }

        public void Stop(string token)
        {
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(token))
                {
                    OpticalSession ended;
                    if (_sessions.TryGetValue(token, out ended)) { Diagnostics.Event("send.stopped", ended.TransferId, null); ended.Dispose(); }
                    _sessions.Remove(token);
                }
            }
        }

        public string DiagnosticTransfer(string token)
        {
            lock (_gate) {
                OpticalSession session;
                return token != null && _sessions.TryGetValue(token, out session) ? session.TransferId : "";
            }
        }

        public bool TryRenderPackedFrames(string token, ulong sequence, int count, out byte[] packed)
        {
            if (count < 1 || count > 16)
            {
                throw new ArgumentException("Invalid QR prefetch count.", "count");
            }
            OpticalSession session;
            lock (_gate) { session = FindSession(token); }
            packed = session == null ? null : session.RenderPackedFrames(sequence, count);
            return packed != null;
        }

        private OpticalSession FindSession(string token)
        {
            if (_disposed) return null;
            PruneSessions();
            OpticalSession session;
            if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out session))
            {
                return null;
            }
            session.LastUsedUtc = DateTime.UtcNow;
            return session;
        }

        private void PruneSessions()
        {
            var expired = new List<string>();
            foreach (var entry in _sessions)
            {
                if (DateTime.UtcNow - entry.Value.LastUsedUtc > TimeSpan.FromMinutes(10))
                    expired.Add(entry.Key);
            }
            foreach (var token in expired) {
                Diagnostics.Event("send.expired", _sessions[token].TransferId, null);
                _sessions[token].Dispose();
                _sessions.Remove(token);
            }
        }

        internal static uint FountainSweepDigest()
        {
            var hash = 0x811c9dc5u;
            for (var k = 1; k <= 65535; k++)
            {
                HashDouble(ref hash, FountainMath.DLog(2.0 * k));
            }
            for (var index = 64; index < 64 * 4096; index++)
            {
                HashDouble(ref hash, FountainMath.DLog(index / 64.0));
            }
            return hash;
        }

        private static void HashDouble(ref uint hash, double value)
        {
            var bytes = BitConverter.GetBytes(value);
            for (var index = 0; index < bytes.Length; index++)
            {
                hash ^= bytes[index];
                hash = unchecked(hash * 0x01000193u);
            }
        }
    }

    internal sealed class OpticalSession : IDisposable
    {
        private const int FrameHeaderLength = 20;
        private const int QuietZone = 4;

        private readonly string _token;
        private readonly ushort _sessionId;
        private readonly int _frameBytes;
        private readonly int _framesPerSecond;
        private readonly string _errorCorrectionName;
        private readonly ErrorCorrectionLevel _errorCorrection;
        private readonly byte[] _payload;
        private readonly StreamingEncoder _streaming;
        private readonly long _length;
        private readonly FountainEncoder _encoder;
        private readonly uint _payloadFnv;
        private readonly bool _fx2;
        private int _qrVersion;

        public OpticalSession(
            OpticalPayload payload,
            int frameBytes,
            int framesPerSecond,
            string errorCorrection,
            bool fx2 = false)
        {
            _token = Guid.NewGuid().ToString("N");
            LastUsedUtc = DateTime.UtcNow;
            _sessionId = NewSessionId();
            _frameBytes = frameBytes;
            _framesPerSecond = framesPerSecond;
            _errorCorrectionName = errorCorrection;
            _errorCorrection = fx2 ? ErrorCorrectionLevel.L : ParseErrorCorrection(errorCorrection);
            _fx2 = fx2;
            _payload = payload.Bytes;
            _length = payload.Length;
            Label = payload.Label; FileCount = payload.FileCount; OriginalBytes = payload.OriginalBytes;
            if (payload.Streaming != null)
            {
                _streaming = new StreamingEncoder(payload.Streaming, frameBytes);
                TransferId = _streaming.Id;
                return;
            }
            _encoder = new FountainEncoder(
                _payload,
                frameBytes - FrameHeaderLength,
                _sessionId);
            TransferId = Diagnostics.Transfer(_sessionId, _encoder.BlockCount, _encoder.BlockLength, _payload.Length);
            // QR frames (and the phone's reader) count blocks in 16 bits; FX2 frames carry the low 16 bits and the
            // receiver derives the count from the lengths.
            if (_encoder.BlockCount > (fx2 ? OpticalFrame.MaximumBlocks : ushort.MaxValue))
            {
                throw new ArgumentException(
                    "The selection is too large for these settings. Increase the bytes per frame.");
            }
            _payloadFnv = Fnv1a(_payload);
            Label = payload.Label;
            FileCount = payload.FileCount;
            OriginalBytes = payload.OriginalBytes;
        }

        public void Dispose() { if (_streaming != null) _streaming.Dispose(); }

        public string TransferId { get; private set; }
        public DateTime LastUsedUtc { get; set; }
        public string Label { get; private set; }
        public long FileCount { get; private set; }
        public long OriginalBytes { get; private set; }

        public bool Matches(string token)
        {
            return !string.IsNullOrEmpty(token)
                && string.Equals(token, _token, StringComparison.Ordinal);
        }

        public void ValidateFirstFrame()
        {
            byte[] frame;
            QRCode qr;
            Encode(0, out frame, out qr);
            ValidateQrRoundTrip(frame, qr);
            _qrVersion = qr.Version.VersionNumber;
        }

        public OpticalStartResult Describe()
        {
            var result = new OpticalStartResult(
                _token,
                Label,
                FileCount,
                OriginalBytes,
                _length,
                _frameBytes,
                _framesPerSecond,
                _errorCorrectionName,
                _streaming == null ? _encoder.BlockCount : _streaming.Blocks,
                _qrVersion,
                (double)(_streaming == null ? _encoder.BlockCount : _streaming.Blocks) / _framesPerSecond);
            result.TransferId = TransferId;
            return result;
        }

        /// <summary>The fountain frame for a sequence number without a QR code (FX2 patches).</summary>
        public byte[] Frame(ulong sequence)
        {
            if (_streaming != null) return _streaming.Frame(sequence);
            var block = _encoder.Encode(unchecked((uint)sequence));
            var frame = new byte[FrameHeaderLength + block.Length];
            WriteHeader(frame, sequence);
            Buffer.BlockCopy(block, 0, frame, FrameHeaderLength, block.Length);
            return frame;
        }

        private void WriteHeader(byte[] frame, ulong sequence)
        {
            frame[0] = 0xd1;
            frame[1] = 0x0c;
            WriteUInt16(frame, 2, _sessionId);
            WriteUInt32(frame, 4, unchecked((uint)sequence));
            WriteUInt16(frame, 8, _fx2 ? unchecked((ushort)_encoder.BlockCount) : checked((ushort)_encoder.BlockCount));
            WriteUInt16(frame, 10, checked((ushort)_encoder.BlockLength));
            WriteUInt32(frame, 12, checked((uint)_payload.Length));
            WriteUInt32(frame, 16, _payloadFnv);
        }

        public byte[] RenderFrame(ulong sequence)
        {
            byte[] frame;
            QRCode qr;
            Encode(sequence, out frame, out qr);
            return RenderSvg(qr.Matrix, QuietZone);
        }

        public byte[] RenderRasterFrame(ulong sequence)
        {
            byte[] frame;
            QRCode qr;
            Encode(sequence, out frame, out qr);
            return RenderRgba(qr.Matrix, QuietZone);
        }

        // HTTP bitmap transport; independent of the optical frame format.
        // FQR1 | side:u16 LE | count:u16 LE | firstSequence:u32 LE | bitmaps.
        // Each bitmap is row-major, MSB first, one black=1 bit per module, quiet zone included.
        public byte[] RenderPackedFrames(ulong sequence, int count)
        {
            var side = 17 + 4 * _qrVersion + QuietZone * 2;
            var stride = (side * side + 7) / 8;
            var result = new byte[checked(12 + stride * count)];
            result[0] = 0x46; result[1] = 0x51; result[2] = 0x52; result[3] = 0x31;
            WriteUInt16(result, 4, checked((ushort)side));
            WriteUInt16(result, 6, checked((ushort)count));
            WriteUInt32(result, 8, unchecked((uint)sequence));
            for (var frameIndex = 0; frameIndex < count; frameIndex++)
            {
                byte[] frame;
                QRCode qr;
                Encode(unchecked(sequence + (uint)frameIndex), out frame, out qr);
                var offset = 12 + frameIndex * stride;
                for (var y = 0; y < qr.Matrix.Height; y++)
                {
                    for (var x = 0; x < qr.Matrix.Width; x++)
                    {
                        if (qr.Matrix[x, y] == 0) continue;
                        var bit = (y + QuietZone) * side + x + QuietZone;
                        result[offset + bit / 8] |= (byte)(0x80 >> (bit % 8));
                    }
                }
            }
            return result;
        }

        internal static byte[] RenderTextQr(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("The text to encode as a QR code is empty.", "value");
            }

            var expected = Encoding.UTF8.GetBytes(value);
            var qr = CreateQr(expected, ErrorCorrectionLevel.L, 0);
            ValidateQrRoundTrip(expected, qr);
            return RenderSvg(qr.Matrix, QuietZone);
        }

        private void Encode(ulong sequence, out byte[] frame, out QRCode qr)
        {
            var stamp = Diagnostics.Stamp();
            try { EncodeCore(sequence, out frame, out qr); }
            catch (Exception error) { Diagnostics.Failure("send.qr", TransferId, error); throw; }
            finally { Diagnostics.Sample("send.qr", TransferId, Diagnostics.Milliseconds(stamp), new { Sequence = sequence }); }
        }

        private void EncodeCore(ulong sequence, out byte[] frame, out QRCode qr)
        {
            frame = Frame(sequence);
            qr = CreateQr(frame, _errorCorrection, _qrVersion);
            if (_qrVersion > 0 && qr.Version.VersionNumber != _qrVersion)
            {
                throw new InvalidOperationException("Could not use a fixed QR version.");
            }
        }

        private static QRCode CreateQr(
            byte[] frame,
            ErrorCorrectionLevel errorCorrection,
            int version)
        {
            var hints = new Dictionary<EncodeHintType, object>();
            hints[EncodeHintType.CHARACTER_SET] = "ISO-8859-1";
            hints[EncodeHintType.DISABLE_ECI] = true;
            hints[EncodeHintType.QR_MASK_PATTERN] = 4;
            if (version > 0)
            {
                hints[EncodeHintType.QR_VERSION] = version;
            }
            var content = Encoding.GetEncoding(28591).GetString(frame);
            try
            {
                return ZXing.QrCode.Internal.Encoder.encode(
                    content,
                    errorCorrection,
                    hints);
            }
            catch (WriterException exception)
            {
                throw new ArgumentException(
                    "The data does not fit in a QR code at these settings. Reduce the bytes per frame.",
                    exception);
            }
        }

        private static ErrorCorrectionLevel ParseErrorCorrection(string value)
        {
            if (value == "M")
            {
                return ErrorCorrectionLevel.M;
            }
            if (value == "Q")
            {
                return ErrorCorrectionLevel.Q;
            }
            if (value == "H")
            {
                return ErrorCorrectionLevel.H;
            }
            return ErrorCorrectionLevel.L;
        }

        private static void ValidateQrRoundTrip(byte[] expected, QRCode qr)
        {
            var source = qr.Matrix;
            var bits = new BitMatrix(source.Width, source.Height);
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    if (source[x, y] != 0)
                    {
                        bits[x, y] = true;
                    }
                }
            }

            var decoded = new ZXing.QrCode.Internal.Decoder().decode(bits, null);
            var segments = decoded == null ? null : decoded.ByteSegments;
            if (segments == null || segments.Count == 0)
            {
                throw new InvalidOperationException("Could not verify the generated QR code.");
            }

            var actualLength = 0;
            foreach (var segment in segments)
            {
                actualLength = checked(actualLength + segment.Length);
            }
            if (actualLength != expected.Length)
            {
                throw new InvalidOperationException("QR verification failed: length mismatch.");
            }

            var offset = 0;
            foreach (var segment in segments)
            {
                for (var index = 0; index < segment.Length; index++)
                {
                    if (segment[index] != expected[offset++])
                    {
                        throw new InvalidOperationException("QR verification failed: content mismatch.");
                    }
                }
            }
        }

        private static byte[] RenderSvg(ByteMatrix matrix, int quietZone)
        {
            var side = matrix.Width + quietZone * 2;
            var builder = new StringBuilder(side * side * 3);
            builder.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ");
            builder.Append(side.ToString(CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(side.ToString(CultureInfo.InvariantCulture));
            builder.Append("\" shape-rendering=\"crispEdges\" role=\"img\" aria-label=\"Transfer QR code\">");
            builder.Append("<rect width=\"100%\" height=\"100%\" fill=\"#fff\"/><path fill=\"#000\" d=\"");

            for (var y = 0; y < matrix.Height; y++)
            {
                var x = 0;
                while (x < matrix.Width)
                {
                    while (x < matrix.Width && matrix[x, y] == 0)
                    {
                        x++;
                    }
                    if (x >= matrix.Width)
                    {
                        break;
                    }

                    var start = x;
                    while (x < matrix.Width && matrix[x, y] != 0)
                    {
                        x++;
                    }
                    var length = x - start;
                    builder.Append('M');
                    builder.Append((start + quietZone).ToString(CultureInfo.InvariantCulture));
                    builder.Append(' ');
                    builder.Append((y + quietZone).ToString(CultureInfo.InvariantCulture));
                    builder.Append('h');
                    builder.Append(length.ToString(CultureInfo.InvariantCulture));
                    builder.Append("v1h-");
                    builder.Append(length.ToString(CultureInfo.InvariantCulture));
                    builder.Append('z');
                }
            }

            builder.Append("\"/></svg>");
            return new UTF8Encoding(false).GetBytes(builder.ToString());
        }

        private static byte[] RenderRgba(ByteMatrix matrix, int quietZone)
        {
            var side = matrix.Width + quietZone * 2;
            var pixels = new byte[checked(side * side * 4)];
            for (var index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = 255;
                pixels[index + 1] = 255;
                pixels[index + 2] = 255;
                pixels[index + 3] = 255;
            }

            for (var y = 0; y < matrix.Height; y++)
            {
                for (var x = 0; x < matrix.Width; x++)
                {
                    if (matrix[x, y] == 0)
                    {
                        continue;
                    }

                    var offset = ((y + quietZone) * side + x + quietZone) * 4;
                    pixels[offset] = 0;
                    pixels[offset + 1] = 0;
                    pixels[offset + 2] = 0;
                }
            }

            return pixels;
        }

        private static ushort NewSessionId()
        {
            var bytes = new byte[2];
            using (var random = RandomNumberGenerator.Create())
            {
                do
                {
                    random.GetBytes(bytes);
                }
                while (bytes[0] == 0 && bytes[1] == 0);
            }
            return (ushort)(bytes[0] | (bytes[1] << 8));
        }

        private static uint Fnv1a(byte[] bytes)
        {
            var hash = 0x811c9dc5u;
            for (var index = 0; index < bytes.Length; index++)
            {
                hash ^= bytes[index];
                hash = unchecked(hash * 0x01000193u);
            }
            return hash;
        }

        private static void WriteUInt16(byte[] target, int offset, ushort value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
            target[offset + 2] = (byte)(value >> 16);
            target[offset + 3] = (byte)(value >> 24);
        }
    }

    internal sealed class FountainEncoder
    {
        private readonly int _words;
        private readonly uint[] _blocks;
        private readonly double[] _cdf;
        private readonly ushort _sessionId;

        public FountainEncoder(byte[] payload, int blockLength, ushort sessionId)
        {
            if (payload == null || payload.Length == 0)
            {
                throw new ArgumentException("The transfer payload is empty.", "payload");
            }
            if (blockLength <= 0)
            {
                throw new ArgumentOutOfRangeException("blockLength");
            }

            BlockLength = blockLength;
            BlockCount = Math.Max(1, (payload.Length + blockLength - 1) / blockLength);
            _words = (blockLength + 3) / 4;
            _blocks = new uint[checked(BlockCount * _words)];
            _cdf = FountainMath.SolitonCdf(BlockCount);
            _sessionId = sessionId;

            for (var index = 0; index < payload.Length; index++)
            {
                var block = index / blockLength;
                var within = index % blockLength;
                var word = within / 4;
                var shift = (within % 4) * 8;
                _blocks[block * _words + word] |= (uint)payload[index] << shift;
            }
        }

        public int BlockLength { get; private set; }
        public int BlockCount { get; private set; }

        public byte[] Encode(uint sequence)
        {
            var indices = FountainMath.FrameIndices(
                BlockCount,
                _cdf,
                _sessionId,
                sequence);
            var words = new uint[_words];
            foreach (var block in indices)
            {
                var offset = block * _words;
                for (var word = 0; word < _words; word++)
                {
                    words[word] ^= _blocks[offset + word];
                }
            }

            var result = new byte[BlockLength];
            for (var index = 0; index < result.Length; index++)
            {
                var word = words[index / 4];
                result[index] = (byte)(word >> ((index % 4) * 8));
            }
            return result;
        }
    }

    internal static class FountainMath
    {
        private const double Ln2 = 0.6931471805599453;
        private const double SolitonC = 0.1;
        private const double SolitonDelta = 0.5;

        public static double DLog(double value)
        {
            var exponent = 0;
            var mantissa = value;
            while (mantissa >= 1.5)
            {
                mantissa /= 2.0;
                exponent++;
            }
            while (mantissa < 0.75)
            {
                mantissa *= 2.0;
                exponent--;
            }

            var z = (mantissa - 1.0) / (mantissa + 1.0);
            var z2 = z * z;
            var term = z;
            var sum = 0.0;
            for (var number = 1; number <= 21; number += 2)
            {
                sum += term / number;
                term *= z2;
            }
            return exponent * Ln2 + 2.0 * sum;
        }

        public static double[] SolitonCdf(int blockCount)
        {
            var cdf = new double[blockCount];
            if (blockCount == 1)
            {
                cdf[0] = 1.0;
                return cdf;
            }

            var r = Math.Max(
                1.0,
                SolitonC * DLog(blockCount / SolitonDelta) * Math.Sqrt(blockCount));
            var spike = Math.Min(blockCount, (int)Math.Ceiling(blockCount / r));
            var total = 0.0;
            for (var degree = 1; degree <= blockCount; degree++)
            {
                var rho = degree == 1
                    ? 1.0 / blockCount
                    : 1.0 / (degree * (double)(degree - 1));
                var tau = 0.0;
                if (degree < spike)
                {
                    tau = r / (degree * blockCount);
                }
                else if (degree == spike)
                {
                    tau = r * Math.Max(0.0, DLog(r / SolitonDelta)) / blockCount;
                }
                total += rho + tau;
                cdf[degree - 1] = total;
            }
            for (var index = 0; index < blockCount; index++)
            {
                cdf[index] /= total;
            }
            cdf[blockCount - 1] = 1.0;
            return cdf;
        }

        public static List<int> FrameIndices(
            int blockCount,
            double[] cdf,
            ushort sessionId,
            uint sequence)
        {
            var random = new SplitMix32(FrameSeed(sessionId, sequence));
            var sample = random.Next() * (1.0 / 4294967296.0);
            var low = 0;
            var high = blockCount - 1;
            while (low < high)
            {
                var middle = (low + high) >> 1;
                if (cdf[middle] >= sample)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }
            var degree = Math.Min(blockCount, low + 1);

            if (degree > (blockCount >> 3))
            {
                var scratch = new int[blockCount];
                for (var index = 0; index < blockCount; index++)
                {
                    scratch[index] = index;
                }
                var result = new List<int>(degree);
                for (var index = 0; index < degree; index++)
                {
                    var pick = index + (int)(random.Next() % (uint)(blockCount - index));
                    var value = scratch[index];
                    scratch[index] = scratch[pick];
                    scratch[pick] = value;
                    result.Add(scratch[index]);
                }
                return result;
            }

            var seen = new HashSet<int>();
            var ordered = new List<int>(degree);
            while (ordered.Count < degree)
            {
                var value = (int)(random.Next() % (uint)blockCount);
                if (seen.Add(value))
                {
                    ordered.Add(value);
                }
            }
            return ordered;
        }

        private static uint FrameSeed(ushort sessionId, uint sequence)
        {
            var hash = unchecked(((uint)sessionId + 1u) * 0x9e3779b1u)
                ^ unchecked(sequence + 0x85ebca6bu);
            hash = unchecked((hash ^ (hash >> 13)) * 0xc2b2ae35u);
            return hash ^ (hash >> 16);
        }

        private sealed class SplitMix32
        {
            private uint _state;

            public SplitMix32(uint seed)
            {
                _state = seed;
            }

            public uint Next()
            {
                _state = unchecked(_state + 0x9e3779b9u);
                var value = _state ^ (_state >> 16);
                value = unchecked(value * 0x21f0aaadu);
                value ^= value >> 15;
                value = unchecked(value * 0x735a2d97u);
                value ^= value >> 15;
                return value;
            }
        }
    }

    internal sealed class OpticalPayload
    {
        private const int BundleHeaderLength = 8;
        private const int FileHeaderLength = 49;
        private static readonly byte[] BundleMagic = { 0x44, 0x43, 0x42, 0x31 };
        private static readonly byte[] FileMagic = { 0x44, 0x43, 0x46, 0x32 };
        private const string BundleMediaType = "application/vnd.decimen.bundle+dcb1";

        private OpticalPayload(
            string label,
            long fileCount,
            long originalBytes,
            byte[] bytes)
        {
            Label = label;
            FileCount = fileCount;
            OriginalBytes = originalBytes;
            Bytes = bytes;
        }

        public string Label { get; private set; }
        public long FileCount { get; private set; }
        public long OriginalBytes { get; private set; }
        public byte[] Bytes { get; private set; }
        public StreamingBundle Streaming { get; private set; }
        public long Length { get { return Streaming == null ? Bytes.LongLength : Streaming.Length; } }

        public static OpticalPayload Build(
            FolderSnapshot source,
            IList<string> selectedNames,
            string preparation = "")
        {
            var stamp = Diagnostics.Stamp();
            Diagnostics.Event("send.bundle.start", preparation, null);
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }
            if (selectedNames == null || selectedNames.Count == 0)
            {
                throw new ArgumentException("Select at least one file to send.", "selectedNames");
            }

            var selected = Resolve(source, selectedNames);
            // Small selections use compressed bundles; larger selections use a disk-backed receiver.
            // These thresholds select a format rather than limit transfer size.
            long measured = 0, metadata = 0;
            foreach (var file in selected) { measured = checked(measured + file.Size); metadata += Encoding.UTF8.GetByteCount(file.Name) + 160L; }
            if (measured > 8 * 1024 * 1024 || metadata > 512 * 1024 || selected.Count > 2000)
            {
                var stream = new StreamingBundle(source, selectedNames);
                return new OpticalPayload(stream.Label, stream.FileCount, stream.OriginalBytes, null) { Streaming = stream };
            }

            long total = 0;
            var manifestFiles = new List<object>();
            var contents = new List<byte[]>();
            foreach (var file in selected)
            {
                var readStamp = Diagnostics.Stamp();
                byte[] bytes;
                using (var input = File.OpenRead(file.FullPath))
                {
                    if (input.Length != file.Size || input.Length > 8 * 1024 * 1024 - total)
                        throw new IOException("A file changed during transfer preparation.");
                    bytes = new byte[(int)input.Length];
                    StreamingWire.ReadExactly(input, bytes, 0, bytes.Length);
                    if (input.ReadByte() != -1) throw new IOException("A file changed during transfer preparation.");
                }
                Diagnostics.Sample("send.read_file", preparation, Diagnostics.Milliseconds(readStamp), new { Bytes = bytes.Length });
                total = checked(total + bytes.LongLength);
                if (total > 8 * 1024 * 1024) throw new IOException("A file changed during transfer preparation.");
                contents.Add(bytes);
                manifestFiles.Add(new Dictionary<string, object>
                {
                    { "path", ValidateEntryName(file.Name) },
                    { "size", bytes.LongLength },
                    { "sha256", Sha256Hex(bytes) }
                });
            }

            var label = SafeLabel(Path.GetFileName(
                source.DirectoryPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)));
            var manifest = new Dictionary<string, object>
            {
                { "v", 1 },
                { "label", label },
                { "count", selected.Count },
                { "totalSize", total },
                { "files", manifestFiles }
            };
            var manifestBytes = JsonCodec.Serialize(manifest);
            if (manifestBytes.Length > 1024 * 1024)
            {
                throw new ArgumentException("The file name list is too large.");
            }

            var bundleLength = checked(BundleHeaderLength + manifestBytes.Length + (int)total);
            var bundle = new byte[bundleLength];
            Buffer.BlockCopy(BundleMagic, 0, bundle, 0, BundleMagic.Length);
            WriteUInt32(bundle, 4, checked((uint)manifestBytes.Length));
            Buffer.BlockCopy(manifestBytes, 0, bundle, BundleHeaderLength, manifestBytes.Length);
            var offset = BundleHeaderLength + manifestBytes.Length;
            foreach (var bytes in contents)
            {
                Buffer.BlockCopy(bytes, 0, bundle, offset, bytes.Length);
                offset += bytes.Length;
            }

            Diagnostics.Event("send.bundle.ok", preparation, new { Ms = Diagnostics.Milliseconds(stamp), Bytes = bundle.Length, Files = selected.Count });
            var fileContainer = PackFile(label + ".dcb1", BundleMediaType, bundle, preparation);
            return new OpticalPayload(label, selected.Count, total, fileContainer);
        }

        internal static List<FolderFile> Resolve(
            FolderSnapshot source,
            IList<string> names)
        {
            var byName = new Dictionary<string, FolderFile>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var file in source.Files)
            {
                byName[file.Name] = file;
            }

            var result = new List<FolderFile>();
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var name in names)
            {
                FolderFile file;
                if (string.IsNullOrWhiteSpace(name) || !byName.TryGetValue(name, out file))
                {
                    throw new FileNotFoundException(
                        string.Format("Selected file not found: {0}", name));
                }
                if (seen.Add(file.Name))
                {
                    result.Add(file);
                }
            }
            return result;
        }

        private static byte[] PackFile(string name, string mediaType, byte[] bytes, string preparation)
        {
            var stamp = Diagnostics.Stamp();
            Diagnostics.Event("send.compress.start", preparation, new { Bytes = bytes.Length });
            var transmitted = CompressIfUseful(bytes);
            var compressed = !object.ReferenceEquals(transmitted, bytes);
            Diagnostics.Event("send.compress.ok", preparation, new { Compressed = compressed, Bytes = transmitted.Length, Ms = Diagnostics.Milliseconds(stamp) });
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var typeBytes = Encoding.UTF8.GetBytes(mediaType);
            var result = new byte[checked(
                FileHeaderLength + nameBytes.Length + typeBytes.Length + transmitted.Length)];
            Buffer.BlockCopy(FileMagic, 0, result, 0, FileMagic.Length);
            result[4] = compressed ? (byte)1 : (byte)0;
            WriteUInt16(result, 5, checked((ushort)nameBytes.Length));
            WriteUInt16(result, 7, checked((ushort)typeBytes.Length));
            WriteUInt32(result, 9, checked((uint)bytes.Length));
            WriteUInt32(result, 13, checked((uint)transmitted.Length));
            Diagnostics.Event("send.hash.start", preparation, null);
            var digest = Sha256(bytes);
            Diagnostics.Event("send.hash.ok", preparation, null);
            Buffer.BlockCopy(digest, 0, result, 17, digest.Length);
            Buffer.BlockCopy(nameBytes, 0, result, FileHeaderLength, nameBytes.Length);
            Buffer.BlockCopy(
                typeBytes,
                0,
                result,
                FileHeaderLength + nameBytes.Length,
                typeBytes.Length);
            Buffer.BlockCopy(
                transmitted,
                0,
                result,
                FileHeaderLength + nameBytes.Length + typeBytes.Length,
                transmitted.Length);
            return result;
        }

        private static byte[] CompressIfUseful(byte[] bytes)
        {
            if (bytes.Length < 768)
            {
                return bytes;
            }
            using (var buffer = new MemoryStream())
            {
                using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, true))
                {
                    gzip.Write(bytes, 0, bytes.Length);
                }
                var compressed = buffer.ToArray();
                return compressed.Length + 64 < bytes.Length ? compressed : bytes;
            }
        }

        private static byte[] Sha256(byte[] bytes)
        {
            using (var hash = SHA256.Create())
            {
                return hash.ComputeHash(bytes);
            }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            var digest = Sha256(bytes);
            var text = new StringBuilder(digest.Length * 2);
            foreach (var value in digest)
            {
                text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        internal static string ValidateEntryName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)
                || name.Length > 200
                || name.StartsWith("/", StringComparison.Ordinal)
                || name.IndexOf('\\') >= 0
                || (name.Length >= 2 && char.IsLetter(name[0]) && name[1] == ':'))
            {
                throw new ArgumentException(
                    string.Format("Unsupported transfer file name: {0}", name));
            }

            string normalized;
            try
            {
                normalized = name.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                normalized = null;
            }
            if (!string.Equals(name, normalized, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    string.Format("Unsupported transfer file name: {0}", name));
            }

            var segments = name.Split('/');
            if (segments.Length > 32)
            {
                throw new ArgumentException(
                    string.Format("Unsupported transfer file name: {0}", name));
            }
            foreach (var segment in segments)
            {
                if (!IsSafePathSegment(segment))
                {
                    throw new ArgumentException(
                        string.Format("Unsupported transfer file name: {0}", name));
                }
            }
            return name;
        }

        private static bool IsSafePathSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment)
                || segment.Length > 180
                || segment == "."
                || segment == ".."
                || segment[0] == ' '
                || segment[segment.Length - 1] == '.'
                || segment[segment.Length - 1] == ' ')
            {
                return false;
            }

            for (var index = 0; index < segment.Length; index++)
            {
                var character = segment[index];
                if (character < 0x20
                    || character == 0x7f
                    || character == '<'
                    || character == '>'
                    || character == ':'
                    || character == '"'
                    || character == '|'
                    || character == '?'
                    || character == '*'
                    || character == '\\'
                    || character == '\u00ad'
                    || (character >= '\u200b' && character <= '\u200f')
                    || (character >= '\u202a' && character <= '\u202e')
                    || character == '\u2028'
                    || character == '\u2029'
                    || (character >= '\u2066' && character <= '\u2069')
                    || character == '\ufeff')
                {
                    return false;
                }
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= segment.Length || !char.IsLowSurrogate(segment[index + 1]))
                    {
                        return false;
                    }
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return false;
                }
            }

            var dot = segment.IndexOf('.');
            var baseName = (dot < 0 ? segment : segment.Substring(0, dot)).ToUpperInvariant();
            if (baseName == "CON" || baseName == "PRN" || baseName == "AUX" || baseName == "NUL")
            {
                return false;
            }
            if ((baseName.StartsWith("COM", StringComparison.Ordinal)
                    || baseName.StartsWith("LPT", StringComparison.Ordinal))
                && baseName.Length == 4
                && ((baseName[3] >= '0' && baseName[3] <= '9')
                    || baseName[3] == '\u00b9'
                    || baseName[3] == '\u00b2'
                    || baseName[3] == '\u00b3'))
            {
                return false;
            }
            return true;
        }

        internal static string SafeLabel(string value)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            invalid.Add('/');
            invalid.Add('\\');
            invalid.Add(':');
            invalid.Add('*');
            invalid.Add('?');
            invalid.Add('"');
            invalid.Add('<');
            invalid.Add('>');
            invalid.Add('|');
            var builder = new StringBuilder();
            foreach (var character in value ?? string.Empty)
            {
                if (!invalid.Contains(character) && !char.IsControl(character))
                {
                    builder.Append(character);
                }
            }
            var label = builder.ToString().Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(label) ? "Xfer" : label;
        }

        private static void WriteUInt16(byte[] target, int offset, ushort value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
            target[offset + 2] = (byte)(value >> 16);
            target[offset + 3] = (byte)(value >> 24);
        }
    }

    internal sealed class OpticalStartResult
    {
        public OpticalStartResult(
            string token,
            string label,
            long fileCount,
            long originalBytes,
            long transmittedBytes,
            int frameBytes,
            int framesPerSecond,
            string errorCorrection,
            long sourceBlocks,
            int qrVersion,
            double minimumSeconds)
        {
            Token = token;
            Label = label;
            FileCount = fileCount;
            OriginalBytes = originalBytes;
            TransmittedBytes = transmittedBytes;
            FrameBytes = frameBytes;
            FramesPerSecond = framesPerSecond;
            ErrorCorrection = errorCorrection;
            SourceBlocks = sourceBlocks;
            QrVersion = qrVersion;
            MinimumSeconds = minimumSeconds;
        }

        public string TransferId { get; set; }
        public string Token { get; private set; }
        public string Label { get; private set; }
        public long FileCount { get; private set; }
        public long OriginalBytes { get; private set; }
        public long TransmittedBytes { get; private set; }
        public int FrameBytes { get; private set; }
        public int FramesPerSecond { get; private set; }
        public string ErrorCorrection { get; private set; }
        public long SourceBlocks { get; private set; }
        public int QrVersion { get; private set; }
        public double MinimumSeconds { get; private set; }
    }
}
