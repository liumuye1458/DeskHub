using System.IO;

namespace DeskHub.Core;

/// <summary>
/// 把超过归档天数没碰过的桌面文件，移到 归档根目录\yyyy-MM\事项\。
/// 每次移动都记日志，可以按批次撤回。只移动"个人桌面"上的文件，
/// 不动文件夹、快捷方式、公用桌面、钉住的文件。
/// </summary>
public sealed class Archiver
{
    readonly AppConfig _cfg;
    readonly StateStore _st;
    public Archiver(AppConfig cfg, StateStore st) { _cfg = cfg; _st = st; }

    public IEnumerable<DesktopItem> Due(IEnumerable<DesktopItem> items) =>
        items.Where(i => i.Zone == Zone.Cold && i.DaysLeft <= 0 && !i.IsPublic && !i.IsDir && !i.IsShortcut && !i.Pinned);

    public sealed record Result(int Moved, List<(string Path, string Error)> Failed, string Batch);

    public Result Run(IEnumerable<DesktopItem> due, DateTime now)
    {
        var batch = now.ToString("yyyyMMdd-HHmmss");
        var failed = new List<(string, string)>();
        int moved = 0;
        foreach (var it in due.ToList())
        {
            try
            {
                var dir = Path.Combine(_cfg.ArchiveRoot, now.ToString("yyyy-MM"), SafeName(it.Topic));
                Directory.CreateDirectory(dir);
                var dest = Unique(Path.Combine(dir, it.Name));
                File.Move(it.Path, dest); // 跨盘时系统会自动"复制+删除"
                _st.ArchiveLog.Add(new ArchiveRecord { Time = now, From = it.Path, To = dest, Topic = it.Topic, Batch = batch });
                _st.Items.Remove(it.Path);
                moved++;
            }
            catch (Exception ex)
            {
                failed.Add((it.Path, ex is IOException ? L.T("文件正被别的程序打开") : ex.Message)); // 下次再试
            }
        }
        _st.Save();
        return new Result(moved, failed, batch);
    }

    /// <summary>撤回一个批次：把文件搬回桌面原位置。原位置已被占用就加编号。</summary>
    public (int ok, int fail) Undo(string batch)
    {
        int ok = 0, fail = 0;
        foreach (var r in _st.ArchiveLog.Where(r => r.Batch == batch && !r.Undone))
        {
            try
            {
                if (!File.Exists(r.To)) { r.Undone = true; fail++; continue; }
                var back = Unique(r.From);
                File.Move(r.To, back);
                r.Undone = true;
                // 搬回来算"刚碰过"，避免明天又被归档
                _st.Items[back] = new ItemMeta { FirstSeen = DateTime.Now, Topic = r.Topic };
                ok++;
            }
            catch { fail++; }
        }
        _st.Save();
        return (ok, fail);
    }

    static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? AppConfig.OtherTopic : s.Trim();
    }
}
