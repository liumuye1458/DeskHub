using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>
/// 行程卡片：机票 / 火车 / 酒店 / 普通日程各一个固定模板，日历列表、识别结果、编辑框里长得一样。
/// 机票像登机牌：两边大字是出发、到达时间，下面是城市，中间一条线；一眼看清从哪到哪、几点。
/// </summary>
public static class TripCard
{
    static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    /// <summary>每类一个颜色：日历格子、卡片左边的色条、图标都用它。</summary>
    public static string ColorOf(Trip t, bool dark = true) => t.Kind switch
    {
        Trip.Flight => dark ? "#5AA2FF" : "#2F6FDB",
        Trip.Train => dark ? "#3FD0C0" : "#0F8C7E",
        Trip.Hotel => dark ? "#B79CFF" : "#7650D8",
        _ => dark ? "#FFC266" : "#C27A00",
    };

    sealed record Theme(string Ink, string Sub, string Line);
    static readonly Theme Dark = new("#FFFFFF", "#B8FFFFFF", "#40FFFFFF");
    static readonly Theme Light = new("#222222", "#888888", "#D0D5DD");

    static TextBlock T(string s, double size, string color, bool bold = false) => new()
    {
        Text = s, FontSize = size, Foreground = B(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// 卡片内容（不带外框）。dark：日历上的半透明深色；否则白底窗口里用。
    /// 排法照着飞猪订单：第一行大字写“从哪到哪 / 住哪 / 干什么”，下面一行写时间和航班号，再下面写机场、备注。
    /// </summary>
    public static FrameworkElement Body(Trip t, bool dark, bool showDate = false)
    {
        var th = dark ? Dark : Light;
        var accent = ColorOf(t, dark);
        var sp = new StackPanel();
        TextBlock Line(double size, string color, double top = 2) => new()
        {
            FontSize = size, Foreground = B(color), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, top, 0, 0),
        };
        Run R(string s, string? color = null, bool bold = false)
        {
            var r = new Run(s);
            if (color != null) r.Foreground = B(color); // 不给颜色就跟着这一行的颜色
            if (bold) r.FontWeight = FontWeights.SemiBold;
            return r;
        }
        string day = showDate ? CalText.Short(t.Day) + "  " : "";

        // 第一行：标题（大字）；酒店右边写几晚
        var head = new DockPanel();
        if (t.Kind == Trip.Hotel && t.Nights > 0)
        {
            var n = T(L.F("{0} 晚", t.Nights), 11.5, accent, true); n.Margin = new Thickness(8, 0, 0, 0); n.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(n, Dock.Right); head.Children.Add(n);
        }
        var title = Line(14, th.Ink, 0);
        title.FontWeight = FontWeights.SemiBold;
        if (t.Kind.Length > 0)
        {
            title.Inlines.Add(R(t.Icon + " ", accent));
            title.Inlines.Add(R(t.Title.Length > 0 ? t.Title : t.Kind == Trip.Flight ? L.T("航班") : t.Kind == Trip.Train ? L.T("火车") : L.T("酒店")));
        }
        else
        {
            // 日程：时间用颜色标出来，事情长就折行（最多三行）
            if (t.Time.Length > 0) title.Inlines.Add(R(t.Time + (t.EndTime.Length > 0 ? "–" + t.EndTime : "") + "  ", accent));
            title.Inlines.Add(R(t.Text));
            title.TextWrapping = TextWrapping.Wrap; title.MaxHeight = 62;
        }
        head.Children.Add(title);
        sp.Children.Add(head);

        if (t.IsTransport)
        {
            // 第二行：13:55 – 18:20⁺¹ · 9C7341 春秋航空
            var meta = Line(11.5, th.Sub, 3);
            if (day.Length > 0) meta.Inlines.Add(R(day));
            if (t.Time.Length > 0 || t.EndTime.Length > 0)
            {
                meta.Inlines.Add(R((t.Time.Length > 0 ? t.Time : "?") + (t.EndTime.Length > 0 ? " – " + t.EndTime : ""), th.Ink, true));
                if (t.PlusDays.Length > 0) meta.Inlines.Add(new Run(t.PlusDays) { FontSize = 9, BaselineAlignment = BaselineAlignment.Superscript, Foreground = B(accent) });
            }
            var who = string.Join(" ", new[] { t.No, t.Carrier }.Where(s => s.Length > 0));
            if (who.Length > 0) meta.Inlines.Add(R((meta.Inlines.Count > 0 ? "  ·  " : "") + who));
            if (meta.Inlines.Count > 0) sp.Children.Add(meta);
            // 第三行：宝安国际机场 T3 → 苏加诺-哈达 T3
            if (t.FromAt.Length > 0 || t.ToAt.Length > 0)
                sp.Children.Add(T((t.FromAt.Length > 0 ? t.FromAt + " " : "") + "→" + (t.ToAt.Length > 0 ? " " + t.ToAt : ""), 11, th.Sub).Also(x => x.Margin = new Thickness(0, 2, 0, 0)));
        }
        else if (t.Kind == Trip.Hotel)
        {
            // 第二行：11月6日 周五 15:00 入住 → 11月10日 周二 退房
            // 放不下时“→ 退房”整段挪到下一行，不会在“周二”中间断开
            var meta = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
            meta.Children.Add(T(CalText.Short(t.Day) + (t.Time.Length > 0 ? " " + t.Time : "") + " " + L.T("入住"), 11.5, th.Ink));
            if (t.Nights > 0) meta.Children.Add(T("  →  " + CalText.Short(t.LastDay) + (t.EndTime.Length > 0 ? " " + t.EndTime : "") + " " + L.T("退房"), 11.5, th.Ink));
            sp.Children.Add(meta);
        }
        else if (day.Length > 0 || t.Place.Length > 0)
        {
            sp.Children.Add(T(day + (t.Place.Length > 0 ? "@ " + t.Place : ""), 11.5, th.Sub).Also(x => x.Margin = new Thickness(0, 3, 0, 0)));
        }
        if (t.Kind.Length > 0 && t.Text.Length > 0) sp.Children.Add(T(t.Text, 11, th.Sub).Also(x => x.Margin = new Thickness(0, 2, 0, 0)));
        return sp;
    }

    /// <summary>带外框的卡片：左边一条颜色，浅底，悬停变亮。</summary>
    public static Border Card(Trip t, bool dark, bool showDate = false)
    {
        var accent = ColorOf(t, dark);
        var inner = new Border { Padding = new Thickness(10, 7, 10, 8), Child = Body(t, dark, showDate) };
        var bar = new Border { Width = 3, Background = B(accent), CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 4, 0, 4) };
        var dp = new DockPanel();
        DockPanel.SetDock(bar, Dock.Left);
        dp.Children.Add(bar); dp.Children.Add(inner);
        return new Border
        {
            Child = dp, CornerRadius = new CornerRadius(6), Background = B(dark ? "#1AFFFFFF" : "#F6F8FB"),
            BorderBrush = B(dark ? "#00FFFFFF" : "#E6EAF0"), BorderThickness = new Thickness(1),
        };
    }

