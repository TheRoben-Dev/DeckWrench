using DeckWrench.Core;
using DeckWrench.Core.Detection;
using DeckWrench.Core.Logs;
using DeckWrench.Core.Prefixes;
using DeckWrench.Core.Proton;
using DeckWrench.Core.Storage;
using DeckWrench.Host.Toolbox;

namespace DeckWrench.App.Sections;

public sealed class GamesSection : Section
{
    public override string Name => "Games";
    public override string Summary => "Every installed game: its Proton, prefix, shader cache, launch options and log.";
    public override IReadOnlyList<string> Columns => ["Game", "Proton", "Launch options", "Drive", "Size", "Prefix", "Shaders"];
    public override string EmptyText => "No games found. DeckWrench looks in every Steam library (SD card too), Heroic and Lutris.";

    public override string Banner
    {
        get
        {
            var steam = S.Games.Count(g => g.IsSteam);
            var other = S.Games.Count - steam;
            return Plural(steam, "Steam game") + (other > 0 ? $", {Plural(other, "game")} from Heroic and Lutris" : "") +
                   (S.Pending.Count > 0 ? $" · {Plural(S.Pending.Count, "change")} waiting for Steam to restart" : "");
        }
    }

    public override IReadOnlyList<Row> Rows() => S.Games.Select(g => new Row
    {
        Key = g.Key,
        Title = g.Title,
        Detail = string.Join(" · ", new[]
        {
            g.IsSteam ? S.ToolLabel(g.EffectiveCompatTool) : SourceName(g.Install),
            g.Drive?.Label,
            DiskSize.Format(g.Size),
        }.Where(x => !string.IsNullOrEmpty(x))),
        Status = g.Pending.Count > 0 ? "Pending" : g.Log != null ? "Log" : null,
        StatusTone = g.Pending.Count > 0 ? Tone.Warn : Tone.Dim,
        Cover = g.Install.Cover,
        Cells =
        [
            g.Title,
            g.IsSteam ? new Cell(S.ToolLabel(g.EffectiveCompatTool) + (g.Pending.Count > 0 ? " *" : ""), S.ToolLabel(g.EffectiveCompatTool)) : new Cell(SourceName(g.Install), "~"),
            new Cell(g.EffectiveLaunchOptions, g.EffectiveLaunchOptions, Tone.Dim),
            g.Drive?.Label ?? "",
            Cell.Size(g.Size),
            g.Prefix != null ? Cell.Size(g.Prefix.Size) : new Cell("", -1L),
            g.ShaderCache != null ? Cell.Size(g.ShaderCache.Size) : new Cell("", -1L),
        ],
        Item = g,
    }).ToList();

    public static string SourceName(InstalledGame i) => i.Source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Lutris => "Lutris",
        GameSource.Epic => "Heroic (Epic)",
        GameSource.Gog => "Heroic (GOG)",
        GameSource.Amazon => "Heroic (Amazon)",
        _ => i.Source.ToString(),
    };

    public override IReadOnlyList<Fact> Facts(object item)
    {
        var g = (GameEntry)item;
        var facts = new List<Fact>
        {
            new("Source", SourceName(g.Install) + (g.IsSteam ? $" · app {g.AppId}" : "")),
            new("Installed in", g.Install.InstallDir),
            new("Drive", g.Drive != null ? $"{g.Drive.Label} · {DiskSize.Format(g.Drive.Free)} free" : "?"),
            new("Size", DiskSize.Format(g.Size)),
        };
        if (g.IsSteam)
        {
            facts.Add(new("Proton", S.ToolLabel(g.EffectiveCompatTool), g.EffectiveCompatTool != null && S.Tool(g.EffectiveCompatTool) == null ? Tone.Warn : Tone.Accent));
            facts.Add(new("Launch options", g.EffectiveLaunchOptions.Length > 0 ? g.EffectiveLaunchOptions : "(none)", g.EffectiveLaunchOptions.Length > 0 ? Tone.Normal : Tone.Dim));
        }
        facts.Add(g.Prefix is { } p
            ? new("Prefix", $"{DiskSize.Format(p.Size)}" + (p.ProtonVersion != null ? $" · last run with {p.ProtonVersion}" : ""))
            : new("Prefix", Platform.IsWindows ? "Not used on Windows" : "None yet", Tone.Dim));
        if (g.ShaderCache is { } c)
        {
            facts.Add(new("Shader cache", DiskSize.Format(c.Size)));
        }
        if (g.Log is { } l)
        {
            facts.Add(new("Proton log", $"{DiskSize.Format(l.Size)} · {l.Modified.ToLocalTime():g}"));
        }
        foreach (var pc in g.Pending)
        {
            facts.Add(new("Pending", pc.Describe(), Tone.Warn));
        }
        var backups = S.Backups.Count(b => b.AppId == g.AppId);
        if (backups > 0)
        {
            facts.Add(new("Prefix backups", backups.ToString()));
        }
        return facts;
    }

    public override IReadOnlyList<ToolAction> ActionsFor(object item) => Host.Actions.ForGame((GameEntry)item);

    public override IReadOnlyList<ToolAction> PageActions => Host.Actions.Global();
}

