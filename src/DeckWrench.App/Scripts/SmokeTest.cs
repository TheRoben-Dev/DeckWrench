using DeckWrench.App.Controller;
using DeckWrench.App.Sections;
using DeckWrench.App.Ui;
using DeckWrench.Host.Toolbox;
using Godot;

namespace DeckWrench.App;

/// <summary>
/// `DeckWrench -- --smoke &lt;dir&gt;`: drives the UI with scripted input, saves a screenshot after
/// each step and quits. Used by CI (under Xvfb) to catch startup and navigation regressions,
/// and to publish screenshots of each build. Exit code 1 if any step throws.
/// </summary>
public partial class SmokeTest : Node
{
    private readonly string _outDir;
    private readonly Queue<(string Name, Action Act, double Wait, string? Expect, string? Tab)> _steps = new();
    private double _wait = 1.0;
    private int _shot;
    private int _failures;

    private const string Elden = "1245620";
    private IReadOnlyList<string> _controllerActions = [];

    public SmokeTest(string outDir)
    {
        _outDir = outDir;
        Directory.CreateDirectory(outDir);
        // The animated background costs a lot under software rendering (CI) and would skew timing.
        // The run script sets up a fake Steam home (games on internal and SD, Proton versions,
        // prefixes, shader caches, a Proton log) and a stand-in GE-Proton releases API.
        Step("games-after-scan", () => SetAnimatedBackground(false), wait: 3.0, tab: "Games");
        Step("scan-found-everything", () =>
        {
            var st = Main.Instance.Host!.State;
            Check(st.Games.Count == 5, $"expected 5 games, found {st.Games.Count}");
            Check(st.Tools.Any(t => t.Name == "GE-Proton9-20"), "GE-Proton9-20 not found");
            Check(st.Prefixes.Any(p => p.Orphan), "the orphaned prefix wasn't flagged");
            Check(st.ShaderCaches.Any(p => p.Orphan), "the orphaned shader cache wasn't flagged");
            Check(st.Logs.Count == 1, "the Proton log wasn't found");
            Check(st.Reclaimable > 0, "nothing to clean up");
            Focusable("ELDEN RING:")?.GrabFocus();
        }, expect: "ELDEN RING:", tab: "Games");
        // Real joypad events through Pad's translation layer.
        Step("joy-dpad-down", () => Tap(JoyButton.DpadDown), tab: "Games");
        Step("joy-dpad-up", () => Tap(JoyButton.DpadUp), expect: "ELDEN RING:", tab: "Games");
        Step("game-page", () => Tap(JoyButton.A), wait: 1.2, expect: "Proton: GE-Proton9-20");
        Step("game-page-parity", () => _controllerActions = ActionIds());
        // Per-game compatibility tool: pick Proton Experimental; Steam is closed, so it's written straight away.
        Step("compat-pick", () => Tap(JoyButton.A), expect: "GE-Proton9-20");
        Step("compat-pick-experimental", () => Focusable("Proton - Experimental")?.GrabFocus(), expect: "Proton - Experimental");
        Step("compat-set", () => Tap(JoyButton.A), wait: 2.5);
        Step("compat-written", () =>
        {
            var g = Main.Instance.Host!.State.Games.First(x => x.AppId == Elden);
            Check(g.EffectiveCompatTool == "proton_experimental", $"compat tool is {g.EffectiveCompatTool}");
            var cfg = File.ReadAllText(Path.Combine(Main.Instance.Host.State.SteamRoot!, "config", "config.vdf"));
            Check(cfg.Contains("proton_experimental"), "config.vdf wasn't updated");
            ActionButton(ActionId.LaunchPresets)?.GrabFocus();
        }, expect: "Launch option presets");
        Step("presets", () => Tap(JoyButton.A), expect: "Apply");
        Step("presets-mangohud", () =>
        {
            Focusable("MangoHud overlay")?.GrabFocus();
            Tap(JoyButton.A);
        }, expect: "MangoHud overlay");
        Step("presets-apply", () => Tap(JoyButton.Y), wait: 2.5);
        Step("presets-written", () =>
        {
            var g = Main.Instance.Host!.State.Games.First(x => x.AppId == Elden);
            Check(g.EffectiveLaunchOptions.Contains("mangohud"), $"launch options are '{g.EffectiveLaunchOptions}'");
            Check(g.EffectiveLaunchOptions.Contains("%command%"), "lost %command%");
        });
        // The game's prefix: back it up, then look at (and cancel) a reset.
        Step("prefix-backup", () => ActionButton(ActionId.BackupPrefix)?.GrabFocus(), expect: "Back up prefix");
        Step("prefix-backup-run", () => Tap(JoyButton.A), wait: 3.0);
        Step("prefix-backup-made", () =>
        {
            Check(Main.Instance.Host!.Backups.List().Any(b => b.AppId == Elden), "no backup archive");
            ActionButton(ActionId.ResetPrefix)?.GrabFocus();
        }, expect: "Reset prefix");
        Step("prefix-reset-plan", () => Tap(JoyButton.A), wait: 1.5, expect: "Cancel");
        Step("prefix-reset-cancel", () => Tap(JoyButton.B), wait: 0.8, expect: "Reset prefix");
        Step("prefix-reset-kept", () => Check(Directory.Exists(Main.Instance.Host!.State.Games.First(x => x.AppId == Elden).Prefix!.Dir), "cancel removed the prefix"));
        Step("game-back", () => Tap(JoyButton.B), expect: "ELDEN RING:", tab: "Games");
        // Bumpers go through every tab and wrap.
        Step("tab-proton", () => Tap(JoyButton.RightShoulder), tab: "Proton");
        Step("tab-prefixes", () => Tap(JoyButton.RightShoulder), tab: "Prefixes");
        Step("tab-shaders", () => Tap(JoyButton.RightShoulder), tab: "Shaders");
        Step("tab-storage", () => Tap(JoyButton.RightShoulder), tab: "Storage");
        Step("tab-logs", () => Tap(JoyButton.RightShoulder), tab: "Logs");
        Step("tab-settings", () => Tap(JoyButton.RightShoulder), tab: "Settings");
        Step("tab-wraps", () => Tap(JoyButton.RightShoulder), tab: "Games");
        Step("tab-lb-wraps", () => Tap(JoyButton.LeftShoulder), tab: "Settings");
        // Proton: GE-Proton releases from the stand-in API.
        Step("proton-tab", () => Key(Godot.Key.Key2, ctrl: true), tab: "Proton");
        Step("ge-download", () =>
        {
            ActionButton(ActionId.InstallGeProton)?.GrabFocus();
            Tap(JoyButton.A);
        }, wait: 2.5, expect: "GE-Proton10-1");
        Step("ge-download-back", () => Tap(JoyButton.B), tab: "Proton");
        Step("proton-tool-page", () =>
        {
            Focusable("GE-Proton8-25:")?.GrabFocus();
            Tap(JoyButton.A);
        }, wait: 1.2);
        Step("proton-tool-back", () => Tap(JoyButton.B), tab: "Proton");
        // Prefixes: the orphan is flagged; its delete shows size and contents.
        Step("prefixes-tab", () => Key(Godot.Key.Key3, ctrl: true), tab: "Prefixes");
        Step("orphan-prefix", () =>
        {
            Focusable("App 271590:")?.GrabFocus();
            Tap(JoyButton.A);
        }, wait: 1.2);
        Step("orphan-delete-plan", () =>
        {
            ActionButton(ActionId.DeletePrefix)?.GrabFocus();
            Tap(JoyButton.A);
        }, wait: 1.5, expect: "Cancel");
        Step("orphan-delete-cancel", () => Tap(JoyButton.B), expect: "Delete prefix");
        Step("orphan-back", () => Tap(JoyButton.B), tab: "Prefixes");
        Step("shaders-tab", () => Key(Godot.Key.Key4, ctrl: true), tab: "Shaders");
        // Storage: drive bars and the cleanup picker.
        Step("storage-tab", () => Key(Godot.Key.Key5, ctrl: true), tab: "Storage");
        Step("cleanup", () =>
        {
            ActionButton(ActionId.Cleanup)?.GrabFocus();
            Tap(JoyButton.A);
        }, wait: 1.2, expect: "Apply");
        Step("cleanup-back", () => Tap(JoyButton.B), tab: "Storage");
        // Logs: the viewer suggests the missing runtimes.
        Step("logs-tab", () => Key(Godot.Key.Key6, ctrl: true), tab: "Logs");
        Step("log-page", () =>
        {
            Focusable("ELDEN RING:")?.GrabFocus();
            Tap(JoyButton.A);
        }, wait: 1.2, expect: "View Proton log");
        Step("log-viewer", () => Tap(JoyButton.A), wait: 1.2, expect: "Install Visual C++");
        Step("log-problems-only", () => Tap(JoyButton.Y), wait: 0.8);
        Step("log-back", () => Tap(JoyButton.B), expect: "View Proton log");
        Step("log-page-back", () => Tap(JoyButton.B), tab: "Logs");
        // View / Select: status and global actions.
        Step("status", () => Tap(JoyButton.Back), expect: "Scan again");
        Step("status-back", () => Tap(JoyButton.B), tab: "Logs");
        Step("rescan", () => Tap(JoyButton.Y), wait: 2.5, tab: "Logs");
        // Settings: categories and panes.
        Step("settings", () => Key(Godot.Key.Key7, ctrl: true), tab: "Settings");
        Step("settings-scanning", () => Press(Pad.Accept), expect: "Scan again");
        Step("settings-scanning-back", () => Press(Pad.Back), expect: "Scanning");
        Step("settings-appearance", () => Focusable("Theme, accent colour")?.GrabFocus(), expect: "Appearance");
        Step("settings-appearance-pane", () => Press(Pad.Accept), expect: "Pick a theme");
        Step("settings-appearance-back", () => Press(Pad.Back), expect: "Appearance");
        Step("settings-layout", () => Focusable("Controller or desktop layout")?.GrabFocus(), expect: "Layout");
        Step("settings-layout-pane", () => Press(Pad.Accept), expect: "Controller: full screen");
        Step("settings-layout-back", () => Press(Pad.Back), expect: "Layout");
        Step("settings-about", () =>
        {
            Focusable("Where DeckWrench keeps")?.GrabFocus();
            Press(Pad.Accept);
        }, expect: "Open the app log");
        Step("app-log", () => Press(Pad.Accept), expect: "Log lines");
        Step("app-log-back", () => Press(Pad.Back), expect: "Open the app log");
        Step("settings-about-back", () => Press(Pad.Back), expect: "About");
        Step("changelog", () =>
        {
            Focusable("Version, changelog")?.GrabFocus();
            Press(Pad.Accept);  // into Updates: first row is Changelog
            Press(Pad.Accept);
        });
        Step("changelog-back", () => Press(Pad.Back), expect: "Changelog");

        // ---- desktop mode: menu bar, sidebar, sortable tables, detail pane, windows ----
        Step("desktop", () => Main.Instance.ApplyLayout(UiLayout.DesktopId), wait: 1.5, tab: "Games");
        Step("desktop-select", () => Check(Desktop.SelectRow("ELDEN RING"), "ELDEN RING isn't in the table"), wait: 0.8, tab: "Games");
        Step("desktop-parity", () =>
        {
            var desktop = ActionIds(Desktop.DetailPane);
            Check(desktop.Count > 0, "the detail pane has no actions");
            Check(desktop.SequenceEqual(_controllerActions),
                $"actions differ between modes: controller [{string.Join(", ", _controllerActions)}], desktop [{string.Join(", ", desktop)}]");
        });
        Step("desktop-sort-size", () =>
        {
            var size = Main.Instance.Host!.State.Games.Count > 0 ? Section.Get("Games").Columns.ToList().IndexOf("Size") : -1;
            Check(size >= 0, "no Size column");
            Desktop.SortBy(size);
            var biggest = Main.Instance.Host.State.Games.MaxBy(g => g.Size)!.Title;
            var smallest = Main.Instance.Host.State.Games.MinBy(g => g.Size)!.Title;
            var first = Desktop.FirstRowTitle;
            Check(first == biggest || first == smallest, $"sorting by size put {first} first");
        }, tab: "Games");
        Step("desktop-sort-flip", () => Desktop.SortBy(Section.Get("Games").Columns.ToList().IndexOf("Size")), tab: "Games");
        Step("desktop-compat-dialog", () =>
        {
            Desktop.SelectRow("ELDEN RING");
            ActionButton(ActionId.SetCompatTool)?.EmitSignal(BaseButton.SignalName.Pressed);
        }, wait: 1.0);
        Step("desktop-compat-cancel", () => Check(PressInWindow("Cancel"), "no choice window"), tab: "Games");
        Step("desktop-launch-edit", () => ActionButton(ActionId.EditLaunchOptions)?.EmitSignal(BaseButton.SignalName.Pressed), wait: 1.0);
        Step("desktop-launch-cancel", () => Check(PressInWindow("Cancel"), "no text window"), tab: "Games");
        Step("desktop-context-menu", () =>
        {
            Desktop.SelectRow("Hades");
            Desktop.OpenContextMenu();
        }, wait: 0.8);
        Step("desktop-context-close", () => Key(Godot.Key.Escape), tab: "Games");
        Step("desktop-proton", () => Key(Godot.Key.Key2, ctrl: true), tab: "Proton");
        Step("desktop-prefixes", () => Key(Godot.Key.Key3, ctrl: true), tab: "Prefixes");
        Step("desktop-orphan", () => Check(Desktop.SelectRow("App 271590"), "orphan prefix isn't listed"), tab: "Prefixes");
        Step("desktop-delete-plan", () => ActionButton(ActionId.DeletePrefix)?.EmitSignal(BaseButton.SignalName.Pressed), wait: 1.5);
        Step("desktop-delete-cancel", () =>
        {
            Check(PressInWindow("Cancel"), "no confirmation window");
            Check(Main.Instance.Host!.State.Prefixes.Any(p => p.AppId == "271590"), "cancel deleted the prefix");
        }, tab: "Prefixes");
        Step("desktop-shaders", () => Key(Godot.Key.Key4, ctrl: true), tab: "Shaders");
        Step("desktop-storage", () => Key(Godot.Key.Key5, ctrl: true), tab: "Storage");
        Step("desktop-cleanup", () => ActionButton(ActionId.Cleanup)?.EmitSignal(BaseButton.SignalName.Pressed), wait: 1.0);
        Step("desktop-cleanup-cancel", () => Check(PressInWindow("Cancel"), "no cleanup window"), tab: "Storage");
        Step("desktop-logs", () => Key(Godot.Key.Key6, ctrl: true), tab: "Logs");
        Step("desktop-log-viewer", () =>
        {
            Check(Desktop.SelectRow("ELDEN RING"), "the log isn't listed");
            ActionButton(ActionId.ViewLog)?.EmitSignal(BaseButton.SignalName.Pressed);
        }, wait: 1.2);
        Step("desktop-log-close", () => Check(PressInWindow("Close"), "no log window"), tab: "Logs");
        Step("desktop-settings", () => Key(Godot.Key.Key7, ctrl: true), wait: 1.0, tab: "Settings");
        Step("desktop-rescan", () => Key(Godot.Key.F5), wait: 2.5, tab: "Settings");
        Step("controller-again", () => Main.Instance.ApplyLayout(UiLayout.Controller), wait: 1.5, tab: "Games");
    }

