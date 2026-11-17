using Loupe.Core.Editing;
using Loupe.Core.Files;
using Loupe.Core.Imaging;

namespace Loupe.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "loupe-test-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

internal static class Img
{
    /// <summary>A gray test JPEG with a gradient and a bright square (not symmetric, so rotations differ).</summary>
    public static byte[] Gray(int w, int h, int q = 92)
    {
        var px = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = (byte)(x < w / 4 && y < h / 3 ? 240 : 40 + (x * 150 / w));
        return TurboJpeg.Encode(px, w, h, w, TurboJpeg.PixelFormatGray, q, false);
    }

    /// <summary>Adds a JFIF APP0 with 300 dpi in front of the image data.</summary>
    public static byte[] With300Dpi(byte[] jpeg)
    {
        byte[] app0 = [0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 1, 0x01, 0x2C, 0x01, 0x2C, 0, 0];
        var segs = JpegMeta.HeaderSegments(jpeg);
        var ms = new MemoryStream();
        ms.Write(jpeg, 0, 2);
        ms.Write(app0);
        int pos = 2;
        foreach (var s in segs.Where(s => s.Marker == 0xE0)) { ms.Write(jpeg, pos, s.Offset - pos); pos = s.Offset + s.Length; }
        ms.Write(jpeg, pos, jpeg.Length - pos);
        return ms.ToArray();
    }
}

public class ImagingTests
{
    [Theory]
    [InlineData(50)]
    [InlineData(85)]
    [InlineData(95)]
    [InlineData(100)]
    public void QualityIsEstimatedFromTheQuantisationTable(int q) =>
        Assert.InRange(JpegMeta.EstimateQuality(Img.Gray(64, 48, q)), q - 1, q + 1);

    [Fact]
    public void ReEncodingKeepsDpiAndUsesTheOriginalQuality()
    {
        var original = Img.With300Dpi(Img.Gray(64, 48, 90));
        Assert.Equal((300, 300), JpegMeta.Density(original));
        var (bytes, lossless, q) = ImageEditor.Render(original, new EditSettings { Contrast = 20 }, new SaveOptions());
        Assert.False(lossless);
        Assert.InRange(q, 89, 91);
        Assert.Equal((300, 300), JpegMeta.Density(bytes));
        Assert.InRange(JpegMeta.EstimateQuality(bytes), 89, 91);
        Assert.Equal((64, 48, true), TurboJpeg.ReadHeader(bytes));
    }

    [Theory]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(270, true)]
    public void LosslessTransformMatchesThePixelPath(int rotation, bool mirror)
    {
        var src = Img.Gray(64, 48);   // multiples of 8: perfect transform possible
        var lossless = TurboJpeg.TransformLossless(src, TurboJpeg.TransformOp(rotation, mirror));
        Assert.NotNull(lossless);
        var a = TurboJpeg.DecodeNative(lossless!);
        var b = ImageEdit.Apply(TurboJpeg.DecodeNative(src), new EditSettings { Rotation = rotation, Mirror = mirror });
        Assert.Equal((b.Width, b.Height), (a.Width, a.Height));
        double diff = 0;
        for (int i = 0; i < a.Pixels.Length; i++) diff += Math.Abs(a.Pixels[i] - b.Pixels[i]);
        Assert.True(diff / a.Pixels.Length < 3, $"mean difference {diff / a.Pixels.Length}");
    }

    [Fact]
    public void OddSizesFallBackToReEncoding()
    {
        var src = Img.Gray(61, 45);
        var (bytes, lossless, _) = ImageEditor.Render(src, new EditSettings { Rotation = 90 }, new SaveOptions());
        Assert.False(lossless);
        Assert.Equal((45, 61, true), TurboJpeg.ReadHeader(bytes));
    }

    [Fact]
    public void ScaledDecodeCoversTheRequestedSize()
    {
        var src = Img.Gray(800, 400);
        var d = TurboJpeg.DecodeAtLeast(src, 150);
        Assert.Equal(200, d.Width);   // 2/8 is the smallest scale with a long side >= 150
        var t = ImageEdit.Fit(d, 150, 150);
        Assert.Equal((150, 75), (t.Width, t.Height));
    }

    [Fact]
    public void ColourImagesKeepTheirChannels()
    {
        int w = 32, h = 16;
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = 200; px[i * 4 + 1] = 100; px[i * 4 + 2] = 50; px[i * 4 + 3] = 255; }
        var jpg = TurboJpeg.Encode(px, w, h, w * 4, TurboJpeg.PixelFormatBgra, 95, false);
        var img = ImageEdit.Apply(TurboJpeg.DecodeNative(jpg), new EditSettings { Method = ToneMethod.AutoLevels, Rotation = 90 });
        Assert.Equal((16, 32, 4), (img.Width, img.Height, img.Channels));
        Assert.True(img.Pixels[0] > img.Pixels[2], "blue stays stronger than red");
    }
}

