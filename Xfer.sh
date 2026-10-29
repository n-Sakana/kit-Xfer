#!/usr/bin/env bash
set -euo pipefail
Xfer_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
Xfer_host="${Xfer_MINIUI_DIR:-$Xfer_root/artifacts/miniui-linux}"
if [[ -f "$Xfer_host/Xfer.dll" ]]; then
  if ! command -v dotnet >/dev/null 2>&1; then
    echo 'Xfer: .NET 9 runtime is missing. On NixOS use: ./tools/linux/run-nixos.sh' >&2
    exit 1
  fi
  exec dotnet "$Xfer_host/Xfer.dll" --home "$Xfer_root" "$@"
fi
echo 'Xfer MiniUI is not built. Run ./tools/linux/build.sh or ./tools/linux/run-nixos.sh on NixOS.' >&2
exit 1
