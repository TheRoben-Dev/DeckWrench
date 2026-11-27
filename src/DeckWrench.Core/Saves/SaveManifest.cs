using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DeckWrench.Core.Saves;

/// <summary>One save or config location from the manifest: a path pattern with placeholders and when it applies.</summary>
public sealed record SaveRule(string Pattern, IReadOnlyList<string> Tags, IReadOnlyList<(string? Os, string? Store)> When)
{
    /// <summary>Applies to this OS ("windows" for Proton games, "linux" for native ones) from Steam.</summary>
    public bool AppliesTo(string os) =>
        When.Count == 0 || When.Any(w => (w.Os == null || w.Os == os) && (w.Store == null || w.Store == "steam"));
}

/// <summary>A game's entry: its name in the manifest and its rules.</summary>
public sealed record ManifestGame(string Name, IReadOnlyList<SaveRule> Rules);

/// <summary>
/// Reads Ludusavi's community manifest (github.com/mtkennerly/ludusavi-manifest): where thousands of
/// games keep their saves, by Steam app id. The file is big (about 18 MB of YAML), so it is read one
/// game at a time and only the games asked for are kept.
/// </summary>
public static class SaveManifest
{
    /// <summary>The manifest's URL; DECKWRENCH_SAVE_MANIFEST overrides it (tests).</summary>
    public static string Url =>
        Environment.GetEnvironmentVariable("DECKWRENCH_SAVE_MANIFEST") is { Length: > 0 } f ? f : "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";

    public static readonly TimeSpan Fresh = TimeSpan.FromDays(7);

    private sealed class Entry
    {
        public Dictionary<string, FileEntry?>? Files { get; set; }
        public SteamEntry? Steam { get; set; }
        public IdEntry? Id { get; set; }
    }

    private sealed class FileEntry
    {
        public List<string>? Tags { get; set; }
        public List<WhenEntry>? When { get; set; }
    }

    private sealed class WhenEntry
    {
        public string? Os { get; set; }
        public string? Store { get; set; }
    }

    private sealed class SteamEntry
    {
        public long? Id { get; set; }
    }

    private sealed class IdEntry
    {
        public List<long>? SteamExtra { get; set; }
    }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>The entries for the given Steam app ids (also matching "steamExtra" ids), read from <paramref name="yaml"/>.</summary>
    public static IReadOnlyDictionary<string, ManifestGame> Read(TextReader yaml, IReadOnlySet<string> appIds, CancellationToken ct = default)
    {
        var found = new Dictionary<string, ManifestGame>();
        var parser = new Parser(yaml);
        parser.Consume<StreamStart>();
        if (!parser.TryConsume<DocumentStart>(out _) || !parser.TryConsume<MappingStart>(out _))
        {
            return found;
        }
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            ct.ThrowIfCancellationRequested();
            var name = parser.Consume<Scalar>().Value;
            Entry? e;
            try
            {
                e = Yaml.Deserialize<Entry?>(parser);
            }
            catch (YamlException)
            {
                continue;  // one odd entry doesn't spoil the rest
            }
            if (e?.Files is not { Count: > 0 } files)
            {
                continue;
            }
            var ids = new List<string>();
            if (e.Steam?.Id is { } id)
            {
                ids.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            ids.AddRange(e.Id?.SteamExtra?.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? []);
            var match = ids.FirstOrDefault(appIds.Contains);
            if (match == null || found.ContainsKey(match))
            {
                continue;
            }
            var rules = files.Select(f => new SaveRule(f.Key, (IReadOnlyList<string>?)f.Value?.Tags ?? [],
                f.Value?.When?.Select(w => (w.Os, w.Store)).ToList() ?? [])).ToList();
            found[match] = new ManifestGame(name, rules);
        }
        return found;
    }

    /// <summary>
    /// The manifest on disk at <paramref name="file"/>, downloaded again when it's older than a week
    /// (an ETag check, so an unchanged manifest isn't downloaded twice). Returns false when there's
    /// no copy at all (offline on first use).
    /// </summary>
    public static async Task<bool> Ensure(string file, HttpClient http, CancellationToken ct = default)
    {
        var info = new FileInfo(file);
        if (info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc < Fresh)
        {
            return true;
        }
        var etagFile = file + ".etag";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url);
            if (info.Exists && File.Exists(etagFile) && File.ReadAllText(etagFile).Trim() is { Length: > 0 } etag)
            {
                req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                return true;
            }
            resp.EnsureSuccessStatusCode();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".part";
            await using (var out_ = File.Create(tmp))
            {
                await resp.Content.CopyToAsync(out_, ct).ConfigureAwait(false);
            }
            File.Move(tmp, file, overwrite: true);
            if (resp.Headers.ETag is { } tag)
            {
                File.WriteAllText(etagFile, tag.ToString());
            }
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return File.Exists(file);
        }
    }
}
