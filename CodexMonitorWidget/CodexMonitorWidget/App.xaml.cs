using System.Windows;
using CodexMonitorWidget.Services;
using CodexMonitorWidget.Tray;

namespace CodexMonitorWidget;

public partial class App : System.Windows.Application
{
    public static CodexUsageStore Store { get; private set; } = null!;

    private TrayIconService? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Store = new CodexUsageStore(Dispatcher);
        _trayIcon = new TrayIconService(Store);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        Store.Shutdown();
        base.OnExit(e);
    }
}
