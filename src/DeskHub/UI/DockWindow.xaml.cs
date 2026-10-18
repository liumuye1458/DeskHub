using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DeskHub.Interop;

namespace DeskHub.UI;

/// <summary>桌面右下角的工具条：查找、截图、计算器、设置（和分区一样贴在桌面上）。</summary>
public partial class DockWindow : Window
{
    public event Action? Moved;

    public DockWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DesktopHost.Pin(this);
        Root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is Border b && b == Root) { DragMove(); Moved?.Invoke(); }
        };
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
        btn.Click += (_, _) => run();
        Buttons.Children.Add(btn);
    }

    public void SetTip(string text, string tip)
    {
        foreach (Button b in Buttons.Children)
            if (b.Content is StackPanel { Children: [_, TextBlock t] } && t.Text == text) b.ToolTip = tip;
    }
}
