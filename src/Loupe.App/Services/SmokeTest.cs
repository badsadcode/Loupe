using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Loupe.App.Bridge;
using Loupe.Core.Imaging;

namespace Loupe.App.Services;

/// <summary>
/// <c>Loupe.exe --smoke [outputDir]</c>: creates a sample archive (two parts of one signature
/// and a folder of pages), lets the page run its self-test (browse, thumbnails, viewer, edit,
/// restore, rename, merge, undo), takes screenshots, writes <c>smoke.json</c> and exits with 0
/// on success. Used by the Windows CI build.
/// </summary>
public sealed class SmokeTest
{
    private readonly List<string> _log = [];
    private bool _failed;

    public string OutputDir { get; }
    public string SampleFolder { get; }

    private SmokeTest(string outputDir)
    {
        OutputDir = outputDir;
        SampleFolder = Path.Combine(outputDir, "sample");
        CreateSample(SampleFolder);
    }

    public static SmokeTest? FromArgs(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--smoke", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        string dir = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : Path.Combine(Environment.CurrentDirectory, "smoke");
        Directory.CreateDirectory(dir);
        return new SmokeTest(Path.GetFullPath(dir));
    }

    /// <summary>Sample files named like Striplight PRO's archive export.</summary>
    public static void CreateSample(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        void Page(string folder, string name, int w, int h, int seed)
        {
            Directory.CreateDirectory(folder);
            var px = new byte[w * h];
            var rnd = new Random(seed);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool line = y > h / 8 && y < h * 7 / 8 && x > w / 8 && x < w * 7 / 8 && (y / 18) % 2 == 0 && rnd.Next(5) > 0;
                    px[y * w + x] = (byte)Math.Clamp((line ? 70 : 150 + seed % 40) + rnd.Next(12) - (x * 30 / w), 0, 255);
                }
            File.WriteAllBytes(Path.Combine(folder, name), TurboJpeg.Encode(px, w, h, w, TurboJpeg.PixelFormatGray, 90, false));
        }
        string sig = Path.Combine(root, "73", "524", "0", "0");
        for (int i = 1; i <= 6; i++) Page(Path.Combine(sig, "80"), $"73_524_0_0_80_{i:00000}.jpg", 960, 1280, i);
        for (int i = 1; i <= 3; i++) Page(Path.Combine(sig, "123_1"), $"73_524_0_0_123_1_{i:00000}.jpg", 800, 1040, 10 + i);
        for (int i = 1; i <= 2; i++) Page(Path.Combine(sig, "123_2"), $"73_524_0_0_123_2_{i:00000}.jpg", 800, 1040, 20 + i);
    }

    private void Log(string s) => _log.Add($"{DateTime.Now:HH:mm:ss.fff} {s}");

    public void Fail(string why)
    {
        _failed = true;
        Log("FAIL " + why);
        Write(new { ok = false, log = _log });
    }

    public async Task RunAsync(Window window, CoreWebView2 core, AppController controller)
    {
        var result = new Dictionary<string, object?>();
        try
        {
            Log("page ready");
            var selfTest = new TaskCompletionSource<JsonElement>();
            controller.OnSelfTestResult = r => selfTest.TrySetResult(r);
            await core.ExecuteScriptAsync("window.__loupeSelfTest && window.__loupeSelfTest()");
            var done = await Task.WhenAny(selfTest.Task, Task.Delay(90000));
            if (done != selfTest.Task) Fail("page self-test timed out");
            else
            {
                var r = selfTest.Task.Result;
                result["selfTest"] = r;
                Log("self-test " + r.GetRawText());
                if (!r.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) Fail("page self-test reported a problem");
            }
            await Task.Delay(500);
            await Capture(core, "01-main");
            await core.ExecuteScriptAsync("window.__loupeShow && window.__loupeShow('viewer')");
            await Task.Delay(1500);
            await Capture(core, "02-viewer");
            // Pop-out viewer: open it, let the main page send the focused image, take a picture.
            if (window is IWindowHost host)
            {
                host.OpenPopup();
                await Task.Delay(3500);
                if (host.Popup?.Core is { } pc) await Capture(pc, "04-popup");
                else Fail("pop-out viewer did not open");
                host.ClosePopup();
                await Task.Delay(400);
            }
            await core.ExecuteScriptAsync("window.__loupeShow && window.__loupeShow('rename')");
            await Task.Delay(800);
            await Capture(core, "03-rename");

            result["jsErrors"] = controller.JsErrors;
            if (controller.JsErrors.Count > 0) Fail("JavaScript errors: " + string.Join(" | ", controller.JsErrors));
        }
        catch (Exception e)
        {
            Fail(e.GetType().Name + ": " + e.Message);
        }

        result["ok"] = !_failed;
        result["log"] = _log;
        Write(result);
        window.Dispatcher.Invoke(() => Application.Current.Shutdown(_failed ? 1 : 0));
    }

    private void Write(object result) =>
        File.WriteAllText(Path.Combine(OutputDir, "smoke.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions(BridgeHost.Json) { WriteIndented = true }));

    private async Task Capture(CoreWebView2 core, string name)
    {
        try
        {
            string png = Path.Combine(OutputDir, name + ".png");
            using (var fs = File.Create(png))
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            var frame = BitmapDecoder.Create(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var small = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(0.5, 0.5));
            var enc = new JpegBitmapEncoder { QualityLevel = 60 };
            enc.Frames.Add(BitmapFrame.Create(small));
            using var ms = new MemoryStream();
            enc.Save(ms);
            File.WriteAllText(Path.Combine(OutputDir, name + ".jpg.b64"), Convert.ToBase64String(ms.ToArray()), Encoding.ASCII);
            Log("captured " + name);
        }
        catch (Exception e) { Log($"capture {name} failed: {e.Message}"); }
    }
}
