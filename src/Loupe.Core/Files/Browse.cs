namespace Loupe.Core.Files;

/// <summary>Windows-Explorer-like order: "2" before "10", case-insensitive (copied from Striplight PRO).</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var da = a.AsSpan(si, i - si).TrimStart('0');
                var db = b.AsSpan(sj, j - sj).TrimStart('0');
                if (da.Length != db.Length) return da.Length.CompareTo(db.Length);
                int cmp = da.SequenceCompareTo(db);
                if (cmp != 0) return cmp;
                continue;
            }
            char ca = char.ToUpperInvariant(a[i]), cb = char.ToUpperInvariant(b[j]);
            if (ca != cb) return ca.CompareTo(cb);
            i++; j++;
        }
        int rest = (a.Length - i).CompareTo(b.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(a, b);
    }
}

public sealed record FolderEntry(string Name, string Path, bool HasChildren);

public sealed record ImageEntry(string Name, string Path, long Size, DateTime Modified);

public sealed record FolderListing(string Path, List<FolderEntry> Folders, List<ImageEntry> Images, int OtherFiles);

public sealed record RootEntry(string Name, string Path, string Kind, string Label);

/// <summary>Folder contents for the tree and the thumbnail grid.</summary>
public static class FolderScanner
{
    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".jpe"];

    public static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders Loupe keeps for itself and never shows.</summary>
    public static bool IsHidden(string name) =>
        name.StartsWith('.') || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);

    public static FolderListing List(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) throw new DirectoryNotFoundException($"Folder not found: {path}");
        var folders = new List<FolderEntry>();
        var images = new List<ImageEntry>();
        int other = 0;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (var e in dir.EnumerateFileSystemInfos("*", opts))
        {
            if (e is DirectoryInfo d)
            {
                if (!IsHidden(d.Name)) folders.Add(new FolderEntry(d.Name, d.FullName, HasSubfolders(d)));
            }
            else if (e is FileInfo f)
            {
                if (IsImage(f.Name)) images.Add(new ImageEntry(f.Name, f.FullName, f.Length, f.LastWriteTimeUtc));
                else other++;
            }
        }
        folders.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
        images.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
        return new FolderListing(dir.FullName, folders, images, other);
    }

    public static List<FolderEntry> Subfolders(string path)
    {
        var dir = new DirectoryInfo(path);
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        return dir.EnumerateDirectories("*", opts).Where(d => !IsHidden(d.Name))
            .Select(d => new FolderEntry(d.Name, d.FullName, HasSubfolders(d)))
            .OrderBy(f => f.Name, NaturalComparer.Instance).ToList();
    }

    private static bool HasSubfolders(DirectoryInfo d)
    {
        try
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            return d.EnumerateDirectories("*", opts).Any(x => !IsHidden(x.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Ready drives (fixed, removable, network) for the tree.</summary>
    public static List<RootEntry> Drives()
    {
        var list = new List<RootEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown) continue;
                string kind = d.DriveType switch
                {
                    DriveType.Removable => "removable",
                    DriveType.Network => "network",
                    _ => "fixed",
                };
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : d.VolumeLabel;
                string name = OperatingSystem.IsWindows() ? d.Name.TrimEnd('\\') : d.Name;
                list.Add(new RootEntry(name, d.RootDirectory.FullName, kind, label));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return list;
    }
}
