using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Loupe.App.Bridge;
using Loupe.App.Services;

namespace Loupe.App;

/// <summary>
/// The pop-out viewer: a second window (wwwroot/popup.html) for a second monitor, on the same
/// bridge as the main window. It shows the image focused in the main window with its correction
/// preview, can go full screen (F11) and remembers where it was.
/// </summary>
public partial class PopupWindow : Window
{
    private readonly BridgeHost _bridge;
    private readonly ImageService _images;
    private readonly AppSettings _settings;
    private Action? _detach;
    private bool _fullscreen;
    private Rect _normal;

    public bool IsFullscreen => _fullscreen;
    public CoreWebView2? Core => Web.CoreWebView2;

    public PopupWindow(BridgeHost bridge, ImageService images, AppSettings settings, bool dark, System.Drawing.Color bg)
    {
        InitializeComponent();
        _bridge = bridge;
        _images = images;
        _settings = settings;
        Width = Math.Max(MinWidth, settings.PopupWidth);
        Height = Math.Max(MinHeight, settings.PopupHeight);
        if (settings.PopupLeft is double l && settings.PopupTop is double t && OnScreen(l, t))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = settings.PopupTopmost;

        SourceInitialized += (_, _) => DarkTitleBar.Apply(new WindowInteropHelper(this).Handle, dark, bg);
        Loaded += async (_, _) =>
        {
            Web.DefaultBackgroundColor = bg;
            if (settings.PopupFullscreen) SetFullscreen(true);
            await InitAsync();
        };
        Closing += (_, _) =>
        {
            var r = _fullscreen ? _normal : (WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds);
            _settings.PopupLeft = r.Left;
            _settings.PopupTop = r.Top;
            _settings.PopupWidth = r.Width;
            _settings.PopupHeight = r.Height;
            _settings.PopupFullscreen = _fullscreen;
        };
        Closed += (_, _) => _detach?.Invoke();
    }

    private static bool OnScreen(double left, double top) =>
        left > SystemParameters.VirtualScreenLeft - 50 && top > SystemParameters.VirtualScreenTop - 50 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;

    private async Task InitAsync()
    {
        try
        {
            var env = await WebViewSetup.EnvironmentAsync();
            await Web.EnsureCoreWebView2Async(env);
            _detach = WebViewSetup.Configure(Web.CoreWebView2, _bridge, _images, Dispatcher);
            Web.Source = new Uri($"https://{WebViewSetup.AppHost}/popup.html");
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(this, Loc.T("The viewer window could not start: {error}", ("error", ex.Message)), "Loupe");
            Close();
        }
    }

    /// <summary>Full screen on the monitor the window is on (no frame, covers the taskbar). null = toggle.</summary>
    public bool SetFullscreen(bool? on)
    {
        bool want = on ?? !_fullscreen;
        if (want == _fullscreen) return _fullscreen;
        if (want)
        {
            _normal = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            WindowState = WindowState.Normal;   // maximise again below, so the frameless window takes the whole monitor
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            if (_normal.Width > 0) { Left = _normal.Left; Top = _normal.Top; Width = _normal.Width; Height = _normal.Height; }
        }
        _fullscreen = want;
        _bridge.Emit("popup.fullscreen", new { on = want });
        return want;
    }

    public void ApplyTheme(bool dark, System.Drawing.Color bg)
    {
        DarkTitleBar.Apply(new WindowInteropHelper(this).Handle, dark, bg);
        if (Web.CoreWebView2 is not null) Web.DefaultBackgroundColor = bg;
    }
}
