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
    void ShowItemMenu(FenceWindow fence, DesktopItem item);
    void ShowFenceMenu(FenceWindow fence);
    void MoveToTopic(DesktopItem item, string topic, FenceWindow fence);
    void PlaceItem(DesktopItem item, FenceWindow fence, string? topic);
    void DropFiles(string[] paths, FenceWindow fence, string? topic, bool forceCopy, bool forceMove);
    void LayoutChanged(FenceWindow fence);
    void FooterClicked();
    void DeleteItem(DesktopItem item);
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
    ItemVM? _selected;

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

        Items.PreviewMouseLeftButtonDown += OnItemDown;
        Items.PreviewMouseMove += OnItemMove;
        Items.MouseLeftButtonUp += (_, _) => _dragStart = null;
        Items.PreviewMouseRightButtonUp += OnItemRight;
        Scroll.MouseLeftButtonDown += (_, _) => Select(null);
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
        var sel = _selected?.Item.Path;
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
        _selected = null;
        foreach (var it in list)
        {
            var vm = new ItemVM(it, ShowNewDot, ShowDaysBadge);
            if (it.Path == sel) { vm.Selected = true; _selected = vm; }
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
        vm.Icon = await ShellIcons.GetAsync(vm.Item.Path, vm.Item.LastWrite, px, thumb);
    }

    // ---------------- 选择 / 打开 / 右键 ----------------
    Point? _dragStart;
    ItemVM? _downItem;

    void OnItemDown(object sender, MouseButtonEventArgs e)
    {
        var vm = Hit<ItemVM>(e.OriginalSource);
        if (vm == null) return;
        Select(vm);
        Activate();
        Focus();
        if (e.ClickCount == 2) { _dragStart = null; _host.OpenItem(vm.Item); e.Handled = true; return; }
        _dragStart = e.GetPosition(this);
        _downItem = vm;
    }

    void OnItemMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } p0 || _downItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - p0.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - p0.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        var path = _downItem.Item.Path;
        // 同时带上"文件"格式：可以直接拖进微信、企业微信、浏览器上传框、资源管理器
        var data = new DataObject();
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
        data.SetData(InternalFormat, path);
        data.SetData(FromFormat, Key);
        DragDrop.DoDragDrop(Items, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    void OnItemRight(object sender, MouseButtonEventArgs e)
    {
        var vm = Hit<ItemVM>(e.OriginalSource);
        if (vm == null) return;
        Select(vm);
        _host.ShowItemMenu(this, vm.Item);
        e.Handled = true;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_selected == null) return;
        if (e.Key == System.Windows.Input.Key.Enter) _host.OpenItem(_selected.Item);
        else if (e.Key == System.Windows.Input.Key.Delete) _host.DeleteItem(_selected.Item);
        else if (e.Key == System.Windows.Input.Key.Apps) _host.ShowItemMenu(this, _selected.Item);
    }

    void Select(ItemVM? vm)
    {
        if (_selected != null) _selected.Selected = false;
        _selected = vm;
        if (vm != null) vm.Selected = true;
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
        if (e.Data.GetData(InternalFormat) is string path)
        {
            var item = _all.FirstOrDefault(i => i.Path == path) ?? (_host as DeskController)?.Find(path);
            if (item == null) return;
            if (e.Data.GetData(FromFormat) as string != Key) _host.PlaceItem(item, this, topic);
            else if (topic != null) _host.MoveToTopic(item, topic, this);
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