public sealed class ProtonSection : Section
{
    public override string Name => "Proton";
    public override string Summary => "Installed Proton and GE-Proton versions, which games use them, downloads and removal.";
    public override IReadOnlyList<string> Columns => ["Version", "Kind", "Games", "Size", "Folder"];
    public override string EmptyText => Platform.IsWindows ? "Proton runs on Linux only; on Windows games run directly." : "No Proton found. Steam installs Proton the first time a Windows game needs it.";

    public override string Banner => S.SteamRoot == null ? "Steam isn't installed." : $"Default for all games: {(S.DefaultTool == null ? "Steam's choice" : S.ToolLabel(S.DefaultTool))}";

    public override IReadOnlyList<Row> Rows() => S.Tools.Select(t =>
    {
        var users = S.GamesUsing(t).Count;
        var isDefault = string.Equals(t.Name, S.DefaultTool, StringComparison.OrdinalIgnoreCase);
        return new Row
        {
            Key = t.Name,
            Title = t.DisplayName,
            Detail = $"{KindName(t)} · {DiskSize.Format(t.Size)}" + (users > 0 ? $" · {Plural(users, "game")}" : ""),
            Status = isDefault ? "Default" : users == 0 && t.Removable ? "Unused" : null,
            StatusTone = isDefault ? Tone.Accent : Tone.Dim,
            Cells = [t.DisplayName, KindName(t), new Cell(isDefault ? $"{users} + default" : users.ToString(), users + (isDefault ? 1000 : 0)), Cell.Size(t.Size), new Cell(t.Dir, t.Dir, Tone.Dim)],
            Item = t,
        };
    }).ToList();

    public static string KindName(CompatTool t) => t.Kind switch
    {
        CompatToolKind.Valve => "Valve",
        CompatToolKind.System => "System",
        _ => t.IsGe ? "GE-Proton" : "Custom",
    };

    public override IReadOnlyList<Fact> Facts(object item)
    {
        var t = (CompatTool)item;
        var users = S.GamesUsing(t);
        var facts = new List<Fact>
        {
            new("Name in Steam", t.Name),
            new("Kind", KindName(t) + (t.AppId != null ? $" · app {t.AppId}" : "")),
            new("Size", DiskSize.Format(t.Size)),
            new("Folder", t.Dir),
        };
        if (string.Equals(t.Name, S.DefaultTool, StringComparison.OrdinalIgnoreCase))
        {
            facts.Add(new("Default", "Used by every game without its own choice", Tone.Accent));
        }
        facts.Add(new("Used by", users.Count == 0 ? "No game is set to use it" : string.Join(", ", users.Select(u => u.Title)), users.Count == 0 ? Tone.Dim : Tone.Normal));
        var ran = S.Prefixes.Where(p => !p.Orphan && p.ProtonVersion != null && p.ProtonVersion.Contains(t.Name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Owner).ToList();
        if (ran.Count > 0)
        {
            facts.Add(new("Last ran", string.Join(", ", ran)));
        }
        return facts;
    }

    public override IReadOnlyList<ToolAction> ActionsFor(object item) => Host.Actions.ForTool((CompatTool)item);

    public override IReadOnlyList<ToolAction> PageActions => [.. Host.Actions.ForProtonPage(), .. Host.Actions.PendingActions()];
}

public sealed class PrefixesSection : Section
{
    public override string Name => "Prefixes";
    public override string Summary => "Proton prefixes (compatdata): open, back up, restore, reset, delete, protontricks.";
    public override IReadOnlyList<string> Columns => ["Owner", "App id", "Last Proton", "Last used", "Size", "Status"];
    public override (int Column, bool Descending) DefaultSort => (4, true);
    public override string EmptyText => Platform.IsWindows ? "Prefixes are a Linux thing: on Windows games run directly." : "No Proton prefixes yet. Steam makes one the first time a game runs with Proton.";

