using Xunit;

namespace DeckWrench.Core.Tests;

public class TuningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deckwrench-tuning-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private void Put(string path, string text)
    {
        var p = Path.Combine(_root, path.TrimStart('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private string Get(string path) => File.ReadAllText(Path.Combine(_root, path.TrimStart('/'))).Trim();

    private Tuning SteamOsStock()
    {
        Put("/proc/sys/vm/swappiness", "100\n");
        Put("/sys/kernel/mm/transparent_hugepage/enabled", "always [madvise] never\n");
        Put("/sys/kernel/mm/transparent_hugepage/shmem_enabled", "always within_size advise [never] deny force\n");
        Put("/proc/sys/vm/compaction_proactiveness", "20\n");
        Put("/proc/sys/vm/page_lock_unfairness", "5\n");
        Put("/proc/swaps", "Filename\tType\tSize\tUsed\tPriority\n/home/swapfile                          file\t\t1048572\t\t0\t\t-2\n");
        Put("/home/swapfile", "");
        return new Tuning(_root);
    }

    [LinuxFact]
    public void ReadsStockValuesAndSwap()
    {
        var t = SteamOsStock();
        var s = t.Read();
        Assert.Equal("madvise", TuningSetting.Normalize(s.Single(x => x.Id == "thp").Current!));
        Assert.DoesNotContain(s, x => x.IsRecommended);
        var swap = t.Swap();
        Assert.Equal("/home/swapfile", swap.File);
        Assert.Equal(1048572L * 1024, swap.Size);
        Assert.True(swap.CanResize);
        Assert.False(t.Applied);
    }

    [LinuxFact]
    public void ApplyThenRevertPutsEverythingBack()
    {
        var t = SteamOsStock();
        var ran = new List<string>();
        var log = t.Apply(null, c => ran.Add(string.Join(' ', c)));
        Assert.Equal(5, log.Count);
        Assert.Empty(ran);   // no swap change asked for
        Assert.Equal("1", Get("/proc/sys/vm/swappiness"));
        Assert.Equal("always", Get("/sys/kernel/mm/transparent_hugepage/enabled"));
        Assert.Contains("vm.swappiness = 1", Get(Tuning.SysctlFile));
        Assert.Contains("w /sys/kernel/mm/transparent_hugepage/enabled - - - - always", Get(Tuning.TmpfilesFile));
        Assert.True(t.Applied);
        Assert.All(t.Read(), x => Assert.True(x.IsRecommended, x.Id));

        // A second apply doesn't overwrite the saved stock values with tuned ones.
        t.Apply(null, _ => { });
        t.Revert(_ => { });
        Assert.Equal("100", Get("/proc/sys/vm/swappiness"));
        Assert.Equal("madvise", Get("/sys/kernel/mm/transparent_hugepage/enabled"));
        Assert.Equal("20", Get("/proc/sys/vm/compaction_proactiveness"));
        Assert.False(t.Applied);
        Assert.False(File.Exists(Path.Combine(_root, Tuning.StockFile.TrimStart('/'))));
    }

    [LinuxFact]
    public void OnlySteamOsSwapFileIsResizedAndChoicesLeaveRoom()
    {
        var steamos = new SwapInfo("/home/swapfile", 1L << 30, 40L << 30, true);
        Assert.Equal([4L << 30, 8L << 30, 16L << 30], Tuning.SwapChoices(steamos));
        Assert.Equal([4L << 30, 8L << 30], Tuning.SwapChoices(steamos with { FreeOnDisk = 20L << 30 }));
        Assert.Empty(Tuning.SwapChoices(new SwapInfo("/var/swap/swapfile", 8L << 30, 100L << 30, false)));
        Assert.Equal(16L << 30, DeckWrench.Host.Toolbox.Actions.SwapTarget(steamos));
        Assert.Null(DeckWrench.Host.Toolbox.Actions.SwapTarget(steamos with { Size = 16L << 30 }));
    }
}
