using System.Text.RegularExpressions;
using Loupe.Core.Editing;
using Loupe.Core.Imaging;

namespace Loupe.Core.Files;

/// <summary>How to cut: into <see cref="Parts"/> pieces at <see cref="Cuts"/> (fractions, ascending).</summary>
public sealed record SliceSettings
{
    public int Parts { get; init; } = 2;
    /// <summary>true: cuts run top to bottom (left / right pages of a spread); false: left to right.</summary>
    public bool Vertical { get; init; } = true;
    /// <summary>Cut positions as fractions of the width (vertical) or height; empty = equal parts.</summary>
    public List<double> Cuts { get; init; } = [];
    /// <summary>Number the pieces right to left (or bottom to top), e.g. for books bound on the right.</summary>
    public bool Reverse { get; init; }

    public double[] CutPositions()
    {
        int parts = Math.Clamp(Parts, 2, 12);
        var c = Cuts.Count == parts - 1 ? Cuts.Select(x => Math.Clamp(x, 0.02, 0.98)).OrderBy(x => x).ToArray()
                                        : Enumerable.Range(1, parts - 1).Select(i => (double)i / parts).ToArray();
        return c;
    }
}

public sealed record SliceOutput(string Source, int Part, string Target);

public sealed record SlicePlan(
    string Folder, List<string> Sources, List<SliceOutput> Outputs, List<MovePair> Renames,
    int Files, List<string> Problems, string? Error)
{
    public bool CanRun => Error is null && Problems.Count == 0 && Outputs.Count > 0;
}

/// <summary>
/// Cuts images into pieces (typically a double-page spread into two pages) and renumbers the folder
/// so the numbering stays continuous: with <c>x_00041.jpg</c> sliced in two, the pieces become
/// <c>x_00041</c> and <c>x_00042</c>, and the old 00042, 00043… move up by one. Nothing is
/// overwritten (SafeMover), and the originals are kept so the job can be undone.
/// </summary>
public static class Slicer
{
    private static readonly Regex Numbered = new(@"^(?<prefix>.*?)(?<num>\d+)(?<ext>\.[^.]+)$", RegexOptions.CultureInvariant);

    public static SlicePlan Plan(IReadOnlyList<string> sources, SliceSettings s)
    {
        SlicePlan Fail(string e) => new("", [], [], [], 0, [], e);
        if (sources.Count == 0) return Fail("Select the images to slice.");
        string folder = Path.GetDirectoryName(Path.GetFullPath(sources[0]))!;
        if (sources.Any(p => !string.Equals(Path.GetDirectoryName(Path.GetFullPath(p)), folder, StringComparison.OrdinalIgnoreCase)))
            return Fail("All images to slice must be in the same folder.");
        int parts = Math.Clamp(s.Parts, 2, 12);

        var files = Directory.EnumerateFiles(folder).Where(FolderScanner.IsImage)
            .OrderBy(f => Path.GetFileName(f), NaturalComparer.Instance).ToList();
        var srcSet = new HashSet<string>(sources.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var p in srcSet) if (!files.Contains(p, StringComparer.OrdinalIgnoreCase)) return Fail($"File not found: {p}");

        // Every image of the folder must share one numbered pattern to be renumbered.
        var parsed = files.Select(f => Numbered.Match(Path.GetFileName(f))).ToList();
        if (parsed.Any(m => !m.Success))
            return Fail("Some file names in this folder do not end with a number, so the folder cannot be renumbered.");
        string prefix = parsed[0].Groups["prefix"].Value, ext = parsed[0].Groups["ext"].Value;
        if (parsed.Any(m => !string.Equals(m.Groups["prefix"].Value, prefix, StringComparison.OrdinalIgnoreCase)))
            return Fail("The file names in this folder do not all start the same way, so the folder cannot be renumbered.");
        int width = parsed.Max(m => m.Groups["num"].Value.Length);
        long start = parsed.Min(m => long.Parse(m.Groups["num"].Value));
        long total = files.Count + (long)srcSet.Count * (parts - 1);
        width = Math.Max(width, (start + total - 1).ToString().Length);

        string Name(long n, string e) => prefix + n.ToString().PadLeft(width, '0') + e;
        var outputs = new List<SliceOutput>();
        var renames = new List<MovePair>();
        long c = start;
        for (int i = 0; i < files.Count; i++)
        {
            string f = files[i], e = parsed[i].Groups["ext"].Value;
            if (srcSet.Contains(f))
            {
                for (int k = 0; k < parts; k++) outputs.Add(new SliceOutput(f, k, Path.Combine(folder, Name(c++, e))));
            }
            else
            {
                string to = Path.Combine(folder, Name(c++, e));
                if (!string.Equals(to, f, StringComparison.Ordinal)) renames.Add(new MovePair(f, to));
            }
        }
        var problems = new List<string>();
        foreach (var src in srcSet)
        {
            try
            {
                var (w, h, _) = TurboJpeg.ReadHeader(File.ReadAllBytes(src));
                if ((s.Vertical ? w : h) < parts * 16) problems.Add($"{Path.GetFileName(src)} is too small to cut into {parts} pieces.");
            }
            catch (TurboJpegException e) { problems.Add($"{Path.GetFileName(src)}: {e.Message}"); }
        }
        var ordered = files.Where(srcSet.Contains).ToList();
        return new SlicePlan(folder, ordered, outputs, renames, files.Count, problems, null);
    }

