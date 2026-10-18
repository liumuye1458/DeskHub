using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>
/// 布局：把现在分区、时钟、工具条的摆法存起来，随时切换；也能导出成文件拷到别的电脑，再导入。
/// 屏幕大小不同时按比例换算。
/// </summary>
public sealed class LayoutWindow : Window
{
    readonly DeskController _c;
    readonly StackPanel _list = new() { Margin = new Thickness(16, 0, 16, 16) };

    public LayoutWindow(DeskController c)
    {
        _c = c;
        Title = L.T("布局");
        Width = 760; Height = 520; MinWidth = 600; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Brushes.White;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"); FontSize = 13;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));

        var bar = new WrapPanel { Margin = new Thickness(16, 14, 16, 6) };
        bar.Children.Add(Btn(L.T("＋ 保存当前布局…"), true, SaveCurrent));
        bar.Children.Add(Btn(L.T("导出当前布局…"), false, () => Export(_c.CaptureSaved(L.T("我的布局")))));
        bar.Children.Add(Btn(L.T("导入…"), false, Import));
        var hint = new TextBlock
        {
            Text = L.T("把现在分区、时钟、工具条的摆法存起来，随时一键切换。导出的文件可以拷到别的电脑上导入，屏幕大小不同会按比例换算。换布局前会自动存一份“换布局之前的样子”，换错了能切回来。") + Environment.NewLine
                + L.F("现在的显示器：{0}。点“绑定当前显示器”，以后一接上这种组合就自动换成那套布局；没绑定的，换屏时按比例照搬你最近调整过的摆法。", DeskController.DescribeSig(_c.CurrentSig)),
            Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 10), FontSize = 12.5,
        };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(hint, Dock.Top);
        root.Children.Add(bar); root.Children.Add(hint);
        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Build();
    }

    static Button Btn(string t, bool green, Action a, double right = 8)
    {
        var b = new Button { Content = t, Style = (Style)Application.Current.FindResource(green ? "BtnG" : "BtnW"), Margin = new Thickness(0, 0, right, 0) };
        b.Click += (_, _) => a();
        return b;
    }

    public void Popup()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void Build()
    {
        _list.Children.Clear();
        var all = _c.Cfg.SavedLayouts;
        if (all.Count == 0)
            _list.Children.Add(new TextBlock { Text = L.T("还没有保存过布局。摆好以后点“＋ 保存当前布局”。"), Foreground = Brushes.Gray, Margin = new Thickness(4, 30, 4, 0), HorizontalAlignment = HorizontalAlignment.Center });
        foreach (var s in all) _list.Children.Add(Row(s));
    }

    FrameworkElement Row(SavedLayout s)
    {
        var g = new DockPanel();
        var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        acts.Children.Add(Btn(L.T("换成这个"), true, () => Apply(s), 6));
        bool bound = s.BindSig.Length > 0 && s.BindSig == _c.CurrentSig;
        var bind = Btn(bound ? L.T("取消绑定") : L.T("绑定当前显示器"), false, () => { _c.BindLayout(s, !bound); Build(); }, 6);
        bind.ToolTip = L.T("绑定后：每次接上现在这种显示器组合，就自动换成这套布局");
        acts.Children.Add(bind);
        acts.Children.Add(Btn(L.T("导出…"), false, () => Export(s), 6));
        acts.Children.Add(Btn(L.T("删除"), false, () => { _c.Cfg.SavedLayouts.Remove(s); _c.Cfg.Save(); Build(); }, 0));
        DockPanel.SetDock(acts, Dock.Right);
        g.Children.Add(acts);
        var name = s.Name == DeskController.AutoBackupName ? L.T(s.Name) : s.Name;
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), TextTrimming = TextTrimming.CharacterEllipsis });
        int fences = s.Profile.Items.Count(kv => kv.Value.W > 0);
        info.Children.Add(new TextBlock
        {
            Text = L.F("{0:yyyy-MM-dd HH:mm} 保存 · {1} 个分区 · 屏幕 {2:0}×{3:0}", s.Saved, fences, s.Profile.WaW, s.Profile.WaH),
            FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (s.BindSig.Length > 0)
            info.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xF8, 0xEF)), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1, 6, 2),
                Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = L.F("🔗 接 {0} 时自动用", DeskController.DescribeSig(s.BindSig)), FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0x07, 0xA5, 0x48)) },
            });
        g.Children.Add(info);
        return new Border { Child = g, Padding = new Thickness(8, 10, 8, 10), BorderBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    void SaveCurrent()
    {
        var name = InputDialog.Ask(this, L.T("给这套布局起个名字"), L.F("布局 {0:MM-dd HH:mm}", DateTime.Now));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (_c.Cfg.SavedLayouts.Any(x => x.Name == name)
            && MessageBox.Show(this, L.F("已经有叫“{0}”的布局了，要覆盖吗？", name), L.T("布局"), MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _c.SaveCurrentLayout(name);
        Build();
    }

    void Apply(SavedLayout s)
    {
        _c.ApplySaved(s);
        Build();
    }

    void Export(SavedLayout s)
    {
        var d = new Microsoft.Win32.SaveFileDialog { Filter = L.T("布局文件") + " (*.json)|*.json", FileName = L.F("New B Desk 布局 {0}.json", Safe(s.Name)) };
        if (d.ShowDialog(this) != true) return;
        try { _c.ExportLayout(s, d.FileName); MessageBox.Show(this, L.T("已导出。把这个文件拷到别的电脑上，在“布局”里点“导入”。"), L.T("布局")); }
        catch (Exception ex) { MessageBox.Show(this, L.T("导出失败：") + ex.Message, L.T("布局")); }
    }

    static string Safe(string n) => string.Concat(n.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    void Import()
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = L.T("布局文件") + " (*.json)|*.json" };
        if (d.ShowDialog(this) != true) return;
        try { var s = _c.ImportLayout(d.FileName); Build(); MessageBox.Show(this, L.F("已导入并换上布局“{0}”。", s.Name), L.T("布局")); }
        catch (Exception ex) { MessageBox.Show(this, L.T("导入失败：") + ex.Message, L.T("布局")); }
    }
}
