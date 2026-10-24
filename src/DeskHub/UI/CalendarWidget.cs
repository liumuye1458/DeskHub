using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DeskHub.Core;
using DeskHub.Interop;

namespace DeskHub.UI;

/// <summary>
/// 常驻桌面的日历：样子和分区一样（半透明底、标题栏按住拖、右下角拉大小），贴在桌面上，不想要可以从菜单隐藏。
/// 拉小只剩日期；格子够高就显示农历、节气；拉宽右边出现“接下来”的行程，不够宽但够高就放在下面。
/// 悬停看距今天几天，拖一段看共几天（标题栏上显示），点一天弹小框记一句行程。
/// </summary>
public sealed class CalendarWidget : Window
{
    const double ListW = 190, WeekH = 18, HeadH = 32;
    const string Up = "#9CC8FF", Count = "#5BE38F", Gold = "#FFC266", TripC = "#6FB0FF";
    static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    /// <summary>测试用：把“今天”定成某一天。</summary>
    public static DateTime? TodayOverride;
    static DateTime Today => (TodayOverride ?? DateTime.Now).Date;

    /// <summary>挪了位置或改了大小（存布局）。</summary>
    public event Action? Moved;
    /// <summary>菜单里点了“隐藏日历”。</summary>
    public event Action? HideRequested;
    /// <summary>智能识别窗口里点了“去配置 AI”。</summary>
    public event Action? AiSettingsRequested;

    enum ListMode { None, Right, Below }

    readonly AppConfig _cfg;
    DateTime _view;
    DateTime? _selA, _selB, _hover, _picked;
    bool _dragging, _lunar;
    ListMode _mode = (ListMode)(-1);
    double _k = 1, _ks = 1, _cellH;
    double _cellW;
    int _chipRows;   // 格子里能放几行行程小条（放不下就只画一条细线）
    readonly Dictionary<Trip, int> _lanes = new();   // 每条行程固定在第几行，跨天的几格才能连成一条
    Trip? _hl;       // 鼠标停在右边列表哪一条上：日历上把它那几天框出来
    readonly Dictionary<Trip, Border> _cards = new(); // 放大倍数：_k 格子里的字和标记；_ks 标题栏、星期、行程列表（放得温和些）

    readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _hint = new() { Foreground = Res<Brush>("TextSub"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 4, 0) };
    readonly UniformGrid _week = new() { Rows = 1, Columns = 7, Height = WeekH };
    readonly UniformGrid _bg = new() { Rows = 6, Columns = 7 };   // 底色、今天的绿圈、选中、角标（不加阴影）
    readonly UniformGrid _fg = new() { Rows = 6, Columns = 7, IsHitTestVisible = false }; // 文字（整层一个阴影，比每个字一个阴影省）
    readonly Grid _cells = new() { Background = Brushes.Transparent, Cursor = Cursors.Hand };
    readonly Grid _body = new();
    readonly Border _listBox, _head;
    readonly FrameworkElement _headDock;
    readonly RowDefinition _headRow = new() { Height = new GridLength(HeadH) };
    readonly StackPanel _items = new();
    readonly Popup _tip = new() { AllowsTransparency = true, Placement = PlacementMode.Relative, StaysOpen = true };
    readonly TextBlock _tipText = new() { FontSize = 12, LineHeight = 19, Foreground = B("#333333") };
    TripEditor? _editor;
    TripImportWindow? _import;

