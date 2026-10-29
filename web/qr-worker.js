/* Xfer camera decoder. The bundled ZXing-WASM 2.2.4 reader is unchanged. */
"use strict";

var readerReady = false;
var core;
var readBarcodes;
var diagnosticStage = "worker.boot";
var wasmStatus = 0, wasmNetworkFailed = false;
var originalFetch = self.fetch;
self.fetch = function () {
  // Observe the worker's existing WASM request; do not add requests or change its retry behavior.
  return originalFetch.apply(self, arguments).then(function (response) { wasmStatus = response.status; return response; },
    function (error) { wasmNetworkFailed = true; throw error; });
};

function failureDetail(error) {
  var name = error && error.name;
  return { stage: diagnosticStage, type: /^(Error|TypeError|RangeError|ReferenceError|SyntaxError|RuntimeError|CompileError|LinkError|NetworkError|AbortError|SecurityError|InvalidStateError)$/.test(name) ? name : "OtherError",
    code: error && Number.isFinite(error.code) ? error.code : 0, site: "qr-worker",
    wasmStatus: wasmStatus, wasmNetworkFailed: wasmNetworkFailed };
}

async function readTransfer(image, harder, binarizer, session, observed) {
  var results = await readBarcodes(image, {
    formats: ["QRCode"],
    // A pairing/link QR may be in the same camera image as the transfer QR.
    maxNumberOfSymbols: 4,
    tryHarder: harder,
    tryRotate: true,
    tryInvert: harder,
    tryDownscale: harder,
    binarizer: binarizer || "LocalAverage"
  });
  results.forEach(function (result) {
    if (!result.isValid) return;
    observed.symbols++;
    var frame = core.parseFrame(result.bytes);
    if (!frame) observed.invalid++;
    else if (session && frame.session !== session) observed.foreign++;
  });
  return results.find(function (result) {
    var frame = result.isValid && core.parseFrame(result.bytes);
    return frame && (!session || frame.session === session);
  });
}

self.onmessage = async function (event) {
  var message = event.data || {};
  if (!readerReady) {
    self.postMessage({ id: message.id, bytes: null });
    return;
  }
  var observed = { symbols: 0, invalid: 0, foreign: 0 };
  try {
    diagnosticStage = "decode.image";
    var image = new ImageData(new Uint8ClampedArray(message.buf), message.w, message.h);
    diagnosticStage = "decode.normal";
    var result = await readTransfer(image, false, null, message.session, observed);
    if (!result) { diagnosticStage = "decode.harder"; result = await readTransfer(image, true, null, message.session, observed); }
    // A different threshold can recover some screen glare/uneven illumination.
    // Bound this additional expensive search rather than doing it on every frame.
    if (!result && message.id % 4 === 0) { diagnosticStage = "decode.histogram"; result = await readTransfer(image, true, "GlobalHistogram", message.session, observed); }
    if (!result) {
      self.postMessage({ id: message.id, bytes: null, observed: observed });
      return;
    }
    // Never transfer the WASM heap itself. Copy only this symbol's bytes.
    var bytes = new Uint8Array(result.bytes);
    self.postMessage({ id: message.id, bytes: bytes, observed: observed, position: result.position }, [bytes.buffer]);
  } catch (error) {
    self.postMessage({ id: message.id, bytes: null, error: String(error && error.message || error),
      diagnostic: failureDetail(error),
      fatal: error && error.name === "RuntimeError" });
  }
};

(async function initialize() {
  try {
    diagnosticStage = "worker.import";
    importScripts("/optical-core.js?v=__Xfer_BUILD_ID__", "/zxing-reader.js?v=__Xfer_BUILD_ID__");
    core = self.XferOptical;
    readBarcodes = self.XferZXingRead;
    diagnosticStage = "worker.wasm_initialize";
    await readBarcodes(new ImageData(8, 8), { formats: ["QRCode"] });
    readerReady = true;
    self.postMessage({ id: -1, ready: true });
  } catch (error) {
    // Initialization failure is not a camera alignment problem.
    self.postMessage({ id: -1, fatal: true, diagnostic: failureDetail(error), error: String(error && error.message || error) });
  }
})();
