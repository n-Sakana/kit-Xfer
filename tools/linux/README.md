# Linux setup

Xfer runs the bundled Rust MiniUI executable and the Native AOT App library.
Using the desktop needs no .NET SDK, GTK host, or MiniUI source checkout.
See [runtime components](../../runtime/README.md) for OS requirements and hashes.

On NixOS, `./Xfer` enters the runtime environment in `shell.nix` and starts the
included executable. It does not build anything:

```sh
./Xfer --path ./input
```

On other Linux systems, run `./Xfer.sh --path ./input`.
To rebuild the App, install the .NET 9 SDK, clang and zlib development files,
then run `./tools/linux/build.sh`. On NixOS that script enters its own build
environment. It writes `ui/app/miniui-app.so` and `runtime/linux-x64/xfer-cli`.
The MiniUI executable is distributed as a built component.

Received files go to `output/` in the application folder. Set `--output` to
choose another location. Set `Xfer_CAMERA_DEVICE=/dev/videoN` to select a
V4L2 capture device explicitly. ffmpeg captures BGRA; the existing receiver
automatically detects Tile and QR.

Use `--window-at X,Y` to position the window in X11 pixels, `--window WxH`
to set its size in CSS pixels, and `--fullscreen` to start fullscreen. `--windowed`
keeps Tile in the window, useful when positioning it on a secondary monitor. Placement
waits up to five seconds before entering fullscreen. Tile automatically enters fullscreen
and Esc returns to the window. Host options include `--state-dir`, `--font-dir`,
`--duration-ms`, `--screenshot`, and `--mcp`.

Both desktop platforms use ABI v2 image limits: 8,192 device pixels per side
and 33,554,432 pixels (128 MiB BGRA). Beyond that, Tile uses a centred viewport
with the same device pixels per cell; the data is never stretched to fit.

For a camera-free check against a recording of another Xfer window, set
`Xfer_CAMERA_RECORDING=/absolute/path/screen.mkv`. The receive view lists it as
**Recorded screen** and uses ffmpeg to replay it through the normal Tile/QR
receiver. The recording repeats until stopped, allowing missing blocks to be
read on later passes. Preserve all captured frames: replay cannot recover
data that was never captured. Capture without lossy compression or resized cells;
the capture-width setting determines the decoded picture size. Unset the
variable to use V4L2 cameras again.

## Desktop menu

```sh
./tools/linux/install-desktop.sh
```

This installs `Xfer.desktop` under
`${XDG_DATA_HOME:-$HOME/.local/share}/applications` for your account. It uses
the current installation path. Run it again after moving the folder.

## HTTP app

```sh
./Xfer.sh --no-browser
```

Open the printed localhost URL in a browser. At the default port, Xfer uses
Tailscale Serve on HTTPS port 10000 for phone connections. A custom `--port`
skips Tailscale setup. `./Xfer.sh --help` prints command-line options.