    private static DeckWrench.App.Desktop.DesktopShell Desktop =>
        Main.Instance.Shell as DeckWrench.App.Desktop.DesktopShell ?? throw new InvalidOperationException("not in desktop mode");

    private static void Check(bool ok, string what)
    {
        if (!ok)
        {
            throw new InvalidOperationException(what);
        }
    }

    private Control? Focusable(string tooltip) => FindFocusable(GetTree().Root, tooltip);

    /// <summary>The visible button for this action (controller page or desktop detail pane).</summary>
    private Button? ActionButton(ActionId id) => AllButtons(GetTree().Root)
        .FirstOrDefault(b => b.IsVisibleInTree() && b.HasMeta(Rows.ActionMeta) && b.GetMeta(Rows.ActionMeta).AsString() == id.ToString());

    /// <summary>The action ids on screen, in order: what parity compares.</summary>
    private IReadOnlyList<string> ActionIds(Node? root = null) => AllButtons(root ?? GetTree().Root)
        .Where(b => b.IsVisibleInTree() && b.HasMeta(Rows.ActionMeta))
        .Select(b => b.GetMeta(Rows.ActionMeta).AsString())
        .ToList();

    private static IEnumerable<Button> AllButtons(Node root)
    {
        foreach (var c in root.GetChildren())
        {
            if (c is Button b)
            {
                yield return b;
            }
            foreach (var d in AllButtons(c))
            {
                yield return d;
            }
        }
    }

