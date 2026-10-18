using System.IO;
using System.Text;
using System.Text.Json;

namespace DeskHub.Core;

public sealed class MemoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public DateTime? DoneAt { get; set; }
    /// <summary>0 无，1 红（紧急），2 黄（重要），3 蓝（一般）。</summary>
    public int Priority { get; set; }
    public DateTime? Remind { get; set; }
    /// <summary>"" 不重复 / day 每天 / workday 工作日 / week 每周 / month 每月。</summary>
    public string Repeat { get; set; } = "";
    /// <summary>这次提醒已经弹过了（不重复的提醒弹一次就够）。</summary>
    public bool Reminded { get; set; }
    public string? File { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
}

public sealed class MemoList
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>钉到桌面，显示成便签。</summary>
    public bool Pinned { get; set; }
    public List<MemoItem> Items { get; set; } = new();
}

/// <summary>备忘录数据（memo.json，不加密；要保密的东西请放密码箱）。</summary>
public sealed class MemoStore
{
    public List<MemoList> Lists { get; set; } = new();

    static string FilePath => Path.Combine(AppConfig.DataDir, "memo.json");
    static MemoStore? _inst;
    public static MemoStore I => _inst ??= Load();

    /// <summary>任何地方改了数据都发这个事件，备忘录窗口和桌面便签一起刷新。</summary>
    public static event Action? Changed;

    static MemoStore Load()
    {
        try
        {
            if (System.IO.File.Exists(FilePath))
                return JsonSerializer.Deserialize<MemoStore>(System.IO.File.ReadAllText(FilePath), AppConfig.Json) ?? Default();
        }
        catch { }
        return Default();
    }

    static MemoStore Default() => new()
    {
        Lists = { new MemoList { Name = L.T("今日待办") }, new MemoList { Name = L.T("本周") }, new MemoList { Name = L.T("灵感记录") } },
    };

    public void Save()
    {
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppConfig.Json));
        Changed?.Invoke();
    }

    public MemoList? Find(string id) => Lists.FirstOrDefault(l => l.Id == id);

    /// <summary>待办文字统一换行（Windows 的 \r\n 在界面上会显示成两行），去掉首尾空白。</summary>
    public static string Clean(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    public static string RepeatName(string r) => r switch { "day" => L.T("每天"), "workday" => L.T("工作日"), "week" => L.T("每周"), "month" => L.T("每月"), _ => "" };

    /// <summary>重复提醒：算出下一次（晚于 now）。</summary>
    public static DateTime Next(DateTime t, string repeat, DateTime now)
    {
        do
        {
            t = repeat switch
            {
                "day" => t.AddDays(1),
                "workday" => t.AddDays(t.DayOfWeek == DayOfWeek.Friday ? 3 : t.DayOfWeek == DayOfWeek.Saturday ? 2 : 1),
                "week" => t.AddDays(7),
                "month" => t.AddMonths(1),
                _ => now.AddYears(100),
            };
        } while (t <= now);
        return t;
    }

    /// <summary>到点的提醒（每 30 秒查一次）。</summary>
    public List<(MemoList List, MemoItem Item)> Due(DateTime now) =>
        Lists.SelectMany(l => l.Items.Where(i => !i.Done && !i.Reminded && i.Remind is { } r && r <= now).Select(i => (l, i))).ToList();

    /// <summary>提醒弹过以后：重复的排到下一次，不重复的标记已提醒。</summary>
    public void Fired(MemoItem it, DateTime now)
    {
        if (it.Repeat.Length > 0 && it.Remind is { } r) it.Remind = Next(r, it.Repeat, now);
        else it.Reminded = true;
    }

    public void SetDone(MemoItem it, bool done)
    {
        it.Done = done;
        it.DoneAt = done ? DateTime.Now : null;
        // 重复的待办：勾完成后自动生成下一次
        if (done && it.Repeat.Length > 0 && it.Remind is { } r)
        {
            var list = Lists.First(l => l.Items.Contains(it));
            list.Items.Add(new MemoItem { Text = it.Text, Priority = it.Priority, Repeat = it.Repeat, File = it.File, Remind = Next(r, it.Repeat, DateTime.Now) });
            it.Repeat = "";
        }
    }

    public static string Export(MemoList l)
    {
        var sb = new StringBuilder($"# {l.Name}\n\n");
        foreach (var i in l.Items.OrderBy(i => i.Done))
        {
            sb.Append(i.Done ? "- [x] " : "- [ ] ").Append(i.Text);
            if (i.Remind is { } r) sb.Append(L.F("  （提醒 {0:yyyy-MM-dd HH:mm}{1}）", r, (i.Repeat.Length > 0 ? " " + RepeatName(i.Repeat) : "")));
            if (i.File != null) sb.Append(L.F("  （文件：{0}）", i.File));
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
