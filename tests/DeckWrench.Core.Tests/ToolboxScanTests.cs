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
        // Steam leftovers of the uninstalled app only: the installed game's download and Workshop folders stay.
        Assert.Equal([LeftoverKind.Download, LeftoverKind.Temp, LeftoverKind.Workshop], s.Leftovers.Select(l => l.Kind).Order());
        Assert.All(s.Leftovers, l => Assert.Equal("271590", l.AppId));
        Assert.Contains(s.Cleanup, c => c.Kind == CleanupKind.SteamLeftover && c.Title == "Unfinished download of app 271590" && c.Size >= 300L << 20);
        Assert.Contains(s.Cleanup, c => c.Kind == CleanupKind.SteamLeftover && c.Title.StartsWith("Workshop items", StringComparison.Ordinal));
        Assert.DoesNotContain(s.Cleanup, c => c.Path.Contains("1245620", StringComparison.Ordinal) && c.Kind == CleanupKind.SteamLeftover);
        Assert.True(s.Reclaimable > 0);
        Assert.NotEmpty(s.Drives);
        Assert.True(s.Drives[0].Games > 0);
    }

    [LinuxFact]
    public async Task BackUpEveryPrefixThenKeepOnlyTheNewest()
    {
        var host = Host();
        static ActionContext Ctx(string? input) => new(input, new Progress<double>(), _ => { }, CancellationToken.None);
        var all = host.Actions.ForPrefixPage().Single(x => x.Id == ActionId.BackupAllPrefixes);
        var picked = all.Input!.Choices.Where(c => c.Selected).Select(c => c.Id).ToList();
        Assert.NotEmpty(picked);
        Assert.DoesNotContain(host.State.Prefixes.Where(p => p.Orphan), p => picked.Contains(p.Dir));
        await all.Run!(Ctx(string.Join('\n', picked)));
        await all.Run!(Ctx(string.Join('\n', picked)));  // same second: a second archive, not a clash
        Assert.Equal(picked.Count * 2, host.Backups.List().Count);

        host.Scan();
        var prune = host.Actions.ForPrefixPage().Single(x => x.Id == ActionId.PruneBackups);
        Assert.Null(prune.Unavailable);
        var plan = prune.Plan!("1", CancellationToken.None);
        Assert.Equal(picked.Count, plan.Contents.Count);
        Assert.True(plan.TotalSize > 0);
        await prune.Run!(Ctx("1"));
        var left = host.Backups.List();
        Assert.Equal(picked.Count, left.Count);
        Assert.Equal(picked.Count, left.Select(b => b.AppId).Distinct().Count());  // the newest of each stays
    }

    [LinuxFact]
    public async Task TheReportCoversTheLibraryWithoutTheHomePath()
    {
        var host = Host();
        host.Notes.Set("steam:1245620", "GE fixes cutscenes");
        var report = LibraryReport.Build(host.State, host, osName: "SteamOS 3.7");
        Assert.Contains("SteamOS 3.7", report);
        Assert.Contains("| ELDEN RING | Steam 1245620 |", report);
        Assert.Contains("Battle.net", report);
        Assert.Contains("GE-Proton9-20", report);
        Assert.Contains("PROTON_LOG=1", report);
        Assert.Contains("GE fixes cutscenes", report);
        Assert.Contains("MSVCP140.dll", report);  // the fake log's error, under Proton logs
        Assert.Contains("~/", report);
        Assert.DoesNotContain(_home, report);
        Assert.Matches(@"\| ELDEN RING \|.*\| 3 days ago \| 90 h \|", report);  // fake localconfig: 3 days ago, 5432 min

        var save = host.Actions.Global().Single(x => x.Id == ActionId.SaveReport);
        var result = await save.Run!(new ActionContext(null, new Progress<double>(), _ => { }, CancellationToken.None));
        var file = result.Message["Report saved: ".Length..];
        Assert.True(File.Exists(file));
        Assert.StartsWith(Path.Combine(host.Paths.Data, "reports"), file);
    }

    [LinuxFact]
    public void ASecondScanReusesFolderSizes()
    {
        var host = Host();  // first scan measured everything
        var first = host.State;
        Assert.True(File.Exists(Path.Combine(host.Paths.Cache, "sizes.json")));
        host.Scan();
        Assert.Equal(first.Prefixes.Select(p => p.Size), host.State.Prefixes.Select(p => p.Size));
        Assert.Equal(first.Tools.Select(t => t.Size), host.State.Tools.Select(t => t.Size));
        // Scan again (the action) measures everything afresh.
        var rescan = host.Actions.Global().Single(x => x.Id == ActionId.Rescan);
        rescan.Run!(new ActionContext(null, new Progress<double>(), _ => { }, CancellationToken.None)).Wait();
        host.Scan();
        Assert.Equal(first.Prefixes.Select(p => p.Size), host.State.Prefixes.Select(p => p.Size));
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
        all.AddRange(a.ForPrefixPage());
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
    public async Task OneProtonForSeveralGamesAndCopiedLaunchOptions()
    {
        var host = Host();
        static ActionContext Ctx(string? input) => new(input, new Progress<double>(), _ => { }, CancellationToken.None);
        var s = host.State;
        var ge9 = s.Tool("GE-Proton9-20")!;
        var use = host.Actions.ForTool(ge9).Single(x => x.Id == ActionId.UseToolForGames);
        var ticked = use.Input!.Choices.Where(c => c.Selected).Select(c => c.Id).ToList();
        Assert.Equal(2, ticked.Count);  // ELDEN RING and the Battle.net shortcut
        var elden = s.Games.Single(g => g.AppId == "1245620");
        var hades = s.Games.Single(g => g.Title == "Hades");
        var cyberpunk = s.Games.Single(g => g.Title == "Cyberpunk 2077");
        // Tick Hades and Cyberpunk, untick ELDEN RING.
        var input = ticked.Where(k => k != elden.Key).Append(hades.Key).Append(cyberpunk.Key);
        var result = await use.Run!(Ctx(string.Join('\n', input)));
        Assert.Contains("2 games to GE-Proton9-20", result.Message);
        Assert.Contains("1 back to Steam's default", result.Message);
        var mapping = new SteamConfig(s.SteamRoot!).CompatToolMapping();
        Assert.Equal("GE-Proton9-20", mapping[hades.AppId]);
        Assert.Equal("GE-Proton9-20", mapping[cyberpunk.AppId]);
        Assert.False(mapping.TryGetValue(elden.AppId, out var e) && e != null && e.Length > 0, $"ELDEN RING still maps to {e}");

        host.Scan();
        hades = host.State.Games.Single(g => g.Title == "Hades");
        var copy = host.Actions.ForGame(hades).Single(x => x.Id == ActionId.CopyLaunchOptions);
        Assert.Contains(copy.Input!.Choices, c => c.Id == cyberpunk.Key);
        Assert.DoesNotContain(copy.Input.Choices, c => c.Id == hades.Key);
        await copy.Run!(Ctx(cyberpunk.Key));
        Assert.Equal("-skipStartScreen", new SteamConfig(s.SteamRoot!).LaunchOptions()[hades.AppId]);
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

    [LinuxFact]
    public void SteamGamesCarryLastPlayedAndPlaytime()
    {
        var host = Host();
        var elden = host.State.Games.Single(g => g.AppId == "1245620");
        Assert.Equal(5432, elden.PlaytimeMinutes);
        Assert.Equal("Last played 3 days ago · 90 h in total", PlayStats.Describe(elden, DateTime.UtcNow));
        var cp = host.State.Games.Single(g => g.AppId == "1091500");
        Assert.Equal("Last played a year ago · 1.6 h in total", PlayStats.Describe(cp, DateTime.UtcNow));
        var never = host.State.Games.First(g => g.IsSteam && g.AppId is not "1245620" and not "1091500");
        Assert.Equal("Never played", PlayStats.Describe(never, DateTime.UtcNow));
    }

    [Theory]
    [InlineData(0, "today")]
    [InlineData(1, "yesterday")]
    [InlineData(45, "45 days ago")]
    [InlineData(200, "6 months ago")]
    [InlineData(400, "a year ago")]
    [InlineData(800, "2 years ago")]
    public void LastPlayedWording(int daysAgo, string expected)
    {
        var now = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, PlayStats.Ago(now.AddDays(-daysAgo).AddMinutes(-5), now));
        Assert.Equal("never", PlayStats.Ago(null, now));
        Assert.Equal("", PlayStats.Hours(0));
        Assert.Equal("45 min", PlayStats.Hours(45));
        Assert.Equal("120 h", PlayStats.Hours(7200));
    }

    [LinuxFact]
    public async Task UninstallBacksUpThePrefixAndHandsOverToSteam()
    {
        var host = Host();
        var elden = host.State.Games.Single(g => g.AppId == "1245620");
        var actions = host.Actions.ForGame(elden);
        Assert.Contains(actions, a => a.Id == ActionId.PlayGame);
        Assert.Contains(actions, a => a.Id == ActionId.VerifyGameFiles);
        var uninstall = actions.Single(a => a.Id == ActionId.UninstallGame);
        Assert.True(uninstall.Destructive);
        var plan = uninstall.Plan!(null, CancellationToken.None);
        Assert.Equal(3, plan.Contents.Count);  // game, prefix, shader cache
        Assert.Contains(plan.Contents, c => c.Name.StartsWith("Proton prefix"));
        Assert.NotNull(plan.Backup);

        await uninstall.Run!(new ActionContext(null, new Progress<double>(), _ => { }, CancellationToken.None));
        var backup = Assert.Single(host.Backups.List("1245620"));
        Assert.Equal("before uninstall", backup.Reason);
        Assert.True(Directory.Exists(elden.Prefix!.Dir));  // Steam does the removal, not us
    }

    [LinuxFact]
    public void CheckupFlagsLogProblemsCleanupAndMissingBackupsWorstFirst()
    {
        var host = Host();
        var f = host.Findings;
        Assert.Equal(FindingLevel.Problem, f[0].Level);
        Assert.True(f.Select(x => x.Level).SequenceEqual(f.Select(x => x.Level).Order()), "not ordered worst first");

        var elden = Assert.Single(f, x => x.Key == "log:steam:1245620");
        Assert.Equal("ELDEN RING: MSVCP140.dll is missing", elden.Title);
        var fix = Assert.Single(elden.Actions, a => a.Id == ActionId.RunTricks);
        Assert.Equal(["d3dx9", "vcrun2022"], fix.Input!.Choices.Where(c => c.Selected).Select(c => c.Id).Order());
        Assert.Contains(elden.Actions, a => a.Id == ActionId.ViewLog);

        Assert.Single(f, x => x.Key.StartsWith("log:", StringComparison.Ordinal) && x.Title == "Battle.net crashed");

        Assert.Contains(f, x => x.Key == "cleanup" && x.Actions.Any(a => a.Id == ActionId.Cleanup));
        Assert.Contains(f, x => x.Key == "no-backup" && x.Actions.Any(a => a.Id == ActionId.BackupAllPrefixes));
        Assert.DoesNotContain(f, x => x.Level == FindingLevel.Good);
        Assert.Contains("problem", Checkup.Summary(host.State, f), StringComparison.Ordinal);
    }

    [Fact]
    public void CheckupHintTitlesAreShort()
    {
        Assert.Equal("MSVCP140.dll is missing", Checkup.Short("MSVCP140.dll is missing: install the Visual C++ 2015-2022 runtime into the prefix."));
        Assert.Equal("The game crashed", Checkup.Short("The game crashed (unhandled exception). Try another Proton version."));
    }
}
