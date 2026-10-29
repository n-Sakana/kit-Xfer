#!/usr/bin/env bash
set -euo pipefail
Xfer_args=()
if [[ -n "${Xfer_ARGUMENTS_BASE64:-}" ]]; then
  while IFS= read -r -d '' argument; do Xfer_args+=("$argument"); done < <(printf '%s' "$Xfer_ARGUMENTS_BASE64" | base64 --decode)
fi
"$Xfer_SOURCE_ROOT/tools/linux/build.sh"
exec "$Xfer_SOURCE_ROOT/Xfer.sh" "${Xfer_args[@]}"
