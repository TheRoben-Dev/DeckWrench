using System.Diagnostics;
using DeckWrench.Core;
using DeckWrench.Core.Steam;
using DeckWrench.Host;
using DeckWrench.Host.Toolbox;
using Xunit;

namespace DeckWrench.Core.Tests;

/// <summary>The whole scan and the action catalog against tools/smoke/fake-steam.sh (the smoke test's fake Steam).</summary>
public class ToolboxScanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deckwrench-scan-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _home;

    public ToolboxScanTests() => _home = Path.Combine(_dir, "home");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Core.Prefixes.PrefixBackups.DeleteTree(_dir);
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "DeckWrench.sln")))
        {
            d = d.Parent;
        }
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private AppHost Host()
    {
        Directory.CreateDirectory(_home);
        var psi = new ProcessStartInfo("bash", [Path.Combine(RepoRoot(), "tools", "smoke", "fake-steam.sh"), _home, Path.Combine(_dir, "sd")])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.Environment["HOME"] = _home;
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
        var env = new Dictionary<string, string?> { ["HOME"] = _home, ["XDG_RUNTIME_DIR"] = Path.Combine(_dir, "run") };
        var host = new AppHost(new AppPaths(k => env.GetValueOrDefault(k)));
        host.Scan();
        return host;
    }

    [LinuxFact]
    public void ScanFindsGamesToolsPrefixesCachesLogsAndCleanup()
    {
        var s = Host().State;
        Assert.Equal(["Battle.net", "Cyberpunk 2077", "ELDEN RING", "Hades", "Stardew Valley", "Warframe"], s.Games.Select(g => g.Title).Order());
        Assert.Equal(2, s.Libraries.Count);
        var elden = s.Games.Single(g => g.AppId == "1245620");
        Assert.Equal("GE-Proton9-20", elden.CompatTool);
        Assert.Contains("PROTON_LOG=1", elden.LaunchOptions);
        Assert.NotNull(elden.Prefix);
        Assert.NotNull(elden.ShaderCache);
        Assert.NotNull(elden.Log);
        Assert.Equal("proton_experimental", s.DefaultTool);

        Assert.Contains(s.Tools, t => t.Name == "GE-Proton9-20" && t.Removable);
        Assert.Contains(s.Tools, t => t.Name == "proton_experimental" && !t.Removable);
        Assert.DoesNotContain(s.Tools, t => t.DisplayName.Contains("Runtime", StringComparison.Ordinal));  // SLR isn't a compat tool choice
        Assert.Equal(2, s.GamesUsing(s.Tool("GE-Proton9-20")!).Count);  // ELDEN RING and the Battle.net shortcut

        Assert.Contains(s.Prefixes, p => p.AppId == "271590" && p.Orphan);
        Assert.Contains(s.ShaderCaches, p => p.AppId == "271590" && p.Orphan);
        Assert.Contains(s.Cleanup, c => c.Kind == CleanupKind.OrphanPrefix);
        Assert.Contains(s.Cleanup, c => c.Kind == CleanupKind.OrphanShaderCache);
        Assert.Contains(s.Cleanup, c => c.Kind == CleanupKind.UnusedProton && c.Title.Contains("GE-Proton8-25"));
        Assert.True(s.Reclaimable > 0);
        Assert.NotEmpty(s.Drives);
        Assert.True(s.Drives[0].Games > 0);
    }

    [LinuxFact]
    public void EveryDestructiveActionShowsWhatItRemovesFirst()
    {
        var host = Host();
        var s = host.State;
        var a = host.Actions;
        var all = new List<ToolAction>();
        all.AddRange(a.Global());
        all.AddRange(a.ForProtonPage());
        all.AddRange(a.ForShaderPage());
        all.AddRange(a.ForLogsPage());
        all.AddRange(a.ForStoragePage());
        foreach (var g in s.Games)
        {
            all.AddRange(a.ForGame(g));
        }
        foreach (var t in s.Tools)
        {
            all.AddRange(a.ForTool(t));
        }
        foreach (var p in s.Prefixes)
        {
            all.AddRange(a.ForPrefix(p));
        }
        foreach (var l in s.Logs)
        {
            all.AddRange(a.ForLog(l));
        }
        Assert.Contains(all, x => x.Destructive);
        foreach (var x in all.Where(x => x.Destructive && x.Unavailable == null))
        {
            Assert.True(x.Plan != null, $"{x.Id} '{x.Label}' removes data without a confirmation");
        }
        // The orphan prefix's delete lists its contents with sizes.
        var orphan = s.Prefixes.Single(p => p.AppId == "271590");
        var delete = a.ForPrefix(orphan).Single(x => x.Id == ActionId.DeletePrefix);
        var plan = delete.Plan!(null, CancellationToken.None);
        Assert.True(plan.TotalSize > 0);
        Assert.NotEmpty(plan.Contents);
        Assert.Null(plan.Backup);
    }

    [LinuxFact]
    public async Task ResetMakesABackupBeforeDeleting()
    {
        var host = Host();
        var elden = host.State.Games.Single(g => g.AppId == "1245620");
        var reset = host.Actions.ForGame(elden).Single(x => x.Id == ActionId.ResetPrefix);
        Assert.NotNull(reset.Plan!(null, CancellationToken.None).Backup);
        await reset.Run!(new ActionContext(null, new Progress<double>(), _ => { }, CancellationToken.None));
        Assert.False(Directory.Exists(elden.Prefix!.Dir));
        Assert.Contains(host.Backups.List("1245620"), b => b.Reason == "before reset");
    }

    [LinuxFact]
    public async Task CompatToolAndLaunchOptionsAreWrittenWhenSteamIsClosed()
    {
        var host = Host();
        var hades = host.State.Games.Single(g => g.Title == "Hades");
        var set = host.Actions.ForGame(hades).Single(x => x.Id == ActionId.SetCompatTool);
        await set.Run!(new ActionContext("GE-Proton9-20", new Progress<double>(), _ => { }, CancellationToken.None));
        var steam = new SteamConfig(host.State.SteamRoot!);
        Assert.Equal("GE-Proton9-20", steam.CompatToolMapping()[hades.AppId]);

        var edit = host.Actions.ForGame(hades).Single(x => x.Id == ActionId.EditLaunchOptions);
        await edit.Run!(new ActionContext("gamemoderun %command%", new Progress<double>(), _ => { }, CancellationToken.None));
        Assert.Equal("gamemoderun %command%", steam.LaunchOptions()[hades.AppId]);
        Assert.Empty(host.Pending.All);
    }

    [LinuxFact]
    public async Task NonSteamShortcutsAreGamesWithProtonSettings()
    {
        var host = Host();
        var s = host.State;
        var bnet = s.Games.Single(g => g.Title == "Battle.net");
        Assert.True(bnet.IsShortcut);
        Assert.True(uint.Parse(bnet.AppId) >= 0x80000000u);
        Assert.Equal("GE-Proton9-20", bnet.CompatTool);
        Assert.NotNull(bnet.Prefix);
        Assert.False(bnet.Prefix!.Orphan);
        Assert.NotNull(bnet.ShaderCache);
        Assert.NotNull(bnet.Log);  // steam-<64-bit game id>.log
        Assert.Contains("Unhandled exception", string.Join("\n", Core.Logs.ProtonLogs.Tail(bnet.Log!.Path)));
        Assert.Single(s.GamesUsing(s.Tool("GE-Proton9-20")!), g => g.IsShortcut);

        var actions = host.Actions.ForGame(bnet);
        Assert.Contains(actions, a => a.Id == ActionId.SetCompatTool);
        Assert.Contains(actions, a => a.Id == ActionId.ResetPrefix);
        Assert.DoesNotContain(actions, a => a.Id is ActionId.MoveGame or ActionId.OpenInSteam);  // no store page, not in a library folder

        // Launch options go to shortcuts.vdf (Steam closed: written straight away).
        var presets = actions.Single(a => a.Id == ActionId.ToggleProtonLog);
        await presets.Run!(new ActionContext(null, new Progress<double>(), _ => { }, CancellationToken.None));
        var file = Core.Steam.SteamShortcuts.Load(Path.GetDirectoryName(bnet.Install.ShortcutsFile)!);
        Assert.Equal("PROTON_LOG=1 %command%", file.Find(uint.Parse(bnet.AppId))!.Str("LaunchOptions"));
        Assert.True(File.Exists(bnet.Install.ShortcutsFile + ".deckwrench-bak"));
        host.Scan();
        Assert.Equal("PROTON_LOG=1 %command%", host.State.Games.Single(g => g.Title == "Battle.net").LaunchOptions);

        // Queued while Steam runs, then applied like any other change.
        host.Pending.Add(new PendingChange { Kind = PendingKind.ShortcutLaunchOptions, AppId = bnet.AppId, Value = "-x", Title = "Battle.net", Target = bnet.Install.ShortcutsFile });
        Assert.Equal(1, host.ApplyPendingNow());
        Assert.Equal("-x", Core.Steam.SteamShortcuts.Load(Path.GetDirectoryName(bnet.Install.ShortcutsFile)!).Find(uint.Parse(bnet.AppId))!.Str("LaunchOptions"));
    }

    [LinuxFact]
    public void PendingChangesKeepTheNewestValueAndApply()
    {
        var host = Host();
        host.Pending.Add(new PendingChange { Kind = PendingKind.CompatTool, AppId = "1091500", Value = "proton_9", Title = "Cyberpunk 2077" });
        host.Pending.Add(new PendingChange { Kind = PendingKind.LaunchOptions, AppId = "1091500", Value = "-a", Title = "Cyberpunk 2077" });
        host.Pending.Add(new PendingChange { Kind = PendingKind.LaunchOptions, AppId = "1091500", Value = "-b", Title = "Cyberpunk 2077" });
        Assert.Equal(2, host.Pending.All.Count);  // the newest value for the same setting replaces the older one
        Assert.Equal(2, host.ApplyPendingNow());
        var steam = new SteamConfig(host.State.SteamRoot!);
        Assert.Equal("proton_9", steam.CompatToolMapping()["1091500"]);
        Assert.Equal("-b", steam.LaunchOptions()["1091500"]);
        Assert.Empty(host.Pending.All);
    }
}
