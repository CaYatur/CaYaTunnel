using System.IO;
using System.Windows;
using System.Windows.Threading;
using CaYaTunnel.Client.App.ViewModels;
using CaYaTunnel.Client.App.Views;
using CaYaTunnel.Core.Provisioning;
using CaYaTunnel.Ui;

namespace CaYaTunnel.Client.App;

public partial class App : Application
{
    public const string StartHiddenSwitch = "--hidden";

    private ShellViewModel? _shell;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A WPF process has no console, so an unhandled exception would otherwise vanish and
        // look like "it just closes". Writing it somewhere findable is the difference between a
        // reportable bug and an unreproducible one.
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);
        DispatcherUnhandledException += OnDispatcherException;

        if (Diagnostics.TryHandle(e.Args))
        {
            Shutdown();
            return;
        }

        if (TryCapture(e.Args))
        {
            Shutdown();
            return;
        }

        var store = new ClientSettingsStore();
        var settings = store.Load();
        var embedded = ClientConfigBlob.ReadFromCurrentProcess();
        var profile = ClientConnectionProfile.Resolve(settings, embedded);

        _shell = new ShellViewModel(store, settings, profile);

        var window = new MainWindow { DataContext = _shell };
        MainWindow = window;

        _tray = new TrayIcon(_shell, window);

        // Launching hidden is the normal case for "start with Windows": the agent should just
        // be there in the tray, not steal focus every sign-in.
        var startHidden = settings.StartMinimised
            || e.Args.Any(a => string.Equals(a, StartHiddenSwitch, StringComparison.OrdinalIgnoreCase));

        if (!startHidden)
        {
            window.Show();
        }

        if (settings.ConnectOnLaunch && profile.IsUsable)
        {
            _shell.Connect();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _shell?.ShutdownAsync().GetAwaiter().GetResult();
        base.OnExit(e);
    }

    /// <summary>
    /// <c>--capture &lt;directory&gt;</c>: renders each screen to PNG and exits. Used to check
    /// layout after a change and to regenerate the README images.
    /// </summary>
    private static bool TryCapture(string[] args)
    {
        var index = Array.FindIndex(args, a =>
            string.Equals(a, ScreenCapture.CaptureSwitch, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            return false;
        }

        var directory = index + 1 < args.Length ? args[index + 1] : "screenshots";

        // A throwaway settings file, so capturing never disturbs the real configuration.
        var store = new ClientSettingsStore(Path.Combine(Path.GetTempPath(), $"cayatunnel-capture-{Guid.NewGuid():n}.json"));
        var settings = new ClientSettings { DeviceName = "CAGAN-PC" };
        var profile = new ClientConnectionProfile(
            "gateway.example.com", 48771, "preview", new string('a', 64), "CaYaDev Gateway", Provisioned: true);

        var shell = new ShellViewModel(store, settings, profile);
        shell.LoadPreviewSnapshot(PreviewData.Build());

        ScreenCapture.Save(
            new MainWindow { DataContext = shell },
            Path.Combine(directory, "client-tunnels.png"),
            1080, 700);

        shell.Page = ClientPage.Devices;
        ScreenCapture.Save(
            new MainWindow { DataContext = shell },
            Path.Combine(directory, "client-devices.png"),
            1080, 700);

        shell.Page = ClientPage.Settings;
        ScreenCapture.Save(
            new MainWindow { DataContext = shell },
            Path.Combine(directory, "client-settings.png"),
            1080, 700);

        ScreenCapture.Save(
            new Views.NewTunnelDialog { DataContext = new NewTunnelViewModel(shell) },
            Path.Combine(directory, "client-new-tunnel.png"),
            620, 900);

        return true;
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);

        MessageBox.Show(
            $"CaYaTunnel hit an unexpected error.\n\n{e.Exception.Message}\n\nDetails were written to:\n{CrashLogPath}",
            "CaYaTunnel",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Keep running: a failure while rendering one screen should not drop live tunnels.
        e.Handled = true;
    }

    private static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CaYaTunnel",
        "client-errors.log");

    private static void WriteCrashLog(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(
                CrashLogPath,
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write; nothing further we can do.
        }
    }
}
