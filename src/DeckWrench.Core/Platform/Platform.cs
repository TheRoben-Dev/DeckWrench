using System.Diagnostics;

namespace DeckWrench.Core;

/// <summary>
/// The operating system DeckWrench runs on, and the few things that differ by it: binary names, how
/// paths compare, whether a process is alive. Linux (SteamOS, Bazzite...) and Windows are supported.
/// Platform-specific code lives behind this and in the Windows/ folders; callers stay the same.
/// </summary>
public static class Platform
{
    public static bool IsWindows { get; } = OperatingSystem.IsWindows();

    /// <summary>"Windows" or "Linux", for messages.</summary>
    public static string Name => IsWindows ? "Windows" : "Linux";

    /// <summary>How file paths compare here: without case on Windows, exactly on Linux.</summary>
    public static StringComparer PathComparer => IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison PathComparison => IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The Godot app's executable in a release folder.</summary>
    public static string AppBinary => IsWindows ? "DeckWrench.exe" : "DeckWrench.x86_64";

    /// <summary>The command line helper (`deckwrench`) next to it; deckwrench-cli.exe on Windows, where deckwrench.exe would clash with DeckWrench.exe.</summary>
    public static string CliBinary => IsWindows ? "deckwrench-cli.exe" : "deckwrench";

    /// <summary>True while a process with this id is running.</summary>
    public static bool ProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }
        if (!IsWindows)
        {
            return Directory.Exists($"/proc/{pid}");
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
