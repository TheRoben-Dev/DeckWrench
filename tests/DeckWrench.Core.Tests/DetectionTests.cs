using DeckWrench.Core.Detection;
using Xunit;

namespace DeckWrench.Core.Tests;

public class DetectionTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "deckwrench-det-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Dir(string relative) => Directory.CreateDirectory(Path.Combine(_home, relative)).FullName;

    [Fact]
    public void KeyValuesParsesNestingCommentsEscapesAndCase()
    {
        var kv = KeyValues.Parse("""
            // comment
            "Root"
            {
                "name"  "Say \"hi\""   [$WIN32]
                "Inner" { "Key" "v" }
                bare value
            }
            """);
        var root = kv.Map("root")!;
        Assert.Equal("Say \"hi\"", root.Str("NAME"));
        Assert.Equal("v", kv.Path("Root", "inner")!.Str("key"));
        Assert.Equal("value", root.Str("bare"));
        Assert.Throws<InvalidDataException>(() => KeyValues.Parse("\"a\" { \"b\" \"c\""));
    }

    /// <summary>A Steam Deck layout: internal library plus an SD card, a Proton prefix, launch options and art.</summary>
    private (string Root, string Sd) FakeSteam()
    {
        var root = Path.Combine(_home, ".local/share/Steam");
        var sd = Dir("media/sdcard");
        Write(".local/share/Steam/steamapps/libraryfolders.vdf", $$"""
            "libraryfolders"
            {
                "0" { "path" "{{root}}" "apps" { "489830" "1" } }
                "1" { "path" "{{sd}}" }
                "2" { "path" "/nonexistent/drive" }
            }
            """);
        Write(".local/share/Steam/steamapps/appmanifest_489830.acf", """
            "AppState" { "appid" "489830" "name" "The Elder Scrolls V: Skyrim Special Edition" "installdir" "Skyrim Special Edition" }
            """);
        Write(".local/share/Steam/steamapps/common/Skyrim Special Edition/SkyrimSE.exe", "");
        Dir(".local/share/Steam/steamapps/compatdata/489830/pfx/drive_c");
        Write("media/sdcard/steamapps/appmanifest_1966720.acf", """
            "AppState" { "appid" "1966720" "name" "Lethal Company" "installdir" "Lethal Company" }
            """);
        Write("media/sdcard/steamapps/common/Lethal Company/Lethal Company.exe", "");
        // Uninstalled leftover: manifest without a folder.
        Write("media/sdcard/steamapps/appmanifest_292030.acf", """
            "AppState" { "appid" "292030" "name" "The Witcher 3" "installdir" "The Witcher 3" }
            """);
        // An unsupported game.
        Write(".local/share/Steam/steamapps/appmanifest_12345.acf", """
            "AppState" { "appid" "12345" "name" "Some Puzzle Game" "installdir" "Puzzle" }
            """);
        Dir(".local/share/Steam/steamapps/common/Puzzle");
        Write(".local/share/Steam/config/config.vdf", """
            "InstallConfigStore" { "Software" { "Valve" { "Steam" { "CompatToolMapping" {
                "1966720" { "name" "GE-Proton9-20" "config" "" "priority" "250" }
            } } } } }
            """);
        Write(".local/share/Steam/userdata/123/config/localconfig.vdf", """
            "UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" {
                "489830" { "LaunchOptions" "PROTON_LOG=1 %command%" }
            } } } } }
            """);
        Write(".local/share/Steam/appcache/librarycache/489830/abc123/library_600x900.jpg", "jpg");
        // ~/.steam/steam is a symlink to the same root on real systems.
        Directory.CreateDirectory(Path.Combine(_home, ".steam"));
        Directory.CreateSymbolicLink(Path.Combine(_home, ".steam/steam"), root);
        return (root, sd);
    }

    [LinuxFact]
    public void SteamScanFindsAllLibrariesPrefixesOptionsAndArt()
    {
        var (root, sd) = FakeSteam();
        var scanner = new SteamScanner(_home);
        Assert.Single(scanner.Roots());
        Assert.Equal([root, sd], SteamScanner.Libraries(root));

        var games = scanner.Scan().ToDictionary(g => g.SourceId);
        Assert.Equal(["12345", "1966720", "489830"], games.Keys.Order());
        var skyrim = games["489830"];
        Assert.Equal(Path.Combine(root, "steamapps/compatdata/489830/pfx"), skyrim.Prefix);
        Assert.Equal("PROTON_LOG=1 %command%", skyrim.LaunchOptions);
        Assert.EndsWith("library_600x900.jpg", skyrim.Cover);
        var lethal = games["1966720"];
        Assert.Equal(sd, lethal.Library);
        Assert.Equal("GE-Proton9-20", lethal.CompatTool);
        Assert.Null(lethal.Prefix);  // never launched yet
    }

    [LinuxFact]
    public void FlatpakSteamIsFound()
    {
        Write(".var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/appmanifest_413150.acf",
            "\"AppState\" { \"appid\" \"413150\" \"name\" \"Stardew Valley\" \"installdir\" \"Stardew Valley\" }");
        Dir(".var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Stardew Valley");
        var g = Assert.Single(new SteamScanner(_home).Scan());
        Assert.Equal("flatpak", g.Packaging);
    }

    [LinuxFact]
    public void HeroicEpicAndGogInstallsWithPrefixes()
    {
        var game = Dir("Games/Heroic/Cyberpunk 2077");
        Write("Games/Heroic/Cyberpunk 2077/bin/x64/Cyberpunk2077.exe", "");
        var pfx = Dir("Games/Heroic/Prefixes/cp/pfx/drive_c");
        Write(".config/heroic/legendaryConfig/legendary/installed.json", $$"""
            { "Ginger": { "app_name": "Ginger", "title": "Cyberpunk 2077", "install_path": "{{game}}", "platform": "Windows" } }
            """);
        Write(".config/heroic/GamesConfig/Ginger.json", $$"""
            { "Ginger": { "winePrefix": "{{Path.Combine(_home, "Games/Heroic/Prefixes/cp")}}" } }
            """);
        var gog = Dir("Games/Heroic/Stardew Valley");
        Write(".config/heroic/gog_store/installed.json", $$"""
            { "installed": [ { "appName": "1453375253", "install_path": "{{gog}}", "platform": "linux" } ] }
            """);
        var installs = new HeroicScanner(_home).Scan();
        var epic = Assert.Single(installs, i => i.Source == GameSource.Epic);
        Assert.Equal("Cyberpunk 2077", epic.Title);
        Assert.Equal(Path.GetDirectoryName(pfx), epic.Prefix);
        var g = Assert.Single(installs, i => i.Source == GameSource.Gog);
        Assert.Null(g.Prefix);  // a native Linux build has no prefix
    }

    [LinuxFact]
    public void LutrisGameFoundFromExeDeepInTheFolder()
    {
        Write("Games/skyrim/drive_c/Games/Skyrim/SkyrimSE.exe", "");
        Write(".config/lutris/games/skyrim-special-edition-1700000000.yml", $$"""
            game:
              exe: {{Path.Combine(_home, "Games/skyrim/drive_c/Games/Skyrim/SkyrimSE.exe")}}
              prefix: {{Path.Combine(_home, "Games/skyrim")}}
              args: ''
            system:
              env:
                exe: not-this-one
            wine:
              version: lutris-GE
            """);
        var install = Assert.Single(new LutrisScanner(_home).Scan());
        Assert.Equal("skyrim-special-edition", install.SourceId);
        Assert.Equal("Skyrim Special Edition", install.Title);
        Assert.Equal(Path.Combine(_home, "Games/skyrim"), install.Prefix);
    }

    [Fact]
    public void CaseInsensitivePathLookup()
    {
        Write("g/Bin/X64/Game.EXE", "");
        Assert.NotNull(PathUtil.FindCaseInsensitive(Path.Combine(_home, "g"), "bin/x64/game.exe"));
        Assert.Null(PathUtil.FindCaseInsensitive(Path.Combine(_home, "g"), "bin/x64/other.exe"));
    }
}

public class WindowsPlatformTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deckwrench-win-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AppPathsUseAppDataOnWindows()
    {
        var p = new AppPaths(v => v switch
        {
            "USERPROFILE" => @"C:\Users\me",
            "LOCALAPPDATA" => @"C:\Users\me\AppData\Local",
            "APPDATA" => @"C:\Users\me\AppData\Roaming",
            _ => null,
        }, windows: true);
        Assert.Equal(@"C:\Users\me", p.Home);
        Assert.Equal(Path.Combine(@"C:\Users\me\AppData\Local", "DeckWrench"), p.Data);
        Assert.Equal(Path.Combine(@"C:\Users\me\AppData\Roaming", "DeckWrench"), p.Config);
        Assert.StartsWith(p.Data, p.Cache);
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void WindowsRegistryLookupsDontThrow()
    {
        _ = WindowsSteam.Roots();
        _ = WindowsSteam.Running();
        Assert.False(Platform.ProcessAlive(-1));
        Assert.True(Platform.ProcessAlive(Environment.ProcessId));
    }
}
