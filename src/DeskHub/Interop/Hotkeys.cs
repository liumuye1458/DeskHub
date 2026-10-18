using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

using DeskHub.Core;

namespace DeskHub.Interop;

/// <summary>
/// 全局快捷键。两种写法：
///   组合键：Ctrl+Alt+A（用系统的 RegisterHotKey，被别的程序占了会注册失败，设置里提示"冲突"）
///   连按两下 Ctrl：写作 "Ctrl Ctrl"（只看 Ctrl 键的按下 / 抬起，不记录任何按键内容）
/// </summary>
public sealed class Hotkeys : IDisposable
{
    public const string DoubleCtrl = "Ctrl Ctrl";

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, out RAWINPUT data, ref uint size, uint header);

    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTHEADER { public uint Type, Size; public IntPtr Device, WParam; }
    [StructLayout(LayoutKind.Sequential)] struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, ExtraInformation; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUT { public RAWINPUTHEADER Header; public RAWKEYBOARD Keyboard; }

    const int WM_HOTKEY = 0x0312, WM_INPUT = 0x00FF;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    const uint RIDEV_INPUTSINK = 0x100, RIDEV_REMOVE = 1, RID_INPUT = 0x10000003, RIM_TYPEKEYBOARD = 1;
    const ushort RI_KEY_BREAK = 1, VK_CONTROL = 0x11, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;

    readonly HwndSource _wnd;
    readonly Dictionary<int, Action> _actions = new();
    Action? _doubleCtrl;
    bool _rawOn;
    int _nextId = 1;

    public Hotkeys()
    {
        // 只收消息、不显示的窗口
        _wnd = new HwndSource(new HwndSourceParameters("DeskHub.Hotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _wnd.AddHook(Hook);
    }

    /// <summary>清掉全部，重新注册。返回注册失败（被占用 / 写法不对）的那些：名字 → 原因。</summary>
    public Dictionary<string, string> Apply(IEnumerable<(string Name, string Spec, Action Run)> list)
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_wnd.Handle, id);
        _actions.Clear();
        _doubleCtrl = null;
        var failed = new Dictionary<string, string>();
        foreach (var (name, spec, run) in list)
        {
            if (string.IsNullOrWhiteSpace(spec)) continue;
            if (spec == DoubleCtrl) { _doubleCtrl += run; continue; }
            if (!TryParse(spec, out var mods, out var vk)) { failed[name] = L.T("快捷键写法不对"); continue; }
            int id = _nextId++;
            if (RegisterHotKey(_wnd.Handle, id, mods | MOD_NOREPEAT, vk)) _actions[id] = run;
            else failed[name] = L.T("已被别的程序占用，换一个");
        }
        SetRaw(_doubleCtrl != null);
        return failed;
    }

    /// <summary>试一下这个组合键现在能不能用（设置里录快捷键时用）。</summary>
    public bool IsFree(string spec)
    {
        if (spec == DoubleCtrl || string.IsNullOrWhiteSpace(spec)) return true;
        if (!TryParse(spec, out var mods, out var vk)) return false;
        const int probe = 0xBFFF;
        if (!RegisterHotKey(_wnd.Handle, probe, mods | MOD_NOREPEAT, vk)) return false;
        UnregisterHotKey(_wnd.Handle, probe);
        return true;
    }

    // ---------------- 连按两下 Ctrl ----------------
    void SetRaw(bool on)
    {
        if (on == _rawOn) return;
        var dev = new[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = on ? RIDEV_INPUTSINK : RIDEV_REMOVE, Target = on ? _wnd.Handle : IntPtr.Zero } };
        if (RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>())) _rawOn = on;
    }

    bool _ctrlDown, _otherKey;
    long _ctrlDownAt, _lastTap;

    void OnRawKey(ushort vk, bool up)
    {
        long now = Environment.TickCount64;
        bool ctrl = vk is VK_CONTROL or VK_LCONTROL or VK_RCONTROL;
        if (!ctrl)
        {
            // 中间按了别的键（例如 Ctrl+C）就不算
            if (!up) { _otherKey = true; _lastTap = 0; }
            return;
        }
        if (!up)
        {
            if (_ctrlDown) return; // 按住不放时的自动重复
            _ctrlDown = true; _otherKey = false; _ctrlDownAt = now;
            return;
        }
        _ctrlDown = false;
        if (_otherKey || now - _ctrlDownAt > 300) { _lastTap = 0; return; }
        if (_lastTap != 0 && now - _lastTap < 400) { _lastTap = 0; _doubleCtrl?.Invoke(); return; }
        _lastTap = now;
    }

    IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue((int)w, out var run)) { run(); handled = true; }
        else if (msg == WM_INPUT)
        {
            uint size = (uint)Marshal.SizeOf<RAWINPUT>();
            if (GetRawInputData(l, RID_INPUT, out var ri, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != uint.MaxValue && ri.Header.Type == RIM_TYPEKEYBOARD)
                OnRawKey(ri.Keyboard.VKey, (ri.Keyboard.Flags & RI_KEY_BREAK) != 0);
        }
        return IntPtr.Zero;
    }

    // ---------------- 写法 ----------------
    public static bool TryParse(string spec, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": mods |= MOD_CONTROL; continue;
                case "alt": mods |= MOD_ALT; continue;
                case "shift": mods |= MOD_SHIFT; continue;
                case "win": mods |= MOD_WIN; continue;
            }
            var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
            if (!Enum.TryParse<Key>(name, true, out var key)) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        }
        return vk != 0;
    }

    /// <summary>录快捷键：把按下的组合变成 "Ctrl+Alt+A" 这样的写法；只按了修饰键返回 null。</summary>
    public static string? Describe(ModifierKeys mods, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None) return null;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        bool fkey = key is >= Key.F1 and <= Key.F24;
        if (parts.Count == 0 && !fkey) return null; // 单个字母当快捷键会影响正常打字
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString());
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_wnd.Handle, id);
        SetRaw(false);
        _wnd.Dispose();
    }
}
