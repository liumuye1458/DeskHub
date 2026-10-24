using System.Windows;
using System.Windows.Threading;
using DeskHub.Interop;
using DeskHub.Search;
using DeskHub.Shot;
using DeskHub.UI;

using DeskHub.Core;

namespace DeskHub;

/// <summary>
/// 小工具：文件查找、截图、计算器、备忘录、密码箱，以及它们的快捷键和桌面工具条。
/// 窗口都是第一次用到时才创建，不用不占内存。
/// </summary>
public sealed class ToolHub : IDisposable
{
    /// <summary>可以设快捷键的功能：配置里的键名、显示名、默认快捷键。</summary>
    public static readonly (string Key, string Name, string Default)[] Actions =
    {
        ("shot", L.T("截图"), "Ctrl+Alt+A"),          // 沿用 360 的设置
        ("search", L.T("文件查找"), Hotkeys.DoubleCtrl),
        ("calc", L.T("计算器"), ""),
        ("memo", L.T("备忘录"), ""),
        ("calendar", L.T("日历"), ""),
        ("vault", L.T("密码箱"), ""),
    };

    /// <summary>功能键名 → 工具条上的按钮文字。</summary>
    static string DockLabel(string key) => key switch { "shot" => L.T("截图"), "search" => L.T("查找"), "calc" => L.T("计算器"), "memo" => L.T("备忘录"), "calendar" => L.T("日历"), _ => L.T("密码箱") };

