# Linux setup

Xfer uses the included desktop rendering libraries in `runtime/linux-x64`.
Builds require the .NET 9 SDK. The desktop host also needs GTK 3 with X11
support, ffmpeg, and zenity or kdialog for file selection.

On NixOS, `./Xfer` enters the environment defined in `shell.nix`, builds the
host, and starts it:

```sh
./Xfer --path ./input
```

To build and start separately:

```sh
./tools/linux/build.sh
./Xfer.sh --path ./input
```

The build output is `artifacts/miniui-linux/Xfer.dll`. The desktop loads
`ui/index.html`. [Runtime components](../../runtime/README.md) lists the
included libraries.

Received files go to `output/` in the application folder. Set `--output` to
choose another location. Set `Xfer_CAMERA_DEVICE=/dev/videoN` to select a
V4L2 capture device explicitly.

Use `--window-at X,Y` to position the window in X11 pixels, `--window WxH`
to set its size, and `--fullscreen` to start fullscreen. With both
`--window-at` and `--fullscreen`, fullscreen starts only after the window
reaches the requested position, with a five-second timeout.

Host options include `--state-dir`, `--font-dir`, `--duration-ms`,
`--screenshot`, and `--mcp`. Use isolated state and output directories for
scripted UI tests.

## Desktop menu

```sh
./tools/linux/install-desktop.sh
```

This installs `Xfer.desktop` under
`${XDG_DATA_HOME:-$HOME/.local/share}/applications` for your account. It uses
the current installation path. Run it again after moving the folder.

## HTTP app

```sh
dotnet run --project Xfer.csproj -- --no-browser
```

Open the printed localhost URL in a browser. At the default port, Xfer uses
Tailscale Serve on HTTPS port 10000 for phone connections. A custom `--port`
skips Tailscale setup.