public class EditAndBackupTests
{
    [Fact]
    public void EditKeepsTheFirstOriginalAndRestoresIt()
    {
        using var t = new TempDir();
        string file = t.File("roll", "a.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var original = Img.Gray(64, 48);
        File.WriteAllBytes(file, original);
        var editor = new ImageEditor(new BackupStore(t.File("backups")));

        Assert.True(editor.Apply(file, new EditSettings { Brightness = 30 }, new SaveOptions()).Ok);
        Assert.True(editor.Apply(file, new EditSettings { Rotation = 90 }, new SaveOptions()).Ok);
        Assert.True(editor.Backups.Has(file));
        Assert.Equal((48, 64, true), TurboJpeg.ReadHeader(File.ReadAllBytes(file)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.loupe-tmp"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!));   // the archive folder stays clean

        Assert.True(editor.Backups.Restore(file));
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.False(editor.Backups.Has(file));
        Assert.False(Directory.Exists(t.File("backups")) && Directory.EnumerateFiles(t.File("backups"), "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void BackupPathsMirrorTheFullPath()
    {
        var b = new BackupStore(@"C:\B");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"C:\B\E\roll\a.jpg", b.PathFor(@"E:\roll\a.jpg"));
            Assert.Equal(@"C:\B\UNC\srv\share\a.jpg", b.PathFor(@"\\srv\share\a.jpg"));
        }
        else Assert.EndsWith(Path.Combine("roll", "a.jpg"), b.PathFor("/mnt/roll/a.jpg"));
    }

    [Fact]
    public void ThumbnailsAreCachedAndFollowEdits()
    {
        using var t = new TempDir();
        string file = t.File("a.jpg");
        File.WriteAllBytes(file, Img.Gray(400, 200));
        var r = new ImageRenderer(t.File("thumbs"));
        var a = r.Thumbnail(file, 128);
        Assert.Equal((128, 64, true), TurboJpeg.ReadHeader(a));
        Assert.Equal(1, r.Usage().Files);
        File.WriteAllBytes(file, Img.Gray(200, 400));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal((64, 128, true), TurboJpeg.ReadHeader(r.Thumbnail(file, 128)));
    }
}

public class RenameTests
{
    private static List<string> Make(TempDir t, string folder, params string[] names)
    {
        Directory.CreateDirectory(t.File(folder));
        return names.Select(n => { string p = t.File(folder, n); File.WriteAllText(p, n); return p; }).ToList();
    }

    [Fact]
    public void PatternCounterPerFolder()
    {
        using var t = new TempDir();
        var files = Make(t, "80", "x.JPG", "y.jpg").Concat(Make(t, "81", "z.jpg")).ToList();
        var plan = RenamePlanner.Plan(files, new RenameRules { Pattern = "{folder}_{n}" });
        Assert.Equal(["80_00001.jpg", "80_00002.jpg", "81_00001.jpg"], plan.Items.Select(i => i.NewName));
        Assert.True(plan.CanRun);
    }

    [Fact]
    public void ShiftingNumbersAndSwapsNeverOverwrite()
    {
        using var t = new TempDir();
        var files = Make(t, "f", "p_00001.jpg", "p_00002.jpg", "p_00003.jpg");
        var plan = RenamePlanner.Plan(files, new RenameRules { Pattern = "p_{n}", Start = 2 });
        Assert.True(plan.CanRun);
        SafeMover.Run(RenamePlanner.Moves(plan));
        Assert.Equal("p_00001.jpg", File.ReadAllText(t.File("f", "p_00002.jpg")));
        Assert.Equal("p_00003.jpg", File.ReadAllText(t.File("f", "p_00004.jpg")));
        Assert.False(File.Exists(t.File("f", "p_00001.jpg")));
    }

    [Fact]
    public void ConflictsAndExistingFilesBlockTheRename()
    {
        using var t = new TempDir();
        var files = Make(t, "f", "a.jpg", "b.jpg");
        File.WriteAllText(t.File("f", "taken.jpg"), "x");
        var same = RenamePlanner.Plan(files, new RenameRules { Pattern = "same" });
        Assert.All(same.Items, i => Assert.Equal("conflict", i.Status));
        var exists = RenamePlanner.Plan([files[0]], new RenameRules { Pattern = "taken" });
        Assert.Equal("exists", exists.Items[0].Status);
        Assert.False(exists.CanRun);
        var bad = RenamePlanner.Plan([files[0]], new RenameRules { Pattern = "a:b" });
        Assert.Equal("invalid", bad.Items[0].Status);
    }

    [Fact]
    public void FindReplaceRegexAndUndo()
    {
        using var t = new TempDir();
        var files = Make(t, "f", "scan 001.jpg", "scan 002.jpg");
        var plan = RenamePlanner.Plan(files, new RenameRules { Find = @"scan (\d+)", Replace = "page_$1", Regex = true });
        Assert.Equal(["page_001.jpg", "page_002.jpg"], plan.Items.Select(i => i.NewName));
        var moves = RenamePlanner.Moves(plan);
        SafeMover.Run(moves);
        SafeMover.Undo(new UndoLog { Moves = moves });
        Assert.True(File.Exists(t.File("f", "scan 001.jpg")));
        Assert.False(File.Exists(t.File("f", "page_001.jpg")));
    }
}

public class MergeTests
{
    private static string Part(TempDir t, string name, string prefix, int count)
    {
        string dir = t.File("73", name);
        Directory.CreateDirectory(dir);
        for (int i = 1; i <= count; i++) File.WriteAllText(Path.Combine(dir, $"{prefix}{i:00000}.jpg"), $"{name}:{i}");
        return dir;
    }

    [Fact]
    public void PartsAreMergedIntoANewFolderWithContinuousNumbers()
    {
        using var t = new TempDir();
        var p2 = Part(t, "123_2", "73_524_0_0_123_2_", 2);
        var p1 = Part(t, "123_1", "73_524_0_0_123_1_", 3);
        var plan = PartMerger.Plan([p2, p1]);   // order of selection does not matter
        Assert.True(plan.CanRun, plan.Error ?? string.Join("; ", plan.Problems));
        Assert.Equal(t.File("73", "123"), plan.Target);
        Assert.Equal(
            ["73_524_0_0_123_00001.jpg", "73_524_0_0_123_00002.jpg", "73_524_0_0_123_00003.jpg", "73_524_0_0_123_00004.jpg", "73_524_0_0_123_00005.jpg"],
            plan.Moves.Select(m => Path.GetFileName(m.To)));

        var log = PartMerger.Run(plan, deleteEmptyParts: true);
        Assert.Equal("123_1:3", File.ReadAllText(t.File("73", "123", "73_524_0_0_123_00003.jpg")));
        Assert.Equal("123_2:1", File.ReadAllText(t.File("73", "123", "73_524_0_0_123_00004.jpg")));
        Assert.False(Directory.Exists(p1));
        Assert.False(Directory.Exists(p2));

        SafeMover.Undo(log);
        Assert.Equal("123_2:2", File.ReadAllText(Path.Combine(p2, "73_524_0_0_123_2_00002.jpg")));
        Assert.False(Directory.Exists(plan.Target));
    }

    [Fact]
    public void QuantumNamesKeepTheirPrefix()
    {
        using var t = new TempDir();
        var plan = PartMerger.Plan([Part(t, "9_1", "page_", 2), Part(t, "9_2", "page_", 1)]);
        Assert.Equal(["page_00001.jpg", "page_00002.jpg", "page_00003.jpg"], plan.Moves.Select(m => Path.GetFileName(m.To)));
    }

    [Fact]
    public void ExistingTargetOrMixedSignaturesAreRefused()
    {
        using var t = new TempDir();
        var a = Part(t, "5_1", "x_", 1);
        var b = Part(t, "5_2", "x_", 1);
        Directory.CreateDirectory(t.File("73", "5"));
        File.WriteAllText(t.File("73", "5", "keep.jpg"), "!");
        Assert.False(PartMerger.Plan([a, b]).CanRun);
        Assert.NotNull(PartMerger.Plan([a, Part(t, "6_2", "x_", 1)]).Error);
        Assert.NotNull(PartMerger.Plan([a]).Error);
    }

    [Theory]
    [InlineData("73_524_0_0_123_1_", "123_1", "123", "73_524_0_0_123_")]
    [InlineData("123_1_", "123_1", "123", "123_")]
    [InlineData("page_", "123_1", "123", "page_")]
    [InlineData("x1123_1_", "123_1", "123", "x1123_1_")]
    public void PrefixReplacement(string prefix, string part, string baseName, string expected) =>
        Assert.Equal(expected, PartMerger.ReplacePartInPrefix(prefix, part, baseName));
}

public class RescanTests
{
    [Fact]
    public void MicrofilmFillsTheFolderAndEntriesFollowRenames()
    {
        using var t = new TempDir();
        var list = new RescanList(t.File("rescan.json"));
        string a = Path.Combine("D:", "QC", "73", "80", "a.jpg"), b = Path.Combine("D:", "QC", "73", "80", "b.jpg"), c = Path.Combine("D:", "QC", "73", "81", "c.jpg");
        Assert.Equal(3, list.Set([a, b, c], true));
        list.Update(a, "Out of focus", "1234");
        Assert.Equal("1234", list.Entries.Single(e => e.Path == b).Microfilm);   // same folder
        Assert.Equal("", list.Entries.Single(e => e.Path == c).Microfilm);       // other folder
        string d = Path.Combine("D:", "QC", "73", "80", "d.jpg");
        list.Set([d], true);
        Assert.Equal("1234", list.Entries.Single(e => e.Path == d).Microfilm);   // new entry takes the folder's number

        list.Follow([new MovePair(a, a.Replace("a.jpg", "z.jpg"))]);
        var again = new RescanList(t.File("rescan.json"));                       // saved and reloaded
        Assert.Contains(again.Entries, e => e.Path.EndsWith("z.jpg") && e.Reason == "Out of focus");
        Assert.Equal(1, again.Set([b], false));
        Assert.Equal(3, again.Entries.Count);
    }

    [Fact]
    public void FolderIsRelativeToTheLongestRoot()
    {
        string sep = Path.DirectorySeparatorChar.ToString();
        string file = string.Join(sep, "", "mnt", "d", "QC", "73", "524", "0", "0", "80", "x.jpg");
        string drive = string.Join(sep, "", "mnt", "d"), pinned = string.Join(sep, "", "mnt", "d", "QC");
        Assert.Equal(string.Join(sep, "73", "524", "0", "0", "80"), RescanList.RelativeFolder(file, [drive, pinned]));
        Assert.Equal(string.Join(sep, "QC", "73", "524", "0", "0", "80"), RescanList.RelativeFolder(file, [drive]));
    }

    [Fact]
    public void CsvAndTsv()
    {
        var rows = new List<string[]> { new[] { "1234", "73\\80", "a;b.jpg", "Too \"dark\"", "2026-11-17 10:00", "D:\\x" } };
        string csv = System.Text.Encoding.UTF8.GetString(RescanList.ToCsv(rows, ["M", "F", "N", "R", "D", "P"]));
        Assert.Contains("\"a;b.jpg\";\"Too \"\"dark\"\"\"", csv);
        Assert.Equal("1234\t73\\80\ta;b.jpg\tToo \"dark\"\t2026-11-17 10:00\tD:\\x", RescanList.ToTsv(rows, null));
    }
}

public class CropAndSliceTests
{
    [Fact]
    public void CropIsAppliedAfterOrientation()
    {
        var img = TurboJpeg.DecodeNative(Img.Gray(64, 48));
        var r = ImageEdit.Apply(img, new EditSettings { Rotation = 90, CropLeft = 0.25, CropRight = 0.25 });
        Assert.Equal((24, 64), (r.Width, r.Height));   // 48 wide after rotation, half kept
        var (bytes, lossless, _) = ImageEditor.Render(Img.Gray(64, 48), new EditSettings { CropTop = 0.5 }, new SaveOptions());
        Assert.False(lossless);
        Assert.Equal((64, 24, true), TurboJpeg.ReadHeader(bytes));
    }

    private static void Folder(TempDir t, int n, int w = 200, int h = 100)
    {
        Directory.CreateDirectory(t.File("80"));
        for (int i = 1; i <= n; i++) File.WriteAllBytes(t.File("80", $"73_80_{i:00000}.jpg"), Img.Gray(w, h));
    }

    [Fact]
    public void SlicingRenumbersTheFolderAndCanBeUndone()
    {
        using var t = new TempDir();
        Folder(t, 3);
        string marker = t.File("80", "73_80_00003.jpg");
        File.WriteAllBytes(marker, Img.Gray(120, 100));          // recognisable by its width
        var s = new SliceSettings { Parts = 2 };
        var plan = Slicer.Plan([t.File("80", "73_80_00001.jpg")], s);
        Assert.True(plan.CanRun, plan.Error ?? string.Join(";", plan.Problems));
        Assert.Equal(["73_80_00001.jpg", "73_80_00002.jpg"], plan.Outputs.Select(o => Path.GetFileName(o.Target)));
        Assert.Equal(["73_80_00002.jpg→73_80_00003.jpg", "73_80_00003.jpg→73_80_00004.jpg"],
            plan.Renames.Select(m => Path.GetFileName(m.From) + "→" + Path.GetFileName(m.To)));

        var log = Slicer.Run(plan, s, t.File("keep"));
        var names = Directory.GetFiles(t.File("80")).Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(["73_80_00001.jpg", "73_80_00002.jpg", "73_80_00003.jpg", "73_80_00004.jpg"], names);
        Assert.Equal((100, 100, true), TurboJpeg.ReadHeader(File.ReadAllBytes(t.File("80", "73_80_00001.jpg"))));
        Assert.Equal(120, TurboJpeg.ReadHeader(File.ReadAllBytes(t.File("80", "73_80_00004.jpg"))).Width);

        SafeMover.Undo(log);
        names = Directory.GetFiles(t.File("80")).Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(["73_80_00001.jpg", "73_80_00002.jpg", "73_80_00003.jpg"], names);
        Assert.Equal(200, TurboJpeg.ReadHeader(File.ReadAllBytes(t.File("80", "73_80_00001.jpg"))).Width);
        Assert.Equal(120, TurboJpeg.ReadHeader(File.ReadAllBytes(marker)).Width);
    }

    [Fact]
    public void ReverseOrderAndUnevenCuts()
    {
        var r = Slicer.Ranges(1000, new SliceSettings { Parts = 2, Cuts = [0.4], Reverse = true });
        Assert.Equal([(400, 1000), (0, 400)], r);
        Assert.Equal([(0, 333), (333, 667), (667, 1000)], Slicer.Ranges(1000, new SliceSettings { Parts = 3 }));
    }

    [Fact]
    public void FoldersWithMixedNamesAreRefused()
    {
        using var t = new TempDir();
        Folder(t, 2);
        File.WriteAllBytes(t.File("80", "cover.jpg"), Img.Gray(64, 48));
        Assert.NotNull(Slicer.Plan([t.File("80", "73_80_00001.jpg")], new SliceSettings()).Error);
    }

    [Fact]
    public void NumberWidthGrowsWhenNeeded()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.File("f"));
        for (int i = 1; i <= 9; i++) File.WriteAllBytes(t.File("f", $"p_{i}.jpg"), Img.Gray(64, 48));
        var plan = Slicer.Plan([t.File("f", "p_9.jpg")], new SliceSettings());
        Assert.Equal(["p_09.jpg", "p_10.jpg"], plan.Outputs.Select(o => Path.GetFileName(o.Target)));
        Assert.Contains(plan.Renames, m => Path.GetFileName(m.From) == "p_1.jpg" && Path.GetFileName(m.To) == "p_01.jpg");
    }
}

public class ReverseRenameTests
{
    [Fact]
    public void ReverseOrderSwapsTheNumbersSafely()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.File("f"));
        var files = Enumerable.Range(1, 3).Select(i => { string p = t.File("f", $"p_{i:00000}.jpg"); File.WriteAllText(p, $"was {i}"); return p; }).ToList();
        var plan = RenamePlanner.Plan(files, new RenameRules { Pattern = "p_{n}", Reverse = true });
        Assert.Equal(["p_00003.jpg", "p_00002.jpg", "p_00001.jpg"], plan.Items.Select(i => i.NewName));
        Assert.True(plan.CanRun);
        SafeMover.Run(RenamePlanner.Moves(plan));
        Assert.Equal("was 3", File.ReadAllText(t.File("f", "p_00001.jpg")));
        Assert.Equal("was 1", File.ReadAllText(t.File("f", "p_00003.jpg")));
        Assert.Equal("was 2", File.ReadAllText(t.File("f", "p_00002.jpg")));
    }
}

