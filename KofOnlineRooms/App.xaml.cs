using System.Windows;

namespace KofOnlineRooms;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var mode = e.Args.FirstOrDefault()?.ToLowerInvariant() == "create" ? "create" : "join";
        new MainWindow(mode).Show();
    }
}
