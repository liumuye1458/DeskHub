using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DeskHub.Core;
using DeskHub.Interop;

namespace DeskHub.UI;

/// <summary>分区窗口需要回调到总控的动作。</summary>
public interface IFenceHost
{
    void OpenItem(DesktopItem item);
    void ShowItemMenu(FenceWindow fence, IReadOnlyList<DesktopItem> items);
    void ShowFenceMenu(FenceWindow fence);
    void MoveToTopic(DesktopItem item, string topic, FenceWindow fence);
    void PlaceItem(DesktopItem item, FenceWindow fence, string? topic);
    /// <summary>一次拖了好几个过来：一起换分区（换事项时不弹“记住规则”）。</summary>
    void PlaceItems(IReadOnlyList<DesktopItem> items, FenceWindow fence, string? topic, bool sameFence);
    void DropFiles(string[] paths, FenceWindow fence, string? topic, bool forceCopy, bool forceMove);
    void LayoutChanged(FenceWindow fence);
    void FooterClicked();
    void DeleteItems(IReadOnlyList<DesktopItem> items);
    /// <summary>这个分区里开始选东西了：别的分区的选中清掉（和桌面一样，同一时间只有一处有选中）。</summary>
    void SelectionStarted(FenceWindow fence);
    Rect SnapRect(FenceWindow fence, Rect r, bool resize);
}

public partial class FenceWindow : Window
{
    public const string AllTab = "全部";
    const string InternalFormat = "DeskHub.Item";
    const string FromFormat = "DeskHub.From";
    const double IconSize = 42; // 和 FenceWindow.xaml 里图标的宽高一致

    public string Key { get; }
    public Zone Zone => Key switch { "apps" => Zone.Apps, "folders" => Zone.Folders, "today" => Zone.Today, "cold" => Zone.Cold, "active" => Zone.Active, _ => Zone.Custom };
    readonly IFenceHost _host;
    readonly ObservableCollection<ItemVM> _items = new();
    List<DesktopItem> _all = new();
    ItemVM? _anchor; // Shift 连选的起点

    public string CurrentTab { get; private set; } = AllTab;
    string _chip = L.T("全部");

