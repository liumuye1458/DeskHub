using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DeskHub.Interop;

using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>
/// 桌面时钟：时区、12/24 小时制、日期格式全部跟随 Windows 设置。
/// 改了系统时区或区域格式会立刻跟着变。只在整分钟（或开了秒显示时每秒）唤醒一次。
/// </summary>
public partial class ClockWindow : Window
{
    readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    public bool ShowSeconds { get; set; }
    /// <summary>时钟下面自己写的一行字。</summary>
    public string Note { get; set; } = "";
    public event Action? Moved;
    /// <summary>点了一下（没拖动）。</summary>
    public event Action? Clicked;

    public ClockWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        SourceInitialized += (_, _) =>
        {
            DesktopHost.Pin(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(Hook);
        };
        // 按住拖 = 挪位置；点一下（没挪动）= 打开日历
        MouseLeftButtonDown += (_, _) =>
        {
            double x = Left, y = Top;
            DragMove();
            if (Math.Abs(Left - x) + Math.Abs(Top - y) > 2) Moved?.Invoke();
            else Clicked?.Invoke();
        };
        Loaded += (_, _) => Tick();
    }

    IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        // 系统时间、时区、区域格式变了：清掉缓存，马上刷新
        if (msg is Native.WM_TIMECHANGE or Native.WM_SETTINGCHANGE)
        {
            CultureInfo.CurrentCulture.ClearCachedData();
            TimeZoneInfo.ClearCachedData();
            Dispatcher.BeginInvoke(Tick);
        }
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetUserDefaultLocaleName(System.Text.StringBuilder buf, int cch);

    /// <summary>每次都重新读 Windows 当前的"区域格式"（含用户自定义的时间格式），不用进程启动时的旧值。</summary>
    static CultureInfo SystemCulture()
    {
        try
        {
            var sb = new System.Text.StringBuilder(85);
            if (GetUserDefaultLocaleName(sb, sb.Capacity) > 0) return new CultureInfo(sb.ToString(), useUserOverride: true);
        }
        catch { }
        return CultureInfo.CurrentCulture;
    }

    static readonly CultureInfo Zh = new("zh-CN");

    public void Tick()
    {
        var culture = SystemCulture();
        var now = DateTime.Now;
        var dtf = culture.DateTimeFormat;
        // 时间：12/24 小时制跟随系统；日期：统一中文显示
        TimeText.Text = now.ToString(ShowSeconds ? dtf.LongTimePattern : dtf.ShortTimePattern, culture);
        if (L.IsZh) DateText.Text = $"{now.ToString("yyyy年M月d日", Zh)}  {now.ToString("dddd", Zh)}";
        else
        {
            // 其他语言：用该语言的长日期写法，没带星期就补上
            var c = L.Culture;
            var d = now.ToString(c.DateTimeFormat.LongDatePattern, c);
            DateText.Text = c.DateTimeFormat.LongDatePattern.Contains("dddd") ? d : $"{d}  {now.ToString("dddd", c)}";
        }
        ZoneText.Text = L.F("跟随系统时区 · {0}", TimeZoneInfo.Local.DisplayName);
        NoteText.Text = Note.Trim();
        NoteText.Visibility = NoteText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 对齐到下一个整分钟（或整秒），避免空转
        var next = ShowSeconds ? 1000 - now.Millisecond : (60 - now.Second) * 1000 - now.Millisecond;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, next + 20));
        _timer.Start();
    }
}
