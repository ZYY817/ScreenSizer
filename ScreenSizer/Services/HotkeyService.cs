using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;

namespace ScreenSizer.Services;

/// <summary>一组修饰键 + 虚拟键，如 "Ctrl+Alt+S"。</summary>
public readonly record struct HotkeyCombo(uint Mods, uint Vk)
{
    public bool IsEmpty => Vk == 0;

    public override string ToString()
    {
        var s = new StringBuilder();
        if ((Mods & HotkeyService.MOD_CONTROL) != 0) s.Append("Ctrl+");
        if ((Mods & HotkeyService.MOD_ALT) != 0) s.Append("Alt+");
        if ((Mods & HotkeyService.MOD_SHIFT) != 0) s.Append("Shift+");
        if ((Mods & HotkeyService.MOD_WIN) != 0) s.Append("Win+");
        var key = KeyInterop.KeyFromVirtualKey((int)Vk);
        s.Append(key == Key.None ? $"0x{Vk:X2}" : KeyText(key));
        return s.ToString();
    }

    private static string KeyText(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + k - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => $"Num{(char)('0' + k - Key.NumPad0)}",
        Key.OemPlus => "+", Key.OemMinus => "-", Key.OemPeriod => ".", Key.OemComma => ",",
        _ => k.ToString(),
    };

    /// <summary>解析 "Ctrl+Alt+S" 这类字符串。</summary>
    public static bool TryParse(string? s, out HotkeyCombo hk)
    {
        hk = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        uint mods = 0, vk = 0;
        foreach (var part in s.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyService.MOD_CONTROL; continue;
                case "alt": mods |= HotkeyService.MOD_ALT; continue;
                case "shift": mods |= HotkeyService.MOD_SHIFT; continue;
                case "win": mods |= HotkeyService.MOD_WIN; continue;
            }
            if (vk != 0 || !Enum.TryParse<Key>(part, true, out var key) || key == Key.None)
                return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        }
        if (vk == 0) return false;
        hk = new HotkeyCombo(mods, vk);
        return true;
    }

    /// <summary>从当前按键状态构造组合键（要求非修饰键）。</summary>
    public static HotkeyCombo FromKeys(ModifierKeys mods, Key key)
    {
        uint m = 0;
        if ((mods & ModifierKeys.Control) != 0) m |= HotkeyService.MOD_CONTROL;
        if ((mods & ModifierKeys.Alt) != 0) m |= HotkeyService.MOD_ALT;
        if ((mods & ModifierKeys.Shift) != 0) m |= HotkeyService.MOD_SHIFT;
        if ((mods & ModifierKeys.Windows) != 0) m |= HotkeyService.MOD_WIN;
        return new HotkeyCombo(m, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
}

/// <summary>全局热键。默认 Ctrl+Alt+S 应用/还原，Ctrl+Alt+R 应急还原，可自定义。</summary>
internal sealed class HotkeyService : IDisposable
{
    public const int WM_HOTKEY = 0x0312;
    public const int IdToggle = 0x5301, IdForce = 0x5302;

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, HotkeyCombo> _bound = new();

    public bool ToggleOk => _bound.ContainsKey(IdToggle);
    public bool ForceOk => _bound.ContainsKey(IdForce);

    public HotkeyService(IntPtr hwnd, HotkeyCombo toggle, HotkeyCombo force)
    {
        _hwnd = hwnd;
        Rebind(IdToggle, toggle);
        Rebind(IdForce, force);
    }

    /// <summary>重新绑定某个热键；返回是否注册成功（被占用来 false）。</summary>
    public bool Rebind(int id, HotkeyCombo hk)
    {
        if (_bound.Remove(id)) UnregisterHotKey(_hwnd, id);
        if (hk.IsEmpty) return true;
        if (!RegisterHotKey(_hwnd, id, hk.Mods | MOD_NOREPEAT, hk.Vk)) return false;
        _bound[id] = hk;
        return true;
    }

    public void Dispose()
    {
        foreach (var id in _bound.Keys) UnregisterHotKey(_hwnd, id);
        _bound.Clear();
    }
}
