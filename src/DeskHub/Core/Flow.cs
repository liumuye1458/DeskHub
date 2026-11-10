using System.IO;
using System.Text.RegularExpressions;

namespace DeskHub.Core;

/// <summary>事项分类（编译开关 FeatFlow）：按文件名猜事项、从文件名里猜一个可以记住的关键词。</summary>
public static class Flow
{
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

}