    static TextBlock Also(this TextBlock x, Action<TextBlock> set) { set(x); return x; }
}

/// <summary>
/// 改一条行程的表单：上面选类型（日程 / 航班 / 火车 / 酒店），下面是这一类固定的几格。
/// 日历的编辑框和识别结果里“改”都用它。
/// </summary>
public sealed class TripForm : StackPanel
{
    static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    readonly Trip _t;
    readonly bool _showDate;
    readonly StackPanel _fields = new();
    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    readonly Dictionary<string, TextBox> _box = new();
    /// <summary>任何一格改了。</summary>
    public event Action? Changed;
    /// <summary>在最后一格按了回车。</summary>
    public event Action? Submit;

    static readonly (string Kind, string Name)[] Kinds = { ("", "日程"), (Trip.Flight, "航班"), (Trip.Train, "火车"), (Trip.Hotel, "酒店") };

    public TripForm(Trip t, bool showDate)
    {
        _t = t.Clone();
        _showDate = showDate;
        Children.Add(_tabs);
        Children.Add(_fields);
        Build();
    }

    void Build()
    {
        _tabs.Children.Clear();
        foreach (var (kind, name) in Kinds)
        {
            bool on = _t.Kind == kind;
            var tab = new Border
            {
                Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 4, 0), CornerRadius = new CornerRadius(11), Cursor = Cursors.Hand,
                Background = B(on ? TripCard.ColorOf(new Trip { Kind = kind }, false) : "#F0F2F5"),
                Child = new TextBlock { Text = L.T(name), FontSize = 12, Foreground = on ? Brushes.White : B("#555555") },
            };
            var k = kind;
            tab.MouseLeftButtonUp += (_, _) => { if (_t.Kind == k) return; Read(); _t.Kind = k; Build(); Changed?.Invoke(); FocusFirst(); };
            _tabs.Children.Add(tab);
        }

