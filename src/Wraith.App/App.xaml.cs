using System.Windows;

namespace Wraith.App;

public partial class App : Application
{
    public static AppController Controller { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) =>
        {
            try { Controller?.Logs.App.Error("Unhandled UI error", a.Exception); } catch { }
            MessageBox.Show(a.Exception.Message, "Wraith", MessageBoxButton.OK, MessageBoxImage.Error);
            a.Handled = true;
        };
        Controller = new AppController();
        new MainWindow(Controller).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Controller?.Dispose();
        base.OnExit(e);
    }
}
