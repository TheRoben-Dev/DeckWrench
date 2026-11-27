using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using DeckWrench.Core.Storage;

namespace DeckWrench.Core.Saves;

/// <summary>A save backup on disk: a .tar.gz of the files (by root name) with a .json next to it.</summary>
public sealed record SaveBackup
{
    /// <summary>"steam:1245620": the game it belongs to.</summary>
    public required string GameKey { get; init; }

    public string Title { get; init; } = "";
    public DateTime Created { get; init; }

    /// <summary>"manual", "all", "before-reset", "before-restore"...</summary>
    public string Reason { get; init; } = "manual";

    public int Files { get; init; }

    /// <summary>Total size of the saved files (uncompressed).</summary>
    public long Size { get; init; }

    /// <summary>Where each root was when the backup was made (for the record; a restore uses the game's current folders).</summary>
    public Dictionary<string, string> Roots { get; init; } = new();

    public string Archive { get; init; } = "";
    public long ArchiveSize { get; init; }
}

/// <summary>
/// Save backups: small archives of a game's save and config files, kept per game in the data
/// folder. A restore writes the files back into the game's current folders (so it still works after
/// the game moved to the SD card or its prefix was reset), after backing up what is there now.
/// </summary>
public sealed class SaveBackups
{
    public string Dir { get; }

    public SaveBackups(string dir) => Dir = dir;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Safe(string key) => string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    public IReadOnlyList<SaveBackup> List(string? gameKey = null)
    {
        if (!Directory.Exists(Dir))
        {
            return [];
        }
        var list = new List<SaveBackup>();
        foreach (var json in Directory.EnumerateFiles(Dir, "*.tar.gz.json"))
        {
            try
            {
                var b = JsonSerializer.Deserialize<SaveBackup>(File.ReadAllText(json), Json);
                var archive = json[..^5];
                if (b == null || !File.Exists(archive) || (gameKey != null && b.GameKey != gameKey))
                {
                    continue;
                }
                list.Add(b with { Archive = archive, ArchiveSize = new FileInfo(archive).Length });
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }
        return list.OrderByDescending(b => b.Created).ToList();
    }

    public SaveBackup Create(string gameKey, string title, SaveRoots roots, IReadOnlyList<SaveFile> files, string reason, CancellationToken ct = default)
    {
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"No save files found for {title}.");
        }
        Directory.CreateDirectory(Dir);
        var created = DateTime.UtcNow;
        var stem = $"{Safe(gameKey)}_{created.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        var archive = Path.Combine(Dir, stem + ".tar.gz");
        for (var n = 2; File.Exists(archive) || File.Exists(archive + ".part"); n++)
        {
            archive = Path.Combine(Dir, $"{stem}-{n}.tar.gz");
        }
        var tmp = archive + ".part";
        try
        {
            using (var file = File.Create(tmp))
            using (var gz = new GZipStream(file, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: false))
            {
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    tar.WriteEntry(f.Path, $"{f.Root}/{f.Relative}");
                }
            }
            File.Move(tmp, archive);
        }
        catch
        {
            try
            {
                File.Delete(tmp);
            }
            catch (IOException)
            {
            }
            throw;
        }
        var b = new SaveBackup
        {
            GameKey = gameKey,
            Title = title,
            Created = created,
            Reason = reason,
            Files = files.Count,
            Size = files.Sum(f => f.Size),
            Roots = roots.Named().Where(r => files.Any(f => f.Root == r.Name)).ToDictionary(r => r.Name, r => r.Dir),
        };
        File.WriteAllText(archive + ".json", JsonSerializer.Serialize(b, Json));
        return b with { Archive = archive, ArchiveSize = new FileInfo(archive).Length };
    }

    /// <summary>What a restore of <paramref name="b"/> would write: each file's destination and size, and whether it replaces one.</summary>
    public IReadOnlyList<(string Dest, long Size, bool Replaces)> Plan(SaveBackup b, SaveRoots roots)
    {
        var named = roots.Named().ToDictionary(r => r.Name, r => r.Dir);
        var list = new List<(string, long, bool)>();
        using var file = File.OpenRead(b.Archive);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } e)
        {
            if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || Dest(e.Name, named) is not { } dest)
            {
                continue;
            }
            list.Add((dest, e.Length, File.Exists(dest)));
        }
        return list;
    }

    /// <summary>The destination of an archive entry in the game's current folders; null when its root doesn't exist now (or it would leave its root).</summary>
    private static string? Dest(string entry, IReadOnlyDictionary<string, string> named)
    {
        var name = entry.Replace('\\', '/');
        var slash = name.IndexOf('/');
        if (slash <= 0 || !named.TryGetValue(name[..slash], out var root))
        {
            return null;
        }
        var full = Path.GetFullPath(Path.Combine(root, name[(slash + 1)..]));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>Writes the backup's files into the game's current folders, replacing what's there. Returns how many were written.</summary>
    public int Restore(SaveBackup b, SaveRoots roots, CancellationToken ct = default)
    {
        var named = roots.Named().ToDictionary(r => r.Name, r => r.Dir);
        var written = 0;
        using var file = File.OpenRead(b.Archive);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } e)
        {
            ct.ThrowIfCancellationRequested();
            if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || Dest(e.Name, named) is not { } dest)
            {
                continue;
            }
            var root = named[e.Name.Replace('\\', '/').Split('/')[0]];
            // Never write through a link that leads out of the root.
            for (var d = Path.GetDirectoryName(dest); d != null && d.Length > root.Length; d = Path.GetDirectoryName(d))
            {
                if (IsLink(d))
                {
                    throw new InvalidDataException($"{d} is a link; not writing saves through it.");
                }
            }
            if (IsLink(dest))
            {
                File.Delete(dest);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var tmp = dest + ".deckwrench-restore";
            e.ExtractToFile(tmp, overwrite: true);
            File.Move(tmp, dest, overwrite: true);
            written++;
        }
        return written;
    }

    /// <summary>A symlink (dangling too); false when nothing is there.</summary>
    private static bool IsLink(string p) => new FileInfo(p).LinkTarget != null;

    public void Delete(SaveBackup b)
    {
        File.Delete(b.Archive);
        try
        {
            File.Delete(b.Archive + ".json");
        }
        catch (IOException)
        {
        }
    }
}