        _fields.Children.Clear();
        _box.Clear();
        if (_showDate) Row((L.T("日期"), nameof(Trip.Date), _t.Date, 96, "2026-11-06"));
        switch (_t.Kind)
        {
            case Trip.Flight or Trip.Train:
                Row((_t.Kind == Trip.Flight ? L.T("航班号") : L.T("车次"), nameof(Trip.No), _t.No, 70, _t.Kind == Trip.Flight ? "SQ951" : "G1234"),
                    (null, nameof(Trip.Carrier), _t.Carrier, double.NaN, _t.Kind == Trip.Flight ? L.T("航空公司") : L.T("铁路 / 客运公司")));
                Row((L.T("出发"), nameof(Trip.From), _t.From, 72, L.T("城市")), (null, nameof(Trip.Time), _t.Time, 52, "07:45"), (null, nameof(Trip.FromAt), _t.FromAt, double.NaN, _t.Kind == Trip.Flight ? L.T("机场 / 航站楼") : L.T("车站")));
                Row((L.T("到达"), nameof(Trip.To), _t.To, 72, L.T("城市")), (null, nameof(Trip.EndTime), _t.EndTime, 52, "10:35"), (null, nameof(Trip.ToAt), _t.ToAt, double.NaN, _t.Kind == Trip.Flight ? L.T("机场 / 航站楼") : L.T("车站")));
                Row((L.T("备注"), nameof(Trip.Text), _t.Text, double.NaN, L.T("座位、舱位等，可不填")));
                break;
            case Trip.Hotel:
                Row((L.T("酒店"), nameof(Trip.Place), _t.Place, double.NaN, L.T("酒店名")));
                Row((L.T("入住"), nameof(Trip.Time), _t.Time, 52, "15:00"), (L.T("住几晚"), "Nights", _t.Nights > 0 ? _t.Nights.ToString() : "", 40, "1"));
                Row((L.T("备注"), nameof(Trip.Text), _t.Text, double.NaN, L.T("房型等，可不填")));
                break;
            default:
                Row((L.T("时间"), nameof(Trip.Time), _t.Time, 52, "15:00"), (L.T("到"), nameof(Trip.EndTime), _t.EndTime, 52, L.T("可不填")));
                Row((L.T("事情"), nameof(Trip.Text), _t.Text, double.NaN, L.T("例如：和客户吃饭")));
                Row((L.T("地点"), nameof(Trip.Place), _t.Place, double.NaN, L.T("可不填")));
                break;
        }
    }

    /// <summary>一行几格：(标签, 字段, 值, 宽度, 提示)。宽度 NaN = 占满剩下的。</summary>
    void Row(params (string? Label, string Key, string Value, double W, string Hint)[] cells)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 7, 0, 0), LastChildFill = true };
        for (int i = 0; i < cells.Length; i++)
        {
            var (label, key, value, w, hint) = cells[i];
            if (label != null)
                Add(dp, new TextBlock { Text = label, Width = i == 0 ? (L.IsZh ? 46 : 70) : double.NaN, Margin = new Thickness(i == 0 ? 0 : 6, 0, 4, 0), Foreground = B("#666666"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = label }, true);
            var box = new TextBox { Style = Res<Style>("WinTextBox"), Text = value, Height = 26, FontSize = 12.5, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, i < cells.Length - 1 ? 4 : 0, 0) };
            if (!double.IsNaN(w)) box.Width = w;
            var ph = new TextBlock { Text = hint, Foreground = B("#B5B5B5"), FontSize = 12, IsHitTestVisible = false, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var g = new Grid();
            if (!double.IsNaN(w)) g.Width = w;
            g.Margin = box.Margin; box.Margin = new Thickness(0);
            g.Children.Add(box); g.Children.Add(ph);
            ph.Visibility = value.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            box.TextChanged += (_, _) => { ph.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Read(); Changed?.Invoke(); };
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Read(); Submit?.Invoke(); } };
            _box[key] = box;
            Add(dp, g, i < cells.Length - 1 || !double.IsNaN(w));
        }
        _fields.Children.Add(dp);

        static void Add(DockPanel dp, UIElement e, bool left) { if (left) DockPanel.SetDock(e, Dock.Left); dp.Children.Add(e); }
    }

    /// <summary>把格子里的字读回 _t。</summary>
    void Read()
    {
        foreach (var (key, box) in _box)
        {
            var v = box.Text.Trim();
            if (key == "Nights")
            {
                _t.EndDate = int.TryParse(v, out var n) && n > 0 && n < 400 ? TripStore.Key(_t.Day.AddDays(n)) : "";
                continue;
            }
            if (key == nameof(Trip.Date))
            {
                if (DateTime.TryParseExact(v, new[] { "yyyy-MM-dd", "yyyy-M-d", "yyyy/M/d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                {
                    // 改了日子：结束日跟着平移
                    var span = _t.EndDate.Length > 0 ? (_t.LastDay - _t.Day).Days : 0;
                    _t.Date = TripStore.Key(d);
                    if (span > 0) _t.EndDate = TripStore.Key(d.AddDays(span));
                }
                continue;
            }
            typeof(Trip).GetProperty(key)!.SetValue(_t, v);
        }
        if (_t.Kind == "" && _box.ContainsKey(nameof(Trip.Text)) && _box[nameof(Trip.Time)].Text.Trim().Length == 0)
        {
            // 普通日程里直接写“15:00 和客户吃饭”也认
            var (time, text) = Trip.Split(_t.Text);
            if (time.Length > 0) { _t.Time = time; _t.Text = text; }
        }
    }

    /// <summary>日期填得对不对（显示日期那一格时才可能不对）。</summary>
    public bool DateOk => !_box.TryGetValue(nameof(Trip.Date), out var b) || DateTime.TryParseExact(b.Text.Trim(), new[] { "yyyy-MM-dd", "yyyy-M-d", "yyyy/M/d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>当前填的内容（整理过）。</summary>
    public Trip Value { get { Read(); var t = _t.Clone(); t.Tidy(); return t; } }

    public void FocusFirst()
    {
        var first = _box.Where(kv => kv.Key != nameof(Trip.Date)).Select(kv => kv.Value).FirstOrDefault();
        if (first == null) return;
        Dispatcher.BeginInvoke(() => { first.Focus(); first.CaretIndex = first.Text.Length; }, System.Windows.Threading.DispatcherPriority.Input);
    }
}
