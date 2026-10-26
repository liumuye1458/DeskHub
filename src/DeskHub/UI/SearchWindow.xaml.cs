using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskHub.Core;
using DeskHub.Interop;
using DeskHub.Search;

namespace DeskHub.UI;

public sealed class SearchGroupVM
{
    public string Title { get; init; } = "";
}

public sealed class SearchRowVM : INotifyPropertyChanged
{
    public string Path { get; init; } = "";
    public bool Dir { get; init; }
    public string Where { get; init; } = "";
    public TextBlock NameBlock { get; init; } = new();

    string _size = "", _time = "";
    public string SizeText { get => _size; set { _size = value; Changed(nameof(SizeText)); } }
    public string TimeText { get => _time; set { _time = value; Changed(nameof(TimeText)); } }
    ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { _icon = value; Changed(nameof(Icon)); } }
    bool _sel;
    public bool Selected { get => _sel; set { _sel = value; Changed(nameof(Selected)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string n) => PropertyChanged?.Invoke(this, new(n));
}

public sealed class KindTabVM
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public string CountText => Count > 0 ? Count.ToString("N0") : "";
    public bool On { get; init; }
}

/// <summary>
/// 文件查找：交互照搬 360 搜索框（类型分组、最佳匹配在前、回车打开），去掉网络搜索、热搜和推广。
/// 全盘结果来自索引程序；索引程序没起来时只搜桌面。
/// </summary>
public partial class SearchWindow : Window
{
    readonly DeskController _c;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    CancellationTokenSource? _cts;
    string _kind = FileKinds.All;
    readonly List<SearchRowVM> _rows = new();
    int _sel = -1;
    Point? _down;

    public SearchWindow(DeskController c)
    {
        InitializeComponent();
        _c = c;
        Box.TextChanged += (_, _) =>
        {
            Placeholder.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _debounce.Stop(); _debounce.Start();
        };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await Run(); };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => Hide();

