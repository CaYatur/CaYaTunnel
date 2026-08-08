using System.IO;
using System.Windows;
using System.Windows.Threading;
using CaYaTunnel.Server.App.ViewModels;
using CaYaTunnel.Server.App.Views;
using CaYaTunnel.Server.Configuration;
using CaYaTunnel.Ui;

namespace CaYaTunnel.Server.App;

public partial class App : Application
{
    private ServerShellViewModel? _shell;
    private SingleInstance? _instance;
    private TrayPresence? _tray;

    /// <summary>True once the user chose Exit, so the window stops bouncing back to the tray.</summary>
    public static bool IsExiting => (Current as App)?._tray?.IsExiting ?? false;

    /// <summary>Shown the first time the window is closed, so the gateway is not assumed gone.</summary>
    public static void AnnounceStillRunning()
    {
        if (Current is App { _tray: { } tray })
        {
            tray.Notify(Loc.Get("StillRunningTitle"), Loc.Get("StillRunningBodyServer"),
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

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

        // Keyed on the data directory, which is what a second gateway would collide over: the
        // same registry, the same certificates, and the same public ports.
        if (!SingleInstance.TryClaim(ServerPaths.DataDirectory, out _instance))
        {
            Shutdown();
            return;
        }

        _shell = new ServerShellViewModel();

        var window = new MainWindow { DataContext = _shell };
        MainWindow = window;
        window.Show();

        // The gateway keeps serving with the window closed, so it needs somewhere to live and a
        // way back — without this, closing the window left a process nobody could reach.
        _tray = new TrayPresence(window, Loc.Get("AppServer"), () => _shell.IsRunning);
        _shell.StateChanged += () => _tray.Refresh(
            _shell.IsRunning
                ? $"{Loc.Get("GatewayRunning")} · {_shell.TunnelCount} {Loc.Get("TunnelCount")}"
                : Loc.Get("GatewayStopped"));

        _instance!.SecondInstanceAttempted += () => _tray.Show();

        if (_shell.Config.AutoStartGateway)
        {
            _shell.StartGatewayCommand.Execute(null);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();

        if (_shell is { } shell)
        {
            ShutdownGuard.Run(shell.ShutdownAsync);
        }

        _instance?.Dispose();
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

        // One window, navigated between pages — see the client app for why this matters.
        var window = new MainWindow { DataContext = shell };
        window.Width = 1120;
        window.Height = 720;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.Show();

        foreach (var (page, name) in new[]
        {
            (ServerPage.Overview, "server-overview.png"),
            (ServerPage.Tunnels, "server-tunnels.png"),
            (ServerPage.Devices, "server-devices.png"),
            (ServerPage.Clients, "server-clients.png"),
            (ServerPage.Settings, "server-settings.png"),
        })
        {
            shell.GoToCommand.Execute(page.ToString());
            ScreenCapture.SaveCurrent(window, Path.Combine(directory, name), 1120, 720);
        }

        window.Close();
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
