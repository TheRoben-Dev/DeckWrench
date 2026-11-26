using DeckWrench.App.Screens;
using DeckWrench.App.Sections;
using DeckWrench.App.Ui;
using DeckWrench.Host.Toolbox;
using Godot;

namespace DeckWrench.App.Controller;

/// <summary>Shared helpers for controller pages that show sections and actions.</summary>
public static class Rows
{
    public const string ActionMeta = "dw_action";

    public static Color ToneColor(Tone t) => t switch
    {
        Tone.Accent => UiTheme.AccentText,
        Tone.Good => UiTheme.Good,
        Tone.Warn => UiTheme.Warn,
        Tone.Bad => UiTheme.Bad,
        Tone.Dim => UiTheme.Dim,
        _ => UiTheme.Text,
    };

    /// <summary>A button for an action: dimmed with the reason when it can't run, red text when it removes data.</summary>
    public static ActionButton Action(ToolAction a, Func<IActionUi> ui)
    {
        var help = a.Unavailable != null ? a.Unavailable : a.Help;
        var b = new ActionButton(a.Label, () => ActionFlow.Start(a, ui()), help);
        b.SetMeta(ActionMeta, a.Id.ToString());
        if (a.Unavailable != null)
        {
            b.AddThemeColorOverride("font_color", UiTheme.Dim);
            b.AddThemeColorOverride("font_focus_color", UiTheme.Dim.Lightened(0.3f));
        }
        else if (a.Destructive)
        {
            b.AddThemeColorOverride("font_color", UiTheme.Bad.Lerp(UiTheme.Text, 0.25f));
        }
        return b;
    }

    /// <summary>"Label   value" line for an item's facts.</summary>
    public static Control Fact(Fact f, int labelWidth = 190)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        var l = UiTheme.Label(f.Label, UiTheme.SmallSize + 1, UiTheme.Dim);
        l.CustomMinimumSize = new Vector2(labelWidth, 0);
        row.AddChild(l);
        var v = UiTheme.Label(f.Value, UiTheme.SmallSize + 2, ToneColor(f.Tone), wrap: true);
        v.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(v);
        return row;
    }
}

/// <summary>
/// One section as a tab: a summary line, the section's own actions, then a row per item.
/// A opens the item; Y scans again.
/// </summary>
public partial class SectionScreen : Screen, IStateView
{
    private readonly Section _section;
    private Control? _first;
    private bool _stale;
    private string? _openedKey;

    public SectionScreen(Section section) => _section = section;

    public bool Stale => _stale;

    public override IEnumerable<(string Action, string Label)> Hints => [(Pad.Secondary, "Scan again"), (Pad.Switch, "Status")];

    public void Refresh()
    {
        if (IsVisibleInTree())
        {
            _stale = false;
            Rebuild();
        }
        else
        {
            _stale = true;
        }
    }

    protected override void Build()
    {
        var col = Column(8);
        AddChild(Scroller(col));
        var title = new HBoxContainer();
        title.AddThemeConstantOverride("separation", 18);
        title.AddChild(UiTheme.Label(_section.Name, UiTheme.TitleSize));
        var banner = UiTheme.Label(Main.Instance.Scanning && Host.State.ScannedAt == default ? "Scanning…" : _section.Banner, UiTheme.SmallSize + 2, UiTheme.Dim);
        banner.SizeFlagsVertical = SizeFlags.ShrinkEnd;
        banner.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        banner.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        title.AddChild(banner);
        col.AddChild(title);
        col.AddChild(UiTheme.Label(_section.Summary, UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));

        var items = new List<Control>();
        Control? reopen = null;
        if (_section is StorageSection)
        {
            foreach (var d in Host.State.Drives)
            {
                col.AddChild(new DriveBar(d, compact: false));
            }
        }
        var page = _section.PageActions;
        if (page.Count > 0)
        {
            col.AddChild(Section("Actions"));
            foreach (var a in page)
            {
                var b = Rows.Action(a, () => Main.Instance.Shell);
                col.AddChild(b);
                items.Add(b);
            }
        }

        if (Host.State.ScannedAt == default)
        {
            col.AddChild(UiTheme.Label("Scanning Steam, Heroic and Lutris…", UiTheme.FontSize, UiTheme.Dim));
        }
        else
        {
            var rows = _section.Rows();
            if (rows.Count == 0)
            {
                col.AddChild(Section(_section.Name));
                col.AddChild(UiTheme.Label(_section.EmptyText, UiTheme.SmallSize + 2, UiTheme.Dim, wrap: true));
            }
            string? group = null;
            foreach (var r in rows)
            {
                if (r.Group != group)
                {
                    group = r.Group;
                    col.AddChild(Section(group.Length > 0 ? group : _section.Name));
                }
                var key = r.Key;
                Control? leading = r.Cover != null || _section is GamesSection or StorageSection
                    ? new CoverArt(r.Title, r.Cover, new Vector2(44, 66), 6)
                    : null;
                var row = new ListRow(r.Title, r.Detail, r.Status, Rows.ToneColor(r.StatusTone), leading, () =>
                {
                    _openedKey = key;
                    Main.Instance.Push(new ItemPage(_section, key));
                }, $"{r.Title}: A for details and actions.");
                col.AddChild(row);
                items.Add(row);
                if (key == _openedKey)
                {
                    reopen = row;
                }
            }
        }
        Focus.Column(items);
        Anim.Stagger(items);
        // After a rescan rebuilt the list, focus goes back to the row that was opened.
        _first = reopen ?? items.FirstOrDefault(i => i is ListRow) ?? items.FirstOrDefault();
    }

