#!/usr/bin/env bash
set -euo pipefail

ARCH="${1:-x64}"
RID="linux-$ARCH"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUTDIR="$SCRIPT_DIR/publish"
PROJECT_ROOT="$SCRIPT_DIR/.."

echo "==> Publishing Open Burning Suite for $RID ..."
dotnet publish "$PROJECT_ROOT/OpenBurningSuite/OpenBurningSuite.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained \
  -o "$OUTDIR"

echo "==> Creating ZIP archive: openburningsuite-$RID.zip"
cd "$OUTDIR"
zip -9 -r "$SCRIPT_DIR/openburningsuite-$RID.zip" .
cd "$SCRIPT_DIR"

echo "==> Done! openburningsuite-$RID.zip ready."
