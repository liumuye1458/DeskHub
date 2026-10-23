using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DeskHub.Interop;

/// <summary>
/// 和 Windows 桌面打交道：
///  1. 隐藏 / 恢复系统自带的桌面图标层（只隐藏窗口，不改系统设置；资源管理器重启会自动恢复，所以本工具崩溃也不会把图标弄丢）
///  2. 把分区窗口"贴"在桌面上：始终在其他窗口下面，按 Win+D 显示桌面时也不消失
/// </summary>
static class DesktopHost
{
    public static IntPtr Progman => Native.FindWindow("Progman", null);

    /// <summary>找到系统桌面图标列表窗口（Progman 或某个 WorkerW 下的 SHELLDLL_DefView\SysListView32）。</summary>
    public static IntPtr FindIconList()
    {
        var defView = Native.FindWindowEx(Progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            Native.EnumWindows((h, _) =>
            {
                var dv = Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero) { defView = dv; return false; }
                return true;
            }, IntPtr.Zero);
        }
        return defView == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    static IntPtr _hook;
    static Native.WinEventProc? _hookProc; // 必须保存引用，防止被回收
    static bool _hidden;

    public static void HideSystemIcons()
    {
        _hidden = true;
        var lv = FindIconList();
        if (lv != IntPtr.Zero) Native.ShowWindow(lv, 0 /*SW_HIDE*/);
        // 资源管理器刷新桌面时可能把图标层重新显示出来：监听"窗口显示"事件，出现就再藏起来
        if (_hook == IntPtr.Zero && lv != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(lv, out var pid);
            _hookProc = (h, ev, hwnd, idObj, idChild, th, t) =>
            {
                if (_hidden && idObj == 0 && hwnd == FindIconList()) Native.ShowWindow(hwnd, 0);
            };
            _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW, IntPtr.Zero, _hookProc, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
        }
    }

    public static void ShowSystemIcons()
    {
        _hidden = false;
        if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        var lv = FindIconList();
        if (lv != IntPtr.Zero) Native.ShowWindow(lv, 5 /*SW_SHOW*/);
    }

    /// <summary>资源管理器重启后，旧的句柄和监听都失效了，需要重新来一遍。</summary>
    public static void OnExplorerRestarted(bool hide)
    {
        if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        if (hide) HideSystemIcons();
    }

