using DeckWrench.Core.Detection;
using DeckWrench.Core.Storage;
using DeckWrench.Host.Toolbox;
using Xunit;

namespace DeckWrench.Core.Tests;

public class SpacePlannerTests
{
    private const long GB = 1L << 30;

    private static readonly Drive Sd = new() { Mount = "/run/media/deck/sd", Label = "SD card", Total = 512 * GB, Free = 4 * GB };
    private static readonly Drive Internal = new() { Mount = "/home", Label = "Internal", Total = 1024 * GB, Free = 60 * GB };

    private static Drive? DriveOf(string path) => path.StartsWith(Sd.Mount, StringComparison.Ordinal) ? Sd : path.StartsWith("/home", StringComparison.Ordinal) ? Internal : null;

    private static GameEntry Game(string id, string title, string library, long size, bool shortcut = false) => new()
    {
        Install = new InstalledGame
        {
            Source = shortcut ? GameSource.SteamShortcut : GameSource.Steam,
            SourceId = id,
            Title = title,
            Library = library,
            InstallDir = $"{library}/steamapps/common/{title}",
        },
        Size = size,
        Drive = DriveOf(library),
    };

    private static ToolboxState State() => new()
    {
        Libraries = [Sd.Mount + "/steamlib", "/home/deck/.local/share/Steam"],
        Games =
        [
            Game("1", "Big", Sd.Mount + "/steamlib", 70 * GB),        // doesn't fit on Internal (60 GB free)
            Game("2", "Medium", Sd.Mount + "/steamlib", 30 * GB),
            Game("3", "Small", Sd.Mount + "/steamlib", 5 * GB),
            Game("4", "Home game", "/home/deck/.local/share/Steam", 10 * GB),
            Game("5", "Shortcut", Sd.Mount + "/steamlib", 3 * GB, shortcut: true),
        ],
        Cleanup =
        [
            new CleanupItem { Kind = CleanupKind.OrphanShaderCache, Title = "Old cache", Detail = "", Path = Sd.Mount + "/steamlib/steamapps/shadercache/9", Size = 2 * GB },
            new CleanupItem { Kind = CleanupKind.OrphanPrefix, Title = "Old prefix", Detail = "", Path = Sd.Mount + "/steamlib/steamapps/compatdata/9", Size = GB, Caution = "May hold saves." },
            new CleanupItem { Kind = CleanupKind.ProtonLog, Title = "Log", Detail = "", Path = "/home/deck/steam-1.log", Size = GB },
        ],
    };

    [Fact]
    public void OffersThisDrivesCleanupThenGamesThatFitElsewhere()
    {
        var options = SpacePlanner.Options(State(), Sd, DriveOf, movesAllowed: true);
        Assert.Equal(["clean:" + Sd.Mount + "/steamlib/steamapps/shadercache/9", "clean:" + Sd.Mount + "/steamlib/steamapps/compatdata/9", "move:2", "move:3"],
            options.Select(o => o.Id));
        Assert.True(options[0].Suggested);
        Assert.False(options[1].Suggested);  // a prefix can hold saves
        Assert.All(options.Where(o => o.Game != null), o =>
        {
            Assert.False(o.Suggested);
            Assert.Equal("/home/deck/.local/share/Steam", o.MoveTo);
        });
    }

    [Fact]
    public void NoMovesWhileSteamRunsOrWithoutAnotherDrive()
    {
        Assert.All(SpacePlanner.Options(State(), Sd, DriveOf, movesAllowed: false), o => Assert.Null(o.Game));
        var oneDrive = State() with { Libraries = [Sd.Mount + "/steamlib"] };
        Assert.All(SpacePlanner.Options(oneDrive, Sd, DriveOf, movesAllowed: true), o => Assert.Null(o.Game));
    }

    [Fact]
    public void PickedMovesMustFitTogether()
    {
        var options = SpacePlanner.Options(State(), Sd, DriveOf, movesAllowed: true);
        Assert.Equal(2, SpacePlanner.Picked(options, "move:3\nclean:" + Sd.Mount + "/steamlib/steamapps/shadercache/9", DriveOf).Count);
        var bigger = new Drive { Mount = Internal.Mount, Label = Internal.Label, Total = Internal.Total, Free = 34 * GB };
        Drive? Tight(string p) => p.StartsWith("/home", StringComparison.Ordinal) ? bigger : DriveOf(p);
        Assert.Throws<InvalidOperationException>(() => SpacePlanner.Picked(options, "move:2\nmove:3", Tight));
    }
}
