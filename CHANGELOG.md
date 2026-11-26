# Changelog

Player-facing changes per release. The section for a version opens its GitHub release notes and
the in-app "What's new" page.

## 0.2.0

- Non-Steam games you added to Steam (Battle.net, emulators, other launchers) are listed under Games with their Proton version, prefix, shader cache, Proton log and launch options, with the same actions as Steam games (no store page or mover).
- A GE-Proton build that only a non-Steam game uses is no longer suggested for cleanup.
- Proton logs of non-Steam games (named by their 64-bit game id) are found and opened correctly.

## 0.1.0

- First version: a controller-first system toolbox for SteamOS, Bazzite and Windows, with a separate desktop mode.
- Proton: every installed Proton and GE-Proton, which games use which, set a game's (or the default) compatibility tool, download and remove GE-Proton.
- Prefixes: open, back up, restore, reset (with a backup first) or delete a game's Proton prefix; install runtimes and fonts with protontricks; orphaned prefixes are flagged.
- Shader caches: size per game, clear one, the orphaned ones, or all.
- Storage: what takes space on each drive, move games between internal storage and the SD card, and a cleanup list with how much it frees.
- Launch options: edit them, or toggle presets (GameMode, Proton logging, DXVK/VKD3D flags, frame caps).
- Logs: read Proton logs with errors highlighted and hints for missing runtimes.
