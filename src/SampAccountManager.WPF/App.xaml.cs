using System.Windows;
using System.Windows.Threading;

namespace SampAccountManager.WPF;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Eroare neasteptata:\n" + e.Exception.Message, "SA-MP Account Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
