#!/usr/bin/env bash
set -euo pipefail
Xfer_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
if ! command -v dotnet >/dev/null 2>&1; then
  echo 'Xfer build needs .NET SDK 9. On NixOS use ./tools/linux/run-nixos.sh.' >&2
  exit 1
fi
dotnet publish "$Xfer_root/desktop/Xfer.MiniUI.csproj" -c Release --self-contained false \
  -o "$Xfer_root/artifacts/miniui-linux"
