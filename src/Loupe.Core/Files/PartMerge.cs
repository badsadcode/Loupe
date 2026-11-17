using System.Text.RegularExpressions;

namespace Loupe.Core.Files;

public sealed record MergePart(string Path, string Name, int Number, int Files);

public sealed record MergePlan(
    string Target, string BaseName, List<MergePart> Parts, List<MovePair> Moves,
    List<string> Problems, string? Error)
{
    public bool CanRun => Error is null && Problems.Count == 0 && Moves.Count > 0;
}

/// <summary>
/// Joins the parts of one signature that were scanned on different rolls, e.g.
/// <c>…\123_1</c> (120 files) + <c>…\123_2</c> (95 files) → a NEW folder <c>…\123</c> with 215 files.
/// File names follow Striplight PRO's export: <c>73_524_0_0_123_1_00001.jpg</c> becomes
/// <c>73_524_0_0_123_00001.jpg</c> and part 2 continues the numbering (00121…). Names that do
/// not contain the part folder's name (e.g. <c>page_00001.jpg</c>) keep their prefix and are
/// only renumbered. Nothing is overwritten: the target folder must not exist yet (or be empty).
/// </summary>
public static class PartMerger
{
    private static readonly Regex PartName = new(@"^(?<base>.+?)_(?<n>\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Numbered = new(@"^(?<prefix>.*?)(?<num>\d+)(?<ext>\.[^.]+)$", RegexOptions.CultureInvariant);

    public static (string Base, int Number)? ParsePart(string folderName)
    {
        var m = PartName.Match(folderName);
        return m.Success ? (m.Groups["base"].Value, int.Parse(m.Groups["n"].Value)) : null;
    }

    public static MergePlan Plan(IReadOnlyList<string> folders)
    {
        MergePlan Fail(string e) => new("", "", [], [], [], e);
        if (folders.Count < 2) return Fail("Select at least two part folders (for example 123_1 and 123_2).");

        var parts = new List<(string Path, string Name, string Base, int N)>();
        foreach (string f in folders)
        {
            string path = Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(path);
            if (!Directory.Exists(path)) return Fail($"Folder not found: {path}");
            if (ParsePart(name) is not { } p) return Fail($"\"{name}\" does not end with _<part number> (for example 123_1).");
            parts.Add((path, name, p.Base, p.Number));
        }
        string? parent = Path.GetDirectoryName(parts[0].Path);
        if (parts.Any(p => !string.Equals(Path.GetDirectoryName(p.Path), parent, StringComparison.OrdinalIgnoreCase)))
            return Fail("All parts must be in the same folder.");
        string baseName = parts[0].Base;
        if (parts.Any(p => !string.Equals(p.Base, baseName, StringComparison.OrdinalIgnoreCase)))
            return Fail("The folders belong to different signatures: " + string.Join(", ", parts.Select(p => p.Name).Distinct()));
        if (parts.GroupBy(p => p.N).Any(g => g.Count() > 1)) return Fail("The same part number is selected twice.");
        parts.Sort((a, b) => a.N.CompareTo(b.N));

        string target = Path.Combine(parent!, baseName);
        var problems = new List<string>();
        if (File.Exists(target)) problems.Add($"A file named \"{baseName}\" is in the way.");
        else if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            problems.Add($"The folder \"{baseName}\" already exists and is not empty.");

        var moves = new List<MovePair>();
        var mparts = new List<MergePart>();
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var widths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };

        // First pass: collect files per part, work out the new prefix and the counter width.
        var perPart = new List<(MergePart Part, List<(string File, string Prefix, string Ext, int Width)?> Items, List<string> Files)>();
        foreach (var p in parts)
        {
            if (Directory.EnumerateDirectories(p.Path, "*", opts).Any())
                problems.Add($"\"{p.Name}\" contains subfolders; merge works on folders with files only.");
            var files = Directory.EnumerateFiles(p.Path, "*", opts).Where(x => !Path.GetFileName(x).StartsWith(".loupe-"))
                .OrderBy(x => Path.GetFileName(x), NaturalComparer.Instance).ToList();
            var items = new List<(string, string, string, int)?>();
            foreach (string file in files)
            {
                var m = Numbered.Match(Path.GetFileName(file));
                if (!m.Success) { items.Add(null); continue; }
                string prefix = m.Groups["prefix"].Value;
                string newPrefix = ReplacePartInPrefix(prefix, p.Name, baseName);
                int width = m.Groups["num"].Value.Length;
                string key = newPrefix + "|" + m.Groups["ext"].Value.ToLowerInvariant();
                widths[key] = Math.Max(widths.GetValueOrDefault(key), width);
                items.Add((file, newPrefix, m.Groups["ext"].Value, width));
            }
            var mp = new MergePart(p.Path, p.Name, p.N, files.Count);
            mparts.Add(mp);
            perPart.Add((mp, items, files));
        }

        // Second pass: number continuously across the parts, in part order.
        foreach (var (part, items, files) in perPart)
        {
            for (int i = 0; i < files.Count; i++)
            {
                string newName;
                if (items[i] is { } it)
                {
                    string key = it.Prefix + "|" + it.Ext.ToLowerInvariant();
                    int n = counters.GetValueOrDefault(key) + 1;
                    counters[key] = n;
                    int width = Math.Max(widths[key], n.ToString().Length);
                    newName = it.Prefix + n.ToString().PadLeft(width, '0') + it.Ext;
                }
                else newName = Path.GetFileName(files[i]);
                if (!taken.Add(newName)) problems.Add($"\"{newName}\" would exist twice (from {part.Name}).");
                moves.Add(new MovePair(files[i], Path.Combine(target, newName)));
            }
        }
        if (moves.Count == 0) problems.Add("The selected folders are empty.");
        return new MergePlan(target, baseName, mparts, moves, problems, null);
    }

