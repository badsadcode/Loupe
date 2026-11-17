using System.Windows;
using System.Windows.Threading;

namespace Loupe.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app alive on unexpected UI-thread errors.
        MessageBox.Show(e.Exception.Message, Services.Loc.T("Loupe — unexpected error"),
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
