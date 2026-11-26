namespace DeckWrench.Host.Toolbox;

/// <summary>
/// Every action DeckWrench offers. Both UI modes render the same <see cref="ToolAction"/> lists
/// from <see cref="Actions"/>, which is what keeps the controller and desktop modes at feature
/// parity; the smoke test checks each mode shows the same ids for the same thing.
/// </summary>
public enum ActionId
{
    Rescan,
    SaveReport,
    ApplyPending,
    DiscardPending,

    // Games
    SetCompatTool,
    EditLaunchOptions,
    CopyLaunchOptions,
    LaunchPresets,
    ToggleProtonLog,
    MoveGame,
    OpenInSteam,
    PlayGame,
    VerifyGameFiles,
    UninstallGame,
    OpenInstallFolder,
    EditNotes,
    OpenProtonDb,

    // Proton
    InstallGeProton,
    SetDefaultTool,
    UseToolForGames,
    RemoveTool,
    OpenToolFolder,

    // Prefixes
    OpenPrefix,
    OpenSaves,
    BackupPrefix,
    RestorePrefix,
    ResetPrefix,
    DeletePrefix,
    RunTricks,
    RunTypedTricks,
    DeleteBackup,
    BackupAllPrefixes,
    PruneBackups,

    // Shader caches
    ClearShaderCache,
    ClearOrphanShaderCaches,
    ClearAllShaderCaches,

    // Logs
    ViewLog,
    DeleteLog,
    DeleteAllLogs,

    // Storage
    Cleanup,
    FreeSpaceOnDrive,
}

public enum InputKind
{
    None,

    /// <summary>Pick one of <see cref="ActionInput.Choices"/>.</summary>
    Choice,

    /// <summary>Pick any number of <see cref="ActionInput.Choices"/> (value: ids joined with '\n').</summary>
    MultiChoice,

    /// <summary>Type text (Steam's on-screen keyboard in Game Mode).</summary>
    Text,
}

public sealed record ActionChoice(string Id, string Label, string Detail = "", bool Selected = false, string Group = "");

/// <summary>What an action asks for before it runs.</summary>
public sealed record ActionInput
{
    public InputKind Kind { get; init; }
    public string Prompt { get; init; } = "";

    public IReadOnlyList<ActionChoice> Choices { get; init; } = [];

    /// <summary>Choices fetched when the input opens (GE-Proton releases from GitHub).</summary>
    public Func<CancellationToken, Task<IReadOnlyList<ActionChoice>>>? LoadChoices { get; init; }

    public string Initial { get; init; } = "";

    /// <summary>Checks what was entered: an error keeps the input open (with the text kept), null goes on.</summary>
    public Func<string?, string?>? Validate { get; init; }
}

/// <summary>
/// What a destructive action is about to remove or replace: shown in full (size and contents)
/// and confirmed before anything happens.
/// </summary>
public sealed record ConfirmPlan
{
    public required string Title { get; init; }

    /// <summary>The confirm button ("Delete", "Reset prefix", "Clear 3 caches").</summary>
    public required string Verb { get; init; }

    /// <summary>What goes: folders and files with their sizes, biggest first.</summary>
    public IReadOnlyList<(string Name, long Size)> Contents { get; init; } = [];

    public long TotalSize { get; init; }

    /// <summary>Where the backup made first goes, when the action makes one.</summary>
    public string? Backup { get; init; }

    public string? Caution { get; init; }

    /// <summary>Path(s) affected, for the record.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];
}

public sealed record ActionResult(string Message, bool Rescan = true);

/// <summary>What a running action gets: its input, progress (0..1, negative = unknown), a log line sink, cancellation.</summary>
public sealed record ActionContext(string? Input, IProgress<double> Progress, Action<string> Log, CancellationToken Ct);

public sealed record ToolAction
{
    public required ActionId Id { get; init; }
    public required string Label { get; init; }
    public string Help { get; init; } = "";

    /// <summary>Removes or replaces data: confirmed with a <see cref="ConfirmPlan"/> first.</summary>
    public bool Destructive { get; init; }

    /// <summary>Why it can't run right now (shown instead of running), or null.</summary>
    public string? Unavailable { get; init; }

    public ActionInput? Input { get; init; }

    /// <summary>Builds the confirmation for the chosen input (may measure folders: runs off the UI thread).</summary>
    public Func<string?, CancellationToken, ConfirmPlan>? Plan { get; init; }

    /// <summary>Does the work. Null for actions a UI handles itself (<see cref="ActionId.ViewLog"/>).</summary>
    public Func<ActionContext, Task<ActionResult>>? Run { get; init; }

    /// <summary>Shows live output while running (protontricks).</summary>
    public bool ShowsOutput { get; init; }

    /// <summary>Target for UI-handled actions (the log file for ViewLog).</summary>
    public string? Target { get; init; }
}
