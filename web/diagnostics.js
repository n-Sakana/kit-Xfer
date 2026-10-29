/* Local, bounded diagnostics. No payloads, URLs, file names, tokens or error messages. */
(function () {
  "use strict";
  var run = typeof crypto !== "undefined" && crypto.randomUUID ? crypto.randomUUID() : Date.now().toString(16) + Math.random().toString(16).slice(2);
  var device = run;
  try {
    var savedDevice = localStorage.getItem("Xfer-diagnostic-device");
    if (savedDevice && /^[a-f0-9.-]{8,80}$/i.test(savedDevice)) device = savedDevice;
    else localStorage.setItem("Xfer-diagnostic-device", device);
  } catch (_) { /* page-run identity still works when origin storage is unavailable */ }
  var pending = [], samples = new Map(), active = new Map(), seq = 0, dropped = 0;
  var database = null, writing = null, storageFailures = 0, requestSeq = 0;
  var retention = 16384, incidentRequested = false, uploading = false;
  var boot = performance.now(), lastTick = boot, sampler = null;
  var opening = new Promise(function (resolve) {
    try {
      var request = indexedDB.open("Xfer-diagnostics", 2);
      request.onupgradeneeded = function () {
        ["events", "incidents"].forEach(function (name) {
          if (!request.result.objectStoreNames.contains(name)) request.result.createObjectStore(name, { autoIncrement: true });
        });
        if (!request.result.objectStoreNames.contains("receipts")) request.result.createObjectStore("receipts");
      };
      request.onsuccess = function () { database = request.result; resolve(database); };
      request.onerror = request.onblocked = function () { storageFailures++; resolve(null); };
    } catch (_) { storageFailures++; resolve(null); }
  });
  function id(value) {
    var hash = 2166136261;
    for (var i = 0; i < String(value || "").length; i++) hash = Math.imul(hash ^ String(value).charCodeAt(i), 16777619);
    return (hash >>> 0).toString(16).padStart(8, "0");
  }
  function event(name, correlation, data) {
    if (pending.length >= 128) { pending.shift(); dropped++; }
    pending.push({ schema: 1, utc: new Date().toISOString(), monoMs: Math.round(performance.now() - boot),
      run: run, seq: ++seq, route: "browser", event: name, correlation: correlation || "", data: data || null });
    if (/^(camera\.(stop|failed|open.failed)|send.display.stop|worker.failed|page\.(offline|error|unhandled_rejection))$/.test(name)) {
      // Coalesce incident notifications in the next write. Heartbeats never evict this window.
      incidentRequested = true;
    }
  }
  function sample(name, correlation, ms, last) {
    var key = name + "|" + (correlation || ""), item = samples.get(key);
    if (!item) {
      if (samples.size >= 128) { dropped++; return; }
      item = { name: name, correlation: correlation, count: 0, totalMs: 0, maxMs: 0, last: null };
      samples.set(key, item);
    }
    item.count++; item.totalMs += ms || 0; item.maxMs = Math.max(item.maxMs, ms || 0); item.last = last || null;
  }
  function collect() {
    samples.forEach(function (a) { event(a.name, a.correlation, { count: a.count, totalMs: a.totalMs, maxMs: a.maxMs, last: a.last }); });
    samples.clear();
    if (dropped || storageFailures) event("logging.loss", "", { dropped: dropped, storageFailures: storageFailures });
    dropped = 0;
  }
  function flush() {
    collect();
    // Share one pending write, including database-open time. Export must await it.
    if (writing) return writing;
    writing = writePending().finally(function () { writing = null; });
    return writing;
  }
  async function writePending() {
    if (!pending.length) return;
    try {
      var db = await opening;
      if (!db) return; // bounded memory remains available to export, even when storage is denied
      var batch = pending.splice(0, 128);
      await new Promise(function (resolve) {
        var tx = db.transaction(["events", "incidents"], "readwrite"), store = tx.objectStore("events");
        batch.forEach(function (record) { store.add(record); });
        trim(store, retention);
        if (incidentRequested) {
          incidentRequested = false;
          var recent = [], cursorRequest = store.openCursor(null, "prev");
          cursorRequest.onsuccess = function () {
            var cursor = cursorRequest.result;
            if (cursor && recent.length < 512) { recent.push(cursor.value); cursor.continue(); return; }
            var archive = tx.objectStore("incidents");
            recent.reverse().forEach(function (record) { archive.add(record); });
            trim(archive, 4096);
          };
        }
        tx.oncomplete = function () { resolve(); };
        tx.onerror = tx.onabort = function () { storageFailures++; dropped += batch.length; resolve(); };
      });
    } catch (_) { storageFailures++; }
  }
  function trim(store, limit) {
    var count = store.count();
    count.onsuccess = function () {
      var excess = count.result - limit;
      if (excess <= 0) return;
      store.openCursor().onsuccess = function (e) {
        var cursor = e.target.result;
        if (cursor && excess-- > 0) { cursor.delete(); cursor.continue(); }
      };
    };
  }
  function readBatch(name) {
    return new Promise(function (resolve, reject) {
      var tx = database.transaction([name, "receipts"]), result = [], bytes = 0;
      var receipt = tx.objectStore("receipts").get(name);
      receipt.onsuccess = function () {
        var cursorRequest = tx.objectStore(name).openCursor(IDBKeyRange.lowerBound(receipt.result || 0, true));
        cursorRequest.onsuccess = function () {
          var cursor = cursorRequest.result;
          if (!cursor || result.length >= 48) return;
          var size = new TextEncoder().encode(JSON.stringify(cursor.value)).length;
          if (bytes + size > 48000) return;
          bytes += size; result.push({ key: cursor.key, record: cursor.value }); cursor.continue();
        };
      };
      tx.oncomplete = function () { resolve(result); }; tx.onerror = tx.onabort = reject;
    });
  }
  async function upload() {
    if (uploading || !navigator.onLine) return;
    uploading = true;
    try {
      await flush();
      // A bounded diagnostics-only drain. Never observes its own fetch or changes transfer retries.
      for (var round = 0; round < 4; round++) {
        var name = database && round % 2 === 0 ? "incidents" : "events";
        var batch = database ? await readBatch(name) : pending.slice(0, 48).map(function (record) { return { key: record.seq, record: record }; });
        if (!database) {
          var memoryBytes = 0;
          batch = batch.filter(function (item) { memoryBytes += new TextEncoder().encode(JSON.stringify(item.record)).length; return memoryBytes <= 48000; });
        }
        if (!batch.length) continue;
        var controller = new AbortController(), timeout = setTimeout(function () { controller.abort(); }, 4000);
        try {
          var response = await fetch("/api/diagnostics", { method: "POST", credentials: "same-origin", signal: controller.signal,
            headers: { "Content-Type": "application/json", "X-Xfer-Diagnostics": "1" },
            body: JSON.stringify({ archive: name === "incidents", records: batch.map(function (item) { return item.record; }) }) });
          if (!response.ok) return;
          var ack = await response.json();
          if (ack.saved !== batch.length) return;
          if (!database) { pending = pending.filter(function (record) { return record.seq > batch[batch.length - 1].key; }); continue; }
          await new Promise(function (resolve, reject) {
            var tx = database.transaction("receipts", "readwrite"), store = tx.objectStore("receipts"), old = store.get(name);
            old.onsuccess = function () { store.put(Math.max(old.result || 0, batch[batch.length - 1].key), name); };
            tx.oncomplete = resolve; tx.onerror = tx.onabort = reject;
          });
        } finally { clearTimeout(timeout); }
      }
    } catch (_) { /* keep unacknowledged records on device for the next diagnostic drain */ }
    finally { uploading = false; }
  }
  function route(url) {
    var path = String(url).split("?")[0];
    return /^\/api\/(status|folder|pick-files|pick-folder|devices|shutdown|remote-entry(?:\/qr)?|remotes(?:\/(heartbeat|command|disconnect))?|optical\/(start|stop|frames?|receive\/(start|stop|frames?)))$/.test(path) ? path : "asset_or_unknown";
  }
  function begin(name, correlation) {
    var start = performance.now(), key = ++requestSeq;
    if (active.size < 32) active.set(key, { stage: name, correlation: correlation, start: start });
    sample(name + ".begin", correlation, 0);
    return function (status, failed) {
      active.delete(key);
      sample(name + (failed ? ".failed" : ".end"), correlation, performance.now() - start, { status: status || 0 });
    };
  }
  function fetchObserved(url, options) {
    var settings = Object.assign({}, options || {});
    settings.headers = new Headers(settings.headers || {});
    settings.headers.set("X-Xfer-Trace", run);
    var end = begin("http.headers", route(url));
    return fetch(url, settings).then(function (response) { end(response.status, !response.ok); return response; },
      function (error) { end(0, true); throw error; });
  }
  async function exportJsonl() {
    await flush();
    if (database && pending.length) await flush();
    var records = [];
    if (database) records = await new Promise(function (resolve) {
      try {
        var request = database.transaction("events").objectStore("events").getAll();
        request.onsuccess = function () { resolve(request.result); };
        request.onerror = function () { resolve([]); };
      } catch (_) { resolve([]); }
    });
    return records.concat(pending).slice(database ? -retention : -128).map(function (record) { return JSON.stringify(record); }).join("\n") + "\n";
  }
  window.XferDiagnostics = { event: event, sample: sample, begin: begin, id: id, route: route,
    transfer: function (session) { return String(session || "").split(":").slice(0, 4).join(":"); },
    fetch: fetchObserved, flush: flush, exportJsonl: exportJsonl,
    observe: function (fn) { sampler = fn; },
    download: async function () {
      var url = URL.createObjectURL(new Blob([await exportJsonl()], { type: "application/x-ndjson" }));
      var link = document.createElement("a"); link.href = url; link.download = "Xfer-browser-log.jsonl"; link.click();
      setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
    }
  };
  var agent = navigator.userAgent || "", browser = agent.match(/(Firefox|Edg|Chrome|Version)\/([\d.]+)/);
  var environment = { device: device, build: "__Xfer_BUILD_ID__", browser: browser ? browser[0] : "unknown",
    os: /Android/.test(agent) ? "Android" : /iPhone|iPad/.test(agent) ? "iOS" : /Windows/.test(agent) ? "Windows" : /Mac OS/.test(agent) ? "macOS" : "other",
    cpus: navigator.hardwareConcurrency || 0, dpr: devicePixelRatio, width: screen.width, height: screen.height,
    standalone: !!(window.matchMedia && window.matchMedia("(display-mode: standalone)").matches) };
  event("page.start", id(run), environment);
  setInterval(function () {
    var now = performance.now(), waiting = [];
    active.forEach(function (a) { waiting.push({ stage: a.stage, correlation: a.correlation, ageMs: now - a.start }); });
    event("page.heartbeat", id(run), { intervalMs: now - lastTick, visible: document.visibilityState === "visible",
      online: navigator.onLine, waiting: waiting, environment: environment });
    lastTick = now;
    try { if (sampler) sampler(); } catch (_) { sample("logging.sampler_failed", "", 0); }
    flush();
  }, 5000);
  document.addEventListener("visibilitychange", function () { event("page.visibility", "", { visible: document.visibilityState === "visible" }); flush(); });
  ["online", "offline", "pagehide"].forEach(function (name) { window.addEventListener(name, function () { event("page." + name); flush(); }); });
  window.addEventListener("error", function (error) { event("page.error", "", { line: error.lineno || 0, column: error.colno || 0 }); });
  window.addEventListener("unhandledrejection", function () { event("page.unhandled_rejection"); });
  flush();
  setTimeout(upload, 1500);
  setInterval(upload, 10000);
  window.addEventListener("online", upload);
})();
