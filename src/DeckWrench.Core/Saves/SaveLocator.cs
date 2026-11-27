using System.Text.RegularExpressions;
using DeckWrench.Core.Prefixes;
using DeckWrench.Core.Storage;

namespace DeckWrench.Core.Saves;

/// <summary>Where a game's saves can be, as named roots: a save file is stored by its root name and its path under it.</summary>
public sealed record SaveRoots
{
    /// <summary>The Windows user profile in the prefix (pfx/drive_c/users/steamuser), for Proton games.</summary>
    public string? WinHome { get; init; }

    /// <summary>The prefix's drive_c, for ProgramData and Public.</summary>
    public string? DriveC { get; init; }

    /// <summary>The game's install folder.</summary>
    public string? Install { get; init; }

    /// <summary>The Steam library the game is in (Ludusavi's &lt;root&gt;).</summary>
    public string? Library { get; init; }

    /// <summary>The player's real home folder (native games, and Steam's userdata).</summary>
    public required string Home { get; init; }

    /// <summary>Steam's install folder (for userdata/&lt;account&gt;/&lt;appid&gt;).</summary>
    public string? SteamRoot { get; init; }

    public string? AppId { get; init; }

    /// <summary>"windows" when the game runs with Proton (it has a prefix), else "linux".</summary>
    public string Os => WinHome != null ? "windows" : "linux";

    /// <summary>Named roots, most specific first, so a file is stored under the deepest one it's in.</summary>
    public IReadOnlyList<(string Name, string Dir)> Named()
    {
        var list = new List<(string, string)>();
        void Add(string name, string? dir)
        {
            if (dir != null)
            {
                list.Add((name, Path.TrimEndingDirectorySeparator(dir)));
            }
        }
        Add("winhome", WinHome);
        Add("drive_c", DriveC);
        Add("game", Install);
        if (SteamRoot != null && AppId != null)
        {
            Add("userdata", Path.Combine(SteamRoot, "userdata"));
        }
        Add("library", Library);
        Add("home", Home);
        return list.OrderByDescending(x => x.Item2.Length).ToList();
    }
}

/// <summary>A save file found for a game: where it is, and where it goes in a backup.</summary>
public sealed record SaveFile(string Path, string Root, string Relative, long Size, DateTime Modified);

/// <summary>
/// Finds a game's save and config files: the manifest's rules for it (placeholders filled in
/// for its prefix, install folder and library), Steam's own userdata for it, and, when the
/// manifest doesn't know the game (or its paths match nothing), the per-game folders in the prefix
/// (<see cref="SaveFinder"/>).
/// </summary>
public static partial class SaveLocator
{
    /// <summary>No game's saves are this big; a rule that matches more is skipped (it would be the game itself).</summary>
    public const long RuleLimit = 2L << 30;

    public const int FileLimit = 20000;

