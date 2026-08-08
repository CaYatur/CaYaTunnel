using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CaYaTunnel.Client.App.ViewModels;

namespace CaYaTunnel.Client.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => HookViewModel();
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void HookViewModel()
    {
        if (Shell is not { } shell)
        {
            return;
        }

        shell.NewTunnelRequested += ShowNewTunnelDialog;
        shell.EditTunnelRequested += ShowEditTunnelDialog;
    }

    private void ShowEditTunnelDialog(TunnelRow row)
    {
        if (Shell is not { } shell)
        {
            return;
        }

        new EditTunnelDialog
        {
            Owner = this,
            DataContext = new EditTunnelViewModel(shell, row),
        }.ShowDialog();
    }

    private void ShowNewTunnelDialog()
    {
        if (Shell is not { } shell)
        {
            return;
        }

        var dialog = new NewTunnelDialog
        {
            Owner = this,
            DataContext = new NewTunnelViewModel(shell),
        };

        dialog.ShowDialog();
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

    private bool _announcedTray;

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window is not the same as quitting: the agent's whole job is to stay
        // connected, so the default is to keep running in the tray. Exit from the tray menu sets
        // IsExiting first, which is what lets that close actually go through.
        if (Shell?.Settings.CloseToTray == true && !App.IsExiting)
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
        Application.Current.Shutdown();
    }
}
