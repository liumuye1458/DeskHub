using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>
/// 日历：点桌面时钟（或工具条、快捷键）弹出来，点别处就收起。
/// 选日子：周末浅灰、印尼红日子整格标红、中国放假/调休标“休/班”，鼠标放上去看“距今天几天”，拖一段看“共几天”。
/// 记行程：点一天写一句话，右边列出接下来的行程和倒计时，可以提前一天在桌面弹便签提醒。
/// </summary>
public sealed class CalendarWindow : Window
{
    const string Green = "#07C255", GreenD = "#07A548", Ink = "#222222", Sub = "#666666", Mute = "#999999", LineC = "#ECECEC", Soft = "#F6F7F8",
        Red = "#E0524C", Blue = "#3A7BE0", BlueSoft = "#E8F1FF", WeekendBg = "#F7F9FB", LiburBg = "#FBDAD6", CutiBg = "#FDECE9", Amber = "#C98316";
    const double CellW = 82, CellH = 72, SideW = 250, Shadow = 16;
    static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    /// <summary>测试用：把“今天”定成某一天。</summary>
    public static DateTime? TodayOverride;
    static DateTime Today => (TodayOverride ?? DateTime.Now).Date;

    readonly AppConfig _cfg;
    DateTime _view;                         // 正在看的月份（1 号）
    DateTime? _selA, _selB, _hover, _picked;
    bool _dragging;

