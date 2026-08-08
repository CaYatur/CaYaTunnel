using System.IO;
using System.Windows;
using System.Windows.Threading;
using CaYaTunnel.Server.App.ViewModels;
using CaYaTunnel.Server.App.Views;
using CaYaTunnel.Server.Configuration;

namespace CaYaTunnel.Server.App;

public partial class App : Application
{
    private ServerShellViewModel? _shell;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);
        DispatcherUnhandledException += OnDispatcherException;

        if (TryCapture(e.Args))
        {
            Shutdown();
            return;
        }

        ServerPaths.EnsureCreated();

        _shell = new ServerShellViewModel();

        var window = new MainWindow { DataContext = _shell };
        MainWindow = window;
        window.Show();

        if (_shell.Config.AutoStartGateway)
        {
            _shell.StartGatewayCommand.Execute(null);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shell?.ShutdownAsync().GetAwaiter().GetResult();
        base.OnExit(e);
    }

    /// <summary>Renders each screen to PNG for the README; see the client app's equivalent.</summary>
    private static bool TryCapture(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, "--capture", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        var directory = index + 1 < args.Length ? args[index + 1] : "screenshots";
        var shell = ServerShellViewModel.CreatePreview();

        ScreenCapture.Save(new MainWindow { DataContext = shell },
            Path.Combine(directory, "server-overview.png"), 1120, 720);

        shell.Page = ServerPage.Devices;
        ScreenCapture.Save(new MainWindow { DataContext = shell },
            Path.Combine(directory, "server-devices.png"), 1120, 720);

        shell.Page = ServerPage.Clients;
        ScreenCapture.Save(new MainWindow { DataContext = shell },
            Path.Combine(directory, "server-clients.png"), 1120, 720);

        shell.Page = ServerPage.Settings;
        ScreenCapture.Save(new MainWindow { DataContext = shell },
            Path.Combine(directory, "server-settings.png"), 1120, 720);

        return true;
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);

        MessageBox.Show(
            $"CaYaTunnel Server hit an unexpected error.\n\n{e.Exception.Message}\n\nDetails were written to:\n{CrashLogPath}",
            "CaYaTunnel Server",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Keep running: a UI failure must not take live tunnels down with it.
        e.Handled = true;
    }

    private static string CrashLogPath => Path.Combine(ServerPaths.LogDirectory, "admin-errors.log");

    private static void WriteCrashLog(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(ServerPaths.LogDirectory);
            File.AppendAllText(
                CrashLogPath,
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write.
        }
    }
}
