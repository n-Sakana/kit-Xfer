using System;
using System.Collections.Generic;
using System.Threading;

namespace Xfer
{
    /// <summary>FX2 pictures of one display session, made ahead on a background thread (fountain frames,
    /// Reed-Solomon, cells) so the page can show a new picture every display tick. The page names the picture by
    /// its first sequence number and the layout (window size, cell size, coarse middle); the next pictures of the
    /// same layout are made while this one is on screen.</summary>
    internal sealed class Fx2Stream : IDisposable
    {
        private const int Ahead = 3;
        private readonly OpticalService _service;
        private readonly Fx2Code _code;
        private readonly bool _color;
        private readonly object _gate = new object();
        private readonly Dictionary<ulong, Fx2Picture> _ready = new Dictionary<ulong, Fx2Picture>();
        private readonly Thread _worker;
        private Fx2Layout _layout;
        private ulong _wanted;
        private bool _stopped;
        private string _error;

        public readonly string Token;
        public readonly string TransferId;

        public Fx2Stream(OpticalService service, string token, string errorCorrection, bool color = false)
        {
            _color = color;
            _service = service; Token = token; TransferId = service.DiagnosticTransfer(token);
            _code = Fx2Code.ForLevel(Fx2Code.ParseLevel(errorCorrection));
            _worker = new Thread(Run) { IsBackground = true, Name = "Xfer FX2 pictures", Priority = ThreadPriority.AboveNormal };
            _worker.Start();
        }

        public string Error { get { lock (_gate) return _error; } }

        /// <summary>The layout for a picture of width x height pixels (the same object while it does not change).</summary>
        public Fx2Layout Layout(int width, int height, int cell, int coarse)
        {
            lock (_gate)
            {
                var layout = _layout;
                if (layout != null && layout.Width == width && layout.Height == height && layout.CellPixels == cell && layout.Coarse == (coarse < 2 ? 0 : coarse))
                    return layout;
                layout = Fx2Layout.Create(width, height, cell, coarse);
                if (layout == null) throw new ArgumentException("画面が小さく、Tile の区画が 1 つも入りません。セルを小さくしてください。");
                _layout = layout;
                _ready.Clear();
                Diagnostics.Event("send.fx2_layout", TransferId, new { Width = width, Height = height, Cell = cell, Coarse = layout.Coarse, Patches = layout.Patches,
                    Tiers = layout.Tiers.Length });
                return layout;
            }
        }

        /// <summary>The picture whose first patch carries this sequence number.</summary>
        public Fx2Picture Picture(ulong sequence, Fx2Layout layout)
        {
            var stamp = Diagnostics.Stamp();
            Fx2Picture picture;
            lock (_gate)
            {
                if (layout != _layout) throw new InvalidOperationException("Tile layout changed");
                _wanted = sequence;
                foreach (var old in new List<ulong>(_ready.Keys)) if (unchecked(old - sequence) > (ulong)(Ahead * layout.Patches)) _ready.Remove(old);
                _ready.TryGetValue(sequence, out picture);
                Monitor.PulseAll(_gate);
            }
            bool missed = picture == null;
            if (picture == null)
            {
                picture = Render(sequence, layout);
                lock (_gate) if (layout == _layout) _ready[sequence] = picture;
            }
            Diagnostics.Sample(missed ? "send.image_cache_miss" : "send.image_request", TransferId, Diagnostics.Milliseconds(stamp), new { Sequence = sequence, Patches = layout.Patches });
            return picture;
        }

        private Fx2Picture Render(ulong sequence, Fx2Layout layout)
        {
            var stamp = Diagnostics.Stamp();
            var picture = new Fx2Picture(layout, _code, sequence, delegate(ulong s)
            {
                byte[] frame;
                if (!_service.TryFrame(Token, s, out frame)) throw new InvalidOperationException("QR の表示は終了しました。");
                return frame;
            }, _color);
            Diagnostics.Sample("send.fx2_picture", TransferId, Diagnostics.Milliseconds(stamp), new { Sequence = sequence, Patches = layout.Patches });
            return picture;
        }

        private void Run()
        {
            while (true)
            {
                ulong next; Fx2Layout layout;
                lock (_gate)
                {
                    while (!_stopped && !NeedsWork(out next)) Monitor.Wait(_gate, 200);
                    if (_stopped) return;
                    NeedsWork(out next);
                    layout = _layout;
                }
                try
                {
                    var picture = Render(next, layout);
                    lock (_gate) { if (layout == _layout && unchecked(next - _wanted) <= (ulong)(Ahead * layout.Patches)) _ready[next] = picture; }
                }
                catch (Exception error)
                {
                    Diagnostics.Failure("send.prefetch", TransferId, error);
                    lock (_gate) { _error = error.Message; _stopped = true; }
                    return;
                }
            }
        }

        private bool NeedsWork(out ulong next)
        {
            next = 0;
            if (_layout == null) return false;
            for (ulong i = 1; i <= Ahead; i++)
            {
                next = unchecked(_wanted + i * (ulong)_layout.Patches);
                if (!_ready.ContainsKey(next)) return true;
            }
            return false;
        }

        public void Dispose()
        {
            lock (_gate) { _stopped = true; _ready.Clear(); Monitor.PulseAll(_gate); }
        }
    }
}
