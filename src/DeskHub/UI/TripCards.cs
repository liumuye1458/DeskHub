using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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

    /// <summary>卡片内容（不带外框）。dark：日历上的半透明深色；否则白底窗口里用。</summary>
    public static FrameworkElement Body(Trip t, bool dark, bool showDate = false)
    {
        var th = dark ? Dark : Light;
        var accent = ColorOf(t, dark);
        var sp = new StackPanel();
        if (showDate)
            sp.Children.Add(T(CalText.Short(t.Day), 11, th.Sub));

        if (t.IsTransport)
        {
            // 第一行：✈ SQ951 · 新加坡航空
            var top = string.Join(" · ", new[] { t.No, t.Carrier }.Where(s => s.Length > 0));
            var head = new TextBlock { FontSize = 10.5, Foreground = B(th.Sub), TextTrimming = TextTrimming.CharacterEllipsis };
            head.Inlines.Add(new System.Windows.Documents.Run(t.Icon + " ") { Foreground = B(accent) });
            head.Inlines.Add(new System.Windows.Documents.Run(top.Length > 0 ? top : (t.Kind == Trip.Flight ? L.T("航班") : L.T("火车"))));
            sp.Children.Add(head);

            // 第二行：07:45 ——— 10:35+1
            var g = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition());
            void Put(UIElement e, int r, int c) { Grid.SetRow(e, r); Grid.SetColumn(e, c); g.Children.Add(e); }
            Put(T(t.Time.Length > 0 ? t.Time : "--:--", 16, th.Ink, true), 0, 0);
            var arr = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = B(th.Ink), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            arr.Inlines.Add(new System.Windows.Documents.Run(t.EndTime.Length > 0 ? t.EndTime : "--:--"));
            if (t.PlusDays.Length > 0) arr.Inlines.Add(new System.Windows.Documents.Run(t.PlusDays) { FontSize = 10, BaselineAlignment = BaselineAlignment.Superscript, Foreground = B(accent) });
            Put(arr, 0, 2);
            // 中间：一条线，线上一个小图标
            var mid = new Grid { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new Rectangle { Height = 1, Fill = B(th.Line), VerticalAlignment = VerticalAlignment.Center });
            mid.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = B(th.Line), HorizontalAlignment = HorizontalAlignment.Left });
            mid.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = B(accent), HorizontalAlignment = HorizontalAlignment.Right });
            Put(mid, 0, 1);
            // 第三行：城市（左右各占一半，名字长就省略，不会叠在一起）
            var toCity = T(t.To.Length > 0 ? t.To : "?", 12, th.Ink); toCity.HorizontalAlignment = HorizontalAlignment.Right;
            var cities = new UniformGrid2(T(t.From.Length > 0 ? t.From : "?", 12, th.Ink), toCity);
            Grid.SetColumnSpan(cities, 3);
            Put(cities, 1, 0);
            // 第四行：机场 / 航站楼（有才写）
            if (t.FromAt.Length > 0 || t.ToAt.Length > 0)
            {
                var fa = T(t.FromAt, 10, th.Sub); var ta = T(t.ToAt, 10, th.Sub); ta.HorizontalAlignment = HorizontalAlignment.Right; ta.TextAlignment = TextAlignment.Right;
                // 两边各占一半，太长就省略
                var two = new UniformGrid2(fa, ta);
                Grid.SetColumnSpan(two, 3);
                Put(two, 2, 0);
            }
            sp.Children.Add(g);
            if (t.Text.Length > 0) sp.Children.Add(T(t.Text, 10.5, th.Sub));
        }
        else if (t.Kind == Trip.Hotel)
        {
            // 第一行：🏨 酒店名 ……… 4 晚
            var headRow = new DockPanel();
            if (t.Nights > 0)
            {
                var nights = T(L.F("{0} 晚", t.Nights), 10.5, accent, true); nights.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(nights, Dock.Right); headRow.Children.Add(nights);
            }
            var head = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = B(th.Ink), TextTrimming = TextTrimming.CharacterEllipsis };
            head.Inlines.Add(new System.Windows.Documents.Run(t.Icon + " ") { Foreground = B(accent) });
            head.Inlines.Add(new System.Windows.Documents.Run(t.Title.Length > 0 ? t.Title : L.T("酒店")));
            headRow.Children.Add(head);
            sp.Children.Add(headRow);
            if (t.Nights > 0)
            {
                // 和机票一个样子：11月6日 ──── 11月10日，下面写星期和入住 / 退房
                var g = new Grid { Margin = new Thickness(0, 3, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.RowDefinitions.Add(new RowDefinition());
                g.RowDefinitions.Add(new RowDefinition());
                void Put(UIElement e, int r, int c) { Grid.SetRow(e, r); Grid.SetColumn(e, c); g.Children.Add(e); }
                Put(T(CalText.MonthDay(t.Day), 14, th.Ink, true), 0, 0);
                var outDay = T(CalText.MonthDay(t.LastDay), 14, th.Ink, true); outDay.HorizontalAlignment = HorizontalAlignment.Right;
                Put(outDay, 0, 2);
                var mid = new Grid { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                mid.Children.Add(new Rectangle { Height = 1, Fill = B(th.Line), VerticalAlignment = VerticalAlignment.Center });
                mid.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = B(th.Line), HorizontalAlignment = HorizontalAlignment.Left });
                mid.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = B(accent), HorizontalAlignment = HorizontalAlignment.Right });
                Put(mid, 0, 1);
                var outSub = T(CalText.Weekday(t.LastDay) + " " + L.T("退房") + (t.EndTime.Length > 0 ? " " + t.EndTime : ""), 10, th.Sub); outSub.HorizontalAlignment = HorizontalAlignment.Right;
                var subs = new UniformGrid2(T(CalText.Weekday(t.Day) + " " + L.T("入住") + (t.Time.Length > 0 ? " " + t.Time : ""), 10, th.Sub), outSub);
                Grid.SetColumnSpan(subs, 3);
                Put(subs, 1, 0);
                sp.Children.Add(g);
            }
            else
            {
                var sub = T(L.T("入住") + " " + CalText.Short(t.Day) + (t.Time.Length > 0 ? " " + t.Time : ""), 10.5, th.Sub);
                sub.Margin = new Thickness(0, 2, 0, 0);
                sp.Children.Add(sub);
            }
            if (t.Text.Length > 0) sp.Children.Add(T(t.Text, 10.5, th.Sub));
        }
        else
        {
            var dp = new DockPanel();
            if (t.Time.Length > 0)
            {
                var tm = T(t.Time + (t.EndTime.Length > 0 ? "–" + t.EndTime : ""), 12, accent, true);
                tm.Margin = new Thickness(0, 0, 8, 0); tm.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(tm, Dock.Left);
                dp.Children.Add(tm);
            }
            var tx = T(t.Text, 12.5, th.Ink); tx.TextWrapping = TextWrapping.Wrap; tx.TextTrimming = TextTrimming.None; tx.MaxHeight = 38;
            dp.Children.Add(tx);
            sp.Children.Add(dp);
            if (t.Place.Length > 0) sp.Children.Add(T("@ " + t.Place, 10.5, th.Sub));
        }
        return sp;
    }

    /// <summary>带外框的卡片：左边一条颜色，浅底，悬停变亮。</summary>
    public static Border Card(Trip t, bool dark, bool showDate = false)
    {
        var accent = ColorOf(t, dark);
        var inner = new Border { Padding = new Thickness(8, 6, 8, 7), Child = Body(t, dark, showDate) };
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

    /// <summary>两段文字左右各占一半（机场名太长时各自省略，不会挤到一起）。</summary>
    sealed class UniformGrid2 : Grid
    {
        public UniformGrid2(UIElement a, UIElement b)
        {
            ColumnDefinitions.Add(new ColumnDefinition()); ColumnDefinitions.Add(new ColumnDefinition());
            SetColumn(a, 0); SetColumn(b, 1);
            Children.Add(a); Children.Add(b);
        }
    }
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
