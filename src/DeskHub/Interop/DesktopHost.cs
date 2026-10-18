using System.Windows;
using System.Windows.Interop;

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

    /// <summary>让窗口变成"桌面挂件"：不进任务栏和 Alt+Tab、点击不抢焦点、永远压在普通窗口下面。</summary>
    public static void Pin(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        var ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
        ex = (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex));
        // 归属于桌面窗口：按 Win+D 时系统把它当作桌面的一部分，不会被藏起来
        Native.SetWindowLongPtr(h, Native.GWLP_HWNDPARENT, Progman);
        SendToBottom(h);
        HwndSource.FromHwnd(h)?.AddHook(BottomHook);
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
        SendToBottom(h);
    }

    static void SendToBottom(IntPtr h) =>
        Native.SetWindowPos(h, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    /// <summary>系统想把窗口提到前面时（比如被点击），改成留在最底层。</summary>
    static IntPtr BottomHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0112 && ((int)wParam & 0xFFF0) == 0xF020) { handled = true; return IntPtr.Zero; } // WM_SYSCOMMAND / SC_MINIMIZE：不理
        if (msg == Native.WM_WINDOWPOSCHANGING)
        {
            unsafe
            {
                var wp = (Native.WINDOWPOS*)lParam;
                if ((wp->flags & Native.SWP_NOZORDER) == 0) wp->hwndInsertAfter = Native.HWND_BOTTOM;
            }
        }
        return IntPtr.Zero;
    }
}
