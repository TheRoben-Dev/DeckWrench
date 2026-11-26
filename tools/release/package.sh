#!/usr/bin/env bash
# Packages an exported build into the release files the in-app updater expects:
#   DeckWrench-linux-x86_64.tar.gz (one folder, DeckWrench/) and SHA256SUMS.
#   tools/release/package.sh <export dir with DeckWrench.x86_64 and deckwrench> <version> <out dir>
set -euo pipefail
BUILD=$(realpath "$1"); VERSION="$2"; OUT=$(realpath -m "$3")
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
APP="$WORK/DeckWrench"
mkdir -p "$APP" "$OUT"
cp -a "$BUILD"/. "$APP/"
cp -a "$ROOT/data/steam-art" "$APP/steam-art"
cp "$ROOT/tools/release/install.sh" "$APP/install.sh"
cp "$ROOT/LICENSE" "$APP/LICENSE" 2>/dev/null || true
printf 'DeckWrench %s (Linux x86_64)\n\nRun ./install.sh to install for this user, or start DeckWrench.x86_64 from here.\nhttps://github.com/TheRoben-Dev/DeckWrench\n' "$VERSION" > "$APP/README.txt"
test -x "$APP/DeckWrench.x86_64" && test -x "$APP/deckwrench"
tar -C "$WORK" -czf "$OUT/DeckWrench-linux-x86_64.tar.gz" DeckWrench
(cd "$OUT" && sha256sum DeckWrench-linux-x86_64.tar.gz > SHA256SUMS)
cat "$OUT/SHA256SUMS"
