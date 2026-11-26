using DeckWrench.Core.Storage;
using Xunit;

namespace DeckWrench.Core.Tests;

public sealed class SizeCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deckwrench-sizes-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string Tree()
    {
        var root = Path.Combine(_dir, "prefix");
        Directory.CreateDirectory(Path.Combine(root, "pfx", "drive_c", "windows"));
        File.WriteAllBytes(Path.Combine(root, "pfx", "drive_c", "windows", "a.dll"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(root, "version"), new byte[10]);
        return root;
    }

    [Fact]
    public void UnchangedFoldersComeFromTheCacheAndChangedOnesAreMeasured()
    {
        var root = Tree();
        var file = Path.Combine(_dir, "cache", "sizes.json");
        var now = DateTime.UtcNow;
        var cache = new SizeCache(file, () => now);
        Assert.Equal(1010, cache.Of(root));
        Assert.Equal(1010, cache.Of(root));
        Assert.Equal((1, 1), (cache.Hits, cache.Misses));
        cache.Save();

        // A new file deep inside changes that directory's time: measured again.
        var deep = Path.Combine(root, "pfx", "drive_c", "windows");
        File.WriteAllBytes(Path.Combine(deep, "b.dll"), new byte[500]);
        Directory.SetLastWriteTimeUtc(deep, now.AddMinutes(1));
        var reloaded = new SizeCache(file, () => now);
        Assert.Equal(1510, reloaded.Of(root));
        Assert.Equal(1, reloaded.Misses);

        // A new subfolder with an old time still changes the directory count.
        var sub = Directory.CreateDirectory(Path.Combine(root, "pfx", "new")).FullName;
        Directory.SetLastWriteTimeUtc(sub, now.AddYears(-1));
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "pfx"), now.AddMinutes(1));
        Assert.Equal(1510, reloaded.Of(root));
        Assert.Equal(2, reloaded.Misses);
    }

    [Fact]
    public void EntriesExpireAndScanAgainForgetsThem()
    {
        var root = Tree();
        var now = DateTime.UtcNow;
        var cache = new SizeCache(null, () => now);
        cache.Of(root);
        // A file growing in place isn't visible in directory times: the cache answers until it expires.
        File.WriteAllBytes(Path.Combine(root, "version"), new byte[90]);
        Directory.SetLastWriteTimeUtc(root, Directory.GetLastWriteTimeUtc(root));
        var signature = SizeCache.Signature(root);
        Assert.Equal(signature, SizeCache.Signature(root));
        Assert.Equal(1010, cache.Of(root));
        now += SizeCache.MaxAge;
        Assert.Equal(1090, cache.Of(root));

        File.WriteAllBytes(Path.Combine(root, "version"), new byte[20]);
        cache.Forget();
        Assert.Equal(1020, cache.Of(root));
    }

    [Fact]
    public void SaveDropsFoldersThatWerentSeen()
    {
        var a = Tree();
        var b = Directory.CreateDirectory(Path.Combine(_dir, "other")).FullName;
        File.WriteAllBytes(Path.Combine(b, "x"), new byte[5]);
        var file = Path.Combine(_dir, "sizes.json");
        var cache = new SizeCache(file);
        cache.Of(a);
        cache.Of(b);
        cache.Save();
        cache.Of(a);  // b is gone from this scan
        cache.Save();
        var reloaded = new SizeCache(file);
        reloaded.Of(a);
        reloaded.Of(b);
        Assert.Equal((1, 1), (reloaded.Hits, reloaded.Misses));
        Assert.Equal(5, new SizeCache(file).Of(Path.Combine(b, "x")));  // files are just their length
    }
}
