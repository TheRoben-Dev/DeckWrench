using DeckWrench.Core.Detection;

namespace DeckWrench.Core.Steam;

/// <summary>
/// Reads and edits the two text files Steam keeps per-game settings in:
/// config/config.vdf (CompatToolMapping: which Proton a game uses, "0" = the default for all)
/// and userdata/&lt;account&gt;/config/localconfig.vdf (LaunchOptions per app).
/// Steam rewrites both when it exits, so edits are only made while Steam is closed; every edit
/// keeps a copy of the file as it was (*.deckwrench-bak) first.
/// </summary>
public sealed class SteamConfig
{
    /// <summary>The Steam root (holds config/ and userdata/).</summary>
    public string Root { get; }

    public SteamConfig(string root) => Root = root;

    public string ConfigVdf => Path.Combine(Root, "config", "config.vdf");

    private static readonly string[] CompatPath = ["InstallConfigStore", "Software", "Valve", "Steam", "CompatToolMapping"];
    private static readonly string[] AppsPath = ["UserLocalConfigStore", "Software", "Valve", "Steam", "apps"];

    /// <summary>App id → compatibility tool name. Key "0" is the default for every game.</summary>
    public Dictionary<string, string> CompatToolMapping() => SteamScanner.CompatTools(Root);

    /// <summary>The default compatibility tool for all Windows games, or null when Steam picks.</summary>
    public string? DefaultCompatTool() => CompatToolMapping().GetValueOrDefault("0");

    /// <summary>
    /// Sets the compatibility tool for <paramref name="appId"/> ("0" = the default for all games);
    /// a null <paramref name="tool"/> removes the entry so Steam's default applies again.
    /// </summary>
    public void SetCompatTool(string appId, string? tool)
    {
        if (!File.Exists(ConfigVdf))
        {
            throw new InvalidOperationException("Steam's config.vdf wasn't found. Start Steam once, then try again.");
        }
        var doc = KeyValues.ParseFile(ConfigVdf);
        var mapping = doc.GetOrAddPath(CompatPath);
        if (tool == null)
        {
            mapping.Remove(appId);
        }
        else
        {
            var entry = mapping.GetOrAddMap(appId);
            entry.SetStr("name", tool);
            if (entry.Str("config") == null)
            {
                entry.SetStr("config", "");
            }
            // 250 is what Steam writes for a choice the player made in Properties.
            entry.SetStr("priority", "250");
        }
        Backup(ConfigVdf);
        doc.SaveFile(ConfigVdf);
    }

    /// <summary>The account config folder whose localconfig.vdf DeckWrench reads and edits (the most recently used one).</summary>
    public string? LocalConfigFile => SteamScanner.LocalConfig(Root);

    /// <summary>Steam account id of <see cref="LocalConfigFile"/> (the userdata folder name).</summary>
    public string? AccountId => LocalConfigFile is { } f ? Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))) : null;

    public Dictionary<string, string> LaunchOptions() => SteamScanner.LaunchOptionsByApp(Root);

    /// <summary>Sets (or with an empty string clears) a game's launch options in the current account.</summary>
    public void SetLaunchOptions(string appId, string options)
    {
        var file = LocalConfigFile ?? throw new InvalidOperationException("No Steam account found on this device. Sign in to Steam once, then try again.");
        var doc = KeyValues.ParseFile(file);
        var app = doc.GetOrAddPath(AppsPath).GetOrAddMap(appId);
        if (options.Length == 0)
        {
            app.Remove("LaunchOptions");
        }
        else
        {
            app.SetStr("LaunchOptions", options);
        }
        Backup(file);
        doc.SaveFile(file);
    }

    /// <summary>Keeps the file as it was before DeckWrench's first change of this session.</summary>
    private static void Backup(string file)
    {
        var bak = file + ".deckwrench-bak";
        File.Copy(file, bak, overwrite: true);
    }
}
