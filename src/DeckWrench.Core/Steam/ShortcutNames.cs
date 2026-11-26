namespace DeckWrench.Core.Steam;

/// <summary>Non-Steam games (shortcuts) by the app id Steam gives their Proton prefix and shader cache.</summary>
public static class ShortcutNames
{
    /// <summary>App id (unsigned, as compatdata names it) → shortcut name, over every account on the device.</summary>
    public static Dictionary<string, string> All(string home)
    {
        var map = new Dictionary<string, string>();
        foreach (var dir in SteamShortcuts.AccountConfigDirs(home))
        {
            try
            {
                foreach (var e in SteamShortcuts.Load(dir).Entries)
                {
                    var name = e.Str("AppName") ?? e.Str("appname") ?? "";
                    var id = e.Int("appid") is { } signed && signed != 0
                        ? (uint)signed
                        : SteamShortcut.ComputeAppId(e.Str("Exe") ?? e.Str("exe") ?? "", name);
                    map.TryAdd(id.ToString(System.Globalization.CultureInfo.InvariantCulture), name);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
            }
        }
        return map;
    }

    /// <summary>Non-Steam shortcut ids are above 2^31; Steam's own app ids are below.</summary>
    public static bool IsShortcutId(string appId) => uint.TryParse(appId, out var id) && id >= 0x80000000u;
}
