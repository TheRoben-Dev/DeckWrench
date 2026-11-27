using DeckWrench.Core;
using DeckWrench.Core.Saves;

namespace DeckWrench.Host.Toolbox;

/// <summary>
/// Finds and backs up games' saves: Ludusavi's manifest (downloaded weekly into the cache folder,
/// read only for installed Steam games) plus Steam's userdata, and the prefix's game folders for
/// games the manifest doesn't know.
/// </summary>
public sealed class SaveService
{
    private readonly AppHost _host;
    private readonly object _lock = new();
    private (DateTime Stamp, string Ids, IReadOnlyDictionary<string, ManifestGame> Games)? _read;

    public SaveService(AppHost host)
    {
        _host = host;
        Backups = new SaveBackups(Path.Combine(host.Paths.Data, "backups", "saves"));
    }

    public SaveBackups Backups { get; }

    public string ManifestFile => Path.Combine(_host.Paths.Cache, "ludusavi-manifest.yaml");

    /// <summary>The manifest entries of the installed Steam games (downloads or refreshes the manifest when needed). Empty when offline on first use.</summary>
    public async Task<IReadOnlyDictionary<string, ManifestGame>> Manifest(CancellationToken ct = default)
    {
        if (!await SaveManifest.Ensure(ManifestFile, _host.Http, ct).ConfigureAwait(false))
        {
            return new Dictionary<string, ManifestGame>();
        }
        var ids = _host.State.Games.Where(g => g.IsSteam).Select(g => g.AppId).ToHashSet();
        var key = string.Join(",", ids.Order(StringComparer.Ordinal));
        var stamp = File.GetLastWriteTimeUtc(ManifestFile);
        lock (_lock)
        {
            if (_read is { } r && r.Stamp == stamp && r.Ids == key)
            {
                return r.Games;
            }
        }
        IReadOnlyDictionary<string, ManifestGame> games;
        using (var reader = new StreamReader(ManifestFile))
        {
            games = SaveManifest.Read(reader, ids, ct);
        }
        lock (_lock)
        {
            _read = (stamp, key, games);
        }
        return games;
    }

    public SaveRoots RootsFor(GameEntry g)
    {
        string? winHome = null, driveC = null;
        if (g.Prefix is { } p && !Platform.IsWindows)
        {
            driveC = Path.Combine(p.PfxDir, "drive_c");
            var user = Path.Combine(driveC, "users", "steamuser");
            if (Directory.Exists(user))
            {
                winHome = user;
            }
        }
        return new SaveRoots
        {
            WinHome = winHome,
            DriveC = winHome != null ? driveC : null,
            Install = Directory.Exists(g.Install.InstallDir) ? g.Install.InstallDir : null,
            Library = g.Install.Library,
            Home = _host.Paths.Home,
            SteamRoot = g.RunsInSteam ? _host.State.SteamRoot : null,
            AppId = g.IsSteam ? g.AppId : null,
        };
    }

    public async Task<IReadOnlyList<SaveFile>> Find(GameEntry g, CancellationToken ct = default)
    {
        var manifest = g.IsSteam ? await Manifest(ct).ConfigureAwait(false) : new Dictionary<string, ManifestGame>();
        return SaveLocator.Find(RootsFor(g), manifest.GetValueOrDefault(g.AppId), ct);
    }

    /// <summary>Backs up a game's saves; null when none were found.</summary>
    public async Task<SaveBackup?> Backup(GameEntry g, string reason, CancellationToken ct = default)
    {
        var files = await Find(g, ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            return null;
        }
        var b = Backups.Create(g.Key, g.Title, RootsFor(g), files, reason, ct);
        AppLog.Info($"saves of {g.Title}: {b.Files} files → {b.Archive}");
        return b;
    }

    /// <summary>Best effort before something risky (a prefix reset or delete, an uninstall): never stops it.</summary>
    public void TryBackup(GameEntry? g, string reason)
    {
        if (g == null)
        {
            return;
        }
        try
        {
            // Offline or slow: don't hold up the action for a manifest download; use what's on disk.
            var manifest = File.Exists(ManifestFile) ? Manifest().GetAwaiter().GetResult() : new Dictionary<string, ManifestGame>();
            var files = SaveLocator.Find(RootsFor(g), g.IsSteam ? manifest.GetValueOrDefault(g.AppId) : null);
            if (files.Count > 0)
            {
                Backups.Create(g.Key, g.Title, RootsFor(g), files, reason);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            AppLog.Error($"save backup of {g.Title} ({reason}): {e.Message}");
        }
    }
}
