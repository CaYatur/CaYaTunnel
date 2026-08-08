using System.Runtime.InteropServices;
using System.Windows;

namespace CaYaTunnel.Ui;

/// <summary>
/// Brings a window to the front from a process that is not currently in the foreground.
/// <para>
/// Windows deliberately refuses to let a background process steal focus, so
/// <see cref="Window.Activate"/> alone typically just flashes the taskbar button. Briefly
/// setting topmost is the accepted way to ask properly — the window comes forward once, and
/// does not stay pinned above everything afterwards.
/// </para>
/// </summary>
public static class WindowActivation
{
    public static void BringToFront(Window window)
    {
        if (window is null)
        {
            return;
        }

        window.Dispatcher.Invoke(() =>
        {
            if (!window.IsVisible)
            {
                window.Show();
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();

            var wasTopmost = window.Topmost;
            window.Topmost = true;
            window.Topmost = wasTopmost;

            window.Focus();

            if (new System.Windows.Interop.WindowInteropHelper(window).Handle is { } handle && handle != IntPtr.Zero)
            {
                SetForegroundWindow(handle);
            }
        });
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
