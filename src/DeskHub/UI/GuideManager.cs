using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>编辑一张便签：文字、颜色、字号、小尖角、置顶、每天/一次性、定时出现、流程步骤。</summary>
public sealed class GuideEditor : Window
{
    readonly GuideNote _n;
    readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6), FontSize = 14 };
    readonly ComboBox _color = new() { Width = 110 }, _size = new() { Width = 110 };
    readonly CheckBox _arrow = new(), _top = new(), _timed = new();
    readonly RadioButton _daily = new() { GroupName = "mode" }, _once = new() { GroupName = "mode" };
    readonly TextBox _at = new() { Width = 70, Height = 26, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly ComboBox _flow = new() { IsEditable = true, Width = 180 };
    readonly TextBox _step = new() { Width = 50, Height = 26, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock _err = new() { Foreground = Brushes.IndianRed, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

    public GuideEditor(GuideNote n, bool isNew)
    {
        _n = n;
        Title = isNew ? L.T("新建便签") : L.T("编辑便签");
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false; Background = Brushes.White;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"); FontSize = 13;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));

        _text.Text = n.Text;
        foreach (var (k, name) in new[] { ("yellow", L.T("黄")), ("red", L.T("红")), ("green", L.T("绿")), ("blue", L.T("蓝")) })
            _color.Items.Add(new ComboBoxItem { Content = name, Tag = k, IsSelected = n.Color == k });
        foreach (var (v, name) in new[] { (14.0, L.T("小")), (18.0, L.T("中")), (24.0, L.T("大")), (32.0, L.T("特大")) })
            _size.Items.Add(new ComboBoxItem { Content = name, Tag = v, IsSelected = Math.Abs(n.FontSize - v) < 0.1 });
        if (_size.SelectedIndex < 0) _size.SelectedIndex = 1;
        _arrow.Content = L.T("带小尖角（把便签放在要指的东西右边，尖角对准它）"); _arrow.IsChecked = n.Arrow;
        _top.Content = L.T("置顶：浮在所有窗口上面（指软件里的按钮时用；不勾就贴在桌面上）"); _top.IsChecked = n.Topmost;
        _daily.Content = L.T("每天重复：点 OK 后今天不再显示，明天（凌晨 5 点后）再出现"); _daily.IsChecked = n.Daily;
        _once.Content = L.T("一次性：点 OK 后收起，不再出现"); _once.IsChecked = !n.Daily;
        _timed.Content = L.T("定时出现，时间："); _timed.IsChecked = n.ShowAt.Length > 0;
        _at.Text = n.ShowAt.Length > 0 ? n.ShowAt : "19:55";
        _flow.Items.Add(new ComboBoxItem { Content = L.T("（不属于流程）"), Tag = "" });
        foreach (var f in GuideStore.Flows) _flow.Items.Add(new ComboBoxItem { Content = f, Tag = f });
        _flow.Text = n.Flow.Length > 0 ? n.Flow : L.T("（不属于流程）");
        _step.Text = n.Flow.Length > 0 ? n.Step.ToString() : "";
        _flow.SelectionChanged += (_, _) =>
        {
            if (_flow.SelectedItem is ComboBoxItem { Tag: string f }) _step.Text = f.Length == 0 ? "" : (f == _n.Flow ? _n.Step : GuideStore.NextStep(f)).ToString();
        };

        UIElement Row(string label, params UIElement[] els)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            sp.Children.Add(new TextBlock { Text = label, Width = 70, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray });
            foreach (var e in els) sp.Children.Add(e);
            return sp;
        }
        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(new TextBlock { Text = L.T("内容"), Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(_text);
        root.Children.Add(Row(L.T("颜色"), _color, new TextBlock { Text = L.T("字号"), Margin = new Thickness(24, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray }, _size));
        foreach (var c in new FrameworkElement[] { _arrow, _top, _daily, _once }) { c.Margin = new Thickness(0, 10, 0, 0); root.Children.Add(c); }
        var timedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _timed.VerticalAlignment = VerticalAlignment.Center;
        timedRow.Children.Add(_timed); timedRow.Children.Add(_at);
        timedRow.Children.Add(new TextBlock { Text = L.T("（到这个时间才显示，并闪几下提醒）"), Foreground = Brushes.Gray, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(timedRow);
        root.Children.Add(Row(L.T("流程"), _flow, new TextBlock { Text = L.T("第"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, _step, new TextBlock { Text = L.T("步"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }));
        root.Children.Add(new TextBlock { Text = L.T("同一个流程的便签一步一步出现：前一步点了 OK，下一步才显示。起个流程名，例如“开播前”。"), Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(70, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(_err);
        var ok = new Button { Content = L.T("保存"), Style = (Style)Application.Current.FindResource("BtnG"), IsDefault = false, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = L.T("取消"), Style = (Style)Application.Current.FindResource("BtnW"), IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => { if (Commit()) DialogResult = true; };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        btns.Children.Add(cancel); btns.Children.Add(ok);
        root.Children.Add(btns);
        Content = root;
        Loaded += (_, _) => { _text.Focus(); _text.CaretIndex = _text.Text.Length; };
    }

    bool Commit()
    {
        string at = "";
        if (_timed.IsChecked == true)
        {
            if (!TimeSpan.TryParse(_at.Text.Trim().Replace('：', ':'), out var t) || t.TotalHours >= 24) { _err.Text = L.T("时间格式不对，例如 19:55"); return false; }
            at = $"{(int)t.TotalHours:00}:{t.Minutes:00}";
        }
        var flow = _flow.Text.Trim();
        if (flow == L.T("（不属于流程）")) flow = "";
        int step = 0;
        if (flow.Length > 0 && !int.TryParse(_step.Text.Trim(), out step)) step = GuideStore.NextStep(flow);
        _n.Text = MemoText.Clean(_text.Text);
        _n.Color = (_color.SelectedItem as ComboBoxItem)?.Tag as string ?? "yellow";
        _n.FontSize = (_size.SelectedItem as ComboBoxItem)?.Tag is double d ? d : 18;
        _n.Arrow = _arrow.IsChecked == true;
        _n.Topmost = _top.IsChecked == true;
        _n.Daily = _daily.IsChecked == true;
        _n.ShowAt = at;
        _n.Flow = flow; _n.Step = flow.Length > 0 ? step : 0;
        return true;
    }

    public static bool Ask(Window? owner, GuideNote n, bool isNew)
    {
        var w = new GuideEditor(n, isNew);
        if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        return w.ShowDialog() == true;
    }
}

/// <summary>文字整理：统一换行、去首尾空白。</summary>
public static class MemoText
{
    public static string Clean(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
}

/// <summary>
/// 备忘录（管理处）：所有便签的列表。新建、编辑、锁定、导出 / 导入、让收起的便签重新显示。
/// 便签本身贴在屏幕上，这里只是管理它们。
/// </summary>
public sealed class GuideManager : Window
{
    readonly StackPanel _list = new() { Margin = new Thickness(16, 0, 16, 16) };
    readonly Button _lock;
    readonly Func<Rect> _wa;

    public GuideManager(Func<Rect> workArea)
    {
        _wa = workArea;
        Title = L.T("备忘录");
        Width = 760; Height = 600; MinWidth = 600; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Brushes.White;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"); FontSize = 13;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));

        Button Btn(string t, bool green, Action a)
        {
            var b = new Button { Content = t, Style = (Style)Application.Current.FindResource(green ? "BtnG" : "BtnW"), Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (_, _) => a();
            return b;
        }
        _lock = Btn("", false, ToggleLock);
        var bar = new WrapPanel { Margin = new Thickness(16, 14, 16, 6) };
        bar.Children.Add(Btn(L.T("＋ 新建便签"), true, New));
        bar.Children.Add(_lock);
        bar.Children.Add(Btn(L.T("今天全部重新显示"), false, ReshowToday));
        bar.Children.Add(Btn(L.T("导出…"), false, Export));
        bar.Children.Add(Btn(L.T("导入…"), false, Import));

        var hint = new TextBlock
        {
            Text = L.T("便签像对话气泡，左边有个小尖角：把便签拖到要指的东西右边，尖角对准它。点 OK 后 3 秒收起，每天重复的便签第二天凌晨 5 点后重新出现。摆好后点“锁定”，就只能点 OK，不会被误拖误改。"),
            Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 10), FontSize = 12.5,
        };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(hint, Dock.Top);
        root.Children.Add(bar); root.Children.Add(hint);
        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        GuideStore.Changed += Build;
        Closed += (_, _) => GuideStore.Changed -= Build;
        Build();
    }

    public void Popup()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    void Build()
    {
        _lock.Content = GuideStore.D.Locked ? L.T("🔒 已锁定（点一下解锁）") : L.T("🔓 锁定");
        _list.Children.Clear();
        var now = DateTime.Now;
        var notes = GuideStore.D.Notes.OrderBy(n => n.Flow.Length == 0 ? 1 : 0).ThenBy(n => n.Flow).ThenBy(n => n.Step).ThenBy(n => n.Created).ToList();
        if (notes.Count == 0)
            _list.Children.Add(new TextBlock { Text = L.T("还没有便签。点“＋ 新建便签”写一张，然后拖到要指的东西右边，用左边的小尖角对准它。"), Foreground = Brushes.Gray, Margin = new Thickness(4, 30, 4, 0), TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center });
        foreach (var n in notes) _list.Children.Add(Row(n, now));
    }

    FrameworkElement Row(GuideNote n, DateTime now)
    {
        var st = GuideStore.StateOf(n, now);
        var g = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var (card, border, accent) = GuideWindow.Colors.TryGetValue(n.Color, out var c) ? c : GuideWindow.Colors["yellow"];
        g.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Background = B(card), BorderBrush = B(accent), BorderThickness = new Thickness(2), Margin = new Thickness(0, 3, 12, 0), VerticalAlignment = VerticalAlignment.Top });

        var info = new StackPanel();
        info.Children.Add(new TextBlock { Text = n.Text.Length > 0 ? n.Text : L.T("（空）"), TextWrapping = TextWrapping.Wrap, MaxHeight = 60, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = st == GuideStore.State.Showing ? B("#222") : B("#999"), FontSize = 14 });
        var tags = new List<string>();
        tags.Add(st switch
        {
            GuideStore.State.Showing => L.T("显示中"),
            GuideStore.State.OkToday => L.T("今天已 OK，明天再出现"),
            GuideStore.State.Finished => L.T("已完成（一次性）"),
            GuideStore.State.Waiting => L.F("{0} 出现", n.ShowAt),
            _ => L.T("等上一步 OK"),
        });
        tags.Add(n.Daily ? L.T("每天") : L.T("一次性"));
        if (n.Flow.Length > 0) tags.Add(L.F("{0} · 第 {1} 步", n.Flow, n.Step));
        if (n.ShowAt.Length > 0 && st != GuideStore.State.Waiting) tags.Add(L.F("定时 {0}", n.ShowAt));
        if (n.Topmost) tags.Add(L.T("置顶"));
        if (!n.Arrow) tags.Add(L.T("无尖角"));
        info.Children.Add(new TextBlock { Text = string.Join("  ·  ", tags), Foreground = st == GuideStore.State.Showing ? B("#07A548") : B("#999"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        Grid.SetColumn(info, 1);
        g.Children.Add(info);

        var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        TextBlock Link(string t, Action a, string color = "#1F6FEB")
        {
            var l = new TextBlock { Text = t, Foreground = B(color), Cursor = Cursors.Hand, Margin = new Thickness(10, 0, 0, 0) };
            l.MouseLeftButtonUp += (_, e) => { a(); e.Handled = true; };
            return l;
        }
        if (st is GuideStore.State.OkToday or GuideStore.State.Finished) acts.Children.Add(Link(L.T("重新显示"), () => GuideStore.Reopen(n)));
        acts.Children.Add(Link(L.T("编辑"), () => { if (GuideEditor.Ask(this, n, false)) GuideStore.Save(); }));
        acts.Children.Add(Link(L.T("删除"), () =>
        {
            if (MessageBox.Show(this, L.T("删除这张便签？"), L.T("备忘录"), MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            GuideStore.D.Notes.Remove(n); GuideStore.Save();
        }, "#E5413C"));
        Grid.SetColumn(acts, 2);
        g.Children.Add(acts);

        var row = new Border { Child = g, Padding = new Thickness(8, 10, 8, 10), BorderBrush = B("#F0F0F0"), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent };
        row.MouseEnter += (_, _) => row.Background = B("#F7F8F9");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && GuideEditor.Ask(this, n, false)) GuideStore.Save(); };
        return row;
    }

    // ================= 操作 =================
    public void New()
    {
        var wa = _wa();
        var n = new GuideNote
        {
            X = Math.Round(wa.Left + wa.Width / 2 - 140), Y = Math.Round(wa.Top + wa.Height / 2 - 60),
        };
        if (!GuideEditor.Ask(this, n, true)) return;
        if (n.Flow.Length > 0)
        {
            // 同一流程的新步骤：放在上一步旁边，免得叠在一起
            var prev = GuideStore.D.Notes.Where(o => o.Flow == n.Flow).OrderByDescending(o => o.Step).FirstOrDefault();
            if (prev != null) { n.X = prev.X + 40; n.Y = prev.Y + 40; }
        }
        GuideStore.D.Notes.Add(n);
        bool wasLocked = GuideStore.D.Locked;
        GuideStore.D.Locked = false; // 新便签要能拖到位置
        GuideStore.Save();
        if (wasLocked) MessageBox.Show(this, L.T("已自动解锁，便签可以拖动了。摆好后记得再点“锁定”。"), L.T("备忘录"));
    }

    void ToggleLock()
    {
        GuideStore.D.Locked = !GuideStore.D.Locked;
        GuideStore.Save();
    }

    void ReshowToday()
    {
        foreach (var n in GuideStore.D.Notes.Where(n => n.Daily)) n.OkDay = null;
        GuideStore.Save();
    }

    void Export()
    {
        var d = new Microsoft.Win32.SaveFileDialog { Filter = L.T("便签文件") + " (*.json)|*.json", FileName = L.F("New B Desk 便签 {0:yyyy-MM-dd}.json", DateTime.Now) };
        if (d.ShowDialog(this) != true) return;
        try { GuideStore.Export(d.FileName, _wa()); MessageBox.Show(this, L.F("已导出 {0} 张便签。把这个文件拷到别的电脑上，用“导入”打开。", GuideStore.D.Notes.Count), L.T("备忘录")); }
        catch (Exception ex) { MessageBox.Show(this, L.T("导出失败：") + ex.Message, L.T("备忘录")); }
    }

    void Import()
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = L.T("便签文件") + " (*.json)|*.json" };
        if (d.ShowDialog(this) != true) return;
        bool replace = false;
        if (GuideStore.D.Notes.Count > 0)
        {
            var r = MessageBox.Show(this, L.T("要替换现有的便签吗？\n\n是 = 替换（现有的删掉）\n否 = 追加（两边都保留）"), L.T("导入便签"), MessageBoxButton.YesNoCancel);
            if (r == MessageBoxResult.Cancel) return;
            replace = r == MessageBoxResult.Yes;
        }
        try
        {
            int k = GuideStore.Import(d.FileName, _wa(), replace);
            MessageBox.Show(this, L.F("已导入 {0} 张便签。屏幕大小不同时位置按比例换算过，最好再对一下位置。", k), L.T("备忘录"));
        }
        catch (Exception ex) { MessageBox.Show(this, L.T("导入失败：") + ex.Message, L.T("备忘录")); }
    }
}
