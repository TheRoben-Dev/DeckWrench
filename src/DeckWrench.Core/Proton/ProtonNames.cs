using System.Text.RegularExpressions;

namespace DeckWrench.Core.Proton;

/// <summary>
/// The names Steam uses for Valve's Proton builds in CompatToolMapping, and version ordering.
/// Steam's folder "Proton 9.0 (Beta)" is "proton_9"; before 7.0 the minor version is part of the
/// name ("Proton 6.3" is "proton_63", "Proton 5.0" is "proton_5").
/// </summary>
public static partial class ProtonNames
{
    [GeneratedRegex(@"^Proton (\d+)\.(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ValveFolder();

    [GeneratedRegex(@"^GE-Proton(\d+)-(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GeName();

    [GeneratedRegex(@"^proton_(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ValveName();

    /// <summary>The CompatToolMapping name for a Valve Proton folder, or null when it isn't one.</summary>
    public static string? InternalName(string folder)
    {
        if (folder.Equals("Proton - Experimental", StringComparison.OrdinalIgnoreCase))
        {
            return "proton_experimental";
        }
        if (folder.Equals("Proton Hotfix", StringComparison.OrdinalIgnoreCase))
        {
            return "proton_hotfix";
        }
        if (folder.StartsWith("Proton EasyAntiCheat", StringComparison.OrdinalIgnoreCase) ||
            folder.StartsWith("Proton BattlEye", StringComparison.OrdinalIgnoreCase))
        {
            return null;  // runtimes, not tools
        }
        var m = ValveFolder().Match(folder);
        if (!m.Success)
        {
            return null;
        }
        var major = int.Parse(m.Groups[1].Value);
        var minor = m.Groups[2].Value;
        return major >= 7 || minor == "0" ? $"proton_{major}" : $"proton_{major}{minor}";
    }

    /// <summary>A readable name for a tool id that may not be installed ("proton_9" → "Proton 9.0").</summary>
    public static string Friendly(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "proton_experimental":
                return "Proton Experimental";
            case "proton_hotfix":
                return "Proton Hotfix";
        }
        var m = ValveName().Match(name);
        if (!m.Success)
        {
            return name;
        }
        var digits = m.Groups[1].Value;
        // proton_9 and proton_10 are 9.0 and 10.0; before 7.0 Valve wrote the minor version in (proton_63, proton_513).
        if (digits.Length == 1 || digits[0] == '1' && digits.Length == 2)
        {
            return $"Proton {int.Parse(digits)}.0";
        }
        return $"Proton {digits[0]}.{digits[1..]}";
    }

    /// <summary>(major, minor) for a GE-Proton name ("GE-Proton9-20" → 9, 20), or null.</summary>
    public static (int Major, int Minor)? GeVersion(string name)
    {
        var m = GeName().Match(name);
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    /// <summary>Higher sorts first: Experimental, then by Proton version (GE and Valve mixed), then everything else.</summary>
    public static double SortKey(CompatTool tool)
    {
        if (tool.IsExperimental)
        {
            return 1e6;
        }
        if (GeVersion(tool.Name) is { } ge)
        {
            return ge.Major * 1000 + ge.Minor + 0.5;  // GE 9-20 sits just above Valve's 9.0
        }
        if (tool.Kind == CompatToolKind.Valve && ValveFolder().Match(tool.DisplayName) is { Success: true } m)
        {
            return int.Parse(m.Groups[1].Value) * 1000 + int.Parse(m.Groups[2].Value) / 100.0;
        }
        return tool.Name.Equals("proton_hotfix", StringComparison.OrdinalIgnoreCase) ? 0.5 : 0;
    }
}
