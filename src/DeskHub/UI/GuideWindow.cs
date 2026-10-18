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
/// 屏幕上的一张指引便签（带箭头）。窗口是透明的，大小刚好包住便签和箭头尖；透明的地方点击会穿透到下面。
/// 没锁定时：拖便签移动（箭头尖不动，箭头跟着转），拖箭头尖的圆点改指向，右下角拉宽，右键菜单改设置，双击改文字。
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
    const double Pad = 24, HandleR = 9;

    public static readonly Dictionary<string, (string Card, string Border, string Accent)> Colors = new()
    {
        ["yellow"] = ("#FFF4B8", "#E8C547", "#F08C00"),
        ["red"] = ("#FFE3E0", "#F2A39E", "#E5413C"),
        ["green"] = ("#DDF6E6", "#8FD5A8", "#07A548"),
        ["blue"] = ("#DCEBFF", "#9CC2F5", "#1F6FEB"),
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

        _card = new Border
        {
            Width = n.W, Background = B(cardC), BorderBrush = Locked ? B(borderC) : accent, BorderThickness = new Thickness(Locked ? 1.5 : 2),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 12), Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.3, RenderingBias = RenderingBias.Performance },
            Cursor = Locked ? Cursors.Arrow : Cursors.SizeAll,
        };
        if (!Locked) HookCardDrag(_card);

        _card.Measure(new Size(n.W, double.PositiveInfinity));
        double h = _card.DesiredSize.Height;
        var note = new Rect(n.X, n.Y, n.W, h);
        var tip = new Point(n.TipX, n.TipY);
        bool arrow = n.Arrow && !note.Contains(tip);
        var bound = note;
        if (arrow) bound.Union(new Rect(tip.X - HandleR - 4, tip.Y - HandleR - 4, (HandleR + 4) * 2, (HandleR + 4) * 2));
        bound.Inflate(Pad, Pad);

        Left = bound.X; Top = bound.Y; Width = bound.Width; Height = bound.Height;
        _canvas.Children.Clear();
        _canvas.Width = bound.Width; _canvas.Height = bound.Height;

        if (arrow)
        {
            var o = new Vector(-bound.X, -bound.Y);
            var from = Edge(note, tip) + o; var to = tip + o;
            foreach (var shape in ArrowShapes(from, to, accent)) _canvas.Children.Add(shape);
        }
        Canvas.SetLeft(_card, note.X - bound.X); Canvas.SetTop(_card, note.Y - bound.Y);
        _canvas.Children.Add(_card);

        if (!Locked)
        {
            // 右下角拉宽
            var grip = new Border { Width = 14, Height = 14, Background = Brushes.Transparent, Cursor = Cursors.SizeWE, Child = new Path { Data = Geometry.Parse("M12,2 L2,12 M12,7 L7,12"), Stroke = B("#66000000"), StrokeThickness = 1.2 } };
            Canvas.SetLeft(grip, note.Right - bound.X - 16); Canvas.SetTop(grip, note.Bottom - bound.Y - 16);
            HookResize(grip);
            _canvas.Children.Add(grip);
            if (arrow)
            {
                var handle = new Ellipse { Width = HandleR * 2, Height = HandleR * 2, Fill = accent, Stroke = Brushes.White, StrokeThickness = 2.5, Cursor = Cursors.Cross, ToolTip = L.T("拖动改变箭头指向") };
                Canvas.SetLeft(handle, tip.X - bound.X - HandleR); Canvas.SetTop(handle, tip.Y - bound.Y - HandleR);
                HookTipDrag(handle);
                _canvas.Children.Add(handle);
            }
        }
    }

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

    /// <summary>箭头从便签边框上（朝着箭头尖的方向）出发。</summary>
    static Point Edge(Rect r, Point tip)
    {
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        var d = tip - c;
        double tx = d.X == 0 ? double.MaxValue : (r.Width / 2) / Math.Abs(d.X);
        double ty = d.Y == 0 ? double.MaxValue : (r.Height / 2) / Math.Abs(d.Y);
        return c + d * Math.Min(tx, ty);
    }

    /// <summary>粗箭头：先画一圈白边（在深色、浅色背景上都看得清），再画彩色箭杆和箭头。</summary>
    static IEnumerable<Shape> ArrowShapes(Point from, Point to, Brush color)
    {
        var d = to - from;
        if (d.Length < 1) yield break;
        d.Normalize();
        var n = new Vector(-d.Y, d.X);
        const double head = 20, half = 11;
        var baseP = to - d * head;
        var tri = new PathGeometry(new[] { new PathFigure(to, new[] { new LineSegment(baseP + n * half, true), new LineSegment(baseP - n * half, true) }, true) });
        yield return new Line { X1 = from.X, Y1 = from.Y, X2 = baseP.X, Y2 = baseP.Y, Stroke = Brushes.White, StrokeThickness = 10, StrokeStartLineCap = PenLineCap.Round, Opacity = 0.9 };
        yield return new Path { Data = tri, Fill = Brushes.White, Stroke = Brushes.White, StrokeThickness = 5, StrokeLineJoin = PenLineJoin.Round, Opacity = 0.9 };
        yield return new Line { X1 = from.X, Y1 = from.Y, X2 = baseP.X, Y2 = baseP.Y, Stroke = color, StrokeThickness = 5, StrokeStartLineCap = PenLineCap.Round };
        yield return new Path { Data = tri, Fill = color };
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
        _card.BeginAnimation(OpacityProperty, a);
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

    void HookTipDrag(Ellipse h)
    {
        h.MouseLeftButtonDown += (_, e) => { _dragTip = true; _tipStart = Cursor(e); _tip0 = new Point(Note.TipX, Note.TipY); CaptureMouse(); e.Handled = true; };
    }

    void HookResize(Border g)
    {
        g.MouseLeftButtonDown += (_, e) => { _dragW = true; _tipStart = Cursor(e); _w0 = Note.W; CaptureMouse(); e.Handled = true; };
    }

    bool _dragTip, _dragW; Point _tipStart, _tip0; double _w0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!(_dragTip || _dragW || _dragCard) || e.LeftButton != MouseButtonState.Pressed) return;
        var d = Cursor(e) - _tipStart;
        if (_dragCard)
        {
            if (!_moved && d.Length < 3) return;
            _moved = true;
            Note.X = Math.Round(_tip0.X + d.X); Note.Y = Math.Round(_tip0.Y + d.Y);
        }
        else if (_dragTip) { Note.TipX = Math.Round(_tip0.X + d.X); Note.TipY = Math.Round(_tip0.Y + d.Y); }
        else Note.W = Math.Clamp(Math.Round(_w0 + d.X), 160, 900);
        Build();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!(_dragTip || _dragW || _dragCard)) return;
        bool save = !_dragCard || _moved;
        _dragTip = _dragW = _dragCard = false;
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
        Add(m, L.T("显示箭头"), () => { n.Arrow = !n.Arrow; if (n.Arrow && new Rect(n.X, n.Y, n.W, 80).Contains(new Point(n.TipX, n.TipY))) { n.TipX = n.X - 120; n.TipY = n.Y + 160; } }, n.Arrow);
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
            if (MessageBox.Show(L.T("删除这张便签？"), "HOTA DESK", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            GuideStore.D.Notes.Remove(n); GuideStore.Save();
        };
        m.Items.Add(del);
        m.IsOpen = true;
    }
}
