using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Loupe.App.Bridge;
using Loupe.Core.Editing;
using Loupe.Core.Imaging;

namespace Loupe.App.Services;

/// <summary>
/// Serves images to the page from https://img.loupe/:
///   /thumb?p={path}&amp;s={size}&amp;v={mtime}         — cached thumbnail
///   /view?p={path}&amp;max={px}&amp;e={edit json}&amp;v={mtime} — screen view with the edit applied (live preview);
///                                                  max=0 means full size (1:1)
/// The decoded, unedited view of the last few images is kept in memory, so moving a slider
/// only re-applies the edit instead of reading and decoding the file again.
/// </summary>
public sealed class ImageService(AppController controller)
{
    // Thumbnails are I/O-bound (external HDDs, network shares): a few at a time.
    private readonly SemaphoreSlim _thumbGate = new(4);
    private readonly SemaphoreSlim _viewGate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
    private readonly ConcurrentDictionary<string, byte[]> _cache = new();
    private readonly ConcurrentQueue<string> _cacheOrder = new();
    private const int CacheItems = 48;
    private readonly object _decodedLock = new();
    private readonly LinkedList<(string Key, DecodedImage Image)> _decoded = new();
    private const int DecodedItems = 4;

    public async void Handle(CoreWebView2Environment env, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var uri = new Uri(e.Request.Uri);
            var q = HttpUtility.ParseQueryString(uri.Query);
            string path = q["p"] ?? "";
            string kind = uri.AbsolutePath.Trim('/');
            if (path.Length == 0 || kind is not ("thumb" or "view"))
            {
                e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            string key = uri.PathAndQuery;
            byte[] bytes;
            if (_cache.TryGetValue(key, out var hit)) bytes = hit;
            else
            {
                var renderer = controller.Renderer;
                if (kind == "thumb")
                {
                    int size = Math.Clamp(int.TryParse(q["s"], out int s) ? s : 256, 64, 1024);
                    bytes = await Gated(_thumbGate, () => renderer.Thumbnail(path, size));
                }
                else
                {
                    int max = Math.Clamp(int.TryParse(q["max"], out int m) ? m : 2400, 0, 20000);
                    var edit = ParseEdit(q["e"]);
                    bytes = await Gated(_viewGate, () => RenderView(path, max, edit));
                }
                Remember(key, bytes);
            }
            e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK",
                "Content-Type: image/jpeg\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: https://app.loupe");
        }
        catch (Exception ex) when (ex is IOException or TurboJpegException or InvalidOperationException
                                       or UriFormatException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            e.Response = env.CreateWebResourceResponse(null, 500, "Error", "X-Loupe-Error: " + ex.Message.Replace('\r', ' ').Replace('\n', ' '));
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static EditSettings? ParseEdit(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<EditSettings>(json, BridgeHost.Json);

    private static async Task<byte[]> Gated(SemaphoreSlim gate, Func<byte[]> work)
    {
        await gate.WaitAsync();
        try { return await Task.Run(work); }
        finally { gate.Release(); }
    }

    private byte[] RenderView(string path, int max, EditSettings? edit)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("File not found.", path);
        string dkey = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{max}";
        DecodedImage? img = null;
        lock (_decodedLock)
        {
            for (var n = _decoded.First; n is not null; n = n.Next)
                if (n.Value.Key == dkey) { img = n.Value.Image; _decoded.Remove(n); _decoded.AddFirst(n); break; }
        }
        if (img is null)
        {
            var decoded = TurboJpeg.DecodeAtLeast(File.ReadAllBytes(path), max);
            img = max > 0 ? ImageEdit.Fit(decoded, max, max) : decoded;
            lock (_decodedLock)
            {
                _decoded.AddFirst((dkey, img));
                while (_decoded.Count > DecodedItems) _decoded.RemoveLast();
            }
        }
        var outImg = edit is null || edit.IsNoOp
            ? img
            : ImageEdit.Apply(new DecodedImage(img.Width, img.Height, img.Channels, (byte[])img.Pixels.Clone()), edit);
        return TurboJpeg.Encode(outImg, max == 0 ? 95 : 90);
    }

    private void Remember(string key, byte[] bytes)
    {
        if (!_cache.TryAdd(key, bytes)) return;
        _cacheOrder.Enqueue(key);
        while (_cacheOrder.Count > CacheItems && _cacheOrder.TryDequeue(out var old)) _cache.TryRemove(old, out _);
    }

    /// <summary>Drops cached views of a file after it was changed on disk.</summary>
    public void Forget() => _cache.Clear();
}
