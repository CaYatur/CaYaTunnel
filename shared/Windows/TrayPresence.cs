using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace CaYaTunnel.Ui;

/// <summary>
/// Tray presence shared by both applications.
/// <para>
/// Both keep working with their window closed — the agent holds its tunnels open, the gateway
/// keeps serving them — so both need somewhere to live and a way back. Without it, closing the
/// window leaves a process with no way to reopen it and no obvious way to stop it.
/// </para>
/// </summary>
public sealed class TrayPresence : IDisposable
{
    private readonly Window _window;
    private readonly NotifyIcon _icon;
    private readonly Func<bool> _isActive;
    private readonly string _appName;

    private bool _exiting;

    public TrayPresence(Window window, string appName, Func<bool> isActive)
    {
        _window = window;
        _appName = appName;
        _isActive = isActive;

        _icon = new NotifyIcon
        {
            Icon = BuildIcon(active: isActive()),
            Visible = true,
            Text = appName,
            ContextMenuStrip = BuildMenu(),
        };

        _icon.DoubleClick += (_, _) => Show();
        Refresh(null);
    }

    /// <summary>True once the user chose Exit, so the window stops hiding and closes for real.</summary>
    public bool IsExiting => _exiting;

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = ColorTranslator.FromHtml("#17171D"),
            ForeColor = ColorTranslator.FromHtml("#F3F3F5"),
            RenderMode = ToolStripRenderMode.System,
        };

        menu.Items.Add(Loc.Get("OpenWindow"), null, (_, _) => Show());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.Get("ExitApp"), null, (_, _) => Exit());

        return menu;
    }

    public void Show() => WindowActivation.BringToFront(_window);

    /// <summary>
    /// Quits for real. Sets the flag first so the window's close handler stops sending it back
    /// to the tray, which is what made Exit appear to do nothing.
    /// </summary>
    public void Exit()
    {
        _exiting = true;
        _icon.Visible = false;

        _window.Dispatcher.BeginInvoke(() =>
        {
            _window.Close();
            Application.Current?.Shutdown();
        });
    }

    /// <summary>Updates the icon and tooltip. <paramref name="status"/> is appended when given.</summary>
    public void Refresh(string? status)
    {
        var active = _isActive();

        _icon.Icon?.Dispose();
        _icon.Icon = BuildIcon(active);

        // NotifyIcon silently truncates past 63 characters, so this stays short.
        var text = status is null ? _appName : $"{_appName} — {status}";
        _icon.Text = text.Length > 62 ? text[..62] : text;
    }

    public void Notify(string title, string? body, ToolTipIcon severity)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = body ?? string.Empty;
        _icon.BalloonTipIcon = severity;
        _icon.ShowBalloonTip(6000);
    }

    /// <summary>
    /// Drawn rather than shipped as a resource, so the icon can reflect live state and the
    /// single-file build stays a single file.
    /// </summary>
    private static Icon BuildIcon(bool active)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var accent = active
                ? ColorTranslator.FromHtml("#E8232A")
                : ColorTranslator.FromHtml("#5A5A66");

            using var background = new SolidBrush(ColorTranslator.FromHtml("#0F0F13"));
            graphics.FillEllipse(background, 0, 0, 31, 31);

            using var ring = new Pen(accent, 3f);
            graphics.DrawEllipse(ring, 3, 3, 25, 25);

            using var bar = new SolidBrush(accent);
            graphics.FillRectangle(bar, 9, 14, 14, 4);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
