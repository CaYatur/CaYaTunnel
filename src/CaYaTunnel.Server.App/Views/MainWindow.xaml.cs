using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace CaYaTunnel.Server.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>
    /// Shutdown is explicit rather than tied to this window closing.
    /// <para>
    /// The capture mode opens and closes several windows from inside OnStartup, and letting the
    /// first close begin an application shutdown while Run has not been entered yet deadlocks the
    /// dispatcher. The admin window quits the app itself instead — but only when it is the real
    /// one, which is the case whenever the app has a shell attached.
    /// </para>
    /// </summary>
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