    private static IEnumerable<AppFolder> AllPrefixes =>
        S.Prefixes.Concat(S.Games.Where(g => g.Prefix is { External: true }).Select(g => g.Prefix!));

    public override string Banner
    {
        get
        {
            var orphans = S.Prefixes.Where(p => p.Orphan).ToList();
            var total = AllPrefixes.Sum(p => Math.Max(0, p.Size));
            return $"{Plural(AllPrefixes.Count(), "prefix", "prefixes")}, {DiskSize.Format(total)}" +
                   (orphans.Count > 0 ? $" · {orphans.Count} orphaned ({DiskSize.Format(orphans.Sum(o => o.Size))})" : "") +
                   (S.Backups.Count > 0 ? $" · {Plural(S.Backups.Count, "backup")}" : "");
        }
    }

    public override IReadOnlyList<Row> Rows()
    {
        var rows = AllPrefixes.Select(p => new Row
        {
            Key = "pfx:" + p.Dir,
            Title = p.Owner ?? $"App {p.AppId}",
            Detail = $"{DiskSize.Format(p.Size)}" + (p.ProtonVersion != null ? $" · {p.ProtonVersion}" : "") + (p.External ? " · Heroic/Lutris" : p.IsShortcut ? " · non-Steam" : ""),
            Status = p.Orphan ? "Orphaned" : null,
            StatusTone = Tone.Warn,
            Group = p.Orphan ? "Orphaned: the game isn't installed" : "In use",
            Cells =
            [
                new Cell(p.Owner ?? "(not installed)", p.Owner ?? "~", p.Orphan ? Tone.Warn : Tone.Normal),
                new Cell(p.External ? "" : p.AppId, p.External ? 0L : long.Parse(p.AppId)),
                p.ProtonVersion ?? "",
                Cell.Date(p.Modified),
                Cell.Size(p.Size),
                new Cell(p.Orphan ? "Orphaned" : p.External ? "Heroic/Lutris" : p.IsShortcut ? "Non-Steam" : "", p.Orphan ? 0 : 1, p.Orphan ? Tone.Warn : Tone.Dim),
            ],
            Item = p,
        }).OrderBy(r => ((AppFolder)r.Item).Orphan ? 1 : 0).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
        rows.AddRange(S.Backups.Select(b => new Row
        {
            Key = "bak:" + b.Archive,
            Title = $"Backup: {b.Owner ?? "app " + b.AppId}",
            Detail = $"{b.Created.ToLocalTime():g} · {b.Reason} · {DiskSize.Format(b.ArchiveSize)}",
            Status = "Backup",
            StatusTone = Tone.Dim,
            Group = "Backups",
            Cells = [$"Backup: {b.Owner ?? "app " + b.AppId}", new Cell(b.AppId, long.TryParse(b.AppId, out var id) ? id : 0L), "", Cell.Date(b.Created), Cell.Size(b.ArchiveSize), new Cell("Backup", 2, Tone.Dim)],
            Item = b,
        }));
        return rows;
    }

    public override IReadOnlyList<Fact> Facts(object item)
    {
        if (item is PrefixBackup b)
        {
            return
            [
                new("Prefix of", b.Owner ?? "app " + b.AppId),
                new("Made", b.Created.ToLocalTime().ToString("g")),
                new("Why", b.Reason),
                new("Archive", $"{DiskSize.Format(b.ArchiveSize)} (prefix was {DiskSize.Format(b.PrefixSize)})"),
                new("File", b.Archive),
                new("Restores to", b.Source),
            ];
        }
        var p = (AppFolder)item;
        var facts = new List<Fact>
        {
            new("Belongs to", p.Owner ?? "Nothing installed: the game was uninstalled and Steam kept its prefix", p.Orphan ? Tone.Warn : Tone.Normal),
            new("Folder", p.Dir),
            new("Size", DiskSize.Format(p.Size)),
        };
        if (!p.External)
        {
            facts.Insert(1, new("App id", p.AppId + (p.IsShortcut ? " (non-Steam shortcut)" : "")));
        }
        if (p.ProtonVersion != null)
        {
            facts.Add(new("Last run with", p.ProtonVersion));
        }
        if (p.Modified != default)
        {
            facts.Add(new("Last used", p.Modified.ToLocalTime().ToString("g")));
        }
        var backups = S.Backups.Count(x => x.AppId == p.AppId);
        facts.Add(new("Backups", backups == 0 ? "None" : backups.ToString(), backups == 0 ? Tone.Dim : Tone.Normal));
        return facts;
    }

