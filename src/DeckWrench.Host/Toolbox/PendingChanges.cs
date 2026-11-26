using System.Diagnostics;
using DeckWrench.Core;
using DeckWrench.Core.Steam;

namespace DeckWrench.Host.Toolbox;

public enum PendingKind
{
    /// <summary>Compatibility tool for one game (value null = Steam default), or for all games (app id "0").</summary>
    CompatTool,

    LaunchOptions,

    /// <summary>Launch options of a non-Steam game, kept in shortcuts.vdf (<see cref="PendingChange.Target"/>).</summary>
    ShortcutLaunchOptions,
}

/// <summary>A Steam setting DeckWrench will write once Steam is closed.</summary>
public sealed record PendingChange
{
    public required PendingKind Kind { get; init; }
    public required string AppId { get; init; }
    public string? Value { get; init; }
    public string Title { get; init; } = "";

    /// <summary>The file the change goes to when it isn't Steam's own config (a shortcuts.vdf).</summary>
    public string? Target { get; init; }

    public DateTime Queued { get; init; } = DateTime.UtcNow;

    public string Describe() => Kind switch
    {
        PendingKind.CompatTool when AppId == "0" => $"Default compatibility tool → {Value ?? "Steam's choice"}",
        PendingKind.CompatTool => $"{Title}: compatibility tool → {Value ?? "Steam default"}",
        _ => $"{Title}: launch options → {(string.IsNullOrEmpty(Value) ? "(none)" : Value)}",
    };
}

/// <summary>
/// Steam keeps config.vdf and localconfig.vdf in memory and writes them back when it exits, so a
/// change made while it runs would be lost. DeckWrench writes straight away when Steam is closed;
/// otherwise the change waits here (pending.json) and is written the moment Steam has exited:
/// when the player restarts Steam from DeckWrench (a helper waits for the exit and writes before
/// Steam starts again), or the next time DeckWrench starts with Steam closed.
/// </summary>
public sealed class PendingChanges
{
    private readonly string _file;
    private readonly object _gate = new();

    public PendingChanges(string file) => _file = file;

    public string FilePath => _file;

    public IReadOnlyList<PendingChange> All
    {
        get
        {
            lock (_gate)
            {
                return AppHost.LoadJson<List<PendingChange>>(_file) ?? [];
            }
        }
    }

    public void Add(PendingChange change)
    {
        lock (_gate)
        {
            var list = AppHost.LoadJson<List<PendingChange>>(_file) ?? [];
            // The newest value for the same setting wins.
            list.RemoveAll(c => c.Kind == change.Kind && c.AppId == change.AppId);
            list.Add(change);
            AppHost.SaveJson(_file, list);
        }
    }

    public void Discard()
    {
        lock (_gate)
        {
            if (File.Exists(_file))
            {
                File.Delete(_file);
            }
        }
    }

    /// <summary>Writes every pending change to Steam's files. Steam must be closed. Returns how many were written.</summary>
    public int Apply(SteamConfig steam)
    {
        lock (_gate)
        {
            var list = AppHost.LoadJson<List<PendingChange>>(_file) ?? [];
            foreach (var c in list)
            {
                Write(steam, c);
                AppLog.Info("applied pending: " + c.Describe());
            }
            if (File.Exists(_file))
            {
                File.Delete(_file);
            }
            return list.Count;
        }
    }

    public static void Write(SteamConfig steam, PendingChange c)
    {
        switch (c.Kind)
        {
            case PendingKind.CompatTool:
                steam.SetCompatTool(c.AppId, c.Value);
                break;
            case PendingKind.LaunchOptions:
                steam.SetLaunchOptions(c.AppId, c.Value ?? "");
                break;
            case PendingKind.ShortcutLaunchOptions:
                ShortcutGames.SetLaunchOptions(c.Target ?? throw new InvalidOperationException($"{c.Title}: no shortcuts file recorded."), c.AppId, c.Value ?? "");
                break;
        }
    }

    /// <summary>
    /// Starts a helper (`deckwrench apply-pending --wait`) outside Steam's process tree, then asks
    /// Steam to exit. The helper writes the changes the moment Steam is gone; in Game Mode the session
    /// starts Steam again by itself, on the desktop <paramref name="restartSteam"/> has the helper do it.
    /// </summary>
    public static void ApplyByRestartingSteam(string cli, bool restartSteam)
    {
        if (!File.Exists(cli))
        {
            throw new InvalidOperationException("The deckwrench helper isn't next to the app (run from a release build), so Steam can't be restarted from here.");
        }
        var args = new List<string> { "apply-pending", "--wait" };
        if (restartSteam)
        {
            args.Add("--restart-steam");
        }
        ProcessStartInfo psi;
        if (!Platform.IsWindows && TricksWhich("systemd-run") is { } systemdRun)
        {
            // A transient user service survives Steam (and everything it started) going away.
            psi = new ProcessStartInfo(systemdRun) { UseShellExecute = false };
            foreach (var a in new[] { "--user", "--collect", "--quiet", "--unit", $"deckwrench-apply-{Environment.ProcessId}", cli })
            {
                psi.ArgumentList.Add(a);
            }
        }
        else if (!Platform.IsWindows && TricksWhich("setsid") is { } setsid)
        {
            psi = new ProcessStartInfo(setsid) { UseShellExecute = false };
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(cli);
        }
        else
        {
            psi = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true };
        }
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using (Process.Start(psi))
        {
        }
        AppLog.Info($"started apply helper ({psi.FileName}), asking Steam to exit");
        SteamSetup.RequestSteamExit();
    }

    private static string? TricksWhich(string exe) => Core.Tricks.TricksRunner.Which(exe);
}
