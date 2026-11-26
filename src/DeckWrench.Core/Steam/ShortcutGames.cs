using System.Globalization;
using DeckWrench.Core.Detection;

namespace DeckWrench.Core.Steam;

/// <summary>
/// Non-Steam games added to Steam. Steam runs them with Proton like its own games: their prefix and
/// shader cache are named by the shortcut's app id, their compatibility tool sits in config.vdf's
/// CompatToolMapping, but their launch options live in shortcuts.vdf.
/// </summary>
public static class ShortcutGames
{
    public static IReadOnlyList<InstalledGame> Scan(string home, string? steamRoot)
    {
        var list = new List<InstalledGame>();
        var seen = new HashSet<uint>();
        foreach (var dir in SteamShortcuts.AccountConfigDirs(home))
        {
            SteamShortcuts file;
            try
            {
                file = SteamShortcuts.Load(dir);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var e in file.Entries)
            {
                var name = e.Str("AppName") ?? e.Str("appname") ?? "";
                var exe = e.Str("Exe") ?? e.Str("exe") ?? "";
                if (name.Length == 0 || IsDeckWrench(exe))
                {
                    continue;
                }
                var id = e.Int("appid") is { } signed && signed != 0 ? (uint)signed : SteamShortcut.ComputeAppId(exe, name);
                if (!seen.Add(id))
                {
                    continue;  // the same shortcut under another account
                }
                var start = Unquote(e.Str("StartDir") ?? e.Str("startdir") ?? "");
                list.Add(new InstalledGame
                {
                    Source = GameSource.SteamShortcut,
                    SourceId = id.ToString(CultureInfo.InvariantCulture),
                    Title = name,
                    InstallDir = start.Length > 0 ? start : Path.GetDirectoryName(Unquote(exe)) ?? "",
                    LaunchOptions = e.Str("LaunchOptions") ?? e.Str("launchoptions") ?? "",
                    SteamRoot = steamRoot,
                    Library = steamRoot,
                    ShortcutsFile = file.FilePath,
                    Cover = Cover(dir, id),
                });
            }
        }
        return list;
    }

    /// <summary>The id Proton names a shortcut's log with (SteamGameId: app id in the high half, 0x02000000 below).</summary>
    public static string GameId(string appId) =>
        uint.TryParse(appId, out var id) ? (((ulong)id << 32) | 0x02000000UL).ToString(CultureInfo.InvariantCulture) : appId;

    /// <summary>Writes a shortcut's launch options. Steam must be closed (it rewrites shortcuts.vdf on exit).</summary>
    public static void SetLaunchOptions(string shortcutsFile, string appId, string options)
    {
        var file = SteamShortcuts.Load(Path.GetDirectoryName(shortcutsFile)!);
        var entry = file.Find(uint.Parse(appId, CultureInfo.InvariantCulture)) ?? throw new InvalidOperationException("The shortcut isn't in Steam any more.");
        entry["LaunchOptions"] = options;
        file.Save();  // keeps a .deckwrench-bak copy
    }

    private static string? Cover(string configDir, uint id)
    {
        foreach (var ext in new[] { ".png", ".jpg" })
        {
            var p = Path.Combine(SteamShortcuts.GridDir(configDir), id.ToString(CultureInfo.InvariantCulture) + "p" + ext);
            if (File.Exists(p))
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>DeckWrench's own shortcut (DeckWrench.x86_64 or DeckWrench.exe) isn't a game.</summary>
    private static bool IsDeckWrench(string exe) =>
        Path.GetFileNameWithoutExtension(Unquote(exe)).Equals("DeckWrench", StringComparison.OrdinalIgnoreCase);

    private static string Unquote(string s) => s.Trim().Trim('"');
}
