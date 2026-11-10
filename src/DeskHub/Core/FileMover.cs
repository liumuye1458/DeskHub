using System.IO;
using VbFs = Microsoft.VisualBasic.FileIO.FileSystem;

namespace DeskHub.Core;

/// <summary>要挪的一个文件（或文件夹）：挪到哪个文件夹由调用方定。Topic 是它属于的事项（撤回时还原，可以不填）。</summary>
public sealed record MoveItem(string Path, string ToDir, string? Topic = null);

/// <summary>
/// 把一批文件挪到调用方指定的文件夹，每一批记进“移动记录”，可以按批撤回。
/// 归档、分区标题栏上的按钮（Edition 里配的）都用它，记录在一个地方，设置 →“移动记录”里统一撤回。
///   目标里已经有同名的：加编号（“报价 (2).xlsx”），不覆盖。
///   正被别的程序打开、没权限的：跳过，放进结果里告诉调用方，下次再试。
/// </summary>
public sealed class FileMover
{
    readonly StateStore _st;
    public FileMover(StateStore st) { _st = st; }

    /// <summary>Batch：这一批的编号（撤回用）；Moved：挪成功几个；Skipped：没挪的（路径，原因）。</summary>
    public sealed record Result(string Batch, int Moved, List<(string Path, string Error)> Skipped);

    /// <summary>挪一批。label 显示在移动记录里，说明这批是干什么的（例如“归档”）。</summary>
    public Result Move(IEnumerable<MoveItem> items, string label)
    {
        var now = DateTime.Now;
        var batch = now.ToString("yyyyMMdd-HHmmss-fff");
        var skipped = new List<(string, string)>();
        int moved = 0;
        foreach (var it in items.ToList())
        {
            try
            {
                Directory.CreateDirectory(it.ToDir);
                var dest = Unique(System.IO.Path.Combine(it.ToDir, System.IO.Path.GetFileName(it.Path.TrimEnd('\\'))));
                MoveOne(it.Path, dest);
                _st.MoveLog.Add(new MoveRecord { Time = now, From = it.Path, To = dest, Topic = it.Topic, Batch = batch, Label = label });
                _st.Items.Remove(it.Path);
                moved++;
            }
            catch (Exception ex)
            {
                skipped.Add((it.Path, ex is IOException ? L.T("文件正被别的程序打开") : ex.Message));
            }
        }
        _st.Save();
        return new Result(batch, moved, skipped);
    }

    /// <summary>撤回一批：搬回原来的位置（原位置被占了就加编号）。返回搬回几个、几个没搬回（已经被挪走或删掉）。</summary>
    public (int ok, int fail) Undo(string batch)
    {
        int ok = 0, fail = 0;
        foreach (var r in _st.MoveLog.Where(r => r.Batch == batch && !r.Undone))
        {
            try
            {
                if (!File.Exists(r.To) && !Directory.Exists(r.To)) { r.Undone = true; fail++; continue; }
                var back = Unique(r.From);
                MoveOne(r.To, back);
                r.Undone = true;
                // 搬回来算"刚碰过"，免得马上又被挪走
                _st.Items[back] = new ItemMeta { FirstSeen = DateTime.Now, Topic = r.Topic };
                ok++;
            }
            catch { fail++; }
        }
        _st.Save();
        return (ok, fail);
    }

    /// <summary>移动记录，按批分好，新的在前。</summary>
    public IEnumerable<IGrouping<string, MoveRecord>> Batches() =>
        _st.MoveLog.GroupBy(r => r.Batch).OrderByDescending(g => g.First().Time).ThenByDescending(g => g.Key);

    /// <summary>最近一批还没撤回的（测试用）。</summary>
    public string? LastBatch() => _st.MoveLog.Where(r => !r.Undone).Select(r => r.Batch).LastOrDefault();

    static void MoveOne(string from, string to)
    {
        if (Directory.Exists(from)) VbFs.MoveDirectory(from, to); // 跨盘也能挪
        else File.Move(from, to);                                 // 跨盘时系统会自动"复制+删除"
    }

    static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = Directory.Exists(path) ? "" : System.IO.Path.GetExtension(path);
        if (ext.Length == 0) stem = System.IO.Path.GetFileName(path);
        for (int i = 2; ; i++)
        {
            var p = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }
}
