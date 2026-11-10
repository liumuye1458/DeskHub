using System.Windows;
using System.Windows.Threading;
using DeskHub.Search;
using DeskHub.Shot;
using DeskHub.UI;

using DeskHub.Core;

namespace DeskHub;

/// <summary>
/// 工具条上的一个按钮（列表在 Edition.DockButtons）。
/// 内置功能只写键名，名字、图标、动作由 ToolHub 补上；本版本关掉的功能对应的键会自动跳过。
/// 自己加的按钮要写全：按钮字、图标（20×20 的线条路径）、说明、点了干什么。
/// </summary>
public sealed record DockButton(string Key, string? Label = null, string? Icon = null, string? Tip = null, Action<ToolHub>? Run = null);

/// <summary>
/// 小工具：文件查找、截图、计算器、密码箱，以及桌面工具条。
/// 可选功能各在一个文件里（编译开关见 Edition/Edition.props）：
///   ToolHub.Hotkeys.cs 快捷键 / ToolHub.Calendar.cs 日历、备忘录便签、行程提醒 / ToolHub.VaultApi.cs 密码箱对外接口 / ToolHub.LocalClaude.cs 本机 Claude 小服务
/// 窗口都是第一次用到时才创建，不用不占内存。
/// </summary>
public sealed partial class ToolHub : IDisposable
{
    /// <summary>内置功能：键名、名字（菜单、快捷键设置里用）、按钮字、图标、动作。</summary>
    public sealed record Builtin(string Key, string Name, string Label, string Icon, Action Run);

    readonly DeskController _c;
    readonly bool _sandbox;
    SearchWindow? _search;
    CalcWindow? _calc;
    DockWindow? _dock;
    VaultWindow? _vault;
    List<Builtin>? _builtins;

