using System.Text;
using ScreenSizer.Native;

namespace ScreenSizer.Services;

/// <summary>
/// 窗口化兜底方案：把指定窗口去边框、缩到目标尺寸、居中置顶层。
/// 全显卡通用，不碰显示模式。
/// </summary>
internal sealed class WindowScaler
{
    public sealed record GameWindow(IntPtr Hwnd, string Title, uint Pid)
    {
        private static readonly string[] Palette = { "#D9822B", "#E2434B", "#5B6CF0", "#2FA37A", "#B35CD6", "#3B8FD9" };

        /// <summary>列表图标上的两个字母。</summary>
        public string Initials
        {
            get
            {
                var letters = Title.Where(char.IsLetterOrDigit).Take(2).ToArray();
                return letters.Length == 0 ? "?" : new string(letters).ToUpperInvariant();
            }
        }

        public string IconColor => Palette[(int)(Pid % (uint)Palette.Length)];
        public string Meta => $"pid {Pid}";

        public override string ToString() => $"{Title}  (pid {Pid})";
    }

    private sealed record SavedWindow(long Style, long ExStyle, NativeMethods.RECT Rect);

    private readonly Dictionary<IntPtr, SavedWindow> _saved = new();

    public static List<GameWindow> ListWindows(uint excludePid)
    {
        var list = new List<GameWindow>();
        NativeMethods.EnumWindows((h, _) =>
        {
            if (!NativeMethods.IsWindowVisible(h)) return true;
            if (NativeMethods.GetWindow(h, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;
            int len = NativeMethods.GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length == 0) return true;
            NativeMethods.GetWindowThreadProcessId(h, out var pid);
            if (pid == excludePid || pid == 0) return true;
            list.Add(new GameWindow(h, title, pid));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>窗口居中缩放到 w×h（物理像素），可选置顶层。left/top/screenW/screenH 为显示器工作区。</summary>
    public ScaleResult Apply(IntPtr hwnd, int w, int h, int left, int top, int screenW, int screenH, bool topmost)
    {
        if (!_saved.ContainsKey(hwnd))
        {
            NativeMethods.GetWindowRect(hwnd, out var r);
            _saved[hwnd] = new SavedWindow(
                NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64(),
                NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64(),
                r);
        }

        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME |
                   NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX |
                   NativeMethods.WS_SYSMENU | NativeMethods.WS_BORDER);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));

        long exstyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exstyle &= ~(NativeMethods.WS_EX_WINDOWEDGE | NativeMethods.WS_EX_CLIENTEDGE);
        if (topmost) exstyle |= NativeMethods.WS_EX_TOPMOST;
        else exstyle &= ~NativeMethods.WS_EX_TOPMOST;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exstyle));

        int x = left + (screenW - w) / 2;
        int y = top + (screenH - h) / 2;
        NativeMethods.SetWindowPos(hwnd,
            topmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            x, y, w, h,
            NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
        return new ScaleResult(true, $"窗口已缩放为 {w}×{h} 并居中");
    }

    public ScaleResult Restore(IntPtr hwnd)
    {
        if (!_saved.TryGetValue(hwnd, out var s))
            return new ScaleResult(false, "该窗口没有保存的状态");
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(s.Style));
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(s.ExStyle));
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST,
            s.Rect.Left, s.Rect.Top, s.Rect.Width, s.Rect.Height,
            NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
        _saved.Remove(hwnd);
        return new ScaleResult(true, "窗口已还原");
    }

    public bool HasSaved(IntPtr hwnd) => _saved.ContainsKey(hwnd);
}