public class StructureTests
{
    [Fact]
    public void NamesFromTheFolderStructure()
    {
        using var t = new TempDir();
        string dir = t.File("delivery", "73", "524", "0", "0", "80");
        Directory.CreateDirectory(dir);
        foreach (var n in new[] { "a.jpg", "b.jpg" }) File.WriteAllText(Path.Combine(dir, n), n);
        var files = FolderSorter.CollectImages([t.File("delivery")], recursive: true);
        Assert.Equal(2, files.Count);
        var plan = RenamePlanner.Plan(files, new RenameRules { Pattern = "{path}_{n}", Root = t.File("delivery") });
        Assert.Equal(["73_524_0_0_80_00001.jpg", "73_524_0_0_80_00002.jpg"], plan.Items.Select(i => i.NewName));
        var last3 = RenamePlanner.Plan(files, new RenameRules { Pattern = "{path:3}-{n:3}", PathSeparator = "." });
        Assert.Equal("0.0.80-001.jpg", last3.Items[0].NewName);
    }

    [Fact]
    public void FoldersFromTheFileNamesAndUndo()
    {
        using var t = new TempDir();
        string flat = t.File("flat");
        Directory.CreateDirectory(flat);
        foreach (var n in new[] { "73_524_0_0_80_00001.jpg", "73_524_0_0_80_00002.jpg", "73_524_0_0_81_00001.jpg" })
            File.WriteAllText(Path.Combine(flat, n), n);
        var files = Directory.GetFiles(flat).OrderBy(x => x).ToList();
        var plan = FolderSorter.Plan(files, flat, new SortRules());
        Assert.True(plan.CanRun, string.Join(";", plan.Problems));
        var log = FolderSorter.Run(plan);
        Assert.True(File.Exists(Path.Combine(flat, "73", "524", "0", "0", "80", "73_524_0_0_80_00002.jpg")));
        Assert.True(File.Exists(Path.Combine(flat, "73", "524", "0", "0", "81", "73_524_0_0_81_00001.jpg")));

        SafeMover.Undo(log);
        Assert.Equal(3, Directory.GetFiles(flat).Length);
        Assert.Empty(Directory.GetDirectories(flat));     // created folders are gone again

        var counterOnly = FolderSorter.Plan(files, flat, new SortRules { KeepName = false });
        Assert.Contains(counterOnly.Moves, m => m.To.EndsWith(Path.Combine("80", "00001.jpg")));
        var bad = FolderSorter.Plan([Path.Combine(flat, "73_524_0_0_80_00001.jpg")], flat, new SortRules { CounterParts = 6 });
        Assert.NotEmpty(bad.Problems);
    }
}
