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

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window is not the same as quitting: the agent's whole job is to stay
        // connected, so the default is to keep running in the tray.
        if (Shell?.Settings.CloseToTray == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
        Application.Current.Shutdown();
    }
}
