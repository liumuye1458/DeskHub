using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>
/// 调底色深浅的小浮窗：贴在块旁边，拖滑条桌面上实时变。
/// 右键菜单打开是正常模式（点别处就收）；标题栏上 Ctrl+滚轮打开是“瞄一眼”模式（不抢焦点，停手 1.5 秒自己收）。
/// </summary>
public sealed class LookPopup : Window
{
    const string Ink = "#222222", Mute = "#999999", Green = "#07C255";
    const double Shadow = 14, Step = 0.05;
    static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    static LookPopup? _open;

    readonly string? _key;
    readonly LookSlider _slider = new();
    readonly TextBlock _pct = new() { Width = 40, TextAlignment = TextAlignment.Right, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = B(Ink), VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox? _own;
    readonly TextBlock _wall = new() { FontSize = 11.5, Foreground = B(Mute), TextWrapping = TextWrapping.Wrap, LineHeight = 17 };
    readonly DispatcherTimer _peekClose = new() { Interval = TimeSpan.FromSeconds(1.5) };
    bool _peek, _syncing;

    /// <summary>key = 块的键（分区键、calendar）；null = 只调全局（设置页、工具条）。name = 给人看的块名。block = 浮窗贴着哪个窗口放。</summary>
    public static void Open(string? key, string name, Window block, FrameworkElement? near = null) => Get(key, name, block, peek: false, near);

    /// <summary>Ctrl+滚轮：直接调一档，弹出来给你看现在多少。</summary>
    public static void Nudge(string? key, string name, Window block, int dir)
    {
        bool own = key != null && BgLook.Own(key) != null;
        var now = own ? BgLook.Own(key!)!.Value : BgLook.Global;
        var to = Math.Round((now + dir * Step) / Step) * Step;
        if (own) BgLook.SetOwn(key!, to); else BgLook.SetGlobal(to);
        var p = Get(key, name, block, peek: _open is not { _peek: false });
        p._peekClose.Stop(); p._peekClose.Start();
        SaveSoon();
    }

    static readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromSeconds(1) };
    static void SaveSoon()
    {
        _save.Stop();
        _save.Tick -= OnSave; _save.Tick += OnSave;
        _save.Start();
    }
    static void OnSave(object? s, EventArgs e) { _save.Stop(); BgLook.Save(); }

    static LookPopup Get(string? key, string name, Window block, bool peek, FrameworkElement? near = null)
    {
        if (_open != null && _open._key == key) { if (!peek) _open.Activate(); _open.Sync(); return _open; }
        _open?.Close();
        var p = new LookPopup(key, name) { _peek = peek, ShowActivated = !peek };
        _open = p;
        p.Place(block, near);
        p.Show();
        if (!peek) p.Activate(); else p._peekClose.Start();
        return p;
    }

