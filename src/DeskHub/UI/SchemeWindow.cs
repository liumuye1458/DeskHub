using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>
/// 选个摆法：三张卡片，缩略图用你自己的壁纸画，一眼看出桌面会变成什么样。
/// A 铺满三列（东西全摊开）；B 两翼留白（左边软件、文件夹，右边日历和每天在变的文件，中间露壁纸）；C 一个大盒子（全收进标签）。
/// </summary>
public sealed class SchemeWindow : Window
{
    public static string NameOf(string s) => s switch { "B" => L.T("两翼留白"), "C" => L.T("一个大盒子"), _ => L.T("铺满三列") };

    static string DescOf(string s) => s switch
    {
        "B" => L.T("左边放软件和文件夹，右边放日历、今天、进行中、冷却区；中间留给壁纸，最清爽"),
        "C" => L.T("所有分区收进一个盒子，点标签切换；最省地方，适合笔记本、小屏幕"),
        _ => L.T("所有分区都摊开，铺满整个屏幕；东西多、屏幕大的时候一眼全看到"),
    };

    // 缩略图里各块的位置（占屏幕的比例：x, y, 宽, 高）和是不是盒子（顶上画一排标签）
    static (double X, double Y, double W, double H, int Tabs)[] Blocks(string s) => s switch
    {
        "B" => new[] { (0.015, 0.025, 0.16, 0.40, 2), (0.79, 0.025, 0.195, 0.30, 0), (0.79, 0.345, 0.195, 0.36, 3) },
        "C" => new[] { (0.74, 0.025, 0.245, 0.30, 0), (0.74, 0.345, 0.245, 0.53, 5) },
        _ => new[] { (0.015, 0.025, 0.25, 0.42, 0), (0.015, 0.465, 0.25, 0.41, 0), (0.28, 0.025, 0.40, 0.85, 0), (0.695, 0.025, 0.29, 0.40, 0), (0.695, 0.445, 0.29, 0.43, 0) },
    };

    string _pick;
    readonly Dictionary<string, Border> _cards = new();
    static readonly Brush GreenB = new SolidColorBrush(Color.FromRgb(0x07, 0xC2, 0x55));
    static readonly Brush LineB = new SolidColorBrush(Color.FromRgb(0xE3, 0xE5, 0xE8));

