using DeckWrench.App.Controller;
using DeckWrench.App.Desktop;
using DeckWrench.App.Screens;
using DeckWrench.App.Sections;
using DeckWrench.App.Ui;
using DeckWrench.Host;
using DeckWrench.Host.Updates;
using Godot;

namespace DeckWrench.App;

/// <summary>What Main needs from a UI mode: pages and dialogs, and a refresh after each scan.</summary>
public interface IShell : IActionUi
{
    /// <summary>The section (or "Settings") on screen.</summary>
    string CurrentTab { get; }

    /// <summary>Opens a page (controller: on the stack; desktop: in a window).</summary>
    void Push(Screen screen);

    void Pop();

    void PopTo(Screen screen);

    Screen? Top { get; }

    /// <summary>A scan finished (or started/stopped): refresh what's on screen.</summary>
    void StateChanged();

    void UpdateChrome();
}

/// <summary>
/// Root node: picks the UI mode (controller or desktop, two separate shells), owns the background,
/// sounds, toasts and the scan, and routes controller input. All non-UI work goes through
/// <see cref="AppHost"/>, the same code the headless CLI uses.
/// </summary>
public partial class Main : Control
{
    private Control _bgLayer = null!;
    private Control? _shellNode;
    private Toast _toast = null!;
    private FocusHighlight? _highlight;
    private bool _appFocused = true;
    private AnimatedBackground? _animBg;
    private Scanlines? _crt;
    private Feedback? _feedback;
    private ulong _navAtMs;

    public static Main Instance { get; private set; } = null!;

    /// <summary>Null only if startup failed (shown on screen).</summary>
    public AppHost? Host { get; private set; }

    public IShell Shell { get; private set; } = null!;

    /// <summary>True while a background scan runs.</summary>
    public bool Scanning { get; private set; }

    public override void _Ready()
    {
        Instance = this;
        Pad.Setup();
        Pad.FamilyChanged += () => Shell?.UpdateChrome();
        AppHost? host = null;
        Exception? startupError = null;
        try
        {
            host = new AppHost();
        }
        catch (Exception ex)
        {
            startupError = ex;
        }
        if (host != null)
        {
            AppLog.Open(Path.Combine(host.Paths.Data, "logs"));
        }
        if (startupError != null)
        {
            AppLog.Error("startup: " + startupError);
        }
        Host = host;
        var startTheme = host?.Settings.Theme ?? "dark";
        UiTheme.Use(startTheme, host?.Settings.Accents.GetValueOrDefault(startTheme));
        Anim.Enabled = !(host?.Settings.ReduceMotion ?? false);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var windowed = OS.GetCmdlineUserArgs().Contains("--windowed");
        if (windowed)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        }
        AppLog.Info($"theme {startTheme}, window {DisplayServer.WindowGetSize()}, pads: {string.Join(", ", Input.GetConnectedJoypads().Select(Input.GetJoyName))}");

