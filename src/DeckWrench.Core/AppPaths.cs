namespace DeckWrench.Core;

/// <summary>
/// DeckWrench's own directories: XDG on Linux, %LOCALAPPDATA%\DeckWrench and %APPDATA%\DeckWrench on Windows.
/// Settings, prefix backups, waiting Steam changes and the app log live here; Steam's own folders are only
/// changed by actions the user confirms.
/// </summary>
public sealed class AppPaths
{
    public const string Name = "deckwrench";

    public string Data { get; }
    public string Config { get; }
    public string Cache { get; }
    public string Runtime { get; }
    public string Home { get; }

    /// <param name="env">Environment variables (tests pass their own).</param>
    /// <param name="windows">Windows layout; null = the OS DeckWrench runs on.</param>
    public AppPaths(Func<string, string?>? env = null, bool? windows = null)
    {
        env ??= System.Environment.GetEnvironmentVariable;
        if (windows ?? Platform.IsWindows)
        {
            Home = NonEmpty(env("USERPROFILE")) ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            var local = Path.Combine(NonEmpty(env("LOCALAPPDATA")) ?? Path.Combine(Home, "AppData", "Local"), "DeckWrench");
            Data = local;
            Config = Path.Combine(NonEmpty(env("APPDATA")) ?? Path.Combine(Home, "AppData", "Roaming"), "DeckWrench");
            Cache = Path.Combine(local, "cache");
            Runtime = Path.Combine(local, "run");
            return;
        }
        Home = env("HOME") ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        Data = Path.Combine(Xdg(env, "XDG_DATA_HOME", Home, ".local/share"), Name);
        Config = Path.Combine(Xdg(env, "XDG_CONFIG_HOME", Home, ".config"), Name);
        Cache = Path.Combine(Xdg(env, "XDG_CACHE_HOME", Home, ".cache"), Name);
        var runtime = env("XDG_RUNTIME_DIR");
        Runtime = string.IsNullOrEmpty(runtime) ? Path.Combine(Path.GetTempPath(), Name + "-" + System.Environment.UserName) : Path.Combine(runtime, Name);
    }

    public string SettingsFile => Path.Combine(Config, "settings.json");

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string Xdg(Func<string, string?> env, string name, string home, string fallback)
    {
        var v = env(name);
        return string.IsNullOrEmpty(v) ? Path.Combine(home, fallback) : v;
    }
}
