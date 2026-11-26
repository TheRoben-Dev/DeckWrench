using System.Formats.Tar;

namespace DeckWrench.Core.Storage;

/// <summary>
/// Tar extraction for Wine prefixes and Proton builds. .NET's TarFile refuses any symlink that
/// points outside the destination, but every prefix has dosdevices/z: → / (and often links into
/// the Proton install), so those are recreated as they were. What can't happen: an entry landing
/// outside the destination, either by its own path or by writing through a link made earlier.
/// </summary>
public static class SafeTar
{
    public static void Extract(Stream tar, string destination, CancellationToken ct = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var reader = new TarReader(tar);
        var dirModes = new List<(string Path, UnixFileMode Mode)>();
        while (reader.GetNextEntry() is { } e)
        {
            ct.ThrowIfCancellationRequested();
            var name = e.Name.Replace('\\', '/').TrimStart('/');
            while (name.StartsWith("./", StringComparison.Ordinal))
            {
                name = name[2..];
            }
            if (name.Length == 0 || name == ".")
            {
                continue;
            }
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, name)));
            if (!path.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The archive has an entry outside its folder: {e.Name}");
            }
            var parent = Path.GetDirectoryName(path)!;
            NoLinksBetween(root, parent, e.Name);
            switch (e.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(path);
                    dirModes.Add((path, e.Mode));
                    break;
                case TarEntryType.SymbolicLink:
                    Directory.CreateDirectory(parent);
                    File.CreateSymbolicLink(path, e.LinkName);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(parent);
                    e.ExtractToFile(path, overwrite: false);
                    break;
                case TarEntryType.HardLink:
                    var target = Path.GetFullPath(Path.Combine(root, e.LinkName.Replace('\\', '/').TrimStart('/')));
                    if (!target.StartsWith(root, StringComparison.Ordinal) || !File.Exists(target))
                    {
                        throw new InvalidDataException($"The archive has a hard link to something outside it: {e.Name}");
                    }
                    Directory.CreateDirectory(parent);
                    File.Copy(target, path);
                    break;
                default:
                    break;  // devices, FIFOs and global headers have no place in a prefix or a Proton build
            }
        }
        if (!OperatingSystem.IsWindows())
        {
            // Directory modes last, so a read-only directory doesn't stop its own contents.
            foreach (var (dir, mode) in Enumerable.Reverse(dirModes))
            {
                File.SetUnixFileMode(dir, mode | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    /// <summary>Throws when a folder between <paramref name="root"/> and <paramref name="dir"/> is a link (writing there would leave the destination).</summary>
    private static void NoLinksBetween(string root, string dir, string entry)
    {
        var current = dir;
        while (current.Length >= root.Length - 1 && current.StartsWith(Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal))
        {
            var info = new DirectoryInfo(current);
            if (info.Exists && info.LinkTarget != null)
            {
                throw new InvalidDataException($"The archive writes through a link: {entry}");
            }
            if (current.Length <= Path.TrimEndingDirectorySeparator(root).Length)
            {
                break;
            }
            current = Path.GetDirectoryName(current)!;
        }
    }
}