    public override void FocusDefault() => _first?.GrabFocus();

    public override void OnAction(string action)
    {
        if (action == Pad.Secondary)
        {
            Main.Instance.Rescan();
        }
    }
}

/// <summary>One item (a game, a Proton version, a prefix...): its facts and every action for it.</summary>
public partial class ItemPage : Screen, IStateView
{
    private readonly Section _section;
    private readonly string _key;
    private Control? _first;
    private bool _stale;
    private string? _focusAction;

    public ItemPage(Section section, string key)
    {
        _section = section;
        _key = key;
    }

    public bool Stale => _stale;

    public void Refresh()
    {
        // Keep focus on the same action when a scan rebuilds the page.
        var focus = GetViewport()?.GuiGetFocusOwner();
        if (focus != null && IsAncestorOf(focus) && focus.HasMeta(Rows.ActionMeta))
        {
            _focusAction = focus.GetMeta(Rows.ActionMeta).AsString();
        }
        if (IsVisibleInTree())
        {
            _stale = false;
            Rebuild();
        }
        else
        {
            _stale = true;
        }
    }

    protected override void Build()
    {
        var row = _section.Find(_key);
        var col = Column(8);
        AddChild(Scroller(col));
        if (row == null)
        {
            col.AddChild(UiTheme.Label("It's gone", UiTheme.TitleSize));
            col.AddChild(UiTheme.Label("This isn't there any more after the last scan (removed, moved or uninstalled).", UiTheme.SmallSize + 2, UiTheme.Dim, wrap: true));
            var back = new ActionButton("Back", () => Main.Instance.Pop());
            col.AddChild(back);
            _first = back;
            return;
        }
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 24);
        col.AddChild(top);
        if (row.Cover != null || _section is GamesSection or StorageSection)
        {
            top.AddChild(new CoverArt(row.Title, row.Cover, new Vector2(120, 180), 10) { SizeFlagsVertical = SizeFlags.ShrinkBegin });
        }
        var info = Column(6);
        top.AddChild(info);
        info.AddChild(UiTheme.Label(row.Title, UiTheme.TitleSize, wrap: true));
        foreach (var f in _section.Facts(row.Item))
        {
            info.AddChild(Rows.Fact(f));
        }
        var notes = _section.Notes(row.Item);
        if (notes.Length > 0)
        {
            info.AddChild(UiTheme.Label(notes, UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        }
        col.AddChild(Section("Actions"));
        var items = new List<Control>();
        _first = null;
        foreach (var a in _section.ActionsFor(row.Item))
        {
            var b = Rows.Action(a, () => Main.Instance.Shell);
            col.AddChild(b);
            items.Add(b);
            if (a.Id.ToString() == _focusAction)
            {
                _first = b;
            }
            else if (a.Unavailable == null && _focusAction == null)
            {
                _first ??= b;
            }
        }
        Focus.Column(items);
        Anim.Stagger(items, _focusAction == null ? 0.015 : 0);
        _first ??= items.FirstOrDefault(i => i is ActionButton { Disabled: false }) ?? items.FirstOrDefault();
    }

    public override void FocusDefault() => _first?.GrabFocus();
}

/// <summary>View / Select: scan state, changes waiting for Steam, and the global actions.</summary>
public partial class StatusPage : Screen
{
    private Control? _first;

    public override bool IsOverlay => true;

    public override void AnimateOpen() => Anim.FadeIn(this);

    protected override void Build()
    {
        var col = Column(10);
        AddChild(CardFrame(col));
        col.AddChild(UiTheme.Label("Status", UiTheme.TitleSize));
        var s = Host.State;
        col.AddChild(UiTheme.Label(Main.Instance.Scanning ? "Scanning now…"
            : s.ScannedAt == default ? "Not scanned yet." : $"Last scan {s.ScannedAt.ToLocalTime():t}: {s.Games.Count} games, {s.Tools.Count} Proton versions, {s.Prefixes.Count} prefixes.",
            UiTheme.SmallSize + 2, UiTheme.Dim, wrap: true));
        var steamRunning = Core.Steam.SteamShortcuts.SteamRunning(Host.Paths.Home);
        col.AddChild(UiTheme.Label(steamRunning
                ? "Steam is running: changes to compatibility tools and launch options wait until Steam restarts."
                : "Steam is closed: changes are written straight away.",
            UiTheme.SmallSize + 2, steamRunning ? UiTheme.Warn : UiTheme.Good, wrap: true));
        foreach (var p in s.Pending)
        {
            col.AddChild(UiTheme.Label("• " + p.Describe(), UiTheme.SmallSize + 1, UiTheme.Text, wrap: true));
        }
        var items = new List<Control>();
        foreach (var a in Host.Actions.Global())
        {
            var b = Rows.Action(a, () => Main.Instance.Shell);
            col.AddChild(b);
            items.Add(b);
        }
        Focus.Column(items);
        _first = items.FirstOrDefault();
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
