using System.IO;
using System.Text.Json;

namespace DeskHub.Core;

/// <summary>工具自己记住的、每个桌面文件的信息（按完整路径，不区分大小写）。</summary>
public sealed class ItemMeta
{
    /// <summary>工具第一次看到它出现在桌面的时间。</summary>
    public DateTime FirstSeen { get; set; }
    /// <summary>通过本工具最后一次打开的时间。</summary>
    public DateTime? LastOpened { get; set; }
    /// <summary>用户手动指定的事项（拖进某个事项后固定下来）。</summary>
    public string? Topic { get; set; }
    /// <summary>用户手动钉住：不再参与流转和归档。</summary>
    public bool Pinned { get; set; }
    /// <summary>
    /// 用户手动拖到的分区（优先于自动流转）。
    /// 今天：只当天有效；进行中（文件）：只当天有效，之后按"刚碰过"正常流转；
    /// 冷却：文件再被打开或修改就失效；常用软件 / 项目文件夹：一直有效，直到再拖走。
    /// </summary>
    public Zone? Placed { get; set; }
    /// <summary>放进了哪个自定义分区（分区 Id）。放进去就一直留着，不流转、不归档，直到拖出来或分区被删。</summary>
    public string? Fence { get; set; }
    public DateTime? PlacedAt { get; set; }
}

/// <summary>一次归档移动的记录，用于撤回。</summary>
public sealed class ArchiveRecord
{
    public DateTime Time { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Topic { get; set; } = "";
    public string Batch { get; set; } = "";
    public bool Undone { get; set; }
}

public sealed class StateStore
{
    public Dictionary<string, ItemMeta> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ArchiveRecord> ArchiveLog { get; set; } = new();

    static string FilePath => Path.Combine(AppConfig.DataDir, "state.json");

    public static StateStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<StateStore>(File.ReadAllText(FilePath), AppConfig.Json);
                if (s != null)
                {
                    s.Items = new Dictionary<string, ItemMeta>(s.Items, StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
        }
        catch { }
        return new StateStore();
    }

    readonly object _gate = new();
    public void Save()
    {
        lock (_gate)
        {
            // 撤回记录只留 180 天，避免文件无限增长
            var cutoff = DateTime.Now.AddDays(-180);
            ArchiveLog.RemoveAll(r => r.Time < cutoff);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppConfig.Json));
        }
    }

    public ItemMeta Get(string path, DateTime firstSeenGuess)
    {
        if (!Items.TryGetValue(path, out var m))
        {
            m = new ItemMeta { FirstSeen = firstSeenGuess };
            Items[path] = m;
        }
        return m;
    }

    /// <summary>文件改名/移走后，把旧路径的记录搬到新路径。</summary>
    public void Rename(string oldPath, string newPath)
    {
        if (Items.Remove(oldPath, out var m)) Items[newPath] = m;
    }

    /// <summary>清掉早已不在桌面上的记录。</summary>
    public void Prune(IEnumerable<string> alive)
    {
        var set = new HashSet<string>(alive, StringComparer.OrdinalIgnoreCase);
        foreach (var k in Items.Keys.Where(k => !set.Contains(k)).ToList()) Items.Remove(k);
    }
}
