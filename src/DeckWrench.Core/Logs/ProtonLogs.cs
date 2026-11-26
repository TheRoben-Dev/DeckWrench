using System.Text;
using System.Text.RegularExpressions;

namespace DeckWrench.Core.Logs;

/// <summary>A Proton log file (written when a game runs with PROTON_LOG=1).</summary>
public sealed record ProtonLog
{
    public required string Path { get; init; }
    public required string AppId { get; init; }
    public string? Owner { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
}

public enum LogLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>Something the log suggests doing, with the winetricks verb that fixes it when there is one.</summary>
public sealed record LogHint(string Text, string? Verb);

/// <summary>
/// Finds and reads Proton logs: steam-&lt;appid&gt;.log in the home folder (Proton's default) and in
/// PROTON_LOG_DIR when a game's launch options set one.
/// </summary>
public static partial class ProtonLogs
{
    [GeneratedRegex(@"^steam-(\d+)\.log$")]
    private static partial Regex LogName();

    public static IReadOnlyList<ProtonLog> Scan(IEnumerable<string> dirs, IReadOnlyDictionary<string, string> owners)
    {
        var list = new List<ProtonLog>();
        var seen = new HashSet<string>(Platform.PathComparer);
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "steam-*.log").ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var f in files)
            {
                var m = LogName().Match(System.IO.Path.GetFileName(f));
                if (!m.Success || !seen.Add(PathUtil.RealPath(f)))
                {
                    continue;
                }
                var info = new FileInfo(f);
                list.Add(new ProtonLog
                {
                    Path = f,
                    AppId = m.Groups[1].Value,
                    Owner = owners.GetValueOrDefault(m.Groups[1].Value),
                    Size = info.Length,
                    Modified = info.LastWriteTimeUtc,
                });
            }
        }
        return list.OrderByDescending(l => l.Modified).ToList();
    }

    /// <summary>The last <paramref name="maxLines"/> lines, read from the end so a 2 GB log opens instantly.</summary>
    public static IReadOnlyList<string> Tail(string path, int maxLines = 2000, int maxBytes = 4 << 20)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var start = Math.Max(0, s.Length - maxBytes);
        s.Seek(start, SeekOrigin.Begin);
        using var r = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var text = r.ReadToEnd();
        var lines = text.Split('\n');
        var skipFirst = start > 0 ? 1 : 0;  // the first line is probably cut in half
        return lines.Skip(skipFirst).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(maxLines).ToList();
    }

    /// <summary>Wine's channel prefix: "err:" and crashes are errors, "warn:" warnings, the rest (fixme too) plain.</summary>
    public static LogLevel Classify(string line)
    {
        var body = StripThread(line);
        if (body.StartsWith("err:", StringComparison.Ordinal) || line.Contains("Unhandled exception", StringComparison.Ordinal) ||
            line.Contains("Unhandled page fault", StringComparison.Ordinal) || line.Contains("wine: ", StringComparison.Ordinal) && line.Contains("crash", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Error;
        }
        // "fixme:" lines are Wine noting unimplemented corners: thousands per run, almost never the problem.
        return body.StartsWith("warn:", StringComparison.Ordinal) ? LogLevel.Warn : LogLevel.Info;
    }

    /// <summary>Drops Proton's "1234.567:0040:0044:" timestamp and thread ids before the channel.</summary>
    private static string StripThread(string line) => ThreadPrefix().Replace(line, "", 1);

    // Optional "1234.567:" timestamp, then one or two 4-hex-digit ids. Hex alone would eat the "e" of "err:".
    [GeneratedRegex(@"^(?:\d+\.\d+:)?(?:[0-9a-fA-F]{4}:){1,2}")]
    private static partial Regex ThreadPrefix();

    [GeneratedRegex(@"Library ([A-Za-z0-9_.\-]+\.dll) \(which is needed by", RegexOptions.IgnoreCase)]
    private static partial Regex MissingDll();

    private static readonly (string Prefix, string Verb, string Why)[] DllVerbs =
    [
        ("msvcp140", "vcrun2022", "the Visual C++ 2015-2022 runtime"),
        ("vcruntime140", "vcrun2022", "the Visual C++ 2015-2022 runtime"),
        ("msvcp120", "vcrun2013", "the Visual C++ 2013 runtime"),
        ("msvcr120", "vcrun2013", "the Visual C++ 2013 runtime"),
        ("msvcp110", "vcrun2012", "the Visual C++ 2012 runtime"),
        ("msvcr110", "vcrun2012", "the Visual C++ 2012 runtime"),
        ("msvcp100", "vcrun2010", "the Visual C++ 2010 runtime"),
        ("msvcr100", "vcrun2010", "the Visual C++ 2010 runtime"),
        ("msvcr90", "vcrun2008", "the Visual C++ 2008 runtime"),
        ("msvcr80", "vcrun2005", "the Visual C++ 2005 runtime"),
        ("d3dx9_", "d3dx9", "the DirectX 9 extras"),
        ("d3dcompiler_47", "d3dcompiler_47", "the D3D shader compiler"),
        ("d3dcompiler_43", "d3dcompiler_43", "the older D3D shader compiler"),
        ("xactengine", "xact", "the XACT audio libraries"),
        ("x3daudio", "xact", "the XACT audio libraries"),
        ("physxloader", "physx", "PhysX"),
        ("mscoree", "dotnet48", ".NET Framework"),
    ];

    /// <summary>Plain-language hints from a log: missing DLLs (with the verb that installs them) and crashes.</summary>
    public static IReadOnlyList<LogHint> Hints(IEnumerable<string> lines)
    {
        var hints = new List<LogHint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var crashed = false;
        foreach (var line in lines)
        {
            if (MissingDll().Match(line) is { Success: true } m && seen.Add(m.Groups[1].Value))
            {
                var dll = m.Groups[1].Value;
                var fix = DllVerbs.FirstOrDefault(d => dll.StartsWith(d.Prefix, StringComparison.OrdinalIgnoreCase));
                hints.Add(fix.Verb != null
                    ? new LogHint($"{dll} is missing: install {fix.Why} into the prefix.", fix.Verb)
                    : new LogHint($"{dll} is missing. The game or a mod expects it in its folder or the prefix.", null));
            }
            else if (!crashed && (line.Contains("Unhandled exception", StringComparison.Ordinal) || line.Contains("Unhandled page fault", StringComparison.Ordinal)))
            {
                crashed = true;
                hints.Add(new LogHint("The game crashed (unhandled exception). Try another Proton version, or WineD3D for older DirectX games.", null));
            }
        }
        return hints;
    }
}
