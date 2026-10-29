"use strict";

var BUILD_ID = "__Xfer_BUILD_ID__";
var CACHE_NAME = "xfer-shell-" + BUILD_ID;
var SHELL = [
  "/",
  "/app.css?v=" + BUILD_ID,
  "/app.js?v=" + BUILD_ID,
  "/diagnostics.js?v=" + BUILD_ID,
  "/optical-core.js?v=" + BUILD_ID,
  "/zxing-reader.js?v=" + BUILD_ID,
  "/qr-worker.js?v=" + BUILD_ID,
  "/zxing_reader-EOacYbLr.wasm",
  "/icon.svg?v=" + BUILD_ID,
  "/manifest.webmanifest?v=" + BUILD_ID
];
// Kept when the PC serves them; without them the page uses the phone's own fonts.
var FONTS = [
  "/fonts/Inter-Regular.ttf?v=" + BUILD_ID,
  "/fonts/Inter-Medium.ttf?v=" + BUILD_ID,
  "/fonts/Inter-SemiBold.ttf?v=" + BUILD_ID
];

self.addEventListener("install", function (event) {
  event.waitUntil(
    caches.open(CACHE_NAME).then(function (cache) {
      return cache.addAll(SHELL).then(function () {
        return Promise.all(FONTS.map(function (url) {
          return cache.add(url).catch(function () {});
        }));
      });
    }).then(function () {
      return self.skipWaiting();
    })
  );
});

self.addEventListener("activate", function (event) {
  event.waitUntil(
    caches.keys().then(function (names) {
      return Promise.all(names.filter(function (name) {
        return name.indexOf("xfer-shell-") === 0 && name !== CACHE_NAME;
      }).map(function (name) {
        return caches.delete(name);
      }));
    }).then(function () {
      return self.clients.claim();
    })
  );
});

self.addEventListener("fetch", function (event) {
  var request = event.request;
  if (request.method !== "GET") {
    return;
  }
  var url = new URL(request.url);
  if (url.origin !== self.location.origin || url.pathname.indexOf("/api/") === 0) {
    return;
  }

  if (request.mode === "navigate") {
    event.respondWith(
      fetch(request).catch(function () {
        return caches.match("/");
      })
    );
    return;
  }

  event.respondWith(
    caches.match(request).then(function (cached) {
      return cached || fetch(request);
    })
  );
});
