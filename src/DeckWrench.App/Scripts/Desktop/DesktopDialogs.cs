using DeckWrench.App.Controller;
using DeckWrench.App.Sections;
using DeckWrench.App.Ui;
using DeckWrench.Core.Logs;
using DeckWrench.Core.Storage;
using DeckWrench.Host;
using DeckWrench.Host.Toolbox;
using Godot;

namespace DeckWrench.App.Desktop;

/// <summary>
/// Desktop mode's dialogs: plain modal windows with the usual buttons (default on the safe one),
/// Enter and Escape, and tables where controller mode has rows. Same inputs and outcomes as the
/// controller pages, so an action behaves the same in both modes.
/// </summary>
public static class DesktopDialogs
{
    /// <summary>A modal window embedded in the main one; closes on Escape or the title bar's ×.</summary>
    public static Window NewWindow(string title, Vector2I size)
    {
        var w = new Window
        {
            Title = title,
            Size = size,
            MinSize = new Vector2I(Math.Min(size.X, 420), Math.Min(size.Y, 200)),
            Exclusive = true,
            Transient = true,
            WrapControls = false,
            Unresizable = false,
        };
        return w;
    }

    /// <summary>Window + margin + column; the caller adds content and buttons.</summary>
    private static (Window Window, VBoxContainer Body) Open(Control owner, string title, Vector2I size, Action? onClose = null)
    {
        var w = NewWindow(title, size);
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            margin.AddThemeConstantOverride("margin_" + side, 14);
        }
        w.AddChild(margin);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 10);
        margin.AddChild(body);
        var closing = false;
        w.CloseRequested += () =>
        {
            if (closing)
            {
                return;
            }
            closing = true;
            onClose?.Invoke();
            Close(w);
        };
        owner.AddChild(w);
        w.PopupCentered();
        AppLog.Info($"dialog '{title}'");
        return (w, body);
    }

    public static void Close(Window w)
    {
        if (GodotObject.IsInstanceValid(w))
        {
            w.Hide();
            w.QueueFree();
        }
    }

    private static Label Text(string s, int size = DesktopTheme.Size, Color? color = null, bool wrap = true)
    {
        var l = new Label { Text = s, AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off };
        l.AddThemeFontSizeOverride("font_size", size);
        if (color != null)
        {
            l.AddThemeColorOverride("font_color", color.Value);
        }
        return l;
    }

    private static HBoxContainer ButtonRow(VBoxContainer body)
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 8);
        body.AddChild(row);
        return row;
    }

    private static Button Btn(HBoxContainer row, string text, Action pressed, bool danger = false)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(96, 0) };
        if (danger)
        {
            b.AddThemeColorOverride("font_color", UiTheme.Bad.Lerp(UiTheme.Text, 0.2f));
            b.AddThemeColorOverride("font_hover_color", UiTheme.Bad.Lerp(UiTheme.Text, 0.4f));
        }
        b.Pressed += pressed;
        row.AddChild(b);
        return b;
    }

    // ---- choices ----

    public static void PickOne(Control owner, ActionInput input, Action<string> picked) => Pick(owner, input, false, picked);

    public static void PickMany(Control owner, ActionInput input, Action<string> picked) => Pick(owner, input, true, picked);

    /// <summary>A table of choices: radio-style (double-click or OK) or checkboxes (OK applies the ticked ones).</summary>
    private static void Pick(Control owner, ActionInput input, bool many, Action<string> picked)
    {
        var (w, body) = Open(owner, input.Prompt.Length > 0 ? input.Prompt : "Choose", new Vector2I(640, 480));
        var tree = new Tree
        {
            Columns = 2,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            ColumnTitlesVisible = false,
        };
        tree.SetColumnExpand(0, true);
        tree.SetColumnExpand(1, true);
        tree.SetColumnExpandRatio(1, 1);
        tree.SetColumnExpandRatio(0, 1);
        tree.SetColumnClipContent(1, true);
        body.AddChild(tree);
        var root = tree.CreateItem();
        var groups = new Dictionary<string, TreeItem>();
        TreeItem? firstSelected = null;
        TreeItem? first = null;
        foreach (var c in input.Choices)
        {
            var parent = root;
            if (c.Group.Length > 0)
            {
                if (!groups.TryGetValue(c.Group, out parent!))
                {
                    parent = tree.CreateItem(root);
                    parent.SetText(0, c.Group);
                    parent.SetSelectable(0, false);
                    parent.SetSelectable(1, false);
                    parent.SetCustomColor(0, UiTheme.Dim);
                    groups[c.Group] = parent;
                }
            }
            var it = tree.CreateItem(parent);
            if (many)
            {
                it.SetCellMode(0, TreeItem.TreeCellMode.Check);
                it.SetEditable(0, true);
                it.SetChecked(0, c.Selected);
            }
            it.SetText(0, c.Label);
            it.SetText(1, c.Detail);
            it.SetCustomColor(1, UiTheme.Dim);
            it.SetTooltipText(0, c.Detail);
            it.SetMetadata(0, c.Id);
            first ??= it;
            if (c.Selected)
            {
                firstSelected ??= it;
            }
        }
        var sel = firstSelected ?? first;
        if (sel != null)
        {
            sel.Select(0);
            tree.ScrollToItem(sel);
        }
        var info = Text(many ? "Tick what to include, then press OK." : "Double-click a row or select it and press OK.", DesktopTheme.Small, UiTheme.Dim);
        body.AddChild(info);
        var row = ButtonRow(body);
        if (many)
        {
            Btn(row, "Select all", () => ForEach(root, i => i.SetChecked(0, true)));
            Btn(row, "Select none", () => ForEach(root, i => i.SetChecked(0, false)));
            row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        }
        void Accept()
        {
            string value;
            if (many)
            {
                var ids = new List<string>();
                ForEach(root, i =>
                {
                    if (i.IsChecked(0))
                    {
                        ids.Add(i.GetMetadata(0).AsString());
                    }
                });
                if (ids.Count == 0)
                {
                    return;
                }
                value = string.Join('\n', ids);
            }
            else
            {
                var s = tree.GetSelected();
                if (s == null || s.GetMetadata(0).VariantType == Variant.Type.Nil)
                {
                    return;
                }
                value = s.GetMetadata(0).AsString();
            }
            Close(w);
            picked(value);
        }
        Btn(row, "Cancel", () => Close(w));
        var ok = Btn(row, "OK", Accept);
        tree.ItemActivated += () =>
        {
            if (many)
            {
                var s = tree.GetSelected();
                if (s != null && s.GetCellMode(0) == TreeItem.TreeCellMode.Check)
                {
                    s.SetChecked(0, !s.IsChecked(0));
                }
            }
            else
            {
                Accept();
            }
        };
        ok.SetMeta("dw_default", true);
        tree.CallDeferred(Control.MethodName.GrabFocus);
    }

    private static void ForEach(TreeItem root, Action<TreeItem> f)
    {
        for (var c = root.GetFirstChild(); c != null; c = c.GetNext())
        {
            if (c.GetMetadata(0).VariantType != Variant.Type.Nil)
            {
                f(c);
            }
            ForEach(c, f);
        }
    }

    // ---- text ----

    public static void EnterText(Control owner, ActionInput input, Action<string> entered)
    {
        var (w, body) = Open(owner, "Edit", new Vector2I(720, 220));
        body.AddChild(Text(input.Prompt));
        var field = new LineEdit { Text = input.Initial, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, PlaceholderText = "(empty)", Secret = input.Secret };
        body.AddChild(field);
        body.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        var row = ButtonRow(body);
        void Save()
        {
            Close(w);
            entered(field.Text.Trim());
        }
        Btn(row, "Clear", () => field.Text = "");
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        Btn(row, "Cancel", () => Close(w));
        Btn(row, "Save", Save);
        field.TextSubmitted += _ => Save();
        field.CallDeferred(Control.MethodName.GrabFocus);
        field.CaretColumn = field.Text.Length;
    }

    // ---- confirmation ----

    /// <summary>What goes and how big it is, in a table; Cancel has focus, the verb is red.</summary>
    public static void Confirm(Control owner, ConfirmPlan plan, Action yes)
    {
        var (w, body) = Open(owner, plan.Title, new Vector2I(700, plan.Contents.Count > 0 ? 520 : 260));
        body.AddChild(Text(plan.Title, DesktopTheme.Size + 4, UiTheme.Text));
        if (plan.TotalSize > 0 || plan.Contents.Count > 0)
        {
            body.AddChild(Text($"Total: {DiskSize.Format(plan.TotalSize)}" + (plan.Contents.Count > 0 ? $" in {plan.Contents.Count} item{(plan.Contents.Count == 1 ? "" : "s")}" : ""), DesktopTheme.Size, UiTheme.Text));
        }
        if (plan.Backup != null)
        {
            body.AddChild(Text("✓ A backup is made first: " + plan.Backup, DesktopTheme.Small + 1, UiTheme.Good));
        }
        if (plan.Caution != null)
        {
            body.AddChild(Text("⚠ " + plan.Caution, DesktopTheme.Small + 1, UiTheme.Warn));
        }
        if (plan.Contents.Count > 0)
        {
            var tree = new Tree { Columns = 2, HideRoot = true, ColumnTitlesVisible = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SelectMode = Tree.SelectModeEnum.Row };
            tree.SetColumnTitle(0, "Item");
            tree.SetColumnTitle(1, "Size");
            tree.SetColumnExpand(1, false);
            tree.SetColumnCustomMinimumWidth(1, 110);
            body.AddChild(tree);
            var root = tree.CreateItem();
            foreach (var (name, size) in plan.Contents.Take(500))
            {
                var it = tree.CreateItem(root);
                it.SetText(0, name);
                it.SetTooltipText(0, name);
                it.SetText(1, size > 0 ? DiskSize.Format(size) : "");
                it.SetTextAlignment(1, HorizontalAlignment.Right);
            }
            if (plan.Contents.Count > 500)
            {
                tree.CreateItem(root).SetText(0, $"…and {plan.Contents.Count - 500} more");
            }
        }
        else
        {
            body.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        }
        foreach (var p in plan.Paths.Take(3))
        {
            body.AddChild(Text(p, DesktopTheme.Small, UiTheme.Dim, wrap: false));
        }
        var row = ButtonRow(body);
        var cancel = Btn(row, "Cancel", () => Close(w));
        var go = Btn(row, plan.Verb, () =>
        {
            Close(w);
            yes();
        }, danger: true);
        go.SetMeta("dw_confirm", true);
        cancel.CallDeferred(Control.MethodName.GrabFocus);
    }

    // ---- progress ----

    public static IBusy Busy(Control owner, string title, bool showOutput, CancellationTokenSource cts)
    {
        var busy = new BusyWindow(title, showOutput, cts);
        busy.Show(owner);
        return busy;
    }

    private sealed class BusyWindow(string title, bool output, CancellationTokenSource cts) : IBusy
    {
        private Window? _w;
        private ProgressBar? _bar;
        private TextEdit? _log;
        private Label? _result;
        private Button? _button;
        private bool _done;

        public void Show(Control owner)
        {
            var (w, body) = Open(owner, title, output ? new Vector2I(760, 480) : new Vector2I(480, 150), () =>
            {
                if (!_done)
                {
                    cts.Cancel();
                }
            });
            _w = w;
            body.AddChild(Text(title, DesktopTheme.Size + 2));
            _bar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.001, Indeterminate = true, CustomMinimumSize = new Vector2(0, 12), ShowPercentage = false };
            body.AddChild(_bar);
            _result = Text("", DesktopTheme.Small + 1, UiTheme.Dim);
            body.AddChild(_result);
            if (output)
            {
                _log = new TextEdit { Editable = false, SizeFlagsVertical = Control.SizeFlags.ExpandFill, WrapMode = TextEdit.LineWrappingMode.Boundary };
                _log.AddThemeFontSizeOverride("font_size", DesktopTheme.Small);
                body.AddChild(_log);
            }
            else
            {
                body.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
            }
            var row = ButtonRow(body);
            _button = Btn(row, "Cancel", () =>
            {
                if (_done)
                {
                    Close(w);
                }
                else
                {
                    cts.Cancel();
                    _result!.Text = "Cancelling…";
                }
            });
        }

        private bool Alive => _w != null && GodotObject.IsInstanceValid(_w);

        public void Progress(double p)
        {
            if (Alive && _bar != null)
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
            if (!Alive || _log == null)
            {
                return;
            }
            _log.Text += (_log.Text.Length > 0 ? "\n" : "") + text;
            if (_log.GetLineCount() > 2000)
            {
                _log.RemoveText(0, 0, 1, 0);
            }
            _log.ScrollVertical = _log.GetLineCount();
        }

        public void Done(bool ok, string message)
        {
            _done = true;
            if (!Alive)
            {
                return;
            }
            if (!output)
            {
                Close(_w!);
                return;
            }
            _bar!.Indeterminate = false;
            _bar.Value = 1;
            _result!.Text = message;
            _result.AddThemeColorOverride("font_color", ok ? UiTheme.Good : UiTheme.Bad);
            _button!.Text = "Close";
            _button.GrabFocus();
        }
    }

    // ---- logs ----

    /// <summary>A Proton log: suggestions with install buttons, then the newest lines, colored; a filter for problems.</summary>
    public static void LogViewer(Control owner, string path)
    {
        var (w, body) = Open(owner, Path.GetFileName(path), new Vector2I(900, 600));
        body.AddChild(Text(path, DesktopTheme.Small, UiTheme.Dim, wrap: false));
        IReadOnlyList<string> lines;
        try
        {
            lines = ProtonLogs.Tail(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lines = [];
            body.AddChild(Text("Couldn't read the log: " + e.Message, DesktopTheme.Size, UiTheme.Bad));
        }
        var hints = ProtonLogs.Hints(lines);
        if (hints.Count > 0)
        {
            var box = new PanelContainer();
            box.AddThemeStyleboxOverride("panel", DesktopBoxes.Panel());
            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 6);
            box.AddChild(col);
            col.AddChild(Text("What it suggests", DesktopTheme.Small, UiTheme.Dim));
            var host = Main.Instance.Host!;
            var game = host.State.Games.FirstOrDefault(g => g.Log?.Path == path);
            foreach (var h in hints)
            {
                var r = new HBoxContainer();
                r.AddThemeConstantOverride("separation", 10);
                var l = Text("• " + h.Text, DesktopTheme.Size, UiTheme.Warn);
                l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                r.AddChild(l);
                if (h.Verb != null && game?.Prefix != null)
                {
                    var tricks = host.Actions.ForPrefix(game.Prefix, game).First(a => a.Id == ActionId.RunTricks);
                    var verb = h.Verb;
                    var b = new Button { Text = $"Install {Core.Tricks.TricksCatalog.Find(verb)?.Label ?? verb}", TooltipText = "Runs protontricks for this game's prefix with this verb ticked." };
                    b.Pressed += () => ActionFlow.Start(tricks with { Input = tricks.Input! with { Choices = tricks.Input.Choices.Select(c => c with { Selected = c.Id == verb }).ToList() } }, Main.Instance.Shell);
                    r.AddChild(b);
                }
                col.AddChild(r);
            }
            body.AddChild(box);
        }
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 10);
        var filter = new CheckBox { Text = "Errors and warnings only" };
        bar.AddChild(filter);
        var count = Text("", DesktopTheme.Small, UiTheme.Dim, wrap: false);
        count.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        count.HorizontalAlignment = HorizontalAlignment.Right;
        bar.AddChild(count);
        body.AddChild(bar);
        var view = new RichTextLabel
        {
            BbcodeEnabled = true,
            SelectionEnabled = true,
            ScrollFollowing = false,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            FocusMode = Control.FocusModeEnum.All,
        };
        view.AddThemeFontSizeOverride("normal_font_size", DesktopTheme.Small);
        view.AddThemeFontOverride("normal_font", UiTheme.MonoFont);
        view.AddThemeStyleboxOverride("normal", DesktopBoxes.Panel());
        body.AddChild(view);
        void Fill()
        {
            var shown = filter.ButtonPressed ? lines.Where(l => ProtonLogs.Classify(l) != LogLevel.Info).ToList() : lines.ToList();
            count.Text = filter.ButtonPressed ? $"{shown.Count} problem lines" : $"last {shown.Count} lines";
            view.Clear();
            if (shown.Count == 0)
            {
                view.PushColor(UiTheme.Dim);
                view.AddText(filter.ButtonPressed ? "No errors or warnings." : "The log is empty.");
                view.Pop();
                return;
            }
            foreach (var l in shown)
            {
                var c = ProtonLogs.Classify(l) switch { LogLevel.Error => UiTheme.Bad, LogLevel.Warn => UiTheme.Warn, _ => UiTheme.Text };
                view.PushColor(c);
                view.AddText(l + "\n");
                view.Pop();
            }
            view.CallDeferred(RichTextLabel.MethodName.ScrollToLine, Math.Max(0, view.GetLineCount() - 1));
        }
        filter.Toggled += _ => Fill();
        Fill();
        var row = ButtonRow(body);
        Btn(row, "Open folder", () => Opener.Open(Path.GetDirectoryName(path)!));
        Btn(row, "Close", () => Close(w));
        view.CallDeferred(Control.MethodName.GrabFocus);
    }
}
