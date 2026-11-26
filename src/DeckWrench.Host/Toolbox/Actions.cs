using System.Diagnostics;
using DeckWrench.Core;
using DeckWrench.Core.Launch;
using DeckWrench.Core.Logs;
using DeckWrench.Core.Prefixes;
using DeckWrench.Core.Proton;
using DeckWrench.Core.Steam;
using DeckWrench.Core.Storage;
using DeckWrench.Core.Tricks;

namespace DeckWrench.Host.Toolbox;

/// <summary>
/// The actions for each thing DeckWrench shows (a game, a Proton version, a prefix...). The
/// controller and desktop UIs both render these lists, so every feature exists in both.
/// </summary>
public sealed class Actions
{
    private readonly AppHost _host;

    public Actions(AppHost host) => _host = host;

    private ToolboxState S => _host.State;

    private static readonly string NotOnWindows = "Proton runs on Linux only; on Windows games run directly.";

    // ---- whole app ----

    public IReadOnlyList<ToolAction> Global()
    {
        var list = new List<ToolAction>
        {
            new()
            {
                Id = ActionId.Rescan,
                Label = "Scan again",
                Help = "Reads Steam (every library, SD card too), Heroic and Lutris again and re-measures every folder.",
                Run = _ =>
                {
                    _host.Sizes.Forget();
                    return Task.FromResult(new ActionResult("Scanned"));
                },
            },
            new()
            {
                Id = ActionId.SaveReport,
                Label = "Save a report",
                Help = $"Writes your system, drives, Proton versions, every game's Proton, launch options and prefix, recent Proton log errors and what can be cleaned up to a Markdown file in {Path.Combine(_host.Paths.Data, "reports")}, to share when asking for help. Your home folder shows as ~.",
                ShowsOutput = true,
                Run = ctx =>
                {
                    var file = LibraryReport.Save(S, _host);
                    ctx.Log(file);
                    AppLog.Info("report: " + file);
                    return Task.FromResult(new ActionResult("Report saved: " + file, Rescan: false));
                },
            },
        };
        list.AddRange(PendingActions());
        return list;
    }

    public IReadOnlyList<ToolAction> PendingActions()
    {
        if (S.Pending.Count == 0)
        {
            return [];
        }
        var lines = string.Join("\n", S.Pending.Select(p => "• " + p.Describe()));
        var gameMode = SteamSetup.InGameMode;
        return
        [
            new()
            {
                Id = ActionId.ApplyPending,
                Label = $"Apply {S.Pending.Count} change{(S.Pending.Count == 1 ? "" : "s")} (restarts Steam)",
                Help = "Steam is running and would overwrite these settings when it exits. This restarts Steam and writes them in between:\n" + lines,
                Unavailable = _host.CliPath == null ? "Needs the deckwrench helper from a release build. Close Steam, then open DeckWrench again to apply." : null,
                Run = async ctx =>
                {
                    if (!SteamShortcuts.SteamRunning(_host.Paths.Home))
                    {
                        var n = _host.ApplyPendingNow();
                        return new ActionResult($"Wrote {n} change{(n == 1 ? "" : "s")} to Steam");
                    }
                    PendingChanges.ApplyByRestartingSteam(_host.CliPath!, restartSteam: !gameMode);
                    await Task.Delay(500, ctx.Ct).ConfigureAwait(false);
                    return new ActionResult(gameMode ? "Restarting Steam: the changes are written while it restarts." : "Steam is restarting with the changes.", Rescan: false);
                },
            },
            new()
            {
                Id = ActionId.DiscardPending,
                Label = "Discard pending changes",
                Help = "Forgets the changes waiting for Steam to close:\n" + lines,
                Run = _ =>
                {
                    _host.Pending.Discard();
                    return Task.FromResult(new ActionResult("Pending changes discarded"));
                },
            },
        ];
    }

    // ---- games ----

    public IReadOnlyList<ToolAction> ForGame(GameEntry g)
    {
        var list = new List<ToolAction>();
        if (g.RunsInSteam)
        {
            list.Add(SetCompatTool(g));
            list.Add(LaunchPresets(g));
            list.Add(EditLaunchOptions(g));
            list.Add(ToggleProtonLog(g));
        }
        if (g.Prefix != null)
        {
            list.AddRange(ForPrefix(g.Prefix, g));
        }
        else if (g.RunsInSteam && !Platform.IsWindows)
        {
            list.Add(new ToolAction
            {
                Id = ActionId.OpenPrefix,
                Label = "Prefix",
                Help = "Steam creates the Proton prefix the first time the game runs with Proton.",
                Unavailable = "No prefix yet: the game hasn't run with Proton (or runs natively).",
            });
        }
        if (g.ShaderCache != null)
        {
            list.Add(ClearShaderCache(g.ShaderCache, g.Title));
        }
        if (g.Log != null)
        {
            list.AddRange(ForLog(g.Log));
        }
        if (g.IsSteam)
        {
            list.Add(MoveGame(g));
            list.Add(new ToolAction
            {
                Id = ActionId.OpenInSteam,
                Label = "Show in Steam",
                Help = "Opens the game's page in Steam.",
                Run = _ => Task.FromResult(new ActionResult(Opener.Open($"steam://nav/games/details/{g.AppId}") ? "Opened in Steam" : "Couldn't open Steam", Rescan: false)),
            });
        }
        list.Add(OpenFolder(ActionId.OpenInstallFolder, "Open install folder", g.Install.InstallDir));
        list.Add(EditNotes(g));
        if (g.IsSteam)
        {
            list.Add(OpenProtonDb(g));
        }
        return list;
    }

    private ToolAction SetCompatTool(GameEntry g)
    {
        var current = g.EffectiveCompatTool;
        var choices = new List<ActionChoice> { new("", S.DefaultTool is { } d ? $"Steam default ({S.ToolLabel(d)})" : "Steam default", "Steam picks (Proton for Windows games, native when there's a Linux build).", current == null) };
        choices.AddRange(S.Tools.Select(t => new ActionChoice(t.Name, t.DisplayName, ToolDetail(t), string.Equals(t.Name, current, StringComparison.OrdinalIgnoreCase))));
        if (current != null && S.Tool(current) == null)
        {
            choices.Add(new ActionChoice(current, S.ToolLabel(current), "Set in Steam, but not installed: Steam uses its default until you install it.", true));
        }
        return new ToolAction
        {
            Id = ActionId.SetCompatTool,
            Label = $"Proton: {S.ToolLabel(current)}",
            Help = "The compatibility tool Steam runs this game with (Properties → Compatibility).",
            Unavailable = Platform.IsWindows ? NotOnWindows : S.SteamRoot == null ? "Steam isn't installed." : null,
            Input = new ActionInput { Kind = InputKind.Choice, Prompt = $"Run {g.Title} with", Choices = choices },
            Run = ctx => Task.FromResult(WriteSteam(new PendingChange
            {
                Kind = PendingKind.CompatTool,
                AppId = g.AppId,
                Value = string.IsNullOrEmpty(ctx.Input) ? null : ctx.Input,
                Title = g.Title,
            })),
        };
    }

