using DeckWrench.Core.Storage;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeckWrench.Core;
using DeckWrench.Core.Detection;
using DeckWrench.Core.Prefixes;
using DeckWrench.Core.Steam;
using DeckWrench.Host.Toolbox;
using DeckWrench.Host.Updates;

namespace DeckWrench.Host;

/// <summary>DeckWrench's own app settings.</summary>
public sealed record AppSettings
{
    /// <summary>UI theme id (see the app's theme list); unknown ids fall back to "dark".</summary>
    public string Theme { get; init; } = "dark";

    /// <summary>Accent colour per theme id (a swatch id from the app's list); missing = the theme's own.</summary>
    public Dictionary<string, string> Accents { get; init; } = new();

    /// <summary>Screen layout: "auto" (controller in Game Mode, desktop elsewhere), "controller" or "desktop".</summary>
    public string Layout { get; init; } = "auto";

    /// <summary>Turns off UI animations.</summary>
    public bool ReduceMotion { get; init; }

    /// <summary>CRT scanlines over the UI in any theme.</summary>
    public bool Crt { get; init; }

    /// <summary>CRT effect strength when <see cref="Crt"/> is on: 1 subtle, 2 medium, 3 strong.</summary>
    public int CrtLevel { get; init; } = 2;

    /// <summary>The theme's slow animated background behind the menus.</summary>
    public bool AnimatedBackground { get; init; } = true;

    /// <summary>Menu sounds, 0 (off) to 100.</summary>
    public int SoundVolume { get; init; } = 50;

    /// <summary>A light rumble on the pad for menu moves, presses and errors.</summary>
    public bool Haptics { get; init; } = true;

    /// <summary>Check GitHub Releases for a new DeckWrench version at startup.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Look up ProtonDB tiers and Steam Deck ratings of installed Steam games (sends their app ids to ProtonDB and Steam).</summary>
    public bool OnlineRatings { get; init; } = true;

    /// <summary>Last app version whose "What's new" was shown; null on a fresh install.</summary>
    public string? LastSeenVersion { get; init; }

    /// <summary>Desktop mode: the column each table is sorted by ("Games" → "Size"), with "-" in front for descending.</summary>
    public Dictionary<string, string> TableSort { get; init; } = new();
}

/// <summary>
/// Composition root shared by the Godot UI (both modes) and the headless CLI (`deckwrench`).
/// Holds the settings, the last scan, and the action catalog both UI modes render.
/// </summary>
public sealed class AppHost
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AppPaths Paths { get; }
    public AppSettings Settings { get; private set; }
    public AppUpdater Updater { get; }
    public HttpClient Http { get; }
    public PendingChanges Pending { get; }
    public PrefixBackups Backups { get; }
    public GameNotes Notes { get; }

    /// <summary>Folder sizes remembered between scans; "Scan again" forgets them.</summary>
    public SizeCache Sizes { get; }

    /// <summary>ProtonDB tiers and Steam Deck ratings, cached for a few days.</summary>
    public Core.Compat.CompatReports Compat { get; }
    public Actions Actions { get; }

    private readonly ToolboxScanner _scanner;

    /// <summary>The last scan (empty until the first one finishes).</summary>
    public ToolboxState State { get; private set; } = ToolboxState.Empty;

    /// <summary>What the last scan found that needs attention (Home), worst first.</summary>
    public IReadOnlyList<Finding> Findings { get; private set; } = [];

    /// <summary>Raised (on the scanning thread) when a scan finishes.</summary>
    public event Action? StateChanged;

    public AppHost(AppPaths? paths = null, HttpClient? http = null)
    {
        Paths = paths ?? new AppPaths();
        Directory.CreateDirectory(Paths.Config);
        Directory.CreateDirectory(Paths.Data);
        Settings = LoadJson<AppSettings>(Paths.SettingsFile) ?? new AppSettings();
        Http = http ?? AppUpdater.DefaultClient();
        Updater = new AppUpdater(Http);
        Pending = new PendingChanges(Path.Combine(Paths.Config, "pending.json"));
        Notes = new GameNotes(Path.Combine(Paths.Config, "notes.json"));
        Backups = new PrefixBackups(Path.Combine(Paths.Data, "backups", "prefixes"));
        Sizes = new SizeCache(Path.Combine(Paths.Cache, "sizes.json"));
        Compat = new Core.Compat.CompatReports(Path.Combine(Paths.Cache, "ratings.json"), Http);
        _scanner = new ToolboxScanner(Paths, Pending, Backups, Sizes);
        Actions = new Actions(this);
    }

    /// <summary>Scans everything (Steam, Heroic, Lutris, sizes). Safe off the main thread.</summary>
    public ToolboxState Scan(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var state = _scanner.Scan(progress, ct);
        AppLog.Info($"scan: {(DateTime.UtcNow - started).TotalMilliseconds:0} ms, folder sizes {Sizes.Hits} remembered, {Sizes.Misses} measured");
        Sizes.Save();
        State = state;
        Findings = Checkup.Run(state, Actions, ratings: Compat.Get);
        StateChanged?.Invoke();
        return state;
    }

    /// <summary>
    /// Fetches ratings of Steam games that have none or an old one (when online ratings are on).
    /// Returns how many changed; the caller refreshes the UI when it's more than zero.
    /// </summary>
    public async Task<int> RefreshRatings(CancellationToken ct = default)
    {
        if (!Settings.OnlineRatings)
        {
            return 0;
        }
        var stale = Compat.Stale(State.Games.Where(g => g.IsSteam).Select(g => g.AppId), DateTime.UtcNow);
        var changed = await Compat.Refresh(stale, ct).ConfigureAwait(false);
        if (changed > 0)
        {
            Findings = Checkup.Run(State, Actions, ratings: Compat.Get);
        }
        return changed;
    }

    /// <summary>The `deckwrench` helper next to the app (release builds), or null when running from source.</summary>
    public string? CliPath
    {
        get
        {
            var dir = Environment.ProcessPath is { } exe ? Path.GetDirectoryName(exe)! : AppContext.BaseDirectory;
            var cli = Path.Combine(dir, Platform.CliBinary);
            return File.Exists(cli) ? cli : null;
        }
    }

    /// <summary>Writes pending Steam changes now. Steam must be closed. Returns how many were written.</summary>
    public int ApplyPendingNow()
    {
        if (Pending.All.Count == 0)
        {
            return 0;
        }
        if (SteamShortcuts.SteamRunning(Paths.Home))
        {
            throw new InvalidOperationException("Steam is running; it would overwrite the changes when it exits.");
        }
        var root = new SteamScanner(Paths.Home).Roots().FirstOrDefault().Root ?? throw new InvalidOperationException("Steam isn't installed.");
        return Pending.Apply(new SteamConfig(root));
    }

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        SaveJson(Paths.SettingsFile, settings);
    }

    internal static T? LoadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
