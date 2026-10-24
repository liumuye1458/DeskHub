using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>
/// 桌面上各个块（分区、日历、工具条）的底色深浅。
/// 按壁纸各记一份：换回调过的壁纸自动恢复；没调过的新壁纸先沿用上一次的值。某一块也可以单独调（同样跟着壁纸走）。
/// </summary>
public static class BgLook
{
    /// <summary>原来写死的深浅（#40 黑）。</summary>
    public const double Default = 0.25;
    public const double Max = 0.8;
    /// <summary>冷却区比普通分区浅一点（原来是 #2E 对 #40）。</summary>
    public const double ColdRatio = 0.72;

    static AppConfig? _cfg;
    /// <summary>当前壁纸的标识（各屏壁纸路径 + 大小；纯色壁纸是颜色）。</summary>
    public static string Wallpaper { get; private set; } = "";
    /// <summary>沙盒测试用：假装是这张壁纸。</summary>
    public static string? FakeWallpaper { get; set; }
    /// <summary>深浅变了（拖滑条、换壁纸）：各块重画底色。</summary>
    public static event Action? Changed;

    public static void Init(AppConfig cfg)
    {
        _cfg = cfg;
        Wallpaper = FakeWallpaper ?? WallpaperKey();
    }

    /// <summary>看壁纸换了没有（系统通知 + 定时检查都会来调）。</summary>
    public static void CheckWallpaper()
    {
        var now = FakeWallpaper ?? WallpaperKey();
        if (now == Wallpaper) return;
        Wallpaper = now;
        Changed?.Invoke();
    }

    static WallLook? Cur => _cfg != null && _cfg.Looks.TryGetValue(Wallpaper, out var l) ? l : null;

    /// <summary>这张壁纸调过没有。</summary>
    public static bool Remembered => Cur != null;
    public static double Global => Cur?.Alpha ?? _cfg?.LookLast ?? Default;
    public static double? Own(string key) => Cur?.Own.TryGetValue(key, out var v) == true ? v : null;
    public static double Of(string? key) => key != null && Own(key) is { } v ? v : Global;

    public static void SetGlobal(double a)
    {
        if (_cfg == null) return;
        a = Clamp(a);
        var l = Ensure();
        l.Alpha = a;
        _cfg.LookLast = a;
        Changed?.Invoke();
    }

    /// <summary>单独调某一块；null = 回到跟随全局。</summary>
    public static void SetOwn(string key, double? a)
    {
        if (_cfg == null) return;
        var l = Ensure();
        if (a is { } v) l.Own[key] = Clamp(v); else l.Own.Remove(key);
        Changed?.Invoke();
    }

    public static void Save() => _cfg?.Save();

    static double Clamp(double a) => Math.Round(Math.Clamp(a, 0, Max), 2);

    static WallLook Ensure()
    {
        if (!_cfg!.Looks.TryGetValue(Wallpaper, out var l))
        {
            l = new WallLook { Alpha = Global };
            _cfg.Looks[Wallpaper] = l;
            // 轮播壁纸会攒很多张：只留最近用过的 200 张
            if (_cfg.Looks.Count > 200)
                foreach (var old in _cfg.Looks.OrderBy(kv => kv.Value.Used).Take(_cfg.Looks.Count - 200).Select(kv => kv.Key).ToList())
                    _cfg.Looks.Remove(old);
        }
        l.Used = DateTime.Now;
        return l;
    }

    static readonly Dictionary<byte, Brush> Brushes = new();
    public static Brush Brush(double a)
    {
        var b = (byte)Math.Round(Math.Clamp(a, 0, 1) * 255);
        if (b == 0) b = 1; // 全透明也留一点点，不然点不中空白处
        if (!Brushes.TryGetValue(b, out var br)) { br = new SolidColorBrush(Color.FromArgb(b, 0, 0, 0)); br.Freeze(); Brushes[b] = br; }
        return br;
    }

    /// <summary>给人看的壁纸名（只取文件名；纯色就说纯色）。</summary>
    public static string WallpaperName()
    {
        var first = Wallpaper.Split('\n')[0];
        if (first.StartsWith("color:")) return L.T("纯色背景");
        var path = first.Split('|')[0];
        var name = Path.GetFileName(path);
        if (name.Equals("TranscodedWallpaper", StringComparison.OrdinalIgnoreCase) || name.Length == 0) return L.T("当前壁纸");
        return name + (Wallpaper.Contains('\n') ? " " + L.T("等") : "");
    }

    // ---------------- 读壁纸 ----------------
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
        uint GetMonitorDevicePathCount();
        Rect32 GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect32 { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
    class DesktopWallpaperClass { }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SystemParametersInfo(uint action, uint param, System.Text.StringBuilder sb, uint ini);

    /// <summary>测试用：读一次真实壁纸。</summary>
    public static string ReadRealWallpaper() => WallpaperKey();

    /// <summary>各屏（按位置排）壁纸路径 + 文件大小，一屏一行；没有图片就是背景色。</summary>
    static string WallpaperKey()
    {
        try
        {
            var dw = (IDesktopWallpaper)new DesktopWallpaperClass();
            try
            {
                var screens = new List<(int X, int Y, string Key)>();
                for (uint i = 0; i < dw.GetMonitorDevicePathCount(); i++)
                {
                    try
                    {
                        var id = dw.GetMonitorDevicePathAt(i);
                        var r = dw.GetMonitorRECT(id); // 没接上的屏会抛错，跳过
                        screens.Add((r.Left, r.Top, FileKey(dw.GetWallpaper(id))));
                    }
                    catch { }
                }
                var parts = screens.OrderBy(s => s.X).ThenBy(s => s.Y).Select(s => s.Key).Distinct().ToList();
                if (parts.All(p => p.Length == 0)) return $"color:{dw.GetBackgroundColor():X6}";
                return string.Join("\n", parts.Where(p => p.Length > 0));
            }
            finally { Marshal.ReleaseComObject(dw); }
        }
        catch
        {
            var sb = new System.Text.StringBuilder(520);
            return SystemParametersInfo(0x0073, 520, sb, 0) && sb.Length > 0 ? FileKey(sb.ToString()) : "unknown";
        }
    }

    /// <summary>路径 + 大小：同一个路径换了图（比如系统转存的 TranscodedWallpaper）也认得出来。</summary>
    static string FileKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return $"{path}|{new FileInfo(path).Length}"; } catch { return path; }
    }
}
