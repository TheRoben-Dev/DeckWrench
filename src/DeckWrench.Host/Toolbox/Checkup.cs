using DeckWrench.Core;
using DeckWrench.Core.Logs;
using DeckWrench.Core.Storage;

namespace DeckWrench.Host.Toolbox;

public enum FindingLevel
{
    /// <summary>Something is broken (a game set to a Proton that isn't installed, a crash in its log).</summary>
    Problem,

    /// <summary>Worth doing soon (a drive nearly full, space that can be freed, no backups).</summary>
    Attention,

    /// <summary>All good.</summary>
    Good,
}

/// <summary>One thing the checkup noticed, with the actions that deal with it.</summary>
public sealed record Finding
{
    public required string Key { get; init; }
    public required FindingLevel Level { get; init; }
    public required string Title { get; init; }

    /// <summary>One line under the title.</summary>
    public string Detail { get; init; } = "";

    /// <summary>The longer explanation shown on the finding's page.</summary>
    public string Explain { get; init; } = "";

    /// <summary>Label/value lines for the finding's page.</summary>
    public IReadOnlyList<(string Label, string Value)> Facts { get; init; } = [];

    /// <summary>What deals with it, taken from the same catalog the other sections use.</summary>
    public IReadOnlyList<ToolAction> Actions { get; init; } = [];

    /// <summary>The game it's about, when it's about one.</summary>
    public string? GameKey { get; init; }
}

/// <summary>
/// The Home page: what needs attention after a scan, worst first, each with the action that
/// fixes it. Built only from the scan (plus the tails of the Proton logs), so it costs nothing
/// extra on a rescan and never touches the network.
/// </summary>
public static class Checkup
{
    /// <summary>A drive counts as nearly full under this much free space or this share of its size.</summary>
    public const long LowSpaceBytes = 10L << 30;
    public const double LowSpaceShare = 0.08;

    /// <summary>Space that can be freed is worth a mention from this much.</summary>
    public const long WorthCleaning = 512L << 20;

