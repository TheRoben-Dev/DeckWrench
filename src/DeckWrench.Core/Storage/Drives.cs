namespace DeckWrench.Core.Storage;

/// <summary>A mounted drive that holds at least one Steam library.</summary>
public sealed record Drive
{
    /// <summary>Mount point ("/", "/home", "/run/media/deck/SD", "D:\").</summary>
    public required string Mount { get; init; }

    /// <summary>"Internal", "SD card", "External drive" or the drive letter.</summary>
    public required string Label { get; init; }

    /// <summary>The block device (/dev/nvme0n1p8, /dev/mmcblk0p1) when known.</summary>
    public string? Device { get; init; }

    public long Total { get; init; }
    public long Free { get; init; }
    public long Used => Math.Max(0, Total - Free);

    public bool IsSdCard => Label == "SD card";
}

/// <summary>Which drive a path is on, from the mount table (Linux) or the drive letter (Windows).</summary>
public static class Drives
{
    /// <summary>The drive holding <paramref name="path"/>: the mount point that is the longest prefix of its real path.</summary>
    public static Drive? For(string path, IReadOnlyList<(string Device, string Mount)>? mounts = null)
    {
        string real;
        try
        {
            real = PathUtil.RealPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        if (Platform.IsWindows)
        {
            var root = Path.GetPathRoot(real);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }
            return Measure(root, root.TrimEnd('\\'), null);
        }
        mounts ??= Mounts();
        var best = mounts
            .Where(m => real == m.Mount || real.StartsWith(m.Mount.TrimEnd('/') + "/", StringComparison.Ordinal))
            .OrderByDescending(m => m.Mount.Length)
            .FirstOrDefault();
        if (best.Mount == null)
        {
            return null;
        }
        return Measure(best.Mount, LabelFor(best.Device, best.Mount), best.Device);
    }

    public static string LabelFor(string? device, string mount)
    {
        if (device != null && device.Contains("mmcblk", StringComparison.Ordinal))
        {
            return "SD card";
        }
        if (mount.StartsWith("/run/media/", StringComparison.Ordinal) || mount.StartsWith("/media/", StringComparison.Ordinal) || mount.StartsWith("/mnt/", StringComparison.Ordinal))
        {
            return "External drive";
        }
        return "Internal";
    }

    private static Drive? Measure(string mount, string label, string? device)
    {
        try
        {
            var d = new DriveInfo(mount);
            return new Drive { Mount = mount, Label = label, Device = device, Total = d.TotalSize, Free = d.AvailableFreeSpace };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new Drive { Mount = mount, Label = label, Device = device };
        }
    }

    /// <summary>(device, mount point) pairs from /proc/self/mounts, octal escapes decoded.</summary>
    public static IReadOnlyList<(string Device, string Mount)> Mounts(string file = "/proc/self/mounts")
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                var parts = line.Split(' ');
                if (parts.Length >= 2)
                {
                    list.Add((Unescape(parts[0]), Unescape(parts[1])));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            list.Add(("", "/"));
        }
        return list;
    }

    private static string Unescape(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
}
