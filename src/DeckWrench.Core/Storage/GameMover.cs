using DeckWrench.Core.Detection;

namespace DeckWrench.Core.Storage;

/// <summary>What moving a game takes: its folder, manifest, prefix and shader cache, and the space it needs.</summary>
public sealed record MovePlan
{
    public required InstalledGame Game { get; init; }
    public required string FromLibrary { get; init; }
    public required string ToLibrary { get; init; }

    /// <summary>(source, destination, size) for each folder or file that moves.</summary>
    public required IReadOnlyList<(string From, string To, long Size)> Items { get; init; }

    public long TotalSize => Items.Sum(i => i.Size);

    /// <summary>True when both libraries are on the same drive (a rename, nothing is copied).</summary>
    public bool SameDrive { get; init; }
}

/// <summary>
/// Moves a Steam game between libraries the way Steam's "Move install folder" does: the game
/// folder, its appmanifest, and its compatdata prefix and shader cache, which Steam keeps in the
/// game's own library. Only while Steam is closed (Steam rewrites manifests while running).
/// Copies go to a hidden staging name first and the source is only removed once everything
/// arrived, so a cancelled or failed move leaves the game where it was.
/// </summary>
public static class GameMover
{
    public static MovePlan Plan(InstalledGame game, string toLibrary)
    {
        if (game.Library == null || game.Manifest == null)
        {
            throw new InvalidOperationException("Only Steam games can be moved between libraries.");
        }
        if (PathUtil.RealPath(game.Library) == PathUtil.RealPath(toLibrary))
        {
            throw new InvalidOperationException("The game is already in that library.");
        }
        var from = Path.Combine(game.Library, "steamapps");
        var to = Path.Combine(toLibrary, "steamapps");
        var items = new List<(string, string, long)>
        {
            (game.InstallDir, Path.Combine(to, "common", Path.GetFileName(game.InstallDir)), game.SizeOnDisk > 0 ? game.SizeOnDisk : DiskSize.Of(game.InstallDir)),
        };
        foreach (var sub in new[] { "compatdata", "shadercache" })
        {
            var dir = Path.Combine(from, sub, game.SourceId);
            if (Directory.Exists(dir))
            {
                items.Add((dir, Path.Combine(to, sub, game.SourceId), DiskSize.Of(dir)));
            }
        }
        items.Add((game.Manifest, Path.Combine(to, Path.GetFileName(game.Manifest)), new FileInfo(game.Manifest).Length));
        foreach (var (_, dest, _) in items)
        {
            if (File.Exists(dest) || Directory.Exists(dest))
            {
                throw new InvalidOperationException($"{dest} already exists in the target library. Remove it first.");
            }
        }
        var a = Drives.For(game.Library);
        var b = Drives.For(toLibrary);
        return new MovePlan
        {
            Game = game,
            FromLibrary = game.Library,
            ToLibrary = toLibrary,
            Items = items,
            SameDrive = a != null && b != null && a.Mount == b.Mount,
        };
    }

    public static void Execute(MovePlan plan, IProgress<double>? progress, CancellationToken ct)
    {
        if (!plan.SameDrive && Drives.For(plan.ToLibrary) is { Total: > 0 } target && target.Free < plan.TotalSize + (256L << 20))
        {
            throw new IOException($"Not enough space on {target.Label}: the move needs {DiskSize.Format(plan.TotalSize)}, {DiskSize.Format(target.Free)} is free.");
        }
        var total = Math.Max(1, plan.TotalSize);
        long done = 0;
        var staged = new List<(string Staging, string Final, string Source)>();
        try
        {
            foreach (var (from, to, size) in plan.Items)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                var staging = Path.Combine(Path.GetDirectoryName(to)!, ".deckwrench-move-" + Path.GetFileName(to));
                if (plan.SameDrive)
                {
                    // Same file system: renaming is instant and atomic; nothing to stage.
                    staged.Add((from, to, from));
                    done += size;
                    continue;
                }
                if (File.Exists(from))
                {
                    File.Copy(from, staging, overwrite: true);
                    done += size;
                }
                else
                {
                    CopyTree(from, staging, n =>
                    {
                        done += n;
                        progress?.Report(Math.Min(0.99, (double)done / total));
                    }, ct);
                }
                staged.Add((staging, to, from));
                progress?.Report(Math.Min(0.99, (double)done / total));
            }
        }
        catch
        {
            if (!plan.SameDrive)
            {
                foreach (var (staging, _, _) in staged)
                {
                    Remove(staging);
                }
            }
            throw;
        }
        // Everything arrived: put it in place, manifest last so Steam never sees a game without its files.
        foreach (var (staging, final, source) in staged.OrderBy(s => s.Final.EndsWith(".acf", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            if (File.Exists(staging))
            {
                File.Move(staging, final);
            }
            else
            {
                Directory.Move(staging, final);
            }
            if (!plan.SameDrive)
            {
                Remove(source);
            }
        }
        progress?.Report(1);
    }

    /// <summary>Copies a folder tree; symbolic links are recreated as links, not followed.</summary>
    public static void CopyTree(string from, string to, Action<long>? copied, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        foreach (var e in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(to, e.Name);
            if (e.LinkTarget is { } link)
            {
                if (e is DirectoryInfo)
                {
                    Directory.CreateSymbolicLink(dest, link);
                }
                else
                {
                    File.CreateSymbolicLink(dest, link);
                }
            }
            else if (e is DirectoryInfo d)
            {
                CopyTree(d.FullName, dest, copied, ct);
            }
            else if (e is FileInfo f)
            {
                f.CopyTo(dest, overwrite: false);
                File.SetLastWriteTimeUtc(dest, f.LastWriteTimeUtc);
                copied?.Invoke(f.Length);
            }
        }
    }

    private static void Remove(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Prefixes.PrefixBackups.DeleteTree(path);
        }
    }
}
