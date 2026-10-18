using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>备忘录：左边清单，右边待办。可以标优先级、设提醒（到点弹窗）、关联文件，清单可以钉到桌面变成便签。</summary>
public partial class MemoWindow : Window
{
    const string DoneView = "\u0001done";
    string _cur;
    readonly Action<MemoList, bool> _pin;
    static MemoStore S => MemoStore.I;

    /// <param name="pin">钉到桌面 / 取消钉住（由 ToolHub 创建或关闭便签窗口）。</param>
    public MemoWindow(Action<MemoList, bool> pin)
    {
        InitializeComponent();
        _pin = pin;
        _cur = S.Lists.FirstOrDefault()?.Id ?? DoneView;
        AddBox.TextChanged += (_, _) => AddHint.Visibility = AddBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 多行输入：回车保存，Shift+回车换行；粘贴多段文字整段保留（单行框会把第一个换行后的内容截掉）
        AddBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
            e.Handled = true;
            if (AddBox.Text.Trim().Length == 0 || S.Find(_cur) is not { } l) return;
            l.Items.Add(new MemoItem { Text = MemoStore.Clean(AddBox.Text) });
            AddBox.Text = "";
            S.Save();
        };
        PinBtn.Click += (_, _) => { if (S.Find(_cur) is { } l) _pin(l, !l.Pinned); };
        ExportBtn.Click += (_, _) => Export();
        // “已完成”页里这个按钮是“清空已完成”
        PinBtn.Click += (_, _) =>
        {
            if (_cur != DoneView) return;
            int n = S.Lists.Sum(l => l.Items.Count(i => i.Done));
            if (n == 0 || MessageBox.Show(this, L.F("删除全部 {0} 条已完成的待办？", n), L.T("备忘录"), MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            foreach (var l in S.Lists) l.Items.RemoveAll(i => i.Done);
            S.Save();
        };
        MemoStore.Changed += Build;
        Closed += (_, _) => MemoStore.Changed -= Build;
        Build();
    }

    public void Popup()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        AddBox.Focus();
    }

    void Build()
    {
        if (_cur != DoneView && S.Find(_cur) == null) _cur = S.Lists.FirstOrDefault()?.Id ?? DoneView;
        BuildLists();
        BuildItems();
    }

    void BuildLists()
    {
        ListPanel.Children.Clear();
        foreach (var l in S.Lists) AddListRow(l.Id, l.Name + (l.Pinned ? "  📌" : ""), l.Items.Count(i => !i.Done), l);
        AddListRow(DoneView, L.T("已完成"), S.Lists.Sum(l => l.Items.Count(i => i.Done)), null);
        var add = new TextBlock { Text = L.T("＋ 新建清单"), Foreground = Brush("#07C255"), Margin = new Thickness(12, 10, 0, 0), Cursor = Cursors.Hand };
        add.MouseLeftButtonUp += (_, _) =>
        {
            var n = InputDialog.Ask(this, L.T("新建清单"), "")?.Trim();
            if (string.IsNullOrEmpty(n)) return;
            var l = new MemoList { Name = n };
            S.Lists.Add(l);
            _cur = l.Id;
            S.Save();
        };
        ListPanel.Children.Add(add);
    }

