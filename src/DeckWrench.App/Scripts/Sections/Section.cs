using DeckWrench.Host;
using DeckWrench.Host.Toolbox;

namespace DeckWrench.App.Sections;

public enum Tone
{
    Normal,
    Accent,
    Good,
    Warn,
    Bad,
    Dim,
}

/// <summary>One cell of a table row: shown text, and the value it sorts by.</summary>
public readonly record struct Cell(string Text, IComparable Sort, Tone Tone = Tone.Normal)
{
    public static implicit operator Cell(string s) => new(s, s);

    public static Cell Size(long bytes, Tone tone = Tone.Normal) => new(Core.Storage.DiskSize.Format(bytes), bytes, tone);

    public static Cell Date(DateTime utc) => new(utc == default ? "" : utc.ToLocalTime().ToString("g"), utc);
}

/// <summary>
/// One thing in a section's list (a game, a Proton version, a prefix...). The controller mode shows
/// Title/Detail/Status as a big row; the desktop mode shows <see cref="Cells"/> as a table row.
/// Both open the same facts and actions for it.
/// </summary>
public sealed record Row
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string? Status { get; init; }
    public Tone StatusTone { get; init; } = Tone.Accent;

    /// <summary>Cover art on disk, for games.</summary>
    public string? Cover { get; init; }

    /// <summary>Table cells, one per <see cref="Section.Columns"/> entry.</summary>
    public IReadOnlyList<Cell> Cells { get; init; } = [];

    /// <summary>Optional group heading (controller mode shows it above the first row of the group).</summary>
    public string Group { get; init; } = "";

    public required object Item { get; init; }
}

/// <summary>A label and value shown for an item ("Proton", "GE-Proton9-20").</summary>
public readonly record struct Fact(string Label, string Value, Tone Tone = Tone.Normal);

/// <summary>
/// A part of DeckWrench (Games, Proton, Prefixes...). Everything both UI modes need comes from
/// here: list rows (and table columns), the facts and actions for one item, and section-wide
/// actions. A feature added to a section shows up in both modes at once.
/// </summary>
public abstract class Section
{
    protected static AppHost Host => Main.Instance.Host!;
    protected static ToolboxState S => Host.State;

    public abstract string Name { get; }

    /// <summary>One line on what the section is for.</summary>
    public abstract string Summary { get; }

    /// <summary>Desktop table columns (the first is the name).</summary>
    public abstract IReadOnlyList<string> Columns { get; }

    /// <summary>Column the desktop table sorts by at first, and whether descending.</summary>
    public virtual (int Column, bool Descending) DefaultSort => (0, false);

    public abstract IReadOnlyList<Row> Rows();

    /// <summary>What's shown when there are no rows.</summary>
    public abstract string EmptyText { get; }

    /// <summary>A short summary above the list ("3 prefixes are orphaned, 1.2 GB").</summary>
    public virtual string Banner => "";

    public abstract IReadOnlyList<Fact> Facts(object item);

    public abstract IReadOnlyList<ToolAction> ActionsFor(object item);

    /// <summary>Actions for the whole section (Download GE-Proton, Clear all, Clean up).</summary>
    public virtual IReadOnlyList<ToolAction> PageActions => [];

    /// <summary>Controller mode: page actions under the list instead of above it (Home: findings first).</summary>
    public virtual bool PageActionsLast => false;

    public virtual string PageActionsTitle => "Actions";

    /// <summary>A longer explanation for an item, shown under its facts (log hints, contents).</summary>
    public virtual string Notes(object item) => "";

    public string TitleOf(object item) => Rows().FirstOrDefault(r => ReferenceEquals(r.Item, item) || Equals(r.Item, item))?.Title ?? item.ToString() ?? "";

    /// <summary>Finds an item again after a rescan (by row key).</summary>
    public Row? Find(string key) => Rows().FirstOrDefault(r => r.Key == key);

    public static readonly IReadOnlyList<Section> All =
    [
        new HomeSection(),
        new GamesSection(),
        new ProtonSection(),
        new PrefixesSection(),
        new ShadersSection(),
        new StorageSection(),
        new LogsSection(),
    ];

    public static Section Get(string name) => All.First(s => s.Name == name);

    protected static string Plural(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";
}
