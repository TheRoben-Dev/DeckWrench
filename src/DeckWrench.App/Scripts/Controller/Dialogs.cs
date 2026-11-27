using DeckWrench.App.Screens;
using DeckWrench.App.Sections;
using DeckWrench.App.Ui;
using DeckWrench.Core.Logs;
using DeckWrench.Core.Storage;
using DeckWrench.Host;
using DeckWrench.Host.Toolbox;
using Godot;

namespace DeckWrench.App.Controller;

/// <summary>Pick one choice (A picks) or several (A ticks, Y or the Apply row applies).</summary>
public partial class PickPage : Screen
{
    private readonly ActionInput _input;
    private readonly bool _many;
    private readonly Action<string> _picked;
    private readonly HashSet<string> _selected;
    private Control? _first;
    private string? _toggled;

    public PickPage(ActionInput input, bool many, Action<string> picked)
    {
        _input = input;
        _many = many;
        _picked = picked;
        _selected = input.Choices.Where(c => c.Selected).Select(c => c.Id).ToHashSet();
    }

    public override IEnumerable<(string Action, string Label)> Hints => _many ? [(Pad.Secondary, "Apply")] : [];

    protected override void Build()
    {
        var col = Column(8);
        AddChild(Scroller(col));
        col.AddChild(UiTheme.Label(_input.Prompt, UiTheme.TitleSize - 4, wrap: true));
        var items = new List<Control>();
        Control? current = null;
        if (_many)
        {
            col.AddChild(UiTheme.Label("A ticks or unticks a row. Apply when done.", UiTheme.SmallSize + 1, UiTheme.Dim));
            var apply = new ActionButton($"Apply ({_selected.Count} picked)", Apply, "Uses the ticked rows.");
            col.AddChild(apply);
            items.Add(apply);
        }
        string? group = null;
        foreach (var c in _input.Choices)
        {
            if (c.Group != group && c.Group.Length > 0)
            {
                group = c.Group;
                col.AddChild(Section(group));
            }
            var id = c.Id;
            var on = _many ? _selected.Contains(id) : c.Selected;
            var mark = _many ? (on ? "☑  " : "☐  ") : on ? "●  " : "";
            ListRow? row = null;
            row = new ListRow(mark + c.Label, c.Detail, _many ? null : on ? "Current" : null, UiTheme.AccentText, null, () =>
            {
                if (!_many)
                {
                    Main.Instance.Pop();
                    _picked(id);
                    return;
                }
                if (!_selected.Add(id))
                {
                    _selected.Remove(id);
                }
                _toggled = id;  // focus stays on this row after the rebuild
                Rebuild();
                FocusDefault();
            }, c.Detail.Length > 0 ? $"{c.Label}: {c.Detail}" : c.Label);
            col.AddChild(row);
            items.Add(row);
            if (_many ? id == _toggled : c.Selected)
            {
                current ??= row;
            }
        }
        Focus.Column(items);
        _first = current ?? items.FirstOrDefault();
    }

    private void Apply()
    {
        Main.Instance.Pop();
        _picked(string.Join('\n', _input.Choices.Where(c => _selected.Contains(c.Id)).Select(c => c.Id)));
    }

    public override void OnAction(string action)
    {
        if (_many && action == Pad.Secondary)
        {
            Apply();
        }
    }

    public override void FocusDefault() => _first?.GrabFocus();
}

/// <summary>Text entry: a field that opens Steam's on-screen keyboard when focused in Game Mode, Save and Cancel.</summary>
public partial class TextInputPage : Screen
{
    private readonly ActionInput _input;
    private readonly Action<string> _entered;
    private LineEdit? _field;
    private string _text;

    public TextInputPage(ActionInput input, Action<string> entered)
    {
        _input = input;
        _entered = entered;
        _text = input.Initial;
    }

