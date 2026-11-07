using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DeskHub.Interop;

/// <summary>
/// 桌面块的毛玻璃：让系统把窗口背后的壁纸模糊后垫在下面，我们自己的半透明底色（深浅可调）再叠在上面。
/// 分区是分层透明窗口（AllowsTransparency），也从不当“活动窗口”，Windows 11 正式的云母 / 亚克力
/// （DWMWA_SYSTEMBACKDROP_TYPE）对这种窗口不起作用或会退成纯色，所以用窗口合成属性（SetWindowCompositionAttribute）：
/// Windows 10 1803 起就有，不看窗口是否活动。
/// </summary>
static class Backdrop
{
    public enum Kind { None, Blur, Acrylic }

    public static Kind Parse(string? s) => s?.ToLowerInvariant() switch { "blur" => Kind.Blur, "acrylic" => Kind.Acrylic, _ => Kind.None };

    /// <summary>Windows 11（有系统圆角）默认开毛玻璃；Windows 10 圆角外会露出方的模糊，默认不开，设置里可以自己打开。</summary>
    public static bool DefaultOn => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>桌面块现在用的底子（设置里的“毛玻璃底”）。</summary>
    public static Kind Current { get; private set; }
    static event Action? Changed;

    public static void Set(Kind kind)
    {
        if (kind == Current) return;
        Current = kind;
        Changed?.Invoke();
    }

    /// <summary>
    /// 桌面块（分区、日历、工具条）挂上来：跟着设置换底子；onChange(true) = 现在有毛玻璃，块自己调圆角、外边距。
    /// 亚克力窗口在移动、拉大小时会跟不上鼠标：动起来的时候先换成普通模糊，停下 0.3 秒再换回来。
    /// </summary>
    public static void Attach(Window w, Action<bool>? onChange = null)
    {
        var shown = Kind.None;
        var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        void Show(Kind k)
        {
            if (k == shown) return;
            shown = k;
            Apply(w, k);
        }
        void Refresh() { Show(Current); onChange?.Invoke(Current != Kind.None); }
        void Moving(object? s, EventArgs e)
        {
            if (Current != Kind.Acrylic || new WindowInteropHelper(w).Handle == IntPtr.Zero) return;
            Show(Kind.Blur);
            settle.Stop(); settle.Start();
        }
        settle.Tick += (_, _) => { settle.Stop(); Show(Current); };
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Refresh(); else w.SourceInitialized += (_, _) => Refresh();
        Changed += Refresh;
        w.LocationChanged += Moving;
        w.SizeChanged += Moving;
        w.Closed += (_, _) => { Changed -= Refresh; settle.Stop(); };
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy { public int State, Flags; public uint Gradient; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)]
    struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref CompositionData d);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    const int WCA_ACCENT_POLICY = 19;
    const int ACCENT_DISABLED = 0, ACCENT_BLURBEHIND = 3, ACCENT_ACRYLICBLURBEHIND = 4;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DEFAULT = 0, DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3;

    /// <summary>给窗口垫上（或去掉）背后模糊。</summary>
    static void Apply(Window w, Kind kind)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        var p = new AccentPolicy
        {
            State = kind switch { Kind.Blur => ACCENT_BLURBEHIND, Kind.Acrylic => ACCENT_ACRYLICBLURBEHIND, _ => ACCENT_DISABLED },
            // 亚克力自带一层底色（AABBGGRR）：给最淡的一层，颜色深浅交给我们自己的底色
            Gradient = kind == Kind.Acrylic ? 0x01000000u : 0,
        };
        var size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(p, ptr, false);
            var d = new CompositionData { Attribute = WCA_ACCENT_POLICY, Data = ptr, Size = size };
            SetWindowCompositionAttribute(h, ref d);
        }
        finally { Marshal.FreeHGlobal(ptr); }
        Round(h, kind != Kind.None);
    }

    /// <summary>Windows 11 的系统圆角（模糊是按整个窗口矩形垫的，不圆的话四个角会露出方的模糊）。Windows 10 上没有这个属性，调用无效也无害。</summary>
    static void Round(IntPtr h, bool on)
    {
        int v = on ? DWMWCP_ROUND : DWMWCP_DEFAULT;
        DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int));
    }
}