        Tabs.PreviewMouseLeftButtonUp += async (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is KindTabVM t && t.Key != _kind) { _kind = t.Key; await Run(); Box.Focus(); }
        };
        List.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var vm = RowOf(e.OriginalSource);
            if (vm == null) return;
            Select(_rows.IndexOf(vm));
            if (e.ClickCount == 2) { Open(vm, reveal: false); e.Handled = true; return; }
            _down = e.GetPosition(this);
        };
        List.PreviewMouseMove += (_, e) =>
        {
            // 拖出去：可以直接拖进微信、浏览器上传框、资源管理器，也能拖到任务栏、开始菜单固定
            if (_down is not { } p0 || e.LeftButton != MouseButtonState.Pressed || _sel < 0) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - p0.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - p0.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _down = null;
            Interop.ShellDrag.Start(this, new[] { _rows[_sel].Path }, null);
        };
        List.PreviewMouseLeftButtonUp += (_, _) => _down = null;
        List.PreviewMouseRightButtonUp += (_, e) =>
        {
            var vm = RowOf(e.OriginalSource);
            if (vm == null) return;
            Select(_rows.IndexOf(vm));
            ShellMenu.Show(this, new[] { vm.Path }, new List<ShellMenu.Own?>
            {
                new(L.T("打开所在位置"), () => { Hide(); ShellMenu.Reveal(vm.Path); }),
                new(L.T("复制路径"), () => Clipboard.SetText(vm.Path)),
                null,
            });
            e.Handled = true;
        };
    }

    // ---------------- 打开 / 收起 ----------------
    public void Toggle()
    {
        if (IsVisible && IsActive) { Hide(); return; }
        Popup();
    }

    public void Popup()
    {
        IndexerHost.EnsureStarted();
        Opacity = 0;
        Show();
        PlaceOnCursorScreen();
        Opacity = 1;
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Box.Focus();
        Box.SelectAll();
        if (Box.Text.Length > 0) _ = Run();
    }

    /// <summary>放在鼠标所在屏幕的上方居中（和 360 一样）。</summary>
    void PlaceOnCursorScreen()
    {
        var scr = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var h = new WindowInteropHelper(this).Handle;
        // 先挪到目标屏幕，DPI 跟着变了再按实际像素居中
        Native.SetWindowPos(h, IntPtr.Zero, scr.Left + 100, scr.Top + 100, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)(ActualWidth * dpi.DpiScaleX);
        Native.SetWindowPos(h, IntPtr.Zero, scr.Left + (scr.Width - w) / 2, scr.Top + (int)(scr.Height * 0.16), 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Escape: Hide(); e.Handled = true; break;
            case System.Windows.Input.Key.Down: Select(Math.Min(_rows.Count - 1, _sel + 1)); e.Handled = true; break;
            case System.Windows.Input.Key.Up: Select(Math.Max(0, _sel - 1)); e.Handled = true; break;
            case System.Windows.Input.Key.PageDown: Select(Math.Min(_rows.Count - 1, _sel + 8)); e.Handled = true; break;
            case System.Windows.Input.Key.PageUp: Select(Math.Max(0, _sel - 8)); e.Handled = true; break;
            case System.Windows.Input.Key.Enter:
                if (_sel >= 0) Open(_rows[_sel], reveal: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Tab:
                // Tab / Shift+Tab 切换类型分组
                var keys = FileKinds.Tabs.Select(t => t.Key).ToList();
                int i = keys.IndexOf(_kind) + ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                _kind = keys[(i + keys.Count) % keys.Count];
                _ = Run();
                e.Handled = true;
                break;
        }
    }

    void Open(SearchRowVM vm, bool reveal)
    {
        Hide();
        if (reveal) ShellMenu.Reveal(vm.Path);
        else ShellMenu.Open(vm.Path);
    }

    void Select(int i)
    {
        if (_sel >= 0 && _sel < _rows.Count) _rows[_sel].Selected = false;
        _sel = i;
        if (i < 0 || i >= _rows.Count) return;
        _rows[i].Selected = true;
        // 滚到可见位置
        if (List.ItemContainerGenerator.ContainerFromItem(_rows[i]) is FrameworkElement fe) fe.BringIntoView();
    }

    static SearchRowVM? RowOf(object src)
    {
        for (var d = src as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: SearchRowVM vm }) return vm;
        return null;
    }

    // ---------------- 查询 ----------------
    async Task Run()
    {
        var q = Box.Text.Trim();
        _cts?.Cancel();
        if (q.Length == 0) { Show(Array.Empty<object>(), null, ""); return; }
        var cts = _cts = new CancellationTokenSource();
        var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        QueryResult? res = null;
        try { res = await IndexerHost.QueryAsync(new QueryRequest { Q = q, Kind = _kind, Max = 200 }, cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;

        // 桌面上的文件单独从本程序里找（带分区、事项信息），排在最前面
        var desk = _c.Items.Where(i => terms.All(t => i.Name.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        var deskCounts = desk.GroupBy(i => FileKinds.Of(i.Path, i.IsDir)).ToDictionary(g => g.Key, g => g.Count());
        if (_kind != FileKinds.All) desk = desk.Where(i => FileKinds.Of(i.Path, i.IsDir) == _kind).ToList();
        desk = desk.OrderByDescending(i => i.Name.StartsWith(terms[0], StringComparison.OrdinalIgnoreCase)).ThenByDescending(i => i.LastTouch).ToList();

        var deskDirs = new[] { _c.Source.UserDesktop, _c.Source.PublicDesktop }.Select(d => d.TrimEnd('\\') + "\\").ToArray();
        var archive = _c.Cfg.ArchiveRoot.TrimEnd('\\') + "\\";
        bool OnDesk(string p) => deskDirs.Any(d => p.StartsWith(d, StringComparison.OrdinalIgnoreCase) && p.IndexOf('\\', d.Length) < 0);
        var other = res?.Items.Where(h => !OnDesk(h.Path)).ToList() ?? new();
        var archived = other.Where(h => h.Path.StartsWith(archive, StringComparison.OrdinalIgnoreCase)).ToList();
        other = other.Except(archived).ToList();

        var list = new List<object>();
        if (desk.Count > 0)
        {
            list.Add(new SearchGroupVM { Title = L.T("桌面") });
            foreach (var it in desk.Take(50)) list.Add(Row(it.Path, it.IsDir, DeskWhere(it), terms));
        }
        if (archived.Count > 0)
        {
            list.Add(new SearchGroupVM { Title = L.T("已归档") });
            foreach (var h in archived.Take(30)) list.Add(Row(h.Path, h.Dir, Path.GetDirectoryName(h.Path) + "\\", terms));
        }
        if (other.Count > 0)
        {
            list.Add(new SearchGroupVM { Title = desk.Count + archived.Count > 0 ? L.T("其他位置") : L.T("最佳匹配") });
            foreach (var h in other) list.Add(Row(h.Path, h.Dir, Path.GetDirectoryName(h.Path) + "\\", terms));
        }

        string status = res == null ? L.T("全盘索引还没启动，这次只搜了桌面")
            : !res.Ready ? L.T("正在建立全盘索引，第一次大约需要半分钟…")
            : L.F("已索引 {0:N0} 个文件 · {1}", res.Indexed, (res.Scanning ? L.T("正在核对变化") : L.T("实时更新")));
        Show(list, res?.Counts ?? deskCounts, status);
    }

    string DeskWhere(DesktopItem it)
    {
        var s = L.T("桌面 › ") + _c.FenceTitle(it);
        if (it.Zone == Zone.Active) s += " › " + it.Topic;
        return s;
    }

    SearchRowVM Row(string path, bool dir, string where, string[] terms)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0) name = path;
        if (!dir && Path.GetExtension(name).ToLowerInvariant() is ".lnk" or ".url") name = Path.GetFileNameWithoutExtension(name);
        return new SearchRowVM { Path = path, Dir = dir, Where = where, NameBlock = Highlight(name, terms) };
    }

    /// <summary>名字里命中的字标成绿色。</summary>
    static TextBlock Highlight(string name, string[] terms)
    {
        var mark = new bool[name.Length];
        foreach (var t in terms)
            for (int i = name.IndexOf(t, StringComparison.OrdinalIgnoreCase); i >= 0; i = name.IndexOf(t, i + Math.Max(1, t.Length), StringComparison.OrdinalIgnoreCase))
                for (int k = i; k < i + t.Length && k < mark.Length; k++) mark[k] = true;
        var tb = new TextBlock { FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)), TextTrimming = TextTrimming.CharacterEllipsis };
        int s = 0;
        while (s < name.Length)
        {
            int e = s;
            while (e < name.Length && mark[e] == mark[s]) e++;
            var run = new Run(name[s..e]);
            if (mark[s]) { run.Foreground = (Brush)Application.Current.FindResource("Green"); run.FontWeight = FontWeights.SemiBold; }
            tb.Inlines.Add(run);
            s = e;
        }
        return tb;
    }

    void Show(IList<object> list, Dictionary<string, int>? counts, string status)
    {
        _rows.Clear();
        _rows.AddRange(list.OfType<SearchRowVM>());
        List.ItemsSource = list;
        _sel = -1;
        if (_rows.Count > 0) Select(0);
        Scroll.ScrollToTop();

        bool any = Box.Text.Trim().Length > 0;
        TabsBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Scroll.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = any && _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = L.T("没有找到名字里包含这些字的文件");
        Foot.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Status.Text = status;
        Tabs.ItemsSource = FileKinds.Tabs
            .Select(t => new KindTabVM { Key = t.Key, Name = t.Name, Count = counts?.GetValueOrDefault(t.Key) ?? 0, On = t.Key == _kind })
            .Where(t => t.Key == FileKinds.All || t.On || t.Count > 0)
            .ToList();

        LoadDetails(_rows.ToList());
    }

    /// <summary>图标、大小、修改时间放到后台取，不卡输入。</summary>
    void LoadDetails(List<SearchRowVM> rows)
    {
        var px = (int)Math.Round(32 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        foreach (var r in rows.Take(80)) LoadIcon(r, px);
        Task.Run(() =>
        {
            var info = rows.Select(r =>
            {
                try
                {
                    if (r.Dir) return (r, "", Directory.GetLastWriteTime(r.Path));
                    var fi = new FileInfo(r.Path);
                    return (r, ItemVM.Size(fi.Length), fi.LastWriteTime);
                }
                catch { return (r, "", DateTime.MinValue); }
            }).ToList();
            Dispatcher.BeginInvoke(() =>
            {
                foreach (var (r, size, time) in info) { r.SizeText = size; r.TimeText = When(time); }
            });
        });
    }

    async void LoadIcon(SearchRowVM r, int px) => r.Icon = await ShellIcons.GetAsync(r.Path, DateTime.MinValue, px, false);

    static string When(DateTime t)
    {
        if (t == DateTime.MinValue || t.Year < 1990) return "";
        var now = DateTime.Now;
        if (t.Date == now.Date) return L.F("今天 {0:HH:mm}", t);
        if (t.Date == now.Date.AddDays(-1)) return L.F("昨天 {0:HH:mm}", t);
        return t.Year == now.Year ? t.ToString("MM-dd") : t.ToString("yyyy-MM-dd");
    }
}
