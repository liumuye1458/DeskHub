using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DeskHub.Interop;
using Path = System.Windows.Shapes.Path;

using DeskHub.Core;

namespace DeskHub.Shot;

/// <summary>
/// 截图：照搬 360 截图的界面和操作。
/// 每块显示器一个全屏窗口，显示按下快捷键那一刻冻结的画面；在上面选区、标注，然后复制 / 保存 / 提取文字。
/// </summary>
public static class ShotSession
{
    static List<ShotWindow>? _wins;
    public static bool Active => _wins != null;

    /// <summary>截图结束后的回调（用于整理内存）。</summary>
    public static event Action? Ended;

    public static void Start()
    {
        if (_wins != null) { _wins.FirstOrDefault()?.Activate(); return; }
        var mons = ScreenGrab.Monitors();
        var windows = ScreenGrab.Windows(IntPtr.Zero);
        _wins = new List<ShotWindow>();
        foreach (var m in mons)
        {
            var bmp = ScreenGrab.Capture(m.Bounds, out var px);
            _wins.Add(new ShotWindow(m, bmp, px, windows));
        }
        foreach (var w in _wins) w.ShowOnMonitor();
        // 鼠标所在的那块屏幕获得键盘焦点
        Native.GetCursorPos(out var c);
        var home = _wins.FirstOrDefault(w => w.Contains(c.X, c.Y)) ?? _wins[0];
        home.Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(home).Handle);
    }

    /// <summary>某块屏幕开始选区了：其他屏幕清掉选区。</summary>
    internal static void Claim(ShotWindow owner)
    {
        if (_wins == null) return;
        foreach (var w in _wins) if (w != owner) w.ResetSelection();
    }

    internal static void HideAll() { if (_wins != null) foreach (var w in _wins) w.Hide(); }

    public static void End()
    {
        if (_wins == null) return;
        var wins = _wins;
        _wins = null;
        foreach (var w in wins) w.Close();
        Ended?.Invoke();
    }
}

public partial class ShotWindow : Window
{
    enum Mode { Detect, Pending, Dragging, Selected, Resizing, Moving, Drawing }
    enum Tool { None, Rect, Ellipse, Line, Arrow, Pen, Marker, Mosaic, Text }

    readonly Monitor _mon;
    readonly BitmapSource _bmp;
    readonly byte[] _px;
    readonly List<(Int32Rect Rect, List<Int32Rect> Children)> _windows;
    double _s = 1; // DPI 缩放
    double W => _mon.Bounds.Width / _s;
    double H => _mon.Bounds.Height / _s;

    Mode _mode = Mode.Detect;
    Tool _tool = Tool.None;
    Rect _sel, _hover, _selStart;
    Point _start;
    int _handle = -1;
    readonly List<UIElement> _undo = new();
    Shape? _drawing;
    TextBox? _editing;
    ImageBrush? _mosaic;

    static int _sizeIdx = 1;
    static Color _color = Color.FromRgb(0xf0, 0x4b, 0x4b);
    static string? _lastDir;

    static readonly Color[] Palette =
    {
        Color.FromRgb(0xf0, 0x4b, 0x4b), Color.FromRgb(0xff, 0xb8, 0x00), Color.FromRgb(0x07, 0xc2, 0x55),
        Color.FromRgb(0x00, 0x95, 0xfc), Color.FromRgb(0x9b, 0x59, 0xb6), Color.FromRgb(0x33, 0x33, 0x33), Colors.White,
    };

