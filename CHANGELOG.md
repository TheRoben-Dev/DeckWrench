# Changelog

Player-facing changes per release. The section for a version opens its GitHub release notes and
the in-app "What's new" page.

## 0.14.0

- Save backups: "Back up saves" on a game's page keeps a small archive of its save and config files, found with Ludusavi's community list of where thousands of games keep them (downloaded weekly), plus Steam's own files for the game. Games the list doesn't know fall back to their folders in the prefix.
- "Restore saves" puts a backup back into the game's folders where they are now (after a move to the SD card or a prefix reset too), lists every file it adds or replaces first, and backs up the current files before writing.
- "Back up every game's saves" on the Games page, and Home now flags played games whose saves were never backed up.
- Saves are backed up by themselves before a prefix reset, a prefix delete and an uninstall.
- `deckwrench saves [game]` lists where each game's save files are; `deckwrench backup-saves [game]` backs them up.
- SteamOS and Bazzite: "Tune memory for games" on Home applies the changes CryoUtilities made popular (swappiness 1, transparent huge pages, no proactive compaction, fair page locks, and a bigger swap file on SteamOS when there's room), after showing every change. It asks for your password (typed as dots; Steam's keyboard in Game Mode) and keeps the old values so "Revert memory tuning" puts them back. Home says when a SteamOS update reset them. Also `deckwrench tune`.
- Decky Loader plugin (DeckWrench-decky.zip in the release): DeckWrench in the Quick Access menu, with Home's findings, free space per drive, Steam changes waiting for a restart with "Apply now", and "Fix in DeckWrench" to open the app. Also `deckwrench status --json`.

## 0.13.0

- A Steam game's page shows its ProtonDB tier (with how many reports, and where recent ones are heading) and Valve's Steam Deck rating with Valve's notes ("Some text is small and hard to read"). They're fetched in the background, kept for a few days, and can be turned off in Settings → Scanning (only the games' app ids are sent).
- "Try Proton - Experimental" / "Try GE-Proton…": one press switches a game to the usual next Proton to try (the newest GE-Proton for a game on Valve's Proton, Experimental for one on GE). Home offers it for games that crash or are set to a Proton that's missing.
- A game's page lists what to try, in order, when it won't start or crashes.

## 0.12.0

- New Home page, first in both layouts: a checkup after every scan that lists what needs your attention, worst first. Games set to a Proton that isn't installed, crashes and missing DLLs in Proton logs (the runtimes that fix them come already ticked), drives that are nearly full, space that can be freed, prefixes with no backup, and Steam changes waiting for a restart. Each one opens with the action that deals with it.
- `deckwrench checkup` prints the same list (exit code 1 when something needs fixing).
- One-line install: `curl -fsSL https://raw.githubusercontent.com/TheRoben-Dev/DeckWrench/main/tools/release/get.sh | bash`, or double-click Install-DeckWrench.desktop from the release. The download is checked against SHA256SUMS.
- Controller mode: the top bar fits the Deck's screen again with the extra tab.

## 0.11.0

- Games and Storage show when each Steam game was last played and how many hours it has (from Steam's own records), with a sortable "Played" column in desktop mode. Sort by it to find big games you haven't touched in months.
- The free-space planner and the saved report mention last played and hours too.
- Games: "Play", "Verify game files" and "Uninstall…" for Steam games. Uninstall shows what goes (game, prefix, shader cache), backs the prefix up first, then hands over to Steam's own uninstall dialog. The backup is under Prefixes → Backups for after a reinstall.
- Prefixes: a "Saves" line names the game's own folders in Documents, Saved Games and AppData, newest first, and "Open saves folder" opens one (Desktop Mode).

## 0.10.0

- Cleanup finds Steam's leftovers of games that aren't installed any more: unfinished downloads (steamapps/downloading), update scratch (steamapps/temp) and Workshop items (steamapps/workshop/content). Folders of installed games are never listed.

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
