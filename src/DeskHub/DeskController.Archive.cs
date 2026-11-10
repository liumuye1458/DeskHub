using System.IO;
using System.Windows.Threading;
using DeskHub.Core;
using DeskHub.Interop;
using DeskHub.UI;

namespace DeskHub;

// 自动归档（编译开关 FeatArchive，要有事项分类和流转）：冷却区里超过归档天数没碰过的文件，移到归档文件夹
public sealed partial class DeskController
{
    Archiver _archiver = null!;
    readonly DispatcherTimer _archiveSoon = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(2) };
    DateTime _archiveAskedDay = DateTime.MinValue;

    partial void InitArchive()
    {
        _archiver = new Archiver(Cfg, Mover);
        _archiveSoon.Tick += (_, _) => { _archiveSoon.Stop(); CheckArchive(); };
    }

    partial void StartArchive() => _archiveSoon.Start(); // 开机两分钟后再检查归档，不和开机抢资源
    partial void ArchiveTick() => CheckArchive();

    partial void AddArchiveMenu(List<ShellMenu.Own?> own, DesktopItem item)
    {
        if (!item.IsPublic) own.Add(new ShellMenu.Own(L.T("立即归档"), () => ArchiveNow(item)));
    }

    partial void AddArchiveMenu(System.Windows.Controls.ContextMenu m)
    {
        var mi = new System.Windows.Controls.MenuItem { Header = L.T("立即检查归档") };
        mi.Click += (_, _) => CheckArchive(userAsked: true);
        m.Items.Add(mi);
    }

    public List<DesktopItem> DueItems() => _archiver.Due(_items).ToList();

    public void CheckArchive(bool userAsked = false)
    {
        Refresh();
        var due = DueItems();
        if (due.Count == 0) { if (userAsked) Notify?.Invoke(L.T("现在没有需要归档的文件")); return; }
        if (!Cfg.AutoArchive && !userAsked) return;
        if (!Cfg.ArchiveConfirmed || userAsked)
        {
            // 第一次归档必须让用户看过清单；之后才自动执行
            if (!userAsked && _archiveAskedDay == DateTime.Today) return;
            _archiveAskedDay = DateTime.Today;
            var win = new ArchiveConfirmWindow(due, Cfg);
            if (win.ShowDialog() != true) return;
            Cfg.ArchiveConfirmed = true;
            Cfg.Save();
        }
        RunArchive(due);
    }

    public void ArchiveNow(DesktopItem item) => RunArchive(new List<DesktopItem> { item });

    void RunArchive(List<DesktopItem> due)
    {
        var r = _archiver.Run(due, DateTime.Now);
        Refresh();
        var msg = L.F("已归档 {0} 个文件到 {1}，可在“移动记录”里撤回", r.Moved, Path.Combine(Cfg.ArchiveRoot, DateTime.Now.ToString("yyyy-MM")) + "\\");
        if (r.Skipped.Count > 0) msg += L.F("；{0} 个文件正在使用，下次再试", r.Skipped.Count);
        Notify?.Invoke(msg);
    }
}