    protected override void Build()
    {
        var center = new CenterContainer();
        AddChild(center);
        var col = Column(16);
        col.CustomMinimumSize = new Vector2(900, 0);
        center.AddChild(col);
        col.AddChild(UiTheme.Label(_input.Prompt, UiTheme.TitleSize - 4, wrap: true));
        _field = new LineEdit
        {
            Text = _text,
            Secret = _input.Secret,
            CustomMinimumSize = new Vector2(0, 58),
            PlaceholderText = "Type here",
            TooltipText = "A or a tap opens Steam's keyboard in Game Mode (Steam + X anywhere). Enter saves.",
            ClearButtonEnabled = true,
            CaretBlink = true,
        };
        _field.AddThemeFontSizeOverride("font_size", UiTheme.FontSize + 2);
        _field.AddThemeStyleboxOverride("normal", UiTheme.Box(UiTheme.PanelHi, 2, 8, UiTheme.Line));
        _field.AddThemeStyleboxOverride("focus", UiTheme.Box(UiTheme.PanelHi, 2, 8, UiTheme.Accent));
        _field.TextChanged += t => _text = t;
        _field.TextSubmitted += _ => Save();
        _field.FocusEntered += SteamKeyboard.Show;
        _field.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                SteamKeyboard.Show();
            }
        };
        col.AddChild(_field);
        col.AddChild(UiTheme.Label("In Game Mode, Steam's keyboard opens when the field is focused (or press Steam + X). A real keyboard types straight in.",
            UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        var save = new ActionButton("Save", Save, "Saves the text.");
        var clear = new ActionButton("Clear", () =>
        {
            _text = "";
            if (_field != null)
            {
                _field.Text = "";
            }
        }, "Empties the field.");
        var cancel = new ActionButton("Cancel", () => Main.Instance.Pop(), "Leaves without saving.");
        foreach (var b in new Control[] { save, clear, cancel })
        {
            col.AddChild(b);
        }
        Focus.Column([_field, save, clear, cancel]);
    }

    private void Save()
    {
        Main.Instance.Pop();
        _entered(_text);
    }

    public override void FocusDefault() => _field?.GrabFocus();
}

/// <summary>Steam's on-screen keyboard, for text fields in Game Mode and Big Picture.</summary>
public static class SteamKeyboard
{
    /// <summary>True when started by Steam (Game Mode, Big Picture, a Steam shortcut), where steam://open/keyboard works.</summary>
    public static bool UnderSteam =>
        SteamSetup.InGameMode || System.Environment.GetEnvironmentVariable("SteamAppId") is { Length: > 0 } ||
        System.Environment.GetEnvironmentVariable("SteamTenfoot") == "1";

    public static void Show()
    {
        if (UnderSteam && !UiLayout.Desktop)
        {
            Opener.Open("steam://open/keyboard");
        }
    }
}

/// <summary>
/// The confirmation for anything that removes or replaces data: what goes (contents and sizes),
/// the total, the backup made first, cautions. Cancel is focused first.
/// </summary>
public partial class PlanPage : Screen
{
    private readonly ConfirmPlan _plan;
    private readonly Action _yes;
    private Control? _cancel;

    public PlanPage(ConfirmPlan plan, Action yes)
    {
        _plan = plan;
        _yes = yes;
    }

    public override bool IsOverlay => true;

    public override void AnimateOpen() => Anim.FadeIn(this);

    protected override void Build()
    {
        var outer = Column(12);
        AddChild(CardFrame(outer));
        outer.AddChild(UiTheme.Label(_plan.Title, UiTheme.FontSize + 6, UiTheme.Text, wrap: true));
        outer.AddChild(UiTheme.Label($"Total: {DiskSize.Format(_plan.TotalSize)}", UiTheme.FontSize, UiTheme.AccentText));
        if (_plan.Backup != null)
        {
            outer.AddChild(UiTheme.Label("✓ " + _plan.Backup, UiTheme.SmallSize + 2, UiTheme.Good, wrap: true));
        }
        if (_plan.Caution != null)
        {
            outer.AddChild(UiTheme.Label("⚠ " + _plan.Caution, UiTheme.SmallSize + 2, UiTheme.Warn, wrap: true));
        }
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 16);
        var cancel = new ActionButton("Cancel", () => Main.Instance.Pop(), "Changes nothing.") { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var yes = new ActionButton(_plan.Verb, () =>
        {
            Main.Instance.Pop();
            _yes();
        }, "Goes ahead.") { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        yes.AddThemeColorOverride("font_color", UiTheme.Bad.Lerp(UiTheme.Text, 0.2f));
        buttons.AddChild(cancel);
        buttons.AddChild(yes);
        outer.AddChild(buttons);
        outer.AddChild(Section("What it affects"));
        var list = Column(2);
        var scroll = Scroller(list);
        outer.AddChild(scroll);
        var shown = _plan.Contents.Take(200).ToList();
        foreach (var (name, size) in shown)
        {
            var row = new HBoxContainer();
            var n = UiTheme.Label(name, UiTheme.SmallSize + 1, UiTheme.Text);
            n.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            n.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            row.AddChild(n);
            row.AddChild(UiTheme.Label(DiskSize.Format(size), UiTheme.SmallSize + 1, UiTheme.Dim));
            list.AddChild(row);
        }
        if (_plan.Contents.Count > shown.Count)
        {
            list.AddChild(UiTheme.Label($"…and {_plan.Contents.Count - shown.Count} more", UiTheme.SmallSize, UiTheme.Dim));
        }
        // The list scrolls with the right stick or a finger; focus stays on the two buttons.
        Focus.Row([cancel, yes]);
        _cancel = cancel;
    }

    public override void FocusDefault() => _cancel?.GrabFocus();
}

/// <summary>Progress while an action runs: a bar, live output when there is any, Cancel (B too).</summary>
public partial class WorkPage : Screen, IBusy
{
    private readonly string _title;
    private readonly bool _output;
    private readonly CancellationTokenSource _cts;
    private ProgressBar? _bar;
    private VBoxContainer? _lines;
    private ScrollContainer? _scroll;
    private ActionButton? _button;
    private Label? _result;
    private bool _done;
    private double _progress = -1;
    private readonly List<string> _buffer = new();