    LookPopup(string? key, string name)
    {
        _key = key;
        Title = L.T("底色深浅");
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; SizeToContent = SizeToContent.Height; Width = 300 + Shadow * 2;
        FontFamily = Res<FontFamily>("UiFont"); FontSize = 12.5; UseLayoutRounding = true;

        var close = new TextBlock { Text = "✕", FontSize = 13, Foreground = B(Mute), Cursor = Cursors.Hand, Padding = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = L.T("关闭") };
        close.MouseEnter += (_, _) => close.Foreground = B("#E5484D");
        close.MouseLeave += (_, _) => close.Foreground = B(Mute);
        close.MouseLeftButtonUp += (_, _) => Close();
        var reset = new TextBlock { Text = L.T("恢复默认"), FontSize = 11.5, Foreground = B("#3A7BE0"), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
        reset.MouseLeftButtonUp += (_, _) =>
        {
            if (_key != null) BgLook.SetOwn(_key, null);
            BgLook.SetGlobal(BgLook.Default);
            BgLook.Save();
        };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(reset, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(reset);
        head.Children.Add(new TextBlock { Text = L.T("底色深浅"), FontWeight = FontWeights.Bold, FontSize = 13.5, Foreground = B(Ink) });

        var row = new DockPanel();
        DockPanel.SetDock(_pct, Dock.Right);
        row.Children.Add(_pct);
        row.Children.Add(_slider);
        _slider.Margin = new Thickness(0, 0, 8, 0);
        _slider.ValueChanged += v =>
        {
            if (_syncing) return;
            if (_own?.IsChecked == true) BgLook.SetOwn(_key!, v); else BgLook.SetGlobal(v);
        };
        _slider.Committed += BgLook.Save;

        var panel = new StackPanel();
        panel.Children.Add(head);
        panel.Children.Add(row);
        var ends = new Grid { Margin = new Thickness(0, 3, 48, 0) };
        ends.Children.Add(new TextBlock { Text = L.T("透明"), FontSize = 11, Foreground = B(Mute) });
        ends.Children.Add(new TextBlock { Text = L.T("深"), FontSize = 11, Foreground = B(Mute), HorizontalAlignment = HorizontalAlignment.Right });
        panel.Children.Add(ends);

        if (key != null)
        {
            _own = new CheckBox
            {
                Style = Res<Style>("Toggle"), Margin = new Thickness(0, 12, 0, 0),
                Content = new TextBlock { Text = L.F("只调这一块（{0}）", name), FontSize = 12, Foreground = B("#555555"), TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = L.T("打开后这一块单独记一个深浅，不跟着别的块变；关掉就回到和大家一样"),
            };
            _own.Click += (_, _) =>
            {
                // 打开：从现在看到的深浅开始单独调；关掉：回到全局
                BgLook.SetOwn(_key!, _own.IsChecked == true ? BgLook.Global : null);
                BgLook.Save();
            };
            panel.Children.Add(_own);
        }

        _wall.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(new Border { Height = 1, Background = B("#EEEEEE"), Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(_wall);

        Content = new Border
        {
            Margin = new Thickness(Shadow), Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8),
            Background = Brushes.White, BorderBrush = B("#E3E3E3"), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.3, RenderingBias = RenderingBias.Performance },
            Child = panel,
        };

        Sync();
        BgLook.Changed += Sync;
        Closed += (_, _) => { BgLook.Changed -= Sync; _peekClose.Stop(); if (_open == this) _open = null; BgLook.Save(); };
        Deactivated += (_, _) => { if (!_peek && IsVisible) Close(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        // 瞄一眼模式：停手 1.5 秒收起；鼠标移上去或点了就变成正常的浮窗
        _peekClose.Tick += (_, _) => { if (IsMouseOver) return; _peekClose.Stop(); Close(); };
        PreviewMouseDown += (_, _) => { if (!_peek) return; _peek = false; _peekClose.Stop(); Activate(); };
        MouseWheel += (_, e) => { _slider.Nudge(e.Delta > 0 ? Step : -Step); e.Handled = true; };
    }

    void Sync()
    {
        _syncing = true;
        bool own = _key != null && BgLook.Own(_key) != null;
        if (_own != null) _own.IsChecked = own;
        var v = own ? BgLook.Own(_key!)!.Value : BgLook.Global;
        _slider.Value = v;
        _pct.Text = $"{Math.Round(v * 100)}%";
        _wall.Text = L.F("壁纸：{0}", BgLook.WallpaperName()) + "\n" + L.T("按壁纸各记一份，换回这张时自动恢复");
        _syncing = false;
    }

    /// <summary>放在块标题栏下方（放不下就往上、往左挪），不出屏幕。</summary>
    void Place(Window block, FrameworkElement? near)
    {
        Measure(new Size(Width, double.PositiveInfinity));
        double h = DesiredSize.Height > 0 ? DesiredSize.Height : 230;
        var anchor = new Rect(block.Left, block.Top, block.ActualWidth, block.ActualHeight);
        if (near != null)
        {
            var pt = near.TranslatePoint(new Point(0, 0), block);
            anchor = new Rect(block.Left + pt.X, block.Top + pt.Y, near.ActualWidth, near.ActualHeight);
        }
        var wa = Screen(anchor, VisualTreeHelper.GetDpi(block));
        double x = anchor.Left - Shadow + 8, y = anchor.Top + 30 - Shadow;
        if (x + Width > wa.Right) x = wa.Right - Width;
        if (x < wa.Left) x = wa.Left;
        if (y + h > wa.Bottom) y = wa.Bottom - h;
        if (y < wa.Top) y = wa.Top;
        Left = x; Top = y;
    }

    static Rect Screen(Rect r, DpiScale dpi)
    {
        var s = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)(r.Left * dpi.DpiScaleX) + 4, (int)(r.Top * dpi.DpiScaleY) + 4));
        var w = s.WorkingArea;
        return new Rect(w.Left / dpi.DpiScaleX, w.Top / dpi.DpiScaleY, w.Width / dpi.DpiScaleX, w.Height / dpi.DpiScaleY);
    }

    /// <summary>沙盒截图用。</summary>
    public static LookPopup? Current => _open;
}

/// <summary>深浅滑条：轨道本身就是“从透明到深”的渐变，拖圆点、点轨道、滚轮、方向键都能调。</summary>
public sealed class LookSlider : Grid
{
    const double Max = BgLook.Max;
    readonly Border _track;
    readonly Ellipse _thumb;
    double _value;
    public event Action<double>? ValueChanged;
    public event Action? Committed;

    public LookSlider()
    {
        Height = 22; Background = Brushes.Transparent; Cursor = Cursors.Hand; Focusable = true; FocusVisualStyle = null;
        // 轨道下面垫一层棋盘格，看得出“透明”
        var checker = new DrawingBrush { Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.Tile };
        checker.Drawing = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), null,
            new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 3, 3)), new RectangleGeometry(new Rect(3, 3, 3, 3)) } });
        checker.Freeze();
        Children.Add(new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = checker, Margin = new Thickness(8, 0, 8, 0) });
        _track = new Border
        {
            Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(8, 0, 8, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)), BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush(Color.FromArgb(0x08, 0, 0, 0), Color.FromArgb((byte)(Max * 255), 0, 0, 0), 0),
        };
        Children.Add(_track);
        _thumb = new Ellipse
        {
            Width = 16, Height = 16, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(0x07, 0xC2, 0x55)), StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.25, RenderingBias = RenderingBias.Performance },
        };
        Children.Add(_thumb);
        SizeChanged += (_, _) => Layout();

        MouseLeftButtonDown += (_, e) => { Focus(); CaptureMouse(); Set(FromX(e.GetPosition(this).X)); e.Handled = true; };
        MouseMove += (_, e) => { if (IsMouseCaptured) Set(FromX(e.GetPosition(this).X)); };
        MouseLeftButtonUp += (_, _) => { if (!IsMouseCaptured) return; ReleaseMouseCapture(); Committed?.Invoke(); };
        MouseWheel += (_, e) => { Nudge(e.Delta > 0 ? 0.05 : -0.05); e.Handled = true; };
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Down) { Nudge(-0.01); e.Handled = true; }
            else if (e.Key is Key.Right or Key.Up) { Nudge(0.01); e.Handled = true; }
        };
    }

    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, Max); Layout(); }
    }

    public void Nudge(double d)
    {
        Set(Math.Round((_value + d) / Math.Abs(d)) * Math.Abs(d));
        Committed?.Invoke();
    }

    void Set(double v)
    {
        v = Math.Round(Math.Clamp(v, 0, Max), 2);
        if (v == _value) return;
        Value = v;
        ValueChanged?.Invoke(v);
    }

    double FromX(double x) => (x - 8) / Math.Max(1, ActualWidth - 16) * Max;

    void Layout() => _thumb.Margin = new Thickness(_value / Max * Math.Max(0, ActualWidth - 16), 0, 0, 0);
}
