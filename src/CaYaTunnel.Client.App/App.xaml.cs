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
    private TrayPresence? _tray;
    private SingleInstance? _instance;

    /// <summary>True once the user chose Exit, so the window stops bouncing back to the tray.</summary>
    public static bool IsExiting => (Current as App)?._tray?.IsExiting ?? false;

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

        // Keyed on the settings file: a second copy of this install would fight over the same
        // device identity, while a separate portable copy pointed at another gateway is fine.
        if (!SingleInstance.TryClaim(store.Path, out _instance))
        {
            Shutdown();
            return;
        }

        var settings = store.Load();
        var embedded = ClientConfigBlob.ReadFromCurrentProcess();
        var profile = ClientConnectionProfile.Resolve(settings, embedded);

        _shell = new ShellViewModel(store, settings, profile);

        var window = new MainWindow { DataContext = _shell };
        MainWindow = window;

        _tray = new TrayPresence(window, Loc.Get("AppClient"), () => _shell.IsOnline);
        _shell.StateChanged += () => _tray.Refresh(BuildTrayStatus(_shell));
        _shell.NoticeRaised += notice =>
        {
            if (!_shell.Settings.ShowNotifications)
            {
                return;
            }

            _tray.Notify(notice.Title, notice.Body, notice.Severity switch
            {
                "error" => System.Windows.Forms.ToolTipIcon.Error,
                "warning" => System.Windows.Forms.ToolTipIcon.Warning,
                _ => System.Windows.Forms.ToolTipIcon.Info,
            });
        };

        // Launching again is how a user asks for the window back when it is hidden in the tray.
        _instance!.SecondInstanceAttempted += () => _tray.Show();

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
        // The tray icon goes first: leaving a dead icon behind while the rest winds down is the
        // most visible way an exit can look broken.
        _tray?.Dispose();

        if (_shell is { } shell)
        {
            ShutdownGuard.Run(shell.ShutdownAsync);
        }

        _instance?.Dispose();
        base.OnExit(e);
    }

    private static string BuildTrayStatus(ShellViewModel shell) => shell.IsOnline
        ? $"{Loc.Get("StateOnline")} · {shell.TunnelCount} {Loc.Get("TunnelCount")}"
        : shell.StatusLabel;

    /// <summary>Shown the first time the window is closed, so the app is not assumed to be gone.</summary>
    public static void AnnounceStillRunning()
    {
        if (Current is App { _tray: { } tray })
        {
            tray.Notify(Loc.Get("StillRunningTitle"), Loc.Get("StillRunningBodyClient"),
                System.Windows.Forms.ToolTipIcon.Info);
        }
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

        // One window for every page, switched the way the sidebar switches it. A window per page
        // would render correctly even with navigation broken, which is how that bug shipped.
        var window = new MainWindow { DataContext = shell };
        window.Width = 1080;
        window.Height = 700;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.Show();

        foreach (var (page, name) in new[]
        {
            (ClientPage.Tunnels, "client-tunnels.png"),
            (ClientPage.Devices, "client-devices.png"),
            (ClientPage.Settings, "client-settings.png"),
        })
        {
            shell.GoToCommand.Execute(page.ToString());
            ScreenCapture.SaveCurrent(window, Path.Combine(directory, name), 1080, 700);
        }

        window.Close();

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
