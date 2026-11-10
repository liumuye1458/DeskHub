using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DeskHub.Interop;

/// <summary>
/// 点工具条上的软件按钮：已经开着就切过去，没开才启动。
///   按 exe 的完整路径找正在运行的进程；有窗口 → 切到前面（最小化的先还原）；
///   在运行但没有窗口（只在托盘里）→ 不再启动一个，返回 RunningNoWindow，由调用方给一句轻提示；
///   没在运行 → 启动。
///   应用商店的应用、带参数的快捷方式（同一个程序不同参数）、网址等找不准是不是“同一个”，直接启动。
/// </summary>
public static class AppLauncher
{
    public enum Result { Focused, RunningNoWindow, Launched, Failed }

    public static Result Open(string path, out string? error)
    {
        error = null;
        try
        {
            var (exe, args) = Resolve(path);
            if (exe != null && string.IsNullOrWhiteSpace(args))
            {
                var pids = ProcessesOf(exe);
                if (pids.Count > 0)
                {
                    var w = MainWindowOf(pids);
                    if (w == IntPtr.Zero) return Result.RunningNoWindow;
                    if (Native.IsIconic(w)) Native.ShowWindow(w, 9 /*SW_RESTORE*/);
                    Native.SetForegroundWindow(w);
                    return Result.Focused;
                }
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = WorkDir(exe ?? path) });
            return Result.Launched;
        }
        catch (Exception ex) { error = ex.Message; return Result.Failed; }
    }

    static string WorkDir(string p) { try { return Path.GetDirectoryName(p) ?? ""; } catch { return ""; } }

    /// <summary>快捷方式 → 目标 exe 和参数；本身是 exe → 它自己；其余（商店应用、网址、文档）→ null。</summary>
    public static (string? Exe, string? Args) Resolve(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".exe") return (path, null);
        if (ext != ".lnk") return (null, null);
        var (target, args) = ReadLink(path);
        return target != null && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target) ? (target, args) : (null, null);
    }

    /// <summary>按 exe 的完整路径找进程（先按进程名筛，再比路径）。</summary>
    static HashSet<uint> ProcessesOf(string exe)
    {
        var set = new HashSet<uint>();
        var full = Path.GetFullPath(exe);
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (p)
            {
                var img = ImagePath(p.Id);
                if (img != null && string.Equals(Path.GetFullPath(img), full, StringComparison.OrdinalIgnoreCase)) set.Add((uint)p.Id);
            }
        }
        return set;
    }

    /// <summary>这些进程的主窗口：看得见（最小化也算）、没被隐藏、不是别的窗口的附属、不是工具小窗。</summary>
    static IntPtr MainWindowOf(HashSet<uint> pids)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid) || !Native.IsWindowVisible(h)) return true;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            if (((long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out var cloaked, 4) == 0 && cloaked != 0) return true;
            found = h;
            return false; // EnumWindows 按前后顺序，第一个就是最上面的
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder s, ref int size);
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    static string? ImagePath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); int n = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString(0, n) : null;
        }
        finally { CloseHandle(h); }
    }

    // ---------- 读快捷方式的目标和参数 ----------
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        // 顺序必须和系统的一致，声明到 GetArguments 为止
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string s);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string s);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int cch);
    }

    static (string? Target, string? Args) ReadLink(string lnk)
    {
        object? o = null;
        try
        {
            o = new CShellLink();
            ((IPersistFile)o).Load(lnk, 0);
            var link = (IShellLinkW)o;
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /*SLGP_RAWPATH*/);
            var target = sb.ToString();
            var ab = new StringBuilder(2048);
            link.GetArguments(ab, ab.Capacity);
            return (string.IsNullOrWhiteSpace(target) ? null : Environment.ExpandEnvironmentVariables(target), ab.ToString());
        }
        catch { return (null, null); }
        finally { if (o != null) Marshal.ReleaseComObject(o); }
    }
}
