using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DeckWrench.Core;
using DeckWrench.Core.Steam;
using DeckWrench.Core.Storage;
using DeckWrench.Host.Updates;

namespace DeckWrench.Host.Toolbox;

/// <summary>
/// A plain-text (Markdown) report of the system and library: OS, Steam, drives, Proton versions,
/// every game's Proton, launch options, prefix and caches, recent Proton log errors, and what can
/// be cleaned up. For asking for help on Discord or Reddit, or comparing before and after a change.
/// The home folder is written as ~ so the account name stays out of it.
/// </summary>
public static class LibraryReport
{
    public static string Build(ToolboxState s, AppHost host, DateTime? now = null, string? osName = null)
    {
        var home = host.Paths.Home.TrimEnd('/', '\\');
        string P(string? path) => path == null ? "" : home.Length > 1 && path.StartsWith(home, Platform.PathComparison) ? "~" + path[home.Length..] : path;
        string Md(string? text) => (text ?? "").Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void L(string line = "") => sb.Append(line).Append('\n');

        L("# DeckWrench report");
        L();
        L($"- Made: {(now ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm", inv)}");
        L($"- DeckWrench: {AppUpdater.DisplayVersion(AppUpdater.CurrentAppVersion())}");
        L($"- System: {osName ?? OsName()} ({RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})" + (SteamSetup.InGameMode ? ", Game Mode" : ""));
        L($"- Steam: {(s.SteamRoot == null ? "not found" : $"{P(s.SteamRoot)} ({s.SteamPackaging})")}" +
          (s.SteamRoot != null ? (SteamShortcuts.SteamRunning(host.Paths.Home) ? ", running" : ", closed") : ""));
        L($"- Default compatibility tool: {(s.DefaultTool == null ? "Steam's choice" : s.ToolLabel(s.DefaultTool))}");
        L($"- Scanned: {s.ScannedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", inv)}");
        L();

        L("## Drives");
        L();
        foreach (var d in s.Drives)
        {
            L($"- **{d.Drive.Label}** ({P(d.Drive.Mount)}): {DiskSize.Format(d.Drive.Free)} free of {DiskSize.Format(d.Drive.Total)}. " +
              string.Join(", ", d.Breakdown.Where(b => b.Size > 0).Select(b => $"{b.Label.ToLowerInvariant()} {DiskSize.Format(b.Size)}")));
        }
        foreach (var lib in s.Libraries)
        {
            L($"- Steam library: {P(lib)}");
        }
        L();

        L("## Proton versions");
        L();
        L("| Name | Kind | Size | Games |");
        L("|---|---|---:|---:|");
        foreach (var t in s.Tools.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            L($"| {Md(t.DisplayName)} (`{Md(t.Name)}`) | {t.Kind} | {DiskSize.Format(t.Size)} | {s.GamesUsing(t).Count} |");
        }
        L();

        L($"## Games ({s.Games.Count})");
        L();
        L("| Game | Source | Proton | Launch options | Size | Prefix (last Proton) | Shaders | Drive | Last played | Hours |");
        L("|---|---|---|---|---:|---|---:|---|---|---:|");
        foreach (var g in s.Games.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase))
        {
            var source = g.IsSteam ? $"Steam {g.AppId}" : g.IsShortcut ? "Non-Steam" : g.Install.Source.ToString();
            var proton = g.RunsInSteam ? s.ToolLabel(g.EffectiveCompatTool) : "";
            var launch = g.EffectiveLaunchOptions.Length > 0 ? $"`{Md(g.EffectiveLaunchOptions)}`" : "";
            var prefix = g.Prefix is { } p ? DiskSize.Format(p.Size) + (p.ProtonVersion != null ? $" ({Md(p.ProtonVersion)})" : "") : "";
            L($"| {Md(g.Title)} | {source} | {Md(proton)} | {launch} | {DiskSize.Format(g.Size)} | {prefix} | {(g.ShaderCache != null ? DiskSize.Format(g.ShaderCache.Size) : "")} | {g.Drive?.Label ?? ""} | {(g.IsSteam ? PlayStats.Ago(g.LastPlayed, (now ?? DateTime.Now).ToUniversalTime()) : "")} | {PlayStats.Hours(g.PlaytimeMinutes)} |");
        }
        L();

        var notes = s.Games.Select(g => (g.Title, Note: host.Notes.Get(g.Key))).Where(x => x.Note != null).ToList();
        if (notes.Count > 0)
        {
            L("## Your notes");
            L();
            foreach (var (title, note) in notes)
            {
                L($"- **{Md(title)}**: {Md(note)}");
            }
            L();
        }

        if (s.Logs.Count > 0)
        {
            L("## Proton logs");
            L();
            foreach (var l in s.Logs.OrderByDescending(l => l.Modified))
            {
                L($"### {Md(l.Owner ?? "App " + l.AppId)} ({l.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", inv)}, {DiskSize.Format(l.Size)})");
                L();
                var errors = ErrorLines(l.Path, 8);
                if (errors.Count == 0)
                {
                    L("No errors.");
                }
                else
                {
                    L("```");
                    foreach (var e in errors)
                    {
                        L(P(e));
                    }
                    L("```");
                }
                L();
            }
        }

        if (s.Pending.Count > 0)
        {
            L("## Waiting for Steam to close");
            L();
            foreach (var pc in s.Pending)
            {
                L($"- {Md(pc.Describe())}");
            }
            L();
        }

        L("## Can be cleaned up");
        L();
        if (s.Cleanup.Count == 0)
        {
            L("Nothing.");
        }
        foreach (var c in s.Cleanup)
        {
            L($"- {Md(c.Title)}: {DiskSize.Format(c.Size)}");
        }
        L();
        L($"Total: {DiskSize.Format(s.Reclaimable)}. Prefix backups: {s.Backups.Count} ({DiskSize.Format(s.Backups.Sum(b => b.ArchiveSize))}).");
        return sb.ToString();
    }

    /// <summary>The last <paramref name="max"/> distinct error lines of a Proton log (thread ids stripped).</summary>
    public static IReadOnlyList<string> ErrorLines(string path, int max)
    {
        try
        {
            return File.ReadLines(path)
                .Where(l => Core.Logs.ProtonLogs.Classify(l) == Core.Logs.LogLevel.Error)
                .Select(l => l.Trim())
                .Distinct()
                .TakeLast(max)
                .Select(l => l.Length > 300 ? l[..300] + "…" : l)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string OsName()
    {
        try
        {
            if (File.Exists("/etc/os-release"))
            {
                var pretty = File.ReadLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal));
                if (pretty != null)
                {
                    return pretty["PRETTY_NAME=".Length..].Trim('"');
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return RuntimeInformation.OSDescription;
    }

    /// <summary>Writes the report to reports/ in DeckWrench's data folder; returns the file.</summary>
    public static string Save(ToolboxState s, AppHost host)
    {
        var dir = Path.Combine(host.Paths.Data, "reports");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"deckwrench-report-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.md");
        File.WriteAllText(file, Build(s, host));
        return file;
    }
}
