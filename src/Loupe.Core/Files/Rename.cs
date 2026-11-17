using System.Text;
using System.Text.RegularExpressions;

namespace Loupe.Core.Files;

/// <summary>
/// Batch rename rules, applied in this order: pattern → find/replace → letter case.
/// Pattern tokens: <c>{name}</c> the current name (without extension), <c>{folder}</c> the
/// folder the file is in, <c>{n}</c> the counter (<c>{n:3}</c> = 3 digits), <c>{path}</c> the folder
/// structure below the folder the rename started from, joined with "_" (<c>{path:5}</c> = the
/// last 5 folder levels). The extension is
/// kept and added automatically.
/// </summary>
public sealed record RenameRules
{
    public string Pattern { get; init; } = "{name}";
    public string Find { get; init; } = "";
    /// <summary>Folder the rename started from: <c>{path}</c> is the folder structure below it.</summary>
    public string Root { get; init; } = "";
    /// <summary>Joins the folder names in <c>{path}</c> (73\524\0\0\80 → 73_524_0_0_80).</summary>
    public string PathSeparator { get; init; } = "_";
    public string Replace { get; init; } = "";
    public bool Regex { get; init; }
    public bool MatchCase { get; init; }
    /// <summary>"keep" | "lower" | "upper" | "title".</summary>
    public string Case { get; init; } = "keep";
    /// <summary>"keep" | "lower" | "upper".</summary>
    public string ExtCase { get; init; } = "lower";
    public int Start { get; init; } = 1;
    public int Step { get; init; } = 1;
    public int Digits { get; init; } = 5;
    /// <summary>The counter starts again in every folder.</summary>
    public bool CounterPerFolder { get; init; } = true;
    /// <summary>Count from the last file to the first (a roll scanned backwards): the last file gets the start number.</summary>
    public bool Reverse { get; init; }
}

public sealed record RenameItem(string From, string To, string NewName, string Status, string? Message)
{
    public bool Changes => Status == "ok";
}

public sealed record RenamePlan(List<RenameItem> Items, int Changes, int Problems, string? Error)
{
    public bool CanRun => Error is null && Problems == 0 && Changes > 0;
}

