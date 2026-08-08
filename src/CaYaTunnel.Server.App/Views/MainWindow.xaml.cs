using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace CaYaTunnel.Server.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is ViewModels.ServerShellViewModel shell)
            {
                shell.EditTunnelRequested += row => new EditTunnelDialog
                {
                    Owner = this,
                    DataContext = new ViewModels.EditTunnelViewModel(shell, row),
                }.ShowDialog();
            }
        };
    }

    private bool _announcedTray;

    /// <summary>
    /// Closing the window puts the gateway in the tray rather than stopping it: tunnels that are
    /// carrying traffic should not drop because someone tidied their desktop. Exit lives in the
    /// tray menu, which sets IsExiting and lets this close go through.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        // Only the real admin window minimises. Capture mode opens and closes windows of its own,
        // and those must close for real.
        if (!App.IsExiting && ReferenceEquals(Application.Current?.MainWindow, this))
        {
            e.Cancel = true;
            Hide();

            if (!_announcedTray)
            {
                _announcedTray = true;
                App.AnnounceStillRunning();
            }

            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (ReferenceEquals(Application.Current?.MainWindow, this))
        {
            Application.Current.Shutdown();
        }
    }

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimise_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
