using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeckWrench.Core.Proton;

/// <summary>One GE-Proton release on GitHub.</summary>
public sealed record GeRelease
{
    /// <summary>"GE-Proton9-20": also the folder and the CompatToolMapping name.</summary>
    public required string Tag { get; init; }

    public DateTime Published { get; init; }

    /// <summary>The .tar.gz asset.</summary>
    public required string TarballUrl { get; init; }

    public long TarballSize { get; init; }

    /// <summary>The .sha512sum asset, when the release has one.</summary>
    public string? ChecksumUrl { get; init; }

    public string Notes { get; init; } = "";
}

/// <summary>
/// GE-Proton from GloriousEggroll's GitHub releases: list, download (checksum-verified), install
/// into compatibilitytools.d, and remove. Steam picks new tools up when it next starts.
/// </summary>
public sealed class GeProton
{
    public const string Repo = "GloriousEggroll/proton-ge-custom";

    /// <summary>The releases API; DECKWRENCH_GE_API overrides it (offline smoke tests).</summary>
    public static string ReleasesApi =>
        Environment.GetEnvironmentVariable("DECKWRENCH_GE_API") is { Length: > 0 } fake ? fake : $"https://api.github.com/repos/{Repo}/releases?per_page=30";

    private readonly HttpClient _http;

    public GeProton(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<GeRelease>> ListAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(ReleasesApi, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseReleases(json);
    }

    /// <summary>Parses the GitHub releases list, keeping stable GE-Proton releases with a tarball.</summary>
    public static IReadOnlyList<GeRelease> ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<GeRelease>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True ||
                r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            {
                continue;
            }
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (ProtonNames.GeVersion(tag) == null)
            {
                continue;
            }
            string? tar = null, sum = null;
            long size = 0;
            foreach (var a in r.TryGetProperty("assets", out var assets) ? assets.EnumerateArray() : default)
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString();
                if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                {
                    tar = url;
                    size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                }
                else if (name.EndsWith(".sha512sum", StringComparison.OrdinalIgnoreCase))
                {
                    sum = url;
                }
            }
            if (tar == null)
            {
                continue;
            }
            list.Add(new GeRelease
            {
                Tag = tag,
                TarballUrl = tar,
                TarballSize = size,
                ChecksumUrl = sum,
                Published = r.TryGetProperty("published_at", out var p) && p.TryGetDateTime(out var d) ? d : default,
                Notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            });
        }
        return list.OrderByDescending(x => ProtonNames.GeVersion(x.Tag)).ToList();
    }

    /// <summary>
    /// Downloads, verifies and installs <paramref name="release"/> into <paramref name="toolsDir"/>
    /// (compatibilitytools.d). Progress is 0..1. Returns the installed folder.
    /// </summary>
    public async Task<string> InstallAsync(GeRelease release, string toolsDir, string downloadDir, IProgress<double>? progress, CancellationToken ct)
    {
        var dest = Path.Combine(toolsDir, release.Tag);
        if (Directory.Exists(dest))
        {
            throw new InvalidOperationException($"{release.Tag} is already installed.");
        }
        Directory.CreateDirectory(toolsDir);
        Directory.CreateDirectory(downloadDir);
        var tarball = Path.Combine(downloadDir, release.Tag + ".tar.gz");
        await DownloadAsync(release.TarballUrl, tarball, release.TarballSize, p => progress?.Report(p * 0.8), ct).ConfigureAwait(false);
        try
        {
            if (release.ChecksumUrl != null)
            {
                var expected = ParseChecksum(await _http.GetStringAsync(release.ChecksumUrl, ct).ConfigureAwait(false));
                var actual = await Sha512Async(tarball, ct).ConfigureAwait(false);
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"The download of {release.Tag} is damaged (checksum mismatch). Nothing was installed.");
                }
            }
            progress?.Report(0.85);
            ct.ThrowIfCancellationRequested();
            Extract(tarball, toolsDir, release.Tag);
            progress?.Report(1);
            return dest;
        }
        finally
        {
            TryDelete(tarball);
        }
    }

    /// <summary>
    /// Unpacks a GE-Proton tarball into a hidden staging folder next to the destination, then moves
    /// its single top-level folder into place, so Steam never sees a half-extracted tool.
    /// </summary>
    public static void Extract(string tarball, string toolsDir, string tag)
    {
        var staging = Path.Combine(toolsDir, $".deckwrench-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            using (var file = File.OpenRead(tarball))
            using (var gz = new GZipStream(file, CompressionMode.Decompress))
            {
                // Refuses entries that would land outside the staging folder.
                Storage.SafeTar.Extract(gz, staging);
            }
            var top = Directory.GetDirectories(staging);
            if (top.Length != 1 || Directory.GetFiles(staging).Length != 0 || !File.Exists(Path.Combine(top[0], "compatibilitytool.vdf")))
            {
                throw new InvalidDataException($"{Path.GetFileName(tarball)} doesn't look like a GE-Proton release. Nothing was installed.");
            }
            Directory.Move(top[0], Path.Combine(toolsDir, tag));
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The hash from a "&lt;hash&gt;  &lt;file&gt;" line.</summary>
    public static string ParseChecksum(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? throw new InvalidDataException("Empty checksum file.");

    public static async Task<string> Sha512Async(string file, CancellationToken ct)
    {
        await using var s = File.OpenRead(file);
        var hash = await SHA512.HashDataAsync(s, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task DownloadAsync(string url, string target, long expectedSize, Action<double> progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? expectedSize;
        var tmp = target + ".part";
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total > 0)
                    {
                        progress(Math.Min(1, (double)done / total));
                    }
                }
            }
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static void TryDelete(string f)
    {
        try
        {
            File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
