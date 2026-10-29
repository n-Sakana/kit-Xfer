# Diagnostic logs

Xfer records startup, transfer preparation, display, capture, decoding, relay,
verification, saving, and shutdown events as JSONL (one JSON object per line).

## Locations and collection

- Windows: `%LOCALAPPDATA%\Xfer\logs`, including `trace-*.jsonl` and
  `launcher.jsonl` / `launcher.jsonl.previous`.
- Linux: `~/.local/share/Xfer/logs`, under .NET's LocalApplicationData directory.
- Set `Xfer_LOG_DIR` before launch to use another log directory.
- Browser and phone: the `Xfer-diagnostics` IndexedDB database, separately for
  each origin and device. The `events` and `incidents` stores retain ordinary
  and incident history.

Browser logs are collected automatically while the page can reach its PC.
Copy the PC's log directory to collect host and browser logs together. Browser
history appears as `browser-trace-*.jsonl` and `browser-incident-*.jsonl`.
Offline history is collected when the connection returns or the same URL is
opened again. Clearing browser data deletes locally stored logs. Open a page
at each origin whose history you want to collect.

Collection is acknowledged after the PC writes the batch. Lost acknowledgements
or multiple tabs may produce duplicate rows; deduplicate by `(run, seq)`.
`XferDiagnostics.download()` and `exportJsonl()` can also export browser logs.

## Reading events

Each row has `schema`, UTC time `utc`, elapsed time `monoMs`, run ID `run`,
sequence `seq`, `route`, `event`, `correlation`, and selected metadata `data`.
Startup and heartbeat events include a pseudonymous device ID, OS, runtime or
browser version, and build identifier.

Transfer correlation uses `session:blocks:blockLength:totalLength`. Compare
`send.ready`, `receive.session`, and `relay.accepted`. Session numbers are
16-bit, so also compare UTC time and run IDs. Browser `page.start.correlation`
matches the PC's `http.*.data.last.client`.

| Events | Stage |
| --- | --- |
| `send.prepare/bundle/compress/hash.*`, `send.qr` | Read, bundle, compress, hash, and encode |
| `send.ui_tick`, `send.image_request`, `send.image_cache_miss` | Desktop display requests and image retrieval |
| `send.draw`, `send.queue_empty`, `send.status` | Browser drawing and prefetch queue |
| `camera.capture`, `camera.video_callback`, `camera.no_worker`, `camera.status` | Capture and worker availability |
| `camera.qr_read`, `decode.read` | Successful QR decoding |
| `decode.foreign`, `decode.non_transfer` | Other sessions or unrelated QR codes observed |
| `relay.accepted/duplicate/full/retry`, `http.*` | Relay queues and communication |
| `receive.accepted/duplicate/reject.*` | Frame acceptance, duplication, or rejection |
| `receive.assemble/verify/decompress/save.*`, `receive.complete` | Reconstruction, verification, and output |
| `*.failed`, `*.stop`, `*.stopped`, `*.expired`, `process.exit`, `page.pagehide` | Failure, stop, expiry, or shutdown |

Display requests do not measure physical screen output. Successful decoding
does not by itself mean that a frame was accepted or advanced reconstruction.
`receive.*.data.last.solved/blocks/progressAgeMs` reports solved blocks and time
since progress. For a stage with `*.start` but no `*.ok`, inspect that stage's
failure events.

Frequent events are aggregated every five seconds with `count`, `totalMs`,
`maxMs`, and the final state `last`. Average duration is `totalMs/count`;
`last` is not cumulative. Device clocks can differ, so cross-device timestamps
do not support precise latency measurements without clock synchronization.

On Windows, normal shutdown records `native.shutdown.start`, `app.stop`,
`native.shutdown.end`, and `launcher.native_shutdown`. Shutdown codes are:
0 completed; 1 two-second timeout; 2 cleanup exception; 3 flush failure;
−1 application not loaded; −2 shutdown export missing; −3 bridge missing.

## Privacy and retention

Logs omit device and camera names, transfer file names, paths, contents, raw QR
data, camera images, authentication tokens, exception messages, and full stack
traces. Failures record stage, type, code, and a source file/method/line where
available. Worker failures distinguish imports, WASM initialization, and
decoding, including the WASM fetch status.

Host logs rotate at approximately 2 MiB per file, retaining eight files. The
host queue and aggregate key count are each limited to 256. Launcher logs retain
two files of approximately 2 MiB. Browser logs keep the latest 16,384 events per
origin. A stop, failure, or offline event protects up to 512 preceding events in
the incident store, which retains 4,096 events in total. Later incidents can
evict earlier ones. If IndexedDB is unavailable, the page keeps up to 128 events
in memory while it remains open.

Browser collection runs every ten seconds, with up to four batches of 48 records
or about 48 KB, one request at a time, and a four-second timeout. The PC applies
origin, header, JSON, depth, and field validation with a 64 KiB request limit.
Collection accepts up to 120 batches per minute with one writer. Browser trace
files retain eight files of approximately 2 MiB; incident files retain four.
`logging.loss` records queue overflow or write failures when logging is available.

Forced termination, power loss, or browser suspension can lose recent events,
including shutdown records. A missing shutdown event alone does not identify
the cause or show whether a transfer failed.
