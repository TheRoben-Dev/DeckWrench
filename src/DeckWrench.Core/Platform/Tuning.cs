using System.Globalization;
using System.Text.Json;

namespace DeckWrench.Core;

/// <summary>One kernel setting DeckWrench can tune: where it lives, what it is now, stock and recommended.</summary>
public sealed record TuningSetting(string Id, string Label, string Path, string? Current, string Recommended, string Why)
{
    public bool IsRecommended => Current != null && Normalize(Current) == Recommended;

    /// <summary>"always [madvise] never" → "madvise" (the kernel marks the active choice in brackets).</summary>
    public static string Normalize(string raw)
    {
        var t = raw.Trim();
        var open = t.IndexOf('[');
        var close = t.IndexOf(']');
        return open >= 0 && close > open ? t[(open + 1)..close] : t;
    }
}

/// <summary>The swap file and how big it is.</summary>
public sealed record SwapInfo(string? File, long Size, long FreeOnDisk, bool CanResize);

/// <summary>
/// Memory tuning for the Deck, the same changes CryoUtilities made popular: a bigger swap file,
/// a low swappiness (swap only when RAM is really full), transparent huge pages, no proactive
/// compaction and fair page locking. Applied as root through `deckwrench tune` and kept over
/// reboots with a sysctl.d and a tmpfiles.d file; Revert removes both and puts back what was
/// there before (saved on the first apply).
/// </summary>
public sealed class Tuning
{
    /// <summary>"/" normally; a temp folder in tests.</summary>
    public string Root { get; }

    public Tuning(string root = "/") => Root = root;

    private string P(string path) => System.IO.Path.Combine(Root, path.TrimStart('/'));

    public const string SysctlFile = "/etc/sysctl.d/zzz-deckwrench.conf";
    public const string TmpfilesFile = "/etc/tmpfiles.d/zzz-deckwrench.conf";
    public const string StockFile = "/etc/deckwrench-tuning-stock.json";
    public const string SwapFile = "/home/swapfile";

    private static readonly (string Id, string Label, string Path, string Recommended, string Why)[] Settings =
    [
        ("swappiness", "Swappiness", "/proc/sys/vm/swappiness", "1",
            "SteamOS swaps early (100). 1 keeps games in RAM and only swaps when memory is really full: fewer stutters."),
        ("thp", "Transparent huge pages", "/sys/kernel/mm/transparent_hugepage/enabled", "always",
            "Bigger memory pages: less CPU time spent on memory bookkeeping in big games."),
        ("thp-shmem", "Huge pages for shared memory", "/sys/kernel/mm/transparent_hugepage/shmem_enabled", "advise",
            "Lets the GPU driver and Proton ask for huge pages for shared memory."),
        ("compaction", "Proactive compaction", "/proc/sys/vm/compaction_proactiveness", "0",
            "Stops the kernel from shuffling memory in the background while you play (a source of hitches)."),
        ("page-lock", "Page lock unfairness", "/proc/sys/vm/page_lock_unfairness", "1",
            "Less waiting on memory locks under load."),
    ];

    public IReadOnlyList<TuningSetting> Read() =>
        Settings.Select(s => new TuningSetting(s.Id, s.Label, s.Path, ReadValue(s.Path), s.Recommended, s.Why)).ToList();

