# Desktop runtime components

Linux and Windows run the bundled Rust MiniUI Core at revision
`73d3310ac3078d7c6b52badd7537546d24c46fe3`, replacing
`ecbd80e4d5554ced637764a6eb001716ed977ff1` on both platforms. No MiniUI checkout
is used when building or launching Xfer. This repository ships Linux x64 and
Windows x64 desktop runtimes; it has no macOS runtime or entry point.

| Platform | Core | Xfer app (ABI v2) | Number formatting |
| --- | --- | --- | --- |
| Linux x64 | `linux-x64/miniui` | `../ui/app/miniui-app.so` | `../ui/parts/Intl/miniui-intl.so` |
| Windows x64 | `windows-x64/MiniUI.Engine.dll`, `MiniUI.Bridge.dll` | `../ui/app/miniui-app.dll` | `../ui/parts/Intl/miniui-intl.dll` |

Core was refreshed on 2026-11-06 from `kit/miniUI` main. Linux is built with
MiniUI's `crates/tools/pack/linux.py` in its fixed Ubuntu 20.04 / Rust 1.95.0
distribution environment, retaining the glibc 2.31 baseline and standard ELF
interpreter. Windows uses a `MiniUI.Engine.dll` rebuilt from the same
revision (SHA-256 `bcebf0bdae31b6404f482774495a6097b0574c7d7047a63ca1440fd7e2ac83e1`).
The Linux builder compiles and verifies Core, but its final packaging step
rejects the result against MiniUI's 8,000,000-byte size limit. Xfer includes the
verified executable after `strip --strip-all` in the same builder image:
8,056,288 bytes, 56,288 bytes above that limit. The source and optimization
settings are unchanged; the size exception is recorded in the manifest.
`MiniUI-manifest.json` records each engine's target, size, hash and source
revision; each platform's `MINIUI_SOURCE_REVISION` records the same revision.
The unchanged Windows Bridge
(SHA-256 `c4900bcd3a006e5ef7b22f5b1417de63a10413016b20b15a0b93708f5a1e7e2b`)
is the ABI v2 Bridge also used by Henkan. Intl comes from the shimane 2026-11-01
delivery; its per-platform component manifests are in `ui/parts/Intl/`.
Image and Media Parts are unnecessary: Xfer supplies its own BGRA transfer and
camera images through the App ABI. Intl uses the OS ICU, without bundled ICU data.

`Xfer.sh` launches Core with `--app-id org.xfer.desktop`; `Xfer.ps1` supplies
`appId`, `root`, `stateDir`, `window` and `arguments` in the launch JSON.
The root is `ui/`, so Core loads `ui/app/miniui-app.{so,dll}`. Xfer keeps its
C# transfer, camera and server implementation, compiled with .NET Native AOT.
No .NET runtime or SDK is needed to use the desktop distribution. Core owns
module shutdown and Xfer flushes its diagnostics there.

Both Xfer App libraries were rebuilt from this repository's sources on
2026-11-07, including the streaming diagnostics from `3c63799`. Their image
budget now matches Core's 128 MiB instead of the old 64 MiB app-side default.
The Linux App also accepts a recorded screen through its existing ffmpeg
capture path; see [Linux setup](../tools/linux/README.md).
Streaming interleaves balanced groups of at most 128 segments with rotating positions,
selects dense existing fountain repair frames, and reduces received equations within
256 bounded windows. One in ten display frames repairs previously sent segments
while the main pass continues. Its
48-byte header and the QR/Tile identifiers are unchanged; see
[optical formats](../docs/optical-format.md).

Linux requires x86-64, glibc 2.34+, X11, OS ICU 67+, zlib, OpenSSL, ffmpeg,
and zenity or kdialog. Core itself has a glibc 2.31 baseline; the included
Xfer Native AOT app and `xfer-cli` require 2.34. NixOS uses the environment in
`tools/linux/shell.nix` and the host's standard ELF loader/nix-ld. Distribution
binaries contain no Nix store runtime search paths. `linux-x64/xfer-cli` loads
the same App library for `--help` and the windowless `--no-browser` mode.
Windows requires x64 Windows and Windows PowerShell 5.1. Intl uses System32 ICU.

Rebuild Xfer with `tools/linux/build.sh` or `tools\windows\build.cmd` (.NET 9 SDK,
and a native compiler; Windows also needs Visual Studio C++ build tools).
The managed `desktop/Xfer.MiniUI.csproj` exists only for the existing optical
probes; it has no rendering engine dependency.

When replacing Core, keep its platform files together and update
`MiniUI-manifest.json` and both `MINIUI_SOURCE_REVISION` files. Build Linux Core
with MiniUI's distribution builder above, keeping its build cache and outputs
outside this repository, and run `miniui-pack verify` on both engine binaries.
After replacing Core or rebuilding the App, regenerate `MiniUI-SHA256SUMS` and
root `SHA256SUMS.txt`.
Verify startup, selection, optical display/reconstruction, settings, and shutdown.

```sh
sha256sum -c runtime/MiniUI-SHA256SUMS
```

Licenses: `MiniUI-THIRD-PARTY-NOTICES.md`, `windows-x64/licenses/` and
`../ui/parts/Intl/NOTICE.txt`.