    /// <summary>
    /// 让窗口变成"桌面挂件"：不进任务栏和 Alt+Tab、点击不抢焦点、永远压在普通窗口下面。
    /// upper：便签这类要贴在分区上面的挂件——仍在所有程序下面，但永远不会被分区、时钟、坞盖住。
    /// </summary>
    public static void Pin(Window w, bool upper = false)
    {
        var h = new WindowInteropHelper(w).Handle;
        var ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
        ex = (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex));
        // 归属于桌面窗口：按 Win+D 时系统把它当作桌面的一部分，不会被藏起来
        Native.SetWindowLongPtr(h, Native.GWLP_HWNDPARENT, Progman);
        _pinned.Add(h);
        if (upper) _upper.Add(h);
        Place(h);
        HwndSource.FromHwnd(h)?.AddHook(BottomHook);
        w.Closed += (_, _) => { _pinned.Remove(h); _upper.Remove(h); };
        StartGuard();
        // 分区是桌面的一部分，不该被最小化（"全部最小化"、任务栏"显示桌面"等），被最小化就立刻还原
        w.StateChanged += (_, _) =>
        {
            if (w.WindowState != WindowState.Minimized) return;
            w.Dispatcher.BeginInvoke(() => { w.WindowState = WindowState.Normal; Repin(w); });
        };
    }

    public static void Repin(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        Native.SetWindowLongPtr(h, Native.GWLP_HWNDPARENT, Progman);
        Place(h);
    }

    static bool _moving; // 我们自己在排顺序：BottomHook 别插手

    static void MoveAfter(IntPtr h, IntPtr after)
    {
        _moving = true;
        try { Native.SetWindowPos(h, after, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE); }
        finally { _moving = false; }
    }

    static void SendToBottom(IntPtr h) => MoveAfter(h, Native.HWND_BOTTOM);

    /// <summary>放回自己该在的位置：普通挂件到最底，便签到分区正上方。</summary>
    static void Place(IntPtr h)
    {
        if (!_upper.Contains(h)) { SendToBottom(h); return; }
        var slot = UpperSlot(h);
        if (slot != null) MoveAfter(h, slot.Value);
    }

    /// <summary>
    /// 便签该插到谁后面：紧贴最上面那个分区（时钟、坞）的上方，这样仍在所有程序下面，又不会被分区盖住。
    /// 返回 null 表示它已经在分区上面了，不用动。
    /// </summary>
    static IntPtr? UpperSlot(IntPtr self)
    {
        IntPtr first = IntPtr.Zero;
        bool selfAbove = false;
        Native.EnumWindows((h, _) =>
        {
            if (h == self) { selfAbove = Native.IsWindowVisible(self); return true; } // 刚建好还没显示的窗口在最顶上，位置不算数
            if (_pinned.Contains(h) && !_upper.Contains(h) && Native.IsWindowVisible(h)) { first = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (first == IntPtr.Zero) return Native.HWND_BOTTOM; // 没有分区：和以前一样待在最底
        if (selfAbove) return null;
        var prev = Native.GetWindow(first, Native.GW_HWNDPREV);
        if (prev == self) return null;
        // 分区上面没有普通窗口（只有任务栏这类置顶窗口）：放到普通窗口的最上面，正好就是分区上方
        if (prev == IntPtr.Zero || ((long)Native.GetWindowLongPtr(prev, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0) return Native.HWND_TOP;
        return prev;
    }

    /// <summary>整组重新排：便签在上、分区等在下，各自原来的上下顺序不变，全部压在程序下面。</summary>
    static void Restack(List<IntPtr> ours)
    {
        foreach (var h in ours.Where(_upper.Contains)) SendToBottom(h);   // 从上往下逐个压底：先压的最后在最上面
        foreach (var h in ours.Where(h => !_upper.Contains(h))) SendToBottom(h);
    }

    // ---------------- 守底 ----------------
    // 偶尔会有别的程序的窗口排到分区下面（比如它最小化时被系统挪到最底层，之后没抢到焦点就自己弹出来），
    // 看起来就是分区盖在程序上面，点一下程序才恢复。
    // 所以：有窗口弹出、最小化/还原、切换前台、切换虚拟桌面时检查一次，另外每秒兜底；
    // 发现有普通窗口在分区下面，就把分区重新压回最底（整组一起动，彼此的上下顺序不变），并记到 zorder.log。
    static readonly HashSet<IntPtr> _pinned = new(), _upper = new();
    static Native.WinEventProc? _guardProc;
    static DispatcherTimer? _guardTimer, _guardSoon;
    static readonly Dictionary<IntPtr, (int Count, DateTime Since)> _stubborn = new();

    static void StartGuard()
    {
        if (_guardTimer != null) return;
        _guardSoon = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _guardSoon.Tick += (_, _) => { _guardSoon.Stop(); KeepAtBottom(); };
        _guardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _guardTimer.Tick += (_, _) => KeepAtBottom();
        _guardTimer.Start();
        _guardProc = (h, ev, hwnd, idObj, idChild, th, t) =>
        {
            if (idObj != 0 || _pinned.Contains(hwnd)) return; // 只管窗口本身；自己的分区被点不用管
            _guardSoon.Stop(); _guardSoon.Start();          // 连续一串事件只检查一次
        };
        Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _guardProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW, IntPtr.Zero, _guardProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        Native.SetWinEventHook(Native.EVENT_OBJECT_UNCLOAKED, Native.EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, _guardProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    // 输入法的候选框/组字窗口、IE 的隐藏窗口等：看着“可见”，其实不是程序界面，不跟它们较劲
    static readonly HashSet<string> IgnoreClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WorkerW", "MSCTFIME UI", "MSCTFIME Composition", "IME", "CiceroUIWndFrame", "Internet Explorer_Hidden",
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>有真正的程序窗口排在分区下面、而且和分区叠在一起（会被盖住），就把分区压回最底。</summary>
    static void KeepAtBottom()
    {
        _pinned.RemoveWhere(h => !Native.IsWindow(h));
        if (_pinned.Count == 0) return;
        var rects = _pinned.Where(Native.IsWindowVisible).Select(h => { Native.GetWindowRect(h, out var r); return r; }).ToList();
        var ours = new List<IntPtr>();
        var lowRects = new List<Native.RECT>();
        IntPtr under = IntPtr.Zero, buried = IntPtr.Zero;
        var progman = Progman;
        var cls = new StringBuilder(64);
        Native.EnumWindows((h, _) =>
        {
            if (_pinned.Contains(h))
            {
                if (!Native.IsWindowVisible(h)) return true;
                ours.Add(h);
                Native.GetWindowRect(h, out var pr);
                if (!_upper.Contains(h)) lowRects.Add(pr);
                else if (buried == IntPtr.Zero && lowRects.Any(f => Overlap(f, pr))) buried = h; // 便签被分区盖住了
                return true;
            }
            if (h == progman) return false;                                       // 桌面后面的本来就被桌面挡着，不归我们管
            if (ours.Count == 0 || under != IntPtr.Zero || !IsCoverable(h, progman, cls)) return true;
            Native.GetWindowRect(h, out var r);
            if (r.Right - r.Left < 40 || r.Bottom - r.Top < 40) return true;     // 太小的不是程序界面
            if (!rects.Any(f => Overlap(f, r))) return true;                      // 没和分区叠在一起，盖不住
            if (IsStubborn(h)) return true;
            under = h;
            return true;
        }, IntPtr.Zero);
        if (under == IntPtr.Zero && buried == IntPtr.Zero) return;
        if (under != IntPtr.Zero) Note(under);
        var fg = Native.GetForegroundWindow(); // 先记下前台是谁，再动顺序
        Restack(ours);
        if (under != IntPtr.Zero) LogUnder(under, fg);
        else Log("便签被分区盖住，已提回分区上面");
    }

    static bool Overlap(Native.RECT a, Native.RECT b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>这个窗口是不是“会被分区盖住的程序界面”。</summary>
    static bool IsCoverable(IntPtr h, IntPtr progman, StringBuilder cls)
    {
        if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return false;   // 最小化的还原时系统会提到最上面；万一没提，还原事件会触发检查
        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return false; // 别的虚拟桌面上的
        var ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
        if ((ex & Native.WS_EX_TOPMOST) != 0) return false;                  // 置顶窗口本来就在分区上面
        if ((ex & Native.WS_EX_TRANSPARENT) != 0) return false;              // 点得穿的透明层
        if (Native.GetWindow(h, Native.GW_OWNER) == progman) return false;   // 别的桌面挂件
        cls.Clear(); Native.GetClassName(h, cls, cls.Capacity);
        return !IgnoreClasses.Contains(cls.ToString());
    }

    // 顺带记下当时的前台窗口：如果总是“本程序的分区”，说明是从分区打开东西时新窗口没抢到前台
    static void LogUnder(IntPtr h, IntPtr fg) =>
        Log($"分区下面有窗口，已压回最底：{Describe(h)}；当时前台：{(_pinned.Contains(fg) ? "本程序的挂件" : Describe(fg))}");

    static string Describe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "无";
        var cls = new StringBuilder(128); Native.GetClassName(h, cls, cls.Capacity);
        var title = new StringBuilder(128); Native.GetWindowText(h, title, title.Capacity);
        Native.GetWindowThreadProcessId(h, out var pid);
        string proc; try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { proc = "?"; }
        return $"{proc} [{cls}] {title}";
    }

    static void Log(string msg)
    {
        try
        {
            var path = System.IO.Path.Combine(Core.AppConfig.DataDir, "zorder.log");
            if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 256 * 1024) System.IO.File.Delete(path);
            System.IO.File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\r\n");
        }
        catch { }
    }

    // 也想待在最底层的窗口（别的桌面美化软件）会和我们来回抢；同一个窗口一分钟里被压过 5 次就不再管它
    static bool IsStubborn(IntPtr h) => _stubborn.TryGetValue(h, out var s) && s.Count >= 5 && DateTime.Now - s.Since < TimeSpan.FromMinutes(1);

    static void Note(IntPtr h)
    {
        var now = DateTime.Now;
        if (_stubborn.TryGetValue(h, out var s) && now - s.Since < TimeSpan.FromMinutes(1)) _stubborn[h] = (s.Count + 1, s.Since);
        else _stubborn[h] = (1, now);
        if (_stubborn.Count > 200) foreach (var k in _stubborn.Where(p => now - p.Value.Since >= TimeSpan.FromMinutes(1)).Select(p => p.Key).ToList()) _stubborn.Remove(k);
    }

    /// <summary>系统想把窗口提到前面时（比如被点击），改成留在最底层。</summary>
    static IntPtr BottomHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0112 && ((int)wParam & 0xFFF0) == 0xF020) { handled = true; return IntPtr.Zero; } // WM_SYSCOMMAND / SC_MINIMIZE：不理
        if (msg == Native.WM_WINDOWPOSCHANGING && !_moving)
        {
            unsafe
            {
                var wp = (Native.WINDOWPOS*)lParam;
                if ((wp->flags & Native.SWP_NOZORDER) != 0) return IntPtr.Zero;
                if (!_upper.Contains(hwnd)) { wp->hwndInsertAfter = Native.HWND_BOTTOM; return IntPtr.Zero; }
                // 便签被点：不沉到分区下面，留在分区正上方
                var slot = UpperSlot(hwnd);
                if (slot == null) wp->flags |= Native.SWP_NOZORDER; else wp->hwndInsertAfter = slot.Value;
            }
        }
        return IntPtr.Zero;
    }
}
