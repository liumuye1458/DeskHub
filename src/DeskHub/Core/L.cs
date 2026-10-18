using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DeskHub.Core;

/// <summary>
/// 界面多语言。原文就是中文：L.T("设置") 在中文下原样返回，其他语言查 Lang/xx.json（中文 → 译文），查不到就显示中文。
/// L.F("共 {0} 个", n) 是带参数的。XAML 里写死的中文在窗口加载时统一翻译（Apply）。
/// 语言在 设置 → 常规 里选，默认跟随 Windows 显示语言；改了重启生效。
/// </summary>
public static class L
{
    public static readonly (string Code, string Name, string Culture)[] Languages =
    {
        ("zh", "简体中文", "zh-CN"), ("en", "English", "en-US"), ("id", "Bahasa Indonesia", "id-ID"), ("th", "ไทย", "th-TH"),
        ("vi", "Tiếng Việt", "vi-VN"), ("es", "Español", "es-ES"), ("ja", "日本語", "ja-JP"),
    };

    public static string Code { get; private set; } = "zh";
    public static bool IsZh => Code == "zh";
    /// <summary>当前语言的日期格式（只用来显示日期、星期，不影响算数）。</summary>
    public static CultureInfo Culture { get; private set; } = new("zh-CN");
    static Dictionary<string, string> _map = new();

    /// <summary>setting 为空表示跟随 Windows 显示语言。</summary>
    public static void Init(string? setting)
    {
        var code = string.IsNullOrEmpty(setting) ? FromSystem() : setting;
        if (Languages.All(l => l.Code != code)) code = "en";
        Code = code;
        var ci = (CultureInfo)new CultureInfo(Languages.First(l => l.Code == code).Culture).Clone();
        // 泰语默认是佛历（年份显示 2569），统一用公历，免得和文件时间对不上
        if (ci.DateTimeFormat.Calendar is not GregorianCalendar) ci.DateTimeFormat.Calendar = new GregorianCalendar();
        Culture = ci;
        _map = new();
        if (code == "zh") return;
        try
        {
            using var s = typeof(L).Assembly.GetManifestResourceStream($"DeskHub.Lang.{code}.json");
            if (s != null) _map = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
        }
        catch { _map = new(); }
    }

    public static string FromSystem()
    {
        var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return two switch { "zh" => "zh", "in" => "id", _ => Languages.Any(l => l.Code == two) ? two : "en" };
    }

    public static string T(string zh) => _map.TryGetValue(zh, out var t) && t.Length > 0 ? t : zh;

    public static string F(string zh, params object?[] args)
    {
        var fmt = T(zh);
        try { return string.Format(Culture, fmt, args); }
        catch (FormatException) { return string.Format(Culture, zh, args); } // 译文占位符写错时退回中文
    }

    // ---------------- XAML 里写死的文字 ----------------
    static bool _hooked;

    /// <summary>所有窗口加载时自动翻译里面写死的中文（只需调一次）。</summary>
    public static void HookWindows()
    {
        if (_hooked || IsZh) return;
        _hooked = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Apply((DependencyObject)s)));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => Apply((DependencyObject)s)));
    }

    /// <summary>XAML 里的文字前后可能带空白、换行：去掉空白再查。</summary>
    static string TT(string s)
    {
        var t = s.Trim();
        if (t.Length == 0) return s;
        var r = T(t);
        return ReferenceEquals(r, t) || r == t ? s : s.Replace(t, r);
    }

    static bool HasZh(string? s) => s != null && s.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    public static void Apply(DependencyObject root)
    {
        if (IsZh) return;
        var seen = new HashSet<DependencyObject>();
        Walk(root, seen);
    }

    static void Walk(DependencyObject d, HashSet<DependencyObject> seen)
    {
        if (!seen.Add(d)) return;
        try { TranslateOne(d, seen); } catch { /* 个别控件改不了就跳过，不影响其他的 */ }
        foreach (var child in LogicalTreeHelper.GetChildren(d).Cast<object>().ToList())
            if (child is DependencyObject cd) Walk(cd, seen);
        if (d is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), seen);
        }
    }

    static void TranslateOne(DependencyObject d, HashSet<DependencyObject> seen)
    {
        switch (d)
        {
            case Window w when HasZh(w.Title): w.Title = TT(w.Title); break;
            case TextBlock tb:
                if (tb.Inlines.Count > 0 && tb.Inlines.All(i => i is Run or LineBreak) && tb.Inlines.Count(i => i is Run) > 1)
                {
                    foreach (var r in tb.Inlines.OfType<Run>().ToList()) if (HasZh(r.Text)) r.Text = TT(r.Text);
                }
                else if (HasZh(tb.Text) && System.Windows.Data.BindingOperations.GetBinding(tb, TextBlock.TextProperty) == null) tb.Text = TT(tb.Text);
                break;
            case HeaderedItemsControl hi when hi.Header is string h && HasZh(h): hi.Header = TT(h); break;
            case HeaderedContentControl hc when hc.Header is string h2 && HasZh(h2): hc.Header = TT(h2); break;
        }
        if (d is ContentControl cc && cc.Content is string c && HasZh(c)) cc.Content = TT(c);
        if (d is FrameworkElement fe && fe.ToolTip is string tip && HasZh(tip)) fe.ToolTip = TT(tip);
        if (d is FrameworkElement fe2 && fe2.ContextMenu is { } cm) Walk(cm, seen);

    }
}