    public ToolHub(DeskController c, bool sandbox)
    {
        _c = c; _sandbox = sandbox;
        ShotSession.Ended += () => Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, App.TrimMemory);
    }

    /// <summary>本版本有的内置功能。</summary>
    public IReadOnlyList<Builtin> Builtins => _builtins ??= MakeBuiltins();

    List<Builtin> MakeBuiltins()
    {
        var l = new List<Builtin>
        {
            new("search", L.T("文件查找"), L.T("查找"), "M8.5,3 A5.5,5.5 0 1 1 8.49,3 Z M12.5,12.5 L17,17", Search),
            new("shot", L.T("截图"), L.T("截图"), "M5,1 V15 H19 M1,5 H15 V19", () => Shot()),
            new("calc", L.T("计算器"), L.T("计算器"), "M4,2 H16 V18 H4 Z M7,5.5 H13 M7,9.5 H7.6 M10,9.5 H10.6 M13,9.5 H13.6 M7,12.5 H7.6 M10,12.5 H10.6 M13,12.5 H13.6 M7,15.5 H7.6 M10,15.5 H10.6 M13,15.5 H13.6", Calc),
            new("vault", L.T("密码箱"), L.T("密码箱"), "M4,9 H16 V18 H4 Z M7,9 V6 A3,3 0 0 1 13,6 V9 M10,12.5 V14.5", Vault),
            new("arrange", L.T("排列桌面：点一下对齐你现在的摆法，再点一下（或双击）按推荐方案重排"), L.T("排列"), "M2,2 H8.5 V8.5 H2 Z M11.5,2 H18 V8.5 H11.5 Z M2,11.5 H8.5 V18 H2 Z M11.5,11.5 H18 V18 H11.5 Z", () => Arrange()),
            new("settings", L.T("设置"), L.T("设置"), "M10,7.4 A2.6,2.6 0 1 1 9.99,7.4 Z M10,2.5 V4.7 M10,15.3 V17.5 M2.5,10 H4.7 M15.3,10 H17.5 M4.7,4.7 L6.3,6.3 M13.7,13.7 L15.3,15.3 M4.7,15.3 L6.3,13.7 M13.7,6.3 L15.3,4.7", () => _c.OpenSettings("general")),
        };
        AddCalendarBuiltins(l);
        return l;
    }

    // 可选功能接进来的地方（对应文件不编译时，这些调用自动去掉）
    partial void AddCalendarBuiltins(List<Builtin> list);
    partial void StartCalendar();
    partial void StopCalendar();
    partial void AddDesktopWindows(List<Window> list);
    partial void StartVaultApi();
    partial void StopVaultApi();
    partial void SetupVault(VaultWindow w);
    partial void StartLocalClaude();
    partial void StopLocalClaude();
    partial void StartHotkeys();
    partial void StopHotkeys();

    public void Start()
    {
        _dock = new DockWindow();
        _dock.SetOptions(Edition.DockNoActivate, Edition.DockExcludeFromCapture);
        foreach (var b in Edition.DockButtons) AddDockButton(b);
        if (Edition.DockAcceptsShortcuts) AddUserApps();
        _dock.Moved += () => _c.SaveWindowPos("dock", _dock);
        _dock.StateChanged += SaveDockState;
        _dock.Show();
        if (!_c.RestoreWindowPos("dock", _dock)) DockDefault();
        if (_c.Cfg.DockCollapsed) _dock.RestoreCollapsed(new Point(_dock.Left, _dock.Top), _c.Cfg.DockRight, _c.Cfg.DockTabTop);

        StartCalendar();
        StartVaultApi();
        StartLocalClaude();

        if (_sandbox) return; // 沙盒测试不抢真实的快捷键
        StartHotkeys();
        // 开机后稍等一会儿再启动索引程序，不和开机抢资源
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        t.Tick += (_, _) => { t.Stop(); IndexerHost.EnsureStarted(); };
        t.Start();
    }

    void AddDockButton(DockButton b)
    {
        if (b.Run != null) { _dock!.Add(b.Key, b.Label ?? b.Key, b.Icon ?? "", b.Tip ?? b.Label ?? "", () => b.Run(this)); return; }
        var bi = Builtins.FirstOrDefault(x => x.Key == b.Key);
        if (bi == null) return; // 本版本没有这个功能
        _dock!.Add(bi.Key, b.Label ?? bi.Label, b.Icon ?? bi.Icon, b.Tip ?? bi.Name, bi.Run);
    }

    public void Search()
    {
        _search ??= new SearchWindow(_c);
        _search.Toggle();
    }

    /// <summary>截图。从菜单点进来时，等菜单消失再截，免得把菜单截进去。</summary>
    public void Shot(int delayMs = 0)
    {
        if (_search is { IsVisible: true }) _search.Hide();
        if (delayMs <= 0) { ShotSession.Start(); return; }
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        t.Tick += (_, _) => { t.Stop(); ShotSession.Start(); };
        t.Start();
    }

    public void Calc()
    {
        if (_calc == null)
        {
            _calc = new CalcWindow();
            _calc.Moved += () => _c.SaveWindowPos("calc", _calc);
            if (!_c.RestoreWindowPos("calc", _calc)) CalcDefault();
        }
        _calc.Toggle();
    }

    public void Vault()
    {
        if (_vault == null)
        {
            _vault = new VaultWindow();
            SetupVault(_vault);
            // 关窗口就锁上并释放，下次打开重新解锁
            _vault.Closed += (_, _) => { _vault = null; Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, App.TrimMemory); };
        }
        _vault.Popup();
    }

    public DockWindow? Dock => _dock;

    /// <summary>参与排列的便签：没有（指引便签是指着具体位置的，不跟着排列挪动）。</summary>
    public IEnumerable<(string, Window)> NoteEntries() => Enumerable.Empty<(string, Window)>();

    // ================= 排列 =================
    // 连着点：第 1 下对齐你现在的摆法，第 2 下按推荐方案重排，第 3 下恢复原样（提示框还在时算"连着点"）。
    ArrangeToast? _arrToast;
    Dictionary<string, Rect>? _arrBefore;
    int _arrStep;

    /// <summary>step=null 按连着点的规则走下一步；1 对齐；2 推荐排版。</summary>
    public void Arrange(int? step = null)
    {
        bool fresh = _arrToast == null || !_arrToast.IsVisible || _arrBefore == null;
        if (fresh) { _arrBefore = _c.CaptureLayout(); _arrStep = 0; }
        _arrStep = step ?? _arrStep % 3 + 1;
        var avoid = _dock is { IsVisible: true } ? new Rect(_dock.Left, _dock.Top, _dock.ActualWidth, _dock.ActualHeight) : Rect.Empty;
        string msg, next;
        switch (_arrStep)
        {
            case 1:
                _c.Arrange(false, _arrBefore!, avoid);
                msg = L.T("已对齐：边线对齐、间距统一、大小贴合图标");
                next = L.T("再点一次「排列」→ 按推荐方案重排");
                break;
            case 2:
                _c.Arrange(true, _arrBefore!, avoid);
                msg = L.T("已按推荐方案重排");
                next = L.T("再点一次「排列」→ 恢复原样");
                break;
            default:
                _c.RestoreLayout(_arrBefore!);
                msg = L.T("已恢复原样");
                next = L.T("再点一次「排列」→ 对齐");
                break;
        }
        _arrToast ??= new ArrangeToast(() =>
        {
            if (_arrBefore != null) _c.RestoreLayout(_arrBefore);
            _arrStep = 3;
        });
        _arrToast.Topmost = !_sandbox; // 沙盒测试不盖在使用者屏幕最上面
        _arrToast.Show(msg, next, canUndo: _arrStep != 3, near: avoid);
    }

    /// <summary>贴在桌面上的窗口（资源管理器重启后要重新贴）。</summary>
    public IEnumerable<Window> DesktopWindows()
    {
        var list = new List<Window>();
        if (_dock != null) list.Add(_dock);
        AddDesktopWindows(list);
        return list;
    }

    void SaveDockState()
    {
        if (_dock == null) return;
        _c.Cfg.DockCollapsed = _dock.Collapsed;
        _c.Cfg.DockRight = _dock.RightSide;
        _c.Cfg.DockTabTop = _dock.Collapsed ? _dock.Top : -1;
        _c.Cfg.Save();
    }

    /// <summary>挪工具条前先展开（位置按展开的样子算），挪完再收回去。</summary>
    void WithDockExpanded(Action move)
    {
        bool fold = _dock?.Collapsed == true;
        if (fold) _dock!.Expand(notify: false);
        move();
        if (fold) _dock!.Collapse();
    }

    void DockDefault()
    {
        if (_dock == null) return;
        var wa = SystemParameters.WorkArea;
        _dock.Left = wa.Right - _dock.ActualWidth - 24;
        _dock.Top = wa.Bottom - _dock.ActualHeight - 20;
    }

    void CalcDefault()
    {
        if (_calc == null) return;
        var wa = SystemParameters.WorkArea;
        _calc.Left = wa.Right - 380;
        _calc.Top = wa.Bottom - 600;
    }

    /// <summary>恢复默认布局时一起归位。</summary>
    public void DefaultPositions()
    {
        if (_dock?.Collapsed == true) { _dock.Expand(notify: false); SaveDockState(); }
        DockDefault(); CalcDefault();
    }

    /// <summary>换了一套布局（换显示器组合）：工具条、计算器、便签按这套布局摆，没有记录的放默认位置。</summary>
    public void Relayout()
    {
        WithDockExpanded(() => { if (_dock != null && !_c.RestoreWindowPos("dock", _dock)) DockDefault(); });
        if (_calc != null && !_c.RestoreWindowPos("calc", _calc)) CalcDefault();
    }

    /// <summary>屏幕变了（拔掉外接屏等），跑到屏幕外的挪回来。</summary>
    public void EnsureOnScreen(Func<Rect, bool> onScreen)
    {
        WithDockExpanded(() => { if (_dock != null && !onScreen(new Rect(_dock.Left, _dock.Top, _dock.ActualWidth, 50))) { _c.Cfg.Layout.Remove("dock"); DockDefault(); } });
        if (_calc != null && !onScreen(new Rect(_calc.Left, _calc.Top, _calc.ActualWidth, 50))) { _c.Cfg.Layout.Remove("calc"); CalcDefault(); }
    }

    public void Dispose()
    {
        StopHotkeys();
        ShotSession.End();
        _search?.Close();
        _calc?.Close();
        StopCalendar();
        _vault?.Close();
        Core.SecretClipboard.ClearNow();
        _arrToast?.Close();
        StopVaultApi();
        StopLocalClaude();
        _dock?.Close();
    }
}