    SchemeWindow(string current)
    {
        _pick = current;
        Title = L.T("选个摆法");
        SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Brushes.White;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"); FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }

        var wall = LoadWallpaper();
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = L.T("桌面上的分区想怎么摆？"), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("WinText") });
        root.Children.Add(new TextBlock { Text = L.T("点一张卡片再按“用这个”，马上换好；换了不喜欢可以撤销。文件不会被移动。"), Foreground = (Brush)FindResource("WinText2"), Margin = new Thickness(0, 6, 0, 16) });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var s in new[] { "B", "C", "A" }) row.Children.Add(Card(s, wall));
        root.Children.Add(row);

        var ok = new Button { Content = L.T("用这个"), Style = (Style)FindResource("BtnG"), Padding = new Thickness(22, 6, 22, 6), IsDefault = true };
        var cancel = new Button { Content = L.T("取消"), Style = (Style)FindResource("BtnW"), Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
        ok.Click += (_, _) => { DialogResult = true; };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        btns.Children.Add(ok); btns.Children.Add(cancel);
        root.Children.Add(btns);
        Content = root;
        Mark();
    }

    /// <summary>弹出来让你选；返回选中的方案（A/B/C），取消返回 null。</summary>
    public static string? Ask(string current)
    {
        var w = new SchemeWindow(current);
        return w.ShowDialog() == true ? w._pick : null;
    }

    /// <summary>测试用：不弹出，直接把窗口画成图片。</summary>
    public static void Render(string current, string file)
    {
        var w = new SchemeWindow(current) { ShowActivated = false, Left = -10000, Top = -10000 };
        w.Show();
        w.UpdateLayout();
        var el = (FrameworkElement)w.Content;
        var dpi = VisualTreeHelper.GetDpi(w);
        var bmp = new RenderTargetBitmap((int)(el.ActualWidth * dpi.DpiScaleX), (int)(el.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight)); dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight)); }
        bmp.Render(dv);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(file)) enc.Save(fs);
        w.Close();
    }

    Border Card(string s, ImageSource? wall)
    {
        const double W = 248, H = 155;
        var thumb = new Grid { Width = W, Height = H, ClipToBounds = true };
        if (wall != null) thumb.Children.Add(new Image { Source = wall, Stretch = Stretch.UniformToFill });
        else thumb.Background = new LinearGradientBrush(Color.FromRgb(0x3F, 0x62, 0x83), Color.FromRgb(0xB0, 0x78, 0x4F), 60);
        var cv = new Canvas();
        double th = H * 0.075; // 任务栏
        cv.Children.Add(At(new Rectangle { Width = W, Height = th, Fill = new SolidColorBrush(Color.FromArgb(0xC8, 0x20, 0x20, 0x24)) }, 0, H - th));
        double usableH = H - th;
        foreach (var (x, y, w, h, tabs) in Blocks(s))
        {
            var r = new Rect(x * W, y * usableH, w * W, h * usableH);
            cv.Children.Add(At(new Rectangle { Width = r.Width, Height = r.Height, RadiusX = 3, RadiusY = 3, Fill = new SolidColorBrush(Color.FromArgb(0x70, 0x14, 0x16, 0x1E)), Stroke = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), StrokeThickness = 0.8 }, r.X, r.Y));
            // 盒子：顶上一排标签，第一个亮
            for (int i = 0; i < tabs; i++)
            {
                double tw = Math.Min(14, (r.Width - 8) / tabs - 2);
                cv.Children.Add(At(new Rectangle { Width = tw, Height = 3.5, RadiusX = 1.75, RadiusY = 1.75, Fill = new SolidColorBrush(Color.FromArgb(i == 0 ? (byte)0xF0 : (byte)0x70, 0xFF, 0xFF, 0xFF)) }, r.X + 4 + i * (tw + 2), r.Y + 4));
            }
            // 图标的小点：示意里面放着东西
            double top = r.Y + (tabs > 0 ? 12 : 8);
            for (double yy = top; yy + 5 < r.Bottom - 3 && yy < top + 30; yy += 9)
                for (double xx = r.X + 6; xx + 5 < r.Right - 3; xx += 9)
                    cv.Children.Add(At(new Rectangle { Width = 5, Height = 5, RadiusX = 1, RadiusY = 1, Fill = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)) }, xx, yy));
        }
        thumb.Children.Add(cv);

        var name = new TextBlock { Margin = new Thickness(2, 10, 0, 0), FontSize = 14, Foreground = (Brush)FindResource("WinText") };
        name.Inlines.Add(new System.Windows.Documents.Run(NameOf(s)) { FontWeight = FontWeights.SemiBold });
        if (s == "B") name.Inlines.Add(new System.Windows.Documents.Run("  " + L.T("推荐")) { Foreground = GreenB, FontSize = 12 });
        var desc = new TextBlock { Text = DescOf(s), TextWrapping = TextWrapping.Wrap, Width = W, Margin = new Thickness(2, 4, 0, 0), FontSize = 12, Foreground = (Brush)FindResource("WinText2"), LineHeight = 18 };
        var sp = new StackPanel();
        sp.Children.Add(new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = thumb });
        sp.Children.Add(name); sp.Children.Add(desc);
        var card = new Border { Child = sp, Padding = new Thickness(10), Margin = new Thickness(0, 0, 14, 0), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), BorderBrush = LineB, Background = Brushes.White, Cursor = Cursors.Hand };
        card.MouseLeftButtonUp += (_, _) => { _pick = s; Mark(); };
        card.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { _pick = s; DialogResult = true; } };
        card.MouseEnter += (_, _) => { if (_pick != s) card.BorderBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0xE8, 0xCB)); };
        card.MouseLeave += (_, _) => Mark();
        _cards[s] = card;
        return card;
    }

    void Mark()
    {
        foreach (var (k, c) in _cards)
        {
            c.BorderBrush = k == _pick ? GreenB : LineB;
            c.Background = k == _pick ? (Brush)FindResource("GreenSoft") : Brushes.White;
        }
    }

    static UIElement At(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); return e; }

    /// <summary>当前壁纸（缩小了读，省内存）；读不到就用渐变代替。</summary>
    static ImageSource? LoadWallpaper()
    {
        try
        {
            var f = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string;
            if (string.IsNullOrEmpty(f) || !File.Exists(f))
                f = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
            if (!File.Exists(f)) return null;
            var img = new BitmapImage();
            img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.DecodePixelWidth = 520; img.UriSource = new Uri(f); img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }
}
