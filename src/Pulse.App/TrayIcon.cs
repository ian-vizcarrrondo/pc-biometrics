using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Pulse.App;

/// <summary>System tray icon with Show / Pause / Resume / Exit. Created once by the controller.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppController _controller;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _pauseItem;
    private readonly Icon _live, _paused;

    public TrayIcon(AppController controller)
    {
        _controller = controller;
        _live = MakeIcon(Color.FromArgb(0x68, 0xD6, 0xE4));
        _paused = MakeIcon(Color.FromArgb(0xF3, 0xBD, 0x72));

        var menu = new System.Windows.Forms.ContextMenuStrip();
        var show = new System.Windows.Forms.ToolStripMenuItem("Show Pulse");
        show.Click += (_, _) => _controller.RequestShow();
        _pauseItem = new System.Windows.Forms.ToolStripMenuItem("Pause collection");
        _pauseItem.Click += (_, _) => _controller.TogglePaused();
        var exit = new System.Windows.Forms.ToolStripMenuItem("Exit");
        exit.Click += (_, _) => _controller.Exit();
        menu.Items.AddRange([show, _pauseItem, new System.Windows.Forms.ToolStripSeparator(), exit]);

        _icon = new System.Windows.Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) _controller.RequestShow();
        };
        _icon.BalloonTipClicked += (_, _) => _controller.RequestShow();
        Refresh();
    }

    public void Refresh()
    {
        var paused = _controller.Paused;
        _pauseItem.Text = paused ? "Resume collection" : "Pause collection";
        _icon.Icon = paused ? _paused : _live;
        _icon.Text = paused ? "Pulse — PAUSED (not collecting)" : "Pulse — collecting";
    }

    public void Notify(string title, string text)
    {
        _icon.ShowBalloonTip(6000, title, text.Length > 240 ? text[..240] : text, System.Windows.Forms.ToolTipIcon.Info);
    }

    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var back = new SolidBrush(Color.FromArgb(0x0B, 0x10, 0x17));
            g.FillEllipse(back, 1, 1, 30, 30);
            using var pen = new Pen(color, 3f) { LineJoin = LineJoin.Round };
            g.DrawLines(pen, [new PointF(5, 17), new PointF(11, 17), new PointF(14, 8), new PointF(19, 25), new PointF(22, 17), new PointF(27, 17)]);
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _live.Dispose();
        _paused.Dispose();
    }
}
