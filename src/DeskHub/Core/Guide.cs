using System.IO;
using System.Text.Json;
using System.Windows;

namespace DeskHub.Core;

/// <summary>
/// 一张贴在屏幕上的指引便签：对话气泡样子，左边一个小尖角指着旁边的东西 + 一个 OK。
/// 用途：给自己和直播间 MC 做“操作提示”，例如放在某个图标右边写“第一步：先打开它”。
/// </summary>
public sealed class GuideNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Text { get; set; } = "";
    /// <summary>yellow / red / green / blue</summary>
    public string Color { get; set; } = "yellow";
    public double FontSize { get; set; } = 18;

    // 位置（设备无关像素，屏幕坐标）：便签左上角和宽度。Arrow = 左边带小尖角
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 280;
    public bool Arrow { get; set; } = true;
    /// <summary>0.6.0 长箭头指的点，已不用（留着读旧数据不报错）。</summary>
    public double TipX { get; set; }
    public double TipY { get; set; }

    /// <summary>浮在所有窗口上面（指软件里的按钮时用）；否则贴在桌面上。</summary>
    public bool Topmost { get; set; }
    /// <summary>每天重复：点 OK 后今天不再显示，第二天（凌晨 5 点后）重新出现。否则一次性：点 OK 后永久收起。</summary>
    public bool Daily { get; set; } = true;
    /// <summary>每天重复的：哪一天点过 OK（按“凌晨 5 点算新的一天”）。</summary>
    public string? OkDay { get; set; }
    /// <summary>一次性的：已经点过 OK。</summary>
    public bool Finished { get; set; }
    /// <summary>定时出现："HH:mm"，到这个时间才显示；空 = 一直显示。</summary>
    public string ShowAt { get; set; } = "";
    /// <summary>流程名：同一流程的便签按 Step 一步一步出现（前一步点了 OK 才出下一步）。空 = 不属于流程。</summary>
    public string Flow { get; set; } = "";
    public int Step { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
}

public sealed class GuideData
{
    /// <summary>锁定：便签拖不动、改不了字，只能点 OK（防 MC 误改）。</summary>
    public bool Locked { get; set; }
    public List<GuideNote> Notes { get; set; } = new();
}

/// <summary>便签的存取、显示规则、导出导入。数据存在 guides.json。</summary>
public static class GuideStore
{
    public static GuideData D { get; private set; } = new();
    public static event Action? Changed;
    static string FilePath => Path.Combine(AppConfig.DataDir, "guides.json");