    readonly TextBlock _ym = new() { FontSize = 20, FontWeight = FontWeights.Bold, Foreground = B(Ink), MinWidth = 140, VerticalAlignment = VerticalAlignment.Center };
    readonly WrapPanel _legend = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    readonly UniformGrid _week = new() { Columns = 7, Rows = 1, Width = CellW * 7, Margin = new Thickness(0, 0, 0, 4) };
    readonly UniformGrid _grid = new() { Columns = 7, Rows = 6, Width = CellW * 7, Height = CellH * 6, Background = Brushes.Transparent };
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = B(Sub) };
    readonly TextBlock _statusHint = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, MaxWidth = 230, Foreground = B(Mute), FontSize = 11.5, Margin = new Thickness(10, 0, 0, 0) };

    readonly Border _editor;
    readonly TextBlock _edWhen = new() { FontWeight = FontWeights.SemiBold, Foreground = B(Ink) };
    readonly TextBlock _edSub = new() { Foreground = B(Mute), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    readonly TextBox _edText;
    readonly TextBlock _edPlaceholder = new() { Foreground = B("#AAAAAA"), IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(9, 7, 9, 0), FontSize = 12.5 };
    readonly CheckBox _edRemind;
    readonly Button _edDel;
    readonly StackPanel _items = new();
    readonly Path _pointer = new() { Fill = Brushes.White, Data = Geometry.Parse("M0,9 L9,0 L18,9 Z"), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };

    public CalendarWindow(AppConfig cfg)
    {
        _cfg = cfg;
        _view = new DateTime(Today.Year, Today.Month, 1);
        Title = L.T("日历");
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"); FontSize = 13; UseLayoutRounding = true;

        _edText = new TextBox
        {
            Style = Res<Style>("WinTextBox"), Height = 54, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 12.5, MaxLength = 120,
        };
        _edText.TextChanged += (_, _) => _edPlaceholder.Visibility = _edText.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _edText.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SaveTrip(); e.Handled = true; } };
        _edRemind = new CheckBox { Style = Res<Style>("Toggle"), Content = new TextBlock { Text = L.T("提前一天在桌面弹便签提醒"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = B(Sub) }, Margin = new Thickness(0, 8, 0, 0) };
        _edDel = new Button { Content = L.T("删除"), Style = Res<Style>("BtnW"), Foreground = B(Red), Padding = new Thickness(12, 3, 12, 3) };
        _edDel.Click += (_, _) => { if (_picked is { } d) TripStore.Set(d, "", false); CloseEditor(); };
        _editor = BuildEditor();

        var main = BuildMain();
        var side = BuildSide();
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SideW) });
        root.Children.Add(main);
        Grid.SetColumn(side, 1);
        root.Children.Add(side);

        var card = new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(12), Child = root, ClipToBounds = false,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.28, RenderingBias = RenderingBias.Performance },
        };
        var outer = new Grid { Margin = new Thickness(Shadow) };
        outer.Children.Add(card);
        _pointer.Margin = new Thickness(32, -8, 0, 0);
        outer.Children.Add(_pointer);
        Content = new Grid { Children = { outer } }; // 外面再包一层：阴影留白也算在内容里（截图测试按内容大小画）

        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (IsVisible && !_keepOpen) Close(); };
        TripStore.Changed += Render;
        Closed += (_, _) => TripStore.Changed -= Render;
        Render();
    }

    /// <summary>设置改了（农历、节假日开关、一周从哪天开始）：重画。</summary>
    public void Refresh() => Render();

    bool _keepOpen;
    /// <summary>测试截图时不因为失去焦点就关掉。</summary>
    public void KeepOpenForTest() => _keepOpen = true;

    /// <summary>放到时钟下面（带一个指向时钟的小尖角）；下面放不下就放到上面。anchor 为空时放在屏幕中间。</summary>
    public void ShowNear(Rect? anchor, Rect workArea)
    {
        Opacity = 0;
        Show();
        UpdateLayout();
        double w = ActualWidth, h = ActualHeight;
        if (anchor is { } a)
        {
            double x = a.Left - Shadow + 4, y = a.Bottom - Shadow + 10;
            bool below = y + h - Shadow <= workArea.Bottom;
            if (!below) y = a.Top - h + Shadow - 6;
            _pointer.Visibility = below ? Visibility.Visible : Visibility.Collapsed;
            Left = Math.Clamp(x, workArea.Left - Shadow, Math.Max(workArea.Left - Shadow, workArea.Right - w + Shadow));
            Top = Math.Clamp(y, workArea.Top - Shadow, Math.Max(workArea.Top - Shadow, workArea.Bottom - h + Shadow));
            _pointer.Margin = new Thickness(Math.Clamp(a.Left + 24 - Left - Shadow, 18, w - 2 * Shadow - 40), -8, 0, 0);
        }
        else
        {
            Left = workArea.Left + (workArea.Width - w) / 2;
            Top = workArea.Top + Math.Max(0, (workArea.Height - h) / 3);
        }
        Opacity = 1;
        Activate();
    }

    // ================= 左：月历 =================
    FrameworkElement BuildMain()
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_ym);
        left.Children.Add(NavBtn("‹", () => Go(-1), L.T("上个月（PageUp）")));
        left.Children.Add(NavBtn("›", () => Go(1), L.T("下个月（PageDown）")));
        var today = new Border
        {
            BorderBrush = B(LineC), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 3, 12, 4),
            Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.White,
            Child = new TextBlock { Text = L.T("今天"), Foreground = B(Sub) },
        };
        today.MouseEnter += (_, _) => today.BorderBrush = B(Green);
        today.MouseLeave += (_, _) => today.BorderBrush = B(LineC);
        today.MouseLeftButtonUp += (_, _) => GoToday();
        left.Children.Add(today);
        DockPanel.SetDock(left, Dock.Left);
        head.Children.Add(left);
        head.Children.Add(_legend);

        _grid.MouseLeftButtonDown += (_, e) =>
        {
            if (DayAt(e.GetPosition(_grid)) is not { } d) return;
            _dragging = true; _selA = _selB = d;
            _grid.CaptureMouse();
            Render();
        };
        _grid.MouseMove += (_, e) =>
        {
            var d = DayAt(e.GetPosition(_grid));
            if (d == null || d == _hover) return;
            _hover = d;
            if (_dragging && d != _selB) { _selB = d; Render(); }
            else RenderStatus();
        };
        _grid.MouseLeftButtonUp += (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false;
            _grid.ReleaseMouseCapture();
            if (_selA is { } a && _selA == _selB) { _selA = _selB = null; OpenEditor(a); }
        };
        _grid.MouseLeave += (_, _) => { if (!_dragging) { _hover = null; RenderStatus(); } };
        _grid.MouseWheel += (_, e) => Go(e.Delta < 0 ? 1 : -1);

        var hint = new DockPanel();
        DockPanel.SetDock(_statusHint, Dock.Right);
        hint.Children.Add(_statusHint);
        hint.Children.Add(_status);
        var status = new Border { Background = B(Soft), CornerRadius = new CornerRadius(8), Height = L.IsZh ? 34 : 50, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 8, 0, 0), Child = hint };

        // 宽度定死跟格子一样：外文图例长，放不下就折到第二行，不把整张卡片撑宽
        var sp = new StackPanel { Margin = new Thickness(18, 16, 18, 12), Width = CellW * 7 };
        sp.Children.Add(head);
        sp.Children.Add(_week);
        sp.Children.Add(_grid);
        sp.Children.Add(status);
        return sp;
    }

    FrameworkElement NavBtn(string glyph, Action run, string tip)
    {
        var b = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = tip,
            Child = new TextBlock { Text = glyph, FontSize = 20, Foreground = B(Sub), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -3, 0, 0) },
        };
        b.MouseEnter += (_, _) => b.Background = B(Soft);
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => run();
        return b;
    }

    DateTime? DayAt(Point p)
    {
        int col = (int)(p.X / CellW), row = (int)(p.Y / CellH);
        if (col < 0 || col > 6 || row < 0 || row > 5) return null;
        return FirstCell().AddDays(row * 7 + col);
    }

    DayOfWeek WeekStart => _cfg.CalMondayFirst ? DayOfWeek.Monday : DayOfWeek.Sunday;
    DateTime FirstCell() => _view.AddDays(-(((int)_view.DayOfWeek - (int)WeekStart + 7) % 7));

    void Go(int months) { _view = _view.AddMonths(months); Render(); }
    void GoToday() { _view = new DateTime(Today.Year, Today.Month, 1); _selA = _selB = null; Render(); }

    // ================= 右：行程 =================
    Border BuildEditor()
    {
        var box = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        box.Children.Add(_edText);
        box.Children.Add(_edPlaceholder);
        _edPlaceholder.Text = L.T("写一句就行，例如：08:30 雅加达 → 广州 CZ386");

        var cancel = new Button { Content = L.T("取消"), Style = Res<Style>("BtnW"), Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => CloseEditor();
        var save = new Button { Content = L.T("保存"), Style = Res<Style>("BtnG"), Padding = new Thickness(14, 3, 14, 3) };
        save.Click += (_, _) => SaveTrip();
        var btns = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(_edDel, Dock.Left);
        btns.Children.Add(_edDel);
        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        rightBtns.Children.Add(cancel); rightBtns.Children.Add(save);
        btns.Children.Add(rightBtns);

        var sp = new StackPanel();
        sp.Children.Add(_edWhen); sp.Children.Add(_edSub); sp.Children.Add(box); sp.Children.Add(_edRemind); sp.Children.Add(btns);
        return new Border
        {
            Background = Brushes.White, BorderBrush = B(Blue), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 12), Child = sp, Visibility = Visibility.Collapsed,
        };
    }

    FrameworkElement BuildSide()
    {
        var add = new TextBlock { Text = L.T("＋ 记一笔"), Foreground = B(Blue), Cursor = Cursors.Hand, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        add.MouseLeftButtonUp += (_, _) => OpenEditor(_hover ?? Today);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        head.Children.Add(new TextBlock { Text = L.T("接下来"), FontWeight = FontWeights.SemiBold, FontSize = 13.5, Foreground = B(Ink) });

        var scroll = new ScrollViewer { Content = _items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 470 };
        var sp = new StackPanel { Margin = new Thickness(14, 16, 14, 12) };
        sp.Children.Add(_editor);
        sp.Children.Add(head);
        sp.Children.Add(scroll);
        return new Border
        {
            Background = B("#FBFBFC"), BorderBrush = B(LineC), BorderThickness = new Thickness(1, 0, 0, 0), CornerRadius = new CornerRadius(0, 12, 12, 0), Child = sp,
        };
    }

    void OpenEditor(DateTime d)
    {
        _picked = d;
        var t = TripStore.Get(d);
        _edWhen.Text = CalText.Long(d);
        _edSub.Text = (_cfg.CalLunar && ChinaCal.InRange(d) ? LunarPrefix + ChinaCal.Of(d).Full + " · " : "") + Away((d - Today).Days);
        _edText.Text = t?.Text ?? "";
        _edRemind.IsChecked = t?.Remind ?? true;
        _edDel.Visibility = t != null ? Visibility.Visible : Visibility.Hidden;
        _editor.Visibility = Visibility.Visible;
        Render();
        Dispatcher.BeginInvoke(() => { _edText.Focus(); _edText.CaretIndex = _edText.Text.Length; }, System.Windows.Threading.DispatcherPriority.Input);
    }

    void CloseEditor()
    {
        _picked = null;
        _editor.Visibility = Visibility.Collapsed;
        Render();
    }

    void SaveTrip()
    {
        if (_picked is not { } d) return;
        TripStore.Set(d, _edText.Text, _edRemind.IsChecked == true);
        CloseEditor();
    }

    void OnKey(object s, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_selA != null) { _selA = _selB = null; Render(); }
                else if (_picked != null) CloseEditor();
                else Close();
                e.Handled = true;
                break;
            case Key.PageUp: Go(-1); e.Handled = true; break;
            case Key.PageDown: Go(1); e.Handled = true; break;
            case Key.Home when !_edText.IsKeyboardFocused: GoToday(); e.Handled = true; break;
        }
    }

    // ================= 画 =================
    void Render()
    {
        _ym.Text = CalText.Month(_view);

        _week.Children.Clear();
        for (int i = 0; i < 7; i++)
        {
            var w = (DayOfWeek)(((int)WeekStart + i) % 7);
            bool we = w is DayOfWeek.Saturday or DayOfWeek.Sunday;
            _week.Children.Add(new TextBlock
            {
                Text = CalText.WeekShort(w), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = B(we ? "#8A97A6" : Mute), FontWeight = we ? FontWeights.SemiBold : FontWeights.Normal,
            });
        }

        _legend.Children.Clear();
        if (_cfg.CalIndonesia) _legend.Children.Add(Legend(Swatch(LiburBg, null), L.T("印尼红日子")));
        if (_cfg.CalChina)
        {
            var two = new StackPanel { Orientation = Orientation.Horizontal };
            two.Children.Add(Badge(true, 10)); two.Children.Add(new Border { Width = 3 }); two.Children.Add(Badge(false, 10));
            _legend.Children.Add(Legend(two, L.T("中国")));
        }
        _legend.Children.Add(Legend(Swatch(WeekendBg, LineC), L.T("周末")));
        _legend.Children.Add(Legend(Swatch(BlueSoft, null), L.T("行程")));

        var first = FirstCell();
        DateTime? a = null, b = null;
        if (_selA is { } sa && _selB is { } sb) (a, b) = sa <= sb ? (sa, sb) : (sb, sa);
        _grid.Children.Clear();
        for (int i = 0; i < 42; i++) _grid.Children.Add(Cell(first.AddDays(i), a, b));

        RenderStatus();
        RenderItems();
    }

    static FrameworkElement Legend(FrameworkElement icon, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 1, 0, 1) };
        icon.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(icon);
        sp.Children.Add(new TextBlock { Text = text, Foreground = B(Mute), FontSize = 11.5, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    static Border Swatch(string fill, string? border) => new()
    {
        Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = B(fill),
        BorderBrush = border == null ? null : B(border), BorderThickness = new Thickness(border == null ? 0 : 1),
    };

    /// <summary>中国的“休”（绿底）/“班”（灰底）角标。</summary>
    static Border Badge(bool off, double size) => new()
    {
        Background = B(off ? GreenD : "#6B7280"), CornerRadius = new CornerRadius(4), Padding = new Thickness(3, 0, 3, 1),
        Child = new TextBlock { Text = off ? L.T("休") : L.T("班"), Foreground = Brushes.White, FontSize = size, FontWeight = FontWeights.Bold },
    };

    FrameworkElement Cell(DateTime d, DateTime? a, DateTime? b)
    {
        bool other = d.Month != _view.Month, isToday = d == Today, we = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var id = _cfg.CalIndonesia ? Holidays.Indonesia(d) : null;
        var cn = _cfg.CalChina ? Holidays.China(d) : null;
        var trip = TripStore.Get(d);

        var g = new Grid { Background = B(id is { } m ? (m.Off ? LiburBg : CutiBg) : we ? WeekendBg : "#FFFFFF") };
        g.Children.Add(new Border { BorderBrush = B(LineC), BorderThickness = new Thickness(0, 1, 0, 0) });

        var num = new Border
        {
            MinWidth = 30, Height = 26, CornerRadius = new CornerRadius(13), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Background = isToday ? B(Green) : Brushes.Transparent,
            Child = new TextBlock
            {
                Text = d.Day.ToString(), FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = isToday ? Brushes.White : B(id != null ? Red : Ink),
            },
        };
        g.Children.Add(num);

        if (_cfg.CalLunar && ChinaCal.InRange(d))
        {
            var fest = ChinaCal.Festival(d);
            g.Children.Add(new TextBlock
            {
                Text = fest ?? ChinaCal.Of(d).Cell, FontSize = 11, Margin = new Thickness(2, 34, 2, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Foreground = B(fest != null ? (id != null ? "#B8640F" : Amber) : id != null ? "#CF6F69" : Mute), FontWeight = fest != null ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        if (cn is { } c)
        {
            var badge = Badge(c.Off, L.IsZh ? 10 : 9);
            badge.HorizontalAlignment = HorizontalAlignment.Right; badge.VerticalAlignment = VerticalAlignment.Top; badge.Margin = L.IsZh ? new Thickness(0, 4, 5, 0) : new Thickness(0, 3, 3, 0);
            // 外文角标比“休/班”宽：日期数字往左让一点，免得压住
            if (!L.IsZh) num.Margin = new Thickness(-22, 6, 0, 0);
            g.Children.Add(badge);
        }

        if (trip != null)
            g.Children.Add(new Border
            {
                Background = B(BlueSoft), CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0, 4, 3), Padding = new Thickness(4, 0, 4, 1),
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = "✈ " + trip.Text, Foreground = B(Blue), FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis },
            });

        // 选中的一段：淡绿 + 上下绿线；红日子只描边，保持红色
        if (a is { } sa && b is { } sb && d >= sa && d <= sb)
            g.Children.Add(new Border
            {
                IsHitTestVisible = false, Background = id != null ? Brushes.Transparent : B("#1A07C255"), BorderBrush = B(Green),
                BorderThickness = new Thickness(d == sa ? 3 : 0, 2, d == sb ? 3 : 0, 2),
            });
        if (_picked == d)
            g.Children.Add(new Border { IsHitTestVisible = false, BorderBrush = B(Blue), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6) });

        if (other) g.Opacity = 0.32;
        g.Cursor = Cursors.Hand;
        return g;
    }

    /// <summary>“农历”二字后面接中文农历；外文界面中间隔个空格。</summary>
    static string LunarPrefix => L.IsZh ? "农历" : L.T("农历") + " ";

    static string Away(int n) => n == 0 ? L.T("就是今天") : n > 0 ? L.F("距今天 {0} 天", n) : L.F("{0} 天前", -n);

    void RenderStatus()
    {
        _status.Inlines.Clear();
        void Add(string text, string color, bool bold = false, double size = 13) =>
            _status.Inlines.Add(new Run(text) { Foreground = B(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, FontSize = size });
        void Gap() => _status.Inlines.Add(new Run("   "));

        if (_selA is { } sa && _selB is { } sb && sa != sb)
        {
            var (a, b) = sa <= sb ? (sa, sb) : (sb, sa);
            int n = (b - a).Days + 1, we = 0, red = 0, cnOff = 0;
            for (var d = a; d <= b; d = d.AddDays(1))
            {
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) we++;
                if (Holidays.Indonesia(d) != null) red++;
                if (Holidays.China(d) is { Off: true }) cnOff++;
            }
            Add($"{CalText.Short(a)} → {CalText.Short(b)}", Ink, bold: true); Gap();
            Add(L.F("共 {0} 天", n), GreenD, size: 15); Gap();
            var detail = L.F("周末 {0} 天", we);
            if (_cfg.CalIndonesia) detail += " · " + L.F("印尼红日子 {0} 天", red);
            if (_cfg.CalChina && cnOff > 0) detail += " · " + L.F("中国放假 {0} 天", cnOff);
            Add(detail, Sub);
            _statusHint.Text = L.T("Esc 取消");
            return;
        }

        var day = _hover ?? Today;
        Add(CalText.Long(day), Ink, bold: true);
        if (_cfg.CalLunar && ChinaCal.InRange(day))
        {
            var fest = ChinaCal.Festival(day);
            Gap(); Add(LunarPrefix + ChinaCal.Of(day).Full + (fest != null ? " · " + fest : ""), Sub);
        }
        if (_cfg.CalIndonesia && Holidays.Indonesia(day) is { } id) { Gap(); Add(L.F(id.Off ? "印尼红日子（{0}）" : "印尼联合休假（{0}）", L.T(id.Name)), Red); }
        if (_cfg.CalChina && Holidays.China(day) is { } cn) { Gap(); Add(cn.Off ? L.F("中国放假（{0}）", L.T(cn.Name)) : L.T("中国调休上班"), cn.Off ? GreenD : Sub); }
        Gap(); Add(Away((day - Today).Days), GreenD, bold: true);
        _statusHint.Text = L.T("拖一段日期算天数 · 点一天记行程");
    }

    void RenderItems()
    {
        _items.Children.Clear();
        var list = TripStore.Upcoming(Today).ToList();
        if (list.Count == 0)
        {
            _items.Children.Add(new TextBlock { Text = L.T("还没有行程。点日历上任意一天，写一句话就记下了。"), Foreground = B(Mute), FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(2, 4, 2, 0) });
            return;
        }
        foreach (var t in list)
        {
            var d = t.Day; int n = (d - Today).Days;
            var top = new DockPanel();
            var cd = new TextBlock { Text = n == 0 ? L.T("今天") : n == 1 ? L.T("明天") : L.F("还有 {0} 天", n), Foreground = B(GreenD), FontWeight = FontWeights.SemiBold, FontSize = 12 };
            DockPanel.SetDock(cd, Dock.Right);
            top.Children.Add(cd);
            top.Children.Add(new TextBlock { Text = CalText.Short(d), Foreground = B(Sub), FontSize = 12 });
            var sp = new StackPanel();
            sp.Children.Add(top);
            sp.Children.Add(new TextBlock { Text = t.Text, FontWeight = FontWeights.SemiBold, Foreground = B(Ink), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            if (t.Remind)
            {
                var bell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                bell.Children.Add(new Path
                {
                    Data = Geometry.Parse("M5,1 C3.2,1 2,2.6 2,4.6 V7 L1,8.4 H9 L8,7 V4.6 C8,2.6 6.8,1 5,1 Z M4,9.6 H6"), Stroke = B(Amber), StrokeThickness = 1.1,
                    Width = 10, Height = 11, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
                });
                bell.Children.Add(new TextBlock
                {
                    Text = t.Reminded ? L.T("已提醒") : L.F("{0} 桌面提醒", CalText.Short(d.AddDays(-1))),
                    Foreground = B(Mute), FontSize = 11, Margin = new Thickness(4, 0, 0, 0),
                });
                sp.Children.Add(bell);
            }
            var item = new Border
            {
                Background = Brushes.White, BorderBrush = B(_picked == d ? Blue : LineC), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand, Child = sp,
            };
            item.MouseEnter += (_, _) => { if (_picked != d) item.BorderBrush = B("#CFD4D9"); };
            item.MouseLeave += (_, _) => { if (_picked != d) item.BorderBrush = B(LineC); };
            item.MouseLeftButtonUp += (_, _) => { _view = new DateTime(d.Year, d.Month, 1); OpenEditor(d); };
            _items.Children.Add(item);
        }
    }

    // ================= 测试用 =================
    internal void TestView(DateTime month) { _view = new DateTime(month.Year, month.Month, 1); Render(); }
    internal void TestSelect(DateTime a, DateTime b) { _selA = a; _selB = b; Render(); }
    internal void TestHover(DateTime d) { _hover = d; RenderStatus(); }
    internal void TestEdit(DateTime d) => OpenEditor(d);
    internal string TestStatus => new TextRange(_status.ContentStart, _status.ContentEnd).Text;
}
