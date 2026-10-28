using System.Drawing;
using System.Windows.Forms;
using Poker.Advisor.Enums;
using Application = System.Windows.Application;

namespace Poker.Advisor;

internal sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _pauseMenuItem;
    private readonly ToolStripMenuItem _allBoxesMenuItem;
    private readonly ToolStripMenuItem _minimalBoxesMenuItem;
    private readonly ToolStripMenuItem _adviceOnlyMenuItem;
    private bool _isPaused;
    private BoxDisplayMode _boxMode = BoxDisplayMode.Minimal;

    public TrayIconService()
    {
        _pauseMenuItem = new ToolStripMenuItem("Pause Service", null, OnPauseServiceClicked);
        _allBoxesMenuItem = new ToolStripMenuItem("Show All Boxes", null, OnAllBoxesClicked)
        {
            CheckOnClick = false,
        };
        _minimalBoxesMenuItem = new ToolStripMenuItem("Show Minimal Boxes", null, OnMinimalBoxesClicked)
        {
            Checked = true,
            CheckOnClick = false,
        };
        _adviceOnlyMenuItem = new ToolStripMenuItem("Advice Only", null, OnAdviceOnlyClicked)
        {
            CheckOnClick = false,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_allBoxesMenuItem);
        menu.Items.Add(_minimalBoxesMenuItem);
        menu.Items.Add(_adviceOnlyMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExitClicked));

        _notifyIcon = new NotifyIcon
        {
            Text = "Poker Advisor",
            Icon = CreateTrayIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    public void SetStatus(string status)
    {
        var tip = string.IsNullOrWhiteSpace(status) ? "Poker Advisor" : status.Trim();
        if (tip.Length > 60)
        {
            tip = tip[..57] + "...";
        }

        _notifyIcon.Text = tip;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private void OnAllBoxesClicked(object? sender, EventArgs e) =>
        ApplyBoxMode(_boxMode == BoxDisplayMode.All ? BoxDisplayMode.Off : BoxDisplayMode.All);

    private void OnMinimalBoxesClicked(object? sender, EventArgs e) =>
        ApplyBoxMode(_boxMode == BoxDisplayMode.Minimal ? BoxDisplayMode.Off : BoxDisplayMode.Minimal);

    private void OnAdviceOnlyClicked(object? sender, EventArgs e) =>
        ApplyBoxMode(_boxMode == BoxDisplayMode.AdviceOnly ? BoxDisplayMode.Off : BoxDisplayMode.AdviceOnly);

    private void ApplyBoxMode(BoxDisplayMode mode)
    {
        _boxMode = mode;
        _allBoxesMenuItem.Checked = mode == BoxDisplayMode.All;
        _minimalBoxesMenuItem.Checked = mode == BoxDisplayMode.Minimal;
        _adviceOnlyMenuItem.Checked = mode == BoxDisplayMode.AdviceOnly;

        if (Application.Current.MainWindow is MainWindow window)
        {
            window.SetBoxDisplayMode(mode);
        }
    }

    private void OnPauseServiceClicked(object? sender, EventArgs e)
    {
        _isPaused = !_isPaused;
        _pauseMenuItem.Text = _isPaused ? "Resume Service" : "Pause Service";
        SetStatus(_isPaused ? "Paused" : "Poker Advisor");

        if (Application.Current.MainWindow is MainWindow window)
        {
            window.SetPaused(_isPaused);
        }
    }

    private static void OnExitClicked(object? sender, EventArgs e)
    {
        Application.Current.Shutdown();
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(255, 204, 0));
            graphics.FillEllipse(brush, 1, 1, 14, 14);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
