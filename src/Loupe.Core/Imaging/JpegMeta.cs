namespace Loupe.Core.Imaging;

/// <summary>
/// Reads the parts of a JPEG file that matter when Loupe saves a corrected copy:
/// the quality it was saved with (estimated from the quantisation table), the resolution
/// (JFIF density, e.g. 300 dpi) and the metadata segments (JFIF, EXIF, comments), which are
/// copied into the new file so archives keep their dpi and descriptions.
/// </summary>
public static class JpegMeta
{
    public sealed record Info(int Width, int Height, bool IsGray, int Quality, int DpiX, int DpiY, long FileSize);

    // IJG standard luminance table in zigzag order (the order DQT stores it in).
    private static readonly int[] StdLumaZigzag = BuildStdZigzag();

    private static int[] BuildStdZigzag()
    {
        int[] natural =
        [
            16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55,
            14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
            18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
            49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
        ];
        int[] zz =
        [
            0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        ];
        var r = new int[64];
        for (int i = 0; i < 64; i++) r[i] = natural[zz[i]];
        return r;
    }

    /// <summary>A marker segment: <paramref name="Marker"/> (e.g. 0xE0), offset of the 0xFF and total length including marker.</summary>
    public readonly record struct Segment(byte Marker, int Offset, int Length);

    /// <summary>Segments between SOI and the start of scan (SOS not included).</summary>
    public static List<Segment> HeaderSegments(ReadOnlySpan<byte> jpeg)
    {
        var list = new List<Segment>();
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return list;
        int i = 2;
        while (i + 4 <= jpeg.Length)
        {
            if (jpeg[i] != 0xFF) break;
            byte m = jpeg[i + 1];
            if (m == 0xFF) { i++; continue; }          // fill byte
            if (m == 0xDA || m == 0xD9) break;         // SOS / EOI
            int len = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (len < 2 || i + 2 + len > jpeg.Length) break;
            list.Add(new Segment(m, i, len + 2));
            i += len + 2;
        }
        return list;
    }

    /// <summary>
    /// Estimated IJG quality (1–100) from the luminance quantisation table, or 0 when the
    /// file uses a non-standard table that does not resemble any IJG quality.
    /// </summary>
    public static int EstimateQuality(ReadOnlySpan<byte> jpeg)
    {
        foreach (var s in HeaderSegments(jpeg))
        {
            if (s.Marker != 0xDB) continue;
            int p = s.Offset + 4, end = s.Offset + s.Length;
            while (p < end)
            {
                int pq = jpeg[p] >> 4, tq = jpeg[p] & 15;
                p++;
                var table = new int[64];
                for (int k = 0; k < 64 && p < end; k++)
                {
                    table[k] = pq == 0 ? jpeg[p] : (jpeg[p] << 8) | jpeg[p + 1];
                    p += pq == 0 ? 1 : 2;
                }
                if (tq == 0) return MatchQuality(table);
            }
        }
        return 0;
    }

    private static int MatchQuality(int[] table)
    {
        int best = 0;
        double bestErr = double.MaxValue;
        for (int q = 1; q <= 100; q++)
        {
            int scale = q < 50 ? 5000 / q : 200 - q * 2;
            double err = 0;
            for (int i = 0; i < 64; i++)
            {
                int v = Math.Clamp((StdLumaZigzag[i] * scale + 50) / 100, 1, 255);
                err += Math.Abs(v - table[i]);
            }
            if (err < bestErr) { bestErr = err; best = q; }
        }
        return bestErr / 64 <= 2.0 ? best : 0; // average difference over 2 steps: not an IJG table
    }

    /// <summary>JFIF density in dots per inch (0 when unknown or only an aspect ratio).</summary>
    public static (int X, int Y) Density(ReadOnlySpan<byte> jpeg)
    {
        foreach (var s in HeaderSegments(jpeg))
        {
            if (s.Marker != 0xE0 || s.Length < 18) continue;
            int p = s.Offset + 4;
            if (jpeg[p] != 'J' || jpeg[p + 1] != 'F' || jpeg[p + 2] != 'I' || jpeg[p + 3] != 'F' || jpeg[p + 4] != 0) continue;
            int unit = jpeg[p + 7], x = (jpeg[p + 8] << 8) | jpeg[p + 9], y = (jpeg[p + 10] << 8) | jpeg[p + 11];
            return unit switch
            {
                1 => (x, y),
                2 => ((int)Math.Round(x * 2.54), (int)Math.Round(y * 2.54)),
                _ => (0, 0),
            };
        }
        return (0, 0);
    }

    public static Info Read(ReadOnlySpan<byte> jpeg)
    {
        var (w, h, gray) = TurboJpeg.ReadHeader(jpeg);
        var (dx, dy) = Density(jpeg);
        return new Info(w, h, gray, EstimateQuality(jpeg), dx, dy, jpeg.Length);
    }

    private static bool IsMetadata(byte marker) => marker is >= 0xE0 and <= 0xEF or 0xFE;

    /// <summary>
    /// Returns <paramref name="encoded"/> with its own APPn / COM segments replaced by those of
    /// <paramref name="original"/> (JFIF density, EXIF, ICC profile, comments). The image data
    /// stays exactly as encoded.
    /// </summary>
    public static byte[] CopyMetadata(ReadOnlySpan<byte> original, ReadOnlySpan<byte> encoded)
    {
        var src = HeaderSegments(original).Where(s => IsMetadata(s.Marker)).ToList();
        if (src.Count == 0) return encoded.ToArray();
        var dstMeta = HeaderSegments(encoded).Where(s => IsMetadata(s.Marker)).ToList();

        using var ms = new MemoryStream(encoded.Length + src.Sum(s => s.Length));
        ms.Write(encoded[..2]);                                      // SOI
        foreach (var s in src) ms.Write(original.Slice(s.Offset, s.Length));
        int pos = 2;
        foreach (var s in dstMeta)                                   // skip the encoder's own APPn / COM
        {
            if (s.Offset > pos) ms.Write(encoded[pos..s.Offset]);
            pos = s.Offset + s.Length;
        }
        ms.Write(encoded[pos..]);
        return ms.ToArray();
    }
}
