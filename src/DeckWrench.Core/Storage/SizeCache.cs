using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text.Json;

namespace DeckWrench.Core.Storage;

/// <summary>
/// Remembers folder sizes between scans (sizes.json in the cache folder), so a rescan only walks
/// the files of folders that changed. A folder counts as unchanged while its directory tree has
/// the same number of directories and the same newest directory time: adding, removing or
/// renaming anything inside changes that, and only those directories are stat'ed, not every
/// file. Files that grow in place (a shader cache while a game runs) don't touch directory
/// times, so entries expire after <see cref="MaxAge"/>, and "Scan again" forgets them all.
/// </summary>
public sealed class SizeCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    private sealed record Entry(long Size, long Stamp, int Dirs, DateTime Measured);

    private readonly string? _file;
    private readonly Func<DateTime> _now;
    private ConcurrentDictionary<string, Entry>? _entries;
    private readonly ConcurrentDictionary<string, bool> _used = new(Platform.PathComparer);

    public SizeCache(string? file, Func<DateTime>? now = null)
    {
        _file = file;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Folders answered from the cache / walked, since the last <see cref="Save"/>.</summary>
    public int Hits { get; private set; }

    public int Misses { get; private set; }

    /// <summary>The size of a folder (or file), from the cache when nothing in its tree changed.</summary>
    public long Of(string path, CancellationToken ct = default)
    {
        if (File.Exists(path) || !Directory.Exists(path) || DiskSize.IsLink(path))
        {
            return DiskSize.Of(path, ct);
        }
        var entries = Load();
        var (stamp, dirs) = Signature(path, ct);
        _used[path] = true;
        if (entries.TryGetValue(path, out var e) && e.Stamp == stamp && e.Dirs == dirs && _now() - e.Measured < MaxAge)
        {
            Hits++;
            return e.Size;
        }
        var size = DiskSize.Of(path, ct);
        entries[path] = new Entry(size, stamp, dirs, _now());
        Misses++;
        return size;
    }

    /// <summary>Drops every remembered size: the next scan measures everything.</summary>
    public void Forget()
    {
        Load().Clear();
        _used.Clear();
    }

    /// <summary>Writes the sizes used since the last save (folders not seen any more are dropped).</summary>
    public void Save()
    {
        var entries = Load();
        foreach (var k in entries.Keys.Where(k => !_used.ContainsKey(k)).ToList())
        {
            entries.TryRemove(k, out _);
        }
        _used.Clear();
        Hits = Misses = 0;
        if (_file == null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries.ToDictionary(kv => kv.Key, kv => kv.Value)));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Only a speed-up: a cache that can't be written just means a full measure next time.
        }
    }

    /// <summary>(newest directory write time, number of directories) for the tree, without stat'ing files.</summary>
    public static (long Stamp, int Dirs) Signature(string dir, CancellationToken ct = default)
    {
        long newest;
        try
        {
            newest = Directory.GetLastWriteTimeUtc(dir).Ticks;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
        var count = 1;
        try
        {
            var walk = new FileSystemEnumerable<long>(dir, (ref FileSystemEntry e) => e.LastWriteTimeUtc.UtcTicks,
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => e.IsDirectory,
            };
            foreach (var t in walk)
            {
                ct.ThrowIfCancellationRequested();
                count++;
                newest = Math.Max(newest, t);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
        return (newest, count);
    }

    private ConcurrentDictionary<string, Entry> Load()
    {
        if (_entries != null)
        {
            return _entries;
        }
        var loaded = new ConcurrentDictionary<string, Entry>(Platform.PathComparer);
        try
        {
            if (_file != null && File.Exists(_file) && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file)) is { } d)
            {
                foreach (var (k, v) in d)
                {
                    loaded[k] = v;
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
        return _entries = loaded;
    }
}
