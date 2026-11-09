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
        // 托盘使用应用图标，与 exe / 任务栏一致
        var s = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/AimFrame.ico"))?.Stream;
        if (s != null) _ico = new Icon(s);
        if (_ico != null) _icon.Icon = _ico;
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

    /// <summary>主题强调色变化时托盘保持应用图标不变。</summary>
    public void SetAccent(Color accent) { }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _ico?.Dispose();
    }
}
