using System.Text;
using System.Text.Json;

namespace Loupe.Core.Files;

/// <summary>One image QC marked as beyond repair: it has to be scanned again.</summary>
public sealed record RescanEntry
{
    public string Path { get; init; } = "";
    public string Reason { get; init; } = "";
    /// <summary>Microfilm (roll) number, typed by the controller to find the roll in storage.</summary>
    public string Microfilm { get; init; } = "";
    public DateTime Marked { get; init; } = DateTime.Now;
}

/// <summary>
/// The rescan list, kept in a JSON file so it survives restarts and folder changes. Entries
/// follow their files through renames and merges.
/// </summary>
public sealed class RescanList
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _file;
    private List<RescanEntry> _entries = [];

    public RescanList(string file)
    {
        _file = file;
        try
        {
            if (File.Exists(file)) _entries = JsonSerializer.Deserialize<List<RescanEntry>>(File.ReadAllText(file), Json) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }

    public IReadOnlyList<RescanEntry> Entries => _entries;

    public bool Contains(string path) => _entries.Any(e => Same(e.Path, path));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Folder(string p) => System.IO.Path.GetDirectoryName(p) ?? "";

    /// <summary>Marks (<paramref name="on"/> true) or unmarks the files. A new entry takes the microfilm
    /// number already given to another file of the same folder.</summary>
    public int Set(IEnumerable<string> paths, bool on, string reason = "")
    {
        int n = 0;
        foreach (string p in paths)
        {
            int i = _entries.FindIndex(e => Same(e.Path, p));
            if (on && i < 0)
            {
                string film = _entries.FirstOrDefault(e => Same(Folder(e.Path), Folder(p)) && e.Microfilm.Length > 0)?.Microfilm ?? "";
                _entries.Add(new RescanEntry { Path = p, Reason = reason, Microfilm = film });
                n++;
            }
            else if (!on && i >= 0) { _entries.RemoveAt(i); n++; }
        }
        if (n > 0) Save();
        return n;
    }

    /// <summary>Changes the reason and/or microfilm. A microfilm number is also given to the other
    /// files of the same folder that have none yet (one roll per folder, as a rule).</summary>
    public void Update(string path, string? reason, string? microfilm)
    {
        int i = _entries.FindIndex(e => Same(e.Path, path));
        if (i < 0) return;
        var e = _entries[i];
        if (reason is not null) e = e with { Reason = reason.Trim() };
        if (microfilm is not null)
        {
            string film = microfilm.Trim();
            e = e with { Microfilm = film };
            if (film.Length > 0)
                for (int k = 0; k < _entries.Count; k++)
                    if (k != i && _entries[k].Microfilm.Length == 0 && Same(Folder(_entries[k].Path), Folder(path)))
                        _entries[k] = _entries[k] with { Microfilm = film };
        }
        _entries[i] = e;
        Save();
    }

    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    /// <summary>Files were renamed or moved: their entries follow.</summary>
    public void Follow(IEnumerable<MovePair> moves)
    {
        bool changed = false;
        foreach (var m in moves)
        {
            int i = _entries.FindIndex(e => Same(e.Path, m.From));
            if (i >= 0) { _entries[i] = _entries[i] with { Path = m.To }; changed = true; }
        }
        if (changed) Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_entries, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Folder of a file relative to the longest matching root (a pinned folder or the drive),
    /// e.g. <c>D:\QC\73\524\0\0\80\x.jpg</c> with root <c>D:\QC</c> → <c>73\524\0\0\80</c>.
    /// </summary>
    public static string RelativeFolder(string path, IEnumerable<string> roots)
    {
        string folder = Folder(path);
        string? best = roots.Select(r => r.TrimEnd('\\', '/'))
            .Where(r => r.Length > 0 && (Same(folder, r) || folder.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase) || folder.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.Length).FirstOrDefault();
        if (best is null) return folder;
        string rel = folder.Length > best.Length ? folder[(best.Length + 1)..] : "";
        return rel.Length > 0 ? rel : System.IO.Path.GetFileName(folder);
    }

    /// <summary>Table rows: microfilm, folder, file, reason, date, full path.</summary>
    public List<string[]> Rows(IEnumerable<string> roots)
    {
        var r = roots.ToList();
        return _entries
            .OrderBy(e => Folder(e.Path), NaturalComparer.Instance).ThenBy(e => System.IO.Path.GetFileName(e.Path), NaturalComparer.Instance)
            .Select(e => new[] { e.Microfilm, RelativeFolder(e.Path, r), System.IO.Path.GetFileName(e.Path), e.Reason, e.Marked.ToString("yyyy-MM-dd HH:mm"), e.Path })
            .ToList();
    }

    /// <summary>Tab-separated text for pasting into Excel.</summary>
    public static string ToTsv(IEnumerable<string[]> rows, string[]? header) =>
        string.Join("\r\n", (header is null ? rows : rows.Prepend(header)).Select(r => string.Join('\t', r.Select(c => c.Replace('\t', ' ').Replace("\r", " ").Replace("\n", " ")))));

    /// <summary>CSV with ';' (what Excel expects in Polish and most European settings), UTF-8 with BOM.</summary>
    public static byte[] ToCsv(IEnumerable<string[]> rows, string[] header)
    {
        static string Cell(string c) => c.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c;
        var text = string.Join("\r\n", rows.Prepend(header).Select(r => string.Join(';', r.Select(Cell)))) + "\r\n";
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
    }
}
