using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>
/// 新建 / 编辑一张便签。左边是所见即所得的便签（直接在上面打字），右边四块设置，每块都配图：
/// ① 外观：颜色、字号、有没有小尖角
/// ② 放在哪一层：贴在桌面上 / 浮在最上面
/// ③ 什么时候出现：每天 / 只一次；一直在 / 到点才出现（时间在一条 早 5:00 → 次日 5:00 的时间条上拖）
/// ④ 接力：几张便签排队，这张排第几用 ←→ 挪，步骤号自动编
/// 改的都是草稿，点“保存”才写回便签。
/// </summary>
public sealed class GuideEditor : Window
{
    const string Green = "#07C255", GreenD = "#07A548", Ink = "#222222", Sub = "#666666", Mute = "#999999", LineC = "#ECECEC", Soft = "#F6F7F8", Tint = "#F2FBF5";
    const double NoteW = 228, Tail = 13;
    const int DayStart = GuideStore.DayStartHour * 60;
    static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    // 颜色点的填充色（比便签底色深一点，一眼能分清）
    static readonly Dictionary<string, string> DotFill = new() { ["yellow"] = "#FFE58A", ["red"] = "#FFB3AE", ["green"] = "#9EE2B8", ["blue"] = "#A9C9F5" };

    readonly GuideNote _n;
    string _color; double _size; bool _arrow, _top, _daily, _timed; int _minutes;
    string _flow; int _pos;
    readonly List<string> _newFlows = new();
    readonly List<Action> _refresh = new();

