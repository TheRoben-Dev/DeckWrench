namespace DeckWrench.Core.Storage;

/// <summary>Folder sizes and byte formatting. Symbolic links are never followed (a prefix's dosdevices points at /).</summary>
public static class DiskSize
{
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Total size of the files under <paramref name="path"/> (or of the file itself); 0 when missing.</summary>
    public static long Of(string path, CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }
            if (!Directory.Exists(path) || IsLink(path))
            {
                return 0;
            }
            long total = 0;
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", Walk))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    total += f.Length;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
            return total;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>The entries directly inside a folder with their sizes, biggest first (what a confirmation lists).</summary>
    public static IReadOnlyList<(string Name, long Size, bool IsDir)> Contents(string dir, CancellationToken ct = default)
    {
        var list = new List<(string, long, bool)>();
        if (!Directory.Exists(dir))
        {
            return list;
        }
        try
        {
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                var isDir = e is DirectoryInfo && !e.Attributes.HasFlag(FileAttributes.ReparsePoint);
                list.Add((e.Name, isDir ? Of(e.FullName, ct) : e is FileInfo fi && !fi.Attributes.HasFlag(FileAttributes.ReparsePoint) ? fi.Length : 0, isDir));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return list.OrderByDescending(x => x.Item2).ToList();
    }

    public static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>"1.4 GB", "820 MB", "12 KB", "0 B" (1024-based, as file managers on SteamOS show).</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "…";
        }
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return u == 0 ? $"{bytes} B" : v >= 100 ? v.ToString("0", inv) + " " + units[u] : v.ToString("0.#", inv) + " " + units[u];
    }
}
