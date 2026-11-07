#!/usr/bin/env bash
set -euo pipefail
Xfer_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
for Xfer_argument in "$@"; do
  case "$Xfer_argument" in
    --help|-h|--no-browser)
      exec "$Xfer_root/runtime/linux-x64/xfer-cli" "$Xfer_root/ui/app/miniui-app.so" "$Xfer_root" "$@" ;;
  esac
done
Xfer_host_args=()
Xfer_app_args=()
while (( $# )); do
  case "$1" in
    --mcp|--headless|--no-system-fonts|--no-log) Xfer_host_args+=("$1"); shift ;;
    --state-dir|--screenshot|--duration-ms|--font-dir|--scale|--width|--height|--dump-dom)
      if (( $# < 2 )); then echo "Xfer: missing value for $1" >&2; exit 1; fi
      Xfer_host_args+=("$1" "$2"); shift 2 ;;
    *) Xfer_app_args+=("$1"); shift ;;
  esac
done
exec "$Xfer_root/runtime/linux-x64/miniui" "$Xfer_root/ui/index.html" \
  --app-id org.xfer.desktop --root "$Xfer_root/ui" --title Xfer --width 1180 --height 800 \
  "${Xfer_host_args[@]}" -- --home "$Xfer_root" "${Xfer_app_args[@]}"