    // 预览
    readonly TextBox _text = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
        FontWeight = FontWeights.SemiBold, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FocusVisualStyle = null,
    };
    readonly TextBlock _placeholder = new() { IsHitTestVisible = false, FontWeight = FontWeights.SemiBold };
    readonly Border _flowTag = new() { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
    readonly TextBlock _flowTagText = new() { Foreground = Brushes.White, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
    readonly TextBlock _dailyMark = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    readonly Path _bubble = new() { StrokeLineJoin = PenLineJoin.Round, StrokeThickness = 1.5, Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.22, RenderingBias = RenderingBias.Performance } };
    readonly Border _noteBody = new() { Width = NoteW, Padding = new Thickness(16, 12, 14, 10) };
    readonly Canvas _note = new();
    readonly FrameworkElement _deskIcon;
    readonly TextBlock _hint = new() { Foreground = B("#CCFFFFFF"), FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };

    // 时间条
    readonly Canvas _tl = new() { Height = 30, Background = Brushes.Transparent, Cursor = Cursors.Hand };
    readonly Border _tlHidden = new() { Height = 30, CornerRadius = new CornerRadius(8, 0, 0, 8) };
    readonly Border _tlShown = new() { Height = 30, BorderThickness = new Thickness(1.5), Padding = new Thickness(14, 0, 0, 0) };
    readonly TextBlock _tlShownText = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly Canvas _pin = new() { IsHitTestVisible = false };
    readonly Border _knob = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 3) };
    readonly TextBlock _knobText = new() { Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12 };
    readonly Canvas _ticks = new() { Height = 18, Margin = new Thickness(0, 7, 0, 0) };
    readonly TextBlock _after = new() { Foreground = B(Sub), FontSize = 12 };
    readonly TextBlock _tlTip = new() { FontSize = 12, IsHitTestVisible = false };

    // 接力
    readonly WrapPanel _flowChips = new();
    readonly StackPanel _chain = new() { Orientation = Orientation.Horizontal };
    readonly FrameworkElement _chainBox, _moveRow;
    readonly Button _left, _right;
    readonly TextBlock _posText = new() { Foreground = B(Mute), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };

    public GuideEditor(GuideNote n, bool isNew)
    {
        _n = n;
        _color = GuideWindow.Colors.ContainsKey(n.Color) ? n.Color : "yellow";
        _size = new[] { 14.0, 18, 24, 32 }.OrderBy(v => Math.Abs(v - n.FontSize)).First();
        _arrow = n.Arrow; _top = n.Topmost; _daily = n.Daily;
        _timed = TimeSpan.TryParse(n.ShowAt, out var at);
        _minutes = _timed ? (int)at.TotalMinutes : 19 * 60 + 55;
        _flow = n.Flow;
        _pos = _flow.Length > 0 ? Others(_flow).Count(o => o.Step < n.Step || (o.Step == n.Step && o.Created < n.Created)) : 0;

        Title = isNew ? L.T("新建便签") : L.T("编辑便签");
        Width = 920; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false; Background = Brushes.White;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"); FontSize = 13;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));

        _deskIcon = DeskIcon();
        _left = SmallBtn(L.T("← 往前排"), () => { if (_pos > 0) _pos--; });
        _right = SmallBtn(L.T("往后排 →"), () => { if (_pos < Others(_flow).Count) _pos++; });
        _chainBox = new ScrollViewer { Content = _chain, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(2, 4, 2, 6) };
        var move = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        move.Children.Add(_left); move.Children.Add(_right); move.Children.Add(_posText);
        _moveRow = move;

        var root = new Grid { Background = Brushes.White };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(BuildPreview());
        var right = BuildSettings();
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        Content = root;

        _text.Text = n.Text;
        _text.TextChanged += (_, _) => _placeholder.Visibility = _text.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _placeholder.Visibility = _text.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _noteBody.SizeChanged += (_, _) => DrawBubble();
        _tl.SizeChanged += (_, _) => DrawTimeline();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Save(); e.Handled = true; }
        };
        Loaded += (_, _) => { _text.Focus(); _text.CaretIndex = _text.Text.Length; };
        Refresh();
    }

    // ================= 左：预览 =================
    FrameworkElement BuildPreview()
    {
        var g = new Grid
        {
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new((Color)ColorConverter.ConvertFromString("#3B5B7A"), 0), new((Color)ColorConverter.ConvertFromString("#6D8FA8"), 0.55), new((Color)ColorConverter.ConvertFromString("#B7C7CF"), 1),
            }, new Point(0.2, 0), new Point(0.8, 1)),
            ClipToBounds = true,
        };
        var cap = new TextBlock { Foreground = B("#D9FFFFFF"), FontSize = 12, Margin = new Thickness(18, 16, 18, 0), VerticalAlignment = VerticalAlignment.Top };
        cap.Inlines.Add(new System.Windows.Documents.Run(L.T("预览")) { FontWeight = FontWeights.SemiBold });
        cap.Inlines.Add(new System.Windows.Documents.Run(" · " + L.T("直接在便签上打字")));
        g.Children.Add(cap);

        // 便签：外形（气泡 + 小尖角）单独画，文字层叠在上面，和真实便签同一套画法
        _text.SetValue(TextBlock.LineHeightProperty, double.NaN);
        var textLayer = new Grid();
        textLayer.Children.Add(_text);
        textLayer.Children.Add(_placeholder);
        _placeholder.Text = L.T("在这里直接写内容…");
        _placeholder.Foreground = B("#59000000");
        _flowTag.Child = _flowTagText;
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        var ok = new Border
        {
            Background = B(Green), CornerRadius = new CornerRadius(6), MinWidth = 64, Height = 28, IsHitTestVisible = false,
            Child = new TextBlock { Text = "OK", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        DockPanel.SetDock(ok, Dock.Right);
        bottom.Children.Add(ok);
        bottom.Children.Add(_dailyMark);
        var body = new StackPanel();
        body.Children.Add(_flowTag);
        body.Children.Add(textLayer);
        body.Children.Add(bottom);
        _noteBody.Child = body;
        _note.Children.Add(_bubble);
        Canvas.SetLeft(_noteBody, Tail);
        _note.Children.Add(_noteBody);

        var desk = new Canvas { Margin = new Thickness(0, 0, 0, 0) };
        Canvas.SetLeft(_deskIcon, 30); Canvas.SetTop(_deskIcon, 120);
        desk.Children.Add(_deskIcon);
        Canvas.SetTop(_note, 110);
        desk.Children.Add(_note);
        g.Children.Add(desk);

        _hint.Margin = new Thickness(18, 0, 18, 16);
        _hint.VerticalAlignment = VerticalAlignment.Bottom;
        g.Children.Add(_hint);
        return g;
    }

    /// <summary>预览里“被指的那个图标”：一个普通的桌面图标样子。</summary>
    static FrameworkElement DeskIcon()
    {
        var sp = new StackPanel { Width = 70 };
        var img = new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(10), Background = B("#2B2B2B"),
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Direction = 270, Opacity = 0.3 },
        };
        var ring = new Grid { Width = 26, Height = 26 };
        ring.Children.Add(new Ellipse { Stroke = Brushes.White, StrokeThickness = 4 });
        ring.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Brushes.White });
        img.Child = ring;
        sp.Children.Add(img);
        sp.Children.Add(new TextBlock
        {
            Text = L.T("图标"), Foreground = Brushes.White, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = 0.5 },
        });
        return sp;
    }

    void DrawBubble()
    {
        double h = Math.Max(_noteBody.ActualHeight, 44);
        _bubble.Data = GuideWindow.Bubble(new Rect(Tail, 0, NoteW, h), _arrow);
        _note.Width = Tail + NoteW; _note.Height = h;
    }

    void RefreshPreview()
    {
        var (card, _, accent) = GuideWindow.Colors[_color];
        _bubble.Fill = B(card); _bubble.Stroke = B(accent);
        _flowTag.Background = B(accent);
        _text.FontSize = _size; _placeholder.FontSize = _size;
        _text.SetValue(TextBlock.LineHeightProperty, _size * 1.4);
        _flowTag.Visibility = _flow.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _flowTagText.Text = L.F("{0} · 第 {1} 步", _flow, _pos + 1);
        _dailyMark.Text = _daily ? L.T("每天") : "";
        _dailyMark.Foreground = B("#88000000");
        _deskIcon.Visibility = _arrow ? Visibility.Visible : Visibility.Hidden;
        Canvas.SetLeft(_note, _arrow ? 104 : 66 - Tail);
        _hint.Text = _arrow ? L.T("保存后把便签拖到要指的东西右边，小尖角对准它。") : L.T("保存后把便签拖到想放的地方。");
        DrawBubble();
    }

    // ================= 右：四块设置 =================
    FrameworkElement BuildSettings()
    {
        var sp = new StackPanel { Margin = new Thickness(22, 16, 22, 0) };

        // ① 外观
        var colorRow = Row();
        colorRow.Children.Add(Label(L.T("颜色")));
        foreach (var k in new[] { "yellow", "red", "green", "blue" }) colorRow.Children.Add(ColorDot(k));
        colorRow.Children.Add(Label(L.T("字号"), new Thickness(18, 0, 12, 0)));
        colorRow.Children.Add(Seg(new[] { 14.0, 18, 24, 32 }.Select((v, i) => new SegItem("A", 12 + i * 3.3, () => _size == v, () => _size = v, 42, true)).ToArray()));
        var shapeRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        shapeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shapeRow.ColumnDefinitions.Add(new ColumnDefinition());
        shapeRow.Children.Add(Label(L.T("样式")));
        var shapes = TwoTiles(
            Tile(PicPointer(), L.T("指着东西"), null, () => _arrow, () => _arrow = true, small: true),
            Tile(PicPlain(), L.T("普通便签"), null, () => !_arrow, () => _arrow = false, small: true));
        Grid.SetColumn(shapes, 1);
        shapeRow.Children.Add(shapes);
        sp.Children.Add(Section(1, L.T("外观"), null, colorRow, shapeRow));

        // ② 放在哪一层
        sp.Children.Add(Section(2, L.T("放在哪一层"), null, TwoTiles(
            Tile(PicDesk(), L.T("贴在桌面上"), L.T("窗口会挡住它，像桌面图标一样"), () => !_top, () => _top = false, small: false),
            Tile(PicTop(), L.T("浮在最上面"), L.T("盖在所有窗口上，指软件里的按钮"), () => _top, () => _top = true, small: false))));

        // ③ 什么时候出现
        var modes = Row();
        modes.Children.Add(Seg(new SegItem(L.T("每天提醒"), 13, () => _daily, () => _daily = true, 0, false), new SegItem(L.T("只提醒一次"), 13, () => !_daily, () => _daily = false, 0, false)));
        var when = Seg(new SegItem(L.T("一直在"), 13, () => !_timed, () => _timed = false, 0, false), new SegItem(L.T("到点才出现"), 13, () => _timed, () => _timed = true, 0, false));
        when.Margin = new Thickness(10, 0, 0, 0);
        modes.Children.Add(when);
        sp.Children.Add(Section(3, L.T("什么时候出现"), null, modes, BuildTimeline(), AfterRow()));

        // ④ 接力
        _chainBox.Visibility = Visibility.Collapsed;
        var flowSec = Section(4, L.T("接力"), L.T("几张便签排队，前一张点 OK 才出下一张"), _flowChips, _chainBox, _moveRow);
        flowSec.BorderThickness = new Thickness(0);
        sp.Children.Add(flowSec);
        _refresh.Add(BuildFlow);

        // 底部按钮
        var ok = new Button { Content = L.T("保存"), Style = (Style)Application.Current.FindResource("BtnG"), MinWidth = 84, Height = 34, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = L.T("取消"), Style = (Style)Application.Current.FindResource("BtnW"), IsCancel = true, MinWidth = 84, Height = 34 };
        ok.Click += (_, _) => Save();
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btns.Children.Add(cancel); btns.Children.Add(ok);
        var foot = new Border { BorderBrush = B(LineC), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 16), Margin = new Thickness(0, 6, 0, 0), Child = btns };

        var dock = new DockPanel { Margin = new Thickness(22, 0, 22, 0) };
        DockPanel.SetDock(foot, Dock.Bottom);
        dock.Children.Add(foot);
        sp.Margin = new Thickness(0, 16, 0, 0);
        dock.Children.Add(sp);
        return dock;
    }

    Border Section(int num, string title, string? note, params FrameworkElement[] content)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var circle = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = B(Soft), Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock { Text = num.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = B(Sub), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        head.Children.Add(circle);
        if (note != null)
        {
            var t = new TextBlock { Text = note, Foreground = B(Mute), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            DockPanel.SetDock(t, Dock.Right);
            head.Children.Add(t);
        }
        head.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = B(Ink), VerticalAlignment = VerticalAlignment.Center });
        var sp = new StackPanel();
        sp.Children.Add(head);
        foreach (var c in content) sp.Children.Add(c);
        return new Border { Child = sp, Padding = new Thickness(0, 12, 0, 14), BorderBrush = B(LineC), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    static StackPanel Row() => new() { Orientation = Orientation.Horizontal };

    static TextBlock Label(string t, Thickness? m = null) => new()
    {
        Text = t, Foreground = B(Sub), VerticalAlignment = VerticalAlignment.Center, MinWidth = 34, Margin = m ?? new Thickness(0, 0, 12, 0),
    };

    void Refresh()
    {
        foreach (var r in _refresh) r();
        RefreshPreview();
        DrawTimeline();
    }

    // ---------- 颜色点 ----------
    FrameworkElement ColorDot(string key)
    {
        var accent = B(GuideWindow.Colors[key].Accent);
        var ring = new Ellipse { Width = 34, Height = 34, Stroke = accent, StrokeThickness = 2 };
        var dot = new Ellipse { Width = 26, Height = 26, Fill = B(DotFill[key]), Stroke = B("#14000000"), StrokeThickness = 1.5 };
        var check = new TextBlock { Text = "✓", Foreground = accent, FontWeight = FontWeights.Bold, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var g = new Grid { Width = 34, Height = 34, Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 4, 0) };
        g.Children.Add(ring); g.Children.Add(dot); g.Children.Add(check);
        g.MouseLeftButtonUp += (_, _) => { _color = key; Refresh(); };
        _refresh.Add(() => ring.Visibility = check.Visibility = _color == key ? Visibility.Visible : Visibility.Hidden);
        return g;
    }

    // ---------- 分段按钮 ----------
    record SegItem(string Text, double Size, Func<bool> IsOn, Action Pick, double MinW, bool Bold);

    static readonly Effect SegShadow = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.14 };

    Border Seg(params SegItem[] items)
    {
        var sp = Row();
        foreach (var it in items)
        {
            var t = new TextBlock { Text = it.Text, FontSize = it.Size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(it.MinW > 0 ? 0 : 12, 0, it.MinW > 0 ? 0 : 12, 0), Height = 30, MinWidth = it.MinW, Child = t, Cursor = Cursors.Hand, Margin = new Thickness(1, 0, 1, 0) };
            b.MouseLeftButtonUp += (_, _) => { it.Pick(); Refresh(); };
            _refresh.Add(() =>
            {
                bool on = it.IsOn();
                b.Background = on ? Brushes.White : Brushes.Transparent;
                b.Effect = on ? SegShadow : null;
                t.Foreground = B(on ? Ink : Sub);
                t.FontWeight = on || it.Bold ? FontWeights.SemiBold : FontWeights.Normal;
            });
            sp.Children.Add(b);
        }
        return new Border { Background = B(Soft), CornerRadius = new CornerRadius(8), Padding = new Thickness(2, 3, 2, 3), Child = sp, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    }

    // ---------- 带图的选择块 ----------
    static Grid TwoTiles(FrameworkElement a, FrameworkElement b)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(a);
        Grid.SetColumn(b, 2);
        g.Children.Add(b);
        return g;
    }

    FrameworkElement Tile(FrameworkElement pic, string title, string? desc, Func<bool> isOn, Action pick, bool small)
    {
        var badge = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = B(Green), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = "✓", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var tt = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = B(Ink), VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement content;
        if (small)
        {
            var row = Row();
            pic.Width = 64; pic.Height = 26; pic.Margin = new Thickness(0, 0, 10, 0);
            row.Children.Add(pic); row.Children.Add(tt);
            content = row;
        }
        else
        {
            var col = new StackPanel();
            pic.Height = 50; pic.HorizontalAlignment = HorizontalAlignment.Center;
            col.Children.Add(pic);
            tt.Margin = new Thickness(0, 7, 0, 0);
            col.Children.Add(tt);
            if (desc != null) col.Children.Add(new TextBlock { Text = desc, Foreground = B(Mute), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            content = col;
        }
        var g = new Grid();
        g.Children.Add(content);
        g.Children.Add(badge);
        var b = new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), Padding = small ? new Thickness(10, 6, 8, 6) : new Thickness(10, 10, 8, 9),
            Cursor = Cursors.Hand, Child = g,
        };
        void Paint(bool hover)
        {
            bool on = isOn();
            b.BorderBrush = B(on ? Green : hover ? "#CFD4D9" : LineC);
            b.Background = on ? B(Tint) : Brushes.White;
            badge.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        }
        b.MouseEnter += (_, _) => Paint(true);
        b.MouseLeave += (_, _) => Paint(false);
        b.MouseLeftButtonUp += (_, _) => { pick(); Refresh(); };
        _refresh.Add(() => Paint(b.IsMouseOver));
        return b;
    }

    // ---------- 小图（矢量，在 Viewbox 里等比缩放） ----------
    static Border Box(double x, double y, double w, double h, string fill, CornerRadius r, string? stroke = null, double st = 0)
    {
        var b = new Border { Width = w, Height = h, Background = B(fill), CornerRadius = r };
        if (stroke != null) { b.BorderBrush = B(stroke); b.BorderThickness = new Thickness(st); }
        Canvas.SetLeft(b, x); Canvas.SetTop(b, y);
        return b;
    }
    static CornerRadius R(double r) => new(r);

    static Path BubblePic(double x, double y, double w, double h, bool tail, double st, bool shadow = false)
    {
        var p = new Path { Data = GuideWindow.Bubble(new Rect(x, y, w, h), tail), Fill = B("#FFF7D6"), Stroke = B("#D99A1E"), StrokeThickness = st, StrokeLineJoin = PenLineJoin.Round };
        if (shadow) p.Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 3, Direction = 270, Opacity = 0.3 };
        return p;
    }

    static Viewbox Pic(double w, double h, params UIElement[] els)
    {
        var c = new Canvas { Width = w, Height = h };
        foreach (var e in els) c.Children.Add(e);
        return new Viewbox { Child = c, Stretch = Stretch.Uniform };
    }

    static FrameworkElement PicPointer() => Pic(140, 56,
        Box(4, 17, 22, 22, "#C9CED3", R(6)),
        BubblePic(50, 6, 86, 44, true, 2.6),
        Box(62, 18, 48, 6, "#E2C77E", R(3)), Box(62, 30, 32, 6, "#E2C77E", R(3)));

    static FrameworkElement PicPlain() => Pic(140, 56,
        BubblePic(27, 6, 86, 44, false, 2.6),
        Box(39, 18, 48, 6, "#E2C77E", R(3)), Box(39, 30, 32, 6, "#E2C77E", R(3)));

    static FrameworkElement PicDesk() => Pic(320, 128,
        Box(0, 0, 320, 128, "#6D8FA8", R(12)),
        BubblePic(28, 28, 112, 60, false, 3),
        Box(44, 44, 68, 8, "#E2C77E", R(4)), Box(44, 60, 44, 8, "#E2C77E", R(4)),
        Box(104, 40, 192, 80, "#FFFFFF", R(8), "#D0D5DA", 2),
        Box(105, 41, 190, 17, "#EEF0F2", new CornerRadius(7, 7, 0, 0)));

    static FrameworkElement PicTop() => Pic(320, 128,
        Box(0, 0, 320, 128, "#6D8FA8", R(12)),
        Box(80, 16, 216, 100, "#FFFFFF", R(8), "#D0D5DA", 2),
        Box(81, 17, 214, 17, "#EEF0F2", new CornerRadius(7, 7, 0, 0)),
        Box(112, 58, 26, 26, "#C9CED3", R(6)),
        BubblePic(166, 46, 112, 48, true, 3, shadow: true),
        Box(182, 60, 64, 8, "#E2C77E", R(4)), Box(182, 75, 40, 8, "#E2C77E", R(4)));

    // ---------- 时间条：早 5:00 → 次日 5:00 ----------
    FrameworkElement BuildTimeline()
    {
        var track = new Border { Height = 30, CornerRadius = R(8), Background = B(Soft) };
        _tl.Children.Add(track);
        _tlHidden.Background = new LinearGradientBrush(new GradientStopCollection
        {
            new((Color)ColorConverter.ConvertFromString("#EEF0F2"), 0), new((Color)ColorConverter.ConvertFromString("#EEF0F2"), 0.5),
            new((Color)ColorConverter.ConvertFromString("#E3E6E9"), 0.5), new((Color)ColorConverter.ConvertFromString("#E3E6E9"), 1),
        }, new Point(0, 0), new Point(8, 8)) { MappingMode = BrushMappingMode.Absolute, SpreadMethod = GradientSpreadMethod.Repeat };
        _tl.Children.Add(_tlHidden);
        _tlShown.Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#2E07C255"), (Color)ColorConverter.ConvertFromString("#1A07C255"), 0);
        _tlShown.BorderBrush = B("#7307C255");
        _tlShownText.Foreground = B(GreenD);
        _tlShown.Child = _tlShownText;
        _tl.Children.Add(_tlShown);

        // 拖动的针：竖线 + 中间的圆点 + 上面的时间牌
        _pin.Children.Add(Place(new Rectangle { Width = 2, Height = 36, Fill = B(Green) }, -1, -3));
        _pin.Children.Add(Place(new Ellipse { Width = 13, Height = 13, Fill = Brushes.White, Stroke = B(Green), StrokeThickness = 2.5, Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = 0.2 } }, -6.5, 8.5));
        _knob.Background = B(Green);
        _knob.Child = _knobText;
        _knob.Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Direction = 270, Opacity = 0.3, Color = (Color)ColorConverter.ConvertFromString(Green) };
        _pin.Children.Add(_knob);
        Canvas.SetTop(_knob, -27);
        _knob.SizeChanged += (_, _) => Canvas.SetLeft(_knob, -_knob.ActualWidth / 2);
        _pin.Children.Add(Place(new Path { Data = Geometry.Parse("M0,0 L8,0 L4,4 Z"), Fill = B(Green) }, -4, -6.5));
        _tl.Children.Add(_pin);
        _tlTip.Text = L.T("想到点才出现？在时间条上点一下");
        _tlTip.Foreground = B(Mute);
        _tl.Children.Add(Place(_tlTip, 0, -24));

        _tl.MouseLeftButtonDown += (_, e) => { _timed = true; _tl.CaptureMouse(); SetTime(e.GetPosition(_tl).X); Refresh(); e.Handled = true; };
        _tl.MouseMove += (_, e) => { if (_tl.IsMouseCaptured) { SetTime(e.GetPosition(_tl).X); Refresh(); } };
        _tl.MouseLeftButtonUp += (_, _) => _tl.ReleaseMouseCapture();
        _tl.MouseWheel += (_, e) =>
        {
            if (!_timed) return;
            _minutes = Snap(_minutes + (e.Delta > 0 ? 5 : -5));
            Refresh(); e.Handled = true;
        };
        _tl.ToolTip = L.T("拖动选出现时间；滚轮按 5 分钟微调");

        var sp = new StackPanel { Margin = new Thickness(0, 30, 0, 0) };
        sp.Children.Add(_tl);
        sp.Children.Add(_ticks);
        return sp;
    }

    static T Place<T>(T e, double x, double y) where T : UIElement { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); return e; }

    /// <summary>分钟数归到 0～1439，按 5 分钟对齐。</summary>
    static int Snap(int m) => ((int)Math.Round(m / 5.0) * 5 % 1440 + 1440) % 1440;

    void SetTime(double x)
    {
        double w = _tl.ActualWidth;
        if (w <= 0) return;
        double f = Math.Clamp(x / w, 0, 1439.0 / 1440);
        _minutes = Snap(DayStart + (int)Math.Round(f * 1440));
    }

    static string Hm(int m) => $"{m / 60:00}:{m % 60:00}";

    void DrawTimeline()
    {
        double w = _tl.ActualWidth;
        if (w <= 0) return;
        double f = ((_minutes - DayStart) % 1440 + 1440) % 1440 / 1440.0;
        double x = _timed ? Math.Round(f * w) : 0;
        ((Border)_tl.Children[0]).Width = w;
        _tlHidden.Width = x; _tlHidden.Visibility = _timed && x > 0 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_tlShown, x);
        _tlShown.Width = Math.Max(0, w - x);
        _tlShown.CornerRadius = _timed && x > 0 ? new CornerRadius(0, 8, 8, 0) : R(8);
        _tlShownText.Text = _timed ? L.F("{0} 起显示", Hm(_minutes)) : _daily ? L.T("全天显示") : L.T("一直显示，直到点 OK");
        _tlShownText.Visibility = w - x > 90 ? Visibility.Visible : Visibility.Hidden;
        _pin.Visibility = _timed ? Visibility.Visible : Visibility.Collapsed;
        _tlTip.Visibility = _timed ? Visibility.Collapsed : Visibility.Visible;
        Canvas.SetLeft(_pin, x);
        _knobText.Text = Hm(_minutes);

        if (_ticks.Children.Count == 0 || Math.Abs(_ticks.Width - w) > 0.5)
        {
            _ticks.Children.Clear(); _ticks.Width = w;
            var marks = new[] { (0, L.T("早 5:00")), (7, "12:00"), (13, "18:00"), (19, "0:00"), (24, L.T("次日 5:00")) };
            var placed = new List<(TextBlock Tb, double X, double W)>();
            foreach (var (h, t) in marks)
            {
                var tb = new TextBlock { Text = t, FontSize = 11, Foreground = B(Mute) };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double tw = tb.DesiredSize.Width, tx = h / 24.0 * w - (h == 0 ? 0 : h == 24 ? tw : tw / 2);
                Canvas.SetLeft(tb, tx);
                placed.Add((tb, tx, tw));
            }
            // 字太长会挤在一起（比如英文）：两头的一定留着，中间的和别人重叠就不显示
            for (int i = 1; i < placed.Count - 1; i++)
            {
                var (_, x0, w0) = placed[i];
                bool hit = placed.Where((p, j) => j != i && (j == 0 || j == placed.Count - 1 || _ticks.Children.Contains(p.Tb)))
                                 .Any(p => x0 < p.X + p.W + 8 && p.X < x0 + w0 + 8);
                if (!hit) _ticks.Children.Add(placed[i].Tb);
            }
            _ticks.Children.Add(placed[0].Tb);
            _ticks.Children.Add(placed[^1].Tb);
        }
    }

    FrameworkElement AfterRow()
    {
        var row = Row();
        row.Margin = new Thickness(0, 8, 0, 0);
        Border Chip(UIElement child) => new() { Background = B(Soft), CornerRadius = R(10), Padding = new Thickness(10, 3, 10, 4), Child = child };
        row.Children.Add(Chip(new TextBlock { Text = L.T("点 OK"), Foreground = B(Sub), FontSize = 12 }));
        row.Children.Add(new TextBlock { Text = "→", Foreground = B("#C4C8CC"), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(Chip(_after));
        _refresh.Add(() => _after.Text = _daily
            ? (_timed ? L.F("今天收起，明早 5 点后、到 {0} 再出现", Hm(_minutes)) : L.T("今天收起，明早 5 点再出现"))
            : L.T("收起，以后不再出现"));
        return row;
    }

    // ---------- 接力 ----------
    List<GuideNote> Others(string flow) =>
        flow.Length == 0 ? new() : GuideStore.D.Notes.Where(o => o != _n && o.Flow == flow).OrderBy(o => o.Step).ThenBy(o => o.Created).ToList();

    IEnumerable<string> AllFlows() => GuideStore.Flows.Concat(_newFlows).Append(_n.Flow).Where(f => f.Length > 0).Distinct();

    void PickFlow(string f)
    {
        _flow = f;
        _pos = f.Length == 0 ? 0 : f == _n.Flow ? Math.Min(Others(f).Count(o => o.Step < _n.Step || (o.Step == _n.Step && o.Created < _n.Created)), Others(f).Count) : Others(f).Count;
        Refresh();
    }

    void BuildFlow()
    {
        _flowChips.Children.Clear();
        _flowChips.Children.Add(Chip(L.T("不接力"), _flow.Length == 0, () => PickFlow("")));
        foreach (var f in AllFlows()) _flowChips.Children.Add(Chip(f, _flow == f, () => PickFlow(f)));
        _flowChips.Children.Add(AddChip());

        bool on = _flow.Length > 0;
        _chainBox.Visibility = _moveRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _chain.Children.Clear();
        if (!on) return;
        var list = Others(_flow).Cast<GuideNote?>().ToList();
        list.Insert(_pos, null);
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) _chain.Children.Add(Arrow());
            _chain.Children.Add(Step(i + 1, list[i]));
        }
        Usable(_left, _pos > 0);
        Usable(_right, _pos < list.Count - 1);
        _posText.Text = L.F("这张现在是第 {0} 张", _pos + 1);
    }

    static FrameworkElement Chip(string text, bool on, Action click)
    {
        var b = new Border
        {
            CornerRadius = R(16), BorderThickness = new Thickness(1.5), Padding = new Thickness(12, 3, 12, 4), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
            BorderBrush = B(on ? Green : LineC), Background = on ? B(Tint) : Brushes.White,
            Child = new TextBlock { Text = text, Foreground = B(on ? GreenD : Sub), FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal },
        };
        if (!on)
        {
            b.MouseEnter += (_, _) => b.BorderBrush = B("#CFD4D9");
            b.MouseLeave += (_, _) => b.BorderBrush = B(LineC);
        }
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    /// <summary>“＋ 新的一串”：点了原地变成输入框，回车确定。</summary>
    FrameworkElement AddChip()
    {
        var g = new Grid { Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        g.Children.Add(new Rectangle { RadiusX = 15, RadiusY = 15, Stroke = B("#C9CED3"), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2 } });
        g.Children.Add(new TextBlock { Text = L.T("＋ 新的一串"), Foreground = B(Mute), Margin = new Thickness(12, 4, 12, 5) });
        g.MouseLeftButtonUp += (_, _) =>
        {
            int i = _flowChips.Children.IndexOf(g);
            var box = new TextBox { Width = 120, Height = 28, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 8), BorderBrush = B(Green), Padding = new Thickness(6, 0, 6, 0) };
            var tip = new TextBlock { Text = L.T("起个名字，例如“开播前”，回车确定"), Foreground = B(Mute), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            _flowChips.Children.RemoveAt(i);
            _flowChips.Children.Insert(i, box);
            _flowChips.Children.Insert(i + 1, tip);
            bool done = false;
            void Finish(bool keep)
            {
                if (done) return;
                done = true;
                var name = box.Text.Trim();
                if (keep && name.Length > 0 && name != L.T("不接力"))
                {
                    if (!AllFlows().Contains(name)) _newFlows.Add(name);
                    PickFlow(name);
                }
                else Refresh();
            }
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
                else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
            };
            box.LostKeyboardFocus += (_, _) => Finish(true);
            box.Focus();
        };
        return g;
    }

    FrameworkElement Step(int num, GuideNote? n)
    {
        bool me = n == null;
        var text = me ? L.T("这一张") : FirstLine(n!.Text);
        var sp = new StackPanel();
        sp.Children.Add(new Border
        {
            Width = 18, Height = 18, CornerRadius = R(9), Background = B(me ? Green : Soft), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = num.ToString(), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = me ? Brushes.White : B(Sub), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Margin = new Thickness(0, 5, 0, 0), Foreground = B(me ? GreenD : Sub), FontWeight = me ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = me ? null : n!.Text });
        return new Border
        {
            Width = 92, CornerRadius = R(10), BorderThickness = new Thickness(1.5), Padding = new Thickness(8, 7, 8, 6), Child = sp,
            BorderBrush = B(me ? Green : LineC), Background = me ? B(Tint) : Brushes.White,
            Effect = me ? new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Direction = 270, Opacity = 0.18, Color = (Color)ColorConverter.ConvertFromString(Green) } : null,
        };
    }

    static string FirstLine(string s)
    {
        var t = s.Trim().Split('\n')[0].Trim();
        return t.Length > 0 ? t : L.T("（空）");
    }

    static FrameworkElement Arrow()
    {
        var g = new Grid { Width = 22, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(new Rectangle { Height = 2, Fill = B("#D7DBDF"), Margin = new Thickness(0, 0, 4, 0) });
        g.Children.Add(new Path { Data = Geometry.Parse("M0,0 L5,4 L0,8 Z"), Fill = B("#D7DBDF"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
        return g;
    }

    /// <summary>到头了的按钮：淡一点、点不动（比系统的灰底禁用样式轻）。</summary>
    static void Usable(Button b, bool on) { b.Opacity = on ? 1 : 0.4; b.IsHitTestVisible = on; }

    Button SmallBtn(string text, Action a)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.FindResource("BtnW"), Padding = new Thickness(10, 3, 10, 3), FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => { a(); Refresh(); };
        return b;
    }

    // ================= 保存 =================
    void Save()
    {
        Commit();
        DialogResult = true;
    }

    void Commit()
    {
        _n.Text = MemoText.Clean(_text.Text);
        _n.Color = _color;
        _n.FontSize = _size;
        _n.Arrow = _arrow;
        _n.Topmost = _top;
        _n.Daily = _daily;
        _n.ShowAt = _timed ? Hm(_minutes) : "";

        // 接力：按排好的顺序重新编 1、2、3……；原来那一串少了这张，也重新编号，不留空号
        var oldFlow = _n.Flow;
        if (_flow.Length > 0)
        {
            var list = Others(_flow);
            list.Insert(Math.Clamp(_pos, 0, list.Count), _n);
            for (int i = 0; i < list.Count; i++) { list[i].Flow = _flow; list[i].Step = i + 1; }
        }
        else { _n.Flow = ""; _n.Step = 0; }
        if (oldFlow.Length > 0 && oldFlow != _flow)
        {
            var rest = Others(oldFlow);
            for (int i = 0; i < rest.Count; i++) rest[i].Step = i + 1;
        }
    }

    // 沙盒测试用：不点界面，直接设接力位置 / 定时，然后保存
    internal void TestSet(string flow, int pos, string at)
    {
        _flow = flow; _pos = pos;
        _timed = at.Length > 0;
        if (_timed) _minutes = (int)TimeSpan.Parse(at).TotalMinutes;
        Refresh();
    }
    internal void TestCommit() => Commit();

    public static bool Ask(Window? owner, GuideNote n, bool isNew)
    {
        var w = new GuideEditor(n, isNew);
        if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        return w.ShowDialog() == true;
    }
}
