using System.IO;
using System.Text.RegularExpressions;

namespace DeskHub.Core;

/// <summary>
/// 决定每个桌面项放在哪个分区、属于哪个事项。
/// 规则：
///   快捷方式 → 常用软件；文件夹 → 项目文件夹（这两类不流转）
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

            it.Topic = meta.Topic != null && cfg.FindTopic(meta.Topic) != null ? meta.Topic : Match(it.Name, cfg);

            var stale = (now.Date - it.LastTouch.Date).Days;
            it.DaysLeft = cfg.ArchiveDays - stale;

            it.Fence = null;
            if (meta.Fence != null && cfg.CustomFences.Any(f => f.Id == meta.Fence)) { it.Zone = Zone.Custom; it.Fence = meta.Fence; continue; }
            if (Placed(meta, it, now) is { } z) { it.Zone = z; continue; }
            if (it.IsShortcut) { it.Zone = Zone.Apps; continue; }
            if (it.IsDir) { it.Zone = Zone.Folders; continue; }

            if (it.Pinned) it.Zone = Zone.Active;
            else if (it.FirstSeen.Date == now.Date) it.Zone = Zone.Today;
            else if (stale < cfg.ColdDays) it.Zone = Zone.Active;
            else it.Zone = Zone.Cold;
        }
    }

    /// <summary>用户手动拖到的分区是否还有效（规则见 ItemMeta.Placed）。</summary>
    static Zone? Placed(ItemMeta m, DesktopItem it, DateTime now)
    {
        bool file = !it.IsDir && !it.IsShortcut;
        return m.Placed switch
        {
            null => null,
            Zone.Today => m.PlacedAt?.Date == now.Date ? Zone.Today : null,
            Zone.Active when file => m.PlacedAt?.Date == now.Date ? Zone.Active : null,
            Zone.Cold => m.PlacedAt is { } t && it.LastTouch > t ? null : Zone.Cold,
            var z => z,
        };
    }

    /// <summary>按文件名匹配事项：命中关键词最多的事项胜出，一样多就按事项顺序。</summary>
    public static string Match(string fileName, AppConfig cfg)
    {
        var name = fileName.ToLowerInvariant();
        string best = AppConfig.OtherTopic; int bestScore = 0;
        foreach (var t in cfg.Topics)
        {
            int score = 0;
            foreach (var k in t.Keywords.Concat(t.Learned))
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                var kw = k.Trim().ToLowerInvariant();
                if (!name.Contains(kw)) continue;
                // 纯英文短词（如 pk、hr）要求整词命中，避免误伤别的单词
                if (kw.Length <= 3 && Regex.IsMatch(kw, "^[a-z0-9]+$") && !Regex.IsMatch(name, $@"(^|[^a-z]){Regex.Escape(kw)}([^a-z]|$)")) continue;
                score += t.Learned.Contains(k) ? 3 : 1 + kw.Length / 4; // 学到的规则更可信，长关键词更可信
            }
            if (score > bestScore) { best = t.Name; bestScore = score; }
        }
        return best;
    }

    /// <summary>
    /// 从文件名里猜一个可以记住的关键词：去掉扩展名、日期、编号后，取第一个有意义的片段。
    /// 例："付款截图_供应商_1011.jpg" → "付款截图"
    /// </summary>
    public static string SuggestKeyword(string fileName, AppConfig cfg)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var parts = Regex.Split(stem, @"[\s_\-\.\(\)（）\[\]【】,，、+&]+")
            .Select(p => Regex.Replace(p, @"\d{2,}", "").Trim())
            .Where(p => p.Length >= 2 && !Regex.IsMatch(p, @"^(v\d*|copy|副本|final|最终|new)$", RegexOptions.IgnoreCase))
            .ToList();
        var existing = cfg.Topics.SelectMany(t => t.Keywords.Concat(t.Learned)).Select(k => k.ToLowerInvariant()).ToHashSet();
        return parts.FirstOrDefault(p => !existing.Contains(p.ToLowerInvariant())) ?? parts.FirstOrDefault() ?? stem;
    }

    static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
