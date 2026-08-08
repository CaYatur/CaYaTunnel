using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CaYaTunnel.Ui;

/// <summary>
/// Renders windows to PNG without a person driving the app.
/// <para>
/// Two jobs: verifying the layout actually looks right after a change, and producing the
/// screenshots in the README. Both otherwise need a human with a screenshot key, which means
/// they quietly stop happening.
/// </para>
/// </summary>
public static class ScreenCapture
{
    public const string CaptureSwitch = "--capture";

    /// <summary>Renders a window at the requested size, off-screen, and writes a PNG.</summary>
    public static void Save(Window window, string path, int width, int height, double scale = 1.5)
    {
        window.Width = width;
        window.Height = height;

        // Positioned far off-screen so the capture never flashes in front of the user.
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.Show();

        Render(window, path, width, height, scale);
        window.Close();
    }

    /// <summary>
    /// Renders a window that is already open, leaving it open.
    /// <para>
    /// This is what makes the capture worth running: a fresh window per screen renders whatever
    /// the view model currently says and passes even when navigation is broken. Reusing one
    /// window exercises the same path a person clicking the sidebar does.
    /// </para>
    /// </summary>
    public static void SaveCurrent(Window window, string path, int width, int height, double scale = 1.5)
        => Render(window, path, width, height, scale);

    private static void Render(Window window, string path, int width, int height, double scale)
    {
        // Let WPF finish measure, arrange and the first render pass; without draining the
        // dispatcher the bitmap comes out blank.
        window.UpdateLayout();
        Drain();

        var bitmap = new RenderTargetBitmap(
            (int)(width * scale),
            (int)(height * scale),
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);

        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Drain()
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
    }
}