    void AddListRow(string id, string text, int count, MemoList? l)
    {
        bool on = _cur == id;
        var dock = new DockPanel();
        var c = new TextBlock { Text = count.ToString(), Foreground = Brush("#AAA"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(c, Dock.Right);
        dock.Children.Add(c);
        dock.Children.Add(new TextBlock { Text = text, Foreground = on ? Brush("#07C255") : Brush("#333"), FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        var b = new Border { Child = dock, Padding = new Thickness(12, 9, 10, 9), CornerRadius = new CornerRadius(5), Background = on ? Brush("#E9F8EF") : Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 2), AllowDrop = l != null };
        b.MouseEnter += (_, _) => { if (_cur != id) b.Background = Brush("#ECEEF0"); };
        b.MouseLeave += (_, _) => { if (_cur != id) b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += (_, _) => { _cur = id; Build(); };
        if (l != null)
        {
            var m = new ContextMenu();
            void Add(string t, Action a) { var mi = new MenuItem { Header = t }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
            Add(l.Pinned ? L.T("取消钉到桌面") : L.T("钉到桌面"), () => _pin(l, !l.Pinned));
            Add(L.T("清单改名…"), () => { var n = InputDialog.Ask(this, L.T("清单改名"), l.Name)?.Trim(); if (!string.IsNullOrEmpty(n)) { l.Name = n; S.Save(); } });
            Add(L.T("删除清单"), () =>
            {
                if (l.Items.Count > 0 && MessageBox.Show(this, L.F("删除清单“{0}”和里面的 {1} 条待办？", l.Name, l.Items.Count), L.T("备忘录"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                if (l.Pinned) _pin(l, false);
                S.Lists.Remove(l); S.Save();
            });
            b.ContextMenu = m;
            // 把待办拖到别的清单
            b.Drop += (_, e) =>
            {
                if (e.Data.GetData("DeskHub.Memo") is string iid && S.Lists.SelectMany(x => x.Items.Select(i => (x, i))).FirstOrDefault(p => p.i.Id == iid) is { x: not null } p && p.x != l)
                { p.x.Items.Remove(p.i); l.Items.Add(p.i); S.Save(); }
            };
        }
        ListPanel.Children.Add(b);
    }

    void BuildItems()
    {
        ItemPanel.Children.Clear();
        if (_cur == DoneView)
        {
            ListTitle.Text = L.T("已完成");
            AddRow.Visibility = Visibility.Collapsed;
            PinBtn.Content = L.T("清空已完成");
            PinBtn.Visibility = Visibility.Visible;
            ExportBtn.Visibility = Visibility.Collapsed;
            var done = S.Lists.SelectMany(l => l.Items.Where(i => i.Done).Select(i => (l, i))).OrderByDescending(p => p.i.DoneAt).ToList();
            foreach (var (l, i) in done) ItemPanel.Children.Add(MemoUI.Row(this, l, i, dark: false, showList: true));
            if (done.Count == 0) ItemPanel.Children.Add(new TextBlock { Text = L.T("还没有完成的待办"), Foreground = Brush("#BBB"), Margin = new Thickness(8, 30, 0, 0) });
            return;
        }
        var list = S.Find(_cur)!;
        ListTitle.Text = list.Name;
        AddRow.Visibility = Visibility.Visible;
        ExportBtn.Visibility = Visibility.Visible;
        PinBtn.Content = list.Pinned ? L.T("📌 已钉到桌面（点击取消）") : L.T("钉到桌面");
        foreach (var i in MemoUI.Ordered(list)) ItemPanel.Children.Add(MemoUI.Row(this, list, i, dark: false, showList: false));
        if (list.Items.Count == 0) ItemPanel.Children.Add(new TextBlock { Text = L.T("在上面输入一条待办，按回车保存。\n右键一条待办可以设优先级、提醒时间、关联文件；也可以把文件直接拖到待办上。"), Foreground = Brush("#BBB"), Margin = new Thickness(8, 30, 0, 0), LineHeight = 22 });
    }

    void Export()
    {
        if (S.Find(_cur) is not { } l) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = l.Name + ".txt", Filter = L.T("文本文件 (*.txt)|*.txt|Markdown (*.md)|*.md"), InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        if (dlg.ShowDialog(this) == true) File.WriteAllText(dlg.FileName, MemoStore.Export(l), System.Text.Encoding.UTF8);
    }

    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}

/// <summary>备忘录窗口和桌面便签共用的待办行、右键菜单、提醒设置。</summary>
static class MemoUI
{
    static MemoStore S => MemoStore.I;
    public static readonly string[] PriColors = { "Transparent", "#F04B4B", "#FFB800", "#0095FC" };
    public static readonly string[] PriNames = { L.T("无"), L.T("紧急（红）"), L.T("重要（黄）"), L.T("一般（蓝）") };

    public static IEnumerable<MemoItem> Ordered(MemoList l) =>
        l.Items.Where(i => !i.Done).OrderBy(i => i.Priority == 0 ? 9 : i.Priority).ThenBy(i => i.Remind ?? DateTime.MaxValue).ThenBy(i => i.Created)
         .Concat(l.Items.Where(i => i.Done).OrderByDescending(i => i.DoneAt).Take(10));

    public static FrameworkElement Row(Window owner, MemoList list, MemoItem it, bool dark, bool showList)
    {
        var fg = dark ? Brush("#222") : Brush("#222");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var pri = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = Brush(PriColors[it.Done ? 0 : Math.Clamp(it.Priority, 0, 3)]), Margin = new Thickness(0, 2, 8, 2) };
        grid.Children.Add(pri);

        var box = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.5), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0),
            BorderBrush = it.Done ? Brush("#07C255") : Brush("#BBBBBB"), Background = it.Done ? Brush("#07C255") : Brushes.Transparent,
            Child = it.Done ? new TextBlock { Text = "✓", Foreground = Brushes.White, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) } : null,
            ToolTip = it.Done ? L.T("标为未完成") : L.T("标为完成"),
        };
        box.MouseLeftButtonUp += (_, e) => { S.SetDone(it, !it.Done); S.Save(); e.Handled = true; };
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        var txt = new StackPanel();
        var t = new TextBlock { Text = it.Text, Foreground = it.Done ? Brush("#AAA") : fg, TextWrapping = TextWrapping.Wrap, FontSize = dark ? 13 : 14 };
        if (it.Done) t.TextDecorations = TextDecorations.Strikethrough;
        txt.Children.Add(t);
        var meta = new List<Inline>();
        if (it.Done && it.DoneAt is { } da) meta.Add(new Run(L.F("已完成 · {0}", When(da))));
        if (!it.Done && it.Remind is { } r)
            meta.Add(new Run($"⏰ {When(r)}" + (it.Repeat.Length > 0 ? " · " + MemoStore.RepeatName(it.Repeat) : "")) { Foreground = r <= DateTime.Now && !it.Reminded ? Brush("#E5413C") : Brush("#E47600") });
        if (it.File != null) meta.Add(new Run("📎 " + Path.GetFileName(it.File)) { Foreground = Brush("#07A548") });
        if (showList) meta.Add(new Run(list.Name));
        if (meta.Count > 0)
        {
            var m = new TextBlock { FontSize = 12, Foreground = Brush("#999"), Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
            for (int i = 0; i < meta.Count; i++) { if (i > 0) m.Inlines.Add(new Run("  ·  ")); m.Inlines.Add(meta[i]); }
            txt.Children.Add(m);
        }
        Grid.SetColumn(txt, 2);
        grid.Children.Add(txt);

        var row = new Border { Child = grid, Padding = new Thickness(6, 9, 6, 9), BorderBrush = dark ? Brush("#14000000") : Brush("#F2F2F2"), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent, AllowDrop = true };
        row.MouseEnter += (_, _) => row.Background = dark ? Brush("#0F000000") : Brush("#F7F8F9");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        // 双击改文字；双击带文件的打开文件
        Point? down = null;
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { down = null; Edit(owner, it); e.Handled = true; return; }
            down = e.GetPosition(row);
        };
        row.MouseLeftButtonUp += (_, _) => down = null;
        row.MouseMove += (_, e) =>
        {
            // 按住拖动一小段才算拖（拖到左边别的清单上 = 移过去）
            if (dark || down is not { } p0 || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(row);
            if (Math.Abs(p.X - p0.X) < 6 && Math.Abs(p.Y - p0.Y) < 6) return;
            down = null;
            DragDrop.DoDragDrop(row, new DataObject("DeskHub.Memo", it.Id), DragDropEffects.Move);
        };
        // 把文件拖到待办上 = 关联文件
        row.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };
        row.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) { it.File = f[0]; S.Save(); e.Handled = true; }
        };
        row.ContextMenu = Menu(owner, list, it);
        return row;
    }

    static string When(DateTime t)
    {
        var d = (t.Date - DateTime.Today).Days;
        var day = d switch { 0 => L.T("今天"), 1 => L.T("明天"), -1 => L.T("昨天"), 2 => L.T("后天"), _ => t.Year == DateTime.Now.Year ? (L.IsZh ? t.ToString("M月d日") : t.ToString(L.Culture.DateTimeFormat.MonthDayPattern, L.Culture)) : t.ToString("yyyy-MM-dd") };
        return $"{day} {t:HH:mm}";
    }

    static void Edit(Window owner, MemoItem it)
    {
        var n = InputDialog.Ask(owner, L.T("修改待办"), it.Text, multiline: true) is { } s ? MemoStore.Clean(s) : null;
        if (string.IsNullOrEmpty(n) || n == it.Text) return;
        it.Text = n; S.Save();
    }

    static ContextMenu Menu(Window owner, MemoList list, MemoItem it)
    {
        var m = new ContextMenu();
        MenuItem Add(ItemsControl parent, string t, Action a, bool chk = false) { var mi = new MenuItem { Header = t, IsChecked = chk }; mi.Click += (_, _) => a(); parent.Items.Add(mi); return mi; }
        Add(m, it.Done ? L.T("标为未完成") : L.T("标为完成"), () => { S.SetDone(it, !it.Done); S.Save(); });
        Add(m, L.T("修改文字…"), () => Edit(owner, it));
        var pri = new MenuItem { Header = L.T("优先级") };
        for (int p = 1; p <= 3; p++) { int pp = p; Add(pri, PriNames[p], () => { it.Priority = pp; S.Save(); }, it.Priority == p); }
        Add(pri, L.T("不标"), () => { it.Priority = 0; S.Save(); }, it.Priority == 0);
        m.Items.Add(pri);
        Add(m, it.Remind == null ? L.T("设提醒…") : L.T("改提醒…"), () => { if (ReminderDialog.Ask(owner, it)) S.Save(); });
        if (it.File != null)
        {
            Add(m, L.T("打开关联文件"), () => { try { Process.Start(new ProcessStartInfo(it.File) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(owner, L.T("打不开：") + ex.Message, L.T("备忘录")); } });
            Add(m, L.T("去掉关联文件"), () => { it.File = null; S.Save(); });
        }
        else
            Add(m, L.T("关联文件…"), () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("选择要关联的文件") };
                if (dlg.ShowDialog(owner) == true) { it.File = dlg.FileName; S.Save(); }
            });
        var move = new MenuItem { Header = L.T("移到清单") };
        foreach (var l in S.Lists.Where(l => l != list)) { var ll = l; Add(move, l.Name, () => { list.Items.Remove(it); ll.Items.Add(it); S.Save(); }); }
        if (move.Items.Count > 0) m.Items.Add(move);
        m.Items.Add(new Separator());
        Add(m, L.T("删除"), () => { list.Items.Remove(it); S.Save(); });
        return m;
    }

    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}