    public override IReadOnlyList<ToolAction> ActionsFor(object item) => item is PrefixBackup b
        ? Host.Actions.ForBackup(b)
        : Host.Actions.ForPrefix((AppFolder)item, S.Games.FirstOrDefault(g => ReferenceEquals(g.Prefix, item)));
}

public sealed class ShadersSection : Section
{
    public override string Name => "Shaders";
    public override string Summary => "Shader caches per game: sizes, clear one, the orphaned ones, or all.";
    public override IReadOnlyList<string> Columns => ["Game", "App id", "Size", "Last changed", "Status"];
    public override (int Column, bool Descending) DefaultSort => (2, true);
    public override string EmptyText => "No shader caches. Steam fills them as games run (Shader Pre-Caching).";

    public override string Banner
    {
        get
        {
            var total = S.ShaderCaches.Sum(c => Math.Max(0, c.Size));
            var orphans = S.ShaderCaches.Where(c => c.Orphan).ToList();
            return $"{Plural(S.ShaderCaches.Count, "cache")}, {DiskSize.Format(total)}" + (orphans.Count > 0 ? $" · {orphans.Count} orphaned ({DiskSize.Format(orphans.Sum(o => o.Size))})" : "");
        }
    }

    public override IReadOnlyList<Row> Rows() => S.ShaderCaches.OrderByDescending(c => c.Size).Select(c => new Row
    {
        Key = "sc:" + c.Dir,
        Title = c.Owner ?? $"App {c.AppId}",
        Detail = DiskSize.Format(c.Size) + (c.Orphan ? " · not installed" : ""),
        Status = c.Orphan ? "Orphaned" : DiskSize.Format(c.Size),
        StatusTone = c.Orphan ? Tone.Warn : Tone.Dim,
        Cells =
        [
            new Cell(c.Owner ?? "(not installed)", c.Owner ?? "~", c.Orphan ? Tone.Warn : Tone.Normal),
            new Cell(c.AppId, long.Parse(c.AppId)),
            Cell.Size(c.Size),
            Cell.Date(c.Modified),
            new Cell(c.Orphan ? "Orphaned" : "", c.Orphan ? 0 : 1, Tone.Warn),
        ],
        Item = c,
    }).ToList();

    public override IReadOnlyList<Fact> Facts(object item)
    {
        var c = (AppFolder)item;
        return
        [
            new("Belongs to", c.Owner ?? "Nothing installed", c.Orphan ? Tone.Warn : Tone.Normal),
            new("App id", c.AppId),
            new("Size", DiskSize.Format(c.Size)),
            new("Folder", c.Dir),
        ];
    }

    public override string Notes(object item) =>
        "Clearing is safe: Steam downloads pre-compiled shaders again or the game rebuilds them, so the first minutes may stutter.";

    public override IReadOnlyList<ToolAction> ActionsFor(object item) => [Host.Actions.ClearShaderCache((AppFolder)item)];

    public override IReadOnlyList<ToolAction> PageActions => Host.Actions.ForShaderPage();
}

public sealed class StorageSection : Section
{
    public override string Name => "Storage";
    public override string Summary => "What takes space on each drive, moving games between drives, and cleanup.";
    public override IReadOnlyList<string> Columns => ["Game", "Drive", "Game size", "Prefix", "Shaders", "Total"];
    public override (int Column, bool Descending) DefaultSort => (5, true);
    public override string EmptyText => "No Steam libraries found.";

    public override string Banner => S.Cleanup.Count > 0 ? $"{DiskSize.Format(S.Reclaimable)} can be freed ({Plural(S.Cleanup.Count, "item")})" : "Nothing to clean up";

    private static long Total(GameEntry g) => g.Size + Math.Max(0, g.Prefix?.Size ?? 0) + Math.Max(0, g.ShaderCache?.Size ?? 0);