    readonly DeskController _c;
    readonly bool _sandbox;
    Hotkeys? _keys;
    SearchWindow? _search;
    CalcWindow? _calc;
    DockWindow? _dock;
    GuideManager? _memo;
    VaultWindow? _vault;
    readonly Dictionary<string, GuideWindow> _guides = new();
    readonly DispatcherTimer _remind = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };

    public Dictionary<string, string> HotkeyErrors { get; private set; } = new();
    public Hotkeys? Keys => _keys;

    public ToolHub(DeskController c, bool sandbox)
    {
        _c = c; _sandbox = sandbox;
        ShotSession.Ended += () => Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, App.TrimMemory);
    }

    public string HotkeyOf(string key) =>
        _c.Cfg.Hotkeys.TryGetValue(key, out var v) ? v : Actions.First(a => a.Key == key).Default;

    public void Start()
    {
        _dock = new DockWindow();
        _dock.Add(L.T("查找"), "M8.5,3 A5.5,5.5 0 1 1 8.49,3 Z M12.5,12.5 L17,17", L.T("文件查找"), Search);
        _dock.Add(L.T("截图"), "M5,1 V15 H19 M1,5 H15 V19", L.T("截图"), () => Shot());
        _dock.Add(L.T("计算器"), "M4,2 H16 V18 H4 Z M7,5.5 H13 M7,9.5 H7.6 M10,9.5 H10.6 M13,9.5 H13.6 M7,12.5 H7.6 M10,12.5 H10.6 M13,12.5 H13.6 M7,15.5 H7.6 M10,15.5 H10.6 M13,15.5 H13.6", L.T("计算器"), Calc);
        _dock.Add(L.T("备忘录"), "M5,2 H15 V18 H5 Z M8,6.5 H12.5 M8,10 H12.5 M8,13.5 H11", L.T("备忘录"), Memo);
        _dock.Add(L.T("日历"), "M3,4 H17 V17 H3 Z M3,8 H17 M7,2 V5.5 M13,2 V5.5 M6.5,11.5 H7.5 M9.5,11.5 H10.5 M12.5,11.5 H13.5 M6.5,14.5 H7.5 M9.5,14.5 H10.5", L.T("日历"), () => Calendar());
        _dock.Add(L.T("密码箱"), "M4,9 H16 V18 H4 Z M7,9 V6 A3,3 0 0 1 13,6 V9 M10,12.5 V14.5", L.T("密码箱"), Vault);
        _dock.Add(L.T("排列"), "M2,2 H8.5 V8.5 H2 Z M11.5,2 H18 V8.5 H11.5 Z M2,11.5 H8.5 V18 H2 Z M11.5,11.5 H18 V18 H11.5 Z", L.T("排列桌面：点一下对齐你现在的摆法，再点一下（或双击）按推荐方案重排"), () => Arrange());
        _dock.Add(L.T("设置"), "M10,7.4 A2.6,2.6 0 1 1 9.99,7.4 Z M10,2.5 V4.7 M10,15.3 V17.5 M2.5,10 H4.7 M15.3,10 H17.5 M4.7,4.7 L6.3,6.3 M13.7,13.7 L15.3,15.3 M4.7,15.3 L6.3,13.7 M13.7,6.3 L15.3,4.7", L.T("设置"), () => _c.OpenSettings("general"));
        _dock.Moved += () => _c.SaveWindowPos("dock", _dock);
        _dock.StateChanged += SaveDockState;
        _dock.Show();
        if (!_c.RestoreWindowPos("dock", _dock)) DockDefault();
        if (_c.Cfg.DockCollapsed) _dock.RestoreCollapsed(new Point(_dock.Left, _dock.Top), _c.Cfg.DockRight, _c.Cfg.DockTabTop);

        // 备忘录：贴在屏幕上的指引便签。每 30 秒看一次：定时的到点出现、过了凌晨 5 点每天的重新出现
        Core.GuideStore.Load();
        Core.GuideStore.Changed += SyncGuides;
        // 行程：提前一天弹便签提醒（也是每 30 秒看一次）
        Core.TripStore.Load();
        _remind.Tick += (_, _) => { CheckTrips(); SyncGuides(flash: true); };
        _remind.Start();
        CheckTrips();
        SyncGuides(flash: false);
        ApplyVaultApi();

        if (_sandbox) return; // 沙盒测试不抢真实的快捷键
        _keys = new Hotkeys();
        ApplyHotkeys();
        // 开机后稍等一会儿再启动索引程序，不和开机抢资源
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        t.Tick += (_, _) => { t.Stop(); IndexerHost.EnsureStarted(); };
        t.Start();
    }

    /// <summary>按配置重新注册快捷键；返回注册失败的（功能名 → 原因）。</summary>
    public Dictionary<string, string> ApplyHotkeys()
    {
        if (_keys == null) return new();
        var list = new List<(string, string, Action)>();
        foreach (var a in Actions)
        {
            Action run = a.Key switch { "shot" => () => Shot(), "search" => Search, "calc" => Calc, "memo" => Memo, "calendar" => () => Calendar(), _ => Vault };
            list.Add((a.Key, HotkeyOf(a.Key), run));
        }
        HotkeyErrors = _keys.Apply(list);
        foreach (var a in Actions)
        {
            var spec = HotkeyOf(a.Key);
            _dock?.SetTip(DockLabel(a.Key), a.Name + (spec.Length == 0 ? "" : spec == Hotkeys.DoubleCtrl ? L.T("（连按两下 Ctrl）") : $"（{spec}）"));
        }
        return HotkeyErrors;
    }

    /// <summary>设置里录快捷键时先停用，免得按下去直接触发截图。</summary>
    public void SuspendHotkeys() => _keys?.Apply(Array.Empty<(string, string, Action)>());

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

    public void Memo()
    {
        if (_memo == null)
        {
            _memo = new GuideManager(() => SystemParameters.WorkArea);
            _memo.Closed += (_, _) => { _memo = null; App.TrimMemory(); };
        }
        _memo.Popup();
    }

    /// <summary>显示 / 隐藏桌面上的日历。</summary>
    public void Calendar() => _c.ToggleCalendar();

    /// <summary>设置改了：日历重画。</summary>
    public void RefreshCalendar() => _c.CalWidget?.Refresh();

    void CheckTrips() => Core.TripStore.CheckReminders(DateTime.Now, SystemParameters.WorkArea);

    public void Vault()
    {
        if (_vault == null)
        {
            _vault = new VaultWindow { ApiOn = () => _c.Cfg.VaultApi, SetApiOn = on => { _c.Cfg.VaultApi = on; _c.Cfg.Save(); ApplyVaultApi(); }, ApiDoc = ApiDocPath };
            // 关窗口就锁上并释放，下次打开重新解锁
            _vault.Closed += (_, _) => { _vault = null; Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, App.TrimMemory); };
        }
        _vault.Popup();
    }

    // ---------------- 密码箱对外接口 ----------------
    Core.VaultApiServer? _api;
    public static string ApiDocPath => System.IO.Path.Combine(AppContext.BaseDirectory, "密码箱接口说明.md");

    /// <summary>按设置开 / 关接口（默认关）。</summary>
    public void ApplyVaultApi()
    {
        if (_c.Cfg.VaultApi && _api == null)
        {
            _api = new Core.VaultApiServer(Core.VaultApiServer.PipeNameFor(_sandbox),
                (req, cl) => Application.Current.Dispatcher.InvokeAsync(() => HandleApi(req, cl)).Task.Unwrap(),
                msg => { try { System.IO.File.AppendAllText(System.IO.Path.Combine(Core.AppConfig.DataDir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n"); } catch { } });
            _api.Start();
        }
        else if (!_c.Cfg.VaultApi && _api != null) { _api.Dispose(); _api = null; }
    }

    async Task<Core.ApiResponse> HandleApi(Core.ApiRequest req, Core.ApiClient cl)
    {
        if (!_c.Cfg.VaultApi) return Core.ApiResponse.Fail("disabled");
        switch (req.Op.Trim().ToLowerInvariant())
        {
            case "status":
                return new Core.ApiResponse { Ok = true, App = "DeskHub", Api = Core.VaultApiServer.Version, Locked = _vault?.Current == null };
            case "get": break;
            default: return Core.ApiResponse.Fail("bad_request");
        }
        if (Core.VaultApiServer.Host(req.Url) == null && string.IsNullOrWhiteSpace(req.Name)) return Core.ApiResponse.Fail("bad_request");
        var target = req.Url is { Length: > 0 } ? Core.VaultApiServer.Host(req.Url)! : req.Name!.Trim();

        var v = await UnlockForApi(cl, target);
        if (v == null) return Core.ApiResponse.Fail("locked");

        var cands = Core.VaultApiServer.Match(v.Data.Entries, req);
        void Log(string entry, string result)
        {
            v.Data.ApiLog.Add(new Core.ApiLogItem { Exe = cl.Exe, Target = target, Entry = entry, Result = result });
            if (v.Data.ApiLog.Count > 200) v.Data.ApiLog.RemoveRange(0, v.Data.ApiLog.Count - 200);
        }
        Core.ApiResponse Give(Core.VaultEntry e, string result)
        {
            e.LastUsed = DateTime.Now;
            Log(e.Name + "（" + e.Account + "）", result);
            v.Save();
            _vault?.Refresh();
            return new Core.ApiResponse { Ok = true, Credential = Core.VaultApiServer.ToCredential(e) };
        }

        // 点过"总是允许"的：同一个程序（文件指纹也一样）取同一条，直接给
        foreach (var e in cands)
        {
            var g = v.Data.Grants.FirstOrDefault(g => g.EntryId == e.Id && g.Hash == cl.Hash && string.Equals(g.Exe, cl.Exe, StringComparison.OrdinalIgnoreCase));
            if (g != null) { g.LastUsed = DateTime.Now; return Give(e, L.T("已授权")); }
        }

        var (ans, chosen) = await VaultConsent.Ask(cl, req, cands);
        if (_vault?.Current != v) return Core.ApiResponse.Fail("locked"); // 确认期间被锁上了
        switch (ans)
        {
            case VaultConsent.Answer.Once when chosen != null:
                return Give(chosen, L.T("允许一次"));
            case VaultConsent.Answer.Always when chosen != null:
                v.Data.Grants.RemoveAll(g => g.EntryId == chosen.Id && string.Equals(g.Exe, cl.Exe, StringComparison.OrdinalIgnoreCase));
                v.Data.Grants.Add(new Core.ApiGrant { Exe = cl.Exe, Hash = cl.Hash, EntryId = chosen.Id, LastUsed = DateTime.Now });
                return Give(chosen, L.T("总是允许"));
            case VaultConsent.Answer.NotFound:
                Log("", "没找到"); v.Save();
                return Core.ApiResponse.Fail("not_found");
            case VaultConsent.Answer.Timeout:
                Log("", "超时"); v.Save();
                return Core.ApiResponse.Fail("timeout");
            default:
                Log("", "拒绝"); v.Save();
                return Core.ApiResponse.Fail("denied");
        }
    }

    /// <summary>密码箱锁着：打开解锁界面并说明是谁在要，等你解锁（最多 2 分钟）。</summary>
    async Task<Core.Vault?> UnlockForApi(Core.ApiClient cl, string target)
    {
        if (_vault?.Current is { } v) return v;
        if (!Core.Vault.Exists || _sandbox) return null; // 沙盒测试不弹解锁界面
        Vault();
        var w = _vault!;
        w.SetLockNotice(L.F("「{0}」想取用 {1} 的登录信息。\n先输入主密码解锁，之后会再问你是否允许。", cl.Name, target));
        var tcs = new TaskCompletionSource<bool>();
        void OnUnlock() => tcs.TrySetResult(true);
        void OnClose(object? s, EventArgs e) => tcs.TrySetResult(false);
        w.Unlocked += OnUnlock;
        w.Closed += OnClose;
        try { await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(2))); }
        finally { w.Unlocked -= OnUnlock; w.Closed -= OnClose; }
        return w.Current;
    }

    // ---------------- 便签 ----------------
    void SyncGuides() => SyncGuides(flash: false);

    /// <summary>让屏幕上的便签和数据一致：该显示的显示（到点出现的闪几下），该收起的收起，改过的重画。</summary>
    void SyncGuides(bool flash)
    {
        var now = DateTime.Now;
        foreach (var (id, w) in _guides.ToList())
        {
            var n = Core.GuideStore.Find(id);
            bool keep = n != null && (Core.GuideStore.Visible(n, now) || w.CountingDown) && w.IsTopmostNote == n.Topmost;
            if (!keep) { w.Close(); _guides.Remove(id); }
            else w.Refresh(n!);
        }
        foreach (var n in Core.GuideStore.D.Notes.Where(n => !_guides.ContainsKey(n.Id) && Core.GuideStore.Visible(n, now)))
        {
            var w = new GuideWindow(n, EditGuide, Memo);
            w.Show();
            _guides[n.Id] = w;
            if (flash) w.Flash();
        }
    }

    void EditGuide(Core.GuideNote n)
    {
        if (GuideEditor.Ask(_memo, n, false)) Core.GuideStore.Save();
    }

    /// <summary>测试用：屏幕上的便签窗口。</summary>
    public IReadOnlyDictionary<string, GuideWindow> Guides => _guides;
    public GuideManager OpenManagerForTest() { Memo(); return _memo!; }

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
    public IEnumerable<Window> DesktopWindows() => _guides.Values.Where(g => !g.IsTopmostNote).Cast<Window>().Prepend(_dock!).Where(w => w != null);

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
        _keys?.Dispose();
        ShotSession.End();
        _search?.Close();
        _calc?.Close();
        _remind.Stop();
        Core.GuideStore.Changed -= SyncGuides;
        foreach (var g in _guides.Values) g.Close();
        _memo?.Close();
        _vault?.Close();
        Core.SecretClipboard.ClearNow();
        _arrToast?.Close();
        _api?.Dispose();
        _dock?.Close();
    }
}
