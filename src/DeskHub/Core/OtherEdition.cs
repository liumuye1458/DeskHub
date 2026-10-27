using System.Diagnostics;
using System.Windows;

namespace DeskHub.Core;

/// <summary>
/// 专用版 HOTA DESK（公司内部用，单独的仓库）和本程序可以同时装，但不能同时开：两个都会接管桌面（隐藏系统图标、画分区），全局快捷键也会互相抢。
/// 本程序启动时如果 HOTA DESK 正开着，问用户要不要关掉它、改开本程序。专用版那边有对称的一份（Biz/OtherEdition.cs）。
/// </summary>
static class OtherEdition
{
    const string Name = "New B Desk";
    const string OtherName = "HOTA DESK";

    /// <summary>返回 false 表示不启动（用户不让关，或者关不掉）。</summary>
    public static bool MakeWay() => MakeWay(@"Local\HotaDesk.Single", @"Local\HotaDesk.Exit", "HotaDesk", ask: true);

    /// <summary>ask = false 是测试用（沙盒脚本 edition）：换成测试用的名字，不弹窗，当作用户点了“是”。</summary>
    internal static bool MakeWay(string mutex, string exitSignal, string? process, bool ask)
    {
        if (!Running(mutex)) return true;
        if (ask && MessageBox.Show(L.F("{0} 正在运行。两个版本都会接管桌面，不能同时开着。\n\n要关掉 {0}、改开 {1} 吗？", OtherName, Name),
                Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;

        // 和安装程序关程序一样：先发退出信号让它自己退（会恢复系统桌面图标、保存状态），10 秒没退再强制结束
        try { using var ev = EventWaitHandle.OpenExisting(exitSignal); ev.Set(); } catch { }
        if (!WaitGone(mutex, 10000) && process != null)
            foreach (var p in Process.GetProcessesByName(process)) try { p.Kill(true); p.WaitForExit(3000); } catch { }
        if (WaitGone(mutex, 3000))
        {
            if (process != null) foreach (var p in Process.GetProcessesByName(process)) try { p.WaitForExit(5000); } catch { }
            return true;
        }
        if (ask) MessageBox.Show(L.F("没能关掉 {0}。请在它的托盘图标上点“退出（恢复系统桌面）”，再打开 {1}。", OtherName, Name),
                Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>对方开着时一直拿着这个互斥量，进程一退出就没了。对方以管理员身份运行时打不开，但也说明它开着。</summary>
    static bool Running(string mutex)
    {
        try
        {
            if (!Mutex.TryOpenExisting(mutex, out var m)) return false;
            m.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    static bool WaitGone(string mutex, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (Running(mutex)) { if (sw.ElapsedMilliseconds > ms) return false; Thread.Sleep(200); }
        return true;
    }
}
