using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DeskHub.Core;
using DeskHub.Interop;

namespace DeskHub.UI;

/// <summary>
/// 屏幕上的一张指引便签：对话气泡的样子，左边一个小尖角。便签永远放在要指的东西右边，用尖角对准它。
/// 窗口是透明的，大小刚好包住便签（含阴影）；透明的地方点击会穿透到下面。
/// 没锁定时：拖便签移动，右下角拉宽，右键菜单改设置，双击改文字。
/// 锁定后：只能点 OK。
/// </summary>
public sealed class GuideWindow : Window
{
    public GuideNote Note { get; private set; }
    public bool IsTopmostNote { get; }
    public bool CountingDown => _countdown > 0;

    readonly Canvas _canvas = new();
    readonly Action<GuideNote> _edit;
    readonly Action _openManager;
    Border _card = null!;
    int _countdown;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    const double Pad = 18;

    public static readonly Dictionary<string, (string Card, string Border, string Accent)> Colors = new()
    {
        ["yellow"] = ("#FFF7D6", "#EAD58E", "#D99A1E"),
        ["red"] = ("#FFECEA", "#F0B9B4", "#E0524C"),
        ["green"] = ("#E9F8EF", "#A9DFBE", "#07A548"),
        ["blue"] = ("#E8F1FF", "#B3CDF2", "#3A7BE0"),
    };

    public GuideWindow(GuideNote n, Action<GuideNote> edit, Action openManager)
    {
        Note = n; _edit = edit; _openManager = openManager;
        IsTopmostNote = n.Topmost;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; ShowActivated = false;
        Topmost = n.Topmost;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        UseLayoutRounding = true;
        Content = _canvas;
        _tick.Tick += (_, _) => Tick();
        SourceInitialized += (_, _) =>
        {
            if (!IsTopmostNote) DesktopHost.Pin(this);
            else
            {
                // 置顶的：不进 Alt+Tab
                var h = new WindowInteropHelper(this).Handle;
                var ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
                Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
            }
        };
        Build();
    }

    static bool Locked => GuideStore.D.Locked;
    static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    /// <summary>便签内容或设置变了：重新画。</summary>
    public void Refresh(GuideNote n) { Note = n; Build(); }

    // ================= 画 =================
    void Build()
    {
        var n = Note;
        var (cardC, borderC, accentC) = Colors.TryGetValue(n.Color, out var c) ? c : Colors["yellow"];
        var accent = B(accentC);

        var stack = new StackPanel();
        // 顶部：流程步骤 + 菜单
        if (n.Flow.Length > 0 || !Locked)
        {
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            if (!Locked)
            {
                var menu = new TextBlock { Text = "⋯", FontSize = 18, Foreground = B("#99000000"), Cursor = Cursors.Hand, Padding = new Thickness(8, 0, 0, 0), ToolTip = L.T("便签设置") };
                menu.MouseLeftButtonUp += (_, e) => { ShowMenu(menu); e.Handled = true; };
                DockPanel.SetDock(menu, Dock.Right);
                top.Children.Add(menu);
            }
            if (n.Flow.Length > 0)
                top.Children.Add(new Border
                {
                    Background = accent, CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock { Text = L.F("{0} · 第 {1} 步", n.Flow, n.Step), Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold },
                });
            else top.Children.Add(new FrameworkElement());
            stack.Children.Add(top);
        }
        stack.Children.Add(new TextBlock
        {
            Text = n.Text.Length > 0 ? n.Text : L.T("（双击写内容）"), FontSize = n.FontSize, FontWeight = FontWeights.SemiBold,
            Foreground = B(n.Text.Length > 0 ? "#222222" : "#99000000"), TextWrapping = TextWrapping.Wrap, LineHeight = n.FontSize * 1.4,
        });
        stack.Children.Add(BottomRow(accent));

        // 文字层：透明底，外形（圆角卡片 + 左边的小尖角）单独画成一个整体，边框和阴影才是连着的
        _card = new Border
        {
            Width = n.W, Background = Brushes.Transparent, Padding = new Thickness(16, 12, 14, 12), Child = stack,
            Cursor = Locked ? Cursors.Arrow : Cursors.SizeAll,
        };
        if (!Locked) HookCardDrag(_card);

        _card.Measure(new Size(n.W, double.PositiveInfinity));
        double h = Math.Max(_card.DesiredSize.Height, TailBottom + Radius);
        bool tail = n.Arrow;

        // 窗口 = 卡片 + 尖角 + 阴影余量
        double left = Pad + (tail ? TailLen : 0);
        Left = n.X - left; Top = n.Y - Pad;
        Width = left + n.W + Pad; Height = Pad + h + Pad;
        _canvas.Children.Clear();
        _canvas.Width = Width; _canvas.Height = Height;

        var shape = new Path
        {
            Data = Bubble(new Rect(left, Pad, n.W, h), tail),
            Fill = B(cardC), Stroke = Locked ? B(borderC) : accent, StrokeThickness = Locked ? 1 : 1.5, StrokeLineJoin = PenLineJoin.Round,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.22, RenderingBias = RenderingBias.Performance },
        };
        _shape = shape;
        _canvas.Children.Add(shape);
        Canvas.SetLeft(_card, left); Canvas.SetTop(_card, Pad);
        _card.Height = h;
        _canvas.Children.Add(_card);

