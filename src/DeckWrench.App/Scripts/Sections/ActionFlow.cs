using DeckWrench.Host;
using DeckWrench.Host.Toolbox;

namespace DeckWrench.App.Sections;

/// <summary>
/// The dialogs a UI mode provides. The controller mode shows full-screen pages and cards; the
/// desktop mode shows windows. <see cref="ActionFlow"/> drives them the same way in both.
/// </summary>
public interface IActionUi
{
    void PickOne(ActionInput input, Action<string> picked);

    void PickMany(ActionInput input, Action<string> picked);

    void EnterText(ActionInput input, Action<string> entered);

    /// <summary>Shows what a destructive action removes; calls <paramref name="yes"/> only when confirmed.</summary>
    void Confirm(ConfirmPlan plan, Action yes);

    /// <summary>Shows progress (and live output) while work runs; returns a handle to report into.</summary>
    IBusy Busy(string title, bool showOutput, CancellationTokenSource cts);

    void OpenLog(string path);

    void Notice(string text, Tone tone);
}

public interface IBusy
{
    void Progress(double p);

    void Line(string text);

    /// <summary>Work finished: closes the busy view (output pages stay open with a Close button).</summary>
    void Done(bool ok, string message);
}

/// <summary>Runs a <see cref="ToolAction"/>: input, confirmation (with sizes and contents), progress, result, rescan.</summary>
public static class ActionFlow
{
    public static void Start(ToolAction a, IActionUi ui)
    {
        AppLog.Info($"action {a.Id} '{a.Label}'");
        if (a.Unavailable != null)
        {
            ui.Notice(a.Unavailable, Tone.Warn);
            return;
        }
        if (a.Id == ActionId.ViewLog && a.Target != null)
        {
            ui.OpenLog(a.Target);
            return;
        }
        if (a.Input is not { Kind: not InputKind.None } input)
        {
            AfterInput(a, null, ui);
            return;
        }
        if (input.LoadChoices != null)
        {
            var cts = new CancellationTokenSource();
            var busy = ui.Busy("Loading…", false, cts);
            Main.Instance.RunAsync(ct => input.LoadChoices(ct), choices =>
            {
                busy.Done(true, "");
                Ask(a, input with { Choices = choices, LoadChoices = null }, ui);
            }, e =>
            {
                busy.Done(false, e);
                ui.Notice(e, Tone.Bad);
            }, cts.Token);
            return;
        }
        Ask(a, input, ui);
    }

    private static void Ask(ToolAction a, ActionInput input, IActionUi ui)
    {
        switch (input.Kind)
        {
            case InputKind.Choice:
                if (input.Choices.Count == 0)
                {
                    ui.Notice("Nothing to choose from.", Tone.Warn);
                    return;
                }
                ui.PickOne(input, v => AfterInput(a, v, ui));
                break;
            case InputKind.MultiChoice:
                ui.PickMany(input, v => AfterInput(a, v, ui));
                break;
            case InputKind.Text:
                ui.EnterText(input, v => AfterInput(a, v, ui));
                break;
        }
    }

    private static void AfterInput(ToolAction a, string? value, IActionUi ui)
    {
        if (a.Plan == null)
        {
            Execute(a, value, ui);
            return;
        }
        // Measuring what goes can take a moment on a big prefix.
        var cts = new CancellationTokenSource();
        var busy = ui.Busy("Checking what this affects…", false, cts);
        Main.Instance.RunAsync(ct => Task.Run(() => a.Plan(value, ct), ct), plan =>
        {
            busy.Done(true, "");
            ui.Confirm(plan, () => Execute(a, value, ui));
        }, e =>
        {
            busy.Done(false, e);
            ui.Notice(e, Tone.Bad);
        }, cts.Token);
    }

    private static void Execute(ToolAction a, string? value, IActionUi ui)
    {
        if (a.Run == null)
        {
            return;
        }
        var cts = new CancellationTokenSource();
        var busy = ui.Busy(a.Label + "…", a.ShowsOutput, cts);
        var progress = new Progress<double>(busy.Progress);  // created on the main thread: reports marshal back
        var lines = new Progress<string>(busy.Line);
        var ctx = new ActionContext(value, progress, l => ((IProgress<string>)lines).Report(l), cts.Token);
        Main.Instance.RunAsync(ct => Task.Run(() => a.Run(ctx), ct), result =>
        {
            AppLog.Info($"action {a.Id} done: {result.Message}");
            busy.Done(true, result.Message);
            ui.Notice(result.Message, Tone.Good);
            if (result.Rescan)
            {
                Main.Instance.Rescan(quiet: true);
            }
        }, e =>
        {
            AppLog.Warn($"action {a.Id} failed: {e}");
            busy.Done(false, e);
            ui.Notice(e, Tone.Bad);
            Main.Instance.Rescan(quiet: true);
        }, cts.Token);
    }
}