    /// <summary><c>73_524_0_0_123_1_</c> with part <c>123_1</c> and base <c>123</c> → <c>73_524_0_0_123_</c>.</summary>
    internal static string ReplacePartInPrefix(string prefix, string partName, string baseName)
    {
        if (prefix.Length > partName.Length)
        {
            string core = prefix[..^1];
            char sep = prefix[^1];
            if (!char.IsLetterOrDigit(sep) && core.EndsWith(partName, StringComparison.OrdinalIgnoreCase))
            {
                int start = core.Length - partName.Length;
                if (start == 0 || !char.IsLetterOrDigit(core[start - 1]))
                    return core[..start] + baseName + sep;
            }
        }
        else if (string.Equals(prefix, partName, StringComparison.OrdinalIgnoreCase)) return baseName;
        return prefix;
    }

    /// <summary>
    /// Runs the merge: creates the target folder, moves and renumbers every file, then removes
    /// part folders that are now empty (when <paramref name="deleteEmptyParts"/>).
    /// </summary>
    public static UndoLog Run(MergePlan plan, bool deleteEmptyParts, Editing.BackupStore? backups = null)
    {
        if (!plan.CanRun) throw new InvalidOperationException(plan.Error ?? string.Join(" ", plan.Problems));
        bool created = !Directory.Exists(plan.Target);
        Directory.CreateDirectory(plan.Target);
        try { SafeMover.Run(plan.Moves, backups); }
        catch
        {
            if (created && !Directory.EnumerateFileSystemEntries(plan.Target).Any()) Directory.Delete(plan.Target);
            throw;
        }
        var deleted = new List<string>();
        if (deleteEmptyParts)
            foreach (var p in plan.Parts)
                if (Directory.Exists(p.Path) && !Directory.EnumerateFileSystemEntries(p.Path).Any())
                {
                    Directory.Delete(p.Path);
                    deleted.Add(p.Path);
                }
        return new UndoLog
        {
            Kind = "merge",
            Title = string.Join(" + ", plan.Parts.Select(p => p.Name)) + " → " + plan.BaseName,
            Moves = plan.Moves,
            CreatedFolders = created ? [plan.Target] : [],
            DeletedFolders = deleted,
        };
    }
}
