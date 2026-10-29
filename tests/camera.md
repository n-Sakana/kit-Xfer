# Camera tools

`Xfer.CameraLab.csproj` provides a Windows Media Foundation capture utility.
It records camera formats, controls, frame timing, and full-frame QR decoding
with one worker. It measures capture and decoding rather than the desktop
receiver's parallel tracking or end-to-end file throughput.

```powershell
dotnet build tests/Xfer.CameraLab.csproj -c Release -o artifacts/camera
$cameraLab = 'artifacts/camera/Xfer.CameraLab.dll'
dotnet $cameraLab selftest
dotnet $cameraLab list
dotnet $cameraLab formats CameraName --out results/formats.json
dotnet $cameraLab capture results/capture 1920 30 10 CameraName --height 1080 --warmup 2 --exposure auto --focus auto --gain keep
```

`selftest` needs no camera. `list`, `formats`, `capture`, and `focus-hold`
require Windows and access to the device. Close other capture apps before use.
Output directories must be new or empty.

Capture syntax is `capture OUT WIDTH FPS SECONDS [CAMERA] [options]`.
`SECONDS` excludes warmup and starts from the first delivered frame. Native
frame rates must match within 0.05 fps; fractional rates are accepted.

| Option | Meaning |
| --- | --- |
| `--height N` | Require a specific height as well as width |
| `--focus keep\|auto\|N` | Preserve, autofocus, or manual device value; default `keep` |
| `--exposure keep\|auto\|N` | Preserve, automatic, or manual log2 seconds (−7 = 1/128 s) |
| `--gain keep\|auto\|N` | Preserve, automatic, or manual device value |
| `--warmup S` | Warmup duration; default 2 seconds |
| `--modules auto\|N` | Detect QR dimension or specify 21, 25, …, 177 modules, excluding quiet zone |
| `--queue N` | Analysis queue limit, 1–128; default 8 |
| `--save-all N` | Save analyzed images among the first N captures as PGM |
| `--max-misses N` | Maximum failed-decode images to save; default 5 |
| `--save-every N` | Save analyzed captures whose index is a multiple of N; 0 disables |
| `--probe` | Also print control readbacks to the console |
| `--no-restore` | Skip restoring the original camera controls |
| `--roi X,Y,W,H` | Fixed region for row differences; otherwise use the first detected transfer QR |
| `--row-diffs` | Save per-row mean absolute differences |
| `--af-lock-after S` | Lock the current focus after a successful decode at or after S seconds |
| `--af-lock-settle S` | Exclude this duration after locking from comparisons; default 0.5 seconds |

The utility checks control ranges, steps, and supported modes. It reads back
settings immediately and about every 500 ms. All original settings are captured
before any change and restored on exit unless `--no-restore` is set. `keep`
does not prevent changes caused by another automatic control. A successful
restoration readback does not guarantee that a driver will keep the value later.

## Output

| File | Contents |
| --- | --- |
| `run.json` | Requested settings, device details, start time, binary hashes, OS, and runtime |
| `summary.json` | Negotiated format, frame counts, dropped frames, decode statistics, controls, and focus comparison |
| `captures.csv` | Every capture's index, time, interval, phase, and queue acceptance |
| `frames.csv` | Decode results, payload identity, uniqueness, geometry, brightness, timings, focus phase, and boundary estimates |
| `controls.jsonl` | Original, applied, sampled, locked, and restored controls |
| `row-differences.jsonl` | Optional raw row differences and boundary estimates |
| `frame-*.pgm`, `miss-*.pgm` | Analyzed grayscale images, numbered by capture index |
| `trace/` | Application diagnostic events |

`DecodeRate` is valid Xfer packets divided by analyzed images; it is not the
fraction of sent frames received. `QrDecodeRate` includes other QR codes.
`NegotiatedFps` describes the selected format; `DeliveredFps` uses actual callback
timing. Queue drops are counted separately.

`UniqueQr` counts distinct packet SHA-256 hashes within each aggregation phase.
`UniqueSequences` uses session, payload FNV, lengths, and sequence. Phase-local
unique counts may overlap. CSV uniqueness is measured over the whole run.
Module size is estimated from finder-center distances divided by dimension−7.
Horizontal and vertical estimates are separate for perspective views.

Row differences estimate a single change point between consecutive images in a
fixed ROI. A candidate at the search boundary is retained as `CandidateRow`
but excluded from `Row`. Confidence is explained variance, not a probability.
Motion, exposure, and focus changes can also create boundaries; inspect the
raw differences and images before interpreting them as mixed display frames.

Exit codes are 0 for completion, 2 for argument/device/output errors, 3 for
control mismatch, restoration failure, or incomplete focus comparison, and
130 for interruption. Ctrl+C drains queued analysis before writing results.
Zero successful decodes can still be a completed capture.

## Reports and focus control

```powershell
python tests/camera_lab_report.py results/capture
python tests/camera_lab_report.py --plots results/capture
dotnet $cameraLab focus-hold results/focus CameraName
```

Reports cross-check counts and write `analysis.json` and `seconds.csv`.
`--plots` additionally requires matplotlib and NumPy. `focus-hold` checks the
application camera adapter from a worker thread, without a window. Start with
autofocus enabled. It checks holding, releasing, preserving manual settings,
and restoring autofocus on exit; it does not measure reception improvement.

Linux focus control uses V4L2 camera controls on the capture device and does
not require `v4l2-ctl`. The camera utility itself uses the Windows adapter.
