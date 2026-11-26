using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeckWrench.Core.Tricks;

/// <summary>One curated winetricks verb.</summary>
public sealed record TrickVerb
{
    public required string Id { get; init; }
    public string Category { get; init; } = "";
    public string Label { get; init; } = "";
    public string Help { get; init; } = "";
}

/// <summary>
/// The verbs DeckWrench offers: the curated list (data/tricks.json, built in) plus the user's own
/// from tricks.json in the config folder (same shape: {"verbs": [{"id", "category", "label", "help"}]}).
/// </summary>
public static class TricksCatalog
{
    public const string UserCategory = "Your verbs";

    private static IReadOnlyList<TrickVerb>? _verbs;

    /// <summary>The built-in verbs.</summary>
    public static IReadOnlyList<TrickVerb> Verbs => _verbs ??= Load();

    // winetricks verbs and settings: vcrun2022, d3dcompiler_47, win10, renderer=vulkan, mfc140...
    private static readonly Regex VerbPattern = new("^[a-z0-9][a-z0-9_.+=-]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A word winetricks would take as a verb, never an option: lowercase letters, digits and
    /// _ . + = -, not starting with '-' and no spaces, quotes or shell characters.
    /// </summary>
    public static bool IsVerb(string v) => VerbPattern.IsMatch(v);

    private static readonly char[] Separators = [' ', '\t', ',', '\n'];

    /// <summary>Splits typed text into verbs; the error says which word isn't one.</summary>
    public static (IReadOnlyList<string> Verbs, string? Error) ParseTyped(string? text)
    {
        var words = (text ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToList();
        if (words.Count == 0)
        {
            return ([], "Type at least one winetricks verb, like vcrun2022 or corefonts.");
        }
        var bad = words.FirstOrDefault(w => !IsVerb(w));
        if (bad != null)
        {
            return ([], bad.StartsWith('-')
                ? $"\"{bad}\" is an option, not a verb. DeckWrench only passes verbs."
                : $"\"{bad}\" isn't a winetricks verb: use lowercase letters, digits and _ . + = - only.");
        }
        return (words, null);
    }

    /// <summary>Built-in verbs plus the user's (theirs replace a built-in one with the same id).</summary>
    public static IReadOnlyList<TrickVerb> All(string? userFile)
    {
        var user = LoadUser(userFile, out _);
        if (user.Count == 0)
        {
            return Verbs;
        }
        var ids = user.Select(v => v.Id).ToHashSet();
        return Verbs.Where(v => !ids.Contains(v.Id)).Concat(user).ToList();
    }

    /// <summary>The user's verbs; invalid entries are skipped and named in <paramref name="problems"/>.</summary>
    public static IReadOnlyList<TrickVerb> LoadUser(string? file, out IReadOnlyList<string> problems)
    {
        var bad = new List<string>();
        problems = bad;
        if (file == null || !File.Exists(file))
        {
            return [];
        }
        List<TrickVerb>? raw;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            raw = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("verbs", out var verbs)
                ? verbs.Deserialize<List<TrickVerb>>(Json)
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            bad.Add($"{Path.GetFileName(file)}: {e.Message}");
            return [];
        }
        if (raw == null)
        {
            bad.Add($"{Path.GetFileName(file)}: expected {{\"verbs\": [...]}}");
            return [];
        }
        var list = new List<TrickVerb>();
        foreach (var v in raw)
        {
            var id = (v?.Id ?? "").Trim();
            if (!IsVerb(id))
            {
                bad.Add($"\"{id}\" isn't a winetricks verb, skipped");
                continue;
            }
            if (list.Any(x => x.Id == id))
            {
                continue;
            }
            list.Add(new TrickVerb
            {
                Id = id,
                Category = string.IsNullOrWhiteSpace(v!.Category) ? UserCategory : v.Category.Trim(),
                Label = string.IsNullOrWhiteSpace(v.Label) ? id : v.Label.Trim(),
                Help = v.Help?.Trim() ?? "",
            });
        }
        return list;
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private static IReadOnlyList<TrickVerb> Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("tricks.json")
            ?? throw new InvalidOperationException("tricks.json is missing from the build.");
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.GetProperty("verbs").Deserialize<List<TrickVerb>>(Json)!;
    }

    public static TrickVerb? Find(string id) => Verbs.FirstOrDefault(v => v.Id == id);
}

/// <summary>How to run protontricks here: the native command, or the Flatpak (SteamOS: from Discover).</summary>
public sealed record TricksRunner(string Exe, IReadOnlyList<string> Prefix, string Description)
{
    /// <summary>The command for installing <paramref name="verbs"/> quietly into a Steam app's prefix.</summary>
    public (string Exe, List<string> Args) Command(string appId, IEnumerable<string> verbs)
    {
        var args = Prefix.ToList();
        args.Add(appId);
        args.Add("-q");  // unattended: winetricks answers its own installers
        foreach (var v in verbs)
        {
            if (!TricksCatalog.IsVerb(v))
            {
                throw new ArgumentException($"\"{v}\" isn't a winetricks verb.");
            }
            args.Add(v);
        }
        return (Exe, args);
    }

    /// <summary>Finds protontricks on PATH, else the Flatpak; null when neither is installed (or on Windows).</summary>
    public static TricksRunner? Find(string home, Func<string, string?>? which = null)
    {
        if (Platform.IsWindows)
        {
            return null;
        }
        which ??= Which;
        if (which("protontricks") is { } native)
        {
            return new TricksRunner(native, [], "protontricks");
        }
        var flatpakInstalled = new[]
        {
            Path.Combine(home, ".local/share/flatpak/app/com.github.Matoking.protontricks"),
            "/var/lib/flatpak/app/com.github.Matoking.protontricks",
        }.Any(Directory.Exists);
        if (flatpakInstalled && which("flatpak") is { } flatpak)
        {
            return new TricksRunner(flatpak, ["run", "com.github.Matoking.protontricks"], "protontricks (Flatpak)");
        }
        return null;
    }

    public static string? Which(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
}
