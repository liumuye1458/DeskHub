using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using DeskHub.Core;

namespace DeskHub.UI;

public partial class SettingsWindow : Window
{
    readonly DeskController _c;
    readonly List<RuleRow> _rows = new();
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "DeskHub";

    sealed class RuleRow
    {
        public required Border Root;
        public required TextBox Name, Keywords, Learned;
        public required string Color;
    }

    static readonly string[] Palette = { "#f04b4b", "#0095fc", "#ffb800", "#9b59b6", "#4a5568", "#07c255", "#ff7a45", "#13c2c2" };

    public SettingsWindow(DeskController c, string page = "rules")
    {
        InitializeComponent();
        _c = c;
        var cfg = c.Cfg;

        // 导航
        void Nav(RadioButton b, FrameworkElement p) => b.Checked += (_, _) =>
        {
            foreach (var x in new FrameworkElement[] { PageRules, PageFlow, PageKeys, PageGeneral, PageAbout }) x.Visibility = Visibility.Collapsed;
            p.Visibility = Visibility.Visible;
        };
        Nav(NavRules, PageRules); Nav(NavFlow, PageFlow); Nav(NavKeys, PageKeys); Nav(NavGeneral, PageGeneral); Nav(NavAbout, PageAbout);
        if (page == "flow") NavFlow.IsChecked = true;
        else if (page == "keys") NavKeys.IsChecked = true;
        else if (page == "general") NavGeneral.IsChecked = true;

        // 快捷键
        BuildKeys();
        NavKeys.Checked += (_, _) => _ = ShowIndexInfo();
        if (page == "keys") _ = ShowIndexInfo();
        Closed += (_, _) => _c.Tools?.ApplyHotkeys(); // 录快捷键时临时停用过，关窗时恢复

        // 规则
        foreach (var t in cfg.Topics) AddRow(t.Name, t.Color, t.Keywords, t.Learned);
        AddTopic.Click += (_, _) => AddRow(L.T("新事项"), Palette[_rows.Count % Palette.Length], new(), new(), focus: true);

        // 流转与归档
        ColdDays.Text = cfg.ColdDays.ToString();
        ArchiveDays.Text = cfg.ArchiveDays.ToString();
        ArchiveRoot.Text = cfg.ArchiveRoot;
        AutoArchive.IsChecked = cfg.AutoArchive;
        ArchiveRoot.TextChanged += (_, _) => UpdateExample();
        UpdateExample();
        Browse.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = L.T("选择归档位置"), UseDescriptionForTitle = true, SelectedPath = ArchiveRoot.Text };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) ArchiveRoot.Text = dlg.SelectedPath;
        };
        CheckNow.Click += (_, _) => { _c.CheckArchive(userAsked: true); BuildLog(); };
        BuildLog();

        // 常规
        HideIcons.IsChecked = cfg.HideSystemIcons;
        ShowSeconds.IsChecked = cfg.ShowSeconds;
        ClockNote.Text = cfg.ClockNote;
        // 语言：第一项"跟随系统"，其余用各自语言的名字显示（不懂当前语言也能找到自己的）
        LangBox.Items.Add(new ComboBoxItem { Content = L.T("跟随系统") + "（" + L.Languages.First(l => l.Code == L.FromSystem()).Name + "）", Tag = "" });
        foreach (var l in L.Languages) LangBox.Items.Add(new ComboBoxItem { Content = l.Name, Tag = l.Code });
        LangBox.SelectedItem = LangBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == cfg.Language) ?? LangBox.Items[0];
        AutoStart.IsChecked = IsAutoStart();
        AutoStart.IsEnabled = !c.Sandbox;
        ResetLayout.Click += (_, _) => _c.ResetLayout();

        // 关于
        VersionText.Text = L.F("版本 {0}{1}", typeof(App).Assembly.GetName().Version?.ToString(3), (c.Sandbox ? L.T("（沙盒模式）") : ""));
        DataDirRun.Text = AppConfig.DataDir;
        OpenData.Click += (_, _) => Process.Start("explorer.exe", AppConfig.DataDir);

        Save.Click += (_, _) => { if (Apply()) Close(); };
    }

    void UpdateExample() =>
        ArchiveExample.Text = L.F("例：{0}", Path.Combine(ArchiveRoot.Text.Trim(), DateTime.Now.ToString("yyyy-MM"), L.T("报销财务"), "发票_xxx.pdf"));

    // ---------------- 规则行 ----------------
    void AddRow(string name, string color, List<string> kws, List<string> learned, bool focus = false)
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = Brush(color), VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = L.T("点击换颜色") };
        var nameBox = new TextBox { Text = name, Width = 96, Style = Res<Style>("WinTextBox"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 10, 0) };
        var kwBox = new TextBox { Text = string.Join("，", kws), Style = Res<Style>("WinTextBox"), TextWrapping = TextWrapping.Wrap, MinHeight = 30 };
        var lrBox = new TextBox { Text = string.Join("，", learned), Style = Res<Style>("WinTextBox"), Background = Brush("#F3FCF6"), Foreground = Brush("#07A548"), MinHeight = 28, Margin = new Thickness(0, 6, 0, 0), ToolTip = L.T("学到的关键词") };
        var del = new Button { Content = L.T("删除"), Style = Res<Style>("BtnW"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };

        var right = new StackPanel();
        right.Children.Add(kwBox);
        right.Children.Add(new DockPanel
        {
            Children = { lrBox },
        });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
        head.Children.Add(dot); head.Children.Add(nameBox);
        Grid.SetColumn(head, 0); Grid.SetColumnSpan(head, 2); Grid.SetColumn(right, 2); Grid.SetColumn(del, 3);
        grid.Children.Add(head); grid.Children.Add(right); grid.Children.Add(del);

        var root = new Border { BorderBrush = Brush("#E8E8E8"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = grid };
        var row = new RuleRow { Root = root, Name = nameBox, Keywords = kwBox, Learned = lrBox, Color = color };
        dot.MouseLeftButtonUp += (_, _) =>
        {
            var i = Array.IndexOf(Palette, row.Color.ToLowerInvariant());
            row.Color = Palette[(i + 1) % Palette.Length];
            dot.Fill = Brush(row.Color);
        };
        del.Click += (_, _) => { RuleRows.Children.Remove(root); _rows.Remove(row); };
        _rows.Add(row);
        RuleRows.Children.Add(root);
        if (focus) { nameBox.Focus(); nameBox.SelectAll(); }
    }

    // ---------------- 归档记录 ----------------
    void BuildLog()
    {
        LogRows.Children.Clear();
        var batches = _c.State.ArchiveLog.GroupBy(r => r.Batch).OrderByDescending(g => g.Key).ToList();
        LogEmpty.Visibility = batches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var g in batches)
        {
            var first = g.First();
            int live = g.Count(r => !r.Undone);
            var undo = new Button { Content = live == 0 ? L.T("已撤回") : L.T("撤回这批"), Style = Res<Style>("BtnW"), IsEnabled = live > 0 };
            var batch = g.Key;
            undo.Click += (_, _) =>
            {
                var (ok, fail) = _c.Undo(batch);
                MessageBox.Show(this, fail == 0 ? L.F("已把 {0} 个文件搬回桌面", ok) : L.F("已搬回 {0} 个，{1} 个没搬回（归档里的文件已被移动或删除）", ok, fail), L.T("撤回归档"));
                BuildLog();
            };
            var names = string.Join("、", g.Take(4).Select(r => Path.GetFileName(r.From))) + (g.Count() > 4 ? L.F(" 等 {0} 个", g.Count()) : "");
            var dock = new DockPanel();
            DockPanel.SetDock(undo, Dock.Right);
            dock.Children.Add(undo);
            dock.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = L.F("{0:yyyy-MM-dd HH:mm}　归档 {1} 个文件", first.Time, g.Count()), FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = names, Foreground = Brush("#999"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis },
                },
            });
            LogRows.Children.Add(new Border { BorderBrush = Brush("#F0F0F0"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8), Child = dock });
        }
    }

    // ---------------- 保存 ----------------
    bool Apply()
    {
        SaveHint.Text = "";
        if (!int.TryParse(ColdDays.Text, out var cold) || cold < 1 || cold > 365) { SaveHint.Text = L.T("冷却天数要在 1–365 之间"); NavFlow.IsChecked = true; return false; }
        if (!int.TryParse(ArchiveDays.Text, out var arch) || arch <= cold || arch > 3650) { SaveHint.Text = L.T("归档天数要大于冷却天数"); NavFlow.IsChecked = true; return false; }
        var root = ArchiveRoot.Text.Trim();
        if (root.Length < 3 || !Path.IsPathRooted(root)) { SaveHint.Text = L.T("归档位置要写完整路径，例如 D:\\桌面归档"); NavFlow.IsChecked = true; return false; }
        var names = _rows.Select(r => r.Name.Text.Trim()).ToList();
        if (names.Any(n => n.Length == 0) || names.Distinct().Count() != names.Count || names.Contains(AppConfig.OtherTopic))
        { SaveHint.Text = L.T("事项名称不能为空、不能重复，也不能叫“其他”"); NavRules.IsChecked = true; return false; }

        var cfg = _c.Cfg;
        cfg.Topics = _rows.Select(r => new TopicRule
        {
            Name = r.Name.Text.Trim(),
            Color = r.Color,
            Keywords = Split(r.Keywords.Text),
            Learned = Split(r.Learned.Text),
        }).ToList();
        cfg.ColdDays = cold;
        cfg.ArchiveDays = arch;
        cfg.ArchiveRoot = root;
        cfg.AutoArchive = AutoArchive.IsChecked == true;
        cfg.ShowSeconds = ShowSeconds.IsChecked == true;
        cfg.ClockNote = string.Join(" ", ClockNote.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        var newLang = (string)((ComboBoxItem)LangBox.SelectedItem).Tag;
        bool langChanged = newLang != cfg.Language;
        cfg.Language = newLang;
        foreach (var (k, v) in _keySpecs) cfg.Hotkeys[k] = v;
        cfg.Save();
        if (cfg.HideSystemIcons != (HideIcons.IsChecked == true)) _c.SetHideSystemIcons(HideIcons.IsChecked == true);
        if (!_c.Sandbox) SetAutoStart(AutoStart.IsChecked == true);
        _c.ApplyTexts();
        _c.Refresh();
        if (_c.Tools?.ApplyHotkeys() is { Count: > 0 } bad)
        {
            SaveHint.Text = string.Join("；", bad.Select(kv => L.F("{0}快捷键{1}", ToolHub.Actions.First(a => a.Key == kv.Key).Name, kv.Value)));
            NavKeys.IsChecked = true;
            BuildKeys();
            return false;
        }
        if (langChanged && !_c.Sandbox &&
            MessageBox.Show(this, L.T("界面语言改好了，重启本工具后生效。现在重启吗？"), L.T("设置"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Dispatcher.BeginInvoke(() => ((App)Application.Current).Restart());
        return true;
    }

    // ---------------- 快捷键 ----------------
    readonly Dictionary<string, string> _keySpecs = new();

    static string Show(string spec) => spec.Length == 0 ? L.T("未设置") : spec == Interop.Hotkeys.DoubleCtrl ? L.T("连按两下 Ctrl") : spec.Replace("+", " + ");

    void BuildKeys()
    {
        KeyRows.Children.Clear();
        var tools = _c.Tools;
        foreach (var a in ToolHub.Actions)
        {
            var key = a.Key;
            if (!_keySpecs.ContainsKey(key)) _keySpecs[key] = tools?.HotkeyOf(key) ?? a.Default;
            var box = new TextBox
            {
                Width = 200, Height = 30, IsReadOnly = true, IsReadOnlyCaretVisible = false, Cursor = Cursors.Hand,
                Style = Res<Style>("WinTextBox"), TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Text = Show(_keySpecs[key]), ToolTip = L.T("点一下，然后按下新的组合键"),
            };
            var status = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var dbl = new CheckBox { Content = L.T("连按两下 Ctrl"), Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = key == "search" ? Visibility.Visible : Visibility.Collapsed, IsChecked = _keySpecs[key] == Interop.Hotkeys.DoubleCtrl };
            var reset = new Button { Content = L.T("恢复默认"), Style = Res<Style>("BtnW"), Margin = new Thickness(10, 0, 0, 0), Visibility = a.Default.Length == 0 ? Visibility.Collapsed : Visibility.Visible };

            void Set(string spec)
            {
                _keySpecs[key] = spec;
                box.Text = Show(spec);
                dbl.IsChecked = spec == Interop.Hotkeys.DoubleCtrl;
                box.IsEnabled = dbl.IsChecked != true;
                Check(key, spec, status);
            }

            box.GotKeyboardFocus += (_, _) => { tools?.SuspendHotkeys(); box.Text = L.T("请按下组合键…"); };
            box.LostKeyboardFocus += (_, _) => { box.Text = Show(_keySpecs[key]); tools?.ApplyHotkeys(); };
            box.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                var k = e.Key == Key.System ? e.SystemKey : e.Key;
                if (k is Key.Back or Key.Delete) { Set(""); Keyboard.ClearFocus(); return; }
                if (k == Key.Escape) { Keyboard.ClearFocus(); return; }
                if (k == Key.Tab) { e.Handled = false; return; }
                var spec = Interop.Hotkeys.Describe(Keyboard.Modifiers, k);
                if (spec == null)
                {
                    // 只按了修饰键：先显示着，等按下主键
                    if (Keyboard.Modifiers != ModifierKeys.None) box.Text = Show(Interop.Hotkeys.Describe(Keyboard.Modifiers, Key.F24)![..^4]) + " + …";
                    else { status.Text = L.T("单个按键会影响打字，请加上 Ctrl / Alt / Shift / Win"); status.Foreground = Brush("#E47600"); }
                    return;
                }
                Set(spec);
                Keyboard.ClearFocus();
            };
            dbl.Click += (_, _) => Set(dbl.IsChecked == true ? Interop.Hotkeys.DoubleCtrl : a.Default == Interop.Hotkeys.DoubleCtrl ? "" : a.Default);
            reset.Click += (_, _) => Set(a.Default);
            box.IsEnabled = dbl.IsChecked != true;
            Check(key, _keySpecs[key], status);

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
            row.Children.Add(new TextBlock { Text = a.Name, Width = 90, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            row.Children.Add(box);
            row.Children.Add(dbl);
            row.Children.Add(reset);
            row.Children.Add(status);
            KeyRows.Children.Add(new Border { BorderBrush = Brush("#F0F0F0"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8), Child = row });
        }
    }

    /// <summary>冲突检查：和本工具其他功能重复，或者被别的程序占用。</summary>
    void Check(string key, string spec, TextBlock status)
    {
        status.Text = "";
        if (spec.Length == 0) return;
        var dup = ToolHub.Actions.FirstOrDefault(a => a.Key != key && _keySpecs.GetValueOrDefault(a.Key) == spec);
        if (dup.Key != null) { status.Text = L.F("和“{0}”重复了", dup.Name); status.Foreground = Brush("#E5413C"); return; }
        var tools = _c.Tools;
        if (tools == null) return;
        if (tools.HotkeyErrors.TryGetValue(key, out var err) && spec == tools.HotkeyOf(key)) { status.Text = err; status.Foreground = Brush("#E5413C"); return; }
        // 本工具自己正在用的组合不算冲突
        bool ours = ToolHub.Actions.Any(a => tools.HotkeyOf(a.Key) == spec && !tools.HotkeyErrors.ContainsKey(a.Key));
        if (tools.Keys is { } hk && !ours && !hk.IsFree(spec)) { status.Text = L.T("已被别的程序占用，换一个"); status.Foreground = Brush("#E5413C"); return; }
        status.Text = L.T("✓ 可以用"); status.Foreground = Brush("#07C255");
    }

    async Task ShowIndexInfo()
    {
        IndexInfo.Text = L.T("文件查找：正在读取索引状态…");
        var r = await Search.IndexerHost.QueryAsync(new Search.QueryRequest { Q = "", Max = 0 }, CancellationToken.None);
        IndexInfo.Text = r == null ? L.T("文件查找：索引程序还没启动（第一次查找时会自动启动）")
            : !r.Ready ? L.T("文件查找：正在建立全盘索引…")
            : L.F("文件查找：已索引 {0:N0} 个文件，实时更新。索引程序单独运行，不需要管理员权限，不联网。", r.Indexed);
    }

    static List<string> Split(string s) =>
        s.Split(new[] { ',', '，', '、', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static bool IsAutoStart()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunName) is string;
    }

    static void SetAutoStart(bool on)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(RunName, false);
    }

    static T Res<T>(string key) => (T)Application.Current.FindResource(key);
    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
