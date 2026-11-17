namespace Loupe.Core.Files;

/// <summary>How file names turn into folders: <c>73_524_0_0_80_00001.jpg</c> → <c>73\524\0\0\80\</c>.</summary>
public sealed record SortRules
{
    public string Separator { get; init; } = "_";
    /// <summary>Parts at the end of the name that are not folders (the counter).</summary>
    public int CounterParts { get; init; } = 1;
    /// <summary>Keep the whole file name (true) or keep only the counter part (00001.jpg).</summary>
    public bool KeepName { get; init; } = true;
}

public sealed record SortPlan(string Root, List<MovePair> Moves, List<string> NewFolders, List<string> Problems, string? Error)
{
    public bool CanRun => Error is null && Problems.Count == 0 && Moves.Count > 0;
}

/// <summary>
/// The other way round of Striplight PRO's archive export: files named after the archive structure,
/// lying in one folder, are moved into that folder structure below <c>root</c>. Nothing is
/// overwritten; Undo moves them back and removes the folders it created.
/// </summary>
public static class FolderSorter
{
    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static SortPlan Plan(IReadOnlyList<string> files, string root, SortRules r)
    {
        if (files.Count == 0) return new SortPlan(root, [], [], [], "There are no images to sort.");
        if (string.IsNullOrEmpty(r.Separator)) return new SortPlan(root, [], [], [], "The separator is empty.");
        int counter = Math.Clamp(r.CounterParts, 0, 10);
        var moves = new List<MovePair>();
        var problems = new List<string>();
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(files.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        foreach (string f in files)
        {
            string name = Path.GetFileName(f), stem = Path.GetFileNameWithoutExtension(f), ext = Path.GetExtension(f);
            var parts = stem.Split(r.Separator);
            if (parts.Length <= counter) { problems.Add($"{name}: no folder names in it (nothing before the counter)."); continue; }
            var dirs = parts[..^counter];
            if (dirs.Any(d => d.Trim().Length == 0 || d.IndexOfAny(Invalid) >= 0 || d.EndsWith('.') || d.EndsWith(' ')))
            { problems.Add($"{name}: a folder name would be empty or invalid."); continue; }
            string dir = Path.Combine([root, .. dirs]);
            string newName = r.KeepName || counter == 0 ? name : string.Join(r.Separator, parts[^counter..]) + ext;
            string to = Path.Combine(dir, newName);
            if (!targets.Add(to)) { problems.Add($"{newName}: two files would end up in {dir} with this name."); continue; }
            if (File.Exists(to) && !sources.Contains(Path.GetFullPath(to))) { problems.Add($"{to} already exists."); continue; }
            for (string? d = dir; d is not null && d.Length > root.TrimEnd('\\', '/').Length; d = Path.GetDirectoryName(d))
                if (!Directory.Exists(d)) folders.Add(d);
            if (!string.Equals(Path.GetFullPath(f), Path.GetFullPath(to), StringComparison.Ordinal)) moves.Add(new MovePair(f, to));
        }
        return new SortPlan(root, moves, [.. folders], problems, null);
    }

    public static UndoLog Run(SortPlan plan, Editing.BackupStore? backups = null)
    {
        if (!plan.CanRun) throw new InvalidOperationException(plan.Error ?? string.Join(" ", plan.Problems));
        try { SafeMover.Run(plan.Moves, backups); }
        catch
        {
            foreach (var d in plan.NewFolders.OrderByDescending(x => x.Length))
                if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            throw;
        }
        return new UndoLog
        {
            Kind = "sort",
            Title = $"{plan.Moves.Count} files into folders",
            Moves = plan.Moves,
            CreatedFolders = plan.NewFolders,
        };
    }

    /// <summary>All images in the folders (and their subfolders), folder by folder in natural order.</summary>
    public static List<string> CollectImages(IEnumerable<string> folders, bool recursive)
    {
        var list = new List<string>();
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        void Walk(string dir)
        {
            list.AddRange(Directory.EnumerateFiles(dir, "*", opts).Where(FolderScanner.IsImage).OrderBy(Path.GetFileName, NaturalComparer.Instance));
            if (!recursive) return;
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", opts).Where(d => !FolderScanner.IsHidden(Path.GetFileName(d))).OrderBy(Path.GetFileName, NaturalComparer.Instance))
                Walk(sub);
        }
        foreach (var f in folders) if (Directory.Exists(f)) Walk(f);
        return list;
    }
}
