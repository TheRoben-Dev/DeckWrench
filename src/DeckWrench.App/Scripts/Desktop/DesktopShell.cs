using DeckWrench.App.Controller;
using DeckWrench.App.Screens;
using DeckWrench.App.Sections;
using DeckWrench.App.Ui;
using DeckWrench.Core.Storage;
using DeckWrench.Host;
using DeckWrench.Host.Toolbox;
using Godot;

namespace DeckWrench.App.Desktop;

/// <summary>
/// Desktop mode: a traditional utility window. Menu bar on top, sections in a sidebar, a sortable
/// table in the middle, a detail pane with the selected item's facts and actions on the right,
/// and a status bar. Right-click a row for its actions; double-click runs the first one.
/// Dialogs are windows. Every action comes from the same <see cref="Section"/> lists the
/// controller mode shows, so both modes always offer the same features.
/// </summary>
public partial class DesktopShell : Control, IShell
{
    private const string SettingsName = "Settings";

    private ItemList _sidebar = null!;
    private Label _title = null!;
    private Label _banner = null!;
    private HBoxContainer _toolbar = null!;
    private Tree _table = null!;
    private VBoxContainer _detail = null!;
    private ScrollContainer _detailScroll = null!;
    private Control _browse = null!;
    private Control _settingsHost = null!;
    private VBoxContainer _drives = null!;
    private Label _status = null!;
    private PopupMenu _context = null!;
    private Section? _section;
    private string? _selectedKey;
    private readonly List<(Window Window, Screen Screen)> _pages = new();
    private IReadOnlyList<Row> _rows = [];
    private IReadOnlyList<ToolAction> _contextActions = [];

    private static Main App => Main.Instance;
    private static AppHost Host => Main.Instance.Host!;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var root = new VBoxContainer();
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        root.AddChild(BuildMenuBar());
        root.AddChild(new HSeparator());

