using System.IO;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Loupe.App.Bridge;

namespace Loupe.App.Services;

/// <summary>WebView2 setup: the app/image virtual hosts and the JSON bridge.</summary>
public static class WebViewSetup
{
    public const string AppHost = "app.loupe";
    public const string ImageHost = "img.loupe";

    private static Task<CoreWebView2Environment>? _env;

    public static Task<CoreWebView2Environment> EnvironmentAsync()
    {
        string userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loupe", "WebView2");
        return _env ??= CoreWebView2Environment.CreateAsync(null, userData);
    }

    public static void Configure(CoreWebView2 core, BridgeHost bridge, ImageService images, Dispatcher dispatcher)
    {
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false; // F5/Ctrl+R would reload the page
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
#endif
        string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping(AppHost, wwwroot, CoreWebView2HostResourceAccessKind.Deny);

        core.AddWebResourceRequestedFilter($"https://{ImageHost}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => images.Handle(core.Environment, e);

        core.WebMessageReceived += async (_, e) =>
        {
            string reply = await bridge.HandleAsync(e.WebMessageAsJson);
            core.PostWebMessageAsJson(reply);
        };
        bridge.Outgoing += json => dispatcher.InvokeAsync(() => core.PostWebMessageAsJson(json));

        core.NewWindowRequested += (_, e) => { e.Handled = true; Shell.OpenUrl(e.Uri); };
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{AppHost}/", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                Shell.OpenUrl(e.Uri);
            }
        };
    }
}
