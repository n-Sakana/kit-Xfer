using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Xfer
{
    // D1/0D: bounded fountain windows over a seekable, 64-bit virtual bundle.
    // All integers are little endian. See docs/optical-format.md for the wire format.
    internal static class StreamingWire
    {
        public const int Header = 48, ChunkBytes = 65536;
        public static long Read64(byte[] b, int p) { return BitConverter.ToInt64(b, p); }
        public static uint Read32(byte[] b, int p) { return BitConverter.ToUInt32(b, p); }
        public static void Put(byte[] b, int p, byte[] v) { Buffer.BlockCopy(v, 0, b, p, v.Length); }
        public static uint Hash(byte[] b) { uint h = 2166136261; foreach (byte v in b) h = unchecked((h ^ v) * 16777619); return h; }
        public static void ReadExactly(Stream s, byte[] b, int p, int n)
        {
            while (n > 0) { int r = s.Read(b, p, n); if (r == 0) throw new EndOfStreamException(); p += r; n -= r; }
        }
        public static int Blocks(int bytes, int block) { return (bytes + block - 1) / block; }
    }

    // Metadata stays small; file bodies are read directly from their original files, one window at a time.
    // Hashing is a streaming preparation pass, so modified files cannot be silently published by the receiver.
    internal sealed class StreamingBundle : IDisposable
    {
        private sealed class Part { public long Start, Length; public byte[] Header; public FolderFile File; }
        private readonly List<Part> _parts = new List<Part>();
        private FileStream _input;
        private FolderFile _openFile;
        public long Length { get; private set; }
        public long OriginalBytes { get; private set; }
        public long FileCount { get; private set; }
        public string Label { get; private set; }

        public StreamingBundle(FolderSnapshot source, IList<string> names)
        {
            var selected = OpticalPayload.Resolve(source, names);
            Label = OpticalPayload.SafeLabel(Path.GetFileName(source.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            FileCount = selected.Count;
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8))
            { writer.Write(new byte[] { 70, 88, 66, 51 }); writer.Write(Label); writer.Write((long)FileCount); AddHeader(bytes.ToArray()); }
            foreach (var file in selected)
            {
                var path = OpticalPayload.ValidateEntryName(file.Name);
                byte[] hash;
                CheckFile(file);
                using (var input = File.OpenRead(file.FullPath))
                using (var sha = SHA256.Create()) { hash = sha.ComputeHash(input); }
                CheckFile(file);
                using (var bytes = new MemoryStream())
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8))
                { writer.Write(path); writer.Write(file.Size); writer.Write(hash); AddHeader(bytes.ToArray()); }
                _parts.Add(new Part { Start = Length, Length = file.Size, File = file });
                Length = checked(Length + file.Size);
                OriginalBytes = checked(OriginalBytes + file.Size);
            }
        }
        private void AddHeader(byte[] bytes)
        { _parts.Add(new Part { Start = Length, Length = bytes.Length, Header = bytes }); Length = checked(Length + bytes.Length); }
        private static void CheckFile(FolderFile file)
        {
            var info = new FileInfo(file.FullPath);
            if (info.Length != file.Size || info.LastWriteTimeUtc != file.ModifiedUtc.UtcDateTime)
                throw new IOException("A file changed during transfer: " + file.Name);
        }
        public byte[] Read(long offset, int count)
        {
            var result = new byte[count]; int at = 0;
            int low = 0, high = _parts.Count;
            while (low < high) { int mid = low + (high - low) / 2; if (_parts[mid].Start + _parts[mid].Length <= offset) low = mid + 1; else high = mid; }
            while (at < count)
            {
                var part = _parts[low++]; long within = offset - part.Start;
                int take = (int)Math.Min(count - at, part.Length - within);
                if (part.Header != null) Buffer.BlockCopy(part.Header, (int)within, result, at, take);
                else
                {
                    CheckFile(part.File);
                    if (_openFile != part.File)
                    { Dispose(); _input = File.OpenRead(part.File.FullPath); _openFile = part.File; }
                    _input.Position = within; StreamingWire.ReadExactly(_input, result, at, take);
                }
                at += take; offset += take;
            }
            return result;
        }
        public void Dispose() { if (_input != null) _input.Dispose(); _input = null; _openFile = null; }
    }

    internal sealed class StreamingEncoder : IDisposable
    {
        private readonly StreamingBundle _bundle;
        private readonly byte[] _id = Guid.NewGuid().ToByteArray();
        private readonly int _frameBytes, _block, _fullBlocks;
        private readonly long _chunks;
        private readonly object _gate = new object();
        private long _cached = -1;
        private byte[] _bytes;
        private FountainEncoder _encoder;
        private uint _hash;
        private bool _disposed;
        public StreamingEncoder(StreamingBundle bundle, int frameBytes)
        {
            _bundle = bundle; _frameBytes = frameBytes; _block = frameBytes - StreamingWire.Header;
            _fullBlocks = StreamingWire.Blocks(StreamingWire.ChunkBytes, _block);
            _chunks = (bundle.Length - 1) / StreamingWire.ChunkBytes + 1;
        }
        public long Blocks { get { return checked((_chunks - 1) * _fullBlocks + StreamingWire.Blocks((int)(_bundle.Length - (_chunks - 1) * StreamingWire.ChunkBytes), _block)); } }
        public string Id { get { return new Guid(_id).ToString("N"); } }
        public byte[] Frame(ulong sequence)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException("StreamingEncoder");
                ulong slot = sequence / (ulong)(_fullBlocks * 2);
                long chunk = (long)(slot % (ulong)_chunks);
                ulong round = slot / (ulong)_chunks;
                int within = (int)(sequence % (ulong)(_fullBlocks * 2));
                if (_cached != chunk)
                {
                    long offset = chunk * StreamingWire.ChunkBytes;
                    _bytes = _bundle.Read(offset, (int)Math.Min(StreamingWire.ChunkBytes, _bundle.Length - offset));
                    _encoder = new FountainEncoder(_bytes, _block, BitConverter.ToUInt16(_id, 0));
                    _hash = StreamingWire.Hash(_bytes); _cached = chunk;
                }
                int blocks = _encoder.BlockCount;
                uint local = within < blocks ? (uint)within : checked((uint)blocks) + unchecked((uint)(round * (ulong)_fullBlocks + (ulong)(within - blocks)) % (uint.MaxValue - (uint)blocks));
                var frame = new byte[_frameBytes];
                frame[0] = 0xd1; frame[1] = 0x0d;
                StreamingWire.Put(frame, 2, BitConverter.GetBytes(BitConverter.ToUInt16(_id, 0)));
                StreamingWire.Put(frame, 4, BitConverter.GetBytes(local));
                StreamingWire.Put(frame, 8, BitConverter.GetBytes(chunk));
                StreamingWire.Put(frame, 16, BitConverter.GetBytes(_bundle.Length));
                StreamingWire.Put(frame, 24, _id);
                StreamingWire.Put(frame, 40, BitConverter.GetBytes(_hash));
                StreamingWire.Put(frame, 44, BitConverter.GetBytes((ushort)_block));
                if (local < blocks)
                { int start = (int)local * _block; Buffer.BlockCopy(_bytes, start, frame, StreamingWire.Header, Math.Min(_block, _bytes.Length - start)); }
                else
                { var block = _encoder.Encode(local - (uint)blocks); Buffer.BlockCopy(block, 0, frame, StreamingWire.Header, block.Length); }
                return frame;
            }
        }
        public void Dispose() { lock (_gate) { _disposed = true; _bundle.Dispose(); _encoder = null; _bytes = null; } }
    }

    internal sealed class StreamingFrame
    {
        public string Id;
        public ushort Session;
        public long Chunk, Total;
        public uint Sequence, Hash;
        public int BlockLength, ChunkLength, Blocks;
        public byte[] Block;
        public static bool TryParse(byte[] b, out StreamingFrame f)
        {
            f = null;
            if (b == null || b.Length <= StreamingWire.Header || b.Length > 2953 || b[0] != 0xd1 || b[1] != 0x0d) return false;
            long chunk = StreamingWire.Read64(b, 8), total = StreamingWire.Read64(b, 16);
            int block = BitConverter.ToUInt16(b, 44);
            if (total <= 0 || chunk < 0 || chunk > (total - 1) / StreamingWire.ChunkBytes || block != b.Length - StreamingWire.Header || b[46] != 0 || b[47] != 0) return false;
            var id = new byte[16]; Buffer.BlockCopy(b, 24, id, 0, 16);
            if (BitConverter.ToUInt16(b, 2) != BitConverter.ToUInt16(id, 0)) return false;
            int length = (int)Math.Min(StreamingWire.ChunkBytes, total - chunk * StreamingWire.ChunkBytes);
            f = new StreamingFrame { Id = new Guid(id).ToString("N"), Session = BitConverter.ToUInt16(b, 2), Chunk = chunk, Total = total,
                Sequence = StreamingWire.Read32(b, 4), Hash = StreamingWire.Read32(b, 40), BlockLength = block, ChunkLength = length,
                Blocks = StreamingWire.Blocks(length, block), Block = new byte[block] };
            Buffer.BlockCopy(b, StreamingWire.Header, f.Block, 0, block); return true;
        }
    }

    // Four live fountain windows. Eviction checkpoints solved blocks to disk, so later sweeps repair gaps
    // without retaining a transfer-sized decoder, bitmap or set of frame numbers in managed memory.
    internal sealed class StreamingReceiver : IDisposable
    {
        private sealed class Window
        { public long Chunk, Used; public uint Hash; public int Length, Blocks; public FountainDecoder Decoder; public bool Complete; }
        private readonly Dictionary<long, Window> _windows = new Dictionary<long, Window>();
        private readonly string _directory;
        private readonly FileStream _data, _map;
        private readonly int _block, _fullBlocks;
        private readonly long _total, _chunks;
        private readonly ushort _session;
        private long _clock, _solved, _done, _frames, _duplicates;
        private readonly DateTime _started = DateTime.UtcNow;
        public DateTime LastSeenUtc = DateTime.UtcNow;
        public DateTime LastMatchUtc = DateTime.UtcNow;
        public string Id { get; private set; }
        public OpticalReceiveResult CompletedResult { get; private set; }
        public StreamingReceiver(StreamingFrame first, string root)
        {
            Id = first.Id; _total = first.Total; _block = first.BlockLength; _session = first.Session;
            _fullBlocks = StreamingWire.Blocks(StreamingWire.ChunkBytes, _block); _chunks = (_total - 1) / StreamingWire.ChunkBytes + 1;
            Directory.CreateDirectory(root);
            _directory = Path.Combine(root, ".xfer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_directory);
            try
            {
                _data = new FileStream(Path.Combine(_directory, "data"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                _map = new FileStream(Path.Combine(_directory, "map"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            }
            catch { Dispose(); throw; }
        }
        public bool Matches(StreamingFrame f) { return Id == f.Id && _total == f.Total && _block == f.BlockLength; }
        public OpticalReceiveResult Add(StreamingFrame f, string root, bool open)
        {
            LastSeenUtc = LastMatchUtc = DateTime.UtcNow;
            if (CompletedResult != null) return CompletedResult;
            Window w;
            if (!_windows.TryGetValue(f.Chunk, out w))
            {
                if (_windows.Count == 4)
                {
                    Window oldest = null;
                    foreach (var item in _windows.Values) if (oldest == null || item.Used < oldest.Used) oldest = item;
                    Persist(oldest); _windows.Remove(oldest.Chunk);
                }
                w = Load(f); _windows.Add(f.Chunk, w);
            }
            w.Used = ++_clock;
            if (w.Hash != f.Hash) throw new InvalidDataException("The received segment checksum changed.");
            if (w.Complete) { _duplicates++; return Progress(); }
            int before = w.Decoder.SolvedCount;
            int duplicates = w.Decoder.FramesDuplicate;
            if (f.Sequence < w.Blocks) w.Decoder.AddKnownBlock((int)f.Sequence, f.Block);
            else w.Decoder.AddFrame(f.Sequence - (uint)w.Blocks, f.Block);
            if (w.Decoder.FramesDuplicate != duplicates) { _duplicates++; return Progress(); }
            // Bound pending parity equations even under endless duplicate/hostile traffic. Known blocks survive.
            if (w.Decoder.FramesNew > w.Blocks * 4 && !w.Decoder.IsComplete)
            { Persist(w); w = Load(f); _windows[f.Chunk] = w; w.Used = _clock; }
            int gained = w.Decoder.SolvedCount - before;
            _solved += gained; _frames++;
            if (w.Decoder.IsComplete)
            {
                var bytes = w.Decoder.Assemble();
                if (StreamingWire.Hash(bytes) != w.Hash) throw new InvalidDataException("Received segment verification failed.");
                _data.Position = w.Chunk * StreamingWire.ChunkBytes; _data.Write(bytes, 0, bytes.Length);
                w.Complete = true; w.Decoder = null; _done++; Persist(w);
            }
            if (_done != _chunks) return Progress();
            _data.Flush(); _data.Position = 0;
            var saved = StreamingBundleReader.Save(_data, _total, root);
            string error = null;
            if (open) { try { OutputLauncher.OpenFolder(saved.OutputPath); } catch (Exception ex) { error = ex.Message; } }
            var p = Progress();
            CompletedResult = new OpticalReceiveResult(true, true, _frames, _duplicates, p.SourceBlocks, p.SourceBlocks, _total,
                1, p.ElapsedSeconds, p.KilobytesPerSecond, saved.Label, saved.FileCount, saved.OutputPath, error);
            Dispose(); return CompletedResult;
        }
        private long MapOffset(long chunk) { return checked(chunk * (_fullBlocks + 5L)); }
        private Window Load(StreamingFrame f)
        {
            var w = new Window { Chunk = f.Chunk, Hash = f.Hash, Length = f.ChunkLength, Blocks = f.Blocks };
            _map.Position = MapOffset(f.Chunk);
            int flag = _map.ReadByte();
            if (flag > 0)
            {
                var h = new byte[4]; StreamingWire.ReadExactly(_map, h, 0, 4);
                if (BitConverter.ToUInt32(h, 0) != f.Hash) throw new InvalidDataException("The received segment does not match.");
                if (flag == 2) { w.Complete = true; return w; }
            }
            w.Decoder = new FountainDecoder(w.Blocks, _block, _session, w.Length);
            if (flag == 1)
            {
                var flags = new byte[w.Blocks]; StreamingWire.ReadExactly(_map, flags, 0, flags.Length);
                var bytes = new byte[_block];
                for (int i = 0; i < flags.Length; i++) if (flags[i] != 0)
                {
                    Array.Clear(bytes, 0, bytes.Length); _data.Position = w.Chunk * StreamingWire.ChunkBytes + (long)i * _block;
                    StreamingWire.ReadExactly(_data, bytes, 0, Math.Min(_block, w.Length - i * _block));
                    w.Decoder.AddKnownBlock(i, bytes);
                }
            }
            return w;
        }
        private void Persist(Window w)
        {
            _map.Position = MapOffset(w.Chunk); _map.WriteByte(w.Complete ? (byte)2 : (byte)1);
            var hash = BitConverter.GetBytes(w.Hash); _map.Write(hash, 0, hash.Length);
            if (w.Complete) return;
            var flags = new byte[w.Blocks];
            for (int i = 0; i < w.Blocks; i++)
            {
                var bytes = w.Decoder.KnownBlock(i); if (bytes == null) continue;
                flags[i] = 1; _data.Position = w.Chunk * StreamingWire.ChunkBytes + (long)i * _block;
                _data.Write(bytes, 0, Math.Min(_block, w.Length - i * _block));
            }
            _map.Write(flags, 0, flags.Length);
        }
        private OpticalReceiveResult Progress()
        {
            long blocks = checked((_chunks - 1) * _fullBlocks + StreamingWire.Blocks((int)(_total - (_chunks - 1) * StreamingWire.ChunkBytes), _block));
            double seconds = Math.Max(.001, (DateTime.UtcNow - _started).TotalSeconds);
            return new OpticalReceiveResult(true, false, _frames, _duplicates, blocks, _solved, _total,
                Math.Min(.99, (double)_solved / blocks), seconds, _solved * (double)_block / 1024 / seconds, null, 0, null, null);
        }
        public void Dispose()
        {
            _windows.Clear();
            if (_data != null) _data.Dispose(); if (_map != null) _map.Dispose();
            if (_directory != null && Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    internal static class StreamingBundleReader
    {
        private static string ReadString(BinaryReader reader)
        {
            // BinaryWriter's 7-bit UTF-8 length, bounded per path, never by file count or total content size.
            int length = 0, shift = 0;
            while (true) { byte b = reader.ReadByte(); if (shift >= 28) throw new InvalidDataException("Invalid name."); length |= (b & 127) << shift; if (b < 128) break; shift += 7; }
            if (length <= 0 || length > 1024 * 1024) throw new InvalidDataException("Invalid name.");
            var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException();
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        public static OpticalSavedPayload Save(Stream input, long length, string root)
        {
            string staging = null;
            try
            {
                using (var reader = new BinaryReader(input, Encoding.UTF8, true))
                {
                    if (reader.ReadUInt32() != 0x33425846) throw new InvalidDataException("Invalid transfer format.");
                    string label = OpticalPayload.SafeLabel(ReadString(reader));
                    long count = reader.ReadInt64(); if (count <= 0 || count > (length - input.Position) / 42) throw new InvalidDataException("Invalid file count.");
                    staging = Path.Combine(root, ".xfer-save-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
                    var buffer = new byte[65536];
                    for (long i = 0; i < count; i++)
                    {
                        string name = OpticalPayload.ValidateEntryName(ReadString(reader));
                        long size = reader.ReadInt64(); byte[] expected = reader.ReadBytes(32);
                        if (expected.Length != 32 || size < 0 || size > length - input.Position) throw new InvalidDataException("Invalid file length.");
                        string target = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        using (var sha = SHA256.Create())
                        {
                            long remaining = size;
                            while (remaining > 0)
                            { int take = (int)Math.Min(buffer.Length, remaining); StreamingWire.ReadExactly(input, buffer, 0, take); output.Write(buffer, 0, take); sha.TransformBlock(buffer, 0, take, buffer, 0); remaining -= take; }
                            sha.TransformFinalBlock(buffer, 0, 0);
                            if (Convert.ToBase64String(expected) != Convert.ToBase64String(sha.Hash)) throw new InvalidDataException("The received file is corrupt: " + name);
                        }
                    }
                    if (input.Position != length) throw new InvalidDataException("Invalid end of transfer.");
                    string destination = OutputLayout.PublishRunDirectory(root, label, staging);
                    staging = null; return new OpticalSavedPayload(label, count, destination);
                }
            }
            finally { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
    }
}
