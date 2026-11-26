using DeckWrench.Core;
using DeckWrench.Core.Detection;
using DeckWrench.Core.Logs;
using DeckWrench.Core.Prefixes;
using DeckWrench.Core.Proton;
using DeckWrench.Core.Steam;
using DeckWrench.Core.Storage;

namespace DeckWrench.Host.Toolbox;

/// <summary>
/// Builds a <see cref="ToolboxState"/>: reads Steam's files (never runs Steam), Heroic and Lutris,
/// measures folders and works out what can be cleaned up. Safe to run off the main thread.
/// </summary>
public sealed class ToolboxScanner
{
    private readonly AppPaths _paths;
    private readonly PendingChanges _pending;
    private readonly PrefixBackups _backups;

    public ToolboxScanner(AppPaths paths, PendingChanges pending, PrefixBackups backups)
    {
        _paths = paths;
        _pending = pending;
        _backups = backups;
    }

    /// <summary>Steam's own tools: they show under Proton, not as games.</summary>
    public static bool IsSteamTool(InstalledGame g) =>
        g.Title.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
        g.Title.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
        g.Title.StartsWith("Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase) ||
        g.Title.StartsWith("Steamworks Shared", StringComparison.OrdinalIgnoreCase);

    public ToolboxState Scan(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var home = _paths.Home;
        var steam = new SteamScanner(home);
        var roots = steam.Roots();
        var (root, packaging) = roots.Count > 0 ? roots[0] : (null!, "native");
        var libraries = root != null ? SteamScanner.Libraries(root) : [];
        progress?.Report(0.05);

        var installs = new List<InstalledGame>();
        try
        {
            installs.AddRange(steam.Scan(ct));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            AppLog.Warn("steam scan: " + e.Message);
        }
        try
        {
            installs.AddRange(new HeroicScanner(home).Scan());
            if (!Platform.IsWindows)
            {
                installs.AddRange(new LutrisScanner(home).Scan());
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            AppLog.Warn("launcher scan: " + e.Message);
        }
        ct.ThrowIfCancellationRequested();
        progress?.Report(0.15);

        // Who owns each app id: installed Steam apps (games and tools) and non-Steam shortcuts.
        var shortcuts = Platform.IsWindows ? new Dictionary<string, string>() : ShortcutNames.All(home);
        var owners = new Dictionary<string, string>(shortcuts);
        foreach (var g in installs.Where(i => i.Source == GameSource.Steam))
        {
            owners[g.SourceId] = g.Title;
        }
        var shortcutIds = shortcuts.Keys.ToHashSet();

        var tools = root != null ? CompatToolScanner.Scan(root, libraries) : [];
        tools = tools.Select(t => t with { Size = DiskSize.Of(t.Dir, ct) }).ToList();
        progress?.Report(0.3);

        var prefixes = Platform.IsWindows ? [] : AppFolders.Scan(AppFolderKind.Prefix, libraries, owners, shortcutIds, measure: true, ct);
        progress?.Report(0.6);
        var shaders = AppFolders.Scan(AppFolderKind.ShaderCache, libraries, owners, shortcutIds, measure: true, ct);
        progress?.Report(0.75);

        var config = root != null ? new SteamConfig(root) : null;
        var mapping = config?.CompatToolMapping() ?? [];
        var logs = Platform.IsWindows ? [] : ProtonLogs.Scan(LogDirs(installs), owners);
        var pending = _pending.All;
        var mounts = Platform.IsWindows ? null : Drives.Mounts();
        var driveCache = new Dictionary<string, Drive?>(Platform.PathComparer);
        Drive? DriveOf(string path)
        {
            if (!driveCache.TryGetValue(path, out var d))
            {
                d = Drives.For(path, mounts);
                driveCache[path] = d;
            }
            return d;
        }

        var games = new List<GameEntry>();
        foreach (var g in installs)
        {
            ct.ThrowIfCancellationRequested();
            if (g.Source == GameSource.Steam && IsSteamTool(g))
            {
                continue;
            }
            AppFolder? prefix = null;
            if (g.Source == GameSource.Steam)
            {
                prefix = prefixes.FirstOrDefault(p => p.AppId == g.SourceId);
            }
            else if (g.Prefix is { } pfx && Directory.Exists(pfx))
            {
                // Heroic's Proton prefixes keep drive_c in pfx/; Wine prefixes (Lutris) are the prefix itself.
                var dir = Path.GetFileName(pfx) == "pfx" ? Path.GetDirectoryName(pfx)! : pfx;
                prefix = new AppFolder
                {
                    Kind = AppFolderKind.Prefix,
                    AppId = g.SourceId,
                    Dir = dir,
                    PfxDir = pfx,
                    Library = Path.GetDirectoryName(dir)!,
                    Owner = g.Title,
                    External = true,
                    Size = DiskSize.Of(dir, ct),
                };
            }
            var size = g.SizeOnDisk > 0 ? g.SizeOnDisk : DiskSize.Of(g.InstallDir, ct);
            games.Add(new GameEntry
            {
                Install = g,
                CompatTool = g.Source == GameSource.Steam ? mapping.GetValueOrDefault(g.SourceId) : null,
                Prefix = prefix,
                ShaderCache = g.Source == GameSource.Steam ? shaders.FirstOrDefault(s => s.AppId == g.SourceId) : null,
                LaunchOptions = g.LaunchOptions ?? "",
                Log = g.Source == GameSource.Steam ? logs.FirstOrDefault(l => l.AppId == g.SourceId) : null,
                Size = size,
                Drive = DriveOf(g.InstallDir),
                Pending = g.Source == GameSource.Steam ? pending.Where(p => p.AppId == g.SourceId).ToList() : [],
            });
        }
        games = games.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
        progress?.Report(0.85);

        var backups = _backups.List();
        var state = new ToolboxState
        {
            ScannedAt = DateTime.UtcNow,
            SteamRoot = root,
            SteamPackaging = packaging,
            Libraries = libraries,
            Games = games,
            Tools = tools,
            DefaultTool = pending.LastOrDefault(p => p.Kind == PendingKind.CompatTool && p.AppId == "0") is { } pd ? pd.Value : mapping.GetValueOrDefault("0"),
            Prefixes = prefixes,
            ShaderCaches = shaders,
            Logs = logs,
            Backups = backups,
            Pending = pending,
        };
        state = state with
        {
            Drives = DriveReports(state, DriveOf),
            Cleanup = CleanupItems(state),
        };
        progress?.Report(1);
        AppLog.Info($"scan: {games.Count} games, {tools.Count} tools, {prefixes.Count} prefixes ({prefixes.Count(p => p.Orphan)} orphaned), " +
                    $"{shaders.Count} shader caches, {logs.Count} logs, reclaimable {DiskSize.Format(state.Reclaimable)}");
        return state;
    }

    /// <summary>The home folder (Proton's default) and any PROTON_LOG_DIR set in launch options.</summary>
    private IEnumerable<string> LogDirs(IEnumerable<InstalledGame> installs)
    {
        yield return _paths.Home;
        foreach (var g in installs)
        {
            if (Core.Launch.LaunchOptions.Parse(g.LaunchOptions).EnvValue("PROTON_LOG_DIR") is { Length: > 0 } dir)
            {
                yield return dir.Trim('"', '\'').Replace("~", _paths.Home);
            }
        }
    }

    private IReadOnlyList<DriveReport> DriveReports(ToolboxState s, Func<string, Drive?> driveOf)
    {
        var byMount = new Dictionary<string, (Drive Drive, List<string> Libs)>();
        foreach (var lib in s.Libraries)
        {
            if (driveOf(lib) is { } d)
            {
                if (!byMount.TryGetValue(d.Mount, out var entry))
                {
                    entry = (d, []);
                    byMount[d.Mount] = entry;
                }
                entry.Libs.Add(lib);
            }
        }
        var luma = LumaModsDir();
        var reports = new List<DriveReport>();
        foreach (var (mount, (drive, libs)) in byMount)
        {
            bool On(string? path) => path != null && driveOf(path)?.Mount == mount;
            var modsAndBackups = (On(luma) ? DiskSize.Of(luma!) : 0) + (On(_backups.Dir) ? s.Backups.Sum(b => b.ArchiveSize) : 0);
            reports.Add(new DriveReport
            {
                Drive = drive,
                Libraries = libs,
                Games = s.Games.Where(g => g.Drive?.Mount == mount).Sum(g => g.Size),
                Prefixes = s.Prefixes.Where(p => On(p.Dir)).Sum(p => Math.Max(0, p.Size)),
                ShaderCaches = s.ShaderCaches.Where(p => On(p.Dir)).Sum(p => Math.Max(0, p.Size)),
                ProtonVersions = s.Tools.Where(t => t.Kind != CompatToolKind.System && On(t.Dir)).Sum(t => Math.Max(0, t.Size)),
                ModsAndBackups = modsAndBackups,
            });
        }
        // Internal first, then SD card, then the rest.
        return reports.OrderBy(r => r.Drive.Label == "Internal" ? 0 : r.Drive.IsSdCard ? 1 : 2).ThenBy(r => r.Drive.Mount).ToList();
    }

    /// <summary>Luma Modman's mod store, counted under "Mods and backups" when it's installed.</summary>
    private string? LumaModsDir()
    {
        var dir = Platform.IsWindows
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Luma", "mods")
            : Path.Combine(Path.GetDirectoryName(_paths.Data)!, "luma", "mods");
        return Directory.Exists(dir) ? dir : null;
    }

    public static IReadOnlyList<CleanupItem> CleanupItems(ToolboxState s)
    {
        var items = new List<CleanupItem>();
        foreach (var p in s.Prefixes.Where(p => p.Orphan))
        {
            items.Add(new CleanupItem
            {
                Kind = CleanupKind.OrphanPrefix,
                Title = $"Prefix of app {p.AppId}",
                Detail = "No installed game or shortcut uses it any more.",
                Path = p.Dir,
                Size = Math.Max(0, p.Size),
                Caution = "Games without Steam Cloud keep their saves in the prefix. Back it up first if you might play again.",
            });
        }
        foreach (var c in s.ShaderCaches.Where(c => c.Orphan))
        {
            items.Add(new CleanupItem
            {
                Kind = CleanupKind.OrphanShaderCache,
                Title = $"Shader cache of app {c.AppId}",
                Detail = "Left behind by an uninstalled game.",
                Path = c.Dir,
                Size = Math.Max(0, c.Size),
            });
        }
        // Custom tools no game uses. The newest GE-Proton stays: it's what you'd pick next.
        var newestGe = s.Tools.Where(t => t.IsGe).OrderByDescending(t => ProtonNames.GeVersion(t.Name)).FirstOrDefault();
        foreach (var t in s.Tools.Where(t => t.Removable && t != newestGe && s.GamesUsing(t).Count == 0 &&
                                             !string.Equals(t.Name, s.DefaultTool, StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(new CleanupItem
            {
                Kind = CleanupKind.UnusedProton,
                Title = t.DisplayName,
                Detail = "No game is set to use it, and it isn't the default.",
                Path = t.Dir,
                Size = Math.Max(0, t.Size),
            });
        }
        foreach (var l in s.Logs)
        {
            items.Add(new CleanupItem
            {
                Kind = CleanupKind.ProtonLog,
                Title = $"Proton log: {l.Owner ?? "app " + l.AppId}",
                Detail = $"Last written {l.Modified.ToLocalTime():g}.",
                Path = l.Path,
                Size = l.Size,
            });
        }
        foreach (var b in s.Backups.Where(b => b.Created < DateTime.UtcNow.AddDays(-30)))
        {
            items.Add(new CleanupItem
            {
                Kind = CleanupKind.OldBackup,
                Title = $"Prefix backup: {b.Owner ?? "app " + b.AppId}",
                Detail = $"Made {b.Created.ToLocalTime():d} ({b.Reason}).",
                Path = b.Archive,
                Size = b.ArchiveSize,
            });
        }
        return items.OrderByDescending(i => i.Size).ToList();
    }
}
