namespace Loupe.Core.Files;

/// <summary>
/// File access that copes with Windows locking. Reads allow the file to be renamed or deleted
/// while open (another thumbnail being read must never block a rename), and moves / deletes
/// retry a few times when a file is briefly busy (antivirus, the search indexer, network shares,
/// Explorer making its own thumbnail).
/// </summary>
public static class Io
{
    public static byte[] Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        long len = fs.Length;
        if (len > int.MaxValue) throw new IOException($"File too large: {path}");
        var buf = GC.AllocateUninitializedArray<byte>((int)len);
        int read = 0;
        while (read < buf.Length)
        {
            int n = fs.Read(buf, read, buf.Length - read);
            if (n == 0) break;
            read += n;
        }
        return read == buf.Length ? buf : buf[..read];
    }

    public static void Move(string from, string to, bool overwrite = false) => Retry(() => File.Move(from, to, overwrite));

    public static void Delete(string path) => Retry(() => { if (File.Exists(path)) File.Delete(path); });

    /// <summary>Runs <paramref name="action"/>, retrying up to ~2.5 s on sharing violations.</summary>
    public static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (IOException e) when (attempt < 10 && IsBusy(e)) { Thread.Sleep(50 * attempt); }
            catch (UnauthorizedAccessException) when (attempt < 4) { Thread.Sleep(100 * attempt); }
        }
    }

    // ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33) arrive as HResult 0x80070020 / 0x80070021.
    private static bool IsBusy(IOException e) =>
        (e.HResult & 0xFFFF) is 32 or 33 || e.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase);
}