        var main = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        var mainPad = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            mainPad.AddThemeConstantOverride("margin_" + side, 8);
        }
        mainPad.AddChild(main);
        root.AddChild(mainPad);

        _sidebar = new ItemList { CustomMinimumSize = new Vector2(190, 0), SizeFlagsVertical = SizeFlags.ExpandFill, AllowReselect = true };
        foreach (var s in Section.All)
        {
            var i = _sidebar.AddItem(s.Name);
            _sidebar.SetItemTooltip(i, s.Summary);
        }
        var si = _sidebar.AddItem(SettingsName);
        _sidebar.SetItemTooltip(si, "Theme, mode, sounds, updates, Add to Steam, app log.");
        _sidebar.ItemSelected += i => ShowSection((int)i);
        main.AddChild(_sidebar);

        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 8);
        main.AddChild(right);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 12);
        _title = UiTheme.Label("", 22, UiTheme.Text);
        head.AddChild(_title);
        _banner = UiTheme.Label("", DesktopTheme.Size, UiTheme.Dim);
        _banner.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _banner.VerticalAlignment = VerticalAlignment.Center;
        _banner.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        head.AddChild(_banner);
        right.AddChild(head);
        _toolbar = new HBoxContainer();
        _toolbar.AddThemeConstantOverride("separation", 8);
        right.AddChild(_toolbar);

        _browse = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        ((VBoxContainer)_browse).AddThemeConstantOverride("separation", 8);
        right.AddChild(_browse);
        _drives = new VBoxContainer();
        _drives.AddThemeConstantOverride("separation", 10);
        _browse.AddChild(_drives);
        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _browse.AddChild(split);
        _table = new Tree
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.7f,
            HideRoot = true,
            ColumnTitlesVisible = true,
            SelectMode = Tree.SelectModeEnum.Row,
            AllowRmbSelect = true,
        };
        _table.ItemSelected += OnRowSelected;
        _table.ItemActivated += OnRowActivated;
        _table.ColumnTitleClicked += OnColumnClicked;
        _table.ItemMouseSelected += (pos, button) =>
        {
            if (button == (long)MouseButton.Right)
            {
                ShowContextMenu(_table.GetScreenTransform() * pos);
            }
        };
        split.AddChild(_table);
        var detailPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(340, 0) };
        detailPanel.AddThemeStyleboxOverride("panel", DesktopBoxes.Panel());
        _detailScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _detail = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _detail.AddThemeConstantOverride("separation", 6);
        _detailScroll.AddChild(_detail);
        detailPanel.AddChild(_detailScroll);
        split.AddChild(detailPanel);

        _settingsHost = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill, Visible = false };
        right.AddChild(_settingsHost);

        var statusBar = new PanelContainer();
        statusBar.AddThemeStyleboxOverride("panel", DesktopBoxes.StatusBar());
        _status = UiTheme.Label("", DesktopTheme.Small, UiTheme.Dim);
        statusBar.AddChild(_status);
        root.AddChild(statusBar);

        _context = new PopupMenu();
        _context.IdPressed += id =>
        {
            if (id >= 0 && id < _contextActions.Count)
            {
                ActionFlow.Start(_contextActions[(int)id], this);
            }
        };
        AddChild(_context);

        _sidebar.Select(0);
        ShowSection(0);
    }

    // ---- menu bar ----

    private MenuBar BuildMenuBar()
    {
        var bar = new MenuBar { Flat = true };
        var file = new PopupMenu { Name = "File" };
        file.AddItem("Scan again", 1, Key.F5);
        file.AddItem("Apply waiting Steam changes", 2);
        file.AddItem("Discard waiting Steam changes", 3);
        file.AddSeparator();
        file.AddItem("Quit", 9, (Key)KeyModifierMask.MaskCtrl | Key.Q);
        file.AboutToPopup += () =>
        {
            var pending = Host.State.Pending.Count > 0;
            file.SetItemDisabled(file.GetItemIndex(2), !pending);
            file.SetItemDisabled(file.GetItemIndex(3), !pending);
        };
        file.IdPressed += id =>
        {
            switch (id)
            {
                case 1:
                    RunGlobal(ActionId.Rescan);
                    break;
                case 2:
                    RunGlobal(ActionId.ApplyPending);
                    break;
                case 3:
                    RunGlobal(ActionId.DiscardPending);
                    break;
                case 9:
                    GetTree().Quit();
                    break;
            }
        };
        bar.AddChild(file);

        var view = new PopupMenu { Name = "View" };
        for (var i = 0; i < Section.All.Count; i++)
        {
            view.AddItem(Section.All[i].Name, 100 + i, (Key)KeyModifierMask.MaskCtrl | (Key.Key1 + i));
        }
        view.AddItem(SettingsName, 100 + Section.All.Count, (Key)KeyModifierMask.MaskCtrl | Key.Comma);
        view.AddSeparator();
        view.AddItem("Switch to controller mode", 200);
        view.IdPressed += id =>
        {
            if (id >= 100 && id <= 100 + Section.All.Count)
            {
                _sidebar.Select((int)id - 100);
                ShowSection((int)id - 100);
            }
            else if (id == 200)
            {
                App.ApplyLayout(UiLayout.Controller);
            }
        };
        bar.AddChild(view);

        var tools = new PopupMenu { Name = "Tools" };
        tools.AddItem("Download GE-Proton…", 1);
        tools.AddItem("Default Proton for all games…", 2);
        tools.AddSeparator();
        tools.AddItem("Clean up…", 3);
        tools.AddItem("Clear orphaned shader caches…", 4);
        tools.AddItem("Delete all Proton logs…", 5);
        tools.IdPressed += id =>
        {
            ToolAction? a = id switch
            {
                1 => Host.Actions.ForProtonPage().First(x => x.Id == ActionId.InstallGeProton),
                2 => Host.Actions.ForProtonPage().First(x => x.Id == ActionId.SetDefaultTool),
                3 => Host.Actions.ForStoragePage()[0],
                4 => Host.Actions.ForShaderPage().First(x => x.Id == ActionId.ClearOrphanShaderCaches),
                5 => Host.Actions.ForLogsPage()[0],
                _ => null,
            };
            if (a != null)
            {
                ActionFlow.Start(a, this);
            }
        };
        bar.AddChild(tools);

        var help = new PopupMenu { Name = "Help" };
        help.AddItem("Changelog", 1);
        help.AddItem("Check for updates", 2);
        help.AddItem("App log", 3);
        help.AddSeparator();
        help.AddItem("About DeckWrench", 4);
        help.IdPressed += id =>
        {
            switch (id)
            {
                case 1:
                    App.ShowChangelog();
                    break;
                case 2:
                    App.CheckForAppUpdate(silent: false);
                    break;
                case 3:
                    Push(new LogPage());
                    break;
                case 4:
                    Push(new ChoicePage($"DeckWrench {Host.Updater.CurrentVersion.ToString(3)}\n\nA system toolbox for SteamOS, Bazzite and Windows: Proton versions, prefixes, shader caches, storage, launch options and Proton logs.\n\nSettings: {Host.Paths.Config}\nBackups: {Host.Backups.Dir}\nApp log: {AppLog.FilePath}",
                        ("OK", () => { })));
                    break;
            }
        };
        bar.AddChild(help);
        return bar;
    }

    private void RunGlobal(ActionId id)
    {
        if (Host.Actions.Global().FirstOrDefault(a => a.Id == id) is { } a)
        {
            ActionFlow.Start(a, this);
        }
    }

    // ---- sections and the table ----

    private void ShowSection(int index)
    {
        var settings = index >= Section.All.Count;
        _browse.Visible = !settings;
        _settingsHost.Visible = settings;
        foreach (var c in _settingsHost.GetChildren())
        {
            c.QueueFree();
        }
        foreach (var c in _toolbar.GetChildren())
        {
            c.QueueFree();
        }
        if (settings)
        {
            _section = null;
            _title.Text = SettingsName;
            _banner.Text = "";
            // The same settings page as controller mode, inside the window.
            _settingsHost.AddChild(new SettingsScreen());
            AppLog.Info("desktop -> Settings");
            return;
        }
        _section = Section.All[index];
        _selectedKey = null;
        AppLog.Info($"desktop -> {_section.Name}");
        StateChanged();
    }

    public void StateChanged()
    {
        if (!IsInsideTree())
        {
            return;
        }
        UpdateStatus();
        if (_section == null)
        {
            return;
        }
        _title.Text = _section.Name;
        _banner.Text = App.Scanning && Host.State.ScannedAt == default ? "Scanning…" : _section.Banner;
        foreach (var c in _toolbar.GetChildren())
        {
            c.QueueFree();
        }
        foreach (var a in _section.PageActions)
        {
            _toolbar.AddChild(ActionButton(a, compact: true));
        }
        foreach (var c in _drives.GetChildren())
        {
            c.QueueFree();
        }
        if (_section is StorageSection)
        {
            foreach (var d in Host.State.Drives)
            {
                _drives.AddChild(new DriveBar(d, compact: true));
            }
        }
        FillTable();
    }

    private void FillTable()
    {
        if (_section == null)
        {
            return;
        }
        var cols = _section.Columns;
        _table.Clear();
        _table.Columns = cols.Count;
        var (sortCol, desc) = SortFor(_section);
        for (var i = 0; i < cols.Count; i++)
        {
            _table.SetColumnTitle(i, cols[i] + (i == sortCol ? desc ? "  ▼" : "  ▲" : ""));
            _table.SetColumnTitleAlignment(i, HorizontalAlignment.Left);
            _table.SetColumnExpand(i, i == 0 || cols[i] is "Launch options" or "Folder");
            _table.SetColumnClipContent(i, true);
            _table.SetColumnCustomMinimumWidth(i, i == 0 ? 200 : cols[i] is "Launch options" or "Folder" or "Proton" or "Last Proton" ? 170 : 90);
        }
        var root = _table.CreateItem();
        _rows = Host.State.ScannedAt == default ? [] : _section.Rows();
        var sorted = _rows.OrderBy(r => r.Cells.Count > sortCol ? r.Cells[sortCol].Sort : "", Comparer<IComparable>.Create(CompareCells)).ToList();
        if (desc)
        {
            sorted.Reverse();
        }
        TreeItem? reselect = null;
        foreach (var r in sorted)
        {
            var item = _table.CreateItem(root);
            for (var i = 0; i < cols.Count && i < r.Cells.Count; i++)
            {
                var cell = r.Cells[i];
                item.SetText(i, cell.Text);
                item.SetTooltipText(i, cell.Text.Length > 0 ? cell.Text : r.Title);
                if (cell.Tone != Tone.Normal)
                {
                    item.SetCustomColor(i, Rows.ToneColor(cell.Tone));
                }
                if (cell.Sort is long && cols[i] is not "App id")
                {
                    item.SetTextAlignment(i, HorizontalAlignment.Right);
                }
            }
            item.SetMetadata(0, r.Key);
            if (r.Key == _selectedKey)
            {
                reselect = item;
            }
        }
        if (_rows.Count == 0)
        {
            var empty = _table.CreateItem(root);
            empty.SetText(0, Host.State.ScannedAt == default ? "Scanning…" : _section.EmptyText);
            empty.SetCustomColor(0, UiTheme.Dim);
            empty.SetSelectable(0, false);
        }
        if (reselect != null)
        {
            reselect.Select(0);
            _table.ScrollToItem(reselect);
        }
        else
        {
            _selectedKey = null;
            ShowDetail(null);
        }
    }

    private static int CompareCells(IComparable? a, IComparable? b)
    {
        if (a == null || b == null)
        {
            return a == null ? b == null ? 0 : -1 : 1;
        }
        if (a.GetType() != b.GetType())
        {
            return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        return a is string sa ? string.Compare(sa, (string)b, StringComparison.OrdinalIgnoreCase) : a.CompareTo(b);
    }

    private (int Column, bool Descending) SortFor(Section s)
    {
        if (Host.Settings.TableSort.GetValueOrDefault(s.Name) is { Length: > 0 } saved)
        {
            var desc = saved.StartsWith('-');
            var i = s.Columns.ToList().IndexOf(saved.TrimStart('-'));
            if (i >= 0)
            {
                return (i, desc);
            }
        }
        return s.DefaultSort;
    }

    public string CurrentTab => _section?.Name ?? SettingsName;

    /// <summary>The detail pane (the selected row's facts and actions).</summary>
    public Control DetailPane => _detail;

    /// <summary>Selects the row with this title (as a click would); false when it isn't in the table.</summary>
    public bool SelectRow(string title)
    {
        for (var it = _table.GetRoot()?.GetFirstChild(); it != null; it = it.GetNext())
        {
            var key = it.GetMetadata(0).AsString();
            if (_rows.FirstOrDefault(r => r.Key == key) is { } row && row.Title == title)
            {
                it.Select(0);
                _table.ScrollToItem(it);
                OnRowSelected();
                return true;
            }
        }
        return false;
    }

    /// <summary>Clicks a column title (sorts by it, or flips the order).</summary>
    public void SortBy(int column) => OnColumnClicked(column, (long)MouseButton.Left);

    /// <summary>The first row's title as shown, for checking sort order.</summary>
    public string? FirstRowTitle => _table.GetRoot()?.GetFirstChild()?.GetMetadata(0).AsString() is { } key ? _rows.FirstOrDefault(r => r.Key == key)?.Title : null;

    public void OpenContextMenu() => ShowContextMenu(GetViewport().GetMousePosition());

    private void OnColumnClicked(long column, long mouseButton)
    {
        if (_section == null || mouseButton != (long)MouseButton.Left)
        {
            return;
        }
        var (current, desc) = SortFor(_section);
        // Sizes and dates sort biggest/newest first on the first click.
        var firstDesc = _section.Rows().FirstOrDefault()?.Cells.ElementAtOrDefault((int)column).Sort is long or DateTime or int;
        var nextDesc = current == column ? !desc : firstDesc;
        var sort = new Dictionary<string, string>(Host.Settings.TableSort) { [_section.Name] = (nextDesc ? "-" : "") + _section.Columns[(int)column] };
        Host.SaveSettings(Host.Settings with { TableSort = sort });
        FillTable();
    }

    private Row? SelectedRow()
    {
        var item = _table.GetSelected();
        var key = item?.GetMetadata(0).AsString();
        return string.IsNullOrEmpty(key) ? null : _rows.FirstOrDefault(r => r.Key == key);
    }

    private void OnRowSelected()
    {
        var row = SelectedRow();
        _selectedKey = row?.Key;
        ShowDetail(row);
    }

    /// <summary>Double-click or Enter: the row's first action that can run.</summary>
    private void OnRowActivated()
    {
        if (SelectedRow() is { } row && _section != null &&
            _section.ActionsFor(row.Item).FirstOrDefault(a => a.Unavailable == null && !a.Destructive) is { } a)
        {
            ActionFlow.Start(a, this);
        }
    }

    private void ShowContextMenu(Vector2 at)
    {
        if (SelectedRow() is not { } row || _section == null)
        {
            return;
        }
        _context.Clear();
        _contextActions = _section.ActionsFor(row.Item);
        for (var i = 0; i < _contextActions.Count; i++)
        {
            var a = _contextActions[i];
            if (i > 0 && a.Destructive && !_contextActions[i - 1].Destructive)
            {
                _context.AddSeparator();
            }
            _context.AddItem(a.Label + (a.Input != null || a.Destructive ? "…" : ""), i);
            _context.SetItemDisabled(_context.GetItemIndex(i), a.Unavailable != null);
            _context.SetItemTooltip(_context.GetItemIndex(i), a.Unavailable ?? a.Help);
        }
        _context.Position = (Vector2I)at;
        _context.ResetSize();
        _context.Popup();
    }

    // ---- detail pane ----

    private void ShowDetail(Row? row)
    {
        foreach (var c in _detail.GetChildren())
        {
            _detail.RemoveChild(c);  // out of the pane now, not at the end of the frame
            c.QueueFree();
        }
        if (row == null || _section == null)
        {
            _detail.AddChild(UiTheme.Label(_section == null ? "" : $"Select a row to see its details and actions.\n\n{_section.Summary}", DesktopTheme.Size, UiTheme.Dim, wrap: true));
            return;
        }
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 12);
        if (row.Cover != null)
        {
            head.AddChild(new CoverArt(row.Title, row.Cover, new Vector2(60, 90), 4) { SizeFlagsVertical = SizeFlags.ShrinkBegin });
        }
        var title = UiTheme.Label(row.Title, 18, UiTheme.Text, wrap: true);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(title);
        _detail.AddChild(head);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 4);
        foreach (var f in _section.Facts(row.Item))
        {
            grid.AddChild(UiTheme.Label(f.Label, DesktopTheme.Small, UiTheme.Dim));
            var v = UiTheme.Label(f.Value, DesktopTheme.Small + 1, Rows.ToneColor(f.Tone), wrap: true);
            v.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            v.CustomMinimumSize = new Vector2(180, 0);
            grid.AddChild(v);
        }
        _detail.AddChild(grid);
        var notes = _section.Notes(row.Item);
        if (notes.Length > 0)
        {
            _detail.AddChild(UiTheme.Label(notes, DesktopTheme.Small, UiTheme.Dim, wrap: true));
        }
        _detail.AddChild(new HSeparator());
        _detail.AddChild(UiTheme.Label("Actions", DesktopTheme.Small, UiTheme.Dim));
        foreach (var a in _section.ActionsFor(row.Item))
        {
            _detail.AddChild(ActionButton(a, compact: false));
        }
    }

    private Button ActionButton(ToolAction a, bool compact)
    {
        var b = new Button
        {
            Text = a.Label + (a.Input != null || a.Destructive ? "…" : ""),
            TooltipText = a.Unavailable ?? a.Help,
            Alignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            Disabled = a.Unavailable != null,
            FocusMode = FocusModeEnum.All,
            ClipText = !compact,
        };
        b.SetMeta(Rows.ActionMeta, a.Id.ToString());
        if (a.Destructive && a.Unavailable == null)
        {
            b.AddThemeColorOverride("font_color", UiTheme.Bad.Lerp(UiTheme.Text, 0.3f));
        }
        b.Pressed += () => ActionFlow.Start(a, this);
        return b;
    }

    private void UpdateStatus()
    {
        if (_status == null)
        {
            return;
        }
        var s = Host.State;
        var parts = new List<string>();
        parts.Add(App.Scanning ? "Scanning…" : s.ScannedAt == default ? "Not scanned" : $"Scanned {s.ScannedAt.ToLocalTime():t}");
        parts.Add($"{s.Games.Count} games · {s.Tools.Count} Proton · {s.Prefixes.Count} prefixes");
        parts.Add(Core.Steam.SteamShortcuts.SteamRunning(Host.Paths.Home) ? "Steam running" : "Steam closed");
        if (s.Pending.Count > 0)
        {
            parts.Add($"{s.Pending.Count} change{(s.Pending.Count == 1 ? "" : "s")} waiting for Steam (File → Apply)");
        }
        if (s.Reclaimable > 0)
        {
            parts.Add($"{DiskSize.Format(s.Reclaimable)} can be freed");
        }
        _status.Text = string.Join("   ·   ", parts);
    }

    public void UpdateChrome() => UpdateStatus();

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            if (k.Keycode == Key.F5 || k.CtrlPressed && k.Keycode == Key.R)
            {
                RunGlobal(ActionId.Rescan);
                GetViewport().SetInputAsHandled();
            }
            else if (k.CtrlPressed && k.Keycode >= Key.Key1 && k.Keycode <= Key.Key9 && (int)(k.Keycode - Key.Key1) <= Section.All.Count)
            {
                var i = (int)(k.Keycode - Key.Key1);
                _sidebar.Select(i);
                ShowSection(i);
                GetViewport().SetInputAsHandled();
            }
            else if (k.Keycode == Key.Menu && SelectedRow() != null)
            {
                ShowContextMenu(_table.GetScreenTransform() * (_table.GetItemAreaRect(_table.GetSelected()).Position + new Vector2(40, 20)));
                GetViewport().SetInputAsHandled();
            }
        }
    }

    // ---- pages as windows ----

    public Screen? Top => _pages.Count > 0 ? _pages[^1].Screen : null;

    /// <summary>Pages the controller mode stacks (changelog, app log, questions) open as windows here.</summary>
    public void Push(Screen screen)
    {
        var window = DesktopDialogs.NewWindow(PageTitle(screen), new Vector2I(760, 540));
        window.CloseRequested += () =>
        {
            if (_pages.Count > 0 && _pages[^1].Screen == screen && !screen.OnBack())
            {
                Pop();
            }
        };
        var holder = new MarginContainer();
        holder.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            holder.AddThemeConstantOverride("margin_" + side, 12);
        }
        window.AddChild(holder);
        holder.AddChild(screen);
        AddChild(window);
        _pages.Add((window, screen));
        window.PopupCentered();
        AppLog.Info($"window {screen.GetType().Name}");
    }

    private static string PageTitle(Screen s) => s switch
    {
        ChangelogPage => "Changelog",
        LogPage => "App log",
        BusyPage => "Working…",
        _ => "DeckWrench",
    };

    public void Pop()
    {
        if (_pages.Count == 0)
        {
            return;
        }
        var (window, _) = _pages[^1];
        _pages.RemoveAt(_pages.Count - 1);
        window.Hide();
        window.QueueFree();
    }

    public void PopTo(Screen screen)
    {
        while (_pages.Count > 0 && _pages[^1].Screen != screen && _pages.Any(p => p.Screen == screen))
        {
            Pop();
        }
    }

    // ---- IActionUi: windows ----

    public void PickOne(ActionInput input, Action<string> picked) => DesktopDialogs.PickOne(this, input, picked);

    public void PickMany(ActionInput input, Action<string> picked) => DesktopDialogs.PickMany(this, input, picked);

    public void EnterText(ActionInput input, Action<string> entered) => DesktopDialogs.EnterText(this, input, entered);

    public void Confirm(ConfirmPlan plan, Action yes) => DesktopDialogs.Confirm(this, plan, yes);

    public IBusy Busy(string title, bool showOutput, CancellationTokenSource cts) => DesktopDialogs.Busy(this, title, showOutput, cts);

    public void OpenLog(string path) => DesktopDialogs.LogViewer(this, path);

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
                App.Toast(text);
                break;
        }
    }
}

/// <summary>Panel styles for desktop mode.</summary>
public static class DesktopBoxes
{
    public static StyleBoxFlat Panel()
    {
        var b = new StyleBoxFlat { BgColor = UiTheme.Panel, BorderColor = UiTheme.Line };
        b.SetBorderWidthAll(1);
        b.SetCornerRadiusAll(Math.Min(4, UiTheme.Spec.Radius));
        b.ContentMarginLeft = b.ContentMarginRight = 12;
        b.ContentMarginTop = b.ContentMarginBottom = 10;
        return b;
    }

    public static StyleBoxFlat StatusBar()
    {
        var b = new StyleBoxFlat { BgColor = UiTheme.Panel, BorderColor = UiTheme.Line, BorderWidthTop = 1 };
        b.ContentMarginLeft = b.ContentMarginRight = 10;
        b.ContentMarginTop = b.ContentMarginBottom = 4;
        return b;
    }
}