        _bgLayer = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _bgLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_bgLayer);
        _toast = new Toast();
        _feedback = new Feedback(UiTheme.Name)
        {
            Volume = host?.Settings.SoundVolume ?? 50,
            Haptics = host?.Settings.Haptics ?? true,
        };
        AddChild(_feedback);
        // A tick when the player moves focus (not when a page puts focus somewhere itself).
        GetViewport().GuiFocusChanged += _ =>
        {
            if (Time.GetTicksMsec() - _navAtMs < 250)
            {
                Feedback.Play(Cue.Move);
            }
        };

        if (startupError is { } e)
        {
            Theme = UiTheme.Build();
            BuildBackground(desktop: false);
            GD.PushError(e.ToString());
            var msg = UiTheme.Label($"DeckWrench could not start:\n\n{e.Message}", wrap: true);
            msg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            AddChild(msg);
            return;
        }

        BuildShell(host!.Settings.Layout, windowed);
        // The first scan runs in the background; pages show "Scanning…" until it's in.
        Rescan(quiet: true);
        ApplyPendingAtStart();

        var args = OS.GetCmdlineUserArgs();
        var smoke = Array.IndexOf(args, "--smoke");
        var sounds = Array.IndexOf(args, "--render-sounds");
        if (sounds >= 0 && sounds + 1 < args.Length)
        {
            Feedback.WriteAll(args[sounds + 1]);
            GetTree().Quit();
        }
        else if (smoke >= 0 && smoke + 1 < args.Length)
        {
            AddChild(new SmokeTest(args[smoke + 1]));
        }
        else if (!ShowWhatsNew() && Host is { Settings.CheckForUpdates: true, Updater.CanSelfUpdate: true })
        {
            CheckForAppUpdate(silent: true);
        }
    }

    /// <summary>Changes waiting for Steam to close are written as soon as DeckWrench sees Steam closed.</summary>
    private void ApplyPendingAtStart()
    {
        if (Host == null || Host.Pending.All.Count == 0 || Core.Steam.SteamShortcuts.SteamRunning(Host.Paths.Home))
        {
            return;
        }
        try
        {
            var n = Host.ApplyPendingNow();
            Success($"Wrote {n} waiting change{(n == 1 ? "" : "s")} to Steam");
        }
        catch (Exception ex) when (IsUserFacing(ex))
        {
            Error("Couldn't write the waiting changes: " + ex.Message);
        }
    }

    /// <summary>Builds the controller or desktop shell (two separate layouts) and the matching background.</summary>
    private void BuildShell(string? setting, bool windowed)
    {
        _shellNode?.QueueFree();
        _highlight?.QueueFree();
        _highlight = null;
        if (_toast.GetParent() != null)
        {
            RemoveChild(_toast);
        }
        UiLayout.Apply(GetWindow(), setting, windowed);
        AppLog.Info($"mode {(UiLayout.Desktop ? "desktop" : "controller")} ({setting ?? "auto"})");
        if (UiLayout.Desktop)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
            Theme = DesktopTheme.Build();
            BuildBackground(desktop: true);
            var shell = new DesktopShell();
            _shellNode = shell;
            Shell = shell;
            AddChild(shell);
        }
        else
        {
            Input.MouseMode = Input.MouseModeEnum.Hidden;  // never drawn; clicks and touch still work
            Theme = UiTheme.Build();
            BuildBackground(desktop: false);
            var shell = new ControllerShell();
            _shellNode = shell;
            Shell = shell;
            AddChild(shell);
            _highlight = new FocusHighlight();
            AddChild(_highlight);
        }
        AddChild(_toast);
        _toast.Restyle();
    }

    /// <summary>Background colour, accent glow, the theme's animated background and decoration (controller mode); plain in desktop mode.</summary>
    private void BuildBackground(bool desktop)
    {
        foreach (var c in _bgLayer.GetChildren())
        {
            c.QueueFree();
        }
        var bg = new ColorRect { Color = UiTheme.Bg, MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _bgLayer.AddChild(bg);
        _animBg = null;
        _crt = null;
        if (desktop)
        {
            return;
        }
        if (UiTheme.Spec.Glow > 0)
        {
            // Soft accent glow from the top-left corner so the background isn't flat.
            var glow = new TextureRect
            {
                Texture = new GradientTexture2D
                {
                    Gradient = new Gradient
                    {
                        Offsets = [0f, 1f],
                        Colors = [UiTheme.Accent with { A = UiTheme.Spec.Glow }, UiTheme.Accent with { A = 0f }],
                    },
                    Fill = GradientTexture2D.FillEnum.Radial,
                    FillFrom = new Vector2(0.1f, 0f),
                    FillTo = new Vector2(0.85f, 0.9f),
                    Width = 256,
                    Height = 256,
                },
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            glow.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _bgLayer.AddChild(glow);
        }
        if (Host?.Settings.AnimatedBackground != false)
        {
            _animBg = new AnimatedBackground(UiTheme.Spec);
            _bgLayer.AddChild(_animBg);
        }
        switch (UiTheme.Spec.Decor)
        {
            case Decor.DotGrid:
                _bgLayer.AddChild(new DotGrid());
                break;
            case Decor.PixelGrid:
                _bgLayer.AddChild(new PixelGrid());
                break;
        }
        if (UiTheme.Spec.Decor == Decor.Scanlines || Host?.Settings.Crt == true)
        {
            _crt = new Scanlines(Host?.Settings.Crt == true ? Host.Settings.CrtLevel : 0);
            _bgLayer.AddChild(_crt);
        }
        PauseBackground();
    }

    private void PauseBackground()
    {
        var paused = !_appFocused;
        if (_animBg != null && IsInstanceValid(_animBg))
        {
            _animBg.Paused = paused;
        }
        if (_crt != null && IsInstanceValid(_crt))
        {
            _crt.Paused = paused;
        }
    }

    /// <summary>Re-applies the background decoration (CRT setting changed).</summary>
    public void RefreshBackground() => BuildBackground(UiLayout.Desktop);

    /// <summary>Switches between the controller and desktop modes in place (Settings, View menu).</summary>
    public void ApplyLayout(string setting)
    {
        if (Host == null)
        {
            return;
        }
        Host.SaveSettings(Host.Settings with { Layout = setting });
        var was = UiLayout.Desktop;
        if (UiLayout.Resolve(setting) == was)
        {
            UiLayout.Apply(GetWindow(), setting, OS.GetCmdlineUserArgs().Contains("--windowed"));
            return;
        }
        // Deferred: the control that asked for the switch belongs to the shell being replaced.
        Callable.From(() =>
        {
            BuildShell(setting, OS.GetCmdlineUserArgs().Contains("--windowed"));
            Success(UiLayout.Desktop ? "Desktop mode" : "Controller mode");
        }).CallDeferred();
    }

    public void ApplyFeedback(int volume, bool haptics)
    {
        if (_feedback != null)
        {
            _feedback.Volume = volume;
            _feedback.Haptics = haptics;
        }
    }

    // ---- scanning ----

    /// <summary>
    /// Scans Steam, Heroic and Lutris again in the background, then refreshes the shell. A quiet
    /// rescan (after an action, at start) reuses remembered folder sizes; one the player asked
    /// for measures every folder again.
    /// </summary>
    public async void Rescan(bool quiet = false)
    {
        if (Host == null || Scanning)
        {
            return;
        }
        if (!quiet)
        {
            Host.Sizes.Forget();
        }
        Scanning = true;
        Shell?.StateChanged();
        var host = Host;
        try
        {
            await Task.Run(() => host.Scan());
            if (!quiet)
            {
                Success($"Found {host.State.Games.Count} game{(host.State.Games.Count == 1 ? "" : "s")}");
            }
        }
        catch (Exception e) when (IsUserFacing(e))
        {
            AppLog.Error("scan: " + e);
            Error("Couldn't finish scanning: " + e.Message);
        }
        finally
        {
            Scanning = false;
        }
        Shell?.StateChanged();
        RefreshRatings();
    }

    /// <summary>Fetches missing or old ProtonDB / Steam Deck ratings in the background; refreshes the screen when any changed.</summary>
    public async void RefreshRatings()
    {
        if (Host == null)
        {
            return;
        }
        var host = Host;
        try
        {
            var changed = await Task.Run(() => host.RefreshRatings());
            if (changed > 0 && !Scanning)
            {
                Shell?.StateChanged();
            }
        }
        catch (Exception e) when (IsUserFacing(e))
        {
            AppLog.Info("ratings: " + e.Message);  // offline is normal on a Deck; never bother the player
        }
    }

    /// <summary>
    /// Runs async work and comes back on the main thread with the result, or with a user-facing
    /// message for expected failures (I/O, network, bad data). Cancellation calls neither.
    /// </summary>
    public async void RunAsync<T>(Func<CancellationToken, Task<T>> work, Action<T> done, Action<string> failed, CancellationToken ct)
    {
        T result;
        try
        {
            result = await work(ct);
        }
        catch (OperationCanceledException)
        {
            failed("Cancelled");
            return;
        }
        catch (Exception e) when (IsUserFacing(e) || e is System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            failed(e.Message);
            return;
        }
        done(result);
    }

    // ---- pages (delegated to the shell) ----

    public void Push(Screen screen) => Shell.Push(screen);

    public void Pop() => Shell.Pop();

    public void PopTo(Screen screen) => Shell.PopTo(screen);

    public Screen? Top => Shell.Top;

    public void UpdateChrome() => Shell.UpdateChrome();

    public void Confirm(string message, string yes, Action onYes) => Push(new ConfirmPage(message, yes, onYes));

    /// <summary>Runs background work behind a busy page. Progress is 0..1 or negative for indeterminate.</summary>
    public async void Busy<T>(string title, Func<IProgress<double>, CancellationToken, Task<T>> work, Action<T> done, bool cancellable = true)
    {
        using var cts = new CancellationTokenSource();
        var page = new BusyPage(title, cancellable ? cts : null);
        Push(page);
        var progress = new Progress<double>(page.SetProgress);
        try
        {
            var result = await Task.Run(() => work(progress, cts.Token), cts.Token);
            PopTo(page);
            Pop();
            done(result);
        }
        catch (Exception e) when (IsUserFacing(e) || e is OperationCanceledException)
        {
            PopTo(page);
            Pop();
            if (e is not OperationCanceledException)
            {
                Error(e.Message);
            }
        }
    }

    // ---- feedback ----

    public void Toast(string text) => _toast.Show(text);

    public void Error(string text)
    {
        AppLog.Warn("shown error: " + text);
        Feedback.Play(Cue.Error);
        _toast.Show(text, UiTheme.Bad);
    }

    public void Success(string text)
    {
        Feedback.Play(Cue.Success);
        _toast.Show(text, UiTheme.Good);
    }

    /// <summary>Runs an action and shows expected failures (I/O, bad files, network) as a toast.</summary>
    public bool Guard(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception e) when (IsUserFacing(e))
        {
            Error(e.Message);
            return false;
        }
    }

    public static bool IsUserFacing(Exception e) =>
        e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or InvalidDataException or HttpRequestException or KeyNotFoundException or TaskCanceledException;

    // ---- updates ----

    private bool ShowWhatsNew()
    {
        if (Host == null)
        {
            return false;
        }
        var current = Host.Updater.CurrentVersion;
        var display = AppUpdater.DisplayVersion(current);
        var seen = AppUpdater.ParseVersion(Host.Settings.LastSeenVersion);
        if (Host.Settings.LastSeenVersion != display)
        {
            Host.SaveSettings(Host.Settings with { LastSeenVersion = display });
        }
        if (seen == null)
        {
            return false;
        }
        var news = Changelog.Between(Changelog.Bundled, seen, current);
        if (news.Count == 0)
        {
            return false;
        }
        Push(new ChangelogPage($"What's new in DeckWrench {display}", news, whatsNew: true));
        return true;
    }

    public void ShowChangelog() => Push(new ChangelogPage("Changelog", Changelog.Bundled));

    /// <summary>Asks GitHub for a newer DeckWrench. Silent: no message when up to date or offline.</summary>
    public async void CheckForAppUpdate(bool silent)
    {
        if (Host == null)
        {
            return;
        }
        var updater = Host.Updater;
        AppRelease? release;
        try
        {
            release = await Task.Run(() => updater.CheckAsync());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (!silent)
            {
                Error("Couldn't reach GitHub to check for updates.");
            }
            return;
        }
        var current = AppUpdater.DisplayVersion(updater.CurrentVersion);
        if (release == null)
        {
            if (!silent)
            {
                Success($"DeckWrench {current} is the latest version");
            }
            return;
        }
        var next = AppUpdater.DisplayVersion(release.Version);
        if (!updater.CanSelfUpdate)
        {
            Toast($"DeckWrench {next} is available. Re-run the installer to update this copy.");
            return;
        }
        var whatsNew = Changelog.ItemsFromReleaseNotes(release.Notes);
        var notes = whatsNew.Count > 0 ? "\n\nWhat's new:\n" + string.Join("\n", whatsNew.Take(8).Select(i => "• " + i)) : "";
        Push(new ChoicePage($"DeckWrench {next} is available (you have {current}).{notes}\n\nThe update takes a few seconds. Your settings and backups are not touched, and the current version is kept so you can roll back in Settings.",
            ("Update and restart", () => InstallAppUpdate(release)),
            ("Later", () => { })));
    }

    private void InstallAppUpdate(AppRelease release)
    {
        if (Host == null)
        {
            return;
        }
        var updater = Host.Updater;
        Busy($"Updating to {AppUpdater.DisplayVersion(release.Version)}…", (p, ct) => updater.InstallAsync(release, p, ct), Restart);
    }

    public void RollbackApp()
    {
        if (Host != null)
        {
            Guard(() => Restart(Host.Updater.Rollback()));
        }
    }

    /// <summary>Restarts the app with the same arguments (plus <paramref name="extra"/>), e.g. to apply a theme.</summary>
    public void RestartApp(params string[] extra)
    {
        AppLog.Info($"restart ({string.Join(' ', extra)})");
        var user = OS.GetCmdlineUserArgs().Where(a => !a.StartsWith("--open=", StringComparison.Ordinal)).Concat(extra).ToList();
        var args = OS.GetCmdlineArgs().ToList();
        if (user.Count > 0)
        {
            args.Add("--");
            args.AddRange(user);
        }
        OS.SetRestartOnExit(true, args.ToArray());
        GetTree().Quit();
    }

    private void Restart(string exe)
    {
        try
        {
            AppUpdater.Relaunch(exe, OS.GetCmdlineUserArgs());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            Error("Updated. Please start DeckWrench again to finish.");
            return;
        }
        GetTree().Quit();
    }

    /// <summary>Quitting: release Godot objects that C# statics keep alive, while .NET is still running.</summary>
    public override void _ExitTree()
    {
        Theme = null;
        DeckWrench.App.Ui.Poly.Ps1.ReleaseShared();
        CoverArt.ReleaseShared();
        UiTheme.ReleaseFonts();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut)
        {
            _appFocused = false;
            Pad.ReleaseAll();
            PauseBackground();
        }
        else if (what == NotificationApplicationFocusIn)
        {
            _appFocused = true;
            if (!UiLayout.Desktop)
            {
                Input.MouseMode = Input.MouseModeEnum.Hidden;
            }
            PauseBackground();
        }
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventAction or InputEventKey && e.IsPressed() && !e.IsEcho())
        {
            if (e.IsAction("ui_up") || e.IsAction("ui_down") || e.IsAction("ui_left") || e.IsAction("ui_right"))
            {
                _navAtMs = Time.GetTicksMsec();
            }
            else if (e.IsAction(Pad.Accept) && GetViewport().GuiGetFocusOwner() is BaseButton and not Stepper)
            {
                Feedback.Play(Cue.Confirm);
            }
        }
        if (!UiLayout.Desktop && TouchScroll.Handle(GetViewport(), e))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        var joy = e is InputEventJoypadButton or InputEventJoypadMotion;
        if (joy && !_appFocused)
        {
            // Godot receives joypad input even when unfocused; never react to presses meant for a game.
            GetViewport().SetInputAsHandled();
            return;
        }
        if (Pad.Translate(e))
        {
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (_appFocused)
        {
            if (!UiLayout.Desktop)
            {
                TouchScroll.Process(delta);
            }
            Pad.Process(delta);
        }
    }

    public static string Describe(Control? c) =>
        c == null ? "none" : $"{c.GetType().Name} '{(c is Button b ? b.Text : c.TooltipText).Split('\n')[0]}'";
}
