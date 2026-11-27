#!/usr/bin/env bash
# Checks the one-line installer (get.sh) against a local stand-in release: a good archive
# installs, a tampered one is refused before anything is installed.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
WORK=$(mktemp -d)
mkdir -p "$WORK/build" "$WORK/home"
printf '#!/bin/sh\necho app\n' > "$WORK/build/DeckWrench.x86_64"
printf '#!/bin/sh\necho "steam-add $*" >> "$HOME/steam-add.log"\n' > "$WORK/build/deckwrench"
chmod +x "$WORK/build/DeckWrench.x86_64" "$WORK/build/deckwrench"
"$ROOT/tools/release/package.sh" "$WORK/build" 0.0.0 "$WORK/rel" >/dev/null
PORT=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])')
python3 -m http.server "$PORT" --bind 127.0.0.1 --directory "$WORK/rel" >/dev/null 2>&1 &
PID=$!
trap 'kill $PID 2>/dev/null; rm -rf "$WORK"' EXIT
for _ in $(seq 50); do curl -sf "http://127.0.0.1:$PORT/SHA256SUMS" >/dev/null && break; sleep 0.1; done
export HOME="$WORK/home" XDG_DATA_HOME="$WORK/home/.local/share" DECKWRENCH_RELEASE_URL="http://127.0.0.1:$PORT"
bash "$ROOT/tools/release/get.sh" --restart-steam
test -x "$HOME/Applications/DeckWrench/DeckWrench.x86_64"
test -f "$XDG_DATA_HOME/applications/deckwrench.desktop"
grep -q "steam-add --restart" "$HOME/steam-add.log"
# A download that doesn't match SHA256SUMS installs nothing.
rm -rf "$HOME/Applications"
echo tampered >> "$WORK/rel/DeckWrench-linux-x86_64.tar.gz"
if bash "$ROOT/tools/release/get.sh" 2>/dev/null; then echo "a tampered download was installed" >&2; exit 1; fi
test ! -e "$HOME/Applications/DeckWrench"
echo "get.sh OK"
