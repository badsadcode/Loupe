namespace Loupe.Core.Imaging;

/// <summary>
/// One QC correction: tone (the Striplight PRO engine), sharpening, negative and orientation.
/// Applied in that order: negative → tone → sharpen → rotate (clockwise) → mirror.
/// </summary>
public sealed record EditSettings
{
    public int Method { get; init; } = ToneMethod.Striplight;
    public int Flags { get; init; }
    public int Contrast { get; init; }
    public int Brightness { get; init; }
    public int Gamma { get; init; } = 100;
    public int Strength { get; init; } = ToneAdjust.StrengthDefault;
    public int SharpenMode { get; init; }
    public int Sharpen { get; init; }
    public bool Negative { get; init; }
    /// <summary>0, 90, 180 or 270 degrees clockwise.</summary>
    public int Rotation { get; init; }
    public bool Mirror { get; init; }

    public ToneSettings Tone => new(Method, Flags, Contrast, Brightness, Gamma, Strength);

    public int NormalRotation => ((Rotation % 360) + 360) % 360 / 90 * 90;

    /// <summary>Changes nothing in the pixels' values (only orientation, if anything).</summary>
    public bool IsPixelNoOp => Tone.IsNoOp && !Negative && Sharpen <= 0;

    public bool IsNoOp => IsPixelNoOp && NormalRotation == 0 && !Mirror;

    public static EditSettings None { get; } = new();
}

public static class ImageEdit
{
    /// <summary>Applies <paramref name="e"/> and returns the result (the input is not reused afterwards).</summary>
    public static DecodedImage Apply(DecodedImage img, EditSettings e)
    {
        if (e.IsNoOp) return img;
        if (img.Channels == 1)
        {
            var g = new GrayImage(img.Width, img.Height, img.Pixels);
            ApplyGray(g, e);
            return new DecodedImage(g.Width, g.Height, 1, g.Data);
        }
        return ApplyBgra(img, e);
    }

    private static void ApplyGray(GrayImage g, EditSettings e)
    {
        if (e.Negative) g.Negative();
        var tone = e.Tone;
        if (!tone.IsNoOp) ToneAdjust.ApplyToneGray(g.Data, g.Width, g.Height, g.Width, tone, invertedPolarity: false);
        if (e.Sharpen > 0) g.Sharpen(e.SharpenMode, e.Sharpen);
        if (e.NormalRotation != 0 || e.Mirror) g.Orient(e.NormalRotation, e.Mirror, false);
    }

    /// <summary>
    /// Colour: lookup-table tones run on each channel; adaptive tones are worked out on the
    /// brightness (luma) and the change is added to every channel, so colours keep their hue.
    /// </summary>
    private static DecodedImage ApplyBgra(DecodedImage img, EditSettings e)
    {
        int w = img.Width, h = img.Height, n = w * h;
        var px = img.Pixels;
        if (e.Negative)
            for (int i = 0; i < n; i++) { int o = i * 4; px[o] = (byte)(255 - px[o]); px[o + 1] = (byte)(255 - px[o + 1]); px[o + 2] = (byte)(255 - px[o + 2]); }

        var tone = e.Tone;
        if (!tone.IsNoOp)
        {
            if (ToneAdjust.IsAdaptive(tone.Method))
            {
                var luma = new byte[n];
                for (int i = 0; i < n; i++) { int o = i * 4; luma[i] = (byte)((px[o] * 29 + px[o + 1] * 150 + px[o + 2] * 77 + 128) >> 8); }
                var adjusted = (byte[])luma.Clone();
                ToneAdjust.ApplyToneGray(adjusted, w, h, w, tone, invertedPolarity: false);
                for (int i = 0; i < n; i++)
                {
                    int d = adjusted[i] - luma[i];
                    if (d == 0) continue;
                    int o = i * 4;
                    px[o] = Clamp(px[o] + d); px[o + 1] = Clamp(px[o + 1] + d); px[o + 2] = Clamp(px[o + 2] + d);
                }
            }
            else
            {
                var lut = ToneAdjust.BuildLut(tone.Method, tone.Flags, tone.Contrast, tone.Brightness, tone.Gamma);
                for (int i = 0; i < n; i++) { int o = i * 4; px[o] = lut[px[o]]; px[o + 1] = lut[px[o + 1]]; px[o + 2] = lut[px[o + 2]]; }
            }
        }

        bool orient = e.NormalRotation != 0 || e.Mirror;
        if (e.Sharpen <= 0 && !orient) return img;

        // Sharpen and orient per channel with the grayscale code (same results as for gray images).
        var planes = new GrayImage[3];
        for (int c = 0; c < 3; c++)
        {
            var p = new GrayImage(w, h, clear: false);
            for (int i = 0; i < n; i++) p.Data[i] = px[i * 4 + c];
            if (e.Sharpen > 0) p.Sharpen(e.SharpenMode, e.Sharpen);
            if (orient) p.Orient(e.NormalRotation, e.Mirror, false);
            planes[c] = p;
        }
        int nw = planes[0].Width, nh = planes[0].Height;
        var outPx = GC.AllocateUninitializedArray<byte>(nw * nh * 4);
        for (int i = 0; i < nw * nh; i++)
        {
            int o = i * 4;
            outPx[o] = planes[0].Data[i]; outPx[o + 1] = planes[1].Data[i]; outPx[o + 2] = planes[2].Data[i]; outPx[o + 3] = 255;
        }
        return new DecodedImage(nw, nh, 4, outPx);
    }

    private static byte Clamp(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;

    /// <summary>Area-average downscale so the image fits in <paramref name="maxW"/> × <paramref name="maxH"/> (never enlarges).</summary>
    public static DecodedImage Fit(DecodedImage img, int maxW, int maxH)
    {
        if (img.Width <= maxW && img.Height <= maxH) return img;
        double s = Math.Min((double)maxW / img.Width, (double)maxH / img.Height);
        int nw = Math.Max(1, (int)Math.Round(img.Width * s)), nh = Math.Max(1, (int)Math.Round(img.Height * s));
        return Resize(img, nw, nh);
    }

    /// <summary>Box filter downscale to exactly <paramref name="nw"/> × <paramref name="nh"/>.</summary>
    public static DecodedImage Resize(DecodedImage img, int nw, int nh)
    {
        int ch = img.Channels, w = img.Width, h = img.Height;
        var src = img.Pixels;
        var dst = GC.AllocateUninitializedArray<byte>(nw * nh * ch);
        var x0 = new int[nw + 1];
        for (int x = 0; x <= nw; x++) x0[x] = (int)((long)x * w / nw);
        Parallel.For(0, nh, y =>
        {
            int ya = (int)((long)y * h / nh), yb = Math.Max(ya + 1, (int)((long)(y + 1) * h / nh));
            Span<int> sum = stackalloc int[4];
            for (int x = 0; x < nw; x++)
            {
                int xa = x0[x], xb = Math.Max(xa + 1, x0[x + 1]);
                sum.Clear();
                for (int yy = ya; yy < yb; yy++)
                {
                    int row = (yy * w) * ch;
                    for (int xx = xa; xx < xb; xx++)
                    {
                        int o = row + xx * ch;
                        for (int c = 0; c < ch; c++) sum[c] += src[o + c];
                    }
                }
                int area = (yb - ya) * (xb - xa), d = (y * nw + x) * ch;
                for (int c = 0; c < ch; c++) dst[d + c] = (byte)((sum[c] + area / 2) / area);
            }
        });
        return new DecodedImage(nw, nh, ch, dst);
    }
}
