using System.Text.Json;
using Loupe.Core.Editing;

namespace Loupe.Core.Files;

public sealed record MovePair(string From, string To);

/// <summary>What a rename or merge did, enough to undo it.</summary>
public sealed record UndoLog
{
    public string Kind { get; init; } = "";            // "rename" | "merge"
    public string Title { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;
    public List<MovePair> Moves { get; init; } = [];
    public List<string> CreatedFolders { get; init; } = [];
    public List<string> DeletedFolders { get; init; } = [];
    /// <summary>Slice: files created by the job, removed again on undo (after the moves are reversed).</summary>
    public List<string> RemoveOnUndo { get; init; } = [];
    /// <summary>Slice: originals put aside (From = where they are kept, To = where they go back).</summary>
    public List<MovePair> RestoreOnUndo { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static UndoLog? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<UndoLog>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
}

/// <summary>
/// Renames / moves many files without ever overwriting one: every file first gets a unique
/// temporary name in its own folder, then goes to its final name. So swaps (a→b, b→a) and
/// shifted numbering (1→2, 2→3…) work, and a name that already exists stops the job before
/// anything is touched. If something fails half-way, the files already moved go back.
/// </summary>
public static class SafeMover
{
    public static void Run(IReadOnlyList<MovePair> moves, BackupStore? backups = null)
    {
        var real = moves.Where(m => !string.Equals(m.From, m.To, StringComparison.Ordinal)).ToList();
        if (real.Count == 0) return;

        // Check everything first.
        var sources = new HashSet<string>(real.Select(m => Norm(m.From)), StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in real)
        {
            if (!File.Exists(m.From)) throw new IOException($"File not found: {m.From}");
            if (!targets.Add(Norm(m.To))) throw new IOException($"Two files would get the same name: {m.To}");
            if (File.Exists(m.To) && !sources.Contains(Norm(m.To))) throw new IOException($"File already exists: {m.To}");
        }

        string tag = Guid.NewGuid().ToString("N")[..8];
        var temps = new List<(MovePair Move, string Temp)>();
        var done = new List<(MovePair Move, string Temp)>();
        try
        {
            for (int i = 0; i < real.Count; i++)
            {
                var m = real[i];
                string tmp = Path.Combine(Path.GetDirectoryName(m.From)!, $".loupe-{tag}-{i}.tmp");
                File.Move(m.From, tmp);
                temps.Add((m, tmp));
            }
            foreach (var (m, tmp) in temps)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(m.To)!);
                File.Move(tmp, m.To);
                done.Add((m, tmp));
            }
        }
        catch
        {
            // Roll back: finished files to their temp names, then every temp to its original name.
            foreach (var (m, tmp) in done) TryMove(m.To, tmp);
            foreach (var (m, tmp) in temps) TryMove(tmp, m.From);
            throw;
        }
        if (backups is not null)
            foreach (var m in real)
                try { backups.Follow(m.From, m.To); } catch (IOException) { }
    }

    private static void TryMove(string a, string b)
    {
        try { if (File.Exists(a) && !File.Exists(b)) File.Move(a, b); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Norm(string p) => Path.GetFullPath(p);

    /// <summary>Reverses a logged rename or merge.</summary>
    public static void Undo(UndoLog log, BackupStore? backups = null)
    {
        foreach (var d in log.DeletedFolders) Directory.CreateDirectory(d);
        Run(log.Moves.Select(m => new MovePair(m.To, m.From)).ToList(), backups);
        foreach (var f in log.RemoveOnUndo) if (File.Exists(f)) File.Delete(f);
        foreach (var r in log.RestoreOnUndo)
        {
            if (!File.Exists(r.From)) continue;
            if (File.Exists(r.To)) throw new IOException($"File already exists: {r.To}");
            Directory.CreateDirectory(Path.GetDirectoryName(r.To)!);
            File.Move(r.From, r.To);
        }
        foreach (var d in log.CreatedFolders.OrderByDescending(x => x.Length))
            if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
    }
}
