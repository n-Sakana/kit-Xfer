# Desktop runtime components

Xfer uses the rendering libraries included here for desktop builds and tests.

| Platform | Components |
| --- | --- |
| Linux x64 | `linux-x64/MiniUI.Core.dll`, `linux-x64/MiniUI.Platform.Linux.dll` |
| Windows x64 | `windows-x64/MiniUI.Engine.dll`, `windows-x64/MiniUI.Bridge.dll` |

`windows-x64/Xfer.Launcher.dll` is Xfer's shutdown bridge for Windows
PowerShell 5.1. `ui/MiniUI.App.dll` contains the Windows application code.
Build both Xfer components with `tools\windows\build.cmd`.

`linux-x64/MINIUI_SOURCE_REVISION` identifies the included Linux rendering
library source revision. A source revision is not available for the included
Windows rendering libraries. See [third-party notices](MiniUI-THIRD-PARTY-NOTICES.md)
for licenses.

Check the runtime files from the repository root:

```sh
sha256sum -c runtime/MiniUI-SHA256SUMS
```

When replacing rendering libraries, replace each platform's pair together,
update its source revision and notices, and regenerate the checksums. Verify
startup, file selection, optical display, and shutdown on that platform.
