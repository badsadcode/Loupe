using System.Runtime.InteropServices;

namespace Loupe.Core.Imaging;

/// <summary>
/// Loupe additions to the TurboJPEG wrapper (copied from Striplight PRO):
///  * scaled decoding — libjpeg-turbo decodes straight to 1/8, 1/4, 1/2… size inside the
///    DCT, which makes thumbnails and screen-size previews several times faster;
///  * lossless transforms — rotate / mirror without decoding and re-encoding.
/// </summary>
public static partial class TurboJpeg
{
    private const int ColorspaceCmyk = 3; // TJCS_CMYK
    private const int ColorspaceYcck = 4; // TJCS_YCCK

    [StructLayout(LayoutKind.Sequential)]
    private struct TjRegion { public int X, Y, W, H; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TjTransform
    {
        public TjRegion R;
        public int Op;
        public int Options;
        public IntPtr Data;
        public IntPtr CustomFilter;
    }

    [LibraryImport(Lib)] private static partial IntPtr tjInitTransform();
    [LibraryImport(Lib)]
    private static unsafe partial int tjTransform(IntPtr handle, byte* jpeg, CULong size, int n,
        IntPtr* dstBufs, CULong* dstSizes, TjTransform* transforms, int flags);

    // TJXOP_* and TJXOPT_*
    public const int XopNone = 0, XopHFlip = 1, XopVFlip = 2, XopTranspose = 3, XopTransverse = 4,
        XopRot90 = 5, XopRot180 = 6, XopRot270 = 7;
    private const int XoptPerfect = 1;

    /// <summary>The lossless operation for "rotate clockwise by <paramref name="rotation"/>, then mirror".</summary>
    public static int TransformOp(int rotation, bool mirror) => (((rotation % 360) + 360) % 360, mirror) switch
    {
        (0, false) => XopNone,
        (90, false) => XopRot90,
        (180, false) => XopRot180,
        (270, false) => XopRot270,
        (0, true) => XopHFlip,
        (90, true) => XopTranspose,
        (180, true) => XopVFlip,
        (270, true) => XopTransverse,
        _ => throw new ArgumentOutOfRangeException(nameof(rotation), "Rotation must be 0, 90, 180 or 270."),
    };

    /// <summary>
    /// Lossless rotate / mirror (markers such as JFIF density and EXIF are kept). Returns null
    /// when the image size is not a multiple of the JPEG block size and the transform would
    /// have to drop or garble edge pixels: the caller then re-encodes instead.
    /// </summary>
    public static unsafe byte[]? TransformLossless(ReadOnlySpan<byte> jpeg, int op)
    {
        var h = tjInitTransform();
        if (h == IntPtr.Zero) throw new TurboJpegException(LastError("TurboJPEG transform failed."));
        IntPtr buf = IntPtr.Zero;
        try
        {
            var size = new CULong(0);
            var t = new TjTransform { Op = op, Options = XoptPerfect };
            fixed (byte* p = jpeg)
            {
                if (tjTransform(h, p, new CULong((nuint)jpeg.Length), 1, &buf, &size, &t, 0) != 0) return null;
            }
            if (buf == IntPtr.Zero || size.Value == 0) return null;
            var result = new byte[(int)size.Value];
            Marshal.Copy(buf, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (buf != IntPtr.Zero) tjFree(buf);
            tjDestroy(h);
        }
    }

    /// <summary>
    /// Decodes at the smallest DCT scale (n/8) whose longer side is still at least
    /// <paramref name="minLongSide"/> pixels. Colour images decode to BGRA, grayscale to gray.
    /// </summary>
    public static unsafe DecodedImage DecodeAtLeast(ReadOnlySpan<byte> jpeg, int minLongSide, bool forceGray = false)
    {
        var h = tjInitDecompress();
        if (h == IntPtr.Zero) throw new TurboJpegException(LastError("TurboJPEG decode failed."));
        try
        {
            fixed (byte* p = jpeg)
            {
                var size = new CULong((nuint)jpeg.Length);
                if (tjDecompressHeader3(h, p, size, out int w, out int ht, out _, out int cs) != 0)
                    throw new TurboJpegException(LastError("TurboJPEG decode failed."));
                if (w <= 0 || ht <= 0) throw new TurboJpegException("JPEG has invalid dimensions.");
                if (cs is ColorspaceCmyk or ColorspaceYcck) throw new TurboJpegException("CMYK JPEGs are not supported.");

                int longSide = Math.Max(w, ht), num = 8;
                if (minLongSide > 0)
                    for (int n = 1; n <= 8; n++)
                        if ((longSide * n + 7) / 8 >= minLongSide) { num = n; break; }
                int sw = (w * num + 7) / 8, sh = (ht * num + 7) / 8;

                bool gray = forceGray || cs == ColorspaceGray;
                int fmt = gray ? PixelFormatGray : PixelFormatBgra, channels = gray ? 1 : 4;
                var pixels = GC.AllocateUninitializedArray<byte>(checked(sw * sh * channels));
                fixed (byte* d = pixels)
                {
                    // Fast DCT for reduced sizes (invisible after downscaling), accurate for full size.
                    int flags = num == 8 ? FlagAccurateDct : 2048; // TJFLAG_FASTDCT
                    if (tjDecompress2(h, p, size, d, sw, sw * channels, sh, fmt, flags) != 0)
                        throw new TurboJpegException(LastError("TurboJPEG decode failed."));
                }
                return new DecodedImage(sw, sh, channels, pixels);
            }
        }
        finally { tjDestroy(h); }
    }

    /// <summary>Full-size decode: grayscale JPEGs to gray, everything else to BGRA.</summary>
    public static DecodedImage DecodeNative(ReadOnlySpan<byte> jpeg) => DecodeAtLeast(jpeg, 0);

    public static byte[] Encode(DecodedImage img, int quality, bool progressive = false) =>
        Encode(img.Pixels, img.Width, img.Height, img.Stride,
            img.Channels == 1 ? PixelFormatGray : PixelFormatBgra, quality, progressive);
}