    private string ToolDetail(CompatTool t)
    {
        var used = S.GamesUsing(t).Count;
        var kind = t.Kind switch { CompatToolKind.Valve => "Valve", CompatToolKind.System => "system", _ => "custom" };
        return $"{kind} · {DiskSize.Format(t.Size)}" + (used > 0 ? $" · used by {used} game{(used == 1 ? "" : "s")}" : "");
    }

    private ToolAction EditLaunchOptions(GameEntry g) => new()
    {
        Id = ActionId.EditLaunchOptions,
        Label = "Edit launch options",
        Help = "Type the game's Steam launch options yourself. Current: " + (g.EffectiveLaunchOptions.Length > 0 ? g.EffectiveLaunchOptions : "(none)"),
        Unavailable = S.SteamRoot == null ? "Steam isn't installed." : null,
        Input = new ActionInput { Kind = InputKind.Text, Prompt = $"Launch options for {g.Title}", Initial = g.EffectiveLaunchOptions },
        Run = ctx => Task.FromResult(SetLaunchOptions(g, (ctx.Input ?? "").Trim())),
    };

    private ToolAction LaunchPresets(GameEntry g)
    {
        var opts = LaunchOptions.Parse(g.EffectiveLaunchOptions);
        var presets = LaunchPreset.All.Where(p => !Platform.IsWindows || p.Category is "Game" or "Performance" && p.Kind != PresetKind.Wrapper).ToList();
        return new ToolAction
        {
            Id = ActionId.LaunchPresets,
            Label = "Launch option presets",
            Help = "Turn common options on and off: GameMode, Proton logging, DXVK/VKD3D flags, frame caps...",
            Unavailable = S.SteamRoot == null ? "Steam isn't installed." : null,
            Input = new ActionInput
            {
                Kind = InputKind.MultiChoice,
                Prompt = $"Presets for {g.Title}",
                Choices = presets.Select(p => new ActionChoice(p.Id, p.Label, $"{p.Token} — {p.Help}", opts.Has(p), p.Category)).ToList(),
            },
            Run = ctx =>
            {
                var chosen = (ctx.Input ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                var o = LaunchOptions.Parse(g.EffectiveLaunchOptions);
                foreach (var p in presets.Where(p => o.Has(p) && !chosen.Contains(p.Id)))
                {
                    o.Remove(p);
                }
                foreach (var p in presets.Where(p => chosen.Contains(p.Id) && !o.Has(p)))
                {
                    o.Add(p);
                }
                return Task.FromResult(SetLaunchOptions(g, o.ToString()));
            },
        };
    }

    private ToolAction ToggleProtonLog(GameEntry g)
    {
        var preset = LaunchPreset.Find("proton-log")!;
        var on = LaunchOptions.Parse(g.EffectiveLaunchOptions).Has(preset);
        return new ToolAction
        {
            Id = ActionId.ToggleProtonLog,
            Label = on ? "Turn Proton logging off" : "Turn Proton logging on",
            Help = on ? "Removes PROTON_LOG=1 from the launch options." : "Adds PROTON_LOG=1 to the launch options: the next run writes steam-" + g.AppId + ".log to your home folder.",
            Unavailable = Platform.IsWindows ? NotOnWindows : S.SteamRoot == null ? "Steam isn't installed." : null,
            Run = _ =>
            {
                var o = LaunchOptions.Parse(g.EffectiveLaunchOptions);
                o.Toggle(preset);
                return Task.FromResult(SetLaunchOptions(g, o.ToString()));
            },
        };
    }

    private ActionResult SetLaunchOptions(GameEntry g, string options) => WriteSteam(g.IsShortcut
        ? new PendingChange { Kind = PendingKind.ShortcutLaunchOptions, AppId = g.AppId, Value = options, Title = g.Title, Target = g.Install.ShortcutsFile }
        : new PendingChange { Kind = PendingKind.LaunchOptions, AppId = g.AppId, Value = options, Title = g.Title });

    /// <summary>Writes a Steam setting now when Steam is closed, else queues it.</summary>
    private ActionResult WriteSteam(PendingChange change)
    {
        var root = S.SteamRoot ?? throw new InvalidOperationException("Steam isn't installed.");
        if (SteamShortcuts.SteamRunning(_host.Paths.Home))
        {
            _host.Pending.Add(change);
            return new ActionResult("Saved. Steam is running: apply it from the banner (restarts Steam), or it's written next time Steam is closed.");
        }
        PendingChanges.Write(new SteamConfig(root), change);
        AppLog.Info("wrote " + change.Describe());
        return new ActionResult("Saved to Steam");
    }

    private ToolAction MoveGame(GameEntry g)
    {
        var others = S.Libraries.Where(l => !string.Equals(PathUtil.RealPath(l), PathUtil.RealPath(g.Install.Library ?? ""), Platform.PathComparison)).ToList();
        var steamRunning = SteamShortcuts.SteamRunning(_host.Paths.Home);
        return new ToolAction
        {
            Id = ActionId.MoveGame,
            Label = "Move to another drive",
            Help = steamRunning
                ? "Steam is running, so DeckWrench opens Steam's own mover: the game's page → ⚙ → Properties → Installed Files → Move install folder (in Game Mode also Settings → Storage)."
                : "Moves the game, its manifest, prefix and shader cache to another Steam library, like Steam's own mover. Steam must stay closed until it's done.",
            Unavailable = others.Count == 0 ? "There's only one Steam library. Add one on the SD card or another drive in Steam's Storage settings." : null,
            Input = steamRunning ? null : new ActionInput
            {
                Kind = InputKind.Choice,
                Prompt = $"Move {g.Title} to",
                Choices = others.Select(l => new ActionChoice(l, LibraryLabel(l), Drives.For(l) is { } d ? $"{DiskSize.Format(d.Free)} free" : "")).ToList(),
            },
            Plan = steamRunning ? null : (input, ct) =>
            {
                var plan = GameMover.Plan(g.Install, input!);
                return new ConfirmPlan
                {
                    Title = $"Move {g.Title} to {LibraryLabel(input!)}? Keep Steam closed until it finishes.",
                    Verb = "Move",
                    Contents = plan.Items.Select(i => (i.From, i.Size)).ToList(),
                    TotalSize = plan.TotalSize,
                    Paths = plan.Items.Select(i => i.To).ToList(),
                };
            },
            Run = ctx =>
            {
                if (SteamShortcuts.SteamRunning(_host.Paths.Home) || ctx.Input == null)
                {
                    Opener.Open($"steam://nav/games/details/{g.AppId}");
                    return Task.FromResult(new ActionResult("Opened in Steam: Properties → Installed Files → Move install folder", Rescan: false));
                }
                var plan = GameMover.Plan(g.Install, ctx.Input);
                GameMover.Execute(plan, ctx.Progress, ctx.Ct);
                AppLog.Info($"moved {g.Title} to {ctx.Input}");
                return Task.FromResult(new ActionResult($"Moved {g.Title} to {LibraryLabel(ctx.Input)}"));
            },
        };
    }

    private ToolAction EditNotes(GameEntry g)
    {
        var note = _host.Notes.Get(g.Key) ?? "";
        return new ToolAction
        {
            Id = ActionId.EditNotes,
            Label = note.Length > 0 ? "Edit your notes" : "Add a note",
            Help = "Your own notes on this game: what fixed it, which Proton works, settings to remember. Kept in notes.json in the config folder.",
            Input = new ActionInput { Kind = InputKind.Text, Prompt = $"Notes on {g.Title}", Initial = note },
            Run = ctx =>
            {
                _host.Notes.Set(g.Key, ctx.Input);
                return Task.FromResult(new ActionResult(string.IsNullOrWhiteSpace(ctx.Input) ? "Note removed" : "Note saved"));
            },
        };
    }

    /// <summary>ProtonDB's page for the game: in Steam's own browser in Game Mode (no desktop browser there).</summary>
    private static ToolAction OpenProtonDb(GameEntry g)
    {
        var url = $"https://www.protondb.com/app/{g.AppId}";
        return new ToolAction
        {
            Id = ActionId.OpenProtonDb,
            Label = "Reports on ProtonDB",
            Help = "Opens the game's ProtonDB page: what other players run it with, and their fixes. " + url,
            Run = _ =>
            {
                var opened = Opener.Open(SteamSetup.InGameMode ? "steam://openurl/" + url : url);
                return Task.FromResult(new ActionResult(opened ? "Opened ProtonDB" : "Couldn't open a browser. " + url, Rescan: false));
            },
        };
    }

    public string LibraryLabel(string lib)
    {
        var d = Drives.For(lib);
        return d == null ? lib : $"{d.Label} ({lib})";
    }

    // ---- Proton versions ----

    public IReadOnlyList<ToolAction> ForProtonPage() =>
    [
        new()
        {
            Id = ActionId.InstallGeProton,
            Label = "Download GE-Proton",
            Help = "Installs a GE-Proton release from GloriousEggroll's GitHub into compatibilitytools.d. Steam lists it after a restart.",
            Unavailable = Platform.IsWindows ? NotOnWindows : S.SteamRoot == null ? "Steam isn't installed." : null,
            Input = new ActionInput
            {
                Kind = InputKind.Choice,
                Prompt = "Install which GE-Proton?",
                LoadChoices = async ct =>
                {
                    var releases = await new GeProton(_host.Http).ListAsync(ct).ConfigureAwait(false);
                    return releases.Select((r, i) => new ActionChoice(r.Tag, r.Tag + (i == 0 ? " (latest)" : ""),
                        (S.Tool(r.Tag) != null ? "Installed · " : "") + $"{r.Published.ToLocalTime():d} · {DiskSize.Format(r.TarballSize)} download")).ToList();
                },
            },
            Run = async ctx =>
            {
                var releases = await new GeProton(_host.Http).ListAsync(ctx.Ct).ConfigureAwait(false);
                var release = releases.FirstOrDefault(r => r.Tag == ctx.Input) ?? throw new InvalidOperationException($"{ctx.Input} isn't on GitHub any more.");
                var dir = await new GeProton(_host.Http).InstallAsync(release, CompatToolScanner.CustomDir(S.SteamRoot!), Path.Combine(_host.Paths.Cache, "downloads"), ctx.Progress, ctx.Ct).ConfigureAwait(false);
                AppLog.Info($"installed {release.Tag} to {dir}");
                return new ActionResult($"Installed {release.Tag}. Restart Steam to pick it in game properties.");
            },
        },
        SetDefaultTool(),
    ];

    private ToolAction SetDefaultTool()
    {
        var choices = new List<ActionChoice> { new("", "Steam's choice", "No default set: Steam uses its current stable Proton.", S.DefaultTool == null) };
        choices.AddRange(S.Tools.Select(t => new ActionChoice(t.Name, t.DisplayName, ToolDetail(t), string.Equals(t.Name, S.DefaultTool, StringComparison.OrdinalIgnoreCase))));
        return new ToolAction
        {
            Id = ActionId.SetDefaultTool,
            Label = $"Default for all games: {(S.DefaultTool == null ? "Steam's choice" : S.ToolLabel(S.DefaultTool))}",
            Help = "Steam Play's default compatibility tool (Steam Settings → Compatibility). Games with their own choice keep it.",
            Unavailable = Platform.IsWindows ? NotOnWindows : S.SteamRoot == null ? "Steam isn't installed." : null,
            Input = new ActionInput { Kind = InputKind.Choice, Prompt = "Default compatibility tool", Choices = choices },
            Run = ctx => Task.FromResult(WriteSteam(new PendingChange
            {
                Kind = PendingKind.CompatTool,
                AppId = "0",
                Value = string.IsNullOrEmpty(ctx.Input) ? null : ctx.Input,
                Title = "All games",
            })),
        };
    }

    public IReadOnlyList<ToolAction> ForTool(CompatTool t)
    {
        var users = S.GamesUsing(t);
        var list = new List<ToolAction>();
        list.Add(new ToolAction
        {
            Id = ActionId.RemoveTool,
            Label = "Remove",
            Destructive = true,
            Help = t.Removable ? "Deletes this version from compatibilitytools.d." : t.Kind == CompatToolKind.Valve
                ? "Valve's Proton is a Steam app: uninstall it from Steam (Library → Tools)." : "Installed by the system; remove it with your package manager.",
            Unavailable = t.Removable ? null : t.Kind == CompatToolKind.Valve ? "Valve's Proton is removed through Steam (Library → Tools)." : "Installed by the system, not by you.",
            Plan = (_, ct) => new ConfirmPlan
            {
                Title = $"Remove {t.DisplayName}?",
                Verb = "Remove",
                Contents = DiskSize.Contents(t.Dir, ct).Select(c => (c.Name, c.Size)).ToList(),
                TotalSize = DiskSize.Of(t.Dir, ct),
                Caution = users.Count > 0
                    ? $"{users.Count} game{(users.Count == 1 ? " is" : "s are")} set to use it ({string.Join(", ", users.Take(4).Select(u => u.Title))}{(users.Count > 4 ? "…" : "")}). Steam falls back to its default for them."
                    : null,
                Paths = [t.Dir],
            },
            Run = ctx =>
            {
                PrefixBackups.DeleteTree(t.Dir);
                AppLog.Info($"removed {t.Name} ({t.Dir})");
                return Task.FromResult(new ActionResult($"Removed {t.DisplayName}"));
            },
        });
        list.Add(OpenFolder(ActionId.OpenToolFolder, "Open folder", t.Dir));
        return list;
    }

    // ---- prefixes ----

    public IReadOnlyList<ToolAction> ForPrefix(AppFolder p, GameEntry? game = null)
    {
        var name = p.Owner ?? $"app {p.AppId}";
        var backups = S.Backups.Where(b => b.AppId == p.AppId).ToList();
        var backupNote = $"A backup is made first, in {_host.Backups.Dir}.";
        var list = new List<ToolAction>
        {
            OpenFolder(ActionId.OpenPrefix, "Open prefix (drive_c)", Directory.Exists(Path.Combine(p.PfxDir, "drive_c")) ? Path.Combine(p.PfxDir, "drive_c") : p.Dir),
            new()
            {
                Id = ActionId.BackupPrefix,
                Label = "Back up prefix",
                Help = $"Saves the whole prefix ({DiskSize.Format(p.Size)}) as a .tar.gz in {_host.Backups.Dir}: saves, settings and installed runtimes.",
                Run = ctx =>
                {
                    var b = _host.Backups.Create(p, "manual", ctx.Progress, ctx.Ct);
                    return Task.FromResult(new ActionResult($"Backed up {name} ({DiskSize.Format(b.ArchiveSize)})"));
                },
            },
            new()
            {
                Id = ActionId.RestorePrefix,
                Label = backups.Count > 0 ? $"Restore a backup ({backups.Count})" : "Restore a backup",
                Destructive = true,
                Help = "Puts a saved copy back in place of the current prefix.",
                Unavailable = backups.Count == 0 ? "No backups of this prefix yet." : null,
                Input = new ActionInput
                {
                    Kind = InputKind.Choice,
                    Prompt = $"Restore which backup of {name}?",
                    Choices = backups.Select(b => new ActionChoice(b.Archive, $"{b.Created.ToLocalTime():g}", $"{b.Reason} · {DiskSize.Format(b.ArchiveSize)} archive")).ToList(),
                },
                Plan = (input, ct) => new ConfirmPlan
                {
                    Title = $"Replace the current prefix of {name} with the backup from {backups.First(b => b.Archive == input).Created.ToLocalTime():g}?",
                    Verb = "Restore",
                    Contents = DiskSize.Contents(p.Dir, ct).Select(c => ("current: " + c.Name, c.Size)).ToList(),
                    TotalSize = DiskSize.Of(p.Dir, ct),
                    Caution = "Anything saved in the prefix since that backup (saves without Steam Cloud, settings) is replaced.",
                    Paths = [p.Dir],
                },
                Run = ctx =>
                {
                    var b = backups.First(x => x.Archive == ctx.Input);
                    ctx.Progress.Report(-1);
                    _host.Backups.Restore(b with { Source = p.Dir }, ctx.Ct);
                    return Task.FromResult(new ActionResult($"Restored {name} from {b.Created.ToLocalTime():g}"));
                },
            },
        };
        if (!p.External)
        {
            list.Add(new ToolAction
            {
                Id = ActionId.ResetPrefix,
                Label = "Reset prefix",
                Destructive = true,
                Help = "Backs the prefix up, then deletes it. Steam builds a fresh one the next time the game starts: the classic fix for a game that broke after an update or a bad tweak.",
                Plan = (_, ct) => PrefixPlan(p, $"Reset the prefix of {name}?", "Back up and reset", backupNote, ct),
                Run = ctx =>
                {
                    var b = _host.Backups.Create(p, "before reset", new Progress<double>(v => ctx.Progress.Report(v * 0.9)), ctx.Ct);
                    PrefixBackups.DeleteTree(p.Dir);
                    AppLog.Info($"reset prefix {p.Dir} (backup {b.Archive})");
                    return Task.FromResult(new ActionResult($"Reset. Backup saved ({DiskSize.Format(b.ArchiveSize)}); Steam makes a new prefix on the next start."));
                },
            });
        }
        list.Add(new ToolAction
        {
            Id = ActionId.DeletePrefix,
            Label = "Delete prefix",
            Destructive = true,
            Help = p.Orphan ? "Frees the space. The game it belonged to isn't installed any more." : "Deletes the prefix without a backup (use Back up first to keep saves).",
            Plan = (_, ct) => PrefixPlan(p, $"Delete the prefix of {name}? There's no backup unless you made one.", "Delete", null, ct),
            Run = ctx =>
            {
                PrefixBackups.DeleteTree(p.Dir);
                AppLog.Info($"deleted prefix {p.Dir}");
                return Task.FromResult(new ActionResult($"Deleted the prefix of {name}"));
            },
        });
        if (!p.External)
        {
            list.Add(RunTricks(p, name));
            list.Add(RunTypedTricks(p, name));
        }
        return list;
    }

    private static ConfirmPlan PrefixPlan(AppFolder p, string title, string verb, string? backup, CancellationToken ct) => new()
    {
        Title = title,
        Verb = verb,
        Contents = PrefixContents(p, ct),
        TotalSize = DiskSize.Of(p.Dir, ct),
        Backup = backup,
        Caution = "Games without Steam Cloud keep saves in drive_c/users/steamuser (Documents, AppData). They go with the prefix.",
        Paths = [p.Dir],
    };

    /// <summary>The interesting parts of a prefix: user folders with saves, Program Files, the rest.</summary>
    private static IReadOnlyList<(string, long)> PrefixContents(AppFolder p, CancellationToken ct)
    {
        var list = new List<(string, long)>();
        var user = Path.Combine(p.PfxDir, "drive_c", "users", "steamuser");
        foreach (var sub in new[] { "Documents", "AppData/Local", "AppData/Roaming", "Saved Games" })
        {
            var dir = Path.Combine(user, sub);
            if (Directory.Exists(dir))
            {
                list.Add(($"drive_c/users/steamuser/{sub} (saves and settings)", DiskSize.Of(dir, ct)));
            }
        }
        foreach (var sub in new[] { "Program Files", "Program Files (x86)", "windows" })
        {
            var dir = Path.Combine(p.PfxDir, "drive_c", sub);
            if (Directory.Exists(dir))
            {
                list.Add(($"drive_c/{sub}", DiskSize.Of(dir, ct)));
            }
        }
        if (list.Count == 0)
        {
            list.AddRange(DiskSize.Contents(p.Dir, ct).Select(c => (c.Name, c.Size)));
        }
        return list;
    }

    private static string? TricksUnavailable(TricksRunner? runner) => Platform.IsWindows ? NotOnWindows
        : runner == null ? "protontricks isn't installed. Get \"Protontricks\" from Discover (Flatpak), or your package manager." : null;

    private ToolAction RunTricks(AppFolder p, string name)
    {
        var runner = TricksRunner.Find(_host.Paths.Home);
        var verbs = TricksCatalog.All(_host.Paths.TricksFile);
        return new ToolAction
        {
            Id = ActionId.RunTricks,
            Label = "Install runtimes and fonts (protontricks)",
            Help = "Runs winetricks verbs in this prefix through protontricks: Visual C++, .NET, DirectX extras, fonts"
                + (verbs.Any(v => v.Category == TricksCatalog.UserCategory) ? ", and your own verbs from tricks.json." : ". Add your own in tricks.json in the config folder."),
            Unavailable = TricksUnavailable(runner),
            ShowsOutput = true,
            Input = new ActionInput
            {
                Kind = InputKind.MultiChoice,
                Prompt = $"Install into {name}",
                Choices = verbs.Select(v => new ActionChoice(v.Id, v.Label, $"{v.Id} — {v.Help}".TrimEnd(' ', '—'), false, v.Category)).ToList(),
            },
            Run = ctx => InstallVerbs(runner!, p, name, (ctx.Input ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries), ctx),
        };
    }

    private ToolAction RunTypedTricks(AppFolder p, string name)
    {
        var runner = TricksRunner.Find(_host.Paths.Home);
        return new ToolAction
        {
            Id = ActionId.RunTypedTricks,
            Label = "Run other winetricks verbs",
            Help = "Type any winetricks verbs (separated by spaces), like d3dx9_43 or win7. Only verbs are passed, never options.",
            Unavailable = TricksUnavailable(runner),
            ShowsOutput = true,
            Input = new ActionInput
            {
                Kind = InputKind.Text,
                Prompt = $"winetricks verbs to run in {name}",
                Validate = text => TricksCatalog.ParseTyped(text).Error,
            },
            Run = ctx =>
            {
                var (verbs, error) = TricksCatalog.ParseTyped(ctx.Input);
                if (error != null)
                {
                    throw new ArgumentException(error);
                }
                return InstallVerbs(runner!, p, name, verbs, ctx);
            },
        };
    }

    private static async Task<ActionResult> InstallVerbs(TricksRunner runner, AppFolder p, string name, IReadOnlyList<string> verbs, ActionContext ctx)
    {
        if (verbs.Count == 0)
        {
            return new ActionResult("Nothing picked", Rescan: false);
        }
        var (exe, args) = runner.Command(p.AppId, verbs);
        ctx.Progress.Report(-1);
        var code = await ProcessRunner.Run(exe, args, ctx.Log, ctx.Ct).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"protontricks stopped with exit code {code}. The output shows why.");
        }
        AppLog.Info($"protontricks {p.AppId}: {string.Join(' ', verbs)}");
        return new ActionResult($"Installed {string.Join(", ", verbs)} into {name}");
    }

    public ToolAction DeleteBackup(PrefixBackup b) => new()
    {
        Id = ActionId.DeleteBackup,
        Label = "Delete backup",
        Destructive = true,
        Help = "Deletes this backup archive.",
        Plan = (_, _) => new ConfirmPlan
        {
            Title = $"Delete the backup of {b.Owner ?? "app " + b.AppId} from {b.Created.ToLocalTime():g}?",
            Verb = "Delete",
            Contents = [(Path.GetFileName(b.Archive), b.ArchiveSize)],
            TotalSize = b.ArchiveSize,
            Paths = [b.Archive],
        },
        Run = _ =>
        {
            _host.Backups.Delete(b);
            return Task.FromResult(new ActionResult("Backup deleted"));
        },
    };

    public IReadOnlyList<ToolAction> ForBackup(PrefixBackup b)
    {
        var prefix = S.Prefixes.FirstOrDefault(p => p.AppId == b.AppId) ?? new AppFolder
        {
            Kind = AppFolderKind.Prefix,
            AppId = b.AppId,
            Dir = b.Source,
            Library = "",
            Owner = b.Owner,
        };
        var restore = ForPrefix(prefix).First(a => a.Id == ActionId.RestorePrefix);
        return
        [
            restore with
            {
                Label = "Restore this backup",
                Unavailable = null,
                Input = null,
                Plan = restore.Plan is { } plan ? (_, ct) => plan(b.Archive, ct) : null,
                Run = restore.Run is { } run ? ctx => run(ctx with { Input = b.Archive }) : null,
            },
            DeleteBackup(b),
        ];
    }

    // ---- shader caches ----

    public ToolAction ClearShaderCache(AppFolder c, string? title = null) => new()
    {
        Id = ActionId.ClearShaderCache,
        Label = $"Clear shader cache ({DiskSize.Format(c.Size)})",
        Destructive = true,
        Help = "Deletes the game's compiled shaders. Steam downloads or rebuilds them; the first runs after may stutter.",
        Plan = (_, ct) => new ConfirmPlan
        {
            Title = $"Clear the shader cache of {title ?? c.Owner ?? "app " + c.AppId}?",
            Verb = "Clear",
            Contents = DiskSize.Contents(c.Dir, ct).Select(x => (x.Name, x.Size)).ToList(),
            TotalSize = DiskSize.Of(c.Dir, ct),
            Paths = [c.Dir],
        },
        Run = _ =>
        {
            PrefixBackups.DeleteTree(c.Dir);
            AppLog.Info($"cleared shader cache {c.Dir}");
            return Task.FromResult(new ActionResult("Shader cache cleared"));
        },
    };

    // ---- prefixes page ----

    public IReadOnlyList<ToolAction> ForPrefixPage() => [BackupAllPrefixes(), PruneBackups()];

    private ToolAction BackupAllPrefixes()
    {
        // Prefixes of installed games (Steam and non-Steam shortcuts); Heroic/Lutris prefixes are their launchers' business.
        var prefixes = S.Prefixes.Where(p => !p.External && !p.Orphan).OrderBy(p => p.Owner ?? p.AppId, StringComparer.OrdinalIgnoreCase).ToList();
        return new ToolAction
        {
            Id = ActionId.BackupAllPrefixes,
            Label = "Back up every prefix",
            Help = $"Saves each picked prefix as a .tar.gz in {_host.Backups.Dir}: saves without Steam Cloud, settings, installed runtimes. Good before a SteamOS update or a Proton switch.",
            Unavailable = Platform.IsWindows ? NotOnWindows : prefixes.Count == 0 ? "No prefixes to back up." : null,
            ShowsOutput = true,
            Input = new ActionInput
            {
                Kind = InputKind.MultiChoice,
                Prompt = "Back up which prefixes?",
                Choices = prefixes.Select(p => new ActionChoice(p.Dir, $"{p.Owner ?? "App " + p.AppId} · {DiskSize.Format(p.Size)}",
                    S.Backups.Where(b => b.AppId == p.AppId).MaxBy(b => b.Created) is { } last ? $"Last backup {last.Created.ToLocalTime():g}" : "No backup yet", true)).ToList(),
            },
            Run = ctx =>
            {
                var ids = (ctx.Input ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                var picked = prefixes.Where(p => ids.Contains(p.Dir)).ToList();
                if (picked.Count == 0)
                {
                    return Task.FromResult(new ActionResult("Nothing picked", Rescan: false));
                }
                var total = Math.Max(1, picked.Sum(p => Math.Max(1, p.Size)));
                long done = 0;
                long archives = 0;
                foreach (var p in picked)
                {
                    ctx.Ct.ThrowIfCancellationRequested();
                    var start = done;
                    var weight = Math.Max(1, p.Size);
                    var b = _host.Backups.Create(p, "all", new Relay(f => ctx.Progress.Report((start + f * weight) / total)), ctx.Ct);
                    archives += b.ArchiveSize;
                    done += weight;
                    ctx.Log($"{p.Owner ?? "app " + p.AppId}: {DiskSize.Format(b.ArchiveSize)} → {b.Archive}");
                }
                AppLog.Info($"backed up {picked.Count} prefixes, {DiskSize.Format(archives)}");
                return Task.FromResult(new ActionResult($"Backed up {picked.Count} prefix{(picked.Count == 1 ? "" : "es")} ({DiskSize.Format(archives)})"));
            },
        };
    }

    /// <summary>Backups that go when keeping the newest <paramref name="keep"/> of each prefix.</summary>
    public static IReadOnlyList<PrefixBackup> BeyondNewest(IEnumerable<PrefixBackup> backups, int keep) =>
        backups.GroupBy(b => b.AppId).SelectMany(g => g.OrderByDescending(b => b.Created).Skip(Math.Max(1, keep))).ToList();

    private ToolAction PruneBackups()
    {
        int[] options = [1, 2, 3, 5];
        return new ToolAction
        {
            Id = ActionId.PruneBackups,
            Label = "Keep only the newest backups",
            Destructive = true,
            Help = "Deletes older prefix backups, keeping the newest of each prefix (or the newest few). Shows every archive and its size first.",
            Unavailable = BeyondNewest(S.Backups, 1).Count == 0 ? "No prefix has more than one backup." : null,
            Input = new ActionInput
            {
                Kind = InputKind.Choice,
                Prompt = "Keep how many backups of each prefix?",
                Choices = options.Select(n =>
                {
                    var gone = BeyondNewest(S.Backups, n);
                    return new ActionChoice(n.ToString(System.Globalization.CultureInfo.InvariantCulture), n == 1 ? "The newest one" : $"The newest {n}",
                        gone.Count == 0 ? "Nothing to delete" : $"Deletes {gone.Count} · frees {DiskSize.Format(gone.Sum(b => b.ArchiveSize))}");
                }).ToList(),
            },
            Plan = (input, _) =>
            {
                var gone = BeyondNewest(S.Backups, int.Parse(input!, System.Globalization.CultureInfo.InvariantCulture));
                return new ConfirmPlan
                {
                    Title = gone.Count == 0 ? "Nothing to delete: no prefix has more backups than that." : $"Delete {gone.Count} older backup{(gone.Count == 1 ? "" : "s")}?",
                    Verb = $"Delete {gone.Count}",
                    Contents = gone.Select(b => ($"{b.Owner ?? "app " + b.AppId} · {b.Created.ToLocalTime():g} · {Path.GetFileName(b.Archive)}", b.ArchiveSize)).ToList(),
                    TotalSize = gone.Sum(b => b.ArchiveSize),
                    Caution = "The newest backup of every prefix stays.",
                    Paths = gone.Select(b => b.Archive).ToList(),
                };
            },
            Run = ctx =>
            {
                var gone = BeyondNewest(_host.Backups.List(), int.Parse(ctx.Input!, System.Globalization.CultureInfo.InvariantCulture));
                foreach (var b in gone)
                {
                    _host.Backups.Delete(b);
                    ctx.Log("deleted " + b.Archive);
                }
                return Task.FromResult(new ActionResult(gone.Count == 0 ? "Nothing to delete" : $"Deleted {gone.Count} backup{(gone.Count == 1 ? "" : "s")}, freed {DiskSize.Format(gone.Sum(b => b.ArchiveSize))}"));
            },
        };
    }

    public IReadOnlyList<ToolAction> ForShaderPage()
    {
        var orphans = S.ShaderCaches.Where(c => c.Orphan).ToList();
        return
        [
            ClearMany(ActionId.ClearOrphanShaderCaches, $"Clear orphaned caches ({orphans.Count})", "Shader caches of games that aren't installed any more.", orphans),
            ClearMany(ActionId.ClearAllShaderCaches, $"Clear all shader caches ({S.ShaderCaches.Count})", "Every game's compiled shaders. They're rebuilt as games run; expect stutter at first.", S.ShaderCaches.ToList()),
        ];
    }

    private ToolAction ClearMany(ActionId id, string label, string help, IReadOnlyList<AppFolder> caches) => new()
    {
        Id = id,
        Label = label,
        Help = help,
        Destructive = true,
        Unavailable = caches.Count == 0 ? "Nothing to clear." : null,
        Plan = (_, _) => new ConfirmPlan
        {
            Title = $"Clear {caches.Count} shader cache{(caches.Count == 1 ? "" : "s")}?",
            Verb = "Clear",
            Contents = caches.OrderByDescending(c => c.Size).Select(c => (c.Owner ?? $"app {c.AppId} (not installed)", c.Size)).ToList(),
            TotalSize = caches.Sum(c => Math.Max(0, c.Size)),
            Paths = caches.Select(c => c.Dir).ToList(),
        },
        Run = ctx =>
        {
            var i = 0;
            foreach (var c in caches)
            {
                ctx.Ct.ThrowIfCancellationRequested();
                PrefixBackups.DeleteTree(c.Dir);
                ctx.Progress.Report(++i / (double)caches.Count);
            }
            AppLog.Info($"cleared {caches.Count} shader caches");
            return Task.FromResult(new ActionResult($"Cleared {caches.Count} shader cache{(caches.Count == 1 ? "" : "s")}"));
        },
    };

    // ---- logs ----

    public IReadOnlyList<ToolAction> ForLog(ProtonLog l) =>
    [
        new()
        {
            Id = ActionId.ViewLog,
            Label = $"View Proton log ({DiskSize.Format(l.Size)})",
            Help = $"steam-{l.AppId}.log, last written {l.Modified.ToLocalTime():g}.",
            Target = l.Path,
        },
        new()
        {
            Id = ActionId.DeleteLog,
            Label = "Delete log",
            Destructive = true,
            Help = "Deletes this Proton log file.",
            Plan = (_, _) => new ConfirmPlan { Title = $"Delete {Path.GetFileName(l.Path)}?", Verb = "Delete", Contents = [(l.Path, l.Size)], TotalSize = l.Size, Paths = [l.Path] },
            Run = _ =>
            {
                File.Delete(l.Path);
                return Task.FromResult(new ActionResult("Log deleted"));
            },
        },
    ];

    public IReadOnlyList<ToolAction> ForLogsPage() =>
    [
        new()
        {
            Id = ActionId.DeleteAllLogs,
            Label = $"Delete all Proton logs ({S.Logs.Count})",
            Destructive = true,
            Help = "Deletes every steam-<appid>.log file.",
            Unavailable = S.Logs.Count == 0 ? "No logs." : null,
            Plan = (_, _) => new ConfirmPlan
            {
                Title = $"Delete {S.Logs.Count} Proton log{(S.Logs.Count == 1 ? "" : "s")}?",
                Verb = "Delete",
                Contents = S.Logs.Select(l => (l.Path, l.Size)).ToList(),
                TotalSize = S.Logs.Sum(l => l.Size),
                Paths = S.Logs.Select(l => l.Path).ToList(),
            },
            Run = _ =>
            {
                foreach (var l in S.Logs)
                {
                    File.Delete(l.Path);
                }
                return Task.FromResult(new ActionResult("Logs deleted"));
            },
        },
    ];

    // ---- storage ----

    public IReadOnlyList<ToolAction> ForStoragePage() =>
    [
        CleanupAll(),
        .. S.Drives.Select(d => FreeSpaceOnDrive(d.Drive)),
    ];

    private ToolAction CleanupAll() =>
        new()
        {
            Id = ActionId.Cleanup,
            Label = $"Clean up ({DiskSize.Format(S.Reclaimable)} can be freed)",
            Destructive = true,
            Help = "Orphaned prefixes and shader caches, Proton versions nothing uses, old logs and backups. Pick what goes.",
            Unavailable = S.Cleanup.Count == 0 ? "Nothing to clean up." : null,
            Input = new ActionInput
            {
                Kind = InputKind.MultiChoice,
                Prompt = "Free space: pick what goes",
                // Prefixes can hold saves, so they start unticked; the rest is safe to remove.
                Choices = S.Cleanup.Select(c => new ActionChoice(c.Path, $"{c.Title} · {DiskSize.Format(c.Size)}", c.Caution ?? c.Detail,
                    c.Kind != CleanupKind.OrphanPrefix, CleanupGroup(c.Kind))).ToList(),
            },
            Plan = (input, _) =>
            {
                var picked = Picked(input);
                return new ConfirmPlan
                {
                    Title = $"Remove {picked.Count} item{(picked.Count == 1 ? "" : "s")}?",
                    Verb = $"Free {DiskSize.Format(picked.Sum(c => c.Size))}",
                    Contents = picked.Select(c => (c.Title + " — " + c.Path, c.Size)).ToList(),
                    TotalSize = picked.Sum(c => c.Size),
                    Caution = picked.Any(c => c.Kind == CleanupKind.OrphanPrefix) ? picked.First(c => c.Kind == CleanupKind.OrphanPrefix).Caution : null,
                    Paths = picked.Select(c => c.Path).ToList(),
                };
            },
            Run = ctx =>
            {
                var picked = Picked(ctx.Input);
                long freed = 0;
                var i = 0;
                foreach (var c in picked)
                {
                    ctx.Ct.ThrowIfCancellationRequested();
                    Remove(c);
                    freed += c.Size;
                    ctx.Progress.Report(++i / (double)picked.Count);
                    ctx.Log($"removed {c.Path}");
                }
                AppLog.Info($"cleanup: {picked.Count} items, {DiskSize.Format(freed)}");
                return Task.FromResult(new ActionResult($"Freed {DiskSize.Format(freed)}"));
            },
        };

    private void Remove(CleanupItem c)
    {
        if (c.Kind == CleanupKind.OldBackup && S.Backups.FirstOrDefault(b => b.Archive == c.Path) is { } b)
        {
            _host.Backups.Delete(b);
        }
        else if (File.Exists(c.Path))
        {
            File.Delete(c.Path);
        }
        else if (Directory.Exists(c.Path))
        {
            PrefixBackups.DeleteTree(c.Path);
        }
    }

    /// <summary>Free space on one drive: its cleanup items and moving its biggest games elsewhere, in one go.</summary>
    private ToolAction FreeSpaceOnDrive(Drive drive)
    {
        var steamRunning = SteamShortcuts.SteamRunning(_host.Paths.Home);
        var options = SpacePlanner.Options(S, drive, p => Drives.For(p), movesAllowed: !steamRunning, LibraryLabel);
        var where = $"{drive.Label} ({drive.Mount})";
        return new ToolAction
        {
            Id = ActionId.FreeSpaceOnDrive,
            Label = $"Free space on {drive.Label} ({DiskSize.Format(drive.Free)} free)",
            Destructive = true,
            Help = $"Pick what to remove from {where} and which games to move to another drive; the total shows before anything happens."
                + (steamRunning ? " Close Steam to move games too." : ""),
            Unavailable = options.Count == 0 ? $"Nothing on {drive.Label} can be cleaned up or moved to another drive." : null,
            Target = drive.Mount,
            Input = new ActionInput
            {
                Kind = InputKind.MultiChoice,
                Prompt = $"Free space on {where}: {DiskSize.Format(drive.Free)} of {DiskSize.Format(drive.Total)} free",
                Choices = options.Select(o => new ActionChoice(o.Id, o.Label, o.Detail, o.Suggested, o.Group)).ToList(),
            },
            Plan = (input, _) =>
            {
                var picked = SpacePlanner.Picked(options, input, p => Drives.For(p));
                var frees = picked.Sum(o => o.Frees);
                var moves = picked.Where(o => o.Game != null).ToList();
                var cautions = new List<string>();
                if (picked.FirstOrDefault(o => o.Cleanup?.Kind == CleanupKind.OrphanPrefix) is { } prefix)
                {
                    cautions.Add(prefix.Cleanup!.Caution ?? "");
                }
                if (moves.Count > 0)
                {
                    cautions.Add("Keep Steam closed until the moves finish.");
                }
                return new ConfirmPlan
                {
                    Title = $"Free {DiskSize.Format(frees)} on {drive.Label}? It will have {DiskSize.Format(drive.Free + frees)} free.",
                    Verb = $"Free {DiskSize.Format(frees)}",
                    Contents = picked.Select(o => (o.Game != null
                        ? $"Move {o.Game.Title} to {LibraryLabel(o.MoveTo!)}"
                        : $"Remove {o.Cleanup!.Title} — {o.Cleanup.Path}", o.Frees)).ToList(),
                    TotalSize = frees,
                    Caution = cautions.Count > 0 ? string.Join(" ", cautions.Where(c => c.Length > 0)) : null,
                    Paths = picked.Select(o => o.Cleanup?.Path ?? o.Game!.Install.InstallDir).ToList(),
                };
            },
            Run = ctx =>
            {
                var picked = SpacePlanner.Picked(options, ctx.Input, p => Drives.For(p));
                if (picked.Count == 0)
                {
                    return Task.FromResult(new ActionResult("Nothing picked", Rescan: false));
                }
                if (picked.Any(o => o.Game != null) && SteamShortcuts.SteamRunning(_host.Paths.Home))
                {
                    throw new InvalidOperationException("Steam is running. Close it so games can be moved, then try again.");
                }
                var total = Math.Max(1, picked.Sum(o => o.Frees));
                long done = 0;
                foreach (var o in picked.OrderBy(o => o.Game == null ? 0 : 1))
                {
                    ctx.Ct.ThrowIfCancellationRequested();
                    if (o.Cleanup is { } c)
                    {
                        Remove(c);
                        ctx.Log($"removed {c.Path}");
                    }
                    else
                    {
                        var start = done;
                        var plan = GameMover.Plan(o.Game!.Install, o.MoveTo!);
                        GameMover.Execute(plan, new Relay(f => ctx.Progress.Report((start + f * o.Frees) / total)), ctx.Ct);
                        ctx.Log($"moved {o.Game.Title} to {o.MoveTo}");
                    }
                    done += o.Frees;
                    ctx.Progress.Report(done / (double)total);
                }
                AppLog.Info($"freed {DiskSize.Format(done)} on {where}: {picked.Count} items");
                return Task.FromResult(new ActionResult($"Freed {DiskSize.Format(done)} on {drive.Label}"));
            },
        };
    }

    private static string CleanupGroup(CleanupKind k) => k switch
    {
        CleanupKind.OrphanPrefix => "Orphaned prefixes",
        CleanupKind.OrphanShaderCache => "Orphaned shader caches",
        CleanupKind.UnusedProton => "Unused Proton versions",
        CleanupKind.ProtonLog => "Proton logs",
        _ => "Old backups",
    };

    private List<CleanupItem> Picked(string? input)
    {
        var ids = (input ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return S.Cleanup.Where(c => ids.Contains(c.Path)).ToList();
    }

    // ---- shared ----

    private static ToolAction OpenFolder(ActionId id, string label, string dir) => new()
    {
        Id = id,
        Label = label,
        Help = dir,
        Unavailable = SteamSetup.InGameMode ? "Opens a file manager, which needs Desktop Mode. Path: " + dir : null,
        Run = _ => Task.FromResult(new ActionResult(Opener.Open(dir) ? "Opened" : "Couldn't open a file manager", Rescan: false)),
    };
}

/// <summary>Opens a folder or URL with the desktop's default handler.</summary>
public static class Opener
{
    public static bool Open(string target)
    {
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(target) { UseShellExecute = true }
                : new ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { target } };
            using var p = Process.Start(psi);
            AppLog.Info("open " + target);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn($"open {target}: {e.Message}");
            return false;
        }
    }
}

/// <summary>Runs a command and streams its output line by line; cancelling kills the whole process tree.</summary>
public static class ProcessRunner
{
    public static async Task<int> Run(string exe, IEnumerable<string> args, Action<string> log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        log("$ " + exe + " " + string.Join(' ', psi.ArgumentList));
        AppLog.Info($"run {exe} {string.Join(' ', psi.ArgumentList)}");
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                log(e.Data);
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                log(e.Data);
            }
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }
        return p.ExitCode;
    }
}

/// <summary>Reports straight through (no SynchronizationContext hop, unlike Progress&lt;T&gt;).</summary>
internal sealed class Relay(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
