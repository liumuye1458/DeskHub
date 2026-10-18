using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DeskHub.Core;
using DeskHub.Interop;
using DeskHub.UI;
using VbFs = Microsoft.VisualBasic.FileIO.FileSystem;
using VbUi = Microsoft.VisualBasic.FileIO.UIOption;

namespace DeskHub;

/// <summary>总控：读桌面 → 分类 → 分发给各分区窗口；处理用户操作；定时检查归档。</summary>
public sealed class DeskController : IFenceHost
{
    public AppConfig Cfg { get; }
    public StateStore State { get; }
    public bool Sandbox { get; }
    readonly DesktopSource _src;
    readonly Archiver _archiver;
    readonly Dispatcher _ui;
    readonly Dictionary<string, FenceWindow> _fences = new();
    ClockWindow? _clock;
    List<DesktopItem> _items = new();
    bool _firstRun;
    readonly DispatcherTimer _midnight = new(DispatcherPriority.Background);
    readonly DispatcherTimer _saveSoon = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer _displaySettle = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
    bool _displayBusy;
    readonly DispatcherTimer _archiveSoon = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(2) };
    DateTime _archiveAskedDay = DateTime.MinValue;
    uint _taskbarCreated;

    public event Action<string>? Notify;          // 托盘气泡提示
    public void ShowNote(string msg) => Notify?.Invoke(msg);
    public event Action? RequestSettings;
    public event Action? RequestArchiveLog;

    static readonly StringComparer NameOrder = StringComparer.Create(new CultureInfo("zh-CN"), true);

    public DeskController(AppConfig cfg, StateStore st, DesktopSource src, bool sandbox)
    {
        Cfg = cfg; State = st; _src = src; Sandbox = sandbox;
        _archiver = new Archiver(cfg, st);
        _ui = Dispatcher.CurrentDispatcher;
        _firstRun = st.Items.Count == 0;
        _src.Changed += () => _ui.BeginInvoke(Refresh);
        _src.Renamed += (a, b) => _ui.BeginInvoke(() => State.Rename(a, b));
        _midnight.Tick += (_, _) => { Refresh(); CheckArchive(); ScheduleMidnight(); };
        _saveSoon.Tick += (_, _) => { _saveSoon.Stop(); State.Save(); };
        _archiveSoon.Tick += (_, _) => { _archiveSoon.Stop(); CheckArchive(); };
        // 插拔显示器、改分辨率时系统会连发好几次通知，中间状态是乱的：等稳定 1.5 秒再处理，期间不保存布局
        _displaySettle.Tick += (_, _) => { _displaySettle.Stop(); _displayBusy = false; OnDisplayStable(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanging += (_, _) => _ui.BeginInvoke(DisplayChanging);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => _ui.BeginInvoke(DisplayChanging);
    }

    // ================= 启动 / 退出 =================
    public void Start()
    {
        InitProfiles();
        _clock = new ClockWindow { ShowSeconds = Cfg.ShowSeconds };
        _clock.Moved += () => SaveLayout("clock", _clock);
        Place(_clock, "clock");
        _clock.SourceInitialized += (_, _) =>
        {
            _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
            HwndSource.FromHwnd(new WindowInteropHelper(_clock).Handle)?.AddHook(ExplorerHook);
        };
        _clock.Show();

        Make("apps", L.T("常用软件"), L.T("固定 · 不自动移动"), f => { f.Sort = x => x.OrderBy(i => !i.IsSystem).ThenBy(i => i.DisplayName, NameOrder); f.EmptyHint = L.T("桌面上的快捷方式会放在这里"); });
        Make("today", L.T("今天"), L.T("今天放到桌面的都在这"), f => { f.ShowNewDot = true; f.Sort = x => x.OrderByDescending(i => i.FirstSeen); f.EmptyHint = L.T("今天还没有新文件"); });
        Make("folders", L.T("项目文件夹"), L.T("固定"), f => { f.Sort = x => x.OrderBy(i => i.DisplayName, NameOrder); f.EmptyHint = L.T("桌面上的文件夹会放在这里"); });
        Make("active", L.T("进行中"), "", f => { f.ShowTabs = true; f.ShowChips = true; f.Sort = x => x.OrderByDescending(i => i.LastTouch); });
        Make("cold", L.T("冷却区"), "", f => { f.ShowDaysBadge = true; f.Cold = true; f.Sort = x => x.OrderBy(i => i.DaysLeft).ThenBy(i => i.LastTouch); f.EmptyHint = L.T("没有冷却中的文件"); });
        foreach (var cf in Cfg.CustomFences) MakeCustom(cf);
        ApplyTexts();
        foreach (var f in _fences.Values) f.Show();

        if (Cfg.HideSystemIcons && !Sandbox) DesktopHost.HideSystemIcons();
        Refresh();
        // 启动时遇到没见过的显示器组合：换算出的布局再对齐一遍、拉回屏幕内
        if (_tidyAfterStart) _ui.BeginInvoke(DispatcherPriority.Background, () => { TidyQuiet(); EnsureOnScreen(); });
        ScheduleMidnight();
        _archiveSoon.Start(); // 开机两分钟后再检查归档，不和开机抢资源
    }

    bool _stopped;
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        State.Save();
        if (!Sandbox) DesktopHost.ShowSystemIcons();
        foreach (var f in _fences.Values) f.Close();
        _clock?.Close();
        Tools?.Dispose();
        _src.Dispose();
    }

    IntPtr ExplorerHook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        // 资源管理器崩溃重启后：重新藏起系统图标、重新贴到新的桌面上
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            _ui.BeginInvoke(DispatcherPriority.Background, () =>
            {
                DesktopHost.OnExplorerRestarted(Cfg.HideSystemIcons && !Sandbox);
                foreach (var f in _fences.Values) DesktopHost.Repin(f);
                if (_clock != null) DesktopHost.Repin(_clock);
                if (Tools != null) foreach (var w in Tools.DesktopWindows()) DesktopHost.Repin(w);
            });
        }
        return IntPtr.Zero;
    }

    void Make(string key, string title, string hint, Action<FenceWindow> setup)
    {
        var f = new FenceWindow(key, title, hint, this) { Topics = Cfg.Topics };
        setup(f);
        Place(f, key);
        _fences[key] = f;
    }

    // ================= 自定义分区 =================
    void MakeCustom(CustomFence cf) =>
        Make(cf.Id, cf.Title, "", f => { f.Sort = x => x.OrderBy(i => i.DisplayName, NameOrder); f.EmptyHint = L.T("把文件、文件夹或软件拖到这里"); });

    /// <summary>新建一个分区：起名字，放在屏幕中间，之后想拖什么进去都行。</summary>
    public void NewFence(Window? owner = null)
    {
        owner ??= _fences.Values.FirstOrDefault();
        var name = InputDialog.Ask(owner!, L.T("新建分区（起个名字）"), L.T("我的分区"));
        if (!string.IsNullOrWhiteSpace(name)) NewFenceNamed(name);
    }

    public void NewFenceNamed(string name)
    {
        var cf = new CustomFence { Id = "c" + DateTime.Now.ToString("yyMMddHHmmssfff"), Title = name.Trim() };
        Cfg.CustomFences.Add(cf);
        MakeCustom(cf);
        var f = _fences[cf.Id];
        f.Show();
        SaveLayout(cf.Id, f);
        Refresh();
        f.Activate();
    }

    void RenameFence(FenceWindow f)
    {
        var cf = Cfg.CustomFences.FirstOrDefault(c => c.Id == f.Key);
        if (cf == null) return;
        var name = InputDialog.Ask(f, L.T("分区改名"), cf.Title);
        if (string.IsNullOrWhiteSpace(name)) return;
        cf.Title = name.Trim();
        f.TitleText.Text = cf.Title;
        Cfg.Save();
    }

    void DeleteFence(FenceWindow f)
    {
        var cf = Cfg.CustomFences.FirstOrDefault(c => c.Id == f.Key);
        if (cf == null) return;
        if (MessageBox.Show(f, L.F("删除分区“{0}”？\n\n里面的文件不会被删除，会回到原来的分区（软件回常用软件、文件夹回项目文件夹、文件按时间流转）。", cf.Title),
                L.T("删除分区"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Cfg.CustomFences.Remove(cf);
        Cfg.Layout.Remove(cf.Id);
        foreach (var m in State.Items.Values) if (m.Fence == cf.Id) m.Fence = null;
        _fences.Remove(cf.Id);
        f.Close();
        Cfg.Save();
        Refresh();
    }

    /// <summary>桌面项现在显示在哪个分区（查找结果里用）。</summary>
    public string FenceTitle(DesktopItem it) =>
        it.Zone == Zone.Custom ? Cfg.CustomFences.FirstOrDefault(c => c.Id == it.Fence)?.Title ?? L.T("自定义分区") : ZoneName(it.Zone);

    /// <summary>设置改了以后，刷新各分区上的说明文字。</summary>
    public void ApplyTexts()
    {
        _fences["active"].HintText.Text = L.F("{0} 天内用过 · 按事项分", Cfg.ColdDays);
        _fences["active"].EmptyHint = L.F("最近 {0} 天没有用过的文件", Cfg.ColdDays);
        _fences["cold"].HintText.Text = L.F("{0} 天没碰 · 角标 = 距归档还剩几天", Cfg.ColdDays);
        _fences["cold"].SetFooter(Cfg.AutoArchive
            ? L.F("{0} 天没碰的会移到 {1}", Cfg.ArchiveDays, Path.Combine(Cfg.ArchiveRoot, L.T("年-月"), L.T("事项")) + "\\")
            : L.T("自动归档已关闭"));
        foreach (var f in _fences.Values) f.Topics = Cfg.Topics;
        if (_clock != null) { _clock.ShowSeconds = Cfg.ShowSeconds; _clock.Tick(); }
    }

    // ================= 布局 =================
    static readonly Dictionary<string, Rect> Default = new()
    {
        // 按 1600×852 的设计稿排布，实际按工作区等比缩放（与效果图一致）
        ["clock"] = new(40, 28, 0, 0),
        ["apps"] = new(40, 190, 250, 470),
        ["today"] = new(320, 40, 470, 250),
        ["folders"] = new(320, 310, 470, 150),
        ["active"] = new(820, 40, 540, 340),
        ["cold"] = new(820, 400, 540, 200),
    };

    void Place(Window w, string key)
    {
        var wa = WA;
        if (Cfg.Layout.TryGetValue(key, out var l) && OnScreen(new Rect(l.X, l.Y, Math.Max(l.W, 50), Math.Max(l.H, 50))))
        {
            w.Left = l.X; w.Top = l.Y;
            if (l.W > 0 && w is FenceWindow) { w.Width = l.W; w.Height = l.H; }
            return;
        }
        if (!Default.TryGetValue(key, out var d))
        {
            // 自定义分区：先放默认布局里的空白处（项目文件夹下面、冷却区下面），再多就错开叠放
            int n = Math.Max(0, Cfg.CustomFences.FindIndex(c => c.Id == key));
            d = n switch
            {
                0 => new Rect(320, 490, 470, 190),
                1 => new Rect(820, 620, 540, 190),
                _ => new Rect(340 + (n - 2) * 30, 510 + (n - 2) * 30, 360, 200),
            };
        }
        double sx = Math.Max(0.75, Math.Min(1.6, wa.Width / 1600)), sy = Math.Max(0.75, Math.Min(1.6, wa.Height / 852));
        w.Left = wa.Left + d.X * sx; w.Top = wa.Top + d.Y * sy;
        if (w is FenceWindow) { w.Width = d.Width * sx; w.Height = d.Height * sy; }
    }

    static bool OnScreen(Rect r)
    {
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        // 至少标题栏要有一大半露在屏幕里，才拖得回来
        var head = new Rect(r.X + 20, r.Y, Math.Max(40, r.Width - 40), 28);
        var hit = Rect.Intersect(vs, head);
        return !hit.IsEmpty && hit.Width >= Math.Min(80, head.Width) && hit.Height >= 20;
    }

    /// <summary>拔掉外接屏后，原来放在外接屏上的分区挪回主屏，免得"消失"。</summary>
    void EnsureOnScreen()
    {
        ClampAll();
        Tools?.EnsureOnScreen(OnScreen);
        Cfg.Save();
    }

    // ================= 多显示器：每种显示器组合一套布局 =================
    // 测试用：假装换了显示器（沙盒里没法真的插拔）
    string? _fakeSig; Rect? _fakeWa;
    Rect WA => _fakeWa ?? LiveWorkArea();

    /// <summary>
    /// 主屏工作区（设备无关像素）。直接问系统当前的屏幕，不用 SystemParameters.WorkArea：
    /// 插拔显示器后它要等任务栏重新登记才更新，刚换完屏时读到的是旧值（没扣任务栏）。
    /// </summary>
    static Rect LiveWorkArea()
    {
        try
        {
            var s = System.Windows.Forms.Screen.PrimaryScreen;
            if (s == null || SystemParameters.PrimaryScreenWidth <= 0) return SystemParameters.WorkArea;
            double k = s.Bounds.Width / SystemParameters.PrimaryScreenWidth; // 物理像素 / 设备无关像素
            var w = s.WorkingArea;
            return new Rect(w.X / k, w.Y / k, w.Width / k, w.Height / k);
        }
        catch { return SystemParameters.WorkArea; }
    }

    /// <summary>
    /// 显示器组合的标识：每块屏的位置和分辨率（物理像素，标出主屏）+ 主屏按缩放后的大小。
    /// 只接笔记本、扩展到外接屏、复制到投影仪、改分辨率或缩放比例，都会得到不同的标识。
    /// 任务栏挪位置、自动隐藏不算（不用工作区）。
    /// </summary>
    string DisplaySig()
    {
        if (_fakeSig != null) return _fakeSig;
        var screens = System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)
            .Select(s => $"{(s.Primary ? "*" : "")}{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width}x{s.Bounds.Height}");
        return string.Join("|", screens) + $"@{SystemParameters.PrimaryScreenWidth:0}x{SystemParameters.PrimaryScreenHeight:0}";
    }

    LayoutProfile NewProfile(Dictionary<string, FenceLayout> items)
    {
        var wa = WA;
        return new LayoutProfile { WaX = wa.X, WaY = wa.Y, WaW = wa.Width, WaH = wa.Height, Items = items, Used = DateTime.Now };
    }

    /// <summary>启动时：用这种显示器组合存过的布局；没存过就从上次用的那套换算一套。</summary>
    void InitProfiles()
    {
        var sig = DisplaySig();
        if (Cfg.Layouts.Count == 0)
        {
            // 老版本只有一套布局：算作现在这种组合的
            Cfg.Layouts[sig] = NewProfile(Cfg.Layout);
        }
        else if (!Cfg.Layouts.ContainsKey(sig))
        {
            var src = Cfg.Layouts.TryGetValue(Cfg.LayoutSig, out var last) ? last : Cfg.Layouts.Values.OrderByDescending(p => p.Used).First();
            Cfg.Layouts[sig] = Derive(src);
            _tidyAfterStart = true;
        }
        var p = Cfg.Layouts[sig];
        p.Used = DateTime.Now;
        Cfg.Layout = p.Items; // 同一份：之后拖动保存的就是这套
        Cfg.LayoutSig = sig;
        Trim();
        Cfg.Save();
    }
    bool _tidyAfterStart;

    void DisplayChanging()
    {
        _displayBusy = true;
        _displaySettle.Stop();
        _displaySettle.Start();
    }

    /// <summary>屏幕稳定后：换到这种组合的那套布局（没有就换算一套），保证都在屏幕内。</summary>
    void OnDisplayStable()
    {
        var sig = DisplaySig();
        if (sig == Cfg.LayoutSig) { EnsureOnScreen(); return; }
        // 先把当前这套记下来（就是 Cfg.Layout，拖动时已存），再切换
        var from = Cfg.Layouts.TryGetValue(Cfg.LayoutSig, out var cur) ? cur : NewProfile(Cfg.Layout);
        bool fresh = !Cfg.Layouts.TryGetValue(sig, out var p);
        if (fresh) { p = Derive(from); Cfg.Layouts[sig] = p; }
        p!.Used = DateTime.Now;
        Cfg.Layout = p.Items;
        Cfg.LayoutSig = sig;
        Trim();
        ApplyProfile();
        if (fresh) TidyQuiet();
        EnsureOnScreen();
        Log($"显示器变化 → {(fresh ? "新组合，已按比例换算" : "用这种组合存过的布局")}：{sig}");
    }

    /// <summary>按当前这套布局把所有窗口摆好。</summary>
    void ApplyProfile()
    {
        foreach (var (k, f) in _fences) Place(f, k);
        if (_clock != null) Place(_clock, "clock");
        Tools?.Relayout();
        foreach (var f in _fences.Values) f.UpdateLayout();
    }

    /// <summary>
    /// 从一套布局换算到现在的屏幕：分区按工作区的长宽比例缩放（共用的边线缩放后仍然共用）；
    /// 时钟、工具条、便签这些小窗口保持离最近那条边的距离（贴右下角的还贴右下角）。
    /// </summary>
    LayoutProfile Derive(LayoutProfile src)
    {
        var wa = WA;
        double sw = src.WaW > 0 ? src.WaW : wa.Width, sh = src.WaH > 0 ? src.WaH : wa.Height;
        double sx = wa.Width / sw, sy = wa.Height / sh;
        var items = new Dictionary<string, FenceLayout>();
        foreach (var (k, l) in src.Items)
        {
            double rx = l.X - src.WaX, ry = l.Y - src.WaY;
            if (l.W > 0)
            {
                items[k] = new FenceLayout
                {
                    X = wa.Left + rx * sx, Y = wa.Top + ry * sy,
                    W = Math.Max(160, l.W * sx), H = Math.Max(90, l.H * sy),
                };
            }
            else
            {
                double x = rx < sw / 2 ? wa.Left + rx : wa.Right - (sw - rx);
                double y = ry < sh / 2 ? wa.Top + ry : wa.Bottom - (sh - ry);
                items[k] = new FenceLayout { X = x, Y = y };
            }
        }
        return NewProfile(items);
    }

    /// <summary>换算后对齐一遍（缝宽用你原来的，不重新学）。时钟不跟着缩小，先让压到时钟的分区往下让。</summary>
    void TidyQuiet()
    {
        if (_clock != null)
        {
            _clock.UpdateLayout();
            var c = new Rect(_clock.Left, _clock.Top, _clock.ActualWidth, _clock.ActualHeight);
            foreach (var (k, f) in _fences)
            {
                var r = new Rect(f.Left, f.Top, f.Width, f.Height);
                if (!r.IntersectsWith(c)) continue;
                double top = c.Bottom + Cfg.ArrangeGap;
                Apply(k, f, new Rect(r.X, top, r.Width, Math.Max(90, r.Bottom - top)));
            }
        }
        try { Arrange(false, CaptureLayout(), Rect.Empty, learnGap: false); } catch (Exception ex) { Log("换算后对齐失败：" + ex.Message); }
    }

    /// <summary>所有分区、时钟都完整地放在主屏工作区里（比工作区还大的先缩小）。不删你的摆法。</summary>
    void ClampAll()
    {
        var wa = WA;
        foreach (var (k, f) in _fences)
        {
            double w = Math.Min(f.Width, wa.Width), h = Math.Min(f.Height, wa.Height);
            double x = Math.Clamp(f.Left, wa.Left, wa.Right - w), y = Math.Clamp(f.Top, wa.Top, wa.Bottom - h);
            if (x != f.Left || y != f.Top || w != f.Width || h != f.Height) Apply(k, f, new Rect(x, y, w, h));
        }
        if (_clock != null)
        {
            double w = _clock.ActualWidth > 0 ? _clock.ActualWidth : 200, h = _clock.ActualHeight > 0 ? _clock.ActualHeight : 80;
            double x = Math.Clamp(_clock.Left, wa.Left, Math.Max(wa.Left, wa.Right - w)), y = Math.Clamp(_clock.Top, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
            if (x != _clock.Left || y != _clock.Top) Apply("clock", _clock, new Rect(x, y, 0, 0));
        }
    }

    /// <summary>最多记 10 种组合，太久没用的删掉。</summary>
    void Trim()
    {
        foreach (var k in Cfg.Layouts.OrderByDescending(kv => kv.Value.Used).Skip(10).Select(kv => kv.Key).ToList())
            if (k != Cfg.LayoutSig) Cfg.Layouts.Remove(k);
    }

    static void Log(string s)
    {
        try { File.AppendAllText(Path.Combine(AppConfig.DataDir, "display.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {s}\r\n"); } catch { }
    }

    /// <summary>测试用：sig=null 恢复真实屏幕。</summary>
    public string TestDisplay(string? sig, Rect? wa)
    {
        _fakeSig = sig; _fakeWa = wa;
        OnDisplayStable();
        return $"sig={Cfg.LayoutSig} 共 {Cfg.Layouts.Count} 套";
    }

    public void ResetLayout()
    {
        Cfg.Layout.Clear();
        foreach (var (k, f) in _fences) Place(f, k);
        if (_clock != null) Place(_clock, "clock");
        Tools?.DefaultPositions();
        Cfg.Save();
    }

    // ================= 排列（工具条「排列」） =================
    /// <summary>参与排列的窗口：分区、时钟、桌面便签（工具条不动）。</summary>
    List<(string Key, Window W)> ArrangeTargets()
    {
        var list = _fences.Select(kv => (kv.Key, (Window)kv.Value)).ToList();
        if (_clock != null) list.Insert(0, ("clock", _clock));
        if (Tools != null) list.AddRange(Tools.NoteEntries());
        return list.Where(t => t.Item2.IsVisible).ToList();
    }

    /// <summary>记下现在的摆放（撤销用）。</summary>
    public Dictionary<string, Rect> CaptureLayout() =>
        ArrangeTargets().ToDictionary(t => t.Key, t => new Rect(t.W.Left, t.W.Top, t.W.ActualWidth, t.W.ActualHeight));

    public void RestoreLayout(Dictionary<string, Rect> saved)
    {
        foreach (var (k, w) in ArrangeTargets())
            if (saved.TryGetValue(k, out var r)) Apply(k, w, r);
        Cfg.Save();
    }

    void Apply(string key, Window w, Rect r)
    {
        w.Left = r.X; w.Top = r.Y;
        if (w is FenceWindow) { w.Width = r.Width; w.Height = r.Height; }
        Cfg.Layout[key] = new FenceLayout { X = r.X, Y = r.Y, W = w is FenceWindow ? r.Width : 0, H = w is FenceWindow ? r.Height : 0 };
    }

    /// <summary>auto=false 对齐你现有的摆法；auto=true 按推荐方案重排。都以 from（排列前的样子）为起点。</summary>
    public void Arrange(bool auto, Dictionary<string, Rect> from, Rect avoid, bool learnGap = true)
    {
        var wa = WA;
        var targets = ArrangeTargets();
        // 先按排列前的样子摆回去，量出每个分区标题栏、标签页占多少地方，再算
        RestoreLayout(from);
        // 第二遍：大小变了以后标签页可能换行，重新量一次再算
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var (_, w) in targets) w.UpdateLayout();
            var blocks = new List<Block>();
            foreach (var (k, w) in targets)
            {
                var r = from.TryGetValue(k, out var fr) ? fr : new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight);
                var b = new Block { Key = k, R = r };
                if (w is FenceWindow f)
                {
                    b.Fit = true;
                    b.Items = f.ItemCount;
                    b.ChromeW = f.ChromeW; b.ChromeH = f.ChromeH;
                    b.MinRows = f.Zone is Zone.Today or Zone.Active or Zone.Cold ? 2 : 1;
                    b.Group = f.Zone switch { Zone.Apps => 0, Zone.Folders => 0, Zone.Custom => 1, Zone.Active => 1, _ => 2 };
                    b.Spill = f.Zone == Zone.Custom;
                }
                else b.Group = k == "clock" ? 0 : 9;
                blocks.Add(b);
            }
            // 列内顺序：左列 时钟、常用软件、项目文件夹；中列 自定义分区、进行中；右列 今天、冷却区
            int Rank(Block b) => b.Key switch { "clock" => 0, "apps" => 1, "today" => 0, "folders" => 9, "active" => 9, "cold" => 9, _ => 2 };
            blocks = blocks.OrderBy(Rank).ThenBy(b => Cfg.CustomFences.FindIndex(c => c.Id == b.Key)).ToList();
            var scale = DpiScale();
            if (auto) Arranger.Auto(blocks, wa, avoid, Cfg.ArrangeGap, scale);
            else
            {
                // 对齐：缝宽按你现在的摆法学（学到的也用于拖动吸附和推荐排版）
                if (learnGap) Cfg.ArrangeGap = Arranger.LearnGap(blocks);
                Arranger.Tidy(blocks, wa, Cfg.ArrangeGap, scale);
            }
            foreach (var b in blocks)
            {
                var w = targets.First(t => t.Key == b.Key).W;
                Apply(b.Key, w, b.R);
            }
        }
        Cfg.Save();
    }

    double DpiScale() => _fences.Count > 0 ? System.Windows.Media.VisualTreeHelper.GetDpi(_fences.Values.First()).DpiScaleX : 1;

    /// <summary>拖动、拉大小时吸附到别的窗口的边、统一的缝（按住 Alt 不吸）。</summary>
    public Rect SnapRect(FenceWindow f, Rect r, bool resize)
    {
        if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) return r;
        var others = ArrangeTargets().Where(t => t.W != f).Select(t => new Rect(t.W.Left, t.W.Top, t.W.ActualWidth, t.W.ActualHeight));
        return Arranger.Snap(r, others, WA, Cfg.ArrangeGap, resize, DpiScale());
    }

    public string TestSnap(string keyOrTitle, double x, double y)
    {
        var f = _fences.TryGetValue(keyOrTitle, out var ff) ? ff : _fences.Values.First(v => v.TitleText.Text == keyOrTitle);
        var wa = SystemParameters.WorkArea;
        var r = SnapRect(f, new Rect(wa.Left + x, wa.Top + y, f.ActualWidth, f.ActualHeight), false);
        return $"({x},{y}) → ({r.X - wa.Left:0.#},{r.Y - wa.Top:0.#})";
    }

    /// <summary>测试用：直接摆一个分区。</summary>
    public void TestMove(string keyOrTitle, double x, double y, double w, double h)
    {
        var f = _fences.TryGetValue(keyOrTitle, out var ff) ? ff : _fences.Values.First(v => v.TitleText.Text == keyOrTitle);
        var wa = SystemParameters.WorkArea;
        Apply(f.Key, f, new Rect(wa.Left + x, wa.Top + y, w, h));
        f.UpdateLayout();
    }

    void SaveLayout(string key, Window w)
    {
        if (_displayBusy) return; // 屏幕正在变，窗口位置是乱的，不能存
        Cfg.Layout[key] = new FenceLayout { X = w.Left, Y = w.Top, W = w is FenceWindow ? w.Width : 0, H = w is FenceWindow ? w.Height : 0 };
        Cfg.Save();
    }

    public void LayoutChanged(FenceWindow fence) => SaveLayout(fence.Key, fence);

    // 工具条、计算器这类小窗口的位置
    public void SaveWindowPos(string key, Window w) => SaveLayout(key, w);

    public bool RestoreWindowPos(string key, Window w)
    {
        if (!Cfg.Layout.TryGetValue(key, out var l) || !OnScreen(new Rect(l.X, l.Y, Math.Max(w.ActualWidth, 120), 50))) return false;
        w.Left = l.X; w.Top = l.Y;
        return true;
    }

    public ToolHub? Tools { get; set; }
    public event Action<string>? RequestPage;
    public void OpenSettings(string page) => RequestPage?.Invoke(page);

    // ================= 刷新 =================
    public IReadOnlyList<DesktopItem> Items => _items;
    public DesktopSource Source => _src;

    public static string ZoneName(Zone z) => z switch
    {
        Zone.Apps => L.T("常用软件"), Zone.Folders => L.T("项目文件夹"), Zone.Today => L.T("今天"), Zone.Active => L.T("进行中"), Zone.Custom => L.T("自定义分区"), _ => L.T("冷却区"),
    };

    public DesktopItem? Find(string path) => _items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    public void Refresh()
    {
        var now = DateTime.Now;
        var items = _src.Scan();
        var recent = _src.RecentOpens(items);
        Classifier.Apply(items, Cfg, State, recent, now, _firstRun);
        if (_firstRun) { _firstRun = false; }
        State.Prune(items.Select(i => i.Path));
        _items = items;

        _fences["apps"].SetItems(items.Where(i => i.Zone == Zone.Apps).ToList());
        _fences["folders"].SetItems(items.Where(i => i.Zone == Zone.Folders).ToList());
        _fences["today"].SetItems(items.Where(i => i.Zone == Zone.Today).ToList());
        _fences["active"].SetItems(items.Where(i => i.Zone == Zone.Active).ToList());
        _fences["cold"].SetItems(items.Where(i => i.Zone == Zone.Cold).ToList());
        foreach (var cf in Cfg.CustomFences)
            if (_fences.TryGetValue(cf.Id, out var f)) f.SetItems(items.Where(i => i.Zone == Zone.Custom && i.Fence == cf.Id).ToList());
        _saveSoon.Stop(); _saveSoon.Start();
    }

    void ScheduleMidnight()
    {
        var next = DateTime.Today.AddDays(1).AddSeconds(5);
        _midnight.Interval = next - DateTime.Now;
        _midnight.Start();
    }

    // ================= 归档 =================
    public List<DesktopItem> DueItems() => _archiver.Due(_items).ToList();

    public void CheckArchive(bool userAsked = false)
    {
        Refresh();
        var due = DueItems();
        if (due.Count == 0) { if (userAsked) Notify?.Invoke(L.T("现在没有需要归档的文件")); return; }
        if (!Cfg.AutoArchive && !userAsked) return;
        if (!Cfg.ArchiveConfirmed || userAsked)
        {
            // 第一次归档必须让用户看过清单；之后才自动执行
            if (!userAsked && _archiveAskedDay == DateTime.Today) return;
            _archiveAskedDay = DateTime.Today;
            var win = new ArchiveConfirmWindow(due, Cfg);
            if (win.ShowDialog() != true) return;
            Cfg.ArchiveConfirmed = true;
            Cfg.Save();
        }
        RunArchive(due);
    }

    public void ArchiveNow(DesktopItem item) => RunArchive(new List<DesktopItem> { item });

    void RunArchive(List<DesktopItem> due)
    {
        var r = _archiver.Run(due, DateTime.Now);
        Refresh();
        var msg = L.F("已归档 {0} 个文件到 {1}，可在“归档记录”里撤回", r.Moved, Path.Combine(Cfg.ArchiveRoot, DateTime.Now.ToString("yyyy-MM")) + "\\");
        if (r.Failed.Count > 0) msg += L.F("；{0} 个文件正在使用，下次再试", r.Failed.Count);
        Notify?.Invoke(msg);
    }

    public (int ok, int fail) Undo(string batch)
    {
        var r = _archiver.Undo(batch);
        Refresh();
        return r;
    }

    public void FooterClicked() => RequestArchiveLog?.Invoke();

    // ================= 用户操作 =================
    public void OpenItem(DesktopItem item)
    {
        var meta = State.Get(item.Path, DateTime.Now);
        meta.LastOpened = DateTime.Now;
        if (item.IsSystem) Native.ShellExecute(IntPtr.Zero, null, "explorer.exe", "shell:" + item.Path, null, 1);
        else ShellMenu.Open(item.Path);
        Refresh();
    }

    public void DeleteItem(DesktopItem item)
    {
        if (item.IsSystem) return; // 系统图标不能删
        try
        {
            // 和资源管理器一样：放进回收站，可以还原
            if (item.IsDir) VbFs.DeleteDirectory(item.Path, VbUi.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else VbFs.DeleteFile(item.Path, VbUi.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Notify?.Invoke(L.T("删除失败：") + ex.Message); }
        Refresh();
    }

    public void ShowItemMenu(FenceWindow fence, DesktopItem item)
    {
        var meta = State.Get(item.Path, DateTime.Now);
        var own = new List<ShellMenu.Own?>();
        if (!item.IsSystem)
        {
            own.Add(new(L.T("打开所在位置"), () => ShellMenu.Reveal(item.Path)));
            own.Add(new(L.T("重命名"), () => Rename(fence, item)));
        }
        bool flows = !item.IsDir && !item.IsShortcut && !item.IsSystem;
        if (flows)
        {
            var subs = Cfg.Topics.Select(t => t.Name).Append(AppConfig.OtherTopic)
                .Select(n => (ShellMenu.Own?)new ShellMenu.Own(n, () => MoveToTopic(item, n, fence), Checked: item.Topic == n)).ToList();
            own.Add(new ShellMenu.Own(L.T("归到事项"), null, Sub: subs!));
            own.Add(new ShellMenu.Own(item.Pinned ? L.T("取消钉住") : L.T("钉住（留在进行中，不归档）"), () => { meta.Pinned = !meta.Pinned; meta.Placed = null; Refresh(); }));
            if (!item.IsPublic) own.Add(new ShellMenu.Own(L.T("立即归档"), () => ArchiveNow(item)));
        }
        if (ShellMenu.Show(fence, item.Path, own)) _ui.BeginInvoke(DispatcherPriority.Background, Refresh);
    }

    public void ShowFenceMenu(FenceWindow fence)
    {
        var m = new System.Windows.Controls.ContextMenu();
        void Add(string text, Action a) { var mi = new System.Windows.Controls.MenuItem { Header = text }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
        Add(L.T("新建分区…"), () => NewFence(fence));
        if (fence.Zone == Zone.Custom)
        {
            Add(L.T("分区改名…"), () => RenameFence(fence));
            Add(L.T("删除分区"), () => DeleteFence(fence));
        }
        m.Items.Add(new System.Windows.Controls.Separator());
        Add(L.T("设置…"), () => RequestSettings?.Invoke());
        Add(L.T("立即检查归档"), () => CheckArchive(userAsked: true));
        Add(L.T("归档记录 / 撤回…"), () => RequestArchiveLog?.Invoke());
        m.Items.Add(new System.Windows.Controls.Separator());
        Add(L.T("排列：对齐现在的摆法"), () => Tools?.Arrange(1));
        Add(L.T("排列：按推荐方案重排"), () => Tools?.Arrange(2));
        Add(L.T("恢复默认布局"), ResetLayout);
        Add(L.T("刷新"), Refresh);
        m.PlacementTarget = fence.MenuBtn;
        m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        m.IsOpen = true;
    }

    void Rename(FenceWindow fence, DesktopItem item)
    {
        if (item.IsSystem) return;
        var name = InputDialog.Ask(fence, L.T("重命名"), item.Name, item.IsDir ? null : Path.GetFileNameWithoutExtension(item.Name).Length);
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
        try
        {
            var dest = Path.Combine(Path.GetDirectoryName(item.Path)!, name);
            if (item.IsDir) Directory.Move(item.Path, dest); else File.Move(item.Path, dest);
            State.Rename(item.Path, dest);
            ShellIcons.Forget(item.Path);
        }
        catch (Exception ex) { MessageBox.Show(fence, L.T("重命名失败：") + ex.Message, "HOTA DESK"); }
        Refresh();
    }

    public void MoveToTopic(DesktopItem item, string topic, FenceWindow fence)
    {
        if (item.IsDir || item.IsShortcut || item.IsSystem) return;
        var from = item.Topic;
        var meta = State.Get(item.Path, DateTime.Now);
        meta.Topic = topic;
        Refresh();
        if (from == topic || topic == AppConfig.OtherTopic) return;
        var rule = Cfg.FindTopic(topic);
        if (rule == null) return;
        var kw = Classifier.SuggestKeyword(item.Name, Cfg);
        var toast = new LearnToast(item.Name, from, topic, kw, k =>
        {
            foreach (var t in Cfg.Topics) t.Learned.RemoveAll(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase));
            rule.Learned.Add(k);
            Cfg.Save();
            Refresh();
        });
        toast.Show();
        toast.PlaceNear(fence);
    }

    /// <summary>把桌面项拖到另一个分区。拖到"进行中"的某个事项标签上，同时换事项。</summary>
    public void PlaceItem(DesktopItem item, FenceWindow fence, string? topic)
    {
        if (fence.Zone == Zone.Custom) { PutInFence(item.Path, fence.Key); Refresh(); return; }
        if (item.IsSystem) return; // 系统图标固定在常用软件里
        if (fence.Zone != item.Zone || item.Zone == Zone.Custom) Place(item.Path, fence.Zone, item.IsDir || item.IsShortcut);
        if (fence.Zone == Zone.Active && topic != null && topic != item.Topic) { MoveToTopic(item, topic, fence); return; }
        Refresh();
    }

    /// <summary>放进自定义分区：一直留在里面，不流转、不归档。</summary>
    void PutInFence(string path, string fenceId)
    {
        var meta = State.Get(path, DateTime.Now);
        meta.Fence = fenceId;
        meta.Placed = null;
        meta.Pinned = false;
    }

    void Place(string path, Zone to, bool notFile)
    {
        var now = DateTime.Now;
        var meta = State.Get(path, now);
        meta.Fence = null; // 从自定义分区拖出来
        meta.Placed = to;
        meta.PlacedAt = now;
        if (to != Zone.Active) meta.Pinned = false; // 拖出"进行中"就不再钉住
        // 拖到今天 / 进行中算"碰过一次"，之后从这里开始重新计时
        if (!notFile && to is Zone.Today or Zone.Active) meta.LastOpened = now;
    }

    /// <summary>从资源管理器等地方把文件拖到分区上：放进桌面（同盘默认移动、跨盘默认复制，和资源管理器一致）。</summary>
    public void DropFiles(string[] paths, FenceWindow fence, string? topic, bool forceCopy, bool forceMove)
    {
        foreach (var p in paths)
        {
            try
            {
                var name = Path.GetFileName(p.TrimEnd('\\'));
                string dest;
                if (string.Equals(Path.GetDirectoryName(p), _src.UserDesktop, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetDirectoryName(p), _src.PublicDesktop, StringComparison.OrdinalIgnoreCase))
                {
                    dest = p; // 本来就在桌面上，只是换个事项
                }
                else if (!Directory.Exists(p) && Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // 拖进来的是程序本身：在桌面建一个快捷方式，不把程序搬走（搬走会让软件打不开）
                    dest = Unique(Path.Combine(_src.UserDesktop, Path.GetFileNameWithoutExtension(p) + ".lnk"));
                    MakeShortcut(dest, p);
                }
                else if (Path.GetExtension(p).Equals(".lnk", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    // 快捷方式（比如从开始菜单拖来的）：复制一份，原来的不动
                    dest = Unique(Path.Combine(_src.UserDesktop, name));
                    File.Copy(p, dest);
                }
                else
                {
                    dest = Path.Combine(_src.UserDesktop, name);
                    bool sameDrive = string.Equals(Path.GetPathRoot(p), Path.GetPathRoot(_src.UserDesktop), StringComparison.OrdinalIgnoreCase);
                    bool move = forceMove || (!forceCopy && sameDrive);
                    bool dir = Directory.Exists(p);
                    if (move) { if (dir) VbFs.MoveDirectory(p, dest, VbUi.AllDialogs); else VbFs.MoveFile(p, dest, VbUi.AllDialogs); }
                    else { if (dir) VbFs.CopyDirectory(p, dest, VbUi.AllDialogs); else VbFs.CopyFile(p, dest, VbUi.AllDialogs); }
                }
                var meta = State.Get(dest, DateTime.Now);
                if (topic != null) meta.Topic = topic;
                // 新放上桌面的默认进"今天"；拖到别的分区就放在那个分区
                if (fence.Zone == Zone.Custom) PutInFence(dest, fence.Key);
                else if (fence.Zone != Zone.Today) Place(dest, fence.Zone, Directory.Exists(dest));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Notify?.Invoke(L.F("放到桌面失败：{0}", ex.Message)); }
        }
        Refresh();
    }

    static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    static void MakeShortcut(string lnk, string target)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic sh = Activator.CreateInstance(t)!;
        try
        {
            dynamic sc = sh.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sh); }
    }

    // ================= 调试：把整套桌面布局画成一张图（自查用，不影响使用者屏幕） =================
    public void Snapshot(string file)
    {
        var wa = SystemParameters.WorkArea;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_fences["active"]);
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new System.Windows.Media.LinearGradientBrush(
                System.Windows.Media.Color.FromRgb(0x2c, 0x4f, 0x73), System.Windows.Media.Color.FromRgb(0xd9, 0xa2, 0x7a), 35);
            dc.DrawRectangle(bg, null, new Rect(0, 0, wa.Width, wa.Height));
            var wins = _fences.Values.Cast<Window>().Append(_clock!).Concat(Application.Current.Windows.OfType<LearnToast>())
                .Concat(Tools?.DesktopWindows() ?? Enumerable.Empty<Window>()).Concat(Application.Current.Windows.OfType<ArrangeToast>().Where(t => t.IsVisible));
            foreach (var w in wins)
            {
                if (w.Content is not System.Windows.Media.Visual v || w.ActualWidth < 1) continue;
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w.ActualWidth * dpi.DpiScaleX), (int)(w.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(v);
                dc.DrawImage(rtb, new Rect(w.Left - wa.Left, w.Top - wa.Top, w.ActualWidth, w.ActualHeight));
            }
        }
        var outBmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(wa.Width * dpi.DpiScaleX), (int)(wa.Height * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
        outBmp.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(outBmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    // ================= 系统图标开关 =================
    public void SetHideSystemIcons(bool hide)
    {
        Cfg.HideSystemIcons = hide;
        Cfg.Save();
        if (Sandbox) return;
        if (hide) DesktopHost.HideSystemIcons(); else DesktopHost.ShowSystemIcons();
    }
}
