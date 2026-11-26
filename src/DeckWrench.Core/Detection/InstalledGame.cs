namespace DeckWrench.Core.Detection;


/// <summary>Where an install came from.</summary>
public enum GameSource
{
    Steam,
    Epic,
    Gog,
    Amazon,
    Lutris,

    /// <summary>A non-Steam game added to Steam (shortcuts.vdf): Battle.net, emulators, anything else.</summary>
    SteamShortcut,
}

/// <summary>One installed game as a launcher records it, before matching against the support profiles.</summary>
public sealed record InstalledGame
{
    public GameSource Source { get; init; }

    /// <summary>The launcher's id: Steam app id, Epic app name, GOG product id, Lutris slug.</summary>
    public string SourceId { get; init; } = "";

    public string Title { get; init; } = "";

    /// <summary>The game folder.</summary>
    public string InstallDir { get; init; } = "";

    /// <summary>The Wine/Proton prefix (the folder holding drive_c), when the game runs in one.</summary>
    public string? Prefix { get; init; }

    /// <summary>Steam library this game is in (Steam only), e.g. the SD card's.</summary>
    public string? Library { get; init; }

    /// <summary>Compatibility tool Steam uses for it ("proton_9", "GE-Proton9-20"), when one is set.</summary>
    public string? CompatTool { get; init; }

    /// <summary>Launch options set in Steam for this game (Steam only), as the player typed them.</summary>
    public string? LaunchOptions { get; init; }

    /// <summary>Portrait cover art on disk (Steam's library cache), when there is one.</summary>
    public string? Cover { get; init; }

    /// <summary>Install size Steam recorded in the manifest (SizeOnDisk), in bytes; 0 when unknown.</summary>
    public long SizeOnDisk { get; init; }

    /// <summary>The appmanifest_*.acf file (Steam only).</summary>
    public string? Manifest { get; init; }

    /// <summary>The Steam root this game belongs to (Steam only), where config.vdf and userdata live.</summary>
    public string? SteamRoot { get; init; }

    /// <summary>Where the launcher itself is installed from: "native" or "flatpak".</summary>
    public string Packaging { get; init; } = "native";

    /// <summary>The shortcuts.vdf that holds a non-Steam game (its launch options are kept there).</summary>
    public string? ShortcutsFile { get; init; }
}