    public FenceWindow(string key, string title, string hint, IFenceHost host)
    {
        InitializeComponent();
        Key = key; _host = host;
        TitleText.Text = title;
        HintText.Text = hint;
        Items.ItemsSource = _items;

        // 拖标题栏移动：自己处理（不用系统拖动），这样能边拖边吸附对齐
        Header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 1 || e.OriginalSource is DependencyObject d && IsInside(d, MenuBtn)) return;
            _moveFrom = Cursor(); _moveStart = new Point(Left, Top);
            Header.CaptureMouse();
            e.Handled = true;
        };
        Header.MouseMove += (_, _) =>
        {
            if (_moveFrom is not { } from || !Header.IsMouseCaptured) return;
            var c = Cursor();
            var raw = new Rect(_moveStart.X + c.X - from.X, _moveStart.Y + c.Y - from.Y, ActualWidth, ActualHeight);
            var r = _host.SnapRect(this, raw, false);
            Left = r.X; Top = r.Y;
        };
        Header.MouseLeftButtonUp += (_, _) => EndMove();
        Header.LostMouseCapture += (_, _) => EndMove();
        MenuBtn.Click += (_, _) => _host.ShowFenceMenu(this);
        FooterLink.MouseLeftButtonUp += (_, _) => _host.FooterClicked();
        Grip.DragStarted += (_, _) => { _sizeFrom = Cursor(); _sizeStart = new Size(Width, Height); };
        Grip.DragDelta += OnGrip;
        Grip.DragCompleted += (_, _) => _host.LayoutChanged(this);
        SourceInitialized += (_, _) => DesktopHost.Pin(this);

        // 点选 / Ctrl、Shift 多选 / 空白处拖框多选 / 拖走选中的
        Scroll.PreviewMouseLeftButtonDown += OnDown;
        Scroll.PreviewMouseMove += OnMove;
        Scroll.PreviewMouseLeftButtonUp += OnUp;
        Scroll.LostMouseCapture += (_, _) => EndBand();
        Scroll.PreviewMouseRightButtonUp += OnItemRight;
        KeyDown += OnKey;

        Tabs.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (Hit<TabVM>(e.OriginalSource) is { } t) { CurrentTab = t.Name; Render(); }
        };
        Chips.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (Hit<TabVM>(e.OriginalSource) is { } c) { _chip = c.Name; Render(); }
        };

        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    // ---------------- 外观配置 ----------------
    public bool ShowTabs { get; set; }
    public bool ShowChips { get; set; }
    public bool ShowNewDot { get; set; }
    public bool ShowDaysBadge { get; set; }
    public bool Cold { set { Root.Background = (Brush)FindResource(value ? "FenceBgCold" : "FenceBg"); Root.BorderThickness = new Thickness(1); if (value) Root.BorderBrush = DashedBorder(); } }
    public string EmptyHint { get; set; } = "";
    public IReadOnlyList<TopicRule> Topics { get; set; } = Array.Empty<TopicRule>();
    public Func<IEnumerable<DesktopItem>, IEnumerable<DesktopItem>> Sort { get; set; } = x => x;

    public void SetFooter(string? text)
    {
        Footer.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        FooterText.Text = text ?? "";
    }

    static Brush DashedBorder()
    {
        // 冷却区用虚线框，和普通分区区分开
        var dg = new DrawingBrush { Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.Tile };
        dg.Drawing = new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255)), null,
            new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 4, 4)), new RectangleGeometry(new Rect(4, 4, 4, 4)) } });
        dg.Freeze();
        return dg;
    }

    // ---------------- 数据 ----------------
    /// <summary>排列用：一共多少个图标；图标区以外占的宽、高（边框、标题、标签页、筛选）。</summary>
    public int ItemCount => _all.Count;
    public double ChromeW => Math.Max(0, ActualWidth - Scroll.ActualWidth);
    public double ChromeH => Math.Max(0, ActualHeight - Scroll.ActualHeight);

    public void SetItems(List<DesktopItem> items)
    {
        _all = items;
        Render();
    }

    void Render()
    {
        var sel = _items.Where(v => v.Selected).Select(v => v.Item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anchor = _anchor?.Item.Path;
        IEnumerable<DesktopItem> view = _all;

        if (ShowTabs)
        {
            var tabs = new List<TabVM> { new() { Name = AllTab, Count = _all.Count } };
            foreach (var t in Topics.Select(t => t.Name).Append(AppConfig.OtherTopic))
                tabs.Add(new TabVM { Name = t, Count = _all.Count(i => i.Topic == t) });
            // 没有文件的事项不显示标签（当前选中的除外），保持干净
            tabs = tabs.Where(t => t.Name == AllTab || t.Count > 0 || t.Name == CurrentTab).ToList();
            if (tabs.All(t => t.Name != CurrentTab)) CurrentTab = AllTab;
            foreach (var t in tabs) t.On = t.Name == CurrentTab;
            Tabs.ItemsSource = tabs;
            Tabs.Visibility = Visibility.Visible;
            if (CurrentTab != AllTab) view = view.Where(i => i.Topic == CurrentTab);
        }
        if (ShowChips)
        {
            var groups = new (string Name, Func<DesktopItem, bool> F)[]
            {
                (L.T("全部"), _ => true),
                (L.T("文档"), i => i.Kind is Kind.Doc or Kind.Pdf or Kind.Slide),
                (L.T("表格"), i => i.Kind == Kind.Sheet),
                (L.T("图片"), i => i.Kind == Kind.Image),
                (L.T("压缩包"), i => i.Kind == Kind.Archive),
                (L.T("其他"), i => i.Kind is Kind.Video or Kind.Audio or Kind.Program or Kind.Other),
            };
            var present = groups.Where(g => g.Name == L.T("全部") || g.Name == _chip || view.Any(g.F)).ToList();
            Chips.ItemsSource = present.Select(g => new TabVM { Name = g.Name, On = g.Name == _chip }).ToList();
            Chips.Visibility = present.Count > 2 ? Visibility.Visible : Visibility.Collapsed;
            var f = groups.First(g => g.Name == _chip).F;
            view = view.Where(f);
        }

        var list = Sort(view).ToList();
        CountText.Text = _all.Count.ToString();
        _items.Clear();
        _anchor = null;
        foreach (var it in list)
        {
            var vm = new ItemVM(it, ShowNewDot, ShowDaysBadge);
            if (sel.Contains(it.Path)) vm.Selected = true;
            if (it.Path == anchor) _anchor = vm;
            _items.Add(vm);
            LoadIcon(vm);
        }
        EmptyText.Text = EmptyHint;
        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void LoadIcon(ItemVM vm)
    {
        var px = (int)Math.Round(IconSize * VisualTreeHelper.GetDpi(this).DpiScaleX); // 按屏幕实际像素取，1:1 显示
        bool thumb = vm.Item.Kind is Kind.Image or Kind.Video;
        vm.Icon = await ShellIcons.GetAsync(vm.Item.IconRef ?? vm.Item.Path, vm.Item.LastWrite, px, thumb);
    }

    // ---------------- 选择 / 打开 / 右键 ----------------
    // 和资源管理器一样：单击选一个；Ctrl+单击加选/取消；Shift+单击连选；空白处按住拖出框来框选（按住 Ctrl 是加选）；
    // Ctrl+A 全选；选中好几个以后拖动、删除、右键都是对这一批。
    Point? _dragStart;
    ItemVM? _downItem, _pendingOnly;
    Point? _bandFrom;
    HashSet<ItemVM> _bandBase = new();

    /// <summary>现在选中的（按显示顺序）。</summary>
    public IReadOnlyList<DesktopItem> SelectedItems => _items.Where(v => v.Selected).Select(v => v.Item).ToList();

    public void ClearSelection()
    {
        foreach (var v in _items) if (v.Selected) v.Selected = false;
        _anchor = null;
    }

    void SelectOnly(ItemVM? vm)
    {
        foreach (var v in _items) if (v.Selected && v != vm) v.Selected = false;
        if (vm != null) vm.Selected = true;
        _anchor = vm;
    }

    void SelectRange(ItemVM from, ItemVM to, bool add)
    {
        int a = _items.IndexOf(from), b = _items.IndexOf(to);
        if (a < 0) a = b;
        if (a > b) (a, b) = (b, a);
        for (int i = 0; i < _items.Count; i++)
        {
            bool inRange = i >= a && i <= b;
            if (inRange) _items[i].Selected = true;
            else if (!add && _items[i].Selected) _items[i].Selected = false;
        }
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (Hit<ScrollBar>(e.OriginalSource) != null) return; // 点的是滚动条
        Activate();
        Focus();
        _host.SelectionStarted(this);
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0;
        var vm = Hit<ItemVM>(e.OriginalSource);
        e.Handled = true;

        if (vm == null)
        {
            // 空白处：开始拖框（不按 Ctrl 先清掉原来的选中）
            if (!ctrl) ClearSelection();
            _bandFrom = e.GetPosition(Items);
            _bandBase = _items.Where(v => v.Selected).ToHashSet();
            Scroll.CaptureMouse();
            return;
        }
        if (e.ClickCount == 2 && !ctrl && !shift) { _dragStart = null; _host.OpenItem(vm.Item); return; }

        ClickItem(vm, ctrl, shift);
        if (!vm.Selected) return;
        _dragStart = e.GetPosition(this);
        _downItem = vm;
    }

    /// <summary>按下鼠标时的选择规则。</summary>
    void ClickItem(ItemVM vm, bool ctrl, bool shift)
    {
        _pendingOnly = null;
        if (shift) SelectRange(_anchor ?? vm, vm, add: ctrl);
        else if (ctrl) { vm.Selected = !vm.Selected; _anchor = vm; }
        else if (!vm.Selected) SelectOnly(vm);
        else _pendingOnly = vm; // 点在已选中的上面：可能要拖走这一批，松手时没拖才改成只选它
    }

    // 测试用：模拟点击 / 松手 / 拖框（坐标是图标区里的位置）
    public void TestClick(int index, bool ctrl, bool shift) { _host.SelectionStarted(this); ClickItem(_items[index], ctrl, shift); _dragStart = new Point(); OnUp(this, null!); }
    public void TestBand(Rect r, bool ctrl)
    {
        _host.SelectionStarted(this);
        if (!ctrl) ClearSelection();
        _bandBase = _items.Where(v => v.Selected).ToHashSet();
        _bandFrom = r.TopLeft;
        UpdateBand(r.TopLeft, r.BottomRight);
    }
    public void TestEndBand() => EndBand();
    public void TestDragTo(FenceWindow target) => _host.PlaceItems(SelectedItems, target, null, target == this);

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_bandFrom is { } from)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { EndBand(); return; }
            UpdateBand(from, e.GetPosition(Items));
            AutoScroll(e.GetPosition(Scroll).Y);
            return;
        }
        if (_dragStart is not { } p0 || _downItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - p0.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - p0.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null; _pendingOnly = null;
        var items = SelectedItems;
        if (items.Count == 0) return;
        // 同时带上"文件"格式：可以直接拖进微信、企业微信、浏览器上传框、资源管理器
        var data = new DataObject();
        var files = new System.Collections.Specialized.StringCollection();
        foreach (var it in items) if (!it.IsSystem) files.Add(it.Path);
        if (files.Count > 0) data.SetFileDropList(files);
        data.SetData(InternalFormat, string.Join("\n", items.Select(i => i.Path)));
        data.SetData(FromFormat, Key);
        DragDrop.DoDragDrop(Items, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    void OnUp(object sender, MouseButtonEventArgs? e)
    {
        if (_bandFrom != null) { EndBand(); if (e != null) e.Handled = true; return; }
        if (_pendingOnly != null && _dragStart != null) SelectOnly(_pendingOnly);
        _pendingOnly = null;
        _dragStart = null;
    }

    void UpdateBand(Point a, Point b)
    {
        var r = new Rect(a, b);
        Band.Visibility = r.Width + r.Height > 3 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(Band, r.X); Canvas.SetTop(Band, r.Y);
        Band.Width = r.Width; Band.Height = r.Height;
        foreach (var v in _items)
        {
            if (Items.ItemContainerGenerator.ContainerFromItem(v) is not FrameworkElement c || !c.IsVisible) continue;
            var box = c.TransformToAncestor(Items).TransformBounds(new Rect(c.RenderSize));
            box.Inflate(-6, -6); // 碰到图标格子的边缘不算，要框进去一点
            bool hit = r.IntersectsWith(box);
            bool want = hit || _bandBase.Contains(v);
            if (v.Selected != want) v.Selected = want;
        }
    }

    /// <summary>拖框拖到分区上下边外面：自动滚动。</summary>
    void AutoScroll(double y)
    {
        if (y < 0) Scroll.ScrollToVerticalOffset(Scroll.VerticalOffset + Math.Max(-30, y / 2));
        else if (y > Scroll.ActualHeight) Scroll.ScrollToVerticalOffset(Scroll.VerticalOffset + Math.Min(30, (y - Scroll.ActualHeight) / 2));
    }

    void EndBand()
    {
        if (_bandFrom == null) return;
        _bandFrom = null;
        Band.Visibility = Visibility.Collapsed;
        _anchor = _items.FirstOrDefault(v => v.Selected);
        if (Scroll.IsMouseCaptured) Scroll.ReleaseMouseCapture();
    }

    void OnItemRight(object sender, MouseButtonEventArgs e)
    {
        var vm = Hit<ItemVM>(e.OriginalSource);
        if (vm == null) return;
        _host.SelectionStarted(this);
        if (!vm.Selected) SelectOnly(vm); // 右键没选中的：只选它；右键选中的：对整批
        _host.ShowItemMenu(this, SelectedItems);
        e.Handled = true;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _host.SelectionStarted(this);
            foreach (var v in _items) v.Selected = true;
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.Escape) { ClearSelection(); return; }
        var sel = SelectedItems;
        if (sel.Count == 0) return;
        if (e.Key == System.Windows.Input.Key.Enter) { foreach (var it in sel.Take(20)) _host.OpenItem(it); }
        else if (e.Key == System.Windows.Input.Key.Delete) _host.DeleteItems(sel);
        else if (e.Key == System.Windows.Input.Key.Apps) _host.ShowItemMenu(this, sel);
    }

    // ---------------- 拖进来 ----------------
    string? DropTopic(DragEventArgs e)
    {
        if (!ShowTabs) return null;
        // 拖到某个事项标签上 → 归那个事项；拖到内容区 → 归当前标签（"全部"则不指定）
        if (e.OriginalSource is DependencyObject d && FindTag(d) is string tab && IsTopicName(tab)) return tab == AllTab ? null : tab;
        return CurrentTab == AllTab ? null : CurrentTab;
    }

    bool IsTopicName(string s) => s == AllTab || s == AppConfig.OtherTopic || Topics.Any(t => t.Name == s);

    static string? FindTag(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is Border { Tag: string s } && s.Length > 0) return s;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(InternalFormat))
        {
            // 拖到别的分区：换分区；在"进行中"里拖到某个事项标签：换事项
            bool otherFence = e.Data.GetData(FromFormat) as string != Key;
            e.Effects = otherFence || DropTopic(e) != null ? DragDropEffects.Move : DragDropEffects.None;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            e.Effects = ctrl ? DragDropEffects.Copy : DragDropEffects.Move;
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        var topic = DropTopic(e);
        if (e.Data.GetData(InternalFormat) is string joined)
        {
            var items = joined.Split('\n').Select(p => _all.FirstOrDefault(i => i.Path == p) ?? (_host as DeskController)?.Find(p)).OfType<DesktopItem>().ToList();
            if (items.Count == 0) return;
            bool same = e.Data.GetData(FromFormat) as string == Key;
            if (items.Count > 1) { if (!same || topic != null) _host.PlaceItems(items, this, topic, same); return; }
            if (!same) _host.PlaceItem(items[0], this, topic);
            else if (topic != null) _host.MoveToTopic(items[0], topic, this);
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
            _host.DropFiles(files, this, topic, ctrl, shift);
        }
    }

    // ---------------- 改大小 ----------------
    Point? _moveFrom;
    Point _moveStart, _sizeFrom;
    Size _sizeStart;

    /// <summary>鼠标位置，和 Left / Top 同一套坐标。</summary>
    Point Cursor() { var p = Mouse.GetPosition(this); return new Point(Left + p.X, Top + p.Y); }

    void EndMove()
    {
        if (_moveFrom == null) return;
        _moveFrom = null;
        if (Header.IsMouseCaptured) Header.ReleaseMouseCapture();
        _host.LayoutChanged(this);
    }

    static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (var x = d; x != null; x = x is Visual ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x == parent) return true;
        return false;
    }

    void OnGrip(object sender, DragDeltaEventArgs e)
    {
        // 按鼠标实际走了多远算（不累加每次的小变化），吸附后还能顺畅拉开
        var c = Cursor();
        var raw = new Rect(Left, Top, Math.Max(200, _sizeStart.Width + c.X - _sizeFrom.X), Math.Max(120, _sizeStart.Height + c.Y - _sizeFrom.Y));
        var r = _host.SnapRect(this, raw, true);
        Width = r.Width; Height = r.Height;
    }

    static T? Hit<T>(object src) where T : class
    {
        var d = src as DependencyObject;
        while (d != null)
        {
            if (d is FrameworkElement { DataContext: T t }) return t;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