    public WorkPage(string title, bool output, CancellationTokenSource cts)
    {
        _title = title;
        _output = output;
        _cts = cts;
    }

    public override IEnumerable<(string Action, string Label)> Hints => [];

    protected override void Build()
    {
        var col = Column(14);
        if (_output)
        {
            AddChild(col);
        }
        else
        {
            var center = new CenterContainer();
            AddChild(center);
            col.CustomMinimumSize = new Vector2(620, 0);
            center.AddChild(col);
        }
        col.AddChild(UiTheme.Label(_title, 26, UiTheme.Text, wrap: true));
        _bar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.001, Indeterminate = _progress < 0, Value = Math.Max(0, _progress), CustomMinimumSize = new Vector2(0, 14) };
        col.AddChild(_bar);
        _result = UiTheme.Label("", UiTheme.SmallSize + 2, UiTheme.Dim, wrap: true);
        col.AddChild(_result);
        _button = new ActionButton(_done ? "Close" : "Cancel", OnButton, _done ? "Back to where you were." : "Stops the work. What's done so far stays done.");
        col.AddChild(_button);
        if (_output)
        {
            _lines = Column(0);
            _scroll = Scroller(_lines);
            var panel = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            panel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 1, 8, UiTheme.Line));
            panel.AddChild(_scroll);
            col.AddChild(panel);
            foreach (var l in _buffer)
            {
                AddLine(l);
            }
        }
    }

    private void OnButton()
    {
        if (_done)
        {
            Main.Instance.PopTo(this);
            Main.Instance.Pop();
        }
        else
        {
            _cts.Cancel();
        }
    }

    public void Progress(double p)
    {
        _progress = p;
        if (_bar != null && IsInstanceValid(_bar))
        {
            _bar.Indeterminate = p < 0;
            if (p >= 0)
            {
                _bar.Value = p;
            }
        }
    }

    public void Line(string text)
    {
        _buffer.Add(text);
        if (_buffer.Count > 2000)
        {
            _buffer.RemoveAt(0);
        }
        AddLine(text);
    }

    private void AddLine(string text)
    {
        if (_lines == null || !IsInstanceValid(_lines))
        {
            return;
        }
        var color = text.Contains("err", StringComparison.OrdinalIgnoreCase) && !text.Contains("stderr", StringComparison.Ordinal) ? UiTheme.Warn : UiTheme.Dim;
        _lines.AddChild(UiTheme.Label(text, UiTheme.SmallSize, color, wrap: true));
        if (_lines.GetChildCount() > 400)
        {
            _lines.GetChild(0).QueueFree();
        }
        Callable.From(() =>
        {
            if (_scroll != null && IsInstanceValid(_scroll))
            {
                _scroll.ScrollVertical = (int)_scroll.GetVScrollBar().MaxValue;
            }
        }).CallDeferred();
    }

    public void Done(bool ok, string message)
    {
        _done = true;
        if (!_output || !IsInsideTree())
        {
            // Plain progress closes itself; the toast says how it went.
            if (IsInsideTree())
            {
                Main.Instance.PopTo(this);
                Main.Instance.Pop();
            }
            return;
        }
        // Output stays readable until the player closes it.
        Progress(1);
        if (_result != null)
        {
            _result.Text = message;
            _result.AddThemeColorOverride("font_color", ok ? UiTheme.Good : UiTheme.Bad);
        }
        if (_button != null)
        {
            _button.Text = "Close";
            _button.TooltipText = "Back to where you were.";
            _button.GrabFocus();
        }
    }

    public override void FocusDefault() => _button?.GrabFocus();

    public override bool OnBack()
    {
        if (_done)
        {
            return false;
        }
        _cts.Cancel();
        return true;
    }
}

