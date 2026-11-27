#!/usr/bin/env bash
# One-line install (SteamOS / Bazzite / Linux, Desktop Mode):
#   curl -fsSL https://raw.githubusercontent.com/TheRoben-Dev/DeckWrench/main/tools/release/get.sh | bash
# Downloads the latest release, checks it against SHA256SUMS, and runs its install.sh
# (~/Applications/DeckWrench, a menu entry and a Steam shortcut). Pass --restart-steam to add the
# Steam shortcut right away:  ... | bash -s -- --restart-steam
set -euo pipefail
BASE="${DECKWRENCH_RELEASE_URL:-https://github.com/TheRoben-Dev/DeckWrench/releases/latest/download}"
FILE=DeckWrench-linux-x86_64.tar.gz
WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
cd "$WORK"
echo "Downloading DeckWrench…"
curl -fL --retry 3 --progress-bar -o "$FILE" "$BASE/$FILE"
curl -fsSL --retry 3 -o SHA256SUMS "$BASE/SHA256SUMS"
grep " $FILE\$" SHA256SUMS | sha256sum -c - >/dev/null || { echo "The download doesn't match SHA256SUMS; nothing was installed." >&2; exit 1; }
tar xzf "$FILE"
DeckWrench/install.sh "$@"
echo "Done. Open DeckWrench from the app menu, or from Steam (Library → Non-Steam) in Game Mode."
