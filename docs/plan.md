# DeckWrench plan

Phased pull requests, each with CI green (build, unit tests, headless UI smoke test with
screenshots, Linux and Windows exports).

## Phase 1: the toolbox (this PR)

- Solution structure from Luma Modman: Core (no Godot), Host, CLI, App, tests; themes, sounds,
  animation, controller navigation, self-update, Steam shortcut with art, installers.
- Detection: Steam (every library, SD card, Flatpak), Heroic, Lutris; compat tools, prefixes,
  shader caches, Proton logs, drives.
- All six sections with their actions, in controller and desktop mode, from one shared catalog.
- Steam config writes (CompatToolMapping, LaunchOptions) with backups, and the waiting-changes queue
  with a Steam-restart helper for when Steam is running.
- Confirmations with sizes and contents for every destructive action; backups before resets.
- Unit tests, a scan test against the fake Steam install, and a 90-step UI smoke test in both modes
  with a parity check.

## Phase 2: hardening on real hardware

- Done: non-Steam shortcuts as games (Proton, prefix, shader cache, log, launch options in shortcuts.vdf).

- Run on a Steam Deck (SteamOS stable and beta) and Bazzite; fix what real libraries show up.
- Steam's own mover from Game Mode (`steam://` deep links) where it exists for the running client.
- ~~Non-Steam shortcuts (Heroic/Lutris launched through Steam): their compatdata, names and art.~~ Done in 0.2.0.
- ~~Faster rescans (cache folder sizes by mtime).~~ Done in 0.6.0.

## Phase 3: extras

- ~~protontricks verbs beyond the curated list; custom verb lists in the config folder.~~ Done in 0.3.0.
- ~~Per-game notes on known fixes (from ProtonDB-style data the user adds).~~ Done in 0.5.0, with a ProtonDB link.
- Shader cache pre-caching controls.
- ~~A "free X GB on the SD card" planner.~~ Done in 0.4.0.

## Phase 4: worth downloading (picked 2026-11-27)

1. ~~First impression: a Home checkup page, README pitch, one-line installer.~~ Done in 0.12.0.
2. ~~"Fix my game": ProtonDB tier and Deck Verified status on a game's page, with try-next steps.~~ Done in 0.13.0.
3. ~~Save backup and restore (Ludusavi's manifest), automatic before prefix resets and deletes.~~ Done in 0.14.0.
4. ~~System tuning presets: swap size, swappiness, transparent huge pages, compaction, with a revert.~~ Done in 0.14.0 (needs a real Deck check).
5. ~~A Decky Loader companion for the Quick Access menu.~~ Done in 0.14.0 (needs a real Deck check).