/// <summary>
/// A Proton log: hints first (missing runtimes, crashes, with a button to install the fix), then the
/// newest lines, errors red and warnings amber. Y shows only problems.
/// </summary>
public partial class LogViewerPage : Screen
{
    private readonly string _path;
    private bool _problemsOnly;
    private Control? _first;

    public LogViewerPage(string path) => _path = path;

    public override IEnumerable<(string Action, string Label)> Hints => [(Pad.Secondary, _problemsOnly ? "Show all" : "Problems only")];

    protected override void Build()
    {
        var col = Column(6);
        AddChild(Scroller(col));
        col.AddChild(UiTheme.Label(Path.GetFileName(_path), UiTheme.TitleSize));
        col.AddChild(UiTheme.Label(_path, UiTheme.SmallSize, UiTheme.Dim));
        var lines = Try(() => ProtonLogs.Tail(_path), (IReadOnlyList<string>)[], "the log", col);
        var items = new List<Control>();
        var hints = ProtonLogs.Hints(lines);
        if (hints.Count > 0)
        {
            col.AddChild(Section("What it suggests"));
            var game = Host.State.Games.FirstOrDefault(g => g.Log?.Path == _path);
            foreach (var h in hints)
            {
                col.AddChild(UiTheme.Label("• " + h.Text, UiTheme.SmallSize + 2, UiTheme.Warn, wrap: true));
                if (h.Verb != null && game?.Prefix != null)
                {
                    var tricks = Host.Actions.ForPrefix(game.Prefix, game).First(a => a.Id == ActionId.RunTricks);
                    var verb = h.Verb;
                    var b = new ActionButton($"Install {Core.Tricks.TricksCatalog.Find(verb)?.Label ?? verb}", () =>
                        ActionFlow.Start(tricks with { Input = tricks.Input! with { Choices = tricks.Input.Choices.Select(c => c with { Selected = c.Id == verb }).ToList() } }, Main.Instance.Shell),
                        "Runs protontricks for this game's prefix with this verb ticked.");
                    col.AddChild(b);
                    items.Add(b);
                }
            }
        }
        var shown = _problemsOnly ? lines.Where(l => ProtonLogs.Classify(l) != LogLevel.Info).ToList() : lines.ToList();
        col.AddChild(Section(_problemsOnly ? $"Errors and warnings ({shown.Count})" : $"Last {shown.Count} lines"));
        // Newest first, in blocks of ten so the d-pad moves a screenful at a time.
        foreach (var chunk in Enumerable.Reverse(shown).Chunk(10))
        {
            var block = new PanelContainer { FocusMode = FocusModeEnum.All, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Log lines, newest first" };
            block.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 0, 8));
            var body = Column(2);
            block.AddChild(body);
            foreach (var l in chunk)
            {
                var color = ProtonLogs.Classify(l) switch { LogLevel.Error => UiTheme.Bad, LogLevel.Warn => UiTheme.Warn, _ => UiTheme.Text };
                body.AddChild(UiTheme.Label(l, UiTheme.SmallSize, color, wrap: true));
            }
            col.AddChild(block);
            items.Add(block);
        }
        if (shown.Count == 0)
        {
            col.AddChild(UiTheme.Label(_problemsOnly ? "No errors or warnings." : "The log is empty.", UiTheme.SmallSize + 2, UiTheme.Dim));
        }
        var back = new ActionButton("Back", () => Main.Instance.Pop());
        col.AddChild(back);
        items.Add(back);
        Focus.Column(items);
        _first = items[0];
    }

    public override void OnAction(string action)
    {
        if (action == Pad.Secondary)
        {
            _problemsOnly = !_problemsOnly;
            Rebuild();
            Main.Instance.UpdateChrome();
        }
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
