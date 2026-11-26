namespace DeckWrench.Core.Steam;

public enum LeftoverKind
{
    /// <summary>steamapps/downloading/&lt;appid&gt;: a download or update that never finished.</summary>
    Download,

    /// <summary>steamapps/temp/&lt;appid&gt;: Steam's scratch space for an update.</summary>
    Temp,

    /// <summary>steamapps/workshop/content/&lt;appid&gt;: Workshop items of a game that isn't installed.</summary>
    Workshop,
}

/// <summary>A folder Steam left behind for an app that has no install (no appmanifest) in any library.</summary>
public sealed record LeftoverFolder(LeftoverKind Kind, string AppId, string Dir)
{
    public long Size { get; init; } = -1;
}

/// <summary>
/// Finds Steam's leftovers: unfinished downloads, update scratch folders and Workshop content of
/// apps that aren't installed. Only per-app folders whose app has no appmanifest_&lt;id&gt;.acf in
/// any library count, so nothing that belongs to an installed (or installing) app is listed:
/// Steam writes the manifest the moment an install starts.
/// </summary>
public static class SteamLeftovers
{
    public static IReadOnlyList<LeftoverFolder> Scan(IReadOnlyList<string> libraries)
    {
        var installed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lib in libraries)
        {
            try
            {
                var apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps))
                {
                    continue;
                }
                foreach (var m in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    installed.Add(Path.GetFileNameWithoutExtension(m)["appmanifest_".Length..]);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        var list = new List<LeftoverFolder>();
        foreach (var lib in libraries)
        {
            foreach (var (kind, sub) in new[] { (LeftoverKind.Download, "downloading"), (LeftoverKind.Temp, "temp"), (LeftoverKind.Workshop, Path.Combine("workshop", "content")) })
            {
                var root = Path.Combine(lib, "steamapps", sub);
                try
                {
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        var id = Path.GetFileName(dir);
                        if (id.Length > 0 && id.All(char.IsAsciiDigit) && !installed.Contains(id) && !Storage.DiskSize.IsLink(dir))
                        {
                            list.Add(new LeftoverFolder(kind, id, dir));
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        return list;
    }
}
