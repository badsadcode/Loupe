using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Loupe.App.Bridge;
using Loupe.App.Services;

namespace Loupe.App;

/// <summary>What the controller needs from the window layer.</summary>
public interface IWindowHost
{
    /// <summary>Colours the native title bar like the page (any theme: dark flag + background colour).</summary>
    void ApplyTheme(bool dark, System.Drawing.Color background);
    Window Window { get; }
    /// <summary>Opens the pop-out viewer (second monitor), or brings it to the front.</summary>
    void OpenPopup();
    void ClosePopup();
    PopupWindow? Popup { get; }
}

/// <summary>
/// Hosts the WebView2 page. Three channels connect the page to .NET:
///  * https://app.loupe/  — static files from wwwroot (virtual host mapping)
///  * https://img.loupe/  — thumbnails and views rendered by Loupe.Core
///  * chrome.webview.postMessage — JSON commands/replies/events (Bridge/BridgeHost.cs)
/// </summary>
public partial class MainWindow : Window, IWindowHost
{
    private readonly BridgeHost _bridge = new();
    private readonly AppController _controller;
    private readonly ImageService _images;
    private readonly SmokeTest? _smoke;
    private System.Drawing.Color _bg = System.Drawing.Color.FromArgb(255, 0x0E, 0x0F, 0x12);
    private PopupWindow? _popup;
    private bool _dark = true;

    public Window Window => this;

    public MainWindow()
    {
        InitializeComponent();
        _smoke = SmokeTest.FromArgs(Environment.GetCommandLineArgs());
        _controller = new AppController(this, _bridge) { Smoke = _smoke };
        _images = new ImageService(_controller);
        SourceInitialized += (_, _) => DarkTitleBar.Apply(new WindowInteropHelper(this).Handle, _dark, _bg);
        Loaded += async (_, _) => await InitWebViewAsync();
        Closing += OnClosing;
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var env = await WebViewSetup.EnvironmentAsync();
            await Web.EnsureCoreWebView2Async(env);
            Web.DefaultBackgroundColor = _bg;
            WebViewSetup.Configure(Web.CoreWebView2, _bridge, _images, Dispatcher);

            if (_smoke is not null)
            {
                var core = Web.CoreWebView2;
                _bridge.On("app.ready", _ =>
                {
                    Dispatcher.InvokeAsync(() => _smoke.RunAsync(this, core, _controller));
                    return null;
                });
            }
            Web.Source = new Uri($"https://{WebViewSetup.AppHost}/index.html");
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            if (_smoke is not null) { _smoke.Fail("WebView2 failed to start: " + ex.Message); Application.Current.Shutdown(2); return; }
            StartupError.Visibility = Visibility.Visible;
            StartupErrorText.Text =
                "The Microsoft Edge WebView2 Runtime is required. Install the Evergreen runtime from " +
                "https://developer.microsoft.com/microsoft-edge/webview2/ and start Loupe again.\n\n" + ex.Message;
        }
    }

    public void ApplyTheme(bool dark, System.Drawing.Color background)
    {
        _dark = dark;
        _bg = background;
        DarkTitleBar.Apply(new WindowInteropHelper(this).Handle, dark, background);
        if (Web.CoreWebView2 is not null) Web.DefaultBackgroundColor = background;
        _popup?.ApplyTheme(dark, background);
    }

    public PopupWindow? Popup => _popup;

    public void OpenPopup()
    {
        if (_popup is not null)
        {
            if (_popup.WindowState == WindowState.Minimized) _popup.WindowState = WindowState.Normal;
            _popup.Activate();
            return;
        }
        _popup = new PopupWindow(_bridge, _images, _controller.Settings, _dark, _bg);
        _popup.Closed += (_, _) =>
        {
            _popup = null;
            _bridge.Emit("popup.state", new { open = false });
            _controller.Settings.Save();
        };
        _popup.Show();
        _bridge.Emit("popup.state", new { open = true });
    }

    public void ClosePopup() => _popup?.Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_smoke is null && !_controller.ConfirmClose()) { e.Cancel = true; return; }
        _popup?.Close();
        _controller.Shutdown();
    }
}

/// <summary>Native title bar coloured to match the page theme (Windows 10 20H1+ dark mode, Windows 11 caption colour).</summary>
internal static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    public static void Apply(IntPtr hwnd, bool dark, System.Drawing.Color bg)
    {
        if (hwnd == IntPtr.Zero) return;
        // Each call fails harmlessly on Windows versions that do not know the attribute. COLORREF = 0x00BBGGRR.
        int mode = dark ? 1 : 0, round = 2;
        int caption = bg.R | (bg.G << 8) | (bg.B << 16);
        int border = caption;
        int text = dark ? 0x00ECE8E6 : 0x001D1815;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref mode, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }
}
