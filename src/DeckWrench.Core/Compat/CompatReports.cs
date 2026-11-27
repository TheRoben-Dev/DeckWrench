using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DeckWrench.Core.Compat;

/// <summary>Valve's Steam Deck compatibility rating.</summary>
public enum DeckRating
{
    Unknown,
    Unsupported,
    Playable,
    Verified,
}

/// <summary>What ProtonDB players and Valve's Deck testing say about a Steam game.</summary>
public sealed record CompatInfo
{
    public required string AppId { get; init; }

    /// <summary>ProtonDB's tier ("platinum", "gold", "silver", "bronze", "borked"), null when it has no reports.</summary>
    public string? Tier { get; init; }

    /// <summary>Where recent reports are heading.</summary>
    public string? Trending { get; init; }

    public int Reports { get; init; }

    public DeckRating Deck { get; init; }

    /// <summary>Valve's notes from the Deck test ("Some text is small and difficult to read").</summary>
    public IReadOnlyList<string> DeckNotes { get; init; } = [];

    public DateTime Fetched { get; init; }

    /// <summary>The fetch failed (offline): try again sooner.</summary>
    public bool Failed { get; init; }

    public static string TierLabel(string? tier) => tier is { Length: > 0 } t ? char.ToUpperInvariant(t[0]) + t[1..] : "No reports";

    public static string DeckLabel(DeckRating r) => r switch
    {
        DeckRating.Verified => "Verified",
        DeckRating.Playable => "Playable",
        DeckRating.Unsupported => "Unsupported",
        _ => "Not rated",
    };

    /// <summary>Tiers that mean "most people get it running".</summary>
    public bool TierWorks => Tier is "platinum" or "gold" or "native";
}

/// <summary>
/// ProtonDB's summary and Valve's Steam Deck compatibility report per Steam game, fetched in the
/// background and kept in a small cache (a few days, so ratings stay fresh without a request per
/// scan). Both are public, unauthenticated endpoints; nothing about the player is sent.
/// </summary>
public sealed partial class CompatReports
{
    public static readonly TimeSpan Fresh = TimeSpan.FromDays(3);
    public static readonly TimeSpan RetryFailed = TimeSpan.FromHours(6);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _file;
    private readonly HttpClient _http;
    private readonly object _lock = new();
    private Dictionary<string, CompatInfo> _cache;

    /// <summary>ProtonDB summary URL ({0} = app id); DECKWRENCH_PROTONDB_API overrides it (tests).</summary>
    public static string ProtonDbUrl =>
        Environment.GetEnvironmentVariable("DECKWRENCH_PROTONDB_API") is { Length: > 0 } f ? f : "https://www.protondb.com/api/v1/reports/summaries/{0}.json";

    /// <summary>Steam's Deck compatibility report URL ({0} = app id); DECKWRENCH_DECK_API overrides it (tests).</summary>
    public static string DeckUrl =>
        Environment.GetEnvironmentVariable("DECKWRENCH_DECK_API") is { Length: > 0 } f ? f : "https://store.steampowered.com/saleaction/ajaxgetdeckappcompatibilityreport?nAppID={0}&l=english";

    public CompatReports(string file, HttpClient http)
    {
        _file = file;
        _http = http;
        _cache = Load(file);
    }

    public CompatInfo? Get(string appId)
    {
        lock (_lock)
        {
            return _cache.GetValueOrDefault(appId);
        }
    }

    /// <summary>App ids that have no entry yet or an old one.</summary>
    public IReadOnlyList<string> Stale(IEnumerable<string> appIds, DateTime now)
    {
        lock (_lock)
        {
            return appIds.Distinct().Where(id => !_cache.TryGetValue(id, out var c) || now - c.Fetched > (c.Failed ? RetryFailed : Fresh)).ToList();
        }
    }