    public static IReadOnlyList<SaveFile> Find(SaveRoots roots, ManifestGame? manifest, CancellationToken ct = default)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var os = roots.Os;
        if (manifest != null)
        {
            foreach (var rule in manifest.Rules.Where(r => r.AppliesTo(os)))
            {
                if (Expand(rule.Pattern, roots) is not { } pattern)
                {
                    continue;
                }
                var matched = Glob(pattern, caseInsensitive: os == "windows", ct).SelectMany(p => FilesUnder(p, ct)).Take(FileLimit + 1).ToList();
                if (matched.Count > FileLimit || matched.Sum(f => SafeLength(f)) > RuleLimit)
                {
                    continue;
                }
                paths.UnionWith(matched);
            }
        }
        var fromManifest = paths.Count > 0;
        // Steam's local copy of the game's cloud files and its per-game config, for every account.
        if (roots.SteamRoot != null && roots.AppId != null && Directory.Exists(Path.Combine(roots.SteamRoot, "userdata")))
        {
            foreach (var account in SafeDirs(Path.Combine(roots.SteamRoot, "userdata")))
            {
                paths.UnionWith(FilesUnder(Path.Combine(account, roots.AppId), ct).Take(FileLimit));
            }
        }
        // The manifest doesn't know the game, or its paths matched nothing here: the prefix's game folders.
        if (!fromManifest && roots.WinHome != null)
        {
            var pfx = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(roots.WinHome)!)!)!;  // .../pfx
            foreach (var folder in SaveFinder.Find(pfx, ct))
            {
                var files = FilesUnder(folder.Dir, ct).Take(FileLimit + 1).ToList();
                if (files.Count <= FileLimit && files.Sum(SafeLength) <= RuleLimit)
                {
                    paths.UnionWith(files);
                }
            }
        }
        var named = roots.Named();
        var result = new List<SaveFile>();
        foreach (var p in paths.Order(StringComparer.Ordinal))
        {
            var root = named.FirstOrDefault(n => p.StartsWith(n.Dir + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            if (root.Name == null)
            {
                continue;  // outside every root: never stored, so never written back anywhere unexpected
            }
            var fi = new FileInfo(p);
            result.Add(new SaveFile(p, root.Name, Path.GetRelativePath(root.Dir, p).Replace('\\', '/'), fi.Length, fi.LastWriteTimeUtc));
        }
        return result;
    }

    /// <summary>The manifest's placeholders filled in; null when the rule needs something this game doesn't have (or the registry).</summary>
    public static string? Expand(string pattern, SaveRoots r)
    {
        var windows = r.WinHome != null;
        string? Value(string name) => name switch
        {
            "base" => r.Install,
            "game" => r.Install != null ? Path.GetFileName(Path.TrimEndingDirectorySeparator(r.Install)) : null,
            "root" => r.Library,
            "home" => windows ? r.WinHome : r.Home,
            "storeUserId" => "*",
            "storeGameId" => r.AppId,
            "osUserName" => windows ? "steamuser" : Path.GetFileName(Path.TrimEndingDirectorySeparator(r.Home)),
            "winAppData" => windows ? Path.Combine(r.WinHome!, "AppData", "Roaming") : null,
            "winLocalAppData" => windows ? Path.Combine(r.WinHome!, "AppData", "Local") : null,
            "winLocalAppDataLow" => windows ? Path.Combine(r.WinHome!, "AppData", "LocalLow") : null,
            "winDocuments" => windows ? Path.Combine(r.WinHome!, "Documents") : null,
            "winPublic" => windows && r.DriveC != null ? Path.Combine(r.DriveC, "users", "Public") : null,
            "winProgramData" => windows && r.DriveC != null ? Path.Combine(r.DriveC, "ProgramData") : null,
            "winDir" => windows && r.DriveC != null ? Path.Combine(r.DriveC, "windows") : null,
            "xdgData" => windows ? null : Path.Combine(r.Home, ".local", "share"),
            "xdgConfig" => windows ? null : Path.Combine(r.Home, ".config"),
            _ => null,
        };
        var missing = false;
        var expanded = Placeholder().Replace(pattern, m =>
        {
            var v = Value(m.Groups[1].Value);
            missing |= v == null;
            return v ?? "";
        });
        if (missing || !expanded.StartsWith('/'))
        {
            return null;
        }
        return expanded.Replace('\\', '/');
    }

    [GeneratedRegex("<([A-Za-z]+)>")]
    private static partial Regex Placeholder();

    /// <summary>Paths (files or folders) matching a glob: * ? [..] within a name, ** across folders. Links aren't followed.</summary>
    public static IEnumerable<string> Glob(string pattern, bool caseInsensitive, CancellationToken ct = default)
    {
        var segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Walk("/", segments, 0, caseInsensitive, ct).Distinct(StringComparer.Ordinal);
    }

    private static IEnumerable<string> Walk(string dir, string[] segs, int i, bool ci, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (i == segs.Length)
        {
            yield return Path.TrimEndingDirectorySeparator(dir) is { Length: > 0 } d ? d : "/";
            yield break;
        }
        var seg = segs[i];
        if (seg == "**")
        {
            foreach (var x in Walk(dir, segs, i + 1, ci, ct))
            {
                yield return x;
            }
            foreach (var sub in SafeDirs(dir))
            {
                foreach (var x in Walk(sub, segs, i, ci, ct))
                {
                    yield return x;
                }
            }
            yield break;
        }
        var wild = seg.IndexOfAny(['*', '?', '[']) >= 0;
        if (!wild && !ci)
        {
            var next = Path.Combine(dir, seg);
            if (Exists(next))
            {
                foreach (var x in Walk(next, segs, i + 1, ci, ct))
                {
                    yield return x;
                }
            }
            yield break;
        }
        var re = new Regex("^" + GlobToRegex(seg) + "$", ci ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant);
        foreach (var entry in SafeEntries(dir))
        {
            if (re.IsMatch(Path.GetFileName(entry)) && (i == segs.Length - 1 || (Directory.Exists(entry) && !DiskSize.IsLink(entry))))
            {
                foreach (var x in Walk(entry, segs, i + 1, ci, ct))
                {
                    yield return x;
                }
            }
        }
    }

    private static string GlobToRegex(string seg)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < seg.Length; i++)
        {
            var c = seg[i];
            switch (c)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                case '[':
                    var end = seg.IndexOf(']', i + 1);
                    if (end > i + 1)
                    {
                        var body = seg[(i + 1)..end];
                        sb.Append('[').Append(body.StartsWith('!') ? "^" + Regex.Escape(body[1..]) : Regex.Escape(body)).Append(']');
                        i = end;
                    }
                    else
                    {
                        sb.Append("\\[");
                    }
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        return sb.ToString();
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    private static IEnumerable<string> FilesUnder(string path, CancellationToken ct)
    {
        if (DiskSize.IsLink(path))
        {
            yield break;
        }
        if (File.Exists(path))
        {
            yield return path;
            yield break;
        }
        if (!Directory.Exists(path))
        {
            yield break;
        }
        foreach (var entry in SafeEntries(path))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var f in FilesUnder(entry, ct))
            {
                yield return f;
            }
        }
    }

    private static IEnumerable<string> SafeEntries(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).ToList() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeDirs(string dir) => SafeEntries(dir).Where(d => Directory.Exists(d) && !DiskSize.IsLink(d));

    private static long SafeLength(string f)
    {
        try
        {
            return new FileInfo(f).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