    public static IReadOnlyList<Finding> Run(ToolboxState s, Actions actions, Func<string, IReadOnlyList<string>>? readLog = null, Func<string, Core.Compat.CompatInfo?>? ratings = null)
    {
        readLog ??= path => ProtonLogs.Tail(path, maxLines: 4000);
        var list = new List<Finding>();

        if (s.SteamRoot == null && s.Games.Count == 0)
        {
            list.Add(new Finding
            {
                Key = "no-steam",
                Level = FindingLevel.Attention,
                Title = "Steam wasn't found",
                Detail = "DeckWrench looks for Steam (native and Flatpak), Heroic and Lutris.",
                Explain = "Install Steam and run it once, then scan again.",
                Actions = actions.Global().Where(a => a.Id == ActionId.Rescan).ToList(),
            });
            return list;
        }

        // Games set to a Proton that isn't installed don't start.
        foreach (var g in s.Games.Where(g => g.RunsInSteam && g.EffectiveCompatTool is { } t && s.Tool(t) == null))
        {
            list.Add(new Finding
            {
                Key = "missing-tool:" + g.Key,
                Level = FindingLevel.Problem,
                GameKey = g.Key,
                Title = $"{g.Title} uses a Proton that isn't installed",
                Detail = $"Set to {s.ToolLabel(g.EffectiveCompatTool)}. Steam won't start it like this.",
                Explain = "The Proton version this game is set to was removed or never downloaded. Pick an installed one, or download that GE-Proton again from the Proton tab.",
                Facts = [("Game", g.Title), ("Set to", g.EffectiveCompatTool!)],
                Actions = actions.ForGame(g).Where(a => a.Id is ActionId.SetCompatTool or ActionId.TryOtherProton).ToList(),
            });
        }

        // The newest Proton log of each game: crashes and missing DLLs, with a fix where one is known.
        foreach (var g in s.Games.Where(g => g.Log != null).OrderByDescending(g => g.Log!.Modified))
        {
            IReadOnlyList<LogHint> hints;
            try
            {
                hints = ProtonLogs.Hints(readLog(g.Log!.Path));
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            if (hints.Count == 0)
            {
                continue;
            }
            var fixes = new List<ToolAction>();
            var verbs = hints.Where(h => h.Verb != null).Select(h => h.Verb!).Distinct().ToList();
            if (verbs.Count > 0 && g.Prefix != null && actions.ForPrefix(g.Prefix, g).FirstOrDefault(a => a.Id == ActionId.RunTricks) is { } tricks)
            {
                // The usual picker, with the verbs the log asks for already ticked.
                var labels = verbs.Select(v => Core.Tricks.TricksCatalog.Find(v)?.Label ?? v);
                fixes.Add(tricks with
                {
                    Label = "Install " + string.Join(", ", labels),
                    Help = "Opens protontricks' picker for this game's prefix with what the log asks for already ticked.",
                    Input = tricks.Input! with { Choices = tricks.Input.Choices.Select(c => c with { Selected = verbs.Contains(c.Id) }).ToList() },
                });
            }
            fixes.AddRange(actions.ForLog(g.Log!).Where(a => a.Id == ActionId.ViewLog));
            if (g.RunsInSteam)
            {
                fixes.AddRange(actions.ForGame(g).Where(a => a.Id is ActionId.TryOtherProton or ActionId.SetCompatTool));
            }
            list.Add(new Finding
            {
                Key = "log:" + g.Key,
                Level = FindingLevel.Problem,
                GameKey = g.Key,
                Title = Short(hints[0].Text) is var first && first.StartsWith("The game ", StringComparison.Ordinal) ? $"{g.Title} {first["The game ".Length..]}" : $"{g.Title}: {first}",
                Detail = hints.Count > 1 ? $"and {hints.Count - 1} more in its Proton log" : $"From its Proton log, {Ago(g.Log!.Modified)}",
                Explain = string.Join("\n", hints.Select(h => "• " + h.Text)),
                Facts = [("Game", g.Title), ("Proton", g.RunsInSteam ? s.ToolLabel(g.EffectiveCompatTool) : "?"), ("Log written", g.Log!.Modified.ToLocalTime().ToString("g")),
                    .. (ratings?.Invoke(g.AppId) is { } r && g.IsSteam ? new[] { ("ProtonDB", Core.Compat.CompatInfo.TierLabel(r.Tier)), ("Steam Deck", Core.Compat.CompatInfo.DeckLabel(r.Deck)) } : [])],
                Actions = fixes,
            });
        }

        // Steam changes waiting for a restart: easy to forget, and the game runs without them until then.
        if (s.Pending.Count > 0)
        {
            list.Add(new Finding
            {
                Key = "pending",
                Level = FindingLevel.Attention,
                Title = s.Pending.Count == 1 ? "1 change is waiting for Steam to restart" : $"{s.Pending.Count} changes are waiting for Steam to restart",
                Detail = string.Join(" · ", s.Pending.Take(3).Select(p => p.Describe())),
                Explain = "Steam rewrites its config when it exits, so DeckWrench holds these until Steam restarts:\n" + string.Join("\n", s.Pending.Select(p => "• " + p.Describe())),
                Actions = actions.PendingActions(),
            });
        }

        // Drives running out of space.
        var storage = actions.ForStoragePage();
        foreach (var d in s.Drives.Where(d => d.Drive.Total > 0 && (d.Drive.Free < LowSpaceBytes || d.Drive.Free < d.Drive.Total * LowSpaceShare)))
        {
            var dr = d.Drive;
            list.Add(new Finding
            {
                Key = "low-space:" + dr.Mount,
                Level = dr.Free < 2L << 30 ? FindingLevel.Problem : FindingLevel.Attention,
                Title = $"{dr.Label} is nearly full",
                Detail = $"{DiskSize.Format(dr.Free)} free of {DiskSize.Format(dr.Total)}. Games can fail to update or save.",
                Explain = "Free space on this drive lists what can go from it and its biggest games that fit on another drive, with the total before anything happens.",
                Facts = [.. d.Breakdown.Where(b => b.Size > 0).Select(b => (b.Label, DiskSize.Format(b.Size))), ("Free", DiskSize.Format(dr.Free))],
                Actions = storage.Where(a => a.Id == ActionId.FreeSpaceOnDrive && a.Label.StartsWith($"Free space on {dr.Label} ", StringComparison.Ordinal)).ToList(),
            });
        }

        // Space that can go.
        if (s.Reclaimable >= WorthCleaning)
        {
            var groups = s.Cleanup.GroupBy(c => c.Kind).Select(g => (Label: KindLabel(g.Key, g.Count()), Size: g.Sum(c => c.Size))).OrderByDescending(g => g.Size).ToList();
            list.Add(new Finding
            {
                Key = "cleanup",
                Level = FindingLevel.Attention,
                Title = $"{DiskSize.Format(s.Reclaimable)} can be freed",
                Detail = string.Join(" · ", groups.Take(3).Select(g => $"{g.Label} {DiskSize.Format(g.Size)}")),
                Explain = "Leftovers of games you uninstalled, Proton versions nothing uses, old logs and backups. You pick what goes; prefixes (they can hold saves) start unticked.",
                Facts = groups.Select(g => (g.Label, DiskSize.Format(g.Size))).ToList(),
                Actions = storage.Where(a => a.Id == ActionId.Cleanup).ToList(),
            });
        }

        // Prefixes without a backup: saves without Steam Cloud live there.
        if (!Platform.IsWindows)
        {
            var noBackup = s.Prefixes.Where(p => !p.External && !p.Orphan && p.Size > 0 && !s.Backups.Any(b => b.AppId == p.AppId)).ToList();
            if (noBackup.Count > 0)
            {
                list.Add(new Finding
                {
                    Key = "no-backup",
                    Level = FindingLevel.Attention,
                    Title = noBackup.Count == 1 ? $"{noBackup[0].Owner ?? "1 game"}'s prefix has no backup" : $"{noBackup.Count} game prefixes have no backup",
                    Detail = "Saves without Steam Cloud and your fixes live there. " + DiskSize.Format(noBackup.Sum(p => p.Size)) + " in all.",
                    Explain = "A prefix holds the game's Windows side: saves that Steam Cloud doesn't sync, settings, and runtimes you installed. A backup brings it back after a reset, a reinstall or a SteamOS update.",
                    Facts = noBackup.OrderByDescending(p => p.Size).Take(8).Select(p => (p.Owner ?? "App " + p.AppId, DiskSize.Format(p.Size))).ToList(),
                    Actions = actions.ForPrefixPage().Where(a => a.Id == ActionId.BackupAllPrefixes).ToList(),
                });
            }
        }

        if (list.Count == 0)
        {
            list.Add(new Finding
            {
                Key = "all-good",
                Level = FindingLevel.Good,
                Title = "Nothing needs your attention",
                Detail = $"{s.Games.Count} games checked: Proton settings, logs, free space and backups.",
                Explain = "DeckWrench checks this after every scan.",
                Actions = actions.Global().Where(a => a.Id == ActionId.Rescan).ToList(),
            });
        }

        return list.OrderBy(f => f.Level).ToList();
    }

    /// <summary>One line for the top of Home ("6 games · 2 drives · 23 GB can be freed").</summary>
    public static string Summary(ToolboxState s, IReadOnlyList<Finding> findings)
    {
        var parts = new List<string> { $"{s.Games.Count} game{(s.Games.Count == 1 ? "" : "s")}" };
        if (s.Drives.Count > 0)
        {
            parts.Add($"{s.Drives.Count} drive{(s.Drives.Count == 1 ? "" : "s")}, {DiskSize.Format(s.Drives.Sum(d => d.Drive.Free))} free");
        }
        var problems = findings.Count(f => f.Level == FindingLevel.Problem);
        var attention = findings.Count(f => f.Level == FindingLevel.Attention);
        parts.Add(problems + attention == 0 ? "all good"
            : string.Join(", ", new[] { problems > 0 ? $"{problems} problem{(problems == 1 ? "" : "s")}" : null, attention > 0 ? $"{attention} to look at" : null }.Where(x => x != null)));
        return string.Join(" · ", parts);
    }

    private static string KindLabel(CleanupKind k, int n) => k switch
    {
        CleanupKind.OrphanPrefix => $"{n} leftover prefix{(n == 1 ? "" : "es")}",
        CleanupKind.OrphanShaderCache => $"{n} leftover shader cache{(n == 1 ? "" : "s")}",
        CleanupKind.UnusedProton => $"{n} unused Proton",
        CleanupKind.ProtonLog => $"{n} log{(n == 1 ? "" : "s")}",
        CleanupKind.OldBackup => $"{n} old backup{(n == 1 ? "" : "s")}",
        CleanupKind.SteamLeftover => $"{n} Steam leftover{(n == 1 ? "" : "s")}",
        _ => k.ToString(),
    };

    /// <summary>The first clause of a hint ("MSVCP140.dll is missing", "The game crashed").</summary>
    public static string Short(string hint)
    {
        var cut = new[] { hint.IndexOf(':'), hint.IndexOf(". ", StringComparison.Ordinal), hint.IndexOf(" (", StringComparison.Ordinal) }.Where(i => i > 0).DefaultIfEmpty(hint.Length).Min();
        var first = hint[..cut].TrimEnd('.');
        return first.Length > 60 ? first[..57] + "…" : first;
    }

    private static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        return d.TotalHours < 1 ? "just now" : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago" : $"{(int)d.TotalDays} days ago";
    }
}