    /// <summary>Fetches the given games (a few at a time) and saves the cache. Returns how many changed.</summary>
    public async Task<int> Refresh(IReadOnlyList<string> appIds, CancellationToken ct = default)
    {
        if (appIds.Count == 0)
        {
            return 0;
        }
        var changed = 0;
        using var gate = new SemaphoreSlim(4);
        var tasks = appIds.Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var info = await Fetch(id, ct).ConfigureAwait(false);
                lock (_lock)
                {
                    var old = _cache.GetValueOrDefault(id);
                    if (old == null || old.Tier != info.Tier || old.Deck != info.Deck || old.Reports != info.Reports || old.Failed != info.Failed)
                    {
                        changed++;
                    }
                    // A failed fetch keeps the last good answer (with the new time, so it isn't retried at once).
                    _cache[id] = info.Failed && old is { Failed: false } ? old with { Fetched = info.Fetched - Fresh + RetryFailed } : info;
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        Save();
        return changed;
    }

    private async Task<CompatInfo> Fetch(string appId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        string? protonDb = null, deck = null;
        var failed = false;
        try
        {
            using var r = await _http.GetAsync(string.Format(CultureInfo.InvariantCulture, ProtonDbUrl, appId), ct).ConfigureAwait(false);
            // 404: ProtonDB has no reports for it (not a failure).
            protonDb = r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
            failed |= !r.IsSuccessStatusCode && r.StatusCode != System.Net.HttpStatusCode.NotFound;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            failed = true;
        }
        try
        {
            deck = await _http.GetStringAsync(string.Format(CultureInfo.InvariantCulture, DeckUrl, appId), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            failed = true;
        }
        return Parse(appId, protonDb, deck, now) with { Failed = failed };
    }

    /// <summary>Reads ProtonDB's summary and Steam's Deck report (either may be null or malformed).</summary>
    public static CompatInfo Parse(string appId, string? protonDb, string? deck, DateTime fetched)
    {
        string? tier = null, trending = null;
        var reports = 0;
        if (protonDb != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(protonDb);
                var root = doc.RootElement;
                tier = Str(root, "tier")?.ToLowerInvariant();
                trending = Str(root, "trendingTier")?.ToLowerInvariant();
                reports = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : 0;
            }
            catch (JsonException)
            {
            }
        }
        var rating = DeckRating.Unknown;
        var notes = new List<string>();
        if (deck != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(deck);
                if (doc.RootElement.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Object)
                {
                    rating = res.TryGetProperty("resolved_category", out var c) && c.TryGetInt32(out var cat) ? cat switch
                    {
                        1 => DeckRating.Unsupported,
                        2 => DeckRating.Playable,
                        3 => DeckRating.Verified,
                        _ => DeckRating.Unknown,
                    } : DeckRating.Unknown;
                    if (res.TryGetProperty("resolved_items", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var it in items.EnumerateArray())
                        {
                            if (Str(it, "loc_token") is { } token && NoteFor(token) is { } note)
                            {
                                notes.Add(note);
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }
        }
        return new CompatInfo { AppId = appId, Tier = tier, Trending = trending, Reports = reports, Deck = rating, DeckNotes = notes, Fetched = fetched };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    // Valve's test results come as tokens ("#SteamDeckVerified_TestResult_TextInputDoesNotHaveGamepadUI").
    private static readonly Dictionary<string, string> KnownNotes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DefaultControllerConfigFullySupported"] = "Full controller support",
        ["ControllerGlyphsMatchDeckDevice"] = "Shows Steam Deck button icons",
        ["InterfaceTextIsLegible"] = "Text is legible",
        ["DefaultConfigurationIsPerformant"] = "Runs well with default settings",
        ["DefaultControllerConfigNotFullyFunctional"] = "Some controls need the touchscreen or a keyboard",
        ["SomeInGameTextSmall"] = "Some text is small and hard to read",
        ["TextInputDoesNotHaveGamepadUI"] = "Typing text needs Steam's keyboard (Steam + X)",
        ["ExternalControllersNotSupportedLocalMultiplayer"] = "Local multiplayer needs extra controllers that aren't supported",
        ["LauncherInteractionIssues"] = "Its launcher needs the touchscreen",
        ["DeckGlyphsNotDisplayedOnInitialLaunch"] = "Shows other controller icons at first",
        ["ControllerGlyphsDoNotMatchDeckDevice"] = "Shows other controller icons",
        ["DefaultConfigurationIsNotPerformant"] = "Needs lower settings to run well",
        ["GameOrCompatToolNotSupported"] = "Doesn't run on SteamOS (anti-cheat or launcher)",
        ["UnsupportedAntiCheatConfiguration"] = "Its anti-cheat doesn't support SteamOS",
        ["SingleplayerGameplayNotSupported"] = "Single-player doesn't work",
    };

    private static string? NoteFor(string token)
    {
        var key = token.TrimStart('#');
        var i = key.LastIndexOf('_');
        key = i >= 0 ? key[(i + 1)..] : key;
        if (KnownNotes.TryGetValue(key, out var known))
        {
            return known;
        }
        if (key.Length == 0)
        {
            return null;
        }
        // Unknown tokens: "SomeNewTestResult" → "Some new test result".
        var words = Words().Replace(key, " $1").Trim().ToLowerInvariant();
        return char.ToUpperInvariant(words[0]) + words[1..];
    }

    [GeneratedRegex(@"(?<!^)([A-Z])")]
    private static partial Regex Words();

    private static Dictionary<string, CompatInfo> Load(string file)
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<List<CompatInfo>>(File.ReadAllText(file), Json) is { } list)
            {
                return list.Where(c => c.AppId != null).GroupBy(c => c.AppId).ToDictionary(g => g.Key, g => g.Last());
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
        }
        return new();
    }

    private void Save()
    {
        List<CompatInfo> all;
        lock (_lock)
        {
            all = _cache.Values.OrderBy(c => c.AppId, StringComparer.Ordinal).ToList();
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
