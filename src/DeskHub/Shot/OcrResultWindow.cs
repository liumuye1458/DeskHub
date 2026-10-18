using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using DeskHub.Core;

namespace DeskHub.Shot;

/// <summary>提取文字的结果：左边原图，右边可编辑的文字，一键复制。</summary>
sealed class OcrResultWindow : Window
{
    public OcrResultWindow(string text, BitmapSource img)
    {
        Title = L.T("提取文字");
        Width = 760; Height = 460; MinWidth = 480; MinHeight = 300;
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        Background = Brushes.White;
        Topmost = true;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));
        UseLayoutRounding = true;

        var box = new TextBox
        {
            Text = text.Length > 0 ? text : "",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xe3, 0xe3, 0xe3)), Padding = new Thickness(8), FontSize = 14,
        };
        var hint = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), VerticalAlignment = VerticalAlignment.Center,
            Text = text.Length > 0 ? L.T("识别结果可以直接修改") : L.T("没有识别到文字") };
        var copy = new Button { Content = L.T("复制全部"), Style = (Style)FindResource("BtnG"), MinWidth = 90 };
        var close = new Button { Content = L.T("关闭"), Style = (Style)FindResource("BtnW"), MinWidth = 70, Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(box.SelectionLength > 0 ? box.SelectedText : box.Text); hint.Text = L.T("已复制"); } catch { }
        };
        close.Click += (_, _) => Close();

        var pic = new Image { Source = img, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top };
        var grid = new Grid { Margin = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var picBox = new Border { Child = pic, Background = new SolidColorBrush(Color.FromRgb(0xf5, 0xf6, 0xf7)), Padding = new Thickness(6) };
        Grid.SetColumn(box, 2);
        grid.Children.Add(picBox);
        grid.Children.Add(box);
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(copy); btns.Children.Add(close);
        DockPanel.SetDock(btns, Dock.Right);
        bottom.Children.Add(btns); bottom.Children.Add(hint);
        Grid.SetRow(bottom, 1); Grid.SetColumnSpan(bottom, 3);
        grid.Children.Add(bottom);
        Content = grid;

        Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    /// <summary>放在截图所在屏幕的中间（参数是物理像素）。</summary>
    public void CenterOn(Rect screenPx)
    {
        var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)(ActualWidth * dpi.DpiScaleX), hh = (int)(ActualHeight * dpi.DpiScaleY);
        Interop.Native.SetWindowPos(h, IntPtr.Zero, (int)(screenPx.X + (screenPx.Width - w) / 2), (int)(screenPx.Y + (screenPx.Height - hh) / 2), 0, 0,
            Interop.Native.SWP_NOSIZE | Interop.Native.SWP_NOZORDER);
        Activate();
    }
}
