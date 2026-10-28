namespace Poker.Advisor;

public partial class App : System.Windows.Application
{
    private TrayIconService? _trayIcon;

    internal static App CurrentApp => (App)Current;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        _trayIcon = new TrayIconService();
    }

    internal void UpdateTrayStatus(string status) => _trayIcon?.SetStatus(status);

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        base.OnExit(e);
    }
}
