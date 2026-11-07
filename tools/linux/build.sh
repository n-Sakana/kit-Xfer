#!/usr/bin/env bash
set -euo pipefail
Xfer_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
if ! command -v dotnet >/dev/null 2>&1 && [[ -e /etc/NIXOS ]]; then
  exec nix-shell "$Xfer_root/tools/linux/shell.nix" --arg withBuildTools true \
    --run 'exec "$Xfer_SOURCE_ROOT/tools/linux/build.sh"' --argstr XferRoot "$Xfer_root"
fi
# Only Xfer's app component is built here; MiniUI is shipped in runtime/.
dotnet publish "$Xfer_root/native/Xfer.Native.csproj" -c Release -r linux-x64 \
  -o "$Xfer_root/artifacts/native-linux-x64"
if command -v patchelf >/dev/null 2>&1; then
  patchelf --remove-rpath "$Xfer_root/artifacts/native-linux-x64/miniui-app.so"
fi
install -m 755 "$Xfer_root/artifacts/native-linux-x64/miniui-app.so" "$Xfer_root/ui/app/miniui-app.so"
cc -Os -s "$Xfer_root/native/Linux/cli.c" -ldl -o "$Xfer_root/runtime/linux-x64/xfer-cli"
if command -v patchelf >/dev/null 2>&1; then
  patchelf --set-interpreter /lib64/ld-linux-x86-64.so.2 --remove-rpath "$Xfer_root/runtime/linux-x64/xfer-cli"
fi
