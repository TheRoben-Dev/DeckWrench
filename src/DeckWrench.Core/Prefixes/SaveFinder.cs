using DeckWrench.Core.Storage;

namespace DeckWrench.Core.Prefixes;

/// <summary>A folder in a prefix where a game likely keeps its saves or settings.</summary>
public sealed record SaveFolder(string Dir, string Name, DateTime Modified);

/// <summary>
/// Finds where a Windows game keeps saves inside its Proton prefix: the per-game folders under the
/// steamuser profile (Documents, My Games, Saved Games, AppData). Wine's and Microsoft's own folders
/// are skipped and empty folders left out. Newest first. Cheap: no sizes, only a look one level down
/// (fit for the UI thread).
/// </summary>
public static class SaveFinder
{
    private static readonly string[] Roots =
    [
        "Documents/My Games", "Documents", "Saved Games", "AppData/Local", "AppData/LocalLow", "AppData/Roaming",
    ];

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "My Games", "Microsoft", "Temp", "openxr", "D3DSCache", "CrashDumps", "Downloads", "Music", "Pictures", "Videos",
        "My Music", "My Pictures", "My Videos", "Templates", "wine", "Packages", "History", "Temporary Internet Files",
        "Application Data", "NVIDIA", "Desktop",
    };

    public static IReadOnlyList<SaveFolder> Find(string pfxDir, CancellationToken ct = default)
    {
        var user = Path.Combine(pfxDir, "drive_c", "users", "steamuser");
        var list = new List<SaveFolder>();
        foreach (var root in Roots)
        {
            var dir = Path.Combine(user, root.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir) || DiskSize.IsLink(dir))
            {
                continue;
            }
            IEnumerable<string> subs;
            try
            {
                subs = Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var sub in subs)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(sub);
                if (Skip.Contains(name) || DiskSize.IsLink(sub))
                {
                    continue;
                }
                if (Newest(sub) is { } modified)
                {
                    list.Add(new SaveFolder(sub, $"{root}/{name}", modified));
                }
            }
        }
        return list.OrderByDescending(s => s.Modified).ToList();
    }

    /// <summary>Newest change among the folder and its direct children; null when it is empty.</summary>
    private static DateTime? Newest(string dir)
    {
        try
        {
            var entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList();
            return entries.Count == 0 ? null : entries.Max(e => e.LastWriteTimeUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