    public CalendarWidget(AppConfig cfg)
    {
        _cfg = cfg;
        _view = new DateTime(Today.Year, Today.Month, 1);
        Title = L.T("日历");
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false;
        FontFamily = Res<FontFamily>("UiFont"); FontSize = 12; Foreground = Brushes.White; UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Width = 300; Height = 290; MinWidth = 210; MinHeight = 190;

        // ---- 标题栏：按住拖动；‹ › 翻月；右边提示（今天 / 共几天）；≡ 菜单 ----
        var menu = new Button { Width = 24, Height = 24, Cursor = Cursors.Hand, Focusable = false, ToolTip = L.T("日历菜单"), Template = MenuBtnTemplate() };
        menu.Click += (_, _) => ShowMenu(menu);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        nav.Children.Add(NavBtn("‹", -1, L.T("上个月（PageUp）")));
        nav.Children.Add(NavBtn("›", 1, L.T("下个月（PageDown）")));
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_title); left.Children.Add(nav);
        var headDock = new DockPanel { Margin = new Thickness(12, 0, 6, 0) };
        DockPanel.SetDock(menu, Dock.Right); DockPanel.SetDock(left, Dock.Left);
        headDock.Children.Add(menu); headDock.Children.Add(left);
        _hint.HorizontalAlignment = HorizontalAlignment.Right;
        headDock.Children.Add(_hint);
        _hint.MouseLeftButtonDown += (_, e) =>
        {
            // 提示是“回到今天”或带 ✕ 的选中天数时可以点
            if (_hint.Cursor != Cursors.Hand) return;
            e.Handled = true;
            if (_selA != null) { _selA = _selB = null; Render(); } else GoToday();
        };
        var head = new Border
        {
            Height = HeadH, Background = B("#01000000"), BorderBrush = Res<Brush>("FenceLine"), BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = Cursors.SizeAll, Child = headDock, ToolTip = L.T("按住标题栏拖动 · 右下角拉大小 · 拉宽会显示接下来的行程"),
        };
        head.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { GoToday(); return; }
            double x = Left, y = Top;
            DragMove();
            if (Left != x || Top != y) Moved?.Invoke();
        };
        _head = head; _headDock = headDock;

        // ---- 月历 ----
        _fg.Effect = Res<Effect>("TextShadow");
        _week.Effect = Res<Effect>("TextShadow");
        _cells.Children.Add(_bg);
        _cells.Children.Add(_fg);
        _cells.MouseLeftButtonDown += (_, e) =>
        {
            if (DayAt(e.GetPosition(_cells)) is not { } d) return;
            _dragging = true; _selA = _selB = d;
            _cells.CaptureMouse();
            HideTip();
            Render();
            e.Handled = true;
        };
        _cells.MouseMove += (_, e) =>
        {
            var d = DayAt(e.GetPosition(_cells));
            if (d == null || d == _hover) return;
            _hover = d;
            if (_dragging) { if (d != _selB) { _selB = d; Render(); } }
            else { RenderCells(); HighlightList(); ShowTip(d.Value); }
        };
        _cells.MouseLeftButtonUp += (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false;
            _cells.ReleaseMouseCapture();
            if (_selA is { } a && _selA == _selB) { _selA = _selB = null; OpenEditor(a); }
            else Render();
        };
        _cells.MouseLeave += (_, _) => { if (!_dragging) { _hover = null; HideTip(); RenderCells(); HighlightList(); } };
        _cells.MouseWheel += (_, e) => Go(e.Delta < 0 ? 1 : -1);
        _cells.MouseRightButtonUp += (_, _) => { if (_selA != null) { _selA = _selB = null; Render(); } };
        var cal = new DockPanel { Margin = new Thickness(6, 2, 6, 6) };
        DockPanel.SetDock(_week, Dock.Top);
        cal.Children.Add(_week); cal.Children.Add(_cells);

        // ---- 接下来 ----
        var add = new TextBlock { Text = L.T("＋ 记一笔"), Foreground = B(Up), FontSize = 11.5, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        add.MouseLeftButtonUp += (_, _) => OpenEditor(Today);
        var listHead = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(add, Dock.Right);
        listHead.Children.Add(add);
        listHead.Children.Add(new TextBlock { Text = L.T("接下来"), FontWeight = FontWeights.Bold, FontSize = 12 });
        var listPanel = new DockPanel { Margin = new Thickness(12, 8, 10, 6) };
        DockPanel.SetDock(listHead, Dock.Top);
        listPanel.Children.Add(listHead);
        // 底下一块虚线框：点它、往日历上拖截图、Ctrl+V 都能智能识别
        var drop = new Grid { Margin = new Thickness(0, 6, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = L.T("把飞猪 / 携程订单截图、聊天记录拖到日历上，或者在日历上按 Ctrl+V，自动找出哪天要干什么") };
        drop.Children.Add(new Rectangle { Stroke = B("#66FFFFFF"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, RadiusX = 5, RadiusY = 5 });
        drop.Children.Add(new TextBlock { Text = "✨ " + L.T("拖截图进来，自动识别日程"), Foreground = B(Up), FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(4, 7, 4, 7) });
        drop.MouseEnter += (_, _) => drop.Background = Res<Brush>("ItemHover");
        drop.MouseLeave += (_, _) => drop.Background = Brushes.Transparent;
        drop.MouseLeftButtonUp += (_, _) => OpenImport(null);
        DockPanel.SetDock(drop, Dock.Bottom);
        listPanel.Children.Add(drop);
        listPanel.Children.Add(new ScrollViewer { Content = _items, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
        listPanel.Effect = Res<Effect>("TextShadow");
        _listBox = new Border { BorderBrush = Res<Brush>("FenceLine"), Child = listPanel };

        _body.ColumnDefinitions.Add(new ColumnDefinition());
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        _body.RowDefinitions.Add(new RowDefinition());
        _body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        _body.Children.Add(cal);
        _body.Children.Add(_listBox);

        // ---- 右下角拉大小（和分区一样的把手） ----
        var grip = new Thumb
        {
            Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE, Margin = new Thickness(0, 0, 1, 1),
            Template = GripTemplate(),
        };
        grip.DragDelta += (_, e) =>
        {
            Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, 1000);
            Height = Math.Clamp(Height + e.VerticalChange, MinHeight, 800);
        };
        grip.DragCompleted += (_, _) => Moved?.Invoke();

        var grid = new Grid();
        grid.RowDefinitions.Add(_headRow);
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(head);
        Grid.SetRow(_body, 1);
        grid.Children.Add(_body);
        Grid.SetRowSpan(grip, 2);
        grid.Children.Add(grip);
        Content = new Border { Background = Res<Brush>("FenceBg"), BorderBrush = Res<Brush>("FenceBorder"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = grid };

        _tip.PlacementTarget = _cells;
        _tip.Child = new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 7), Margin = new Thickness(4, 2, 10, 10), Child = _tipText,
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.35, RenderingBias = RenderingBias.Performance },
        };

        SourceInitialized += (_, _) => DesktopHost.Pin(this);
        SizeChanged += (_, _) => Relayout();
        KeyDown += OnKey;
        // 截图、文字、图片文件拖到日历上 → 智能识别
        AllowDrop = true;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap) || e.Data.GetDataPresent(DataFormats.UnicodeText) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { OpenImport(e.Data); e.Handled = true; };
        TripStore.Changed += Render;
        Closed += (_, _) => { TripStore.Changed -= Render; _editor?.Close(); _import?.Close(); _tip.IsOpen = false; };
        Relayout();
    }

    /// <summary>设置改了（农历、节假日开关、一周从哪天开始）、过了零点：重画。</summary>
    public void Refresh() { _mode = (ListMode)(-1); Relayout(); }

    public void GoToday() { _view = new DateTime(Today.Year, Today.Month, 1); _selA = _selB = null; Render(); }
    void Go(int months) { _view = _view.AddMonths(months); Render(); }

    DayOfWeek WeekStart => _cfg.CalMondayFirst ? DayOfWeek.Monday : DayOfWeek.Sunday;
    DateTime FirstCell() => _view.AddDays(-(((int)_view.DayOfWeek - (int)WeekStart + 7) % 7));

    DateTime? DayAt(Point p)
    {
        double cw = _cells.ActualWidth / 7, ch = _cells.ActualHeight / 6;
        if (cw <= 0 || ch <= 0) return null;
        int col = (int)(p.X / cw), row = (int)(p.Y / ch);
        if (col < 0 || col > 6 || row < 0 || row > 5) return null;
        return FirstCell().AddDays(row * 7 + col);
    }

    /// <summary>按大小决定：行程放右边 / 下面 / 不放；格子够高才显示农历；格子大了字跟着放大。</summary>
    void Relayout()
    {
        double w = double.IsNaN(Width) ? ActualWidth : Width, h = double.IsNaN(Height) ? ActualHeight : Height;
        var mode = w >= 440 ? ListMode.Right : h >= 400 ? ListMode.Below : ListMode.None;
        // 格子比默认（约 41×38）大多少，字就大多少（打七折，免得太夸张），最多 2 倍；
        // 按 0.1 一档走，拖大小时不会每挪一个像素就重画一遍
        double k = 1;
        if (_cfg.CalAutoScale)
        {
            for (int pass = 0; pass < 2; pass++) // 标题栏和行程列表也跟着放大，会挤掉一点格子，再算一遍
            {
                var (cw, ch, _) = Geo(w, h, mode, k);
                k = Math.Clamp(1 + (Math.Min(cw / 41, ch / 38) - 1) * 0.7, 1, 2);
            }
            k = Math.Floor(k * 10 + 1e-6) / 10;
        }
        var (cellW, cellH, calH) = Geo(w, h, mode, k);
        bool lunar = _cfg.CalLunar && cellH >= 32 * k;
        if (mode == _mode && lunar == _lunar && k == _k && Math.Abs(cellH - _cellH) < 4 && Math.Abs(cellW - _cellW) < 4 && _bg.Children.Count > 0) return;
        _mode = mode; _lunar = lunar; _k = k; _ks = Soft(k); _cellH = cellH; _cellW = cellW;
        // 数字 + 农历约 37k 高，剩下的空间每 15k 放一行行程小条
        _chipRows = lunar ? Math.Clamp((int)((cellH - 38 * k) / (13 * Soft(k))), 0, 3) : 0;
        _headRow.Height = new GridLength(HeadH * _ks); _head.Height = HeadH * _ks;
        _week.Height = WeekH * _ks;
        // 标题栏整块放大（排版不变，只是变大）；行程列表字号不变，只是变宽，拉大后一条能写得更全
        var zoom = _ks > 1 ? new ScaleTransform(_ks, _ks) : null;
        _headDock.LayoutTransform = zoom;
        TextOptions.SetTextFormattingMode(_headDock, zoom == null ? TextFormattingMode.Display : TextFormattingMode.Ideal);
        _tipText.FontSize = 12 * _ks; _tipText.LineHeight = 19 * _ks;
        _body.ColumnDefinitions[1].Width = new GridLength(mode == ListMode.Right ? ListWidth(w, _ks) : 0);
        _body.RowDefinitions[0].Height = mode == ListMode.Below ? new GridLength(calH) : new GridLength(1, GridUnitType.Star);
        _body.RowDefinitions[1].Height = mode == ListMode.Below ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(_listBox, mode == ListMode.Right ? 1 : 0);
        Grid.SetRow(_listBox, mode == ListMode.Below ? 1 : 0);
        _listBox.BorderThickness = mode == ListMode.Right ? new Thickness(1, 0, 0, 0) : new Thickness(0, 1, 0, 0);
        _listBox.Visibility = mode == ListMode.None ? Visibility.Collapsed : Visibility.Visible;
        Render();
    }

    static double Soft(double k) => 1 + (k - 1) * 0.6;

    /// <summary>右边行程列表的宽：窗口拉宽时多分给列表（约四成），日历格子让一让，一条行程能写全。</summary>
    static double ListWidth(double w, double ks) => Math.Min(440, Math.Max(ListW * ks, w * 0.38));

    /// <summary>放大 k 倍时：格子宽、格子高、月历那块的高度。</summary>
    static (double, double, double) Geo(double w, double h, ListMode mode, double k)
    {
        double ks = Soft(k);
        double bodyH = h - HeadH * ks - 2;
        double calH = mode == ListMode.Below ? Math.Min(bodyH - 110 * ks, 300 * ks) : bodyH;
        double calW = w - 2 - (mode == ListMode.Right ? ListWidth(w, ks) : 0) - 12;
        return (calW / 7, (calH - WeekH * ks - 8) / 6, calH);
    }

    // ================= 画 =================
    void Render()
    {
        bool narrow = (double.IsNaN(Width) ? ActualWidth : Width) < 260;
        _title.Text = !narrow ? CalText.Month(_view) : L.IsZh ? $"{_view.Month}月" : _view.ToString("MMM", L.Culture);

        _week.Children.Clear();
        for (int i = 0; i < 7; i++)
            _week.Children.Add(new TextBlock { Text = CalText.WeekShort((DayOfWeek)(((int)WeekStart + i) % 7)), FontSize = 11 * _ks, Foreground = Res<Brush>("TextSub"), HorizontalAlignment = HorizontalAlignment.Center });

        var (a, b) = RenderCells();
        RenderHint(a, b);
        RenderItems();
    }

    /// <summary>只重画 42 个格子（悬停、列表里指着某条时用，比整个重画省）。</summary>
    (DateTime?, DateTime?) RenderCells()
    {
        DateTime? a = null, b = null;
        if (_selA is { } sa && _selB is { } sb) (a, b) = sa <= sb ? (sa, sb) : (sb, sa);
        _bg.Children.Clear(); _fg.Children.Clear();
        var first = FirstCell();
        AssignLanes(first);
        for (int i = 0; i < 42; i++)
        {
            var d = first.AddDays(i);
            var (bg, fg) = Cell(d, a, b);
            _bg.Children.Add(bg); _fg.Children.Add(fg);
        }
        return (a, b);
    }

    (FrameworkElement, FrameworkElement) Cell(DateTime d, DateTime? a, DateTime? b)
    {
        var id = _cfg.CalIndonesia ? Holidays.Indonesia(d) : null;
        var cn = _cfg.CalChina ? Holidays.China(d) : null;
        bool sel = a is { } sa && b is { } sb && d >= sa && d <= sb;
        // 有农历时：数字 + 农历这一块（约 37k 高）在格子里上下居中，格子再高也不会都挤在上面
        double k = _k, numTop = _lunar ? Math.Max(3 * k, (_cellH - 36 * k - (_chipRows > 0 ? _chipRows * 13 * _ks : 6 * k)) / 2) : 0;
        var numV = _lunar ? VerticalAlignment.Top : VerticalAlignment.Center;

        // 底层：红日子整格、选中、悬停、今天的绿圈、休/班角标、行程小条
        var bg = new Grid();
        // 选中的一段连成一条绿带；红日子保持红色，只加绿色上下边
        string? fill = id is { } m ? (m.Off ? "#94EC4C44" : "#4DEC4C44") : sel ? "#6107C255" : null;
        var cellBg = new Border { CornerRadius = new CornerRadius(4), Margin = new Thickness(1) };
        if (sel)
        {
            cellBg.Margin = new Thickness(d == a ? 1 : 0, 1, d == b ? 1 : 0, 1);
            cellBg.CornerRadius = new CornerRadius(d == a ? 4 : 0, d == b ? 4 : 0, d == b ? 4 : 0, d == a ? 4 : 0);
            if (id != null) { cellBg.BorderBrush = Res<Brush>("Green"); cellBg.BorderThickness = new Thickness(0, 2, 0, 2); }
        }
        if (fill != null) cellBg.Background = B(fill);
        if (_hover == d && !_dragging) { cellBg.BorderBrush = B("#BFFFFFFF"); cellBg.BorderThickness = new Thickness(1); if (fill == null) cellBg.Background = B("#29FFFFFF"); }
        if (_picked == d) { cellBg.BorderBrush = B(TripC); cellBg.BorderThickness = new Thickness(1.5); }
        // 右边列表指着的那一条：它占的几天框出来
        if (_hl is { } hl && hl.Covers(d))
        {
            var hc = (Color)ColorConverter.ConvertFromString(TripCard.ColorOf(hl));
            cellBg.BorderBrush = new SolidColorBrush(hc); cellBg.BorderThickness = new Thickness(1.5);
            if (fill == null) cellBg.Background = new SolidColorBrush(Color.FromArgb(0x38, hc.R, hc.G, hc.B));
        }
        bg.Children.Add(cellBg);
        if (d == Today)
            bg.Children.Add(new Border { Width = 24 * k, Height = 20 * k, CornerRadius = new CornerRadius(10 * k), Background = Res<Brush>("Green"), Margin = new Thickness(0, numTop, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = numV });
        if (cn is { } c && (!_lunar || !L.IsZh)) // 小尺寸格子窄、外文角标太宽，会压住数字：只点一个小圆点（绿 = 休，灰 = 班）
            bg.Children.Add(new Border { Width = 5 * k, Height = 5 * k, CornerRadius = new CornerRadius(2.5 * k), Background = B(c.Off ? "#2EE07A" : "#B0B6BF"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2 * k, 2 * k, 0) });
        else if (cn is { } c2)
            bg.Children.Add(new Border
            {
                Background = B(c2.Off ? "#07A548" : "#6B7280"), CornerRadius = new CornerRadius(3 * k), Padding = new Thickness(1.5 * k, 0, 1.5 * k, 0.5 * k),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0),
                Child = new TextBlock { Text = c2.Off ? L.T("休") : L.T("班"), FontSize = 7.5 * k, FontWeight = FontWeights.Bold },
            });
        // 上层：日期数字、农历（行程小条上的字也在这层，才能跨过后面几格写完整）
        var fg = new Grid();
        AddTripBands(bg, fg, d, numTop);
        fg.Children.Add(new Border
        {
            Width = 24 * k, Height = 20 * k, Margin = new Thickness(0, numTop, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = numV,
            Child = new TextBlock { Text = d.Day.ToString(), FontSize = (_lunar ? 13 : 12) * k, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        if (_lunar && ChinaCal.InRange(d))
        {
            var fest = ChinaCal.Festival(d);
            fg.Children.Add(new TextBlock
            {
                Text = fest ?? ChinaCal.Of(d).Cell, FontSize = 9.5 * k, Margin = new Thickness(1, numTop + 20 * k, 1, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Foreground = fest != null ? B(Gold) : B("#B0FFFFFF"), FontWeight = fest != null ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        if (d.Month != _view.Month) { bg.Opacity = 0.35; fg.Opacity = 0.35; }
        return (bg, fg);
    }

    /// <summary>
    /// 格子里的行程：放得下就是一行行带字的小条（“✈ 新加坡”“🏨 入住 金沙”），住好几天的酒店连成一整条；
    /// 格子小放不下字，就在底下画一条细色线（同样跨天连起来）。颜色按类型：机票蓝、火车青、酒店紫、日程橙。
    /// </summary>
    void AddTripBands(Grid bg, Grid fg, DateTime d, double numTop)
    {
        var trips = TripStore.On(d);
        if (trips.Count == 0) return;
        double k = _k;
        int col = ((int)d.DayOfWeek - (int)WeekStart + 7) % 7;
        bool rowStart = col == 0, rowEnd = col == 6;
        Brush Fill(Trip t, byte alpha) { var c = (Color)ColorConverter.ConvertFromString(TripCard.ColorOf(t)); return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)); }
        // 跨天的：和前一天 / 后一天连上（不留缝、不圆角）
        (Thickness, CornerRadius) Shape(Trip t, double inset, double r)
        {
            bool joinL = d > t.Day && !rowStart, joinR = d < t.LastDay && !rowEnd;
            return (new Thickness(joinL ? 0 : inset, 0, joinR ? 0 : inset, 0), new CornerRadius(joinL ? 0 : r, joinR ? 0 : r, joinR ? 0 : r, joinL ? 0 : r));
        }

        if (_chipRows == 0)
        {
            var t = trips.OrderBy(x => _lanes.TryGetValue(x, out var l) ? l : 0).First();
            var (m, cr) = Shape(t, 6 * k, 1.5 * k);
            double top = _lunar ? Math.Min(numTop + 38 * k, _cellH - 4 * k) : _cellH - 5 * k;
            bg.Children.Add(new Border { Height = 3 * k, Background = Fill(t, 0xF0), CornerRadius = cr, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(m.Left, Math.Max(0, top), m.Right, 0) });
            return;
        }
        // 每条在自己固定的那一行；放不下的，最后一行换成灰色的“+N”
        double ks = _ks;
        int Lane(Trip t) => _lanes.TryGetValue(t, out var l) ? l : 0;
        bool over = trips.Any(t => Lane(t) >= _chipRows);
        int shown = over && _chipRows > 1 ? _chipRows - 1 : _chipRows;
        int hidden = trips.Count(t => Lane(t) >= shown);
        // 某天放不下（有“+N”）时，连着的色条在那天不写字，字挪到前后干净的格子里，免得和“+N”叠在一起
        bool Over(DateTime e) => TripStore.On(e).Any(x => Lane(x) >= _chipRows);
        bool blocked = over && _chipRows == 1;
        foreach (var t in trips)
        {
            int i = Lane(t);
            if (i >= shown) continue;
            var (m, cr) = Shape(t, 2 * k, 3 * ks);
            bool Clean(DateTime e) => t.Covers(e) && !(Over(e) && (_chipRows == 1 || i >= _chipRows - 1));
            // 字只写在第一天（或者换行后、躲开“+N”后的第一格），连着的几天只有色条
            bool label = blocked ? t.LastDay == t.Day : d == t.Day || rowStart || !Clean(d.AddDays(-1));
            var text = label ? (t.Kind.Length > 0 ? t.Icon + " " : "") + t.Chip(d) : "";
            // 只有一行时，“+N”单独靠右，不会被省略号吃掉
            var badge = blocked ? $"+{hidden}" : null;
            // 连着好几格的（酒店）：字写在上层，一直写到这一行里最后连着的那格，不在第一格就被省略
            int span = 1;
            if (label && !blocked) while (col + span <= 6 && Clean(d.AddDays(span))) span++;
            if (span > 1)
            {
                bg.Children.Add(Chip("", Fill(t, 0xD0), m, cr, i, null));
                var lb = Chip(text, Brushes.Transparent, m, cr, i, null);
                lb.Margin = new Thickness(lb.Margin.Left, lb.Margin.Top, -(span - 1) * _cellW + 2 * k, 0);
                fg.Children.Add(lb);
            }
            else bg.Children.Add(Chip(text, Fill(t, 0xD0), m, cr, i, badge));
        }
        if (over && _chipRows == 1 && trips.All(t => Lane(t) > 0))
            bg.Children.Add(Chip($"+{hidden}", B("#55FFFFFF"), new Thickness(2 * k, 0, 2 * k, 0), new CornerRadius(3 * ks), 0, null));
        else if (over && _chipRows > 1)
            bg.Children.Add(Chip($"+{hidden}", B("#55FFFFFF"), new Thickness(2 * k, 0, 2 * k, 0), new CornerRadius(3 * ks), shown, null));

        Border Chip(string text, Brush fill, Thickness m, CornerRadius cr, int row, string? badge)
        {
            var dp = new DockPanel { Margin = new Thickness(3 * ks, 0, 2 * ks, 0) };
            TextBlock Tx(string s) => new() { Text = s, FontSize = 8.5 * ks, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            if (badge != null) { var bt = Tx(badge); bt.Margin = new Thickness(2 * ks, 0, 0, 0); DockPanel.SetDock(bt, Dock.Right); dp.Children.Add(bt); }
            dp.Children.Add(Tx(text));
            return new Border
            {
                Height = 11.5 * ks, Background = fill, CornerRadius = cr, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(m.Left, numTop + 36 * k + row * 13 * ks, m.Right, 0), Child = dp,
            };
        }
    }

    /// <summary>
    /// 给这一屏 42 天里的行程排行号：先排跨天长的，再按日子、时间；每条找它占的几天都空着的最小一行。
    /// 这样住 4 晚的酒店每天都在同一行，连成一整条，不会这天在上、那天在下。
    /// </summary>
    void AssignLanes(DateTime first)
    {
        _lanes.Clear();
        var last = first.AddDays(41);
        var used = new Dictionary<DateTime, HashSet<int>>();
        var vis = TripStore.Trips.Where(t => t.LastDay >= first && t.Day <= last)
            .OrderBy(t => t.Day).ThenByDescending(t => (t.LastDay - t.Day).Days).ThenBy(t => t.Time, StringComparer.Ordinal);
        foreach (var t in vis)
        {
            var a = t.Day < first ? first : t.Day; var b = t.LastDay > last ? last : t.LastDay;
            int lane = 0;
            while (Enumerable.Range(0, (b - a).Days + 1).Any(i => used.TryGetValue(a.AddDays(i), out var u) && u.Contains(lane))) lane++;
            for (var d = a; d <= b; d = d.AddDays(1)) { if (!used.TryGetValue(d, out var u)) used[d] = u = new(); u.Add(lane); }
            _lanes[t] = lane;
        }
    }

    /// <summary>标题栏右边：选了一段 → 共几天（点 ✕ 取消）；翻到别的月 → 回到今天；平时 → 今天是农历哪天。</summary>
    void RenderHint(DateTime? a, DateTime? b)
    {
        _hint.Inlines.Clear();
        _hint.Cursor = null;
        _hint.ToolTip = null;
        if (a is { } sa && b is { } sb && sa != sb)
        {
            int n = (sb - sa).Days + 1, we = 0, red = 0, cnOff = 0;
            for (var d = sa; d <= sb; d = d.AddDays(1))
            {
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) we++;
                if (Holidays.Indonesia(d) != null) red++;
                if (Holidays.China(d) is { Off: true }) cnOff++;
            }
            var detail = L.F("周末 {0} 天", we);
            if (_cfg.CalIndonesia && red > 0) detail += " · " + L.F("印尼红日子 {0} 天", red);
            if (_cfg.CalChina && cnOff > 0) detail += " · " + L.F("中国放假 {0} 天", cnOff);
            _hint.Inlines.Add(new Run(L.F("共 {0} 天", n)) { Foreground = B(Count), FontWeight = FontWeights.SemiBold });
            _hint.Inlines.Add(new Run(" · " + detail + "  ✕"));
            _hint.ToolTip = $"{CalText.Short(sa)} → {CalText.Short(sb)}\n{L.F("共 {0} 天", n)} · {detail}\n{L.T("点这里或右键取消")}";
            _hint.Cursor = Cursors.Hand;
            return;
        }
        if (_view.Year != Today.Year || _view.Month != Today.Month)
        {
            _hint.Inlines.Add(new Run(L.T("回到今天")) { Foreground = B(Up) });
            _hint.Cursor = Cursors.Hand;
            return;
        }
        _hint.Inlines.Add(new Run(L.T("今天") + " · " + (_cfg.CalLunar && L.IsZh && ChinaCal.InRange(Today) ? ChinaCal.Of(Today).Full : CalText.Short(Today))));
    }

    void RenderItems()
    {
        _items.Children.Clear();
        _cards.Clear();
        if (_mode == ListMode.None) return;
        if (TripStore.Upcoming(Today).Any()) { RenderGroups(); return; }
        _items.Children.Add(new TextBlock { Text = L.T("还没有行程。点日历上任意一天写一句；也可以把订单截图、聊天记录拖进来自动识别。"), Foreground = Res<Brush>("TextSub"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 4, 0, 0) });
    }

    /// <summary>
    /// 接下来：按出行日子分组（日子标题 + 还有几天），每组里按时间排；每条用固定模板的卡片。
    /// 指着卡片 → 日历上框出那几天；指着日历的某天 → 这里那天的卡片亮起来。
    /// </summary>
    void RenderGroups()
    {
        foreach (var g in TripStore.Upcoming(Today).GroupBy(t => t.Day < Today ? Today : t.Day))
        {
            var d = g.Key; int n = (d - Today).Days;
            var head = new DockPanel { Margin = new Thickness(0, _items.Children.Count == 0 ? 2 : 10, 0, 4) };
            var cd = new TextBlock { Text = n == 0 ? L.T("今天") : n == 1 ? L.T("明天") : L.F("还有 {0} 天", n), Foreground = B(Count), FontWeight = FontWeights.SemiBold, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(cd, Dock.Right);
            head.Children.Add(cd);
            head.Children.Add(new TextBlock { Text = CalText.Short(d), FontWeight = FontWeights.SemiBold, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            _items.Children.Add(head);
            foreach (var t in g)
            {
                var card = TripCard.Card(t, dark: true);
                card.Margin = new Thickness(0, 0, 0, 5);
                card.Cursor = Cursors.Hand;
                card.ToolTip = t.Display;
                card.MouseEnter += (_, _) => { _hl = t; RenderCells(); HighlightList(); };
                card.MouseLeave += (_, _) => { if (_hl == t) { _hl = null; RenderCells(); HighlightList(); } };
                card.MouseLeftButtonUp += (_, _) => { _hl = null; _view = new DateTime(t.Day.Year, t.Day.Month, 1); OpenEditor(t.Day, t); };
                _cards[t] = card;
                _items.Children.Add(card);
            }
        }
        HighlightList();
    }

    /// <summary>列表里亮起：鼠标指着的卡片，和日历上鼠标停的那天的行程。</summary>
    void HighlightList()
    {
        Border? first = null;
        foreach (var (t, card) in _cards)
        {
            bool on = t == _hl || (_hover is { } h && t.Covers(h));
            card.Background = B(on ? "#33FFFFFF" : "#1AFFFFFF");
            card.BorderBrush = on ? B(TripCard.ColorOf(t)) : B("#00FFFFFF");
            if (on && first == null && _hl == null) first = card;
        }
        // 指着日历上某天：右边列表滚到那天的卡片
        first?.BringIntoView();
    }

    // ================= 悬停提示 =================
    static string LunarPrefix => L.IsZh ? "农历" : L.T("农历") + " ";
    static string Away(int n) => n == 0 ? L.T("就是今天") : n > 0 ? L.F("距今天 {0} 天", n) : L.F("{0} 天前", -n);

    void ShowTip(DateTime d)
    {
        _tipText.Inlines.Clear();
        void Line(string text, string color, bool bold = false)
        {
            if (_tipText.Inlines.Count > 0) _tipText.Inlines.Add(new LineBreak());
            _tipText.Inlines.Add(new Run(text) { Foreground = B(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
        }
        var head = CalText.Long(d);
        if (_cfg.CalLunar && ChinaCal.InRange(d))
        {
            var fest = ChinaCal.Festival(d);
            head += " · " + LunarPrefix + ChinaCal.Of(d).Full + (fest != null ? " · " + fest : "");
        }
        Line(head, "#333333");
        if (_cfg.CalIndonesia && Holidays.Indonesia(d) is { } id) Line(L.F(id.Off ? "印尼红日子（{0}）" : "印尼联合休假（{0}）", L.T(id.Name)), "#D4433D");
        if (_cfg.CalChina && Holidays.China(d) is { } cn) Line(cn.Off ? L.F("中国放假（{0}）", L.T(cn.Name)) : L.T("中国调休上班"), cn.Off ? "#07A548" : "#666666");
        foreach (var t in TripStore.On(d)) Line(t.Icon + " " + t.DisplayOn(d),TripCard.ColorOf(t, dark: false));
        Line(Away((d - Today).Days), "#07A548", bold: true);

        double cw = _cells.ActualWidth / 7, ch = _cells.ActualHeight / 6;
        int i = (d - FirstCell()).Days;
        _tip.HorizontalOffset = (i % 7) * cw + cw / 2 - 20;
        _tip.VerticalOffset = (i / 7 + 1) * ch + 2;
        _tip.IsOpen = true;
    }

    void HideTip() => _tip.IsOpen = false;

    // ================= 记行程 =================
    void OpenEditor(DateTime d, Trip? trip = null)
    {
        HideTip();
        _editor?.Close();
        _picked = d;
        Render();
        var ed = new TripEditor(d, trip, _cfg, Today);
        _editor = ed;
        ed.Closed += (_, _) => { if (_editor == ed) { _editor = null; _picked = null; Render(); } };
        // 放在挂件右边（右边放不下就放左边），和点的那一天差不多高
        int i = Math.Clamp((d - FirstCell()).Days, 0, 41);
        var cellTop = _cells.TranslatePoint(new Point(0, (i / 7) * (_cells.ActualHeight / 6)), this).Y;
        ed.ShowBeside(new Rect(Left, Top, ActualWidth, ActualHeight), Top + cellTop - 16, DeskController.WorkAreaOf(this));
    }

    /// <summary>打开智能识别窗口（data：拖进来 / 粘贴的东西，可以没有）。已经开着就把东西加进去。</summary>
    void OpenImport(IDataObject? data)
    {
        HideTip();
        _editor?.Close();
        if (_import == null)
        {
            var w = new TripImportWindow(_cfg, () => Today);
            _import = w;
            w.Closed += (_, _) => { if (_import == w) _import = null; };
            w.Added += d => { _view = new DateTime(d.Year, d.Month, 1); _selA = _selB = null; Render(); };
            w.SettingsRequested += () => AiSettingsRequested?.Invoke();
            w.ShowBeside(new Rect(Left, Top, ActualWidth, ActualHeight), DeskController.WorkAreaOf(this));
        }
        else _import.Activate();
        if (data != null) _import.Take(data);
    }

    // ================= 菜单 / 键盘 =================
    void ShowMenu(FrameworkElement at)
    {
        var m = new ContextMenu();
        void Add(string text, Action run) { var mi = new MenuItem { Header = text }; mi.Click += (_, _) => run(); m.Items.Add(mi); }
        void Check(string text, bool on, Action<bool> set)
        {
            var mi = new MenuItem { Header = text, IsCheckable = true, IsChecked = on };
            mi.Click += (_, _) => { set(mi.IsChecked); _cfg.Save(); Refresh(); };
            m.Items.Add(mi);
        }
        Add(L.T("回到今天"), GoToday);
        Add(L.T("＋ 记一笔"), () => OpenEditor(Today));
        Add("✨ " + L.T("智能识别（截图 / 聊天记录）…"), () => OpenImport(null));
        m.Items.Add(new Separator());
        Check(L.T("显示农历"), _cfg.CalLunar, v => _cfg.CalLunar = v);
        Check(L.T("显示中国节假日"), _cfg.CalChina, v => _cfg.CalChina = v);
        Check(L.T("显示印尼红日子"), _cfg.CalIndonesia, v => _cfg.CalIndonesia = v);
        Check(L.T("一周从周一开始"), _cfg.CalMondayFirst, v => _cfg.CalMondayFirst = v);
        Check(L.T("拉大日历时字也跟着变大"), _cfg.CalAutoScale, v => _cfg.CalAutoScale = v);
        m.Items.Add(new Separator());
        Add(L.T("隐藏日历（点工具条上的“日历”再打开）"), () => HideRequested?.Invoke());
        m.PlacementTarget = at;
        m.Placement = PlacementMode.Bottom;
        m.IsOpen = true;
    }

    void OnKey(object s, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when _selA != null: _selA = _selB = null; Render(); e.Handled = true; break;
            case Key.PageUp: Go(-1); e.Handled = true; break;
            case Key.PageDown: Go(1); e.Handled = true; break;
            case Key.Home: GoToday(); e.Handled = true; break;
            case Key.V when Keyboard.Modifiers == ModifierKeys.Control:
                try { OpenImport(Clipboard.GetDataObject()); } catch { OpenImport(null); }
                e.Handled = true;
                break;
        }
    }

    FrameworkElement NavBtn(string glyph, int step, string tip)
    {
        var b = new Border
        {
            Width = 20, Height = 22, CornerRadius = new CornerRadius(3), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip,
            Child = new TextBlock { Text = glyph, FontSize = 15, Opacity = 0.85, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) },
        };
        b.MouseEnter += (_, _) => b.Background = Res<Brush>("ItemHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonDown += (_, e) => { e.Handled = true; Go(step); }; // 不让标题栏把它当成拖动
        return b;
    }

    static ControlTemplate MenuBtnTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border), "b");
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var p = new FrameworkElementFactory(typeof(Path));
        p.SetValue(Path.DataProperty, Geometry.Parse("M0,0 H12 M0,4 H12 M0,8 H12"));
        p.SetValue(Shape.StrokeProperty, Brushes.White);
        p.SetValue(Shape.StrokeThicknessProperty, 1.3);
        p.SetValue(OpacityProperty, 0.8);
        p.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        p.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(p);
        t.VisualTree = bd;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, B("#26FFFFFF"), "b"));
        t.Triggers.Add(hover);
        return t;
    }

    static ControlTemplate GripTemplate()
    {
        var t = new ControlTemplate(typeof(Thumb));
        var g = new FrameworkElementFactory(typeof(Grid));
        g.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var p = new FrameworkElementFactory(typeof(Path));
        p.SetValue(Path.DataProperty, Geometry.Parse("M10,2 L2,10 M10,6 L6,10"));
        p.SetValue(Shape.StrokeProperty, B("#66FFFFFF"));
        p.SetValue(Shape.StrokeThicknessProperty, 1.0);
        g.AppendChild(p);
        t.VisualTree = g;
        return t;
    }

    // ================= 测试用 =================
    internal void TestView(DateTime month) { _view = new DateTime(month.Year, month.Month, 1); Render(); }
    internal void TestSelect(DateTime a, DateTime b) { _selA = a; _selB = b; Render(); }
    internal void TestHover(DateTime d) { _hover = d; Render(); ShowTip(d); }
    internal TripEditor? TestEdit(DateTime d) { OpenEditor(d, TripStore.On(d).FirstOrDefault()); return _editor; }
    internal void TestHl(DateTime d) { _hl = TripStore.On(d).FirstOrDefault(); RenderCells(); HighlightList(); }
    internal TripImportWindow? TestImport() { OpenImport(null); return _import; }
    internal string TestStatus => new TextRange(_hint.ContentStart, _hint.ContentEnd).Text + (_tip.IsOpen ? " | " + new TextRange(_tipText.ContentStart, _tipText.ContentEnd).Text.Replace("\r\n", " / ") : "");
}

/// <summary>
/// 点一天弹出的小框：这天已有的几条按时间排成卡片（点一张改它，✕ 删掉），下面是表单：
/// 先选类型（日程 / 航班 / 火车 / 酒店），再填这一类固定的几格。点别处就收起（不保存）。
/// </summary>
public sealed class TripEditor : Window
{
    const string Ink = "#222222", Mute = "#999999", Red = "#E0524C", Blue = "#3A7BE0";
    const double Shadow = 14, W = 300;
    static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    readonly DateTime _day;
    readonly CheckBox _remind;
    readonly StackPanel _rows = new() { Margin = new Thickness(0, 8, 0, 0) };
    readonly ContentControl _formHost = new() { Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock _formTitle = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("#444444"), Margin = new Thickness(0, 10, 0, 0) };
    readonly Button _del;
    TripForm? _form;
    Trip? _trip;
    bool _keepOpen;

    public TripEditor(DateTime day, Trip? trip, AppConfig cfg, DateTime today)
    {
        _day = day;
        Title = L.T("行程");
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = Res<FontFamily>("UiFont"); FontSize = 12.5; UseLayoutRounding = true;

        var sub = new List<string>();
        if (cfg.CalLunar && ChinaCal.InRange(day)) sub.Add((L.IsZh ? "农历" : L.T("农历") + " ") + ChinaCal.Of(day).Full);
        int n = (day - today).Days;
        sub.Add(n == 0 ? L.T("就是今天") : n > 0 ? L.F("距今天 {0} 天", n) : L.F("{0} 天前", -n));
        if (cfg.CalIndonesia && Holidays.Indonesia(day) is { } id) sub.Add(L.F(id.Off ? "印尼红日子（{0}）" : "印尼联合休假（{0}）", L.T(id.Name)));

        _remind = new CheckBox { Style = Res<Style>("Toggle"), Margin = new Thickness(0, 10, 0, 0), Content = new TextBlock { Text = L.T("提前一天在桌面弹便签提醒"), FontSize = 12, Foreground = B("#666666"), TextWrapping = TextWrapping.Wrap } };

        _del = new Button { Content = L.T("删除"), Style = Res<Style>("BtnW"), Foreground = B(Red), Padding = new Thickness(10, 2, 10, 2) };
        _del.Click += (_, _) => { if (_trip != null) TripStore.Remove(_trip); Close(); };
        var cancel = new Button { Content = L.T("取消"), Style = Res<Style>("BtnW"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
        cancel.Click += (_, _) => Close();
        var save = new Button { Content = L.T("保存"), Style = Res<Style>("BtnG"), Padding = new Thickness(12, 2, 12, 2) };
        save.Click += (_, _) => Save();
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(cancel); right.Children.Add(save);
        var btns = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(_del, Dock.Left);
        btns.Children.Add(_del); btns.Children.Add(right);

        var sp = new StackPanel { Width = W };
        sp.Children.Add(new TextBlock { Text = CalText.Long(day), FontWeight = FontWeights.Bold, FontSize = 14, Foreground = B(Ink) });
        sp.Children.Add(new TextBlock { Text = string.Join(" · ", sub), Foreground = B(Mute), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        sp.Children.Add(_rows);
        sp.Children.Add(_formTitle);
        sp.Children.Add(_formHost);
        sp.Children.Add(_remind); sp.Children.Add(btns);
        Content = new Grid
        {
            Children =
            {
                new Border
                {
                    Margin = new Thickness(Shadow), Background = Brushes.White, BorderBrush = B(Blue), BorderThickness = new Thickness(1.2), CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 12, 14, 14), Child = sp,
                    Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.35, RenderingBias = RenderingBias.Performance },
                },
            },
        };

        Pick(trip);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Deactivated += (_, _) => { if (IsVisible && !_keepOpen) Close(); };
    }

    /// <summary>改哪一条（null = 新记一条）：放进表单，上面的卡片高亮它。</summary>
    void Pick(Trip? t)
    {
        _trip = t;
        _form = new TripForm(t ?? new Trip { Date = TripStore.Key(_day) }, showDate: false);
        _form.Submit += Save;
        _formHost.Content = _form;
        _formTitle.Text = t == null ? (TripStore.On(_day).Count > 0 ? L.T("再记一条") : L.T("记一条")) : L.T("改这一条");
        _remind.IsChecked = t?.Remind ?? true;
        _del.Visibility = t != null ? Visibility.Visible : Visibility.Hidden;
        BuildRows();
        if (IsLoaded) _form.FocusFirst();
    }

    void BuildRows()
    {
        _rows.Children.Clear();
        var list = TripStore.On(_day);
        _rows.Visibility = list.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var t in list)
        {
            bool on = t == _trip;
            var card = TripCard.Card(t, dark: false, showDate: t.Day != _day);
            card.Cursor = Cursors.Hand;
            card.Margin = new Thickness(0, 0, 0, 6);
            if (on) { card.BorderBrush = B(TripCard.ColorOf(t, false)); card.BorderThickness = new Thickness(1.5); card.Background = Brushes.White; }
            var x = new TextBlock { Text = "✕", Foreground = B("#BBBBBB"), FontSize = 11, Padding = new Thickness(6, 2, 6, 2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Cursor = Cursors.Hand, ToolTip = L.T("删除"), Background = Brushes.Transparent };
            x.MouseEnter += (_, _) => x.Foreground = B(Red);
            x.MouseLeave += (_, _) => x.Foreground = B("#BBBBBB");
            x.MouseLeftButtonUp += (_, e) => { e.Handled = true; TripStore.Remove(t); Pick(_trip == t ? null : _trip); };
            // ✕ 放在卡片外面右边，不压住卡片里的字（比如酒店的“4 晚”）
            x.Margin = new Thickness(2, 4, -4, 0);
            var g = new DockPanel();
            DockPanel.SetDock(x, Dock.Right);
            g.Children.Add(x); g.Children.Add(card);
            card.MouseLeftButtonUp += (_, _) => Pick(t);
            _rows.Children.Add(g);
        }
        if (_trip != null)
        {
            var add = new TextBlock { Text = L.T("＋ 这天再记一条"), Foreground = B(Blue), FontSize = 12, Cursor = Cursors.Hand, Margin = new Thickness(2, 0, 0, 0) };
            add.MouseLeftButtonUp += (_, _) => Pick(null);
            _rows.Children.Add(add);
        }
    }

    void Save()
    {
        if (_form == null) return;
        var v = _form.Value;
        v.Remind = _remind.IsChecked == true;
        TripStore.Put(v, _trip);
        Close();
    }

    /// <summary>放在 owner 旁边：右边放得下放右边，否则放左边；上沿大约对齐 y。</summary>
    public void ShowBeside(Rect owner, double y, Rect wa)
    {
        Opacity = 0;
        Show();
        UpdateLayout();
        double w = ActualWidth, h = ActualHeight;
        double x = owner.Right + 6 - Shadow;
        if (x + w - Shadow > wa.Right) x = owner.Left - w + Shadow - 6;
        Left = Math.Clamp(x, wa.Left - Shadow, Math.Max(wa.Left - Shadow, wa.Right - w + Shadow));
        Top = Math.Clamp(y - Shadow, wa.Top - Shadow, Math.Max(wa.Top - Shadow, wa.Bottom - h + Shadow));
        Opacity = 1;
        Activate();
        _form?.FocusFirst();
    }

    /// <summary>测试截图时不因为失去焦点就关掉。</summary>
    public void KeepOpenForTest() => _keepOpen = true;
}
