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