    private string? ReadValue(string path)
    {
        try
        {
            return File.Exists(P(path)) ? File.ReadAllText(P(path)).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when DeckWrench's tuning files are in place.</summary>
    public bool Applied => File.Exists(P(SysctlFile)) || File.Exists(P(TmpfilesFile));

    /// <summary>The swap file from /proc/swaps (the first file, not a zram device) and the free space next to it.</summary>
    public SwapInfo Swap()
    {
        string? file = null;
        long size = 0;
        try
        {
            if (File.Exists(P("/proc/swaps")))
            {
                foreach (var line in File.ReadLines(P("/proc/swaps")).Skip(1))
                {
                    var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length >= 3 && f[1] == "file")
                    {
                        file = f[0];
                        size = long.Parse(f[2], CultureInfo.InvariantCulture) * 1024;  // KiB
                        break;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
        {
        }
        file ??= File.Exists(P(SwapFile)) ? SwapFile : null;
        if (file != null && size == 0 && File.Exists(P(file)))
        {
            size = new FileInfo(P(file)).Length;
        }
        long free = 0;
        try
        {
            free = new DriveInfo(P("/home")).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }
        // Only SteamOS's own /home/swapfile is resized; other setups (zram, btrfs) are left alone.
        return new SwapInfo(file, size, free, file == SwapFile);
    }

    /// <summary>The sysctl.d file that keeps swappiness and the vm settings over reboots.</summary>
    public static string SysctlContents() => string.Join("\n",
    [
        "# Written by DeckWrench (Home → Tune memory). Remove with Revert, or delete this file.",
        .. Settings.Where(s => s.Path.StartsWith("/proc/sys/", StringComparison.Ordinal))
            .Select(s => $"{s.Path["/proc/sys/".Length..].Replace('/', '.')} = {s.Recommended}"),
        "",
    ]);

    /// <summary>The tmpfiles.d file that sets the /sys values at every boot.</summary>
    public static string TmpfilesContents() => string.Join("\n",
    [
        "# Written by DeckWrench (Home → Tune memory). Remove with Revert, or delete this file.",
        .. Settings.Where(s => s.Path.StartsWith("/sys/", StringComparison.Ordinal)).Select(s => $"w {s.Path} - - - - {s.Recommended}"),
        "",
    ]);

    /// <summary>Applies the recommended values now and for later boots. Run as root. Returns what changed.</summary>
    public IReadOnlyList<string> Apply(long? swapBytes, Action<string[]> run)
    {
        var log = new List<string>();
        var current = Read();
        if (!File.Exists(P(StockFile)))
        {
            // What was there before the first apply, so Revert can put it back exactly.
            var stock = new Dictionary<string, string>();
            foreach (var s in current.Where(s => s.Current != null))
            {
                stock[s.Path] = TuningSetting.Normalize(s.Current!);
            }
            var swap = Swap();
            if (swap.CanResize)
            {
                stock["swapfile-size"] = swap.Size.ToString(CultureInfo.InvariantCulture);
            }
            Write(StockFile, JsonSerializer.Serialize(stock, new JsonSerializerOptions { WriteIndented = true }));
        }
        Write(SysctlFile, SysctlContents());
        Write(TmpfilesFile, TmpfilesContents());
        foreach (var s in current)
        {
            if (s.Current == null || s.IsRecommended)
            {
                continue;
            }
            WriteValue(s.Path, s.Recommended);
            log.Add($"{s.Label}: {TuningSetting.Normalize(s.Current)} → {s.Recommended}");
        }
        if (swapBytes is { } bytes)
        {
            log.AddRange(ResizeSwap(bytes, run));
        }
        return log;
    }

    /// <summary>Puts back what was there before the first apply and removes DeckWrench's files. Run as root.</summary>
    public IReadOnlyList<string> Revert(Action<string[]> run)
    {
        var log = new List<string>();
        Dictionary<string, string> stock = new();
        try
        {
            if (File.Exists(P(StockFile)))
            {
                stock = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(P(StockFile))) ?? new();
            }
        }
        catch (JsonException)
        {
        }
        foreach (var s in Read())
        {
            if (stock.TryGetValue(s.Path, out var v) && s.Current != null && TuningSetting.Normalize(s.Current) != v)
            {
                WriteValue(s.Path, v);
                log.Add($"{s.Label}: {TuningSetting.Normalize(s.Current)} → {v}");
            }
        }
        if (stock.TryGetValue("swapfile-size", out var size) && long.TryParse(size, CultureInfo.InvariantCulture, out var bytes) && bytes > 0 && Swap() is { CanResize: true } swap && swap.Size != bytes)
        {
            log.AddRange(ResizeSwap(bytes, run));
        }
        foreach (var f in new[] { SysctlFile, TmpfilesFile, StockFile })
        {
            if (File.Exists(P(f)))
            {
                File.Delete(P(f));
                log.Add("removed " + f);
            }
        }
        return log;
    }

    /// <summary>Swap file sizes offered: those that leave at least 10 GB free on /home.</summary>
    public static IReadOnlyList<long> SwapChoices(SwapInfo swap)
    {
        long[] gb = [4, 8, 16];
        return gb.Select(g => g << 30).Where(b => swap.CanResize && b - swap.Size < swap.FreeOnDisk - (10L << 30)).ToList();
    }

    private IEnumerable<string> ResizeSwap(long bytes, Action<string[]> run)
    {
        var swap = Swap();
        if (!swap.CanResize)
        {
            yield return "swap file: left alone (not SteamOS's /home/swapfile)";
            yield break;
        }
        if (bytes - swap.Size > swap.FreeOnDisk - (10L << 30))
        {
            throw new InvalidOperationException($"Not enough free space on /home for a {bytes >> 30} GB swap file.");
        }
        var mb = (bytes >> 20).ToString(CultureInfo.InvariantCulture);
        run(["swapoff", SwapFile]);
        run(["rm", "-f", SwapFile]);
        // dd, not fallocate: swap files must not be sparse, and SteamOS's own setup uses dd too.
        run(["dd", "if=/dev/zero", "of=" + SwapFile, "bs=1M", "count=" + mb, "status=none"]);
        run(["chmod", "600", SwapFile]);
        run(["mkswap", SwapFile]);
        run(["swapon", SwapFile]);
        yield return $"swap file: {swap.Size >> 20} MB → {mb} MB";
    }

    private void Write(string path, string text)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(P(path))!);
        File.WriteAllText(P(path), text);
    }

    private void WriteValue(string path, string value)
    {
        try
        {
            File.WriteAllText(P(path), value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Couldn't write {path}: {e.Message}", e);
        }
    }
}