    /// <summary>Pixel ranges of the pieces along the cut direction, in numbering order.</summary>
    public static List<(int From, int To)> Ranges(int length, SliceSettings s)
    {
        var cuts = s.CutPositions();
        var edges = new List<int> { 0 };
        edges.AddRange(cuts.Select(x => (int)Math.Round(x * length)));
        edges.Add(length);
        var r = new List<(int, int)>();
        for (int i = 0; i + 1 < edges.Count; i++) r.Add((edges[i], Math.Max(edges[i] + 1, edges[i + 1])));
        if (s.Reverse) r.Reverse();
        return r;
    }

    /// <summary>
    /// Runs the plan. Pieces are written next to the originals first (temporary names), the
    /// originals go to <paramref name="keepFolder"/>, then everything moves to its final name.
    /// </summary>
    public static UndoLog Run(SlicePlan plan, SliceSettings s, string keepFolder, BackupStore? backups = null, int quality = 0)
    {
        if (!plan.CanRun) throw new InvalidOperationException(plan.Error ?? string.Join(" ", plan.Problems));
        string tag = Guid.NewGuid().ToString("N")[..8];
        var temps = new List<string>();
        var moves = new List<MovePair>();
        var restore = new List<MovePair>();
        Directory.CreateDirectory(keepFolder);
        try
        {
            // 1. Pieces to temporary files.
            foreach (var src in plan.Sources)
            {
                byte[] bytes = File.ReadAllBytes(src);
                int q = quality is >= 1 and <= 100 ? quality : JpegMeta.EstimateQuality(bytes) is > 0 and var est ? est : 95;
                var img = TurboJpeg.DecodeNative(bytes);
                var ranges = Ranges(s.Vertical ? img.Width : img.Height, s);
                var outs = plan.Outputs.Where(o => string.Equals(o.Source, src, StringComparison.OrdinalIgnoreCase)).OrderBy(o => o.Part).ToList();
                for (int k = 0; k < outs.Count; k++)
                {
                    var (a, b) = ranges[k];
                    var piece = s.Vertical ? ImageEdit.Crop(img, (a, 0, b - a, img.Height)) : ImageEdit.Crop(img, (0, a, img.Width, b - a));
                    string tmp = Path.Combine(plan.Folder, $".loupe-{tag}-{temps.Count}.tmp");
                    File.WriteAllBytes(tmp, JpegMeta.CopyMetadata(bytes, TurboJpeg.Encode(piece, q)));
                    temps.Add(tmp);
                    moves.Add(new MovePair(tmp, outs[k].Target));
                }
            }
            // 2. Originals (and their kept originals from earlier edits) aside.
            foreach (var src in plan.Sources)
            {
                string kept = Path.Combine(keepFolder, Path.GetFileName(src));
                File.Move(src, kept);
                restore.Add(new MovePair(kept, src));
                if (backups is not null && backups.Has(src))
                {
                    string keptBackup = Path.Combine(keepFolder, "original-" + Path.GetFileName(src));
                    File.Move(backups.PathFor(src), keptBackup);
                    restore.Add(new MovePair(keptBackup, backups.PathFor(src)));
                }
            }
            // 3. Renumber and put the pieces in place, without overwriting anything.
            var all = plan.Renames.Concat(moves).ToList();
            SafeMover.Run(all, backups);
            return new UndoLog
            {
                Kind = "slice",
                Title = $"Slice of {plan.Sources.Count} images",
                Moves = all,
                RemoveOnUndo = temps,
                RestoreOnUndo = restore,
            };
        }
        catch
        {
            foreach (var r in restore) if (File.Exists(r.From) && !File.Exists(r.To)) File.Move(r.From, r.To);
            foreach (var t in temps) if (File.Exists(t)) File.Delete(t);
            throw;
        }
    }
}
