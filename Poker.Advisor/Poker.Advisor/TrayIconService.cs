using System.Drawing;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace Poker.Advisor;

internal sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _pauseMenuItem;
    private readonly ToolStripMenuItem _boxesMenuItem;
    private bool _isPaused;
    private bool _boxesEnabled = true;

    public TrayIconService()
    {
        _pauseMenuItem = new ToolStripMenuItem("Pause Service", null, OnPauseServiceClicked);
        _boxesMenuItem = new ToolStripMenuItem("Show Boxes", null, OnBoxesClicked)
        {
            Checked = true,
            CheckOnClick = false,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_boxesMenuItem);
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

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private void OnBoxesClicked(object? sender, EventArgs e)
    {
        _boxesEnabled = !_boxesEnabled;
        _boxesMenuItem.Checked = _boxesEnabled;

        if (Application.Current.MainWindow is MainWindow window)
        {
            window.SetBoxesEnabled(_boxesEnabled);
        }
    }

    private void OnPauseServiceClicked(object? sender, EventArgs e)
    {
        _isPaused = !_isPaused;
        _pauseMenuItem.Text = _isPaused ? "Resume Service" : "Pause Service";

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
