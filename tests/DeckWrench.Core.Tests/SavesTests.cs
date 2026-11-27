using DeckWrench.Core.Saves;
using Xunit;

namespace DeckWrench.Core.Tests;

public class SavesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deckwrench-saves-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    // The manifest's shape: a top-level map of games, with files, when-conditions and steam ids.
    internal const string Manifest = """
        Some Other Game:
          files:
            "<base>/save.dat":
              tags:
                - save
          steam:
            id: 999
        Elden Ring:
          cloud:
            steam: true
          files:
            "<winAppData>/EldenRing/<storeUserId>":
              tags:
                - save
              when:
                - os: windows
            "<winAppData>/EldenRing/GraphicsConfig.xml":
              tags:
                - config
              when:
                - os: windows
            "<home>/Library/Application Support/EldenRing":
              when:
                - os: mac
          id:
            steamExtra:
              - 2778580
          installDir:
            ELDEN RING: {}
          steam:
            id: 1245620
        Registry Only:
          registry:
            "HKEY_CURRENT_USER/Software/X": {}
          steam:
            id: 5
        """;

    [LinuxFact]
    public void ReadsOnlyTheGamesAskedFor()
    {
        var games = SaveManifest.Read(new StringReader(Manifest), new HashSet<string> { "1245620", "5" });
        var er = Assert.Single(games).Value;
        Assert.Equal("Elden Ring", er.Name);
        Assert.Equal(3, er.Rules.Count);
        Assert.Equal(2, er.Rules.Count(r => r.AppliesTo("windows")));
        Assert.DoesNotContain(er.Rules, r => r.AppliesTo("linux"));
        Assert.Equal("Elden Ring", SaveManifest.Read(new StringReader(Manifest), new HashSet<string> { "2778580" })["2778580"].Name);
    }

    private SaveRoots Roots(out string winHome)
    {
        var home = Path.Combine(_dir, "home");
        var pfx = Path.Combine(home, "Steam", "steamapps", "compatdata", "1245620", "pfx");
        winHome = Path.Combine(pfx, "drive_c", "users", "steamuser");
        Directory.CreateDirectory(winHome);
        return new SaveRoots
        {
            WinHome = winHome,
            DriveC = Path.Combine(pfx, "drive_c"),
            Install = Path.Combine(home, "Steam", "steamapps", "common", "ELDEN RING"),
            Library = Path.Combine(home, "Steam"),
            Home = home,
            SteamRoot = Path.Combine(home, "Steam"),
            AppId = "1245620",
        };
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [LinuxFact]
    public void ExpandsPlaceholdersForProtonGames()
    {
        var r = Roots(out var winHome);
        Assert.Equal(Path.Combine(winHome, "AppData", "Roaming") + "/EldenRing/*", SaveLocator.Expand("<winAppData>/EldenRing/<storeUserId>", r));
        Assert.Equal(r.Install + "/save", SaveLocator.Expand("<base>/save", r));
        Assert.Null(SaveLocator.Expand("<xdgData>/x", r));      // a Linux path for a Proton game
        Assert.Null(SaveLocator.Expand("<regHkcu>/x", r));      // unknown placeholder
        var native = r with { WinHome = null, DriveC = null };
        Assert.Equal(Path.Combine(r.Home, ".local", "share") + "/x", SaveLocator.Expand("<xdgData>/x", native));
    }

    [LinuxFact]
    public void FindsManifestSavesSteamUserdataAndGlobsCaseInsensitively()
    {
        var r = Roots(out var winHome);
        Write(Path.Combine(winHome, "AppData", "Roaming", "EldenRing", "7656", "ER0000.sl2"), "slot");
        Write(Path.Combine(winHome, "AppData", "Roaming", "EldenRing", "graphicsconfig.xml"), "cfg");   // other case
        Write(Path.Combine(winHome, "AppData", "Roaming", "Other", "x.sav"), "not this game");
        Write(Path.Combine(r.SteamRoot!, "userdata", "123", "1245620", "remote", "ER0000.sl2"), "cloud");
        Write(Path.Combine(r.SteamRoot!, "userdata", "123", "999", "remote", "other.sav"), "other game");
        var manifest = SaveManifest.Read(new StringReader(Manifest), new HashSet<string> { "1245620" })["1245620"];

        var files = SaveLocator.Find(r, manifest);
        Assert.Equal(["userdata:123/1245620/remote/ER0000.sl2", "winhome:AppData/Roaming/EldenRing/7656/ER0000.sl2", "winhome:AppData/Roaming/EldenRing/graphicsconfig.xml"],
            files.Select(f => $"{f.Root}:{f.Relative}").Order(StringComparer.Ordinal));
    }

    [LinuxFact]
    public void WithoutAManifestEntryThePrefixGameFoldersAreUsed()
    {
        var r = Roots(out var winHome);
        Write(Path.Combine(winHome, "Documents", "My Games", "Studio", "slot1.sav"), "s");
        Write(Path.Combine(winHome, "AppData", "Local", "Microsoft", "junk"), "skip");
        var files = SaveLocator.Find(r, null);
        Assert.Equal(["winhome:Documents/My Games/Studio/slot1.sav"], files.Select(f => $"{f.Root}:{f.Relative}"));
    }

    [LinuxFact]
    public void BackupAndRestoreIntoTheCurrentFolders()
    {
        var r = Roots(out var winHome);
        var save = Path.Combine(winHome, "AppData", "Roaming", "EldenRing", "7656", "ER0000.sl2");
        Write(save, "good save");
        var manifest = SaveManifest.Read(new StringReader(Manifest), new HashSet<string> { "1245620" })["1245620"];
        var store = new SaveBackups(Path.Combine(_dir, "backups"));
        var b = store.Create("steam:1245620", "ELDEN RING", r, SaveLocator.Find(r, manifest), "manual");
        Assert.Equal(1, b.Files);
        Assert.Single(store.List("steam:1245620"));
        Assert.Empty(store.List("steam:1"));

        // The prefix is reset (deleted): the restore recreates the save in the new prefix.
        Directory.Delete(Path.Combine(winHome, "AppData"), true);
        var plan = store.Plan(store.List()[0], r);
        Assert.Equal([(save, 9L, false)], plan);
        Assert.Equal(1, store.Restore(store.List()[0], r));
        Assert.Equal("good save", File.ReadAllText(save));

        // Overwrites a newer, broken save.
        File.WriteAllText(save, "corrupt");
        Assert.True(store.Plan(store.List()[0], r).Single().Replaces);
        store.Restore(store.List()[0], r);
        Assert.Equal("good save", File.ReadAllText(save));
    }

    [LinuxFact]
    public void RestoreNeverWritesOutsideTheGamesFolders()
    {
        var r = Roots(out _);
        var archive = Path.Combine(_dir, "evil.tar.gz");
        using (var f = File.Create(archive))
        using (var gz = new System.IO.Compression.GZipStream(f, System.IO.Compression.CompressionLevel.Fastest))
        using (var tar = new System.Formats.Tar.TarWriter(gz))
        {
            var e = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "winhome/../../../../../escaped.txt") { DataStream = new MemoryStream("x"u8.ToArray()) };
            tar.WriteEntry(e);
            var unknown = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "nowhere/file.txt") { DataStream = new MemoryStream("x"u8.ToArray()) };
            tar.WriteEntry(unknown);
        }
        var store = new SaveBackups(Path.Combine(_dir, "backups"));
        var b = new SaveBackup { GameKey = "steam:1245620", Archive = archive };
        Assert.Empty(store.Plan(b, r));
        Assert.Equal(0, store.Restore(b, r));
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
    }
}