    /// <summary>每天凌晨 5 点算新的一天（直播常常过了半夜才结束，不能一过 0 点就把便签都弹回来）。</summary>
    public const int DayStartHour = 5;
    public static string DayOf(DateTime t) => t.AddHours(-DayStartHour).ToString("yyyy-MM-dd");

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath)) D = JsonSerializer.Deserialize<GuideData>(File.ReadAllText(FilePath), AppConfig.Json) ?? new();
            else MigrateOldMemo();
        }
        catch { D = new(); }
    }

    public static void Save()
    {
        try { AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(D, AppConfig.Json)); } catch { }
        Changed?.Invoke();
    }

    public static GuideNote? Find(string id) => D.Notes.FirstOrDefault(n => n.Id == id);

    // ================= 显示规则 =================
    public enum State { Showing, OkToday, Finished, Waiting, AfterPrev }

    public static State StateOf(GuideNote n, DateTime now)
    {
        if (n.Finished) return State.Finished;
        if (n.Daily && n.OkDay == DayOf(now)) return State.OkToday;
        if (ShowTime(n, now) is { } t && now < t) return State.Waiting;
        if (n.Flow.Length > 0 && D.Notes.Any(o => o != n && o.Flow == n.Flow && o.Step < n.Step && Pending(o, now))) return State.AfterPrev;
        return State.Showing;
    }

    /// <summary>今天还没做完（流程里挡着后面的步骤）。</summary>
    static bool Pending(GuideNote n, DateTime now) => !n.Finished && !(n.Daily && n.OkDay == DayOf(now));

    /// <summary>今天这张便签的出现时间（凌晨 0～5 点的时间算“今天”的深夜）。</summary>
    public static DateTime? ShowTime(GuideNote n, DateTime now)
    {
        if (!TimeSpan.TryParse(n.ShowAt, out var t)) return null;
        var dayStart = DateTime.Parse(DayOf(now)).AddHours(DayStartHour);
        var at = dayStart.Date + t;
        if (t.TotalHours < DayStartHour) at = at.AddDays(1);
        return at;
    }

    public static bool Visible(GuideNote n, DateTime now) => StateOf(n, now) == State.Showing;

    /// <summary>点了 OK（3 秒倒计时结束后）。</summary>
    public static void Ok(GuideNote n)
    {
        if (n.Daily) n.OkDay = DayOf(DateTime.Now); else n.Finished = true;
        Save();
    }

    /// <summary>让便签重新显示（撤回今天的 OK / 一次性的重新打开）。</summary>
    public static void Reopen(GuideNote n)
    {
        n.OkDay = null; n.Finished = false;
        Save();
    }

    /// <summary>同一流程里下一个步骤号。</summary>
    public static int NextStep(string flow) => flow.Length == 0 ? 0 : D.Notes.Where(n => n.Flow == flow).Select(n => n.Step).DefaultIfEmpty(0).Max() + 1;

    public static IEnumerable<string> Flows => D.Notes.Select(n => n.Flow).Where(f => f.Length > 0).Distinct();

    // ================= 导出 / 导入 =================
    sealed class ExportFile
    {
        public string App { get; set; } = "HOTA DESK";
        public int Version { get; set; } = 1;
        public double WaX { get; set; }
        public double WaY { get; set; }
        public double WaW { get; set; }
        public double WaH { get; set; }
        public List<GuideNote> Notes { get; set; } = new();
    }

    public static void Export(string path, Rect wa)
    {
        var f = new ExportFile { WaX = wa.X, WaY = wa.Y, WaW = wa.Width, WaH = wa.Height, Notes = D.Notes };
        File.WriteAllText(path, JsonSerializer.Serialize(f, AppConfig.Json));
    }

    /// <summary>导入：replace=替换现有的，否则追加。屏幕大小不同时位置按比例换算。返回导入的条数。</summary>
    public static int Import(string path, Rect wa, bool replace)
    {
        var f = JsonSerializer.Deserialize<ExportFile>(File.ReadAllText(path), AppConfig.Json) ?? throw new InvalidDataException();
        double sx = f.WaW > 0 ? wa.Width / f.WaW : 1, sy = f.WaH > 0 ? wa.Height / f.WaH : 1;
        var list = f.Notes.Select(n =>
        {
            n.Id = Guid.NewGuid().ToString("N")[..12];
            n.X = wa.X + (n.X - f.WaX) * sx; n.Y = wa.Y + (n.Y - f.WaY) * sy;
            n.TipX = wa.X + (n.TipX - f.WaX) * sx; n.TipY = wa.Y + (n.TipY - f.WaY) * sy;
            n.OkDay = null; n.Finished = false;
            return n;
        }).ToList();
        if (replace) D.Notes.Clear();
        D.Notes.AddRange(list);
        Save();
        return list.Count;
    }

    // ================= 旧备忘录迁移 =================
    /// <summary>
    /// 旧版的清单式备忘录（memo.json）：没完成的待办各变成一张一次性便签（不带箭头），放在屏幕右上角。
    /// 旧文件改名 memo.old.json 留底。
    /// </summary>
    static void MigrateOldMemo()
    {
        var old = Path.Combine(AppConfig.DataDir, "memo.json");
        if (!File.Exists(old)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(old));
            var wa = SystemParameters.WorkArea;
            int i = 0;
            foreach (var l in doc.RootElement.GetProperty("Lists").EnumerateArray())
            {
                foreach (var it in l.GetProperty("Items").EnumerateArray())
                {
                    if (it.TryGetProperty("Done", out var d) && d.GetBoolean()) continue;
                    var text = it.GetProperty("Text").GetString() ?? "";
                    if (text.Trim().Length == 0) continue;
                    D.Notes.Add(new GuideNote
                    {
                        Text = text, Daily = false, Arrow = false,
                        X = wa.Right - 320 - i * 24, Y = wa.Top + 40 + i * 24,
                    });
                    i++;
                }
            }
            File.Move(old, Path.Combine(AppConfig.DataDir, "memo.old.json"), true);
            Save();
        }
        catch { }
    }
}
