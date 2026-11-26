using DeckWrench.Core.Storage;

namespace DeckWrench.Host.Toolbox;

/// <summary>One way to free space on a drive: remove a cleanup item, or move a game to another drive.</summary>
public sealed record SpaceOption
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string Detail { get; init; } = "";
    public string Group { get; init; } = "";

    /// <summary>Space this frees on the drive.</summary>
    public long Frees { get; init; }

    /// <summary>Ticked to start with (safe removals; not prefixes, which can hold saves, and not moves).</summary>
    public bool Suggested { get; init; }

    public CleanupItem? Cleanup { get; init; }
    public GameEntry? Game { get; init; }

    /// <summary>The Steam library a moved game goes to.</summary>
    public string? MoveTo { get; init; }
}

/// <summary>
/// Plans freeing space on one drive (the SD card, the internal SSD): cleanup items that live on
/// it, then its biggest Steam games, each with the library on another drive with the most room.
/// </summary>
public static class SpacePlanner
{
    /// <summary>Headroom left on the target drive after a move.</summary>
    public const long Headroom = 1L << 30;

    public const string CleanupGroup = "Remove from this drive";
    public const string MoveGroup = "Move to another drive";

    public static IReadOnlyList<SpaceOption> Options(ToolboxState s, Drive drive, Func<string, Drive?> driveOf, bool movesAllowed, Func<string, string>? libraryLabel = null)
    {
        libraryLabel ??= l => l;
        bool OnDrive(string path) => driveOf(path) is { } d && d.Mount == drive.Mount;

        var list = new List<SpaceOption>();
        foreach (var c in s.Cleanup.Where(c => OnDrive(c.Path)).OrderByDescending(c => c.Size))
        {
            list.Add(new SpaceOption
            {
                Id = "clean:" + c.Path,
                Label = $"{c.Title} · {DiskSize.Format(c.Size)}",
                Detail = c.Caution ?? c.Detail,
                Group = CleanupGroup,
                Frees = c.Size,
                Suggested = c.Kind != CleanupKind.OrphanPrefix,
                Cleanup = c,
            });
        }
        if (!movesAllowed)
        {
            return list;
        }

        // Other drives with a Steam library, most free space first; each move uses up some of it.
        var targets = s.Libraries
            .Select(l => (Library: l, Drive: driveOf(l)))
            .Where(t => t.Drive != null && t.Drive.Mount != drive.Mount)
            .GroupBy(t => t.Drive!.Mount)
            .Select(g => (g.First().Library, Drive: g.First().Drive!))
            .ToList();
        if (targets.Count == 0)
        {
            return list;
        }
        var games = s.Games
            .Where(g => g.IsSteam && g.Install.Library != null && OnDrive(g.Install.Library))
            .Select(g => (Game: g, Size: g.Size + Math.Max(0, g.Prefix?.Size ?? 0) + Math.Max(0, g.ShaderCache?.Size ?? 0)))
            .Where(x => x.Size > 0)
            .OrderByDescending(x => x.Size);
        foreach (var (g, size) in games)
        {
            var best = targets.OrderByDescending(t => t.Drive.Free).First();
            if (best.Drive.Free < size + Headroom)
            {
                continue;  // doesn't fit anywhere
            }
            list.Add(new SpaceOption
            {
                Id = "move:" + g.AppId,
                Label = $"{g.Title} · {DiskSize.Format(size)}",
                Detail = $"Moves the game, prefix and shader cache to {libraryLabel(best.Library)} ({DiskSize.Format(best.Drive.Free)} free there).",
                Group = MoveGroup,
                Frees = size,
                Game = g,
                MoveTo = best.Library,
            });
        }
        return list;
    }

    /// <summary>The picked options (ids joined with '\n'), and moves checked to still fit when several go to one drive.</summary>
    public static IReadOnlyList<SpaceOption> Picked(IReadOnlyList<SpaceOption> options, string? input, Func<string, Drive?> driveOf)
    {
        var ids = (input ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var picked = options.Where(o => ids.Contains(o.Id)).ToList();
        foreach (var target in picked.Where(o => o.MoveTo != null).GroupBy(o => driveOf(o.MoveTo!)?.Mount))
        {
            var d = driveOf(target.First().MoveTo!);
            var need = target.Sum(o => o.Frees);
            if (d != null && d.Free < need + Headroom)
            {
                throw new InvalidOperationException(
                    $"The picked games need {DiskSize.Format(need)} on {d.Label}, which has {DiskSize.Format(d.Free)} free. Pick fewer games to move.");
            }
        }
        return picked;
    }
}
