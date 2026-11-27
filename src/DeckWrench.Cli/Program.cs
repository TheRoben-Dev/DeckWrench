using DeckWrench.Core;
using DeckWrench.Core.Steam;
using DeckWrench.Core.Storage;
using DeckWrench.Host;
using DeckWrench.Host.Toolbox;
using DeckWrench.Host.Updates;

// Headless entry point (`deckwrench`): read-only reports of what the app shows, the helper that
// writes Steam settings while Steam restarts, and "add to Steam".

return Cli.Run(args);

internal static class Cli
{
    private const string Usage = """
        DeckWrench command line

          deckwrench version                  show the installed version
          deckwrench settings                 print the settings file path and contents
          deckwrench checkup                  what needs attention: broken Proton settings, crashes
                                              in logs, full drives, space to free, missing backups
          deckwrench saves [game]             where each game's save files are (one game: by app id
                                              or name)
          deckwrench backup-saves [game]      back up the saves of one game, or of every game
          deckwrench tune [status|apply [--swap-mb N]|revert]
                                              memory tuning for games (apply and revert need root)
          deckwrench status --json            Home's findings, drives and pending Steam changes as
                                              JSON (for the Decky Loader plugin)
          deckwrench games                    installed games: Proton, prefix, shader cache, size
          deckwrench proton                   installed compatibility tools and the games using them
          deckwrench prefixes [--orphans]     Proton prefixes (only orphaned ones with --orphans)
          deckwrench shadercache              shader caches per game
          deckwrench storage                  what takes space on each drive, and what can be freed
          deckwrench logs                     Proton logs
          deckwrench pending                  Steam settings waiting for Steam to close
          deckwrench report [--save]          a Markdown report of the system and library, for
                                              asking for help (--save: also write it to a file)
          deckwrench apply-pending [--wait] [--restart-steam]
                                              write pending Steam settings (--wait: wait up to two
                                              minutes for Steam to exit first)
          deckwrench steam-add [--restart]    add DeckWrench to Steam with its art (--restart closes
                                              and restarts Steam around it)
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            var host = new AppHost();
            switch (args[0])
            {
                case "version":
                    Console.WriteLine(AppUpdater.DisplayVersion(AppUpdater.CurrentAppVersion()));
                    return 0;
                case "settings":
                    Console.WriteLine(host.Paths.SettingsFile);
                    Console.WriteLine(File.Exists(host.Paths.SettingsFile) ? File.ReadAllText(host.Paths.SettingsFile) : "(defaults)");
                    return 0;
                case "checkup":
                {
                    var s = host.Scan();
                    Console.WriteLine(Checkup.Summary(s, host.Findings));
                    foreach (var f in host.Findings)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"[{f.Level switch { FindingLevel.Problem => "FIX", FindingLevel.Attention => "LOOK", _ => "OK" }}] {f.Title}");
                        Console.WriteLine("    " + f.Detail);
                        foreach (var a in f.Actions.Where(a => a.Unavailable == null))
                        {
                            Console.WriteLine("    → in DeckWrench: " + a.Label);
                        }
                    }
                    return host.Findings.Any(f => f.Level == FindingLevel.Problem) ? 1 : 0;
                }
                case "saves":
                case "backup-saves":
                {
                    var s = host.Scan();
                    var games = host.Actions.SaveCandidates()
                        .Where(g => args.Length < 2 || g.AppId == args[1] || g.Title.Contains(args[1], StringComparison.OrdinalIgnoreCase)).ToList();
                    if (games.Count == 0)
                    {
                        Console.Error.WriteLine("No such game.");
                        return 1;
                    }
                    var started = DateTime.UtcNow;
                    host.Saves.Manifest().GetAwaiter().GetResult();
                    Console.Error.WriteLine($"manifest: {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
                    foreach (var g in games)
                    {
                        if (args[0] == "saves")
                        {
                            var files = host.Saves.Find(g).GetAwaiter().GetResult();
                            Console.WriteLine($"{g.Title}: {files.Count} file{(files.Count == 1 ? "" : "s")}, {DiskSize.Format(files.Sum(f => f.Size))}");
                            foreach (var f in files)
                            {
                                Console.WriteLine($"    {DiskSize.Format(f.Size),9}  {f.Path}");
                            }
                        }
                        else
                        {
                            var b = host.Saves.Backup(g, "cli").GetAwaiter().GetResult();
                            Console.WriteLine(b == null ? $"{g.Title}: no save files found" : $"{g.Title}: {b.Files} files → {b.Archive}");
                        }
                    }
                    return 0;
                }
                case "tune":
                {
                    var t = new Tuning();
                    var sub = args.Length > 1 ? args[1] : "status";
                    if (sub is "apply" or "revert" && Environment.UserName != "root")
                    {
                        Console.Error.WriteLine("Needs root: sudo deckwrench tune " + sub);
                        return 1;
                    }
                    static void Run(string[] cmd)
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(cmd[0]) { UseShellExecute = false };
                        foreach (var a in cmd[1..])
                        {
                            psi.ArgumentList.Add(a);
                        }
                        Console.WriteLine("$ " + string.Join(' ', cmd));
                        using var p = System.Diagnostics.Process.Start(psi)!;
                        p.WaitForExit();
                        // swapoff of a swap file that isn't active is fine; everything else must work.
                        if (p.ExitCode != 0 && cmd[0] != "swapoff")
                        {
                            throw new InvalidOperationException($"{cmd[0]} failed (exit code {p.ExitCode})");
                        }
                    }
                    IReadOnlyList<string> log;
                    switch (sub)
                    {
                        case "apply":
                            var i = Array.IndexOf(args, "--swap-mb");
                            long? swap = i > 0 && i + 1 < args.Length ? long.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture) << 20 : null;
                            log = t.Apply(swap, Run);
                            break;
                        case "revert":
                            log = t.Revert(Run);
                            break;
                        default:
                            foreach (var x in t.Read())
                            {
                                Console.WriteLine($"{x.Label,-30} {(x.Current == null ? "(not available)" : TuningSetting.Normalize(x.Current)),-10} recommended {x.Recommended}{(x.IsRecommended ? "  ✓" : "")}");
                            }
                            var sw = t.Swap();
                            Console.WriteLine($"{"Swap file",-30} {(sw.File ?? "none")} {DiskSize.Format(sw.Size)}");
                            Console.WriteLine(t.Applied ? "DeckWrench's tuning is installed." : "Not tuned by DeckWrench.");
                            return 0;
                    }
                    foreach (var l in log)
                    {
                        Console.WriteLine(l);
                    }
                    return 0;
                }
                case "status":
                {
                    var s = host.Scan();
                    var installDir = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!;
                    var appId = SteamSetup.Shortcut(installDir).AppId;
                    var status = new
                    {
                        version = AppUpdater.DisplayVersion(AppUpdater.CurrentAppVersion()),
                        summary = Checkup.Summary(s, host.Findings),
                        findings = host.Findings.Select(f => new { level = f.Level.ToString().ToLowerInvariant(), title = f.Title, detail = f.Detail }),
                        drives = s.Drives.Select(d => new { label = d.Drive.Label, free = d.Drive.Free, total = d.Drive.Total }),
                        reclaimable = s.Reclaimable,
                        pending = s.Pending.Select(p => p.Describe()),
                        // Steam's 64-bit id of the DeckWrench shortcut, for steam://rungameid/.
                        gameId = (((ulong)appId << 32) | 0x02000000UL).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    };
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(status, new System.Text.Json.JsonSerializerOptions { WriteIndented = !args.Contains("--json") }));
                    return 0;
                }
                case "games":
                {
                    var s = host.Scan();
                    foreach (var g in s.Games)
                    {
                        Console.WriteLine($"{g.Key,-22} {g.Title}");
                        Console.WriteLine($"    {(g.RunsInSteam ? "proton: " + s.ToolLabel(g.EffectiveCompatTool) + " · " : "")}size {DiskSize.Format(g.Size)} on {g.Drive?.Label ?? "?"}" +
                                          (g.Prefix != null ? $" · prefix {DiskSize.Format(g.Prefix.Size)}" : "") +
                                          (g.ShaderCache != null ? $" · shaders {DiskSize.Format(g.ShaderCache.Size)}" : ""));
                        if (g.EffectiveLaunchOptions.Length > 0)
                        {
                            Console.WriteLine($"    launch: {g.EffectiveLaunchOptions}");
                        }
                    }
                    Console.WriteLine($"{s.Games.Count} games");
                    return 0;
                }
                case "proton":
                {
                    var s = host.Scan();
                    Console.WriteLine($"default: {(s.DefaultTool == null ? "Steam's choice" : s.ToolLabel(s.DefaultTool))}");
                    foreach (var t in s.Tools)
                    {
                        var users = s.GamesUsing(t);
                        Console.WriteLine($"{t.Name,-24} {t.DisplayName,-28} {t.Kind,-7} {DiskSize.Format(t.Size),9}  {users.Count} game(s)");
                        foreach (var u in users)
                        {
                            Console.WriteLine($"    {u.Title}");
                        }
                    }
                    return 0;
                }
                case "prefixes":
                {
                    var s = host.Scan();
                    var orphansOnly = args.Contains("--orphans");
                    foreach (var p in s.Prefixes.Where(p => !orphansOnly || p.Orphan).OrderByDescending(p => p.Size))
                    {
                        Console.WriteLine($"{p.AppId,-11} {DiskSize.Format(p.Size),9}  {(p.Orphan ? "ORPHAN " : "")}{p.Owner ?? ""}  [{p.ProtonVersion ?? "?"}]  {p.Dir}");
                    }
                    return 0;
                }
                case "shadercache":
                {
                    var s = host.Scan();
                    foreach (var c in s.ShaderCaches.OrderByDescending(c => c.Size))
                    {
                        Console.WriteLine($"{c.AppId,-11} {DiskSize.Format(c.Size),9}  {(c.Orphan ? "ORPHAN " : "")}{c.Owner ?? ""}");
                    }
                    return 0;
                }
                case "storage":
                {
                    var s = host.Scan();
                    foreach (var d in s.Drives)
                    {
                        Console.WriteLine($"{d.Drive.Label} ({d.Drive.Mount}): {DiskSize.Format(d.Drive.Used)} used of {DiskSize.Format(d.Drive.Total)}, {DiskSize.Format(d.Drive.Free)} free");
                        foreach (var (label, size) in d.Breakdown)
                        {
                            Console.WriteLine($"    {label,-18} {DiskSize.Format(size),9}");
                        }
                    }
                    Console.WriteLine($"cleanup: {s.Cleanup.Count} item(s), {DiskSize.Format(s.Reclaimable)} can be freed");
                    foreach (var c in s.Cleanup)
                    {
                        Console.WriteLine($"    {DiskSize.Format(c.Size),9}  {c.Title}  ({c.Path})");
                    }
                    return 0;
                }
                case "logs":
                {
                    var s = host.Scan();
                    foreach (var l in s.Logs)
                    {
                        Console.WriteLine($"{l.Modified.ToLocalTime():g}  {DiskSize.Format(l.Size),9}  {l.Owner ?? "app " + l.AppId}  {l.Path}");
                    }
                    return 0;
                }
                case "report":
                {
                    var s = host.Scan();
                    Console.Write(LibraryReport.Build(s, host));
                    if (args.Contains("--save"))
                    {
                        Console.Error.WriteLine("saved " + LibraryReport.Save(s, host));
                    }
                    return 0;
                }
                case "pending":
                    foreach (var p in host.Pending.All)
                    {
                        Console.WriteLine(p.Describe());
                    }
                    return 0;
                case "apply-pending":
                {
                    if (args.Contains("--wait"))
                    {
                        // Started by the app before it asks Steam to exit: write in the gap before Steam starts again.
                        var until = DateTime.UtcNow.AddMinutes(2);
                        while (SteamShortcuts.SteamRunning(host.Paths.Home) && DateTime.UtcNow < until)
                        {
                            Thread.Sleep(100);
                        }
                    }
                    var n = host.ApplyPendingNow();
                    Console.WriteLine($"wrote {n} change(s) to Steam");
                    if (args.Contains("--restart-steam"))
                    {
                        SteamSetup.StartSteam();
                    }
                    return 0;
                }
                case "steam-add":
                {
                    var dir = AppContext.BaseDirectory.TrimEnd('/', '\\');
                    var restart = args.Contains("--restart") && SteamShortcuts.SteamRunning(host.Paths.Home);
                    if (restart && !SteamSetup.CloseSteam(host.Paths.Home, TimeSpan.FromSeconds(30)))
                    {
                        Console.Error.WriteLine("error: Steam didn't close. Exit it yourself, then run this again.");
                        return 1;
                    }
                    var n = SteamSetup.Add(host.Paths.Home, dir);
                    Console.WriteLine($"DeckWrench added to Steam ({n} account{(n == 1 ? "" : "s")}).");
                    if (restart)
                    {
                        SteamSetup.StartSteam();
                    }
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"unknown command \"{args[0]}\"\n");
                    Console.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }
}
