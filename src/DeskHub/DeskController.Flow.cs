using DeskHub.Core;
using DeskHub.UI;

namespace DeskHub;

// 事项分类和流转（编译开关 FeatFlow）：换事项时问要不要记住关键词。
public sealed partial class DeskController
{
    partial void LearnTopic(DesktopItem item, string topic, FenceWindow fence)
    {
        if (item.IsDir || item.IsShortcut || item.IsSystem) return;
        var from = item.Topic;
        var meta = State.Get(item.Path, DateTime.Now);
        meta.Topic = topic;
        Refresh();
        if (from == topic || topic == AppConfig.OtherTopic) return;
        var rule = Cfg.FindTopic(topic);
        if (rule == null) return;
        var kw = Flow.SuggestKeyword(item.Name, Cfg);
        var toast = new LearnToast(item.Name, from, topic, kw, k =>
        {
            foreach (var t in Cfg.Topics) t.Learned.RemoveAll(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase));
            rule.Learned.Add(k);
            Cfg.Save();
            Refresh();
        });
        toast.Show();
        toast.PlaceNear(fence);
    }
}