    /// <summary>Presses a button in the topmost dialog window.</summary>
    private bool PressInWindow(string text)
    {
        var windows = Windows(GetTree().Root).Where(w => w.Visible && w != GetTree().Root).ToList();
        if (windows.Count == 0)
        {
            return false;
        }
        var b = AllButtons(windows[^1]).FirstOrDefault(x => x.Text == text);
        if (b == null)
        {
            return false;
        }
        b.EmitSignal(BaseButton.SignalName.Pressed);
        return true;
    }

    private static IEnumerable<Window> Windows(Node root)
    {
        foreach (var c in root.GetChildren())
        {
            if (c is Window w)
            {
                yield return w;
            }
            foreach (var d in Windows(c))
            {
                yield return d;
            }
        }
    }

    private static void TypeText(string text)
    {
        foreach (var ch in text)
        {
            var k = (Key)char.ToUpperInvariant(ch);
            Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Unicode = ch, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Unicode = ch, Pressed = false });
        }
        Input.FlushBufferedEvents();
    }

    private static void SetAnimatedBackground(bool on)
    {
        if (Main.Instance.Host is { } host)
        {
            host.SaveSettings(host.Settings with { AnimatedBackground = on });
            Main.Instance.RefreshBackground();
        }
    }

    private void Step(string name, Action act, double wait = 1.0, string? expect = null, string? tab = null) => _steps.Enqueue((name, act, wait, expect, tab));

    private static Control? FindFocusable(Node root, string tooltip)
    {
        foreach (var c in root.GetChildren())
        {
            if (c is Control { FocusMode: Control.FocusModeEnum.All } ctl && ctl.TooltipText.StartsWith(tooltip, StringComparison.Ordinal) && ctl.IsVisibleInTree())
            {
                return ctl;
            }
            if (FindFocusable(c, tooltip) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static void Joy(JoyButton b, bool pressed) =>
        Input.ParseInputEvent(new InputEventJoypadButton { Device = 0, ButtonIndex = b, Pressed = pressed });

    private static void Mouse(Vector2 at, bool pressed) =>
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = at, GlobalPosition = at });

    private static void RightClick(Control? c)
    {
        if (c == null)
        {
            return;
        }
        var at = c.GetViewport().GetFinalTransform() * c.GetGlobalRect().GetCenter();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        Input.FlushBufferedEvents();
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = at, GlobalPosition = at });
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = at, GlobalPosition = at });
        Input.FlushBufferedEvents();
    }

    private static void TouchTap(Control? c)
    {
        if (c == null)
        {
            return;
        }
        var at = c.GetGlobalRect().GetCenter();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        Mouse(at, true);
        Mouse(at, false);
    }

    private static void Tap(JoyButton b)
    {
        Joy(b, true);
        Joy(b, false);
        Input.FlushBufferedEvents();
    }

    private static void Stick(float x, float y)
    {
        Input.ParseInputEvent(new InputEventJoypadMotion { Device = 0, Axis = JoyAxis.LeftX, AxisValue = x });
        Input.ParseInputEvent(new InputEventJoypadMotion { Device = 0, Axis = JoyAxis.LeftY, AxisValue = y });
    }

    private static void Key(Key k, bool shift = false, bool ctrl = false)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, ShiftPressed = shift, CtrlPressed = ctrl, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, ShiftPressed = shift, CtrlPressed = ctrl, Pressed = false });
        Input.FlushBufferedEvents();
    }

    private static void Press(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
        Input.FlushBufferedEvents();
    }

    public override void _Process(double delta)
    {
        _wait -= delta;
        if (_wait > 0)
        {
            return;
        }
        if (_steps.Count == 0)
        {
            GD.Print($"smoke: done, {_shot} screenshots, {_failures} failures");
            GetTree().Quit(_failures == 0 ? 0 : 1);
            return;
        }
        var (name, act, wait, expect, tab) = _steps.Dequeue();
        try
        {
            act();
        }
        catch (Exception e)
        {
            _failures++;
            GD.PushError($"smoke: step {name} failed: {e}");
        }
        // Give layout and focus a few frames to settle, then capture.
        _wait = wait;
        GetTree().CreateTimer(wait - 0.15).Timeout += () => Capture(name, expect, tab);
    }

    private void Capture(string name, string? expect, string? tab)
    {
        if (tab != null && Main.Instance.Shell.CurrentTab != tab)
        {
            _failures++;
            GD.PushError($"smoke: step {name} failed: expected the {tab} tab, got {Main.Instance.Shell.CurrentTab}");
        }
        var focus = GetViewport().GuiGetFocusOwner();
        // Tiles draw their title with child labels, so fall back to the tooltip (which has the title).
        var text = focus is Button { Text: { Length: > 0 } t } ? t : focus?.TooltipText ?? "";
        if (expect != null && !text.Contains(expect))
        {
            _failures++;
            GD.PushError($"smoke: step {name} failed: expected focus on '{expect}', got '{text.Split('\n')[0]}'");
        }
        GD.Print($"smoke: {name}: focus={(focus == null ? "none" : focus.GetType().Name + " '" + text.Split('\n')[0] + "'")}");
        var img = GetViewport().GetTexture().GetImage();
        img?.SavePng(Path.Combine(_outDir, $"{++_shot:00}-{name}.png"));
    }
}