    public override IReadOnlyList<Row> Rows() => S.Games.OrderByDescending(Total).Select(g => new Row
    {
        Key = g.Key,
        Title = g.Title,
        Detail = $"{g.Drive?.Label ?? "?"} · game {DiskSize.Format(g.Size)}" + (g.Prefix != null ? $" · prefix {DiskSize.Format(g.Prefix.Size)}" : "") +
                 (g.ShaderCache != null ? $" · shaders {DiskSize.Format(g.ShaderCache.Size)}" : ""),
        Status = DiskSize.Format(Total(g)),
        StatusTone = Tone.Dim,
        Cover = g.Install.Cover,
        Cells =
        [
            g.Title,
            g.Drive?.Label ?? "",
            Cell.Size(g.Size),
            g.Prefix != null ? Cell.Size(g.Prefix.Size) : new Cell("", -1L),
            g.ShaderCache != null ? Cell.Size(g.ShaderCache.Size) : new Cell("", -1L),
            Cell.Size(Total(g)),
        ],
        Item = g,
    }).ToList();

    public override IReadOnlyList<Fact> Facts(object item)
    {
        var g = (GameEntry)item;
        return
        [
            new("Drive", g.Drive != null ? $"{g.Drive.Label} ({g.Drive.Mount}) · {DiskSize.Format(g.Drive.Free)} free" : "?"),
            new("Library", g.Install.Library ?? g.Install.InstallDir),
            new("Game", DiskSize.Format(g.Size)),
            new("Prefix", g.Prefix != null ? DiskSize.Format(g.Prefix.Size) : "None", g.Prefix != null ? Tone.Normal : Tone.Dim),
            new("Shader cache", g.ShaderCache != null ? DiskSize.Format(g.ShaderCache.Size) : "None", g.ShaderCache != null ? Tone.Normal : Tone.Dim),
            new("Total", DiskSize.Format(Total(g)), Tone.Accent),
        ];
    }

    public override IReadOnlyList<ToolAction> ActionsFor(object item)
    {
        var g = (GameEntry)item;
        return Host.Actions.ForGame(g).Where(a => a.Id is ActionId.MoveGame or ActionId.ClearShaderCache or ActionId.DeletePrefix or ActionId.BackupPrefix or ActionId.OpenInstallFolder).ToList();
    }

    public override IReadOnlyList<ToolAction> PageActions => Host.Actions.ForStoragePage();
}

public sealed class LogsSection : Section
{
    public override string Name => "Logs";
    public override string Summary => "Proton logs (PROTON_LOG=1): errors highlighted, with hints for missing runtimes.";
    public override IReadOnlyList<string> Columns => ["Game", "App id", "Written", "Size"];
    public override (int Column, bool Descending) DefaultSort => (2, true);
    public override string EmptyText => Platform.IsWindows ? "Proton logs exist on Linux only." : "No Proton logs. Turn logging on for a game (Games → the game → Turn Proton logging on), run it, then come back.";

    public override string Banner => S.Logs.Count == 0 ? "" : $"{Plural(S.Logs.Count, "log")}, {DiskSize.Format(S.Logs.Sum(l => l.Size))}";

    public override IReadOnlyList<Row> Rows() => S.Logs.Select(l => new Row
    {
        Key = "log:" + l.Path,
        Title = l.Owner ?? $"App {l.AppId}",
        Detail = $"{l.Modified.ToLocalTime():g} · {DiskSize.Format(l.Size)}",
        Status = DiskSize.Format(l.Size),
        StatusTone = l.Size > 100 << 20 ? Tone.Warn : Tone.Dim,
        Cells = [l.Owner ?? $"App {l.AppId}", new Cell(l.AppId, long.Parse(l.AppId)), Cell.Date(l.Modified), Cell.Size(l.Size, l.Size > 100 << 20 ? Tone.Warn : Tone.Normal)],
        Item = l,
    }).ToList();

    public override IReadOnlyList<Fact> Facts(object item)
    {
        var l = (ProtonLog)item;
        return
        [
            new("Game", l.Owner ?? "app " + l.AppId),
            new("File", l.Path),
            new("Size", DiskSize.Format(l.Size)),
            new("Written", l.Modified.ToLocalTime().ToString("g")),
        ];
    }

    public override IReadOnlyList<ToolAction> ActionsFor(object item) => Host.Actions.ForLog((ProtonLog)item);

    public override IReadOnlyList<ToolAction> PageActions => Host.Actions.ForLogsPage();
}
