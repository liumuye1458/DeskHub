namespace DeskHub.Core;

/// <summary>
/// 决定每个桌面项放在哪个分区、属于哪个事项。
/// 规则：
///   快捷方式 → 常用软件；文件夹 → 项目文件夹（这两类不流转）
///   以下要有事项分类和流转（FEAT_FLOW）；没有时散放的文件都进“文件”（Zone.Files）：
///   今天第一次出现在桌面 → 今天
///   最后碰过 &lt; 冷却天数 → 进行中（按事项分）
///   否则 → 冷却区；超过归档天数 → 到期，等待归档
/// "碰过" = 修改、打开（任何程序）、首次放上桌面，取最近的一次。
/// </summary>
public static class Classifier
{
    public static void Apply(List<DesktopItem> items, AppConfig cfg, StateStore st, Dictionary<string, DateTime> recent, DateTime now, bool firstRun)
    {
        foreach (var it in items)
        {
            // 首次运行：用文件创建时间估算它是何时放上桌面的；之后新出现的就记为"现在"
            var guess = firstRun ? Min(it.Created, now) : now;
            var meta = st.Get(it.Path, guess);
            it.FirstSeen = meta.FirstSeen;
            it.Pinned = meta.Pinned;

            var touch = Max(it.LastWrite, meta.FirstSeen);
            if (recent.TryGetValue(it.Path, out var r)) touch = Max(touch, r);
            if (meta.LastOpened is { } lo) touch = Max(touch, lo);
            it.LastTouch = Min(touch, now);

#if FEAT_FLOW
            it.Topic = meta.Topic != null && cfg.FindTopic(meta.Topic) != null ? meta.Topic : Flow.Match(it.Name, cfg);
#endif

            var stale = (now.Date - it.LastTouch.Date).Days;
            it.DaysLeft = cfg.ArchiveDays - stale;

            it.Fence = null;
            if (meta.Fence != null && cfg.CustomFences.Any(f => f.Id == meta.Fence)) { it.Zone = Zone.Custom; it.Fence = meta.Fence; continue; }
            if (it.IsSystem) { it.Zone = Zone.Apps; continue; }
            if (Placed(meta, it, now) is { } z) { it.Zone = z; continue; }
            if (it.IsShortcut) { it.Zone = Zone.Apps; continue; }
            if (it.IsDir) { it.Zone = Zone.Folders; continue; }

#if FEAT_FLOW
            if (it.Pinned) it.Zone = Zone.Active;
            else if (it.FirstSeen.Date == now.Date) it.Zone = Zone.Today;
            else if (stale < cfg.ColdDays) it.Zone = Zone.Active;
            else it.Zone = Zone.Cold;
#else
            it.Zone = Zone.Files;
#endif
        }
    }

    /// <summary>用户手动拖到的分区是否还有效（规则见 ItemMeta.Placed）。</summary>
    static Zone? Placed(ItemMeta m, DesktopItem it, DateTime now)
    {
        bool file = !it.IsDir && !it.IsShortcut;
        return m.Placed switch
        {
            null => null,
#if FEAT_FLOW
            Zone.Today => m.PlacedAt?.Date == now.Date ? Zone.Today : null,
            Zone.Active when file => m.PlacedAt?.Date == now.Date ? Zone.Active : null,
            Zone.Cold => m.PlacedAt is { } t && it.LastTouch > t ? null : Zone.Cold,
#else
            Zone.Today or Zone.Active or Zone.Cold => null, // 以前在有流转的版本里拖过
#endif
            var z => z,
        };
    }

    static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
