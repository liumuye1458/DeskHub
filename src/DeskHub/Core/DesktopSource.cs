using System.IO;

namespace DeskHub.Core;

/// <summary>
/// 读取桌面文件，并监听变化。只在文件真的变了才通知（系统推送，不轮询）。
/// 连续多次变化会合并成一次（300 毫秒防抖），避免复制一批文件时刷新几十遍。
/// </summary>
public sealed class DesktopSource : IDisposable
{
    public string UserDesktop { get; }
    public string PublicDesktop { get; }
    public string RecentDir { get; }

    public event Action? Changed;
    public event Action<string, string>? Renamed;

    readonly List<FileSystemWatcher> _watchers = new();
    readonly System.Threading.Timer _debounce;

    public DesktopSource(string userDesktop, string publicDesktop, string recentDir)
    {
        UserDesktop = userDesktop;
        PublicDesktop = publicDesktop;
        RecentDir = recentDir;
        _debounce = new System.Threading.Timer(_ => Changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
        Watch(UserDesktop);
        Watch(PublicDesktop);
        Watch(RecentDir); // 别的程序打开桌面文件时，Windows 会在这里留一个快捷方式
        // 回收站：删文件、清空回收站时这里会变，回收站图标要跟着换（空 / 有东西）
        foreach (var dir in RecycleDirs()) Watch(dir);
    }

    /// <summary>每个本地硬盘上当前用户的回收站目录（X:\$Recycle.Bin\用户SID），没用过的盘没有这个目录。</summary>
    static IEnumerable<string> RecycleDirs()
    {
        string? sid = null;
        try { sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value; } catch { }
        if (sid == null) yield break;
        foreach (var d in DriveInfo.GetDrives())
        {
            bool ok;
            try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; }
            if (ok) yield return Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", sid);
        }
    }

    /// <summary>
    /// 回收站图标按实际空 / 满自己选：系统在同一个程序里会把回收站图标缓存住，清空后还显示满的。
    /// 图标位置取 Windows 登记的 empty / full（换过主题图标的也跟着走）。
    /// </summary>
    static string RecycleIcon()
    {
        var name = RecycleEmpty() ? "empty" : "full";
        string? v = null;
        foreach (var (root, sub) in new[] { (Microsoft.Win32.Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\CLSID\" + RecycleBin + @"\DefaultIcon"),
                                            (Microsoft.Win32.Registry.ClassesRoot, @"CLSID\" + RecycleBin + @"\DefaultIcon") })
        {
            try { using var k = root.OpenSubKey(sub); v = k?.GetValue(name) as string; } catch { }
            if (!string.IsNullOrWhiteSpace(v)) break;
        }
        if (string.IsNullOrWhiteSpace(v)) v = name == "empty" ? @"%SystemRoot%\System32\imageres.dll,-55" : @"%SystemRoot%\System32\imageres.dll,-54";
        return "@" + v;
    }

    /// <summary>回收站是否为空。</summary>
    static bool RecycleEmpty()
    {
        var info = new Interop.Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Interop.Native.SHQUERYRBINFO>() };
        try { return Interop.Native.SHQueryRecycleBin(null, ref info) != 0 || info.i64NumItems == 0; } catch { return true; }
    }

    void Watch(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var w = new FileSystemWatcher(dir)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            InternalBufferSize = 32 * 1024,
        };
        w.Created += (_, _) => Poke();
        w.Deleted += (_, _) => Poke();
        w.Changed += (_, _) => Poke();
        w.Renamed += (_, e) => { Renamed?.Invoke(e.OldFullPath, e.FullPath); Poke(); };
        w.Error += (_, _) => Poke();
        w.EnableRaisingEvents = true;
        _watchers.Add(w);
    }

    void Poke() => _debounce.Change(300, Timeout.Infinite);

    static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "thumbs.db", ".ds_store" };

    public static bool ShouldSkip(string path, FileAttributes attr)
    {
        var name = Path.GetFileName(path);
        if (SkipNames.Contains(name)) return true;
        if (name.StartsWith("~$")) return true;                       // Office 打开文件时的临时锁文件
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return true; // 下载中 / 临时文件
        if (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System)) return true;
        return false;
    }

    public List<DesktopItem> Scan()
    {
        var list = new List<DesktopItem>();
        Add(list, UserDesktop, false);
        Add(list, PublicDesktop, true);
        list.AddRange(SystemIcons());
        return list;
    }

    /// <summary>
    /// 系统桌面图标（此电脑、回收站……）。它们不是文件，扫目录看不到；系统图标层被藏起来后要由分区来显示。
    /// 显示哪几个跟 Windows“桌面图标设置”一致：HideDesktopIcons\NewStartPanel 里值为 0 的显示，没设置过的只有回收站默认显示。
    /// </summary>
    const string RecycleBin = "{645FF040-5081-101B-9F08-00AA002F954E}";

    static readonly (string Clsid, bool Default, string Fallback)[] SystemList =
    {
        ("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", false, "此电脑"),
        ("{59031a47-3f72-44a7-89c5-5595fe6b30ee}", false, "用户的文件"),
        ("{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", false, "网络"),
        ("{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", false, "控制面板"),
        ("{645FF040-5081-101B-9F08-00AA002F954E}", true, "回收站"),
    };

    static IEnumerable<DesktopItem> SystemIcons()
    {
        Microsoft.Win32.RegistryKey? k = null;
        try { k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel"); } catch { }
        var list = new List<DesktopItem>();
        foreach (var (id, def, fallback) in SystemList)
        {
            bool show = def;
            try { if (k?.GetValue(id) is int v) show = v == 0; } catch { }
            if (!show) continue;
            var path = "::" + id;
            list.Add(new DesktopItem { Path = path, IsDir = false, IsPublic = true, IsSystem = true, Label = Interop.ShellIcons.DisplayName(path) ?? fallback, IconRef = id == RecycleBin ? RecycleIcon() : null });
        }
        k?.Dispose();
        return list;
    }

    static void Add(List<DesktopItem> list, string dir, bool isPublic)
    {
        if (!Directory.Exists(dir)) return;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); } catch { return; }
        foreach (var p in entries)
        {
            FileAttributes a;
            try { a = File.GetAttributes(p); } catch { continue; }
            if (ShouldSkip(p, a)) continue;
            var it = DesktopItem.From(p, isPublic);
            if (it != null) list.Add(it);
        }
    }

    /// <summary>
    /// 从"最近使用"里找桌面文件的最后打开时间。
    /// Windows 打开文件时会在 Recent 目录写一个"文件名.lnk"，按名字对上后再核对它真指向这个文件。
    /// </summary>
    public Dictionary<string, DateTime> RecentOpens(IEnumerable<DesktopItem> items)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(RecentDir)) return result;
        foreach (var it in items)
        {
            if (it.IsShortcut || it.IsSystem) continue;
            var lnk = Path.Combine(RecentDir, it.Name + ".lnk");
            try
            {
                if (!File.Exists(lnk)) continue;
                var t = File.GetLastWriteTime(lnk);
                var target = Interop.ShellLink.Target(lnk);
                if (target == null || string.Equals(target, it.Path, StringComparison.OrdinalIgnoreCase))
                    result[it.Path] = t;
            }
            catch { }
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _debounce.Dispose();
    }
}
