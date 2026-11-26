#!/usr/bin/env bash
# Installs DeckWrench for this user: ~/Applications/DeckWrench, a menu entry and
# (when Steam is closed, or with --restart-steam) a Steam shortcut with DeckWrench's art.
# Run from the unpacked release folder:  ./install.sh   (re-run to repair; updates happen in-app)
set -euo pipefail
SRC=$(cd "$(dirname "$0")" && pwd)
DEST="${DECKWRENCH_INSTALL_DIR:-$HOME/Applications/DeckWrench}"
mkdir -p "$(dirname "$DEST")"
if [ "$SRC" != "$DEST" ]; then
  rm -rf "$DEST.installing"
  cp -a "$SRC" "$DEST.installing"
  rm -rf "$DEST"
  mv "$DEST.installing" "$DEST"
fi
chmod +x "$DEST/DeckWrench.x86_64" "$DEST/deckwrench"

APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$APPS"
cat > "$APPS/deckwrench.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=DeckWrench
Comment=Controller-first system toolbox: Proton, prefixes, shader cache, storage, launch options, logs
Exec="$DEST/DeckWrench.x86_64"
Icon=$DEST/steam-art/icon.png
Terminal=false
Categories=Game;Utility;
DESK
command -v update-desktop-database >/dev/null && update-desktop-database "$APPS" || true

echo "Installed to $DEST"
if [ "${1:-}" = "--restart-steam" ]; then
  "$DEST/deckwrench" steam-add --restart || true
else
  "$DEST/deckwrench" steam-add || echo "Add DeckWrench to Steam later from DeckWrench → Settings → Updates, or run: $DEST/install.sh --restart-steam"
fi
