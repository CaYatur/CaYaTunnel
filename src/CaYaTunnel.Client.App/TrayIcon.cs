using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using CaYaTunnel.Client.App.ViewModels;
using CaYaTunnel.Ui;
using Application = System.Windows.Application;

namespace CaYaTunnel.Client.App;

/// <summary>
/// Tray presence for the agent.
/// <para>
/// The client is meant to run unattended, so the tray is its normal home rather than a nicety:
/// it is where the user checks whether tunnels are live, and where a "removed remotely" notice
/// reaches them when the window is closed.
/// </para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly Window _window;
    private readonly NotifyIcon _icon;

    public TrayIcon(ShellViewModel shell, Window window)
    {
        _shell = shell;
        _window = window;

        _icon = new NotifyIcon
        {
            Icon = BuildIcon(offline: true),
            Visible = true,
            Text = "CaYaTunnel",
            ContextMenuStrip = BuildMenu(),
        };

        _icon.DoubleClick += (_, _) => ShowWindow();

        _shell.StateChanged += OnStateChanged;
        _shell.NoticeRaised += OnNotice;

        OnStateChanged();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = ColorTranslator.FromHtml("#17171D"),
            ForeColor = ColorTranslator.FromHtml("#F3F3F5"),
            RenderMode = ToolStripRenderMode.System,
        };

        menu.Items.Add(Loc.Get("Open"), null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.Get("Close"), null, (_, _) => Application.Current.Shutdown());

        return menu;
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnStateChanged()
    {
        var online = _shell.IsOnline;

        _icon.Icon?.Dispose();
        _icon.Icon = BuildIcon(offline: !online);

        // NotifyIcon truncates at 63 characters, and silently, so the text is kept short.
        var status = online
            ? $"{Loc.Get("StateOnline")} · {_shell.TunnelCount} {Loc.Get("TunnelCount")}"
            : _shell.StatusLabel;

        _icon.Text = $"CaYaTunnel — {status}";
    }

    private void OnNotice(Core.Protocol.Messages.NoticeMessage notice)
    {
        if (!_shell.Settings.ShowNotifications)
        {
            return;
        }

        _icon.BalloonTipTitle = notice.Title;
        _icon.BalloonTipText = notice.Body ?? string.Empty;
        _icon.BalloonTipIcon = notice.Severity switch
        {
            "error" => ToolTipIcon.Error,
            "warning" => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info,
        };

        _icon.ShowBalloonTip(6000);
    }

    /// <summary>
    /// Draws the tray icon rather than shipping .ico resources, so the state is reflected live
    /// and the single-file build stays a single file.
    /// </summary>
    private static Icon BuildIcon(bool offline)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var accent = offline
                ? ColorTranslator.FromHtml("#5A5A66")
                : ColorTranslator.FromHtml("#E8232A");

            using var background = new SolidBrush(ColorTranslator.FromHtml("#0F0F13"));
            graphics.FillEllipse(background, 0, 0, 31, 31);

            using var ring = new Pen(accent, 3f);
            graphics.DrawEllipse(ring, 3, 3, 25, 25);

            // A short bar through the middle: the tunnel.
            using var bar = new SolidBrush(accent);
            graphics.FillRectangle(bar, 9, 14, 14, 4);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _shell.StateChanged -= OnStateChanged;
        _shell.NoticeRaised -= OnNotice;

        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}
