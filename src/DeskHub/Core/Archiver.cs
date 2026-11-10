using System.IO;

namespace DeskHub.Core;

/// <summary>
/// 自动归档（编译开关 FeatArchive）：把超过归档天数没碰过的桌面文件，移到 归档根目录\yyyy-MM\事项\。
/// 移动和撤回都交给 FileMover（记进“移动记录”）。只移动"个人桌面"上的文件，
/// 不动文件夹、快捷方式、公用桌面、钉住的文件。
/// </summary>
public sealed class Archiver
{
    readonly AppConfig _cfg;
    readonly FileMover _mover;
    public Archiver(AppConfig cfg, FileMover mover) { _cfg = cfg; _mover = mover; }

    public IEnumerable<DesktopItem> Due(IEnumerable<DesktopItem> items) =>
        items.Where(i => i.Zone == Zone.Cold && i.DaysLeft <= 0 && !i.IsPublic && !i.IsDir && !i.IsShortcut && !i.Pinned);

    public FileMover.Result Run(IEnumerable<DesktopItem> due, DateTime now) =>
        _mover.Move(due.Select(it => new MoveItem(it.Path, Path.Combine(_cfg.ArchiveRoot, now.ToString("yyyy-MM"), SafeName(it.Topic)), it.Topic)), L.T("归档"));

    static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? AppConfig.OtherTopic : s.Trim();
    }
}
