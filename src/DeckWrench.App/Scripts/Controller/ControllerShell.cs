using DeckWrench.App.Screens;
using DeckWrench.App.Sections;
using DeckWrench.App.Ui;
using DeckWrench.Host;
using DeckWrench.Host.Toolbox;
using Godot;

namespace DeckWrench.App.Controller;

/// <summary>
/// Controller mode: full screen, big rows, no cursor. Tabs across the top (bumpers switch), a hint
/// bar at the bottom, pages on a stack (A opens, B goes back) and cards over a dimmed page for
/// choices and confirmations. Touch and mouse work everywhere.
/// </summary>
public partial class ControllerShell : Control, IShell
{
    public static readonly string[] TabNames = [.. Section.All.Select(s => s.Name), "Settings"];

    private readonly List<Screen> _stack = new();
    private Control _content = null!;
    private Control _overlay = null!;
    private Backdrop _backdrop = null!;
    private Header _header = null!;
    private HintBar _hints = null!;
    private int _tab;

    private static Main App => Main.Instance;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 40);
        margin.AddThemeConstantOverride("margin_right", 40);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        AddChild(margin);
        var root = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        root.AddThemeConstantOverride("separation", 16);
        margin.AddChild(root);
        _header = new Header(TabNames);
        _header.TabClicked += i =>
        {
            if (_stack.Count == 1)
            {
                SwitchTab(i);
            }
        };
        _header.GameClicked += OpenStatus;
        root.AddChild(_header);
        _content = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        root.AddChild(_content);
        _hints = new HintBar();
        root.AddChild(_hints);
        // Overlay layer (cards) above header and content; the hint bar stays visible below it.
        _overlay = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _overlay.OffsetBottom = -56;
        AddChild(_overlay);
        _backdrop = new Backdrop();
        _backdrop.Clicked += () =>
        {
            if (_stack.Count > 1 && _stack[^1].IsOverlay)
            {
                Pop();
            }
        };
        _overlay.AddChild(_backdrop);
        GetViewport().GuiFocusChanged += OnFocus;
        if (OS.GetCmdlineUserArgs().Contains("--open=appearance"))
        {
            // Back from a restart that applied a theme: Settings → Appearance.
            _tab = Array.IndexOf(TabNames, "Settings");
            var settings = new SettingsScreen { StartIn = "Appearance" };
            _stack.Add(settings);
            _content.AddChild(settings);
        }
        else
        {
            SwitchTab(0);
        }
        UpdateStatus();
        UpdateChrome();
    }

    public override void _ExitTree() => GetViewport().GuiFocusChanged -= OnFocus;

    private void OnFocus(Control c)
    {
        if (IsInstanceValid(_hints))
        {
            _hints.SetHelp(c.HasMeta(Stepper.ExpandsMeta) ? "" : c.TooltipText);
        }
    }

    // ---- scan results ----

    public void StateChanged()
    {
        UpdateStatus();
        foreach (var s in _stack)
        {
            if (s is IStateView v)
            {
                v.Refresh();
            }
        }
        UpdateChrome();
    }

    /// <summary>The chip next to the wordmark: scanning, pending Steam changes, or what can be freed.</summary>
    private void UpdateStatus()
    {
        var host = App.Host;
        if (host == null)
        {
            return;
        }
        var s = host.State;
        string text;
        if (App.Scanning)
        {
            text = "Scanning…";
        }
        else if (s.Pending.Count > 0)
        {
            text = $"{s.Pending.Count} change{(s.Pending.Count == 1 ? "" : "s")} waiting";
        }
        else
        {
            text = s.ScannedAt == default ? "Not scanned" : $"{Core.Storage.DiskSize.Format(s.Reclaimable)} to free";
        }
        _header.SetGame(text, null);
        _header.SetBadge(Array.IndexOf(TabNames, "Storage"), s.Reclaimable > 1L << 30);
    }

    /// <summary>View / Select: scan again, and apply or discard changes waiting for Steam.</summary>
    private void OpenStatus()
    {
        if (_stack.Count == 1 && Top is not StatusPage)
        {
            Push(new StatusPage());
        }
    }

    // ---- tabs and the page stack ----

    public string CurrentTab => TabNames[_tab];

    public void SwitchTab(string name)
    {
        var i = Array.IndexOf(TabNames, name);
        if (i >= 0)
        {
            while (_stack.Count > 1)
            {
                Pop();
            }
            SwitchTab(i);
        }
    }

    private void SwitchTab(int index)
    {
        AppLog.Info($"tab -> {TabNames[index]}");
        if (index != _tab)
        {
            Feedback.Play(Cue.Tab);
        }
        var dir = index == _tab ? 0 : (index - _tab + TabNames.Length) % TabNames.Length == 1 ? 1 : -1;
        _tab = index;
        foreach (var s in _stack)
        {
            if (dir != 0 && s.Visible && !s.IsOverlay)
            {
                SetFocusable(s, false);
                Anim.Exit(s, -dir);
            }
            else
            {
                s.QueueFree();
            }
        }
        _stack.Clear();
        Screen screen = TabNames[index] == "Settings" ? new SettingsScreen() : new SectionScreen(Section.Get(TabNames[index]));
        _stack.Add(screen);
        _content.AddChild(screen);
        if (dir != 0)
        {
            Anim.Enter(screen, dir);
        }
        else
        {
            Anim.FadeIn(screen);
        }
        UpdateChrome();
    }

    private bool OverlayOpen => _stack.Any(x => x.IsOverlay);

    public Screen? Top => _stack.Count > 0 ? _stack[^1] : null;

    private string StackNames() => string.Join(" > ", _stack.Select(x => x.GetType().Name));

    public void Push(Screen screen)
    {
        AppLog.Info($"push {screen.GetType().Name} over {StackNames()}");
        var top = Top;
        top?.Remember();
        if (screen.IsOverlay)
        {
            if (top != null)
            {
                SetFocusable(top, false);
            }
            _stack.Add(screen);
            _overlay.AddChild(screen);
            _backdrop.FadeTo(true);
            screen.AnimateOpen();
        }
        else
        {
            if (top != null)
            {
                top.Visible = false;
            }
            _stack.Add(screen);
            (OverlayOpen ? _overlay : _content).AddChild(screen);
            Anim.Enter(screen, 1);
        }
        UpdateChrome();
    }

    public void Pop()
    {
        AppLog.Info($"pop ({StackNames()})");
        if (_stack.Count <= 1)
        {
            return;
        }
        var top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        var prev = _stack[^1];
        if (top.IsOverlay)
        {
            top.AnimateClose(top.QueueFree);
            SetFocusable(prev, true);
            if (!OverlayOpen)
            {
                _backdrop.FadeTo(false);
            }
        }
        else
        {
            SetFocusable(top, false);
            Anim.Exit(top, 1);
            prev.Visible = true;
            Anim.Enter(prev, -1);
        }
        if (prev is IStateView v && v.Stale)
        {
            v.Refresh();
        }
        prev.Restore();
        UpdateChrome();
    }

    public void PopTo(Screen screen)
    {
        while (_stack.Count > 1 && _stack[^1] != screen && _stack.Contains(screen))
        {
            Pop();
        }
    }

    private static void SetFocusable(Node root, bool on)
    {
        foreach (var n in root.GetChildren())
        {
            if (n is Control c)
            {
                if (!on && c.FocusMode != FocusModeEnum.None)
                {
                    c.SetMeta("ed_focus", (int)c.FocusMode);
                    c.FocusMode = FocusModeEnum.None;
                }
                else if (on && c.HasMeta("ed_focus"))
                {
                    c.FocusMode = (FocusModeEnum)c.GetMeta("ed_focus").AsInt32();
                    c.RemoveMeta("ed_focus");
                }
            }
            SetFocusable(n, on);
        }
    }

    public void UpdateChrome()
    {
        if (!IsInsideTree())
        {
            return;
        }
        _header.SetTab(_tab, _stack.Count == 1);
        if (_stack.Count == 0)
        {
            return;
        }
        var hints = new List<(string, string)>();
        hints.AddRange(_stack[^1].Hints.Where(h => h.Action != Pad.Switch || _stack.Count == 1));
        hints.Add((Pad.Accept, "Select"));
        if (_stack.Count > 1)
        {
            hints.Add((Pad.Back, "Back"));
        }
        _hints.SetHints(hints);
    }

    // ---- input ----

    public override void _UnhandledInput(InputEvent e)
    {
        if (_stack.Count == 0)
        {
            return;
        }
        var top = _stack[^1];
        if (e.IsActionPressed(Pad.Back))
        {
            var handled = top.OnBack();
            if (!handled && _stack.Count > 1)
            {
                Pop();
                handled = true;
            }
            if (handled)
            {
                Feedback.Play(Cue.Back);
            }
        }
        else if (e.IsActionPressed(Pad.TabPrev) && _stack.Count == 1)
        {
            SwitchTab((_tab + TabNames.Length - 1) % TabNames.Length);
        }
        else if (e.IsActionPressed(Pad.TabNext) && _stack.Count == 1)
        {
            SwitchTab((_tab + 1) % TabNames.Length);
        }
        else if (e.IsActionPressed(Pad.Switch) && _stack.Count == 1)
        {
            OpenStatus();
        }
        else if (e.IsActionPressed(Pad.Secondary) || e.IsActionPressed(Pad.Tertiary))
        {
            top.OnAction(e.IsActionPressed(Pad.Secondary) ? Pad.Secondary : Pad.Tertiary);
        }
        else if (e is InputEventKey { Pressed: true, CtrlPressed: true, Keycode: >= Key.Key1 and <= Key.Key9 } k && _stack.Count == 1 &&
                 (int)(k.Keycode - Key.Key1) < TabNames.Length)
        {
            SwitchTab((int)(k.Keycode - Key.Key1));
        }
        else
        {
            return;
        }
        GetViewport().SetInputAsHandled();
    }

    // ---- IActionUi: pages and cards ----

    public void PickOne(ActionInput input, Action<string> picked) => Push(new PickPage(input, false, picked));

    public void PickMany(ActionInput input, Action<string> picked) => Push(new PickPage(input, true, picked));

    public void EnterText(ActionInput input, Action<string> entered) => Push(new TextInputPage(input, entered));

    public void Confirm(ConfirmPlan plan, Action yes) => Push(new PlanPage(plan, yes));

    public IBusy Busy(string title, bool showOutput, CancellationTokenSource cts)
    {
        var page = new WorkPage(title, showOutput, cts);
        Push(page);
        return page;
    }

    public void OpenLog(string path) => Push(new LogViewerPage(path));

    public void Notice(string text, Tone tone)
    {
        switch (tone)
        {
            case Tone.Bad:
                App.Error(text);
                break;
            case Tone.Good:
                App.Success(text);
                break;
            default:
                Feedback.Play(Cue.Error);
                App.Toast(text);
                break;
        }
    }
}

/// <summary>A page that shows scan results and rebuilds when a scan finishes.</summary>
public interface IStateView
{
    void Refresh();

    /// <summary>True when a scan finished while the page was hidden.</summary>
    bool Stale { get; }
}