    internal ShotWindow(Monitor mon, BitmapSource bmp, byte[] px, List<(Int32Rect, List<Int32Rect>)> windows)
    {
        InitializeComponent();
        _mon = mon; _bmp = bmp; _px = px; _windows = windows;
        Shot.Source = bmp;
        BuildBar();

        Root.MouseLeftButtonDown += OnDown;
        Root.MouseMove += OnMove;
        Root.MouseLeftButtonUp += OnUp;
        Root.MouseRightButtonUp += OnRight;
        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) => DesktopHostless();
    }

    public bool Contains(int x, int y) =>
        x >= _mon.Bounds.X && y >= _mon.Bounds.Y && x < _mon.Bounds.X + _mon.Bounds.Width && y < _mon.Bounds.Y + _mon.Bounds.Height;

    // ---------------- 窗口铺满整块屏幕 ----------------
    void DesktopHostless()
    {
        // 截图窗口不出现在 Alt+Tab 里
        var h = new WindowInteropHelper(this).Handle;
        var ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
    }

    public void ShowOnMonitor()
    {
        Left = -32000; Top = -32000; Width = 100; Height = 100;
        Show();
        var h = new WindowInteropHelper(this).Handle;
        var b = _mon.Bounds;
        // 挪过去以后 DPI 可能变，系统会按新 DPI 调整大小，所以再设一次
        for (int i = 0; i < 2; i++)
            Native.SetWindowPos(h, new IntPtr(-1), b.X, b.Y, b.Width, b.Height, Native.SWP_NOACTIVATE);
        _s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Shot.Width = W; Shot.Height = H;
        Root.Width = W; Root.Height = H;
        Frame.StrokeThickness = 1 / _s;
        UpdateMask();
        UpdateHover(Mouse.GetPosition(Root));
    }

    // ---------------- 自动识别窗口 ----------------
    Rect DetectAt(Point p)
    {
        int x = _mon.Bounds.X + (int)(p.X * _s), y = _mon.Bounds.Y + (int)(p.Y * _s);
        foreach (var (r, kids) in _windows)
        {
            if (!In(r, x, y)) continue;
            // 窗口里面最小的那个控件（例如对话框里的一块区域）
            var best = r;
            foreach (var k in kids) if (In(k, x, y) && (long)k.Width * k.Height < (long)best.Width * best.Height) best = k;
            return Clip(ToDip(best));
        }
        return new Rect(0, 0, W, H);
    }

    static bool In(Int32Rect r, int x, int y) => x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;
    Rect ToDip(Int32Rect r) => new((r.X - _mon.Bounds.X) / _s, (r.Y - _mon.Bounds.Y) / _s, r.Width / _s, r.Height / _s);
    Rect Clip(Rect r) { r.Intersect(new Rect(0, 0, W, H)); return r.IsEmpty ? new Rect(0, 0, W, H) : r; }

    // ---------------- 鼠标 ----------------
    void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(Root);
        if (e.ClickCount == 2 && _mode == Mode.Selected && _sel.Contains(p) && _tool is Tool.None) { Finish(); return; }
        if (_editing != null) { CommitText(); if (_tool == Tool.Text) return; }
        if (IsOnBar(e.OriginalSource)) return;

        switch (_mode)
        {
            case Mode.Detect:
                ShotSession.Claim(this);
                _start = p; _mode = Mode.Pending;
                Root.CaptureMouse();
                break;
            case Mode.Selected:
                _handle = HandleAt(p);
                if (_handle >= 0) { _mode = Mode.Resizing; _selStart = _sel; _start = p; Root.CaptureMouse(); HideBar(); break; }
                if (_sel.Contains(p))
                {
                    if (_tool == Tool.None) { _mode = Mode.Moving; _selStart = _sel; _start = p; Root.CaptureMouse(); HideBar(); }
                    else if (_tool == Tool.Text) StartText(p);
                    else { _mode = Mode.Drawing; _start = p; StartShape(p); Root.CaptureMouse(); }
                    break;
                }
                // 选区外面：还没画东西就重新选
                if (_undo.Count == 0 && _tool == Tool.None) { ResetSelection(); _start = p; _mode = Mode.Pending; Root.CaptureMouse(); }
                break;
        }
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Root);
        switch (_mode)
        {
            case Mode.Detect:
                UpdateHover(p);
                break;
            case Mode.Pending:
                if (Math.Abs(p.X - _start.X) > 3 || Math.Abs(p.Y - _start.Y) > 3) _mode = Mode.Dragging;
                ShowMag(p);
                break;
            case Mode.Dragging:
                SetSelection(new Rect(_start, p), live: true);
                ShowMag(p);
                break;
            case Mode.Resizing:
                SetSelection(Resize(_selStart, _handle, p - _start), live: true);
                ShowMag(p);
                break;
            case Mode.Moving:
                var r = _selStart;
                r.Offset(p - _start);
                r.X = Math.Clamp(r.X, 0, W - r.Width); r.Y = Math.Clamp(r.Y, 0, H - r.Height);
                SetSelection(r, live: true);
                break;
            case Mode.Drawing:
                UpdateShape(Clamp(p));
                break;
            case Mode.Selected:
                Cursor = HandleAt(p) is var h && h >= 0 ? HandleCursor(h)
                    : _sel.Contains(p) ? (_tool == Tool.None ? Cursors.SizeAll : _tool == Tool.Text ? Cursors.IBeam : Cursors.Cross)
                    : Cursors.Arrow;
                break;
        }
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        Root.ReleaseMouseCapture();
        switch (_mode)
        {
            case Mode.Pending:
                // 没拖动，单击：选中鼠标下的窗口
                SetSelection(_hover, live: false);
                break;
            case Mode.Dragging:
            case Mode.Resizing:
            case Mode.Moving:
                if (_sel.Width * _s < 3 || _sel.Height * _s < 3) { ResetSelection(); return; }
                SetSelection(_sel, live: false);
                break;
            case Mode.Drawing:
                EndShape();
                _mode = Mode.Selected;
                break;
        }
    }

    void OnRight(object sender, MouseButtonEventArgs e)
    {
        // 右键：有选区就取消选区，没有就退出截图（和 360 一样）
        if (_mode == Mode.Selected) ResetSelection();
        else ShotSession.End();
        e.Handled = true;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_editing != null)
        {
            if (e.Key == System.Windows.Input.Key.Escape) { CommitText(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { CommitText(); e.Handled = true; }
            return;
        }
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Escape: ShotSession.End(); break;
            case System.Windows.Input.Key.Enter: if (_mode == Mode.Selected) Finish(); break;
            case System.Windows.Input.Key.C when ctrl: if (_mode == Mode.Selected) Finish(); break;
            case System.Windows.Input.Key.S when ctrl: if (_mode == Mode.Selected) Save(); break;
            case System.Windows.Input.Key.Z when ctrl: Undo(); break;
            case System.Windows.Input.Key.Left or System.Windows.Input.Key.Right or System.Windows.Input.Key.Up or System.Windows.Input.Key.Down when _mode == Mode.Selected:
                // 方向键微调选区 1 个像素（按住 Shift 改大小）
                var d = 1 / _s;
                var v = e.Key switch { System.Windows.Input.Key.Left => new Vector(-d, 0), System.Windows.Input.Key.Right => new Vector(d, 0), System.Windows.Input.Key.Up => new Vector(0, -d), _ => new Vector(0, d) };
                var r = _sel;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) { r.Width = Math.Max(d, r.Width + v.X); r.Height = Math.Max(d, r.Height + v.Y); }
                else r.Offset(v);
                SetSelection(Clip(r), live: false);
                break;
            default: return;
        }
        e.Handled = true;
    }

    Point Clamp(Point p) => new(Math.Clamp(p.X, _sel.Left, _sel.Right), Math.Clamp(p.Y, _sel.Top, _sel.Bottom));

    // ---------------- 选区 ----------------
    void UpdateHover(Point p)
    {
        _hover = DetectAt(p);
        DrawSelection(_hover);
        ShowMag(p);
    }

    void SetSelection(Rect r, bool live)
    {
        // 选区边缘对齐到整像素，截出来的图边缘干净
        r = Snap(Clip(r));
        _sel = r;
        DrawSelection(r);
        if (live) return;
        _mode = Mode.Selected;
        Mag.Visibility = Visibility.Collapsed;
        Handles.Visibility = Visibility.Visible;
        ShowBar();
        Ink.Clip = new RectangleGeometry(r);
    }

    Rect Snap(Rect r) => new(Math.Round(r.X * _s) / _s, Math.Round(r.Y * _s) / _s, Math.Max(1, Math.Round(r.Width * _s)) / _s, Math.Max(1, Math.Round(r.Height * _s)) / _s);

    internal void ResetSelection()
    {
        _mode = Mode.Detect;
        _sel = Rect.Empty;
        foreach (var u in _undo) Ink.Children.Remove(u);
        _undo.Clear();
        _editing = null;
        Handles.Visibility = Visibility.Collapsed;
        HideBar();
        Frame.Visibility = Visibility.Collapsed;
        SizeTip.Visibility = Visibility.Collapsed;
        UpdateMask();
        Cursor = Cursors.Cross;
    }

    void DrawSelection(Rect r)
    {
        UpdateMask(r);
        Frame.Visibility = Visibility.Visible;
        Canvas.SetLeft(Frame, r.X); Canvas.SetTop(Frame, r.Y);
        Frame.Width = r.Width; Frame.Height = r.Height;

        Handles.Children.Clear();
        double hs = 5;
        foreach (var c in HandlePoints(r))
        {
            var dot = new Rectangle { Width = hs, Height = hs, Fill = (Brush)FindResource("Green"), Stroke = Brushes.White, StrokeThickness = 1 / _s };
            Canvas.SetLeft(dot, c.X - hs / 2); Canvas.SetTop(dot, c.Y - hs / 2);
            Handles.Children.Add(dot);
        }

        int x = _mon.Bounds.X + (int)Math.Round(r.X * _s), y = _mon.Bounds.Y + (int)Math.Round(r.Y * _s);
        SizeText.Text = L.F("{0} × {1}　左上角 ({2}, {3})", Math.Round(r.Width * _s), Math.Round(r.Height * _s), x, y);
        SizeTip.Visibility = Visibility.Visible;
        SizeTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(SizeTip, Math.Min(r.X, W - SizeTip.DesiredSize.Width));
        Canvas.SetTop(SizeTip, r.Y - SizeTip.DesiredSize.Height - 4 >= 0 ? r.Y - SizeTip.DesiredSize.Height - 4 : r.Y + 4);
    }

    void UpdateMask(Rect? hole = null)
    {
        var all = new RectangleGeometry(new Rect(0, 0, W, H));
        Mask.Data = hole is { } h ? new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(h)) : all;
    }

    static Point[] HandlePoints(Rect r) => new[]
    {
        r.TopLeft, new Point(r.X + r.Width / 2, r.Y), r.TopRight, new Point(r.Right, r.Y + r.Height / 2),
        r.BottomRight, new Point(r.X + r.Width / 2, r.Bottom), r.BottomLeft, new Point(r.X, r.Y + r.Height / 2),
    };

    int HandleAt(Point p)
    {
        var pts = HandlePoints(_sel);
        for (int i = 0; i < pts.Length; i++) if (Math.Abs(pts[i].X - p.X) <= 5 && Math.Abs(pts[i].Y - p.Y) <= 5) return i;
        return -1;
    }

    static Cursor HandleCursor(int h) => h switch { 0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW, 1 or 5 => Cursors.SizeNS, _ => Cursors.SizeWE };

    static Rect Resize(Rect r, int h, Vector d)
    {
        double l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
        if (h is 0 or 6 or 7) l += d.X;
        if (h is 2 or 3 or 4) rr += d.X;
        if (h is 0 or 1 or 2) t += d.Y;
        if (h is 4 or 5 or 6) b += d.Y;
        return new Rect(new Point(l, t), new Point(rr, b));
    }

    // ---------------- 放大镜 ----------------
    void ShowMag(Point p)
    {
        int px = Math.Clamp((int)(p.X * _s), 0, _mon.Bounds.Width - 1), py = Math.Clamp((int)(p.Y * _s), 0, _mon.Bounds.Height - 1);
        MagView.Fill = new ImageBrush(_bmp) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(px - 12.5, py - 9.5, 25, 19), Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(MagView.Fill, BitmapScalingMode.NearestNeighbor);
        int i = (py * _mon.Bounds.Width + px) * 4;
        MagPos.Text = $"({_mon.Bounds.X + px}, {_mon.Bounds.Y + py})";
        MagRgb.Text = $"RGB: {_px[i + 2]},{_px[i + 1]},{_px[i]}";
        Mag.Visibility = Visibility.Visible;
        Mag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = p.X + 20, y = p.Y + 22;
        if (x + Mag.DesiredSize.Width > W) x = p.X - 20 - Mag.DesiredSize.Width;
        if (y + Mag.DesiredSize.Height > H) y = p.Y - 22 - Mag.DesiredSize.Height;
        Canvas.SetLeft(Mag, x); Canvas.SetTop(Mag, y);
    }

    // ---------------- 工具条 ----------------
    readonly Dictionary<Tool, Border> _toolBtns = new();

    void BuildBar()
    {
        void ToolBtn(Tool t, string tip, string data, bool fill = false)
        {
            var path = new Path { Data = Geometry.Parse(data), Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            if (fill) path.Fill = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)); else path.Stroke = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
            var b = IconButton(path, tip);
            b.MouseLeftButtonUp += (_, _) => SetTool(_tool == t ? Tool.None : t);
            _toolBtns[t] = b;
            BarItems.Children.Add(b);
        }
        ToolBtn(Tool.Rect, L.T("矩形"), "M1,2 H15 V14 H1 Z");
        ToolBtn(Tool.Ellipse, L.T("椭圆"), "M8,2 A7,6 0 1 1 7.99,2 Z");
        ToolBtn(Tool.Line, L.T("直线"), "M2,14 L14,2");
        ToolBtn(Tool.Arrow, L.T("箭头"), "M2,14 L14,2 M7,2 H14 V9");
        ToolBtn(Tool.Pen, L.T("画笔"), "M2,13 C4,8 6,12 8,8 S12,3 14,3");
        ToolBtn(Tool.Marker, L.T("荧光笔"), "M3,11 L10,4 L13,7 L6,14 Z M2,15 H14");
        ToolBtn(Tool.Mosaic, L.T("马赛克"), "M1,1 H5 V5 H1 Z M9,1 H13 V5 H9 Z M5,5 H9 V9 H5 Z M1,9 H5 V13 H1 Z M9,9 H13 V13 H9 Z", fill: true);
        ToolBtn(Tool.Text, L.T("文字"), "M2,3 H14 M8,3 V15");
        Sep();
        var undo = IconButton(new Path { Data = Geometry.Parse("M5,3 L2,6 L5,9 M2,6 H10 A4,4 0 0 1 10,14 H6"), Width = 16, Height = 16, Stretch = Stretch.Uniform, Stroke = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), StrokeThickness = 1.5 }, L.T("撤销（Ctrl+Z）"));
        undo.MouseLeftButtonUp += (_, _) => Undo();
        BarItems.Children.Add(undo);
        Sep();
        TextBtn(L.T("提取文字"), RecognizeText, L.T("识别选区里的文字（Windows 自带的离线识别，不联网）"));
        TextBtn(L.T("保存"), Save, L.T("保存成图片（Ctrl+S）"));
        TextBtn(L.T("复制"), Finish, L.T("复制到剪贴板（Ctrl+C）"));
        var cancel = IconButton(new TextBlock { Text = "✕", Foreground = new SolidColorBrush(Color.FromRgb(0xe5, 0x41, 0x3c)), FontSize = 14 }, L.T("退出截图（Esc）"));
        cancel.MouseLeftButtonUp += (_, _) => ShotSession.End();
        BarItems.Children.Add(cancel);
        var ok = new Border { Background = (Brush)FindResource("Green"), CornerRadius = new CornerRadius(3), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = L.T("完成：复制到剪贴板（双击选区 / Enter）") };
        ok.Child = new TextBlock { Text = L.T("✓ 完成"), Foreground = Brushes.White, FontSize = 12.5 };
        ok.MouseLeftButtonUp += (_, _) => Finish();
        BarItems.Children.Add(ok);

        void Sep() => BarItems.Children.Add(new Border { Width = 1, Height = 18, Background = new SolidColorBrush(Color.FromRgb(0xe3, 0xe3, 0xe3)), Margin = new Thickness(5, 0, 5, 0) });
        void TextBtn(string text, Action act, string tip)
        {
            var b = IconButton(new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), FontSize = 12.5 }, tip);
            b.Padding = new Thickness(8, 5, 8, 5);
            b.MouseLeftButtonUp += (_, _) => act();
            BarItems.Children.Add(b);
        }
    }

    static Border IconButton(UIElement content, string tip)
    {
        var b = new Border { Child = content, Padding = new Thickness(7, 6, 7, 6), CornerRadius = new CornerRadius(3), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        b.MouseEnter += (_, _) => { if (b.Tag is not true) b.Background = new SolidColorBrush(Color.FromRgb(0xf0, 0xf1, 0xf2)); };
        b.MouseLeave += (_, _) => { if (b.Tag is not true) b.Background = Brushes.Transparent; };
        return b;
    }

    void SetTool(Tool t)
    {
        if (_editing != null) CommitText();
        _tool = t;
        foreach (var (k, b) in _toolBtns)
        {
            b.Tag = k == t;
            b.Background = k == t ? (Brush)FindResource("GreenSoft") : Brushes.Transparent;
            b.BorderBrush = k == t ? (Brush)FindResource("Green") : null;
            b.BorderThickness = new Thickness(k == t ? 1 : 0);
        }
        BuildSub();
        PlaceBar();
    }

    void BuildSub()
    {
        SubItems.Children.Clear();
        if (_tool == Tool.None) { Sub.Visibility = Visibility.Collapsed; return; }
        SubItems.Children.Add(new TextBlock { Text = _tool == Tool.Text ? L.T("字号") : L.T("粗细"), Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            var dot = new Ellipse { Width = 5 + i * 3.5, Height = 5 + i * 3.5, Fill = idx == _sizeIdx ? (Brush)FindResource("Green") : new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)) };
            var b = new Border { Child = dot, Width = 20, Height = 20, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            b.MouseLeftButtonUp += (_, _) => { _sizeIdx = idx; BuildSub(); };
            SubItems.Children.Add(b);
        }
        if (_tool == Tool.Mosaic) { Sub.Visibility = Visibility.Visible; return; }
        SubItems.Children.Add(new Border { Width = 1, Height = 16, Background = new SolidColorBrush(Color.FromRgb(0xe3, 0xe3, 0xe3)), Margin = new Thickness(6, 0, 8, 0) });
        foreach (var c in Palette)
        {
            var col = c;
            var sw = new Border
            {
                Width = 16, Height = 16, Margin = new Thickness(0, 0, 5, 0), Background = new SolidColorBrush(c), Cursor = Cursors.Hand,
                BorderBrush = col == _color ? new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)) : new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
                BorderThickness = new Thickness(col == _color ? 2 : 1),
            };
            sw.MouseLeftButtonUp += (_, _) => { _color = col; BuildSub(); if (_editing != null) _editing.Foreground = new SolidColorBrush(col); };
            SubItems.Children.Add(sw);
        }
        Sub.Visibility = Visibility.Visible;
    }

    void ShowBar() { Bar.Visibility = Visibility.Visible; BuildSub(); PlaceBar(); }
    void HideBar() { Bar.Visibility = Visibility.Collapsed; Sub.Visibility = Visibility.Collapsed; }

    /// <summary>工具条放在选区右下方；下面放不下就放上面，再放不下就放在选区里面。</summary>
    void PlaceBar()
    {
        if (Bar.Visibility != Visibility.Visible) return;
        Bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Sub.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var bw = Bar.DesiredSize.Width; var bh = Bar.DesiredSize.Height;
        var sh = Sub.Visibility == Visibility.Visible ? Sub.DesiredSize.Height + 4 : 0;
        double x = Math.Clamp(_sel.Right - bw, 0, Math.Max(0, W - bw));
        double y;
        bool below = _sel.Bottom + 6 + bh + sh <= H;
        if (below) y = _sel.Bottom + 6;
        else if (_sel.Top - 6 - bh - sh >= 0) y = _sel.Top - 6 - bh - sh;
        else y = _sel.Bottom - bh - sh - 6;
        Canvas.SetLeft(Bar, x); Canvas.SetTop(Bar, y);
        Canvas.SetLeft(Sub, x); Canvas.SetTop(Sub, y + bh + 4);
    }

    bool IsOnBar(object src)
    {
        for (var d = src as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d == Bar || d == Sub || d == _editing) return true;
        return false;
    }

    // ---------------- 标注 ----------------
    double Thick => _tool switch
    {
        Tool.Marker => new[] { 10.0, 16, 24 }[_sizeIdx],
        Tool.Mosaic => new[] { 12.0, 20, 32 }[_sizeIdx],
        _ => new[] { 2.0, 3.5, 5.5 }[_sizeIdx],
    };

    void StartShape(Point p)
    {
        var brush = new SolidColorBrush(_color);
        _drawing = _tool switch
        {
            Tool.Rect => new Rectangle { Stroke = brush, StrokeThickness = Thick },
            Tool.Ellipse => new Ellipse { Stroke = brush, StrokeThickness = Thick },
            Tool.Line => new Line { Stroke = brush, StrokeThickness = Thick, X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round },
            Tool.Arrow => new Path { Fill = brush },
            Tool.Pen => new Polyline { Stroke = brush, StrokeThickness = Thick, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Points = { p } },
            Tool.Marker => new Polyline { Stroke = new SolidColorBrush(Color.FromArgb(0x70, _color.R, _color.G, _color.B)), StrokeThickness = Thick, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Square, StrokeEndLineCap = PenLineCap.Square, Points = { p } },
            Tool.Mosaic => new Polyline { Stroke = MosaicBrush(), StrokeThickness = Thick, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Points = { p } },
            _ => null,
        };
        if (_drawing == null) return;
        Ink.Children.Add(_drawing);
        UpdateShape(p);
    }

    void UpdateShape(Point p)
    {
        switch (_drawing)
        {
            case Rectangle or Ellipse:
                var r = new Rect(_start, p);
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) { var m = Math.Min(r.Width, r.Height); r.Width = r.Height = m; } // 按住 Shift 画正方形 / 圆
                Canvas.SetLeft(_drawing, r.X); Canvas.SetTop(_drawing, r.Y);
                _drawing.Width = r.Width; _drawing.Height = r.Height;
                break;
            case Line l: l.X2 = p.X; l.Y2 = p.Y; break;
            case Polyline pl: pl.Points.Add(p); break;
            case Path a: a.Data = ArrowGeometry(_start, p, Thick); break;
        }
    }

    void EndShape()
    {
        if (_drawing == null) return;
        bool tiny = _drawing switch
        {
            Polyline pl => pl.Points.Count < 2,
            Line l => Math.Abs(l.X2 - l.X1) + Math.Abs(l.Y2 - l.Y1) < 3,
            Path a => a.Data == null || a.Data.Bounds.Width + a.Data.Bounds.Height < 6,
            _ => _drawing.Width + _drawing.Height < 4,
        };
        if (tiny) Ink.Children.Remove(_drawing);
        else _undo.Add(_drawing);
        _drawing = null;
    }

    /// <summary>360 那种尾巴细、头部粗的实心箭头。</summary>
    static Geometry? ArrowGeometry(Point a, Point b, double t)
    {
        var v = b - a;
        double len = v.Length;
        if (len < 2) return null;
        v /= len;
        var n = new Vector(-v.Y, v.X);
        double head = Math.Min(len * 0.6, Math.Max(10, t * 4.5)), wing = head * 0.55, shaft = Math.Max(1, t * 0.7);
        var baseP = b - v * head;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(a + n * 0.4, true, true);
            c.LineTo(baseP + n * shaft, true, true);
            c.LineTo(baseP + n * wing, true, true);
            c.LineTo(b, true, true);
            c.LineTo(baseP - n * wing, true, true);
            c.LineTo(baseP - n * shaft, true, true);
            c.LineTo(a - n * 0.4, true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>马赛克：把画面缩小 10 倍再放大（不做平滑），用它当画笔的颜料。</summary>
    Brush MosaicBrush()
    {
        if (_mosaic != null) return _mosaic;
        double k = Math.Max(6, 10 * _s) ;
        var small = new TransformedBitmap(_bmp, new ScaleTransform(1 / k, 1 / k));
        small.Freeze();
        _mosaic = new ImageBrush(small) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, W, H), Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_mosaic, BitmapScalingMode.NearestNeighbor);
        return _mosaic;
    }

    void StartText(Point p)
    {
        var size = new[] { 14.0, 18, 24 }[_sizeIdx];
        _editing = new TextBox
        {
            Foreground = new SolidColorBrush(_color), FontSize = size, Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xff, 0xff, 0xff)), BorderThickness = new Thickness(1),
            AcceptsReturn = true, MinWidth = 40, Padding = new Thickness(2), CaretBrush = new SolidColorBrush(_color),
            FontFamily = (FontFamily)FindResource("UiFont"),
        };
        Canvas.SetLeft(_editing, p.X - 3); Canvas.SetTop(_editing, p.Y - size * 0.7);
        Root.Children.Add(_editing);
        _editing.Focus();
    }

    void CommitText()
    {
        var tb = _editing;
        _editing = null;
        if (tb == null) return;
        Root.Children.Remove(tb);
        if (string.IsNullOrWhiteSpace(tb.Text)) return;
        var t = new TextBlock { Text = tb.Text, Foreground = tb.Foreground, FontSize = tb.FontSize, FontFamily = tb.FontFamily, Padding = new Thickness(3) };
        Canvas.SetLeft(t, Canvas.GetLeft(tb)); Canvas.SetTop(t, Canvas.GetTop(tb));
        Ink.Children.Add(t);
        _undo.Add(t);
        Focus();
    }

    void Undo()
    {
        if (_editing != null) { Root.Children.Remove(_editing); _editing = null; return; }
        if (_undo.Count == 0) return;
        Ink.Children.Remove(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
    }

    // ---------------- 输出 ----------------
    Int32Rect PixelRect()
    {
        int x = (int)Math.Round(_sel.X * _s), y = (int)Math.Round(_sel.Y * _s);
        int w = Math.Min((int)Math.Round(_sel.Width * _s), _mon.Bounds.Width - x), h = Math.Min((int)Math.Round(_sel.Height * _s), _mon.Bounds.Height - y);
        return new Int32Rect(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>把选区画面和标注合成一张图（物理像素大小）。</summary>
    BitmapSource Compose(bool withInk = true)
    {
        if (_editing != null) CommitText();
        var pr = PixelRect();
        var crop = new CroppedBitmap(_bmp, pr);
        if (!withInk || _undo.Count == 0) { crop.Freeze(); return crop; }
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(crop, new Rect(0, 0, _sel.Width, _sel.Height));
            var vb = new VisualBrush(Ink) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = _sel, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, _sel.Width, _sel.Height), Stretch = Stretch.Fill };
            dc.DrawRectangle(vb, null, new Rect(0, 0, _sel.Width, _sel.Height));
        }
        var rtb = new RenderTargetBitmap(pr.Width, pr.Height, 96 * _s, 96 * _s, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    void Finish()
    {
        if (_mode != Mode.Selected) return;
        var img = Compose();
        CopyImage(img);
        ShotSession.End();
    }

    public static void CopyImage(BitmapSource img)
    {
        // 同时放 PNG 格式：有的软件（例如网页、企业微信）粘贴 PNG 更清楚
        var data = new DataObject();
        data.SetImage(img);
        var png = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        enc.Save(png);
        data.SetData("PNG", png);
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(data, true); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(60); } // 剪贴板被别的程序占着，稍等再试
        }
    }

    void Save()
    {
        if (_mode != Mode.Selected) return;
        var img = Compose();
        ShotSession.HideAll();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("保存截图"),
            FileName = L.F("截图_{0:yyyyMMdd_HHmmss}", DateTime.Now),
            DefaultExt = ".png",
            Filter = L.T("PNG 图片|*.png|JPG 图片|*.jpg|BMP 图片|*.bmp"),
            InitialDirectory = _lastDir ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog() == true)
        {
            BitmapEncoder enc = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
                ".bmp" => new BmpBitmapEncoder(),
                _ => new PngBitmapEncoder(),
            };
            enc.Frames.Add(BitmapFrame.Create(img));
            try
            {
                using var fs = File.Create(dlg.FileName);
                enc.Save(fs);
                _lastDir = System.IO.Path.GetDirectoryName(dlg.FileName);
            }
            catch (Exception ex) { MessageBox.Show(L.T("保存失败：") + ex.Message, L.T("截图")); }
        }
        ShotSession.End();
    }

    async void RecognizeText()
    {
        if (_mode != Mode.Selected) return;
        var img = Compose(withInk: false);
        var screen = new Rect(_mon.Bounds.X, _mon.Bounds.Y, _mon.Bounds.Width, _mon.Bounds.Height);
        ShotSession.End();
        string text;
        try { text = await Ocr.RecognizeAsync(img); }
        catch (Exception ex) { text = ""; MessageBox.Show(ex.Message, L.T("提取文字")); return; }
        var w = new OcrResultWindow(text, img);
        w.Show();
        w.CenterOn(screen);
    }
}
