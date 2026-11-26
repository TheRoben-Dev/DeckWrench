using DeckWrench.Core.Detection;
using DeckWrench.Core.Logs;
using DeckWrench.Core.Prefixes;
using DeckWrench.Core.Proton;
using DeckWrench.Core.Storage;

namespace DeckWrench.Host.Toolbox;

/// <summary>An installed game with everything DeckWrench knows about it.</summary>
public sealed record GameEntry
{
    public required InstalledGame Install { get; init; }

    /// <summary>"steam:1245620", "epic:Fortnite", "lutris:skyrim": unique per install.</summary>
    public string Key => $"{Install.Source.ToString().ToLowerInvariant()}:{Install.SourceId}";

    public string Title => Install.Title;

    public bool IsSteam => Install.Source == GameSource.Steam;

    /// <summary>A non-Steam game added to Steam: Proton settings like a Steam game, but no store page or mover.</summary>
    public bool IsShortcut => Install.Source == GameSource.SteamShortcut;

    /// <summary>Steam runs it, so it has a compatibility tool, Steam launch options and a compatdata prefix.</summary>
    public bool RunsInSteam => IsSteam || IsShortcut;

    public string AppId => Install.SourceId;

    /// <summary>Compatibility tool set for this game in Steam (null = Steam's default).</summary>
    public string? CompatTool { get; init; }

    /// <summary>The Proton prefix (Steam compatdata, or Heroic's / Lutris's Wine prefix).</summary>
    public AppFolder? Prefix { get; init; }

    public AppFolder? ShaderCache { get; init; }

    public string LaunchOptions { get; init; } = "";

    /// <summary>The newest Proton log for this game, if any.</summary>
    public ProtonLog? Log { get; init; }

    /// <summary>Install size (from Steam's manifest, or measured).</summary>
    public long Size { get; init; }

    /// <summary>The drive the game is installed on.</summary>
    public Drive? Drive { get; init; }

    /// <summary>A change waiting for Steam to close (compatibility tool or launch options).</summary>
    public IReadOnlyList<PendingChange> Pending { get; init; } = [];

    /// <summary>Launch options as they will be once pending changes apply.</summary>
    public string EffectiveLaunchOptions => Pending.LastOrDefault(p => p.Kind == PendingKind.LaunchOptions)?.Value ?? LaunchOptions;

    /// <summary>Compatibility tool as it will be once pending changes apply (null = Steam's default).</summary>
    public string? EffectiveCompatTool => Pending.LastOrDefault(p => p.Kind == PendingKind.CompatTool) is { } p ? p.Value : CompatTool;
}

/// <summary>One drive with Steam content and what takes its space.</summary>
public sealed record DriveReport
{
    public required Drive Drive { get; init; }
    public IReadOnlyList<string> Libraries { get; init; } = [];
    public long Games { get; init; }
    public long Prefixes { get; init; }
    public long ShaderCaches { get; init; }
    public long ProtonVersions { get; init; }

    /// <summary>Mods managed by Luma (if installed) and DeckWrench's own prefix backups.</summary>
    public long ModsAndBackups { get; init; }

    public long Other => Math.Max(0, Drive.Used - Games - Prefixes - ShaderCaches - ProtonVersions - ModsAndBackups);

    public IEnumerable<(string Label, long Size)> Breakdown =>
    [
        ("Games", Games),
        ("Proton prefixes", Prefixes),
        ("Shader caches", ShaderCaches),
        ("Proton versions", ProtonVersions),
        ("Mods and backups", ModsAndBackups),
        ("Everything else", Other),
    ];
}

public enum CleanupKind
{
    OrphanPrefix,
    OrphanShaderCache,
    UnusedProton,
    ProtonLog,
    OldBackup,
    SteamLeftover,
}

/// <summary>Something that can go to free space, with how much it frees.</summary>
public sealed record CleanupItem
{
    public required CleanupKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string Path { get; init; }
    public long Size { get; init; }

    /// <summary>Risk note shown before removing (saves in prefixes).</summary>
    public string? Caution { get; init; }
}

/// <summary>Everything one scan found. Immutable; a rescan replaces it.</summary>
public sealed record ToolboxState
{
    public DateTime ScannedAt { get; init; }

    /// <summary>The Steam install DeckWrench works with (native first, then Flatpak); null when Steam isn't installed.</summary>
    public string? SteamRoot { get; init; }

    public string SteamPackaging { get; init; } = "native";

    public IReadOnlyList<string> Libraries { get; init; } = [];

    public IReadOnlyList<GameEntry> Games { get; init; } = [];

    public IReadOnlyList<CompatTool> Tools { get; init; } = [];

    /// <summary>The compatibility tool set for all games (Steam Play default), when one is set.</summary>
    public string? DefaultTool { get; init; }

    public IReadOnlyList<AppFolder> Prefixes { get; init; } = [];

    public IReadOnlyList<AppFolder> ShaderCaches { get; init; } = [];

    public IReadOnlyList<ProtonLog> Logs { get; init; } = [];

    public IReadOnlyList<DriveReport> Drives { get; init; } = [];

    public IReadOnlyList<CleanupItem> Cleanup { get; init; } = [];

    public IReadOnlyList<PrefixBackup> Backups { get; init; } = [];

    public IReadOnlyList<PendingChange> Pending { get; init; } = [];

    /// <summary>Unfinished downloads, update scratch and Workshop content of apps that aren't installed.</summary>
    public IReadOnlyList<Core.Steam.LeftoverFolder> Leftovers { get; init; } = [];

    public static readonly ToolboxState Empty = new();

    public GameEntry? Game(string? key) => key == null ? null : Games.FirstOrDefault(g => g.Key == key);

    public CompatTool? Tool(string? name) => name == null ? null : Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Steam games that use <paramref name="tool"/> explicitly (pending changes counted).</summary>
    public IReadOnlyList<GameEntry> GamesUsing(CompatTool tool) =>
        Games.Where(g => g.RunsInSteam && string.Equals(g.EffectiveCompatTool, tool.Name, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Readable name for a tool id (installed or not); "Steam default" for null.</summary>
    public string ToolLabel(string? name) =>
        name == null ? DefaultTool is { } d ? $"Default ({ToolLabel(d)})" : "Steam default"
        : Tool(name)?.DisplayName ?? ProtonNames.Friendly(name) + (Tool(name) == null ? " (not installed)" : "");

    public long Reclaimable => Cleanup.Sum(c => c.Size);
}
