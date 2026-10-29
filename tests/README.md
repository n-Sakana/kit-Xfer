# Tests

Use the .NET 9 SDK for C# tests and Node.js for JavaScript tests. Browser tests
require Chromium and Python packages from `requirements.txt`:

```sh
python -m pip install -r tests/requirements.txt
dotnet build Xfer.csproj
dotnet run --project tests/Xfer.Regression.csproj -c Release
node --test tests/optical-core.test.js
python tests/browser_regression.py
python tests/app_smoke.py
```

Set `PLAYWRIGHT_CHROMIUM_EXECUTABLE` to select a Chromium executable for the
browser regression and HTTP smoke tests. `DOTNET_BIN` selects the .NET command
for `app_smoke.py`.

The regression suite checks fountain recovery with loss, duplicates, and
reordering; QR encoding; file round trips; corruption rejection; selection
validation; and output name conflicts. It uses C# 5 syntax for compatibility
with Windows PowerShell 5.1. The browser suite uses the included WASM decoder,
synthetic QR images, a virtual camera, and mocked HTTP APIs. The smoke test runs
the real HTTP server on an available port, checks transfer startup and stop,
and verifies that an occupied port does not cause another app to be replaced.

## Tile and native receiver

```sh
dotnet run --project tests/Xfer.Fx2Lab.csproj -c Release -- selftest
dotnet run --project tests/Xfer.Fx2Color.csproj -c Release
dotnet run --project tests/Xfer.Headless.Probe.csproj -c Release
dotnet run --project tests/Xfer.MiniUI.Probe.csproj -c Release
dotnet run --project tests/Xfer.FocusHold.csproj -c Release
```

Fx2Lab checks marker codes, orientation, parity, layouts, tracking, and patch
recovery. Its `sim` command adds perspective, blur, noise, rolling shutter, and
partial views. Its `pgm` command reads saved 8-bit camera images and can write
PPM detection overlays:

```sh
dotnet run --project tests/Xfer.Fx2Lab.csproj -c Release -- sim --screen 1920x1050 --cell 3 --coarse 2 --scale 1.26 --blur 0.8 --noise 3 --frames 120
dotnet run --project tests/Xfer.Fx2Lab.csproj -c Release -- pgm /path/to/frames --out /path/to/overlays
```

The headless and desktop probes send QR and Tile images through the native
receiver and compare received files with the originals. They use synthetic
camera images. The desktop probe links the included Linux rendering libraries.
FocusHold tests temporary focus locking, restoration, unsupported devices,
control failures, and worker/shutdown races without a physical camera.

[Camera tools](camera.md) documents the Windows capture utility.
`receive_report.py TRACE_OR_FOLDER [--table]` summarizes diagnostic traces,
including throughput, frame counts, duplicates, and stalls. A stall is at least
ten seconds of five-second buckets with no more than five new frames each.

## Streaming transfers

```sh
dotnet run --project tests/Xfer.Streaming.csproj -c Release -- /path/to/work selftest
dotnet run --project tests/Xfer.Streaming.csproj -c Release -- /path/to/work fx2 2147549185
dotnet run --project tests/Xfer.Streaming.csproj -c Release -- /path/to/work picture 73400320
dotnet run --project tests/Xfer.SmallBenchmark.csproj -c Release -- /path/to/work
```

Use a work directory outside the repository. `selftest` checks 2,001 files,
checkpointing and repair, duplicates and reordering, 64-bit metadata, QR
round trips, corruption and invalid paths, and cleanup. `fx2` passes all bytes
through cell encoding and decoding; `picture` also uses full raster images and
marker detection. Both require matching SHA-256 hashes. These are synthetic
throughput tests without a physical camera or a display frame-rate limit.

The large-file tests remove generated payloads when finished. On Linux they
check available memory every 250 ms and stop below 9 GiB. The small benchmark
runs a 5.7 MiB payload three times through the compressed bundle path.
`Streaming` also accepts `small-stream` to exercise that size with streaming.
See [optical formats](../docs/optical-format.md) for format selection.

## Diagnostics and shutdown

```sh
dotnet run --project tests/Xfer.Diagnostics.csproj -c Release -- /path/to/log-test
dotnet run --project tests/Xfer.Shutdown.csproj -c Release -- /path/to/Xfer /path/to/shutdown-test
dotnet publish Xfer.csproj -c Release --no-self-contained -o /path/to/http
python tests/logging_integration.py --app /path/to/http --work /path/to/evidence
```

Diagnostics checks event format, correlation, sensitive data exclusion, queue
limits, rotation, and success/failure paths. Shutdown checks the native host's
server cleanup and bounded log flush, including an occupied writer. These
managed tests do not exercise the Windows Native AOT/PowerShell boundary.

The integration test uses real HTTP, WASM, a canvas camera, and C# reconstruction.
It verifies file equality, browser log retention, offline recovery, collection,
and storage denial. Use isolated build, work, and log directories. Set
`Xfer_LOG_DIR` and `TMPDIR` to isolate host logs and temporary files, and use
`--artifacts-path /path/to/build` to isolate .NET build outputs.

`diagnostics_collection.py --stage /path/to/distribution --work /path/to/evidence`
uses a separately built Linux distribution and a private Xvfb display. It
checks automatic collection, WASM fetch failures, origin checks, size limits,
and sensitive data filtering. It requires Xvfb and Chromium and opens no
physical camera.

## Desktop UI and context menus

```sh
python tests/ui_drive.py --linux
```

On Windows, omit `--linux`. The script uses the desktop host's MCP interface to
exercise send, receive, phone, QR, and theme views, saving screenshots under
`tests/results/screens`.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test-desktop-context.ps1
```

This checks PowerShell syntax and context menu definitions without writing to
the registry. It also runs with PowerShell 7 on Linux. Native Windows startup,
shutdown, physical cameras, phones, and Tailscale connections require separate
checks on the corresponding devices.
