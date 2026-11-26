namespace DeckWrench.Host.Toolbox;

/// <summary>Short wording for when a game was last played and for how long, as Steam counts it.</summary>
public static class PlayStats
{
    /// <summary>"today", "yesterday", "12 days ago", "3 months ago", "2 years ago"; "never" when unknown.</summary>
    public static string Ago(DateTime? lastUtc, DateTime nowUtc)
    {
        if (lastUtc is not { } last)
        {
            return "never";
        }
        var days = (int)Math.Floor((nowUtc - last).TotalDays);
        return days switch
        {
            < 1 => "today",
            1 => "yesterday",
            < 60 => $"{days} days ago",
            < 365 => $"{days / 30} months ago",
            < 730 => "a year ago",
            _ => $"{days / 365} years ago",
        };
    }

    /// <summary>"45 min", "3.5 h", "120 h"; empty for no time.</summary>
    public static string Hours(int minutes) => minutes switch
    {
        <= 0 => "",
        < 60 => $"{minutes} min",
        < 600 => $"{minutes / 60.0:0.#} h".Replace(',', '.'),
        _ => $"{minutes / 60} h",
    };

    /// <summary>"Last played 12 days ago · 3.5 h in total", or "Never played" for Steam games with no record.</summary>
    public static string Describe(GameEntry g, DateTime nowUtc)
    {
        if (g.LastPlayed == null && g.PlaytimeMinutes == 0)
        {
            return "Never played";
        }
        var parts = new List<string>();
        if (g.LastPlayed != null)
        {
            parts.Add("Last played " + Ago(g.LastPlayed, nowUtc));
        }
        if (g.PlaytimeMinutes > 0)
        {
            parts.Add(Hours(g.PlaytimeMinutes) + " in total");
        }
        return string.Join(" · ", parts);
    }
}