        if (!Locked)
        {
            // 右下角拉宽
            var grip = new Border { Width = 14, Height = 14, Background = Brushes.Transparent, Cursor = Cursors.SizeWE, Child = new Path { Data = Geometry.Parse("M12,3 L3,12 M12,8 L8,12"), Stroke = B("#55000000"), StrokeThickness = 1.2 } };
            Canvas.SetLeft(grip, left + n.W - 17); Canvas.SetTop(grip, Pad + h - 17);
            HookResize(grip);
            _canvas.Children.Add(grip);
        }
    }

    // 外形尺寸：卡片圆角；小尖角长在左边靠上，尖朝左上（像对话气泡）
    const double Radius = 10, TailTop = 10, TailBottom = 32, TailLen = 13;

    /// <summary>
    /// 圆角卡片 + 左侧小尖角，一笔画成（边框连续、阴影一体）。尖角两边是微微内凹的弧线，尖端略圆，看起来更柔和。
    /// 尖端位置 = 卡片左上角往左 TailLen、往下 3：便签放在要指的东西右边，用尖端对准它。
    /// </summary>
    static Geometry Bubble(Rect r, bool tail)
    {
        double x = r.X, y = r.Y, R = r.Right, Bm = r.Bottom, k = Radius;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x + k, y), true, true);
            c.LineTo(new Point(R - k, y), true, true);
            c.ArcTo(new Point(R, y + k), new Size(k, k), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(R, Bm - k), true, true);
            c.ArcTo(new Point(R - k, Bm), new Size(k, k), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(x + k, Bm), true, true);
            c.ArcTo(new Point(x, Bm - k), new Size(k, k), 0, false, SweepDirection.Clockwise, true, true);
            if (tail)
            {
                double ty = y + 3, tx = x - TailLen;   // 尖端：略高于卡片顶边下一点，朝左上
                c.LineTo(new Point(x, y + TailBottom), true, true);
                // 下边：从卡片边缘弯出去到尖端（内凹）
                c.QuadraticBezierTo(new Point(x - 2, y + 16), new Point(tx + 1.2, ty + 1.6), true, true);
                // 尖端小圆头
                c.QuadraticBezierTo(new Point(tx - 0.8, ty - 0.4), new Point(tx + 1.6, ty - 0.2), true, true);
                // 上边：回到卡片边缘（内凹）
                // 上边：顺着接进左上圆角（TailTop = 圆角半径，正好接上，不留缺口）
                c.QuadraticBezierTo(new Point(x - 4, y + 5), new Point(x, y + TailTop), true, true);
            }
            c.LineTo(new Point(x, y + k), true, true);
            c.ArcTo(new Point(x + k, y), new Size(k, k), 0, false, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    Path? _shape;

    FrameworkElement BottomRow(Brush accent)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        if (_countdown > 0)
        {
            var undo = new TextBlock { Text = L.T("撤销"), Foreground = accent, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            undo.MouseLeftButtonUp += (_, e) => { _tick.Stop(); _countdown = 0; Opacity = 1; Build(); e.Handled = true; };
            DockPanel.SetDock(undo, Dock.Right);
            row.Children.Add(undo);
            var t = new TextBlock { Text = L.F("{0} 秒后关闭", _countdown), Foreground = B("#666666"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(t, Dock.Right);
            row.Children.Add(t);
            return row;
        }
        var ok = new Button
        {
            Content = "OK", MinWidth = 76, Height = 32, FontSize = 15, FontWeight = FontWeights.Bold, Cursor = Cursors.Hand,
            Background = B("#07C255"), Foreground = Brushes.White, BorderThickness = new Thickness(0), Focusable = false,
            ToolTip = Note.Daily ? L.T("做完了：今天不再显示，明天再出现") : L.T("做完了：这张便签收起来"),
        };
        ok.Template = OkTemplate();
        ok.Click += (_, _) => StartCountdown();
        DockPanel.SetDock(ok, Dock.Right);
        row.Children.Add(ok);
        if (Note.Daily)
            row.Children.Add(new TextBlock { Text = L.T("每天"), Foreground = B("#88000000"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    static ControlTemplate OkTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        return t;
    }

    // ================= OK：3 秒后收起 =================
    void StartCountdown()
    {
        _countdown = 3;
        Build();
        _tick.Start();
    }

    void Tick()
    {
        _countdown--;
        if (_countdown > 0) { Build(); return; }
        _tick.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) => GuideStore.Ok(Note); // 存盘后 ToolHub 会关掉这个窗口
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>到点出现时闪几下，引起注意。</summary>
    public void Flash()
    {
        var a = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(350)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(4) };
        BeginAnimation(OpacityProperty, a);
    }

    // ================= 拖动 =================
    /// <summary>鼠标在屏幕上的位置（设备无关像素）：窗口位置 + 窗口内坐标。窗口跟着变，用它算才不会抖。</summary>
    Point Cursor(MouseEventArgs e) { var p = e.GetPosition(this); return new Point(Left + p.X, Top + p.Y); }

    void HookCardDrag(Border card)
    {
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { _edit(Note); e.Handled = true; return; }
            // 拖动交给窗口处理（拖的过程中会重画，便签的 Border 会换新的）
            _dragCard = true; _moved = false; _tipStart = Cursor(e); _tip0 = new Point(Note.X, Note.Y);
            CaptureMouse(); e.Handled = true;
        };
        card.MouseRightButtonUp += (_, e) => { ShowMenu(card); e.Handled = true; };
    }

    bool _dragCard, _moved;

    void HookResize(Border g)
    {
        g.MouseLeftButtonDown += (_, e) => { _dragW = true; _tipStart = Cursor(e); _w0 = Note.W; CaptureMouse(); e.Handled = true; };
    }

    bool _dragW; Point _tipStart, _tip0; double _w0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!(_dragW || _dragCard) || e.LeftButton != MouseButtonState.Pressed) return;
        var d = Cursor(e) - _tipStart;
        if (_dragCard)
        {
            if (!_moved && d.Length < 3) return;
            _moved = true;
            Note.X = Math.Round(_tip0.X + d.X); Note.Y = Math.Round(_tip0.Y + d.Y);
        }
        else Note.W = Math.Clamp(Math.Round(_w0 + d.X), 160, 900);
        Build();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!(_dragW || _dragCard)) return;
        bool save = !_dragCard || _moved;
        _dragW = _dragCard = false;
        ReleaseMouseCapture();
        if (save) GuideStore.Save();
    }

    // ================= 右键菜单 =================
    void ShowMenu(FrameworkElement target)
    {
        if (Locked) return;
        var n = Note;
        var m = new ContextMenu { PlacementTarget = target };
        MenuItem Add(ItemsControl parent, string text, Action a, bool chk = false)
        {
            var mi = new MenuItem { Header = text, IsChecked = chk };
            mi.Click += (_, _) => { a(); GuideStore.Save(); };
            parent.Items.Add(mi);
            return mi;
        }
        var edit = new MenuItem { Header = L.T("编辑…") }; edit.Click += (_, _) => _edit(n); m.Items.Add(edit);
        m.Items.Add(new Separator());
        Add(m, L.T("显示小尖角"), () => n.Arrow = !n.Arrow, n.Arrow);
        Add(m, L.T("置顶（浮在所有窗口上面）"), () => n.Topmost = !n.Topmost, n.Topmost);
        Add(m, L.T("每天重复（点 OK 后明天再出现）"), () => n.Daily = true, n.Daily);
        Add(m, L.T("一次性（点 OK 后收起）"), () => n.Daily = false, !n.Daily);
        var col = new MenuItem { Header = L.T("颜色") };
        foreach (var (k, name) in new[] { ("yellow", L.T("黄")), ("red", L.T("红")), ("green", L.T("绿")), ("blue", L.T("蓝")) })
            Add(col, name, () => n.Color = k, n.Color == k);
        m.Items.Add(col);
        var size = new MenuItem { Header = L.T("字号") };
        foreach (var (v, name) in new[] { (14.0, L.T("小")), (18.0, L.T("中")), (24.0, L.T("大")), (32.0, L.T("特大")) })
            Add(size, name, () => n.FontSize = v, Math.Abs(n.FontSize - v) < 0.1);
        m.Items.Add(size);
        m.Items.Add(new Separator());
        Add(m, L.T("锁定所有便签（只能点 OK）"), () => GuideStore.D.Locked = true);
        var mgr = new MenuItem { Header = L.T("打开备忘录…") }; mgr.Click += (_, _) => _openManager(); m.Items.Add(mgr);
        m.Items.Add(new Separator());
        var del = new MenuItem { Header = L.T("删除这张便签") };
        del.Click += (_, _) =>
        {
            if (MessageBox.Show(L.T("删除这张便签？"), "New B Desk", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            GuideStore.D.Notes.Remove(n); GuideStore.Save();
        };
        m.Items.Add(del);
        m.IsOpen = true;
    }
}
