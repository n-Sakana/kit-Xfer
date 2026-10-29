/* Xfer optical transport helpers, including bounded streaming transfers. */
(function (root, factory) {
  "use strict";
  if (typeof module === "object" && module.exports) module.exports = factory();
  else root.XferOptical = factory();
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  function parseFrame(value) {
    if (!(value instanceof Uint8Array) && !(value instanceof ArrayBuffer)) return null;
    var bytes = value instanceof Uint8Array ? value : new Uint8Array(value);
    if (bytes.length > 48 && bytes.length <= 2953 && bytes[0] === 0xd1 && bytes[1] === 0x0d) {
      var stream = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
      var block = stream.getUint16(44, true);
      var total = stream.getUint32(16, true) + stream.getUint32(20, true) * 4294967296;
      var chunk = stream.getUint32(8, true) + stream.getUint32(12, true) * 4294967296;
      if (block !== bytes.length - 48 || bytes[46] || bytes[47] || !Number.isSafeInteger(total) || total <= 0 ||
          chunk < 0 || chunk > Math.floor((total - 1) / 65536) || stream.getUint16(2, true) !== stream.getUint16(24, true)) return null;
      var id = Array.prototype.join.call(bytes.subarray(24, 40), ".") + ":" + total + ":" + block;
      var local = stream.getUint32(4, true), key = id + ":" + chunk + ":" + local;
      return { bytes: bytes, session: id, sequence: chunk + ":" + local, key: key };
    }
    if (bytes.length <= 20 || bytes.length > 2953 || bytes[0] !== 0xd1 || bytes[1] !== 0x0c) return null;
    var view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    var blocks = view.getUint16(8, true), length = view.getUint16(10, true);
    var total = view.getUint32(12, true);
    if (!blocks || !length || length > 2933 || !total || total > 65 * 1024 * 1024 + 64 * 1024 ||
        bytes.length !== length + 20 || Math.ceil(total / length) !== blocks) return null;
    var session = [view.getUint16(2, true), blocks, length, total, view.getUint32(16, true)].join(":");
    var sequence = view.getUint32(4, true);
    return { bytes: bytes, session: session, sequence: sequence, key: session + ":" + sequence };
  }

  function unpackFrames(buffer, expectedSide, expectedSequence, expectedCount) {
    if (!(buffer instanceof ArrayBuffer) || buffer.byteLength < 12) throw new Error("QR batch is truncated");
    var view = new DataView(buffer);
    if (view.getUint32(0, false) !== 0x46515231) throw new Error("Unknown QR batch format");
    var side = view.getUint16(4, true), count = view.getUint16(6, true);
    var sequence = view.getUint32(8, true), stride = Math.ceil(side * side / 8);
    if (side < 29 || side > 185 || side !== expectedSide || count < 1 || count > 16 ||
        count !== expectedCount || sequence !== (expectedSequence >>> 0) ||
        buffer.byteLength !== 12 + stride * count) throw new Error("Invalid QR batch dimensions");
    var bits = new Uint8Array(buffer), frames = [];
    for (var frame = 0; frame < count; frame++) {
      var pixels = new Uint8ClampedArray(side * side * 4);
      for (var bit = 0; bit < side * side; bit++) {
        var white = (bits[12 + frame * stride + (bit >>> 3)] & (0x80 >>> (bit & 7))) ? 0 : 255;
        var offset = bit * 4;
        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = white;
        pixels[offset + 3] = 255;
      }
      frames.push({ sequence: expectedSequence + frame, pixels: pixels, side: side });
    }
    return frames;
  }

  function scanRegion(width, height, tracking, frameId, now) {
    var full = { x: 0, y: 0, width: width, height: height, full: true };
    if (!tracking || tracking.width !== width || tracking.height !== height ||
        now - tracking.at > 1200 || tracking.misses >= 3 || frameId % 6 === 0) return full;
    var padding = Math.max(32, Math.max(tracking.right - tracking.left, tracking.bottom - tracking.top) * 0.35);
    var left = Math.max(0, Math.floor(tracking.left - padding));
    var top = Math.max(0, Math.floor(tracking.top - padding));
    var right = Math.min(width, Math.ceil(tracking.right + padding));
    var bottom = Math.min(height, Math.ceil(tracking.bottom + padding));
    if (right <= left || bottom <= top) return full;
    return { x: left, y: top, width: right - left, height: bottom - top, full: false };
  }

  function trackPosition(position, region, width, height, now) {
    if (!position) return null;
    var points = [position.topLeft, position.topRight, position.bottomRight, position.bottomLeft];
    if (points.some(function (point) { return !point || !Number.isFinite(point.x) || !Number.isFinite(point.y); })) return null;
    var xs = points.map(function (point) { return point.x + region.x; });
    var ys = points.map(function (point) { return point.y + region.y; });
    var left = Math.max(0, Math.min.apply(null, xs)), right = Math.min(width, Math.max.apply(null, xs));
    var top = Math.max(0, Math.min.apply(null, ys)), bottom = Math.min(height, Math.max.apply(null, ys));
    if (right <= left || bottom <= top) return null;
    return { left: left, right: right, top: top, bottom: bottom, width: width, height: height, at: now, misses: 0 };
  }

  function maximumFrameBytes(correction) {
    return { L: 2953, M: 2331, Q: 1663, H: 1273 }[correction] || 2953;
  }

  // A single ordered, bounded upload queue. Retrying the same sequence is idempotent
  // at the fountain decoder, including when a reply is lost after successful storage.
  function UploadQueue(options) {
    this.options = options;
    this.queue = [];
    this.keys = new Set();
    this.history = [];
    this.session = null;
    this.inFlight = false;
    this.stopped = false;
    this.timer = null;
    this.failures = 0;
    this.controller = null;
  }
  UploadQueue.prototype.push = function (value) {
    if (this.stopped) return false;
    var frame = parseFrame(value);
    if (!frame || (this.session && this.session !== frame.session) || this.keys.has(frame.key) || this.queue.length >= 128) return false;
    this.session = frame.session;
    this.keys.add(frame.key);
    this.queue.push(frame);
    this.schedule(24);
    return true;
  };
  UploadQueue.prototype.schedule = function (delay) {
    if (this.stopped || this.inFlight || this.timer !== null || !this.queue.length) return;
    var self = this;
    this.timer = setTimeout(function () { self.timer = null; self.flush(); }, delay);
  };
  UploadQueue.prototype.flush = async function () {
    if (this.stopped || this.inFlight || !this.queue.length) return;
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
    this.inFlight = true;
    this.controller = typeof AbortController === "function" ? new AbortController() : null;
    var batch = this.queue.slice(0, 8);
    try {
      var result = await this.options.send(batch.map(function (frame) { return frame.bytes; }), this.controller && this.controller.signal);
      if (this.stopped) return;
      this.queue.splice(0, batch.length);
      for (var i = 0; i < batch.length; i++) this.history.push(batch[i].key);
      while (this.history.length > 2048) this.keys.delete(this.history.shift());
      this.failures = 0;
      this.options.onProgress(result);
    } catch (error) {
      if (this.stopped) return;
      var status = Number(error && error.status || 0);
      if (status >= 400 && status < 500 && status !== 408 && status !== 429) {
        this.stop();
        this.options.onFatal(error);
      } else {
        this.failures++;
        if (this.options.onTransient) this.options.onTransient(error);
      }
    } finally {
      this.inFlight = false;
      this.controller = null;
      this.schedule(this.failures ? Math.min(5000, 250 * Math.pow(2, Math.min(this.failures - 1, 5))) : 0);
    }
  };
  UploadQueue.prototype.stop = function () {
    this.stopped = true;
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
    if (this.controller) this.controller.abort();
    this.queue = [];
    this.keys.clear();
    this.history = [];
  };

  return { parseFrame: parseFrame, unpackFrames: unpackFrames, scanRegion: scanRegion,
    trackPosition: trackPosition, maximumFrameBytes: maximumFrameBytes, UploadQueue: UploadQueue };
});
