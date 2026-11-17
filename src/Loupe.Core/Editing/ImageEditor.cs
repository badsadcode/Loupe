using System.Security.Cryptography;
using System.Text;
using Loupe.Core.Imaging;

namespace Loupe.Core.Editing;

/// <summary>
/// Originals of edited files, kept outside the archive folders (so delivered folders stay
/// clean) in a mirror of their full path: <c>E:\roll\a.jpg</c> → <c>&lt;root&gt;\E\roll\a.jpg</c>,
/// <c>\\server\share\a.jpg</c> → <c>&lt;root&gt;\UNC\server\share\a.jpg</c>.
/// Only the very first original is kept, so "Restore original" always goes back to the
/// file as it was before Loupe touched it, however many times it was edited.
/// </summary>
public sealed class BackupStore(string root)
{
    public string Root { get; } = root;

    public string PathFor(string file)
    {
        string full = Path.GetFullPath(file);
        string rel;
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) rel = Path.Combine("UNC", full[2..]);
        else if (full.Length >= 2 && full[1] == ':') rel = Path.Combine(full[0].ToString().ToUpperInvariant(), full[2..].TrimStart('\\', '/'));
        else rel = full.TrimStart('/', '\\');
        return Path.Combine(Root, rel);
    }

    public bool Has(string file) => File.Exists(PathFor(file));

    /// <summary>Saves <paramref name="originalBytes"/> as the backup of <paramref name="file"/> unless one exists.</summary>
    public void EnsureBackup(string file, byte[] originalBytes)
    {
        string b = PathFor(file);
        if (File.Exists(b)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        string tmp = b + ".tmp";
        File.WriteAllBytes(tmp, originalBytes);
        File.Move(tmp, b, overwrite: true);
    }

    /// <summary>Puts the original back and removes the backup. False when there is none.</summary>
    public bool Restore(string file)
    {
        string b = PathFor(file);
        if (!File.Exists(b)) return false;
        FileWrite.ReplaceAtomically(file, File.ReadAllBytes(b));
        Delete(b);
        return true;
    }

    /// <summary>A file was renamed or moved: its backup follows it.</summary>
    public void Follow(string from, string to)
    {
        string a = PathFor(from);
        if (!File.Exists(a)) return;
        string b = PathFor(to);
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.Move(a, b, overwrite: true);
        PruneEmpty(Path.GetDirectoryName(a));
    }

    private void Delete(string backup)
    {
        File.Delete(backup);
        PruneEmpty(Path.GetDirectoryName(backup));
    }

    private void PruneEmpty(string? dir)
    {
        string root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar);
        while (dir is not null && dir.Length > root.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    public (int Files, long Bytes) Usage()
    {
        if (!Directory.Exists(Root)) return (0, 0);
        int n = 0; long bytes = 0;
        foreach (var f in new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories)) { n++; bytes += f.Length; }
        return (n, bytes);
    }

    public void Clear()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

public static class FileWrite
{
    /// <summary>Writes next to the target first, then swaps it in, so a crash never leaves half a file.</summary>
    public static void ReplaceAtomically(string path, byte[] bytes)
    {
        string tmp = path + ".loupe-tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed record SaveOptions
{
    /// <summary>JPEG quality 1–100; 0 = the original file's quality (estimated), else 95.</summary>
    public int Quality { get; init; }
    /// <summary>Rotate / mirror without re-encoding when the image size allows it.</summary>
    public bool LosslessWhenPossible { get; init; } = true;
}

public sealed record EditResult(string Path, bool Ok, bool Lossless, int Quality, string? Error);

/// <summary>Applies a correction to image files on disk: backup, edit, safe replace.</summary>
public sealed class ImageEditor(BackupStore backups)
{
    public BackupStore Backups { get; } = backups;

    public EditResult Apply(string path, EditSettings edit, SaveOptions save)
    {
        try
        {
            if (edit.IsNoOp) return new EditResult(path, true, true, 0, null);
            byte[] original = File.ReadAllBytes(path);
            var (bytes, lossless, q) = Render(original, edit, save);
            Backups.EnsureBackup(path, original);
            FileWrite.ReplaceAtomically(path, bytes);
            return new EditResult(path, true, lossless, q, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TurboJpegException or ArgumentException)
        {
            return new EditResult(path, false, false, 0, ex.Message);
        }
    }

    /// <summary>The new file contents for <paramref name="original"/> with <paramref name="edit"/> applied.</summary>
    public static (byte[] Bytes, bool Lossless, int Quality) Render(byte[] original, EditSettings edit, SaveOptions save)
    {
        if (edit.IsPixelNoOp && save.LosslessWhenPossible)
        {
            var t = TurboJpeg.TransformLossless(original, TurboJpeg.TransformOp(edit.NormalRotation, edit.Mirror));
            if (t is not null) return (t, true, 0);
        }
        int q = save.Quality is >= 1 and <= 100 ? save.Quality : JpegMeta.EstimateQuality(original) is > 0 and var est ? est : 95;
        var img = ImageEdit.Apply(TurboJpeg.DecodeNative(original), edit);
        var encoded = TurboJpeg.Encode(img, q);
        return (JpegMeta.CopyMetadata(original, encoded), false, q);
    }
}

/// <summary>
/// Local disk cache of thumbnails and the renderer for on-screen views. Keys include the
/// file's size and modification time, so an edited file never shows a stale thumbnail.
/// </summary>
public sealed class ImageRenderer(string thumbRoot)
{
    public string ThumbRoot { get; } = thumbRoot;

    private string ThumbPath(string file, int size, FileInfo fi)
    {
        string key = $"{Path.GetFullPath(file).ToUpperInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{size}";
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(ThumbRoot, hash[..2], hash + ".jpg");
    }

    /// <summary>
    /// Thumbnail with a correction previewed on it: the cached thumbnail, corrected and re-encoded
    /// (adaptive tones are worked out on the small image, close to but not exactly the full result).
    /// </summary>
    public byte[] Thumbnail(string file, int size, EditSettings? edit)
    {
        var plain = Thumbnail(file, size);
        if (edit is null || edit.IsNoOp) return plain;
        return TurboJpeg.Encode(ImageEdit.Apply(TurboJpeg.DecodeNative(plain), edit), 85);
    }

    /// <summary>Thumbnail JPEG (longer side <paramref name="size"/>), from the cache when possible.</summary>
    public byte[] Thumbnail(string file, int size)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists) throw new FileNotFoundException("File not found.", file);
        string cached = ThumbPath(file, size, fi);
        try { if (File.Exists(cached)) return File.ReadAllBytes(cached); }
        catch (IOException) { }

        var bytes = File.ReadAllBytes(file);
        var img = ImageEdit.Fit(TurboJpeg.DecodeAtLeast(bytes, size), size, size);
        var jpg = TurboJpeg.Encode(img, 82);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            FileWrite.ReplaceAtomically(cached, jpg);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return jpg;
    }

    /// <summary>
    /// Screen view: decoded at the smallest size that still covers <paramref name="maxSide"/>,
    /// the edit applied (live preview), JPEG out. <paramref name="maxSide"/> 0 = full size.
    /// </summary>
    public static byte[] View(byte[] fileBytes, int maxSide, EditSettings? edit, int quality = 90)
    {
        var img = TurboJpeg.DecodeAtLeast(fileBytes, maxSide);
        if (edit is not null && !edit.IsNoOp) img = ImageEdit.Apply(img, edit);
        if (maxSide > 0) img = ImageEdit.Fit(img, maxSide, maxSide);
        return TurboJpeg.Encode(img, quality);
    }

    public (int Files, long Bytes) Usage()
    {
        if (!Directory.Exists(ThumbRoot)) return (0, 0);
        int n = 0; long bytes = 0;
        foreach (var f in new DirectoryInfo(ThumbRoot).EnumerateFiles("*.jpg", SearchOption.AllDirectories)) { n++; bytes += f.Length; }
        return (n, bytes);
    }

    public void Clear()
    {
        if (Directory.Exists(ThumbRoot)) Directory.Delete(ThumbRoot, recursive: true);
    }
}
