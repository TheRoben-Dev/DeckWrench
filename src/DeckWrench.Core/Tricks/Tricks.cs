using System.Reflection;
using System.Text.Json;

namespace DeckWrench.Core.Tricks;

/// <summary>One curated winetricks verb.</summary>
public sealed record TrickVerb
{
    public required string Id { get; init; }
    public string Category { get; init; } = "";
    public string Label { get; init; } = "";
    public string Help { get; init; } = "";
}

/// <summary>The verbs DeckWrench offers (data/tricks.json, built in).</summary>
public static class TricksCatalog
{
    private static IReadOnlyList<TrickVerb>? _verbs;

    public static IReadOnlyList<TrickVerb> Verbs => _verbs ??= Load();

    private static IReadOnlyList<TrickVerb> Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("tricks.json")
            ?? throw new InvalidOperationException("tricks.json is missing from the build.");
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.GetProperty("verbs").Deserialize<List<TrickVerb>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
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
            if (TricksCatalog.Find(v) == null)
            {
                throw new ArgumentException($"\"{v}\" isn't one of DeckWrench's verbs.");
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
