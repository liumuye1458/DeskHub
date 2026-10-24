using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DeskHub.Interop;

namespace DeskHub.UI;

/// <summary>
/// 桌面右下角的工具条：查找、截图、计算器、设置（和分区一样贴在桌面上）。
/// 按住任意地方都能拖（按钮上按住拖也算，不会误点）；最右边的箭头把它收到屏幕左/右边上，只留一个小把手，点把手再展开。
/// </summary>
public partial class DockWindow : Window
{
    /// <summary>展开状态下被拖动了（存位置）。</summary>
    public event Action? Moved;
    /// <summary>收起 / 展开 / 把手挪了位置（存配置）。</summary>
    public event Action? StateChanged;

    public bool Collapsed { get; private set; }
    /// <summary>收在右边（false = 左边）。</summary>
    public bool RightSide { get; private set; } = true;
    /// <summary>展开时的位置（收起期间记着，展开回到这里）。</summary>
    public Point ExpandedPos { get; private set; }

    /// <summary>工具条的底色跟着全局深浅（BgLook）走。</summary>
    static Brush TabBg => BgLook.Brush(BgLook.Global);
    static readonly Brush TabHover = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));

    Point? _down;
    bool _dragged;

    public DockWindow()
    {
        InitializeComponent();
        void Look() { Root.Background = TabBg; if (!Tab.IsMouseOver) Tab.Background = TabBg; }
        Look();
        BgLook.Changed += Look;
        Closed += (_, _) => BgLook.Changed -= Look;
        SourceInitialized += (_, _) => DesktopHost.Pin(this);

        // 整条都能拖：按下后挪动超过 4 像素就开始拖，松手时按钮不会被点到
        PreviewMouseLeftButtonDown += (_, e) => { _down = e.GetPosition(this); _dragged = false; };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            _down = null;
            if (!_dragged && Collapsed && Tab.IsMouseOver) { e.Handled = true; Expand(); }
        };
        PreviewMouseMove += (_, e) =>
        {
            if (_down is not { } p || e.LeftButton != MouseButtonState.Pressed) return;
            var v = e.GetPosition(this) - p;
            if (Math.Abs(v.X) < 4 && Math.Abs(v.Y) < 4) return;
            _down = null; _dragged = true;
            Mouse.Capture(null); // 按钮放掉鼠标，松手时就不算点击
            DragMove();
            AfterDrag();
        };

        FoldBtn.Click += (_, _) => { if (!_dragged) Collapse(); };
        Tab.MouseEnter += (_, _) => Tab.Background = TabHover;
        Tab.MouseLeave += (_, _) => Tab.Background = TabBg;
        Loaded += (_, _) => UpdateGlyphs();
        LocationChanged += (_, _) => { if (!Collapsed) UpdateGlyphs(); };
    }

    /// <summary>加一个按钮。icon 是 20×20 的线条图标路径。</summary>
    public void Add(string text, string icon, string tip, Action run)
    {
        var path = new Path
        {
            Data = Geometry.Parse(icon), Stroke = Brushes.White, StrokeThickness = 1.6, Width = 20, Height = 20, Stretch = Stretch.Uniform,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        var panel = new StackPanel();
        panel.Children.Add(path);
        panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Effect = (System.Windows.Media.Effects.Effect)FindResource("TextShadow") });
        var btn = new Button { Content = panel, Style = (Style)FindResource("DockBtn"), ToolTip = tip };
        btn.Click += (_, _) => { if (!_dragged) run(); };
        Buttons.Children.Add(btn);
    }

    public void SetTip(string text, string tip)
    {
        foreach (Button b in Buttons.Children)
            if (b.Content is StackPanel { Children: [_, TextBlock t] } && t.Text == text) b.ToolTip = tip;
    }

    /// <summary>测试用：模拟拖到 (x, y) 松手。</summary>
    public void TestDragTo(double x, double y) { Left = x; Top = y; AfterDrag(); }

    Rect WorkArea() => DeskController.WorkAreaOf(this);

    void AfterDrag()
    {
        if (!Collapsed) { UpdateGlyphs(); Moved?.Invoke(); return; }
        // 把手拖完：贴回离得近的那条边（左或右），上下位置随你
        var wa = WorkArea();
        RightSide = Left + ActualWidth / 2 >= wa.Left + wa.Width / 2;
        UpdateGlyphs();
        PlaceTab(wa, Top + ActualHeight / 2);
        StateChanged?.Invoke();
    }

    /// <summary>收到离得近的那条边（左或右）上，只留小把手。</summary>
    public void Collapse()
    {
        if (Collapsed) return;
        var wa = WorkArea();
        ExpandedPos = new Point(Left, Top);
        RightSide = Left + ActualWidth / 2 >= wa.Left + wa.Width / 2;
        double cy = Top + ActualHeight / 2;
        SetMode(tab: true);
        PlaceTab(wa, cy);
        StateChanged?.Invoke();
    }

    /// <summary>启动时恢复成收起状态：expanded = 展开时的位置，tabTop = 把手上沿。</summary>
    public void RestoreCollapsed(Point expanded, bool right, double tabTop)
    {
        ExpandedPos = expanded;
        RightSide = right;
        double eh = ActualHeight;
        SetMode(tab: true);
        PlaceTab(WorkArea(), tabTop >= 0 ? tabTop + ActualHeight / 2 : expanded.Y + eh / 2);
    }

    /// <summary>展开。把手没挪过就回到原来的位置；挪过就在把手旁边展开。</summary>
    public void Expand(bool notify = true)
    {
        if (!Collapsed) return;
        var wa = WorkArea();
        double cy = Top + ActualHeight / 2;
        SetMode(tab: false);
        double w = ActualWidth, h = ActualHeight;
        var p = ExpandedPos;
        bool sameSide = (p.X + w / 2 >= wa.Left + wa.Width / 2) == RightSide;
        bool fits = p.X >= wa.Left - 4 && p.X + w <= wa.Right + 4 && p.Y >= wa.Top - 4 && p.Y + h <= wa.Bottom + 4;
        if (sameSide && fits && Math.Abs(p.Y + h / 2 - cy) < 3) { Left = p.X; Top = p.Y; }
        else
        {
            Left = RightSide ? wa.Right - w - 24 : wa.Left + 24;
            Top = Math.Clamp(cy - h / 2, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
        }
        UpdateGlyphs();
        if (!notify) return;
        StateChanged?.Invoke();
        Moved?.Invoke();
    }

    void SetMode(bool tab)
    {
        Collapsed = tab;
        Root.Visibility = tab ? Visibility.Collapsed : Visibility.Visible;
        Tab.Visibility = tab ? Visibility.Visible : Visibility.Collapsed;
        Tab.Background = TabBg;
        UpdateGlyphs();
        UpdateLayout();
    }

    void PlaceTab(Rect wa, double centerY)
    {
        Left = RightSide ? wa.Right - ActualWidth : wa.Left;
        Top = Math.Clamp(centerY - ActualHeight / 2, wa.Top, Math.Max(wa.Top, wa.Bottom - ActualHeight));
    }

    /// <summary>箭头朝向：收起按钮指向要收去的那条边；把手的箭头指回屏幕里面。</summary>
    void UpdateGlyphs()
    {
        bool right = RightSide;
        if (!Collapsed && IsLoaded) { var wa = WorkArea(); right = Left + ActualWidth / 2 >= wa.Left + wa.Width / 2; }
        FoldGlyph.Data = Geometry.Parse(right ? "M2,0 L7,5 L2,10" : "M7,0 L2,5 L7,10");
        TabGlyph.Data = Geometry.Parse(RightSide ? "M6,0 L1,5 L6,10" : "M1,0 L6,5 L1,10");
        Tab.BorderThickness = RightSide ? new Thickness(1, 1, 0, 1) : new Thickness(0, 1, 1, 1);
        Tab.CornerRadius = RightSide ? new CornerRadius(8, 0, 0, 8) : new CornerRadius(0, 8, 8, 0);
    }
}
