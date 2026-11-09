using System.Drawing;
using System.Drawing.Drawing2D;
using WinForms = System.Windows.Forms;

namespace ScreenSizer.Services;

/// <summary>托盘常驻：双击打开，右键菜单应用 / 还原 / 退出。</summary>
internal sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _apply, _restore;
    private Icon? _ico;

    public TrayService(Action open, Action apply, Action restore, Action exit)
    {
        _apply = new WinForms.ToolStripMenuItem("应用", null, (_, _) => apply());
        _restore = new WinForms.ToolStripMenuItem("还原全屏", null, (_, _) => restore());
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(_apply);
        menu.Items.Add(_restore);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("打开 AimFrame", null, (_, _) => open()));
        menu.Items.Add(new WinForms.ToolStripMenuItem("退出", null, (_, _) => exit()));

        _icon = new WinForms.NotifyIcon
        {
            Text = "AimFrame",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
        SetAccent(Color.FromArgb(0xFF, 0x3B, 0x4E));
    }

    public void Update(string applyText, bool live, string tip)
    {
        _apply.Text = applyText;
        _apply.Enabled = !live;
        _restore.Enabled = live;
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    public void Balloon(string title, string text)
        => _icon.ShowBalloonTip(3000, title, text, WinForms.ToolTipIcon.Info);

    /// <summary>托盘图标：四角取景框 + 强调色中心块（与应用 Logo 一致）。</summary>
    public void SetAccent(Color accent)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(Color.Transparent);
            using var pen = new Pen(Color.White, 3);
            void L(int x1, int y1, int x2, int y2, int x3, int y3) => g.DrawLines(pen, new[] { new Point(x1, y1), new Point(x2, y2), new Point(x3, y3) });
            L(2, 11, 2, 2, 11, 2); L(21, 2, 30, 2, 30, 11); L(30, 21, 30, 30, 21, 30); L(11, 30, 2, 30, 2, 21);
            using var b = new SolidBrush(accent);
            g.FillRectangle(b, 10, 12, 12, 8);
        }
        var old = _ico;
        _ico = Icon.FromHandle(bmp.GetHicon());
        _icon.Icon = _ico;
        old?.Dispose();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _ico?.Dispose();
    }
}
