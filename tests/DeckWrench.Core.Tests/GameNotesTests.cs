using DeckWrench.Host.Toolbox;
using Xunit;

namespace DeckWrench.Core.Tests;

public sealed class GameNotesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deckwrench-notes-" + Guid.NewGuid().ToString("N")[..8]);

    private string File => Path.Combine(_dir, "notes.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void NotesAreSavedPerGameOnOneLineAndRemovedWhenEmptied()
    {
        var notes = new GameNotes(File);
        Assert.Null(notes.Get("steam:1"));
        notes.Set("steam:1", "  needs\nPROTON_USE_WINED3D=1 \t ");
        notes.Set("lutris:skyrim", "SKSE via the launcher");
        Assert.Equal("needs PROTON_USE_WINED3D=1", notes.Get("steam:1"));

        var again = new GameNotes(File);  // read back from disk
        Assert.Equal("needs PROTON_USE_WINED3D=1", again.Get("steam:1"));
        Assert.Equal("SKSE via the launcher", again.Get("lutris:skyrim"));

        again.Set("steam:1", "   ");
        Assert.Null(new GameNotes(File).Get("steam:1"));
        Assert.NotNull(new GameNotes(File).Get("lutris:skyrim"));

        again.Set("steam:2", new string('x', 2000));
        Assert.Equal(GameNotes.MaxLength, again.Get("steam:2")!.Length);
    }

    [Fact]
    public void AnUnreadableFileIsKeptAsideNotLost()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ not json");
        var notes = new GameNotes(File);
        Assert.Null(notes.Get("steam:1"));
        Assert.True(System.IO.File.Exists(File + ".bad"));
        notes.Set("steam:1", "ok");
        Assert.Equal("ok", new GameNotes(File).Get("steam:1"));
    }
}
