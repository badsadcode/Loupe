using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Loupe.Core.Files;

/// <summary>
/// Local copies of image files from slow places (network shares over VPN, external disks), so
/// each file crosses the slow link once. Entries are keyed by path + size + modification time:
/// a file that changes at the source simply gets a new entry. The cache keeps itself under its
/// size limit by removing the files used longest ago.
/// </summary>
public sealed class FileCache
{
    private readonly ConcurrentDictionary<string, DriveKind> _kinds = new(StringComparer.OrdinalIgnoreCase);
    private long _approxBytes = -1;
    private int _writesSinceTrim;

    public enum DriveKind { Local, Slow }

    public string Root { get; }
    public long MaxBytes { get; set; }
    /// <summary>"off" | "network" (network shares and removable drives) | "all".</summary>
    public string Mode { get; set; }

    public FileCache(string root, long maxBytes, string mode = "network")
    {
        Root = root;
        MaxBytes = maxBytes;
        Mode = mode;
    }

    /// <summary>Whether files at <paramref name="path"/> go through the cache with the current mode.</summary>
    public bool Applies(string path)
    {
        if (Mode == "off") return false;
        if (Mode == "all") return !path.StartsWith(Root, StringComparison.OrdinalIgnoreCase);
        return Kind(path) == DriveKind.Slow;
    }

    private DriveKind Kind(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return DriveKind.Slow;   // UNC share
        string root = Path.GetPathRoot(full) ?? "";
        return _kinds.GetOrAdd(root, r =>
        {
            try
            {
                var d = new DriveInfo(r);
                return d.DriveType is DriveType.Network or DriveType.Removable ? DriveKind.Slow : DriveKind.Local;
            }
            catch (Exception e) when (e is ArgumentException or IOException) { return DriveKind.Local; }
        });
    }

    private string EntryPath(FileInfo fi)
    {
        string key = $"{fi.FullName.ToUpperInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(Root, hash[..2], hash + Path.GetExtension(fi.Name).ToLowerInvariant());
    }

    /// <summary>True when an up-to-date copy of the file is cached.</summary>
    public bool Has(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists && File.Exists(EntryPath(fi)); }
        catch (IOException) { return false; }
    }

    /// <summary>The file's bytes, from the local copy when there is one; otherwise read and kept.</summary>
    public byte[] Read(string path)
    {
        if (!Applies(path)) return Io.Read(path);
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("File not found.", path);
        string entry = EntryPath(fi);
        try
        {
            if (File.Exists(entry))
            {
                var bytes = Io.Read(entry);
                if (bytes.Length == fi.Length) { Touch(entry); return bytes; }
            }
        }
        catch (IOException) { }
        var data = Io.Read(path);
        Store(entry, data);
        return data;
    }

    /// <summary>
    /// The start of the file (JPEG headers: size, quality, dpi) without pulling the whole file
    /// over a slow link — from the cache when the file is there.
    /// </summary>
    public byte[] ReadHead(string path, int bytes = 512 * 1024)
    {
        string src = path;
        if (Applies(path))
        {
            try { var fi = new FileInfo(path); string e = EntryPath(fi); if (File.Exists(e)) src = e; }
            catch (IOException) { }
        }
        using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var buf = new byte[(int)Math.Min(bytes, fs.Length)];
        int read = 0;
        while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n == 0) break; read += n; }
        return read == buf.Length ? buf : buf[..read];
    }

    /// <summary>Copies the file into the cache if it is not there yet (background pre-caching). True when copied.</summary>
    public bool Warm(string path, CancellationToken ct = default)
    {
        if (!Applies(path)) return false;
        var fi = new FileInfo(path);
        if (!fi.Exists) return false;
        string entry = EntryPath(fi);
        if (File.Exists(entry)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
        string tmp = entry + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
        try
        {
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buf, 0, n);
                }
            }
            Io.Move(tmp, entry, overwrite: true);
            Added(fi.Length);
            return true;
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
    }

    private void Store(string entry, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            string tmp = entry + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
            File.WriteAllBytes(tmp, data);
            Io.Move(tmp, entry, overwrite: true);
            Added(data.Length);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Touch(string entry)
    {
        try { File.SetLastAccessTimeUtc(entry, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Added(long bytes)
    {
        if (_approxBytes >= 0) Interlocked.Add(ref _approxBytes, bytes);
        if (Interlocked.Increment(ref _writesSinceTrim) >= 50 || (_approxBytes > MaxBytes && MaxBytes > 0))
        {
            _writesSinceTrim = 0;
            ThreadPool.QueueUserWorkItem(_ => Trim());
        }
    }

    private readonly object _trimLock = new();

    /// <summary>Removes the least recently used copies until the cache is under 90 % of its limit.</summary>
    public void Trim()
    {
        if (!Monitor.TryEnter(_trimLock)) return;
        try
        {
            if (!Directory.Exists(Root)) { _approxBytes = 0; return; }
            var files = new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => !f.Name.EndsWith(".tmp")).ToList();
            long total = files.Sum(f => f.Length);
            if (MaxBytes > 0 && total > MaxBytes)
            {
                long target = (long)(MaxBytes * 0.9);
                foreach (var f in files.OrderBy(f => Max(f.LastAccessTimeUtc, f.LastWriteTimeUtc)))
                {
                    if (total <= target) break;
                    try { f.Delete(); total -= f.Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            _approxBytes = total;
        }
        finally { Monitor.Exit(_trimLock); }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    public (int Files, long Bytes) Usage()
    {
        if (!Directory.Exists(Root)) return (0, 0);
        int n = 0; long bytes = 0;
        foreach (var f in new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories)) { n++; bytes += f.Length; }
        _approxBytes = bytes;
        return (n, bytes);
    }

    public void Clear()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        _approxBytes = 0;
    }
}
