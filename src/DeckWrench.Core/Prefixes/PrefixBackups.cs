using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using DeckWrench.Core.Storage;

namespace DeckWrench.Core.Prefixes;

/// <summary>A saved copy of a prefix: a .tar.gz with a small .json describing it.</summary>
public sealed record PrefixBackup
{
    public required string AppId { get; init; }
    public string? Owner { get; init; }
    public DateTime Created { get; init; }

    /// <summary>The prefix folder it was taken from (where a restore puts it back).</summary>
    public string Source { get; init; } = "";

    /// <summary>Size of the prefix when it was backed up (uncompressed).</summary>
    public long PrefixSize { get; init; }

    /// <summary>Why it was taken: "manual", "before reset", "before delete".</summary>
    public string Reason { get; init; } = "manual";

    /// <summary>The archive (not serialized; set when listing).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Archive { get; init; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public long ArchiveSize { get; init; }
}

/// <summary>
/// Backs up, restores, resets and deletes Proton prefixes. A backup is a gzip'd tar of the whole
/// compatdata/&lt;appid&gt; folder with symbolic links kept as links (dosdevices, the Windows system
/// folders Proton links in), so a restore gives Steam back exactly what it had.
/// </summary>
public sealed class PrefixBackups
{
    public string Dir { get; }

    public PrefixBackups(string dir) => Dir = dir;

    public IReadOnlyList<PrefixBackup> List(string? appId = null)
    {
        if (!Directory.Exists(Dir))
        {
            return [];
        }
        var list = new List<PrefixBackup>();
        foreach (var meta in Directory.EnumerateFiles(Dir, "*.tar.gz.json"))
        {
            try
            {
                var b = JsonSerializer.Deserialize<PrefixBackup>(File.ReadAllText(meta));
                var archive = meta[..^".json".Length];
                if (b == null || !File.Exists(archive) || (appId != null && b.AppId != appId))
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

    /// <summary>Writes a backup of <paramref name="prefix"/>. Progress is 0..1 by bytes.</summary>
    public PrefixBackup Create(AppFolder prefix, string reason, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(prefix.Dir))
        {
            throw new DirectoryNotFoundException($"The prefix folder {prefix.Dir} is gone.");
        }
        Directory.CreateDirectory(Dir);
        var created = DateTime.UtcNow;
        var stem = $"{prefix.AppId}_{created.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        var archive = Path.Combine(Dir, stem + ".tar.gz");
        for (var n = 2; File.Exists(archive) || File.Exists(archive + ".part"); n++)
        {
            archive = Path.Combine(Dir, $"{stem}-{n}.tar.gz");  // two backups in the same second
        }
        var total = Math.Max(1, prefix.Size > 0 ? prefix.Size : DiskSize.Of(prefix.Dir, ct));
        var tmp = archive + ".part";
        try
        {
            long done = 0;
            using (var file = File.Create(tmp))
            using (var gz = new GZipStream(file, CompressionLevel.Fastest))
            using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: false))
            {
                foreach (var (path, entry) in Walk(prefix.Dir, ""))
                {
                    ct.ThrowIfCancellationRequested();
                    tar.WriteEntry(path, entry);
                    if (File.Exists(path) && !DiskSize.IsLink(path))
                    {
                        done += new FileInfo(path).Length;
                        progress?.Report(Math.Min(1, (double)done / total));
                    }
                }
            }
            File.Move(tmp, archive);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        var backup = new PrefixBackup
        {
            AppId = prefix.AppId,
            Owner = prefix.Owner,
            Created = created,
            Source = prefix.Dir,
            PrefixSize = total,
            Reason = reason,
        };
        File.WriteAllText(archive + ".json", JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
        return backup with { Archive = archive, ArchiveSize = new FileInfo(archive).Length };
    }

    /// <summary>Every file, folder and link under <paramref name="dir"/>, depth first; links are listed, never followed.</summary>
    private static IEnumerable<(string Path, string Entry)> Walk(string dir, string prefix)
    {
        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            var entry = prefix + info.Name;
            var link = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (info is DirectoryInfo && !link)
            {
                yield return (info.FullName, entry + "/");
                foreach (var x in Walk(info.FullName, entry + "/"))
                {
                    yield return x;
                }
            }
            else
            {
                yield return (info.FullName, entry);
            }
        }
    }

    /// <summary>
    /// Puts a backup back in place of the current prefix. The current one is moved aside first and
    /// only removed once the backup is fully unpacked, so a failed restore changes nothing.
    /// </summary>
    public void Restore(PrefixBackup backup, CancellationToken ct = default)
    {
        var target = backup.Source;
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".deckwrench-restore-{backup.AppId}-{Guid.NewGuid():N}");
        var aside = target + ".deckwrench-old";
        Directory.CreateDirectory(staging);
        try
        {
            using (var file = File.OpenRead(backup.Archive))
            using (var gz = new GZipStream(file, CompressionMode.Decompress))
            {
                Storage.SafeTar.Extract(gz, staging, ct);
            }
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(target))
            {
                Directory.Move(target, aside);
            }
            Directory.Move(staging, target);
        }
        catch
        {
            TryDeleteDir(staging);
            if (!Directory.Exists(target) && Directory.Exists(aside))
            {
                Directory.Move(aside, target);
            }
            throw;
        }
        TryDeleteDir(aside);
    }

    public void Delete(PrefixBackup backup)
    {
        TryDelete(backup.Archive + ".json");
        File.Delete(backup.Archive);
    }

    private static void TryDelete(string f)
    {
        try
        {
            File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static void TryDeleteDir(string d)
    {
        try
        {
            if (Directory.Exists(d))
            {
                DeleteTree(d);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes a folder tree without following symbolic links (links are removed, not their targets).</summary>
    public static void DeleteTree(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            info.Delete();  // a link: remove the link only
            return;
        }
        foreach (var e in info.EnumerateFileSystemInfos())
        {
            if (e is DirectoryInfo d && !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                DeleteTree(d.FullName);
            }
            else
            {
                if (e.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    e.Attributes &= ~FileAttributes.ReadOnly;
                }
                e.Delete();
            }
        }
        info.Delete();
    }
}