/// <summary>设提醒：日期 + 时间 + 重复。到点由 ToolHub 弹提醒窗。</summary>
static class ReminderDialog
{
    public static bool Ask(Window owner, MemoItem it)
    {
        var start = it.Remind ?? DateTime.Today.AddHours(DateTime.Now.Hour + 1);
        var date = new DatePicker { SelectedDate = start.Date, Width = 150, Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
        var time = new TextBox { Text = start.ToString("HH:mm"), Width = 70, Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.FindResource("WinTextBox") };
        var repeat = new ComboBox { Width = 110, Height = 30, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var keys = new[] { "", "day", "workday", "week", "month" };
        foreach (var k in keys) repeat.Items.Add(k.Length == 0 ? L.T("不重复") : MemoStore.RepeatName(k));
        repeat.SelectedIndex = Math.Max(0, Array.IndexOf(keys, it.Repeat));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(date); row.Children.Add(time); row.Children.Add(repeat);

        var quick = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        void Q(string text, Func<DateTime> at)
        {
            var b = new Button { Content = text, Style = (Style)Application.Current.FindResource("BtnW"), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (_, _) => { var t = at(); date.SelectedDate = t.Date; time.Text = t.ToString("HH:mm"); };
            quick.Children.Add(b);
        }
        Q(L.T("1 小时后"), () => DateTime.Now.AddHours(1));
        Q(L.T("今天 18:00"), () => DateTime.Today.AddHours(18));
        Q(L.T("明天 9:00"), () => DateTime.Today.AddDays(1).AddHours(9));
        Q(L.T("下周一 9:00"), () => DateTime.Today.AddDays(((int)DayOfWeek.Monday - (int)DateTime.Today.DayOfWeek + 7) % 7 is 0 ? 7 : ((int)DayOfWeek.Monday - (int)DateTime.Today.DayOfWeek + 7) % 7).AddHours(9));

        var err = new TextBlock { Foreground = Brushes.IndianRed, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = L.T("确定"), Style = (Style)Application.Current.FindResource("BtnG"), IsDefault = true, MinWidth = 80 };
        var clear = new Button { Content = L.T("清除提醒"), Style = (Style)Application.Current.FindResource("BtnW"), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Visibility = it.Remind == null ? Visibility.Collapsed : Visibility.Visible };
        var cancel = new Button { Content = L.T("取消"), Style = (Style)Application.Current.FindResource("BtnW"), IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        bar.Children.Add(ok); bar.Children.Add(clear); bar.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
        panel.Children.Add(new TextBlock { Text = it.Text, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(row); panel.Children.Add(quick); panel.Children.Add(err); panel.Children.Add(bar);
        var w = new Window
        {
            Title = L.T("设提醒"), Content = panel, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = owner.FontFamily, FontSize = 13,
        };
        bool changed = false;
        ok.Click += (_, _) =>
        {
            if (date.SelectedDate is not { } d || !TimeSpan.TryParse(time.Text.Replace('：', ':'), out var ts) || ts.TotalHours >= 24) { err.Text = L.T("时间写成 14:30 这样"); return; }
            var at = d.Date + ts;
            var rep = keys[Math.Max(0, repeat.SelectedIndex)];
            if (at <= DateTime.Now && rep.Length == 0) { err.Text = L.T("这个时间已经过了"); return; }
            if (at <= DateTime.Now) at = MemoStore.Next(at, rep, DateTime.Now);
            it.Remind = at; it.Repeat = rep; it.Reminded = false;
            changed = true; w.DialogResult = true;
        };
        clear.Click += (_, _) => { it.Remind = null; it.Repeat = ""; it.Reminded = false; changed = true; w.DialogResult = true; };
        w.ShowDialog();
        return changed;
    }
}
