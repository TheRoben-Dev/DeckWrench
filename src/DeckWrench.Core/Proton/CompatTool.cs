using DeckWrench.Core.Detection;

namespace DeckWrench.Core.Proton;

public enum CompatToolKind
{
    /// <summary>Valve's Proton, installed by Steam as an app (steamapps/common/Proton ...).</summary>
    Valve,

    /// <summary>A custom tool in compatibilitytools.d (GE-Proton, Proton-tkg, Luxtorpeda...).</summary>
    Custom,

    /// <summary>Installed by the system (/usr/share/steam/compatibilitytools.d), not removable here.</summary>
    System,
}

/// <summary>A compatibility tool Steam can run Windows games with.</summary>
public sealed record CompatTool
{
    /// <summary>The name Steam stores in CompatToolMapping ("proton_9", "GE-Proton9-20").</summary>
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>The tool's folder.</summary>
    public required string Dir { get; init; }

    public CompatToolKind Kind { get; init; }

    /// <summary>Steam app id for Valve's Proton builds (removed through Steam, not here).</summary>
    public string? AppId { get; init; }

    /// <summary>Size of the folder in bytes (filled in by the scan; -1 = not measured).</summary>
    public long Size { get; init; } = -1;

    /// <summary>True for GE-Proton builds (the ones DeckWrench downloads and removes).</summary>
    public bool IsGe => Name.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase);

    /// <summary>Only tools in the user's compatibilitytools.d can be removed by DeckWrench.</summary>
    public bool Removable => Kind == CompatToolKind.Custom;

    public bool IsExperimental => Name.Equals("proton_experimental", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Finds the compatibility tools installed for a Steam root: Valve's Proton apps in every library,
/// custom tools in compatibilitytools.d (native and Flatpak Steam), and system-wide ones.
/// </summary>
public static class CompatToolScanner
{
    /// <summary>Where custom tools go for a Steam root (created by the installer when missing).</summary>
    public static string CustomDir(string steamRoot) => Path.Combine(steamRoot, "compatibilitytools.d");

    public static readonly string[] SystemDirs =
    [
        "/usr/share/steam/compatibilitytools.d",
        "/usr/local/share/steam/compatibilitytools.d",
    ];

    public static IReadOnlyList<CompatTool> Scan(string steamRoot, IReadOnlyList<string> libraries)
    {
        var tools = new List<CompatTool>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in libraries)
        {
            foreach (var t in ValveTools(lib))
            {
                if (seen.Add(t.Name))
                {
                    tools.Add(t);
                }
            }
        }
        foreach (var t in CustomTools(CustomDir(steamRoot), CompatToolKind.Custom))
        {
            if (seen.Add(t.Name))
            {
                tools.Add(t);
            }
        }
        if (!Platform.IsWindows)
        {
            foreach (var dir in SystemDirs)
            {
                foreach (var t in CustomTools(dir, CompatToolKind.System))
                {
                    if (seen.Add(t.Name))
                    {
                        tools.Add(t);
                    }
                }
            }
        }
        return tools.OrderBy(t => t, ToolOrder.Instance).ToList();
    }

    /// <summary>Valve's Proton builds in one library: steamapps/common/Proton*, matched to their manifests for the app id.</summary>
    public static IEnumerable<CompatTool> ValveTools(string library)
    {
        var common = Path.Combine(library, "steamapps", "common");
        if (!Directory.Exists(common))
        {
            yield break;
        }
        var appIds = ManifestDirs(library);
        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(common, "Proton*").ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var dir in dirs)
        {
            var folder = Path.GetFileName(dir);
            if (!File.Exists(Path.Combine(dir, "proton")) || ProtonNames.InternalName(folder) is not { } name)
            {
                continue;
            }
            yield return new CompatTool
            {
                Name = name,
                DisplayName = folder,
                Dir = dir,
                Kind = CompatToolKind.Valve,
                AppId = appIds.GetValueOrDefault(folder),
            };
        }
    }

    /// <summary>installdir → app id for every manifest in a library.</summary>
    private static Dictionary<string, string> ManifestDirs(string library)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path.Combine(library, "steamapps"), "appmanifest_*.acf"))
            {
                try
                {
                    var state = KeyValues.ParseFile(f).Map("AppState");
                    if (state?.Str("installdir") is { } dir && state.Str("appid") is { } id)
                    {
                        map[dir] = id;
                    }
                }
                catch (Exception e) when (e is IOException or InvalidDataException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return map;
    }

    /// <summary>Tools described by compatibilitytool.vdf files one level below <paramref name="dir"/>.</summary>
    public static IEnumerable<CompatTool> CustomTools(string dir, CompatToolKind kind)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }
        List<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var sub in subdirs)
        {
            var vdf = Path.Combine(sub, "compatibilitytool.vdf");
            if (!File.Exists(vdf))
            {
                continue;
            }
            KeyValues? tools;
            try
            {
                tools = KeyValues.ParseFile(vdf).Path("compatibilitytools", "compat_tools");
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var (name, entry) in tools?.Maps() ?? [])
            {
                yield return new CompatTool
                {
                    Name = name,
                    DisplayName = entry.Str("display_name") is { Length: > 0 } d ? d : name,
                    Dir = sub,
                    Kind = kind,
                };
            }
        }
    }

    /// <summary>Experimental first, then newest Proton first (Valve and GE mixed by version), then the rest by name.</summary>
    private sealed class ToolOrder : IComparer<CompatTool>
    {
        public static readonly ToolOrder Instance = new();

        public int Compare(CompatTool? a, CompatTool? b)
        {
            if (a == null || b == null)
            {
                return 0;
            }
            var ka = ProtonNames.SortKey(a);
            var kb = ProtonNames.SortKey(b);
            var c = kb.CompareTo(ka);
            return c != 0 ? c : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
