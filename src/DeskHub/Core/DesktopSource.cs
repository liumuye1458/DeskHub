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
            if (it.IsShortcut) continue;
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
