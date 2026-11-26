#!/usr/bin/env bash
# UI smoke test: a fake $HOME with a fake Steam install, then drives the Godot app with scripted
# controller, keyboard and mouse input (--smoke) under Xvfb in both UI modes, checks what each
# step did and saves a screenshot per step.
#
#   tools/smoke/run-ui-smoke.sh <godot-mono-editor | exported DeckWrench.x86_64> <out-dir>
set -euo pipefail
GODOT=$(realpath "$1")
OUT=$(realpath -m "$2")
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

# Every XDG dir inside the fake home: CI runners set XDG_CONFIG_HOME.
export HOME="$WORK/home" XDG_RUNTIME_DIR="$WORK/run"
export XDG_CONFIG_HOME="$HOME/.config" XDG_DATA_HOME="$HOME/.local/share" XDG_CACHE_HOME="$HOME/.cache"
mkdir -p "$HOME" "$XDG_RUNTIME_DIR" "$OUT"
rm -f "$OUT"/*.png

# Games on internal and "SD card" libraries, Proton versions, prefixes, shader caches, a log.
"$ROOT/tools/smoke/fake-steam.sh" "$HOME" "$WORK/sdcard"

if [ -n "${SMOKE_THEME:-}" ]; then  # e.g. SMOKE_THEME=nightgrid for theme screenshots
  mkdir -p "$XDG_CONFIG_HOME/deckwrench"
  printf '{"Theme":"%s"}\n' "$SMOKE_THEME" > "$XDG_CONFIG_HOME/deckwrench/settings.json"
fi

# Offline stand-in for GitHub's GE-Proton releases API.
API="$WORK/api"
mkdir -p "$API"
cat > "$API/releases.json" <<'JSON'
[
 {"tag_name": "GE-Proton10-1", "name": "GE-Proton10-1", "published_at": "2026-11-20T10:00:00Z", "prerelease": false, "draft": false, "body": "Smoke test release.",
  "assets": [{"name": "GE-Proton10-1.tar.gz", "browser_download_url": "http://127.0.0.1:1/GE-Proton10-1.tar.gz", "size": 512000000},
             {"name": "GE-Proton10-1.sha512sum", "browser_download_url": "http://127.0.0.1:1/GE-Proton10-1.sha512sum", "size": 160}]},
 {"tag_name": "GE-Proton9-20", "name": "GE-Proton9-20", "published_at": "2026-09-01T10:00:00Z", "prerelease": false, "draft": false, "body": "",
  "assets": [{"name": "GE-Proton9-20.tar.gz", "browser_download_url": "http://127.0.0.1:1/GE-Proton9-20.tar.gz", "size": 498000000}]}
]
JSON
PORT=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])')
python3 -m http.server "$PORT" --bind 127.0.0.1 --directory "$API" >/dev/null 2>&1 &
API_PID=$!
trap 'kill $API_PID 2>/dev/null; rm -rf "$WORK"' EXIT
export DECKWRENCH_GE_API="http://127.0.0.1:$PORT/releases.json"
for _ in $(seq 50); do curl -sf "$DECKWRENCH_GE_API" >/dev/null && break; sleep 0.1; done

cd "$ROOT/src/DeckWrench.App"
PROJECT=(--path .)
[ "$(basename "$GODOT")" = "DeckWrench.x86_64" ] && PROJECT=()  # exported build: project is embedded
set +e
timeout 400 xvfb-run -a -s "-screen 0 1280x800x24" "$GODOT" "${PROJECT[@]}" --rendering-driver opengl3 \
  -- --windowed --smoke "$OUT" 2>&1 | tee "$OUT/godot.log"
code=${PIPESTATUS[0]}
set -e
cp "$XDG_DATA_HOME"/deckwrench/logs/app.log "$OUT/app.log" 2>/dev/null || true
if grep -qE "Unhandled exception|System\.[A-Za-z.]*Exception|smoke: step .* failed" "$OUT/godot.log"; then
  echo "::error::exceptions or failed steps during the UI smoke test (see godot.log)"
  exit 1
fi
shots=$(ls "$OUT"/*.png 2>/dev/null | wc -l)
echo "exit=$code screenshots=$shots"
[ "$code" -eq 0 ] && [ "$shots" -ge 40 ]
