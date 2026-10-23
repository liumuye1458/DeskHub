using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>
/// 密码箱：左边分组、中间条目、右边详情。
/// 打开先解锁；闲置 5 分钟、Windows 锁屏、关窗口都会锁上（内存里的明文数据随之丢弃）。
/// </summary>
public partial class VaultWindow : Window
{
    Vault? _v;
    string _group = All;           // 当前分组：All / Recent / Weak / 分组名
    VaultEntry? _sel;
    VaultEntry? _draft;            // 正在编辑的副本（null = 查看模式）
    bool _draftIsNew;
    readonly HashSet<string> _revealed = new(); // 当前显示明文的字段
    readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMinutes(5) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

    const string All = "\u0001all", Recent = "\u0001recent", Weak = "\u0001weak";
    static readonly StringComparer NameOrder = StringComparer.Create(new CultureInfo("zh-CN"), true);
    static readonly string[] AvatarColors = { "#111111", "#1677ff", "#c7000b", "#4a5568", "#ea4335", "#ff5a1f", "#2b78e4", "#07a548", "#9b59b6", "#e47600" };

    public VaultWindow()
    {
        InitializeComponent();
        UnlockBtn.Click += async (_, _) => await UnlockOrCreate();
        Pw1.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { if (Pw2.Visibility == Visibility.Visible && Pw2.Password.Length == 0) Pw2.Focus(); else await UnlockOrCreate(); } };
        Pw2.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await UnlockOrCreate(); };
        RestoreLink.MouseLeftButtonUp += async (_, _) => await RestoreBackup();

        LockBtn.Click += (_, _) => Lock();
        AddBtn.Click += (_, _) => StartEdit(null);
        GenBtn.Click += (_, _) => PasswordGenDialog.Show(this, null);
        ImportBtn.Click += (_, _) => SmartImport();
        MoreBtn.Click += (_, _) => ShowMore();
        SearchBox.TextChanged += (_, _) => { SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; BuildList(); };
        SearchBox.KeyDown += (_, e) => { if (e.Key == Key.Escape) SearchBox.Text = ""; };

        _idle.Tick += (_, _) => Lock();
        PreviewMouseMove += (_, _) => Touch();
        PreviewKeyDown += (_, e) =>
        {
            Touch();
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _v != null) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Closed += (_, _) => { Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch; Lock(); };
        ShowLock();
    }

    void OnSessionSwitch(object? s, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect)
            Dispatcher.BeginInvoke(Lock);
    }

    void Touch() { if (_v != null) { _idle.Stop(); _idle.Start(); } }

    // ================= 对外接口用 =================
    /// <summary>解锁了就是打开的密码箱，锁着是 null。</summary>
    internal Vault? Current => _v;
    public event Action? Unlocked;
    public Func<bool>? ApiOn;
    public Action<bool>? SetApiOn;
    public string ApiDoc = "";

    /// <summary>锁着时有程序来要密码：在解锁界面上说明是谁在要。</summary>
    public void SetLockNotice(string text)
    {
        if (_v != null) return;
        LockSub.Text = text;
    }

    public void Popup()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (_v == null) Pw1.Focus();
    }

    // ================= 锁定 / 解锁 =================
    void ShowLock()
    {
        bool setup = !Vault.Exists;
        LockTitle.Text = setup ? L.T("设置主密码") : L.T("密码箱已锁定");
        LockSub.Text = setup
            ? L.T("第一次使用，先设一个主密码（至少 8 位）。\n主密码只在你脑子里，本机不保存，忘了就无法找回。")
            : L.T("输入主密码解锁");
        Pw2.Visibility = Pw2Label.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
        UnlockBtn.Content = setup ? L.T("创建密码箱") : L.T("解锁");
        RestoreLink.Visibility = Visibility.Visible;
        Pw1.Password = Pw2.Password = "";
        LockErr.Text = "";
        LockPane.Visibility = Visibility.Visible;
        MainPane.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Pw1.Focus());
    }

    async Task UnlockOrCreate()
    {
        if (!UnlockBtn.IsEnabled) return;
        var pw = Pw1.Password;
        bool setup = !Vault.Exists;
        if (setup)
        {
            if (pw.Length < 8) { LockErr.Text = L.T("主密码至少 8 位"); return; }
            if (pw != Pw2.Password) { LockErr.Text = L.T("两次输入的不一样"); return; }
        }
        else if (pw.Length == 0) return;

        UnlockBtn.IsEnabled = false;
        LockErr.Foreground = Brush("#999");
        LockErr.Text = setup ? L.T("正在创建…") : L.T("正在解锁…");
        try
        {
            _v = await Task.Run(() => setup ? Vault.Create(pw) : Vault.Open(pw));
        }
        catch (Exception ex) { _v = null; LockErr.Foreground = Brush("#E5413C"); LockErr.Text = L.T("打不开密码箱文件：") + ex.Message; UnlockBtn.IsEnabled = true; return; }
        UnlockBtn.IsEnabled = true;
        LockErr.Foreground = Brush("#E5413C");
        if (_v == null) { LockErr.Text = L.T("主密码不对"); Pw1.SelectAll(); Pw1.Focus(); return; }
        Pw1.Password = Pw2.Password = "";
        ShowMain();
        Unlocked?.Invoke();
        if (setup && MessageBox.Show(this, L.T("密码箱已创建。\n\n建议现在导出一份加密备份（存到 U 盘或网盘）。备份同样要主密码才能打开；电脑坏了可以用它恢复。\n\n现在导出吗？"),
                L.T("密码箱"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            ExportBackup();
    }

    void ShowMain()
    {
        LockPane.Visibility = Visibility.Collapsed;
        MainPane.Visibility = Visibility.Visible;
        _group = All; _sel = null; _draft = null;
        SearchBox.Text = "";
        BuildAll();
        Touch();
    }

    public void Lock()
    {
        _idle.Stop();
        if (_v == null) return;
        _v.Dispose();
        _v = null;
        _sel = _draft = null;
        _revealed.Clear();
        EntryList.Children.Clear(); GroupList.Children.Clear(); Detail.Children.Clear();
        ShowLock();
    }

    // ================= 分组 =================
    void BuildAll() { BuildGroups(); BuildList(); BuildDetail(); }

    void BuildGroups()
    {
        if (_v == null) return;
        GroupList.Children.Clear();
        var es = _v.Data.Entries;
        AddGroup(All, L.T("全部"), es.Count);
        var names = _v.Data.Groups.Concat(es.Select(e => e.Group)).Where(g => g.Length > 0).Distinct().ToList();
        foreach (var g in names) AddGroup(g, g, es.Count(e => e.Group == g));
        if (es.Any(e => e.Group.Length == 0)) AddGroup("", L.T("未分组"), es.Count(e => e.Group.Length == 0));
        var add = new TextBlock { Text = L.T("＋ 新建分组"), Foreground = Brush("#07C255"), Margin = new Thickness(12, 8, 0, 4), Cursor = Cursors.Hand };
        add.MouseLeftButtonUp += (_, _) =>
        {
            var n = InputDialog.Ask(this, L.T("新建分组"), "")?.Trim();
            if (string.IsNullOrEmpty(n) || _v.Data.Groups.Contains(n)) return;
            _v.Data.Groups.Add(n); _v.Save(); BuildGroups();
        };
        GroupList.Children.Add(add);
        GroupList.Children.Add(new TextBlock { Text = L.T("快捷"), Foreground = Brush("#AAA"), FontSize = 12, Margin = new Thickness(12, 14, 0, 4) });
        AddGroup(Recent, L.T("最近使用"), -1);
        AddGroup(Weak, L.T("弱密码"), es.Count(_v.IsWeak), warn: true);
    }

    void AddGroup(string key, string text, int count, bool warn = false)
    {
        bool on = _group == key;
        var dock = new DockPanel();
        if (count >= 0)
        {
            var c = new TextBlock { Text = count.ToString(), Foreground = warn && count > 0 ? Brush("#E47600") : Brush("#AAA"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(c, Dock.Right);
            dock.Children.Add(c);
        }
        dock.Children.Add(new TextBlock { Text = text, Foreground = on ? Brush("#07C255") : Brush("#333"), FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        var b = new Border { Child = dock, Padding = new Thickness(12, 8, 10, 8), CornerRadius = new CornerRadius(5), Background = on ? Brush("#E9F8EF") : Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 2) };
        b.MouseEnter += (_, _) => { if (_group != key) b.Background = Brush("#ECEEF0"); };
        b.MouseLeave += (_, _) => { if (_group != key) b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += (_, _) => { _group = key; BuildGroups(); BuildList(); };
        if (!key.StartsWith('\u0001') && key.Length > 0)
        {
            var m = new ContextMenu();
            var rn = new MenuItem { Header = L.T("分组改名…") };
            rn.Click += (_, _) => RenameGroup(key);
            var del = new MenuItem { Header = L.T("删除分组（记录变为未分组）") };
            del.Click += (_, _) => DeleteGroup(key);
            m.Items.Add(rn); m.Items.Add(del);
            b.ContextMenu = m;
        }
        GroupList.Children.Add(b);
    }

    void RenameGroup(string g)
    {
        var n = InputDialog.Ask(this, L.T("分组改名"), g)?.Trim();
        if (string.IsNullOrEmpty(n) || n == g || _v == null) return;
        foreach (var e in _v.Data.Entries.Where(e => e.Group == g)) e.Group = n;
        var i = _v.Data.Groups.IndexOf(g);
        if (i >= 0) _v.Data.Groups[i] = n; else _v.Data.Groups.Add(n);
        _v.Data.Groups = _v.Data.Groups.Distinct().ToList();
        if (_group == g) _group = n;
        _v.Save(); BuildAll();
    }

    void DeleteGroup(string g)
    {
        if (_v == null) return;
        foreach (var e in _v.Data.Entries.Where(e => e.Group == g)) e.Group = "";
        _v.Data.Groups.Remove(g);
        if (_group == g) _group = All;
        _v.Save(); BuildAll();
    }

    // ================= 条目列表 =================
    IEnumerable<VaultEntry> Filtered()
    {
        if (_v == null) return Enumerable.Empty<VaultEntry>();
        IEnumerable<VaultEntry> es = _v.Data.Entries;
        var q = SearchBox.Text.Trim();
        if (q.Length > 0)
        {
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            es = es.Where(e => words.All(w => $"{e.Name} {e.Url} {e.Account} {e.Group} {e.Note}".Contains(w, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            es = _group switch
            {
                All => es,
                Recent => es.Where(e => e.LastUsed != null),
                Weak => es.Where(_v.IsWeak),
                var g => es.Where(e => e.Group == g),
            };
        }
        return _group == Recent && q.Length == 0 ? es.OrderByDescending(e => e.LastUsed).Take(20) : es.OrderBy(e => e.Name, NameOrder);
    }

    void BuildList()
    {
        EntryList.Children.Clear();
        var list = Filtered().ToList();
        foreach (var e in list) EntryList.Children.Add(EntryRow(e));
        ListEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListEmpty.Text = _v?.Data.Entries.Count == 0 ? L.T("还没有记录\n点右上角“＋ 新增”，或“导入”浏览器里存的密码") : L.T("没有符合的记录");
    }

    Border EntryRow(VaultEntry e)
    {
        bool on = e == _sel;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Avatar(e.Name, 34));
        var txt = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        txt.Children.Add(new TextBlock { Text = e.Name, FontWeight = FontWeights.SemiBold, Foreground = Brush("#222"), TextTrimming = TextTrimming.CharacterEllipsis });
        txt.Children.Add(new TextBlock { Text = e.Account.Length > 0 ? e.Account : e.Url, Foreground = Brush("#999"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(txt, 1);
        grid.Children.Add(txt);
        var b = new Border { Child = grid, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(6), Background = on ? Brush("#E9F8EF") : Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 2) };
        b.MouseEnter += (_, _) => { if (_sel != e) b.Background = Brush("#F5F6F7"); };
        b.MouseLeave += (_, _) => { if (_sel != e) b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += (_, _) => Select(e);
        return b;
    }

    void Select(VaultEntry e)
    {
        if (_draft != null && !ConfirmDiscard()) return;
        _draft = null;
        _sel = e;
        _revealed.Clear();
        BuildList();
        BuildDetail();
    }

    bool ConfirmDiscard() =>
        MessageBox.Show(this, L.T("正在编辑的内容还没保存，放弃吗？"), L.T("密码箱"), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    static Border Avatar(string name, double size)
    {
        var ch = string.IsNullOrWhiteSpace(name) ? "?" : char.ToUpperInvariant(name.Trim()[0]).ToString();
        int h = 0; foreach (var c in name) h = h * 31 + c;
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 4),
            Background = Brush(AvatarColors[Math.Abs(h % AvatarColors.Length)]), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = ch, Foreground = Brushes.White, FontSize = size * 0.42, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    // ================= 详情 =================
    void BuildDetail()
    {
        Detail.Children.Clear();
        if (_draft != null) { BuildEditor(); return; }
        if (_sel == null)
        {
            Detail.Children.Add(new TextBlock { Text = _v?.Data.Entries.Count > 0 ? L.T("选择左边的一条记录查看") : "", Foreground = Brush("#BBB"), Margin = new Thickness(0, 120, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            return;
        }
        var e = _sel;

        // 头部
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var edit = new Button { Content = L.T("编辑"), Style = Res<Style>("BtnW"), Margin = new Thickness(0, 0, 6, 0) };
        edit.Click += (_, _) => StartEdit(e);
        var del = new Button { Content = L.T("删除"), Style = Res<Style>("BtnW") };
        del.Click += (_, _) => DeleteEntry(e);
        btns.Children.Add(edit); btns.Children.Add(del);
        DockPanel.SetDock(btns, Dock.Right);
        head.Children.Add(btns);
        var av = Avatar(e.Name, 48);
        head.Children.Add(av);
        var ht = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        ht.Children.Add(new TextBlock { Text = e.Name, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brush("#222"), TextTrimming = TextTrimming.CharacterEllipsis });
        ht.Children.Add(new TextBlock { Text = e.Group.Length > 0 ? e.Group : L.T("未分组"), Foreground = Brush("#999"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0) });
        head.Children.Add(ht);
        Detail.Children.Add(head);

        if (e.Url.Length > 0)
            Field(L.T("网址"), e.Url, false, link: true, ops: new[] { (L.T("打开"), (Action)(() => OpenUrl(e))), (L.T("复制"), () => CopyValue(e, e.Url, false, L.T("网址"))) });
        if (e.Account.Length > 0)
            Field(L.T("账号"), e.Account, false, ops: new[] { (L.T("复制"), (Action)(() => CopyValue(e, e.Account, false, L.T("账号")))) }, primary: L.T("复制"));
        if (e.Password.Length > 0)
            SecretField(L.T("密码"), "pw", e.Password, e, strength: true);
        for (int i = 0; i < e.Fields.Count; i++)
        {
            var f = e.Fields[i];
            if (f.Secret) SecretField(f.Name, "f" + i, f.Value, e, strength: false);
            else Field(f.Name, f.Value, false, ops: new[] { (L.T("复制"), (Action)(() => CopyValue(e, f.Value, false, f.Name))) });
        }
        if (e.Note.Length > 0) Field(L.T("备注"), e.Note, false, ops: Array.Empty<(string, Action)>(), wrap: true);
        if (_v!.IsWeak(e))
            Detail.Children.Add(new TextBlock { Text = Vault.Rate(e.Password) == Vault.Strength.Weak ? L.T("⚠ 这个密码太简单，建议换一个") : L.T("⚠ 这个密码和别的记录重复，建议换一个"), Foreground = Brush("#E47600"), FontSize = 12, Margin = new Thickness(0, 10, 0, 0) });

        Detail.Children.Add(new TextBlock
        {
            Text = L.F("创建 {0:yyyy-MM-dd}　·　最后修改 {1:yyyy-MM-dd HH:mm}", e.Created, e.Modified) + (e.LastUsed is { } lu ? L.F("　·　最近使用 {0:yyyy-MM-dd HH:mm}", lu) : ""),
            Foreground = Brush("#BBB"), FontSize = 12, Margin = new Thickness(0, 18, 0, 0),
        });
    }

    void Field(string key, string value, bool mono, (string Text, Action Run)[] ops, bool link = false, bool wrap = false, string? primary = null, UIElement? extra = null)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = key, Foreground = Brush("#999"), VerticalAlignment = wrap ? VerticalAlignment.Top : VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var vp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var tb = new TextBlock
        {
            Text = value, Foreground = link ? Brush("#07A548") : Brush("#222"), FontSize = 14,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            FontFamily = mono ? new FontFamily("Consolas, Microsoft YaHei UI") : FontFamily, LineHeight = wrap ? 22 : double.NaN,
        };
        if (wrap) { Grid.SetColumn(tb, 1); g.Children.Add(tb); }
        else
        {
            vp.Children.Add(tb);
            if (extra != null) vp.Children.Add(extra);
            Grid.SetColumn(vp, 1); g.Children.Add(vp);
        }
        var op = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (t, run) in ops)
        {
            var l = new TextBlock { Text = t, Foreground = t == primary ? Brush("#07C255") : Brush("#666"), FontWeight = t == primary ? FontWeights.SemiBold : FontWeights.Normal, Cursor = Cursors.Hand, Margin = new Thickness(14, 0, 0, 0) };
            l.MouseLeftButtonUp += (_, _) => run();
            op.Children.Add(l);
        }
        Grid.SetColumn(op, 2); g.Children.Add(op);
        Detail.Children.Add(new Border { Child = g, BorderBrush = Brush("#F0F0F0"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 12, 0, 12) });
    }

    void SecretField(string key, string id, string value, VaultEntry e, bool strength)
    {
        bool shown = _revealed.Contains(id);
        UIElement? chip = null;
        if (strength)
        {
            var r = Vault.Rate(value);
            var (t, c) = r switch { Vault.Strength.Strong => (L.T("强"), "#07C255"), Vault.Strength.Medium => (L.T("中"), "#E6A700"), _ => (L.T("弱"), "#E5413C") };
            chip = new Border { Background = Brush(c), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = t, Foreground = Brushes.White, FontSize = 11 } };
        }
        Field(key, shown ? value : new string('•', Math.Clamp(value.Length, 8, 16)), shown,
            ops: new[]
            {
                (shown ? L.T("隐藏") : L.T("显示"), (Action)(() => { if (!_revealed.Add(id)) _revealed.Remove(id); BuildDetail(); })),
                (L.T("复制"), () => CopyValue(e, value, true, key)),
            }, primary: L.T("复制"), extra: chip);
    }

    void CopyValue(VaultEntry e, string value, bool secret, string what)
    {
        try { SecretClipboard.Copy(value, secret); }
        catch { ShowToast(L.T("剪贴板被别的程序占用，请再点一次")); return; }
        e.LastUsed = DateTime.Now;
        _v?.Save();
        ShowToast(secret ? L.F("✓ {0}已复制，30 秒后自动清除", what) : L.F("✓ {0}已复制", what));
    }

    void OpenUrl(VaultEntry e)
    {
        var u = e.Url.Contains("://") ? e.Url : "https://" + e.Url;
        try { Interop.ShellMenu.LetOpenedWindowToFront(); Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); e.LastUsed = DateTime.Now; _v?.Save(); }
        catch (Exception ex) { ShowToast(L.T("打不开：") + ex.Message); }
    }

    void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop(); _toastTimer.Start();
    }

    void DeleteEntry(VaultEntry e)
    {
        if (MessageBox.Show(this, L.F("删除“{0}”？删除后无法恢复。", e.Name), L.T("密码箱"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _v!.Data.Entries.Remove(e);
        _v.Save();
        _sel = null;
        BuildAll();
    }

    // ================= 编辑 =================
    void StartEdit(VaultEntry? e)
    {
        if (_v == null) return;
        if (_draft != null && !ConfirmDiscard()) return;
        _draftIsNew = e == null;
        _draft = e?.Clone() ?? new VaultEntry { Group = _group.StartsWith('\u0001') ? "" : _group };
        if (e == null) _sel = null;
        BuildList();
        BuildDetail();
    }

    void BuildEditor()
    {
        var d = _draft!;
        Detail.Children.Add(new TextBlock { Text = _draftIsNew ? L.T("新增记录") : L.T("编辑记录"), FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brush("#222"), Margin = new Thickness(0, 0, 0, 14) });

        TextBox Box(string label, string value, Action<string> set, bool mono = false, bool multi = false)
        {
            var tb = new TextBox
            {
                Text = value, Style = Res<Style>("WinTextBox"), MinHeight = multi ? 80 : 32, VerticalContentAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center,
                AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
                FontFamily = mono ? new FontFamily("Consolas, Microsoft YaHei UI") : FontFamily, FontSize = mono ? 14 : 13,
            };
            tb.TextChanged += (_, _) => set(tb.Text);
            Row(label, tb);
            return tb;
        }

        var name = Box(L.T("服务名 *"), d.Name, v => d.Name = v);
        var group = new ComboBox { IsEditable = true, Height = 32, VerticalContentAlignment = VerticalAlignment.Center, Text = d.Group };
        foreach (var g in _v!.Data.Groups) group.Items.Add(g);
        group.Text = d.Group;
        group.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => d.Group = group.Text.Trim()));
        group.SelectionChanged += (_, _) => { if (group.SelectedItem is string s) d.Group = s; };
        Row(L.T("分组"), group);
        Box(L.T("网址"), d.Url, v => d.Url = v.Trim());
        Box(L.T("账号"), d.Account, v => d.Account = v);

        // 密码 + 生成 + 强度
        var pw = new TextBox { Text = d.Password, Style = Res<Style>("WinTextBox"), Height = 32, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 14 };
        var str = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Width = 36 };
        void Rate()
        {
            var r = Vault.Rate(pw.Text);
            (str.Text, str.Foreground) = r switch { Vault.Strength.Strong => (L.T("强"), Brush("#07C255")), Vault.Strength.Medium => (L.T("中"), Brush("#E6A700")), Vault.Strength.Weak => (L.T("弱"), Brush("#E5413C")), _ => ("", Brushes.Gray) };
        }
        pw.TextChanged += (_, _) => { d.Password = pw.Text; Rate(); };
        Rate();
        var gen = new Button { Content = L.T("生成"), Style = Res<Style>("BtnW"), Margin = new Thickness(8, 0, 0, 0) };
        gen.Click += (_, _) => { if (PasswordGenDialog.Show(this, pw.Text) is { } p) pw.Text = p; };
        var pwRow = new DockPanel();
        DockPanel.SetDock(gen, Dock.Right); DockPanel.SetDock(str, Dock.Right);
        pwRow.Children.Add(gen); pwRow.Children.Add(str); pwRow.Children.Add(pw);
        Row(L.T("密码"), pwRow);

        // 自定义字段
        foreach (var f in d.Fields.ToList())
        {
            var fn = new TextBox { Text = f.Name, Style = Res<Style>("WinTextBox"), Width = 110, Height = 32, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = L.T("字段名") };
            fn.TextChanged += (_, _) => f.Name = fn.Text.Trim();
            var fv = new TextBox { Text = f.Value, Style = Res<Style>("WinTextBox"), Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
            fv.TextChanged += (_, _) => f.Value = fv.Text;
            var sec = new CheckBox { Content = L.T("隐藏"), IsChecked = f.Secret, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = L.T("显示成圆点，复制后 30 秒自动清除") };
            sec.Click += (_, _) => f.Secret = sec.IsChecked == true;
            var rm = new TextBlock { Text = "✕", Foreground = Brush("#BBB"), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), ToolTip = L.T("删掉这个字段") };
            rm.MouseLeftButtonUp += (_, _) => { d.Fields.Remove(f); Detail.Children.Clear(); BuildEditor(); };
            var row = new DockPanel();
            DockPanel.SetDock(fn, Dock.Left); DockPanel.SetDock(rm, Dock.Right); DockPanel.SetDock(sec, Dock.Right);
            row.Children.Add(fn); row.Children.Add(rm); row.Children.Add(sec); row.Children.Add(fv);
            Row(L.T("自定义"), row);
        }
        var addField = new TextBlock { Text = L.T("＋ 添加自定义字段（如支付密码、安全问题、客服电话）"), Foreground = Brush("#07C255"), Cursor = Cursors.Hand, Margin = new Thickness(90, 2, 0, 10), FontSize = 12.5 };
        addField.MouseLeftButtonUp += (_, _) => { d.Fields.Add(new VaultField { Name = L.T("支付密码"), Secret = true }); Detail.Children.Clear(); BuildEditor(); };
        Detail.Children.Add(addField);

        Box(L.T("备注"), d.Note, v => d.Note = v, multi: true);

        var err = new TextBlock { Foreground = Brush("#E5413C"), Margin = new Thickness(90, 6, 0, 0) };
        Detail.Children.Add(err);
        var save = new Button { Content = L.T("保存"), Style = Res<Style>("BtnG"), MinWidth = 90 };
        var cancel = new Button { Content = L.T("取消"), Style = Res<Style>("BtnW"), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) =>
        {
            if (d.Name.Trim().Length == 0) { err.Text = L.T("服务名不能为空"); name.Focus(); return; }
            d.Name = d.Name.Trim();
            d.Fields.RemoveAll(f => f.Name.Length == 0 && f.Value.Length == 0);
            d.Modified = DateTime.Now;
            if (_draftIsNew) { _v!.Data.Entries.Add(d); _sel = d; }
            else
            {
                var i = _v!.Data.Entries.FindIndex(x => x.Id == d.Id);
                if (i >= 0) _v.Data.Entries[i] = d; else _v.Data.Entries.Add(d);
                _sel = d;
            }
            if (d.Group.Length > 0 && !_v.Data.Groups.Contains(d.Group)) _v.Data.Groups.Add(d.Group);
            _v.Save();
            _draft = null;
            _revealed.Clear();
            BuildAll();
            ShowToast(L.T("✓ 已保存（已加密）"));
        };
        cancel.Click += (_, _) => { _draft = null; BuildList(); BuildDetail(); };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(90, 10, 0, 0) };
        bar.Children.Add(save); bar.Children.Add(cancel);
        Detail.Children.Add(bar);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (_draftIsNew) name.Focus(); });
    }

    void Row(string label, FrameworkElement input)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = label, Foreground = Brush("#666"), VerticalAlignment = input is TextBox { AcceptsReturn: true } ? VerticalAlignment.Top : VerticalAlignment.Center, Margin = new Thickness(0, input is TextBox { AcceptsReturn: true } ? 7 : 0, 0, 0) });
        Grid.SetColumn(input, 1);
        g.Children.Add(input);
        Detail.Children.Add(g);
    }

    // ================= 导入 / 备份 / 改主密码 =================
    void ShowMore()
    {
        var m = new ContextMenu { PlacementTarget = MoreBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string t, Action a) { var mi = new MenuItem { Header = t }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
        Add(L.T("导入浏览器导出的密码（CSV）…"), ImportCsv);
        Add(L.T("修改主密码…"), ChangePassword);
        Add(L.T("导出加密备份…"), ExportBackup);
        Add(L.T("从加密备份恢复…"), async () => await RestoreBackup());
        m.Items.Add(new Separator());
        Add(L.T("对外接口（让其他程序取密码）…"), () =>
        {
            if (_v == null) return;
            VaultApiPanel.Show(this, _v.Data, ApiOn?.Invoke() == true, on => SetApiOn?.Invoke(on), () => _v?.Save(), ApiDoc);
            Touch();
        });
        m.IsOpen = true;
    }

    /// <summary>智能识别：粘贴文字 / 打开文档，自动拆成记录。</summary>
    void SmartImport()
    {
        if (_v == null) return;
        _idle.Stop(); // 识别窗口开着的时候不自动锁
        var w = new SmartImportWindow(this, _v.Data);
        var ok = w.ShowDialog() == true;
        Touch();
        if (!ok || w.Added == 0 || _v == null) return;
        _v.Save();
        _group = All; _sel = null;
        SearchBox.Text = "";
        BuildAll();
        ShowToast(L.F("✓ 已识别保存 {0} 条（已加密）", w.Added));
    }

    internal SmartImportWindow TestSmart() { var w = new SmartImportWindow(this, _v!.Data) { NoAsk = true }; w.Show(); return w; }
    internal void TestRefresh() { _v!.Save(); BuildAll(); }
    /// <summary>接口取用后（最近使用时间变了）刷新一下界面。</summary>
    internal void Refresh() { if (_v != null && MainPane.Visibility == Visibility.Visible) { BuildGroups(); BuildList(); } }

    void ImportCsv()
    {
        if (_v == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("选择浏览器导出的密码文件"), Filter = L.T("密码文件 (*.csv)|*.csv") };
        if (dlg.ShowDialog(this) != true) return;
        List<VaultEntry> list;
        try { list = Vault.ParseCsv(File.ReadAllText(dlg.FileName), L.T("导入")); }
        catch (Exception ex) { MessageBox.Show(this, L.T("导入失败：") + ex.Message, L.T("密码箱")); return; }
        int added = 0, dup = 0;
        foreach (var e in list)
        {
            if (_v.Data.Entries.Any(x => x.Url == e.Url && x.Account == e.Account && x.Password == e.Password)) { dup++; continue; }
            _v.Data.Entries.Add(e); added++;
        }
        if (added > 0 && !_v.Data.Groups.Contains(L.T("导入"))) _v.Data.Groups.Add(L.T("导入"));
        _v.Save();
        _group = added > 0 ? L.T("导入") : _group;
        BuildAll();
        var msg = L.F("导入了 {0} 条", added) + (dup > 0 ? L.F("，跳过 {0} 条重复的", dup) : "") + L.T("，放在“导入”分组里。\n\n这个 CSV 文件里的密码是明文，任何人都能看到。要把它删掉（放进回收站）吗？");
        if (MessageBox.Show(this, msg, L.T("密码箱"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(dlg.FileName, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
            catch (Exception ex) { MessageBox.Show(this, L.T("删除失败：") + ex.Message, L.T("密码箱")); }
        }
    }

    void ExportBackup()
    {
        if (_v == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = L.T("导出加密备份"), FileName = L.F("密码箱备份_{0:yyyyMMdd}.dhvault", DateTime.Now), Filter = L.T("密码箱备份 (*.dhvault)|*.dhvault") };
        if (dlg.ShowDialog(this) != true) return;
        try { _v.Backup(dlg.FileName); ShowToast(L.T("✓ 加密备份已导出")); }
        catch (Exception ex) { MessageBox.Show(this, L.T("导出失败：") + ex.Message, L.T("密码箱")); }
    }

    async Task RestoreBackup()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("选择加密备份"), Filter = L.T("密码箱备份 (*.dhvault)|*.dhvault|所有文件|*.*") };
        if (dlg.ShowDialog(this) != true) return;
        if (Vault.Exists && MessageBox.Show(this, L.T("恢复会用备份里的内容替换现在的密码箱（现在的数据会被覆盖）。继续吗？"), L.T("密码箱"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var pw = PasswordDialog.Ask(this, L.T("从备份恢复"), L.T("输入这份备份的主密码"), new[] { L.T("主密码") })?[0];
        if (string.IsNullOrEmpty(pw)) return;
        Vault? v;
        try { v = await Task.Run(() => Vault.Restore(dlg.FileName, pw)); }
        catch (Exception ex) { MessageBox.Show(this, L.T("恢复失败：") + ex.Message, L.T("密码箱")); return; }
        if (v == null) { MessageBox.Show(this, L.T("主密码不对，打不开这份备份。"), L.T("密码箱")); return; }
        _v?.Dispose();
        _v = v;
        ShowMain();
        ShowToast(L.F("✓ 已从备份恢复 {0} 条记录", v.Data.Entries.Count));
    }

    async void ChangePassword()
    {
        if (_v == null) return;
        var r = PasswordDialog.Ask(this, L.T("修改主密码"), L.T("新主密码至少 8 位。改完以后，旧的备份仍然用旧主密码打开，建议重新导出一份备份。"), new[] { L.T("现在的主密码"), L.T("新主密码"), L.T("再输一次新主密码") });
        if (r == null) return;
        if (r[1].Length < 8) { MessageBox.Show(this, L.T("新主密码至少 8 位"), L.T("密码箱")); return; }
        if (r[1] != r[2]) { MessageBox.Show(this, L.T("两次输入的新主密码不一样"), L.T("密码箱")); return; }
        var check = await Task.Run(() => Vault.Open(r[0]));
        if (check == null) { MessageBox.Show(this, L.T("现在的主密码不对"), L.T("密码箱")); return; }
        check.Dispose();
        var v = _v;
        await Task.Run(() => v.ChangePassword(r[1]));
        ShowToast(L.T("✓ 主密码已修改"));
    }

    // ---------------- 自动测试用（只在沙盒脚本里调用） ----------------
    internal async Task<string> TestUnlock(string pw)
    {
        Pw1.Password = pw; Pw2.Password = pw;
        await UnlockOrCreate();
        return _v != null ? "unlocked" : "locked: " + LockErr.Text;
    }

    internal void TestAdd(VaultEntry e) { _v!.Data.Entries.Add(e); _v.Save(); _sel = e; BuildAll(); }
    internal void TestSelect(int i, bool reveal) { _sel = Filtered().ElementAt(i); _revealed.Clear(); if (reveal) _revealed.Add("pw"); BuildAll(); }
    internal void TestEdit() => StartEdit(_sel);
    internal int TestCount => _v?.Data.Entries.Count ?? -1;

    static T Res<T>(string key) => (T)Application.Current.FindResource(key);
    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}

/// <summary>输入一个或几个密码的小对话框。</summary>
static class PasswordDialog
{
    public static string[]? Ask(Window owner, string title, string hint, string[] labels)
    {
        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
        panel.Children.Add(new TextBlock { Text = hint, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), MaxWidth = 340 });
        var boxes = new List<PasswordBox>();
        foreach (var l in labels)
        {
            panel.Children.Add(new TextBlock { Text = l, Margin = new Thickness(0, 6, 0, 4) });
            var b = new PasswordBox { Height = 32, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0), FontSize = 14 };
            boxes.Add(b); panel.Children.Add(b);
        }
        var ok = new Button { Content = L.T("确定"), Style = (Style)Application.Current.FindResource("BtnG"), IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = L.T("取消"), Style = (Style)Application.Current.FindResource("BtnW"), IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        bar.Children.Add(ok); bar.Children.Add(cancel);
        panel.Children.Add(bar);
        var w = new Window
        {
            Title = title, Content = panel, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = owner.FontFamily, FontSize = 13, MinWidth = 380,
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => boxes[0].Focus();
        return w.ShowDialog() == true ? boxes.Select(b => b.Password).ToArray() : null;
    }
}

/// <summary>密码生成器：长度、数字、符号可选；可以复制，也可以直接填进正在编辑的记录。</summary>
static class PasswordGenDialog
{
    /// <summary>current != null 表示从编辑界面打开，会多一个“使用这个”按钮，返回选中的密码。</summary>
    public static string? Show(Window owner, string? current)
    {
        var len = new Slider { Minimum = 8, Maximum = 40, Value = 16, Width = 220, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        var lenText = new TextBlock { Width = 40, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var digits = new CheckBox { Content = L.T("数字"), IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
        var upper = new CheckBox { Content = L.T("大写字母"), IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
        var symbols = new CheckBox { Content = L.T("符号 !@#$"), IsChecked = true };
        var result = new TextBox { FontFamily = new FontFamily("Consolas"), FontSize = 18, IsReadOnly = true, Height = 40, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 14, 0, 0) };
        void Gen() { lenText.Text = L.F("{0} 位", (int)len.Value); result.Text = Vault.Generate((int)len.Value, digits.IsChecked == true, symbols.IsChecked == true, upper.IsChecked == true); }
        len.ValueChanged += (_, _) => Gen();
        foreach (var c in new[] { digits, upper, symbols }) c.Click += (_, _) => Gen();
        Gen();

        var again = new Button { Content = L.T("换一个"), Style = (Style)Application.Current.FindResource("BtnW"), MinWidth = 80 };
        var copy = new Button { Content = L.T("复制"), Style = (Style)Application.Current.FindResource(current == null ? "BtnG" : "BtnW"), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        var use = new Button { Content = L.T("使用这个"), Style = (Style)Application.Current.FindResource("BtnG"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Visibility = current == null ? Visibility.Collapsed : Visibility.Visible, IsDefault = true };
        var tip = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x07, 0xC2, 0x55)), VerticalAlignment = VerticalAlignment.Center };
        var row1 = new StackPanel { Orientation = Orientation.Horizontal };
        row1.Children.Add(new TextBlock { Text = L.T("长度"), Width = 40, VerticalAlignment = VerticalAlignment.Center }); row1.Children.Add(len); row1.Children.Add(lenText);
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(40, 10, 0, 0) };
        row2.Children.Add(digits); row2.Children.Add(upper); row2.Children.Add(symbols);
        var bar = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(again); btns.Children.Add(copy); btns.Children.Add(use);
        DockPanel.SetDock(btns, Dock.Right);
        bar.Children.Add(btns); bar.Children.Add(tip);
        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 16), Width = 400 };
        panel.Children.Add(row1); panel.Children.Add(row2); panel.Children.Add(result); panel.Children.Add(bar);
        panel.Children.Add(new TextBlock { Text = L.T("已去掉容易看错的字符（0 O o 1 l I）"), Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) });

        var w = new Window
        {
            Title = L.T("密码生成器"), Content = panel, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = owner.FontFamily, FontSize = 13,
        };
        again.Click += (_, _) => Gen();
        copy.Click += (_, _) => { try { SecretClipboard.Copy(result.Text, true); tip.Text = L.T("✓ 已复制，30 秒后清除"); } catch { } };
        use.Click += (_, _) => w.DialogResult = true;
        return w.ShowDialog() == true ? result.Text : null;
    }
}
