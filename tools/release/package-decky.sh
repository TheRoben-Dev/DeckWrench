#!/usr/bin/env bash
# Packages the built Decky Loader plugin (decky/, after `pnpm run build`) as DeckWrench-decky.zip:
# one DeckWrench/ folder with plugin.json, package.json, main.py, dist/ and the license, the layout
# Decky's "Install plugin from ZIP" expects.
#   tools/release/package-decky.sh <decky dir> <out dir>
set -euo pipefail
SRC=$(realpath "$1"); OUT=$(realpath -m "$2")
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
P="$WORK/DeckWrench"
mkdir -p "$P/dist" "$OUT"
test -f "$SRC/dist/index.js" || { echo "build the plugin first (pnpm run build)" >&2; exit 1; }
cp "$SRC/plugin.json" "$SRC/package.json" "$SRC/main.py" "$SRC/README.md" "$P/"
cp "$SRC/dist/index.js" "$P/dist/"
cp "$ROOT/LICENSE" "$P/LICENSE"
(cd "$WORK" && python3 -c "import shutil; shutil.make_archive('DeckWrench-decky', 'zip', '.', 'DeckWrench')")
mv "$WORK/DeckWrench-decky.zip" "$OUT/"
echo "$OUT/DeckWrench-decky.zip"
