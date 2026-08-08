using System.Windows;
using System.Windows.Input;
using CaYaTunnel.Client.App.ViewModels;

namespace CaYaTunnel.Client.App.Views;

public partial class EditTunnelDialog : Window
{
    public EditTunnelDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is EditTunnelViewModel viewModel)
            {
                viewModel.Completed += () => Dispatcher.BeginInvoke(Close);
            }
        };
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
