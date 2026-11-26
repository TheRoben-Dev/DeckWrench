# Changelog

Player-facing changes per release. The section for a version opens its GitHub release notes and
the in-app "What's new" page.

## 0.9.0

- Proton: "Use for games" on any Proton version ticks the games that run with it; tick more to switch them over in one go, untick one to set it back to Steam's default. Handy after installing a new GE-Proton.
- Games: "Copy launch options from another game".

## 0.8.0

- "Save a report" (View / Select, and in desktop mode): a Markdown file with your system, drives, Proton versions, every game's Proton, launch options and prefix, recent Proton log errors, your notes and what can be cleaned up, to paste when asking for help. Your home folder is written as ~. Also `deckwrench report` on the command line.

## 0.7.0

- Prefixes: "Back up every prefix" saves all your games' prefixes in one go (pick which), with progress and a log.
- Prefixes: "Keep only the newest backups" deletes older backups, keeping the newest one (or 2, 3, 5) of each prefix, and lists every archive with its size first.
- Cleanup never suggests a prefix's newest backup, however old.
- Two backups of the same prefix in the same second no longer clash.

## 0.6.0

- Faster rescans: folder sizes are remembered between scans, and only folders whose contents changed are measured again. Rescans after an action and at start use them; "Scan again" still measures everything.

## 0.5.0

- Notes: add your own note to any game (what fixed it, which Proton works). It shows first on the game's page and is kept in notes.json in the config folder.
- Steam games get "Reports on ProtonDB", which opens in Steam's browser in Game Mode.
- Controller mode: pages no longer drift to the left after going back through several pages quickly.

## 0.4.0

- Storage: "Free space on <drive>" for each drive with a Steam library. It lists what can go from that drive (orphaned caches and prefixes, unused Proton, logs, old backups) and its biggest games with the drive they'd move to, then shows the total and everything it touches before you confirm. Moves only happen while Steam is closed, and never when the other drive would end up with less than 1 GB free.

## 0.3.0

- Run any other winetricks verb in a game's prefix: type the verbs (Steam's keyboard in Game Mode). Options and anything that isn't a verb are refused before protontricks runs.
- Your own protontricks verbs from `tricks.json` in the config folder show up under "Your verbs" in the runtime and font picker.

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
