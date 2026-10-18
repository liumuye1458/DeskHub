using System.IO;
using System.Windows;
using DeskHub.Core;

namespace DeskHub.UI;

public partial class ArchiveConfirmWindow : Window
{
    public ArchiveConfirmWindow(List<DesktopItem> due, AppConfig cfg)
    {
        InitializeComponent();
        var month = DateTime.Now.ToString("yyyy-MM");
        Head.Text = L.F("有 {0} 个文件已经 {1} 天没碰过，要移出桌面吗？", due.Count, cfg.ArchiveDays);
        Sub.Text = L.F("文件会移到 {0} 下按事项分好的文件夹里，不会删除；随时可以在“归档记录”里一键撤回。", Path.Combine(cfg.ArchiveRoot, month) + "\\");
        List.ItemsSource = due.OrderBy(i => i.Topic).ThenBy(i => i.LastTouch).Select(i => new
        {
            i.Name,
            i.Topic,
            Last = i.LastTouch.ToString("yyyy-MM-dd"),
            To = $"{month}\\{i.Topic}\\",
        }).ToList();
        Ok.Click += (_, _) => DialogResult = true;
    }
}
