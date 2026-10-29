# Xfer

Xfer transfers files and folders from a screen to a camera. The desktop app can
send or receive using animated Tile patterns or QR codes. A phone can also send
and receive QR codes through the web app.

## Windows

Keep the repository's files together and double-click `Xfer.vbs`.
Use `Xfer.cmd` to show startup messages. The included desktop app requires
64-bit Windows and Windows PowerShell 5.1; it does not require a .NET SDK or
WebView2.

Run `install.bat` to add **Xfer** to the file, folder, and background context
menus for your Windows account. On Windows 11, look under **Show more options**.
Run `uninstall.bat` to select and remove a registration; it backs up registry
entries before deleting them. Run the installer again if you move the folder.

## Linux

On NixOS, run:

```sh
./Xfer --path ./input
```

On other Linux systems, install the .NET 9 SDK, GTK 3 with X11 support, ffmpeg,
and either zenity or kdialog, then run:

```sh
./tools/linux/build.sh
./Xfer.sh --path ./input
```

See [Linux setup](tools/linux/README.md) for desktop integration and camera options.

## Send and receive

1. On the sending computer, choose files or a folder and select the files to send.
2. Choose **Tile** for a desktop receiver or **QR** for a phone receiver, then
   click **Show on this screen**.
3. On the receiving device, choose **Receive**, start the camera, and point it
   at the sending screen. Keep the camera steady until the transfer finishes.

The desktop receiver detects Tile and QR automatically. Tile uses independent
patches, so it can read a partially visible screen. QR requires the entire code
to be visible. Tile sending uses fullscreen; move the pointer to the bottom edge
or press Tab to show the controls. Press Esc to stop and restore the window.

The default Tile settings are 3 px cells, 30 fps, error correction L, and no
color layer. QR defaults to 1,000 bytes per frame, 30 fps, and error correction L.
If reception stalls, enlarge Tile cells or enable the 2× coarse center. For QR,
reduce the bytes per frame or frame rate. Increase the screen brightness and
allow the camera a few seconds to focus.

Received files are saved under `output/` in the application folder unless you
set `--output`. The receiver checks file hashes before completing a transfer.
Large selections use a disk-backed streaming format; allow up to twice the
transfer size in free disk space. A transfer cannot resume after restarting the
app. See [optical formats](docs/optical-format.md) for details.

## Phone connection

The **Phone** page shows a pairing QR code when Tailscale is available. Scan it
with your phone to open the web app over HTTPS. The phone and computer must be
able to connect through Tailscale. At the default local port, Xfer configures
`tailscale serve` to forward HTTPS port 10000 to its local server. A custom
`--port` skips this setup.

The phone web app uses QR codes. Select **QR** on the sending desktop before
reading its screen with a phone. You can install the web app as a PWA from your
browser. Browser camera access requires HTTPS or localhost.

## Command-line options

```sh
./Xfer --path ./input --port 18431
./Xfer --path ./document.pdf --send --layout fx2 --cell 3 --fps 30 --ec L
./Xfer --receive --camera-width 1920 --camera-fps 30 --workers 3 --output ./received
```

On Windows, use `.\Xfer.cmd` with the same arguments. Tile is called `fx2` in
command-line arguments and stored settings. `--send` and `--receive` start the
requested operation when the desktop window opens. Their settings apply to that
launch and are not saved. Run with `--help` for the full option list.

To run the HTTP app without a desktop window:

```sh
dotnet run --project Xfer.csproj -- --no-browser
```

Open the localhost URL printed at startup.

## Build and test

Building requires the .NET 9 SDK. On Windows, run
`tools\windows\build.cmd` with the Visual Studio C++ build tools installed;
this rebuilds `ui\MiniUI.App.dll` and the launcher DLL. On Linux, use
`tools/linux/build.sh`. The desktop rendering libraries are included under
`runtime/`; see [runtime components](runtime/README.md).

[Test instructions](tests/README.md) cover the transfer formats, desktop host,
browser, and diagnostics. [Diagnostic logs](docs/diagnostics.md) describes log
locations, collection, and retention.

## License

Xfer is based on
[Decimen Optical Transfer](https://github.com/bashalarmistalt/decimen-optical-transfer)
v0.3.0 and is released under the [MIT license](LICENSE).
Included libraries and fonts are listed in [third-party notices](THIRD-PARTY-NOTICES.md).
