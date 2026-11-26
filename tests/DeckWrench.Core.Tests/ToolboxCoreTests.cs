using System.Formats.Tar;
using System.IO.Compression;
using DeckWrench.Core.Detection;
using DeckWrench.Core.Launch;
using DeckWrench.Core.Logs;
using DeckWrench.Core.Prefixes;
using DeckWrench.Core.Proton;
using DeckWrench.Core.Steam;
using DeckWrench.Core.Storage;
using DeckWrench.Core.Tricks;
using Xunit;

namespace DeckWrench.Core.Tests;

public class ToolboxCoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deckwrench-core-" + Guid.NewGuid().ToString("N")[..8]);

    public ToolboxCoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            PrefixBackups.DeleteTree(_dir);
        }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- VDF ----

    [Fact]
    public void KeyValuesWriteRoundTripsAndKeepsKeySpelling()
    {
        var kv = KeyValues.Parse("""
            "InstallConfigStore" { "Software" { "Valve" { "Steam" { "CompatToolMapping" { "0" { "name" "proton_9" } } } } } }
            """);
        var mapping = kv.Path("InstallConfigStore", "Software", "Valve", "Steam", "CompatToolMapping")!;
        mapping.GetOrAddMap("1245620").SetStr("name", "GE-Proton9-20");
        mapping.GetOrAddMap("0").SetStr("NAME", "proton_experimental");  // case-insensitive, existing spelling kept
        kv.GetOrAddPath("InstallConfigStore", "Odd").SetStr("quote", "say \"hi\" C:\\path");
        var text = kv.Serialize();
        Assert.Contains("\"name\"\t\t\"proton_experimental\"", text);
        var back = KeyValues.Parse(text);
        var m2 = back.Path("InstallConfigStore", "Software", "Valve", "Steam", "CompatToolMapping")!;
        Assert.Equal("GE-Proton9-20", m2.Path("1245620")!.Str("name"));
        Assert.Equal("proton_experimental", m2.Path("0")!.Str("name"));
        Assert.Equal("say \"hi\" C:\\path", back.Path("InstallConfigStore", "Odd")!.Str("quote"));
        Assert.True(m2.Remove("1245620"));
        Assert.Null(KeyValues.Parse(back.Serialize()).Path("InstallConfigStore", "Software", "Valve", "Steam", "CompatToolMapping", "1245620"));
    }

    [Fact]
    public void SteamConfigSetsCompatToolAndLaunchOptionsWithBackups()
    {
        var root = Path.Combine(_dir, "Steam");
        Write("Steam/config/config.vdf", """
            "InstallConfigStore" { "Software" { "Valve" { "Steam" { "CompatToolMapping" { "0" { "name" "proton_9" "config" "" "priority" "75" } } } } } }
            """);
        Write("Steam/userdata/12345678/config/localconfig.vdf", """
            "UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" { "413150" { "LaunchOptions" "-old" "LastPlayed" "1" } } } } } }
            """);
        var steam = new SteamConfig(root);
        Assert.Equal("proton_9", steam.DefaultCompatTool());

        steam.SetCompatTool("1245620", "GE-Proton9-20");
        Assert.Equal("GE-Proton9-20", steam.CompatToolMapping()["1245620"]);
        Assert.True(File.Exists(steam.ConfigVdf + ".deckwrench-bak"));
        steam.SetCompatTool("1245620", null);  // back to Steam's default
        Assert.False(steam.CompatToolMapping().ContainsKey("1245620"));
        Assert.Equal("proton_9", steam.DefaultCompatTool());

        steam.SetLaunchOptions("413150", "gamemoderun %command%");
        steam.SetLaunchOptions("1091500", "-skipStartScreen");
        var opts = steam.LaunchOptions();
        Assert.Equal("gamemoderun %command%", opts["413150"]);
        Assert.Equal("-skipStartScreen", opts["1091500"]);
        Assert.Equal("12345678", steam.AccountId);
        // Other keys of the app are left alone.
        var local = KeyValues.ParseFile(steam.LocalConfigFile!);
        Assert.Equal("1", local.Path("UserLocalConfigStore", "Software", "Valve", "Steam", "apps", "413150")!.Str("LastPlayed"));
    }

    // ---- Proton ----

    [Theory]
    [InlineData("Proton - Experimental", "proton_experimental")]
    [InlineData("Proton Hotfix", "proton_hotfix")]
    [InlineData("Proton 9.0 (Beta)", "proton_9")]
    [InlineData("Proton 8.0", "proton_8")]
    [InlineData("Proton 6.3", "proton_63")]
    [InlineData("Proton 5.0", "proton_5")]
    [InlineData("Proton EasyAntiCheat Runtime", null)]
    [InlineData("Steam Linux Runtime 3.0 (sniper)", null)]
    public void ValveFolderNamesMapToSteamsInternalNames(string folder, string? expected) =>
        Assert.Equal(expected, ProtonNames.InternalName(folder));

    [Fact]
    public void FriendlyNamesAndGeVersions()
    {
        Assert.Equal("Proton 9.0", ProtonNames.Friendly("proton_9"));
        Assert.Equal("Proton 6.3", ProtonNames.Friendly("proton_63"));
        Assert.Equal("Proton Experimental", ProtonNames.Friendly("proton_experimental"));
        Assert.Equal("GE-Proton9-20", ProtonNames.Friendly("GE-Proton9-20"));
        Assert.Equal((9, 20), ProtonNames.GeVersion("GE-Proton9-20"));
        Assert.Null(ProtonNames.GeVersion("proton_9"));
    }

    [Fact]
    public void GeReleasesParseSkippingDraftsAndPrereleases()
    {
        var releases = GeProton.ParseReleases("""
            [
              {"tag_name": "GE-Proton10-2", "prerelease": true, "assets": []},
              {"tag_name": "GE-Proton10-1", "published_at": "2026-11-20T10:00:00Z", "body": "notes",
               "assets": [{"name": "GE-Proton10-1.tar.gz", "browser_download_url": "https://x/a.tar.gz", "size": 500},
                          {"name": "GE-Proton10-1.sha512sum", "browser_download_url": "https://x/a.sha512sum", "size": 1}]},
              {"tag_name": "GE-Proton9-27", "draft": true, "assets": []}
            ]
            """);
        var r = Assert.Single(releases);
        Assert.Equal("GE-Proton10-1", r.Tag);
        Assert.Equal(500, r.TarballSize);
        Assert.Equal("https://x/a.sha512sum", r.ChecksumUrl);
        Assert.Equal("abc123", GeProton.ParseChecksum("abc123  GE-Proton10-1.tar.gz\n"));
    }

    [LinuxFact]
    public void GeTarballExtractsIntoPlaceAndRejectsOtherArchives()
    {
        var tools = Directory.CreateDirectory(Path.Combine(_dir, "compatibilitytools.d")).FullName;
        var good = Tarball("good.tar.gz", ("GE-Proton10-1/compatibilitytool.vdf", "\"compatibilitytools\" {}"), ("GE-Proton10-1/proton", "#!/bin/sh"));
        GeProton.Extract(good, tools, "GE-Proton10-1");
        Assert.True(File.Exists(Path.Combine(tools, "GE-Proton10-1", "proton")));
        Assert.Single(Directory.GetDirectories(tools));  // no staging left behind

        var bad = Tarball("bad.tar.gz", ("random/readme.txt", "hi"));
        Assert.Throws<InvalidDataException>(() => GeProton.Extract(bad, tools, "GE-Proton10-2"));
        Assert.Single(Directory.GetDirectories(tools));
    }

    private string Tarball(string name, params (string Path, string Content)[] files)
    {
        var src = Path.Combine(_dir, "src-" + name);
        foreach (var (p, c) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(src, p))!);
            File.WriteAllText(Path.Combine(src, p), c);
        }
        var tar = Path.Combine(_dir, name);
        using (var f = File.Create(tar))
        using (var gz = new GZipStream(f, CompressionLevel.Fastest))
        {
            TarFile.CreateFromDirectory(src, gz, includeBaseDirectory: false);
        }
        return tar;
    }

    // ---- launch options ----

    [Fact]
    public void LaunchOptionsParseAndPresetsToggle()
    {
        var o = LaunchOptions.Parse("PROTON_LOG=1 DXVK_HUD=\"fps,gpuload\" gamemoderun %command% -skipStartScreen");
        Assert.Equal("1", o.EnvValue("PROTON_LOG"));
        Assert.True(o.Has(LaunchPreset.Find("gamemode")!));
        Assert.True(o.Has(LaunchPreset.Find("proton-log")!));

        o.Remove(LaunchPreset.Find("proton-log")!);
        o.Add(LaunchPreset.Find("mangohud")!);
        var s = o.ToString();
        Assert.DoesNotContain("PROTON_LOG", s);
        Assert.Contains("mangohud", s);
        Assert.EndsWith("%command% -skipStartScreen", s);
        Assert.Contains("DXVK_HUD=\"fps,gpuload\"", s);

        // Plain arguments get %command% once an env var or wrapper is added.
        var plain = LaunchOptions.Parse("-skipStartScreen");
        Assert.Equal("-skipStartScreen", plain.ToString());
        plain.Add(LaunchPreset.Find("gamemode")!);
        Assert.Equal("gamemoderun %command% -skipStartScreen", plain.ToString());
    }

    [Fact]
    public void PresetsInAGroupReplaceEachOther()
    {
        var o = LaunchOptions.Parse("");
        o.Add(LaunchPreset.Find("dxvk-60")!);
        o.Add(LaunchPreset.Find("dxvk-40")!);
        Assert.Equal("40", o.EnvValue("DXVK_FRAME_RATE"));
        Assert.False(o.Has(LaunchPreset.Find("dxvk-60")!));
        Assert.All(LaunchPreset.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Label)));
    }

    // ---- logs ----

    [Fact]
    public void LogHintsNameTheVerbForMissingDlls()
    {
        string[] lines =
        [
            "0024:err:module:import_dll Library MSVCP140.dll (which is needed by L\"Z:\\\\game.exe\") not found",
            "0024:err:module:import_dll Library MSVCP140.dll (which is needed by L\"Z:\\\\other.exe\") not found",
            "0024:err:module:import_dll Library d3dx9_43.dll (which is needed by L\"Z:\\\\game.exe\") not found",
            "0024:fixme:ntdll:NtQuerySystemInformation info_class SYSTEM_PERFORMANCE_INFORMATION",
        ];
        var hints = ProtonLogs.Hints(lines);
        Assert.Equal(2, hints.Count);
        Assert.Equal("vcrun2022", hints[0].Verb);
        Assert.Equal("d3dx9", hints[1].Verb);
        Assert.Equal(LogLevel.Error, ProtonLogs.Classify(lines[0]));
        Assert.Equal(LogLevel.Info, ProtonLogs.Classify(lines[3]));
        Assert.All(hints.Where(h => h.Verb != null), h => Assert.NotNull(TricksCatalog.Find(h.Verb!)));
    }

    // ---- winetricks verbs ----

    [Theory]
    [InlineData("vcrun2022", true)]
    [InlineData("d3dcompiler_47", true)]
    [InlineData("renderer=vulkan", true)]
    [InlineData("-q", false)]
    [InlineData("--force", false)]
    [InlineData("VCRUN2022", false)]
    [InlineData("vcrun;rm", false)]
    [InlineData("$(id)", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void VerbsNeverPassOptionsOrShell(string verb, bool ok) => Assert.Equal(ok, TricksCatalog.IsVerb(verb));

    [Fact]
    public void TypedVerbsAreSplitAndChecked()
    {
        var (verbs, error) = TricksCatalog.ParseTyped("  d3dx9_43, win7  d3dx9_43\n");
        Assert.Null(error);
        Assert.Equal(["d3dx9_43", "win7"], verbs);
        Assert.Contains("option", TricksCatalog.ParseTyped("corefonts --force").Error);
        Assert.NotNull(TricksCatalog.ParseTyped("corefonts; reboot").Error);
        Assert.NotNull(TricksCatalog.ParseTyped("   ").Error);
    }

    [Fact]
    public void UserVerbsJoinTheCatalog()
    {
        var file = Write("tricks.json", """
            {
              // mine
              "verbs": [
                { "id": "mfc42", "label": "MFC 4.2", "help": "For old launchers." },
                { "id": "vcrun2022", "category": "Runtimes", "label": "VC++ (mine)" },
                { "id": "-q", "label": "sneaky" },
                { "id": "mfc42" },
              ]
            }
            """);
        var user = TricksCatalog.LoadUser(file, out var problems);
        Assert.Equal(["mfc42", "vcrun2022"], user.Select(v => v.Id));
        Assert.Equal(TricksCatalog.UserCategory, user[0].Category);
        Assert.Single(problems);
        var all = TricksCatalog.All(file);
        Assert.Equal(TricksCatalog.Verbs.Count + 1, all.Count);
        Assert.Equal("VC++ (mine)", all.Single(v => v.Id == "vcrun2022").Label);
        Assert.Same(TricksCatalog.Verbs, TricksCatalog.All(Path.Combine(_dir, "missing.json")));
        Write("broken.json", "{ nope");
        Assert.Empty(TricksCatalog.LoadUser(Path.Combine(_dir, "broken.json"), out problems));
        Assert.Single(problems);
    }

    [Fact]
    public void TricksCommandRefusesOptions()
    {
        var runner = new TricksRunner("/usr/bin/protontricks", [], "protontricks");
        var (_, args) = runner.Command("1245620", ["vcrun2022", "xna40"]);
        Assert.Equal(["1245620", "-q", "vcrun2022", "xna40"], args);
        Assert.Throws<ArgumentException>(() => runner.Command("1245620", ["--gui"]));
    }

    // ---- drives ----

    [Theory]
    [InlineData("/dev/mmcblk0p1", "/run/media/deck/sdcard", "SD card")]
    [InlineData("/dev/sdb1", "/run/media/deck/Games", "External drive")]
    [InlineData("/dev/nvme0n1p8", "/home", "Internal")]
    public void DriveLabels(string device, string mount, string label) => Assert.Equal(label, Drives.LabelFor(device, mount));

    // ---- prefixes ----

    [LinuxFact]
    public void PrefixBackupRestoresFilesAndSymlinks()
    {
        var lib = Path.Combine(_dir, "lib");
        var prefixDir = Path.Combine(lib, "steamapps", "compatdata", "1245620");
        var save = Write("lib/steamapps/compatdata/1245620/pfx/drive_c/users/steamuser/Documents/save.sl2", "precious");
        Directory.CreateDirectory(Path.Combine(prefixDir, "pfx", "dosdevices"));
        File.CreateSymbolicLink(Path.Combine(prefixDir, "pfx", "dosdevices", "z:"), "/");
        var prefix = new AppFolder { Kind = AppFolderKind.Prefix, AppId = "1245620", Dir = prefixDir, Library = lib, Size = 8 };
        var backups = new PrefixBackups(Path.Combine(_dir, "backups"));

        var b = backups.Create(prefix, "test");
        Assert.True(File.Exists(b.Archive));
        Assert.True(b.ArchiveSize < 1 << 20);  // the z: link (to /) was stored as a link, not followed
        Assert.Single(backups.List("1245620"));

        File.WriteAllText(save, "broken");
        File.WriteAllText(Path.Combine(prefixDir, "pfx", "new-junk.txt"), "x");
        backups.Restore(b);
        Assert.Equal("precious", File.ReadAllText(save));
        Assert.False(File.Exists(Path.Combine(prefixDir, "pfx", "new-junk.txt")));
        Assert.Equal("/", new FileInfo(Path.Combine(prefixDir, "pfx", "dosdevices", "z:")).LinkTarget);

        backups.Delete(b);
        Assert.Empty(backups.List());
    }

    [LinuxFact]
    public void DeleteTreeDoesNotFollowLinks()
    {
        var outside = Write("outside/keep.txt", "keep");
        var tree = Directory.CreateDirectory(Path.Combine(_dir, "tree", "pfx")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(tree, "link"), Path.GetDirectoryName(outside)!);
        PrefixBackups.DeleteTree(Path.Combine(_dir, "tree"));
        Assert.False(Directory.Exists(Path.Combine(_dir, "tree")));
        Assert.True(File.Exists(outside));
    }

    [LinuxFact]
    public void OrphanedFoldersAreFlagged()
    {
        var lib = Path.Combine(_dir, "lib");
        Directory.CreateDirectory(Path.Combine(lib, "steamapps", "compatdata", "1245620", "pfx"));
        Directory.CreateDirectory(Path.Combine(lib, "steamapps", "compatdata", "271590", "pfx"));
        Directory.CreateDirectory(Path.Combine(lib, "steamapps", "compatdata", "0"));  // Steam's own, ignored
        var found = AppFolders.Scan(AppFolderKind.Prefix, [lib], new Dictionary<string, string> { ["1245620"] = "ELDEN RING" }, new HashSet<string>());
        Assert.Equal(["1245620", "271590"], found.Select(f => f.AppId).Order());
        Assert.False(found.Single(f => f.AppId == "1245620").Orphan);
        Assert.True(found.Single(f => f.AppId == "271590").Orphan);
    }

    // ---- moving games ----

    [LinuxFact]
    public void GameMoverMovesFilesAndManifestTogether()
    {
        var from = Path.Combine(_dir, "home", ".local", "share", "Steam");
        var to = Path.Combine(_dir, "sd");
        Write("home/.local/share/Steam/steamapps/libraryfolders.vdf",
            $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{from}\" }} \"1\" {{ \"path\" \"{to}\" }} }}");
        Write("home/.local/share/Steam/steamapps/common/Hades/Hades.exe", "game");
        Write("home/.local/share/Steam/steamapps/compatdata/1145360/pfx/user.reg", "reg");
        var manifest = Write("home/.local/share/Steam/steamapps/appmanifest_1145360.acf", "\"AppState\" { \"appid\" \"1145360\" \"name\" \"Hades\" \"installdir\" \"Hades\" }");
        Directory.CreateDirectory(Path.Combine(to, "steamapps"));
        var game = Assert.Single(new SteamScanner(Path.Combine(_dir, "home")).Scan());

        var plan = GameMover.Plan(game, to);
        Assert.True(plan.TotalSize > 0);
        GameMover.Execute(plan, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(to, "steamapps", "common", "Hades", "Hades.exe")));
        Assert.True(File.Exists(Path.Combine(to, "steamapps", "appmanifest_1145360.acf")));
        Assert.False(File.Exists(manifest));
        Assert.False(Directory.Exists(Path.Combine(from, "steamapps", "common", "Hades")));
    }

    [Fact]
    public void SaveFinderListsGameFoldersInTheProfileNewestFirst()
    {
        const string u = "pfx/drive_c/users/steamuser/";
        Write(u + "Documents/My Games/Skyrim/Saves/quick.ess", "x");
        Write(u + "AppData/LocalLow/Team Cherry/Hollow Knight/user1.dat", "x");
        Write(u + "AppData/Local/Microsoft/junk.txt", "x");  // Windows' own: skipped
        Write(u + "Documents/readme.txt", "x");  // a file, not a game folder
        Directory.CreateDirectory(Path.Combine(_dir, u, "AppData/Roaming/Empty"));  // empty: skipped
        var old = DateTime.UtcNow.AddDays(-10);
        Directory.SetLastWriteTimeUtc(Path.Combine(_dir, u, "Documents/My Games/Skyrim/Saves"), old);  // what's inside counts

        var saves = SaveFinder.Find(Path.Combine(_dir, "pfx"));
        Assert.Equal(["AppData/LocalLow/Team Cherry", "Documents/My Games/Skyrim"], saves.Select(s => s.Name).ToArray());
        Assert.Empty(SaveFinder.Find(Path.Combine(_dir, "nothing-here")));
    }
}
