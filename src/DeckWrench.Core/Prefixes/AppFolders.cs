using DeckWrench.Core.Storage;

namespace DeckWrench.Core.Prefixes;

public enum AppFolderKind
{
    /// <summary>steamapps/compatdata/&lt;appid&gt;: a Proton prefix (pfx/ holds drive_c).</summary>
    Prefix,

    /// <summary>steamapps/shadercache/&lt;appid&gt;: compiled shaders (Fossilize, DXVK/VKD3D, Mesa caches).</summary>
    ShaderCache,
}

/// <summary>A per-game folder Steam keeps next to the games: a Proton prefix or a shader cache.</summary>
public sealed record AppFolder
{
    public required AppFolderKind Kind { get; init; }

    public required string AppId { get; init; }

    /// <summary>The folder itself (compatdata/&lt;appid&gt; or shadercache/&lt;appid&gt;).</summary>
    public required string Dir { get; init; }

    /// <summary>The Steam library it's in.</summary>
    public required string Library { get; init; }

    /// <summary>The installed game or non-Steam shortcut it belongs to; null when nothing owns it.</summary>
    public string? Owner { get; init; }

    /// <summary>True when it belongs to a non-Steam shortcut.</summary>
    public bool IsShortcut { get; init; }

    /// <summary>
    /// True when no installed game or shortcut uses it (the game was uninstalled, Steam kept the
    /// folder). Steam's own tools (Proton, runtimes) are never orphans.
    /// </summary>
    public bool Orphan => Owner == null;

    /// <summary>Proton build that last ran this prefix (compatdata/&lt;appid&gt;/version), prefixes only.</summary>
    public string? ProtonVersion { get; init; }

    public long Size { get; init; } = -1;

    public DateTime Modified { get; init; }

    private readonly string? _pfx;

    /// <summary>The Wine prefix itself (holds drive_c): pfx/ inside a Proton prefix, the folder itself for Wine prefixes.</summary>
    public string PfxDir { get => _pfx ?? Path.Combine(Dir, "pfx"); init => _pfx = value; }

    /// <summary>Heroic or Lutris prefix (not in a Steam library).</summary>
    public bool External { get; init; }
}

/// <summary>Lists compatdata and shadercache folders across libraries and matches them to their owners.</summary>
public static class AppFolders
{
    private static string SubDir(AppFolderKind kind) => kind == AppFolderKind.Prefix ? "compatdata" : "shadercache";

    /// <param name="owners">App id → name, for installed games, shortcuts, and Steam tools that keep folders.</param>
    /// <param name="shortcuts">App ids of non-Steam shortcuts (subset of owners).</param>
    /// <param name="measure">Measure folder sizes (slow on big prefixes; off for quick listings).</param>
    public static IReadOnlyList<AppFolder> Scan(AppFolderKind kind, IEnumerable<string> libraries, IReadOnlyDictionary<string, string> owners,
        IReadOnlySet<string> shortcuts, bool measure = true, CancellationToken ct = default)
    {
        var list = new List<AppFolder>();
        var seen = new HashSet<string>(Platform.PathComparer);
        foreach (var lib in libraries)
        {
            var root = Path.Combine(lib, "steamapps", SubDir(kind));
            List<string> dirs;
            try
            {
                dirs = Directory.Exists(root) ? Directory.EnumerateDirectories(root).ToList() : [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var dir in dirs)
            {
                ct.ThrowIfCancellationRequested();
                var id = Path.GetFileName(dir);
                if (!id.All(char.IsAsciiDigit) || id == "0" || !seen.Add(PathUtil.RealPath(dir)))
                {
                    continue;
                }
                list.Add(new AppFolder
                {
                    Kind = kind,
                    AppId = id,
                    Dir = dir,
                    Library = lib,
                    Owner = owners.GetValueOrDefault(id),
                    IsShortcut = shortcuts.Contains(id),
                    ProtonVersion = kind == AppFolderKind.Prefix ? ReadVersion(dir) : null,
                    Size = measure ? DiskSize.Of(dir, ct) : -1,
                    Modified = SafeModified(dir),
                });
            }
        }
        return list;
    }

    private static string? ReadVersion(string dir)
    {
        try
        {
            var f = Path.Combine(dir, "version");
            return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTime SafeModified(string dir)
    {
        try
        {
            // A prefix's registry changes every time the game runs; the folder itself rarely does.
            var reg = Path.Combine(dir, "pfx", "system.reg");
            return File.Exists(reg) ? File.GetLastWriteTimeUtc(reg) : Directory.GetLastWriteTimeUtc(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }
}