public static class RenamePlanner
{
    private static readonly Regex Token = new(@"\{(name|folder|n|path)(?::(\d{1,2}))?\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Works out every new name (files in the order given, which is the order shown) and marks
    /// problems: invalid names, two files with the same new name, or a name that is already
    /// taken by a file that is not part of the rename.
    /// </summary>
    public static RenamePlan Plan(IReadOnlyList<string> files, RenameRules r)
    {
        Regex? find = null;
        if (!string.IsNullOrEmpty(r.Find) && r.Regex)
        {
            try { find = new Regex(r.Find, (r.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException e) { return new RenamePlan([], 0, 0, "Invalid regular expression: " + e.Message); }
        }

        // Counter values first (in reverse order when asked), then the names in the order shown.
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int global = r.Start;
        var numbers = new int[files.Count];
        for (int k = 0; k < files.Count; k++)
        {
            int i = r.Reverse ? files.Count - 1 - k : k;
            string d = Path.GetDirectoryName(files[i]) ?? "";
            if (r.CounterPerFolder)
            {
                numbers[i] = counters.TryGetValue(d, out int c) ? c : r.Start;
                counters[d] = numbers[i] + r.Step;
            }
            else { numbers[i] = global; global += r.Step; }
        }

        var raw = new List<(string From, string To, string NewName, string? Problem)>();
        for (int i = 0; i < files.Count; i++)
        {
            string from = files[i];
            string dir = Path.GetDirectoryName(from) ?? "";
            int n = numbers[i];

            string name = Path.GetFileNameWithoutExtension(from), ext = Path.GetExtension(from);
            string folder = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string s = Token.Replace(string.IsNullOrEmpty(r.Pattern) ? "{name}" : r.Pattern, m =>
                m.Groups[1].Value.ToLowerInvariant() switch
                {
                    "name" => name,
                    "folder" => folder,
                    "path" => PathToken(dir, r, m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0),
                    _ => Counter(n, m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : r.Digits),
                });
            if (!string.IsNullOrEmpty(r.Find))
            {
                try
                {
                    s = find is not null
                        ? find.Replace(s, r.Replace ?? "")
                        : s.Replace(r.Find, r.Replace ?? "", r.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                }
                catch (RegexMatchTimeoutException) { return new RenamePlan([], 0, 0, "The regular expression takes too long."); }
            }
            s = r.Case switch
            {
                "lower" => s.ToLowerInvariant(),
                "upper" => s.ToUpperInvariant(),
                "title" => Title(s),
                _ => s,
            };
            ext = r.ExtCase switch { "lower" => ext.ToLowerInvariant(), "upper" => ext.ToUpperInvariant(), _ => ext };
            string newName = s + ext;
            raw.Add((from, Path.Combine(dir, newName), newName, Validate(s, newName)));
        }

        // Duplicates within the plan, and names taken by files outside it.
        var sources = new HashSet<string>(files.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var count = raw.GroupBy(x => Path.GetFullPath(x.To), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var items = new List<RenameItem>(raw.Count);
        foreach (var (from, to, newName, problem) in raw)
        {
            string full = Path.GetFullPath(to);
            if (problem is not null) items.Add(new RenameItem(from, to, newName, "invalid", problem));
            else if (count[full] > 1) items.Add(new RenameItem(from, to, newName, "conflict", "Another file gets the same name."));
            else if (string.Equals(from, to, StringComparison.Ordinal)) items.Add(new RenameItem(from, to, newName, "same", null));
            else if (File.Exists(to) && !sources.Contains(full) && !string.Equals(Path.GetFullPath(from), full, StringComparison.OrdinalIgnoreCase))
                items.Add(new RenameItem(from, to, newName, "exists", "A file with this name already exists."));
            else items.Add(new RenameItem(from, to, newName, "ok", null));
        }
        int changes = items.Count(i => i.Status == "ok");
        int problems = items.Count(i => i.Status is "invalid" or "conflict" or "exists");
        return new RenamePlan(items, changes, problems, null);
    }

    /// <summary>Folder names joined: the last <paramref name="levels"/>, or all below <see cref="RenameRules.Root"/>.</summary>
    internal static string PathToken(string dir, RenameRules r, int levels)
    {
        var parts = dir.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && parts[0].EndsWith(':')) parts.RemoveAt(0);          // drive letter
        List<string> pick;
        if (levels > 0) pick = parts.Skip(Math.Max(0, parts.Count - levels)).ToList();
        else
        {
            var root = r.Root.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).ToList();
            if (root.Count > 0 && root[0].EndsWith(':')) root.RemoveAt(0);
            bool under = root.Count <= parts.Count && root.Select((x, i) => string.Equals(x, parts[i], StringComparison.OrdinalIgnoreCase)).All(b => b);
            pick = under && root.Count > 0 ? parts.Skip(root.Count).ToList() : [];
            if (pick.Count == 0 && parts.Count > 0) pick = [parts[^1]];             // files right in the root: their folder
        }
        return string.Join(r.PathSeparator ?? "_", pick);
    }

    private static string Counter(int n, int digits) =>
        n < 0 ? "-" + (-n).ToString().PadLeft(Math.Max(0, digits), '0') : n.ToString().PadLeft(Math.Max(0, digits), '0');

    private static string Title(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool start = true;
        foreach (char c in s)
        {
            sb.Append(start ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            start = !char.IsLetterOrDigit(c);
        }
        return sb.ToString();
    }

    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static string? Validate(string stem, string name)
    {
        if (string.IsNullOrWhiteSpace(stem)) return "The name is empty.";
        if (name.IndexOfAny(Invalid) >= 0 || name.Any(c => c < 32)) return "The name contains a character Windows does not allow (< > : \" / \\ | ? *).";
        if (name.EndsWith(' ') || name.EndsWith('.')) return "The name ends with a space or a dot.";
        if (Reserved.Contains(stem.Split('.')[0])) return "This name is reserved by Windows.";
        if (name.Length > 240) return "The name is too long.";
        return null;
    }

    public static List<MovePair> Moves(RenamePlan plan) =>
        plan.Items.Where(i => i.Status == "ok").Select(i => new MovePair(i.From, i.To)).ToList();
}
