using System.Windows;
using System.Windows.Controls;
using DeskHub.Core;

namespace DeskHub.UI.Settings;

/// <summary>流转：冷却天数（事项分类和流转，编译开关 FeatFlow）。有自动归档时还有归档天数和 ArchiveSection。</summary>
public partial class FlowPage : UserControl, ISettingsPart
{
    int _cold, _arch;

    public FlowPage(DeskController c)
    {
        InitializeComponent();
        ColdDays.Text = c.Cfg.ColdDays.ToString();
        ArchiveDays.Text = c.Cfg.ArchiveDays.ToString();
#if FEAT_ARCHIVE
        var a = new ArchiveSection(c);
        ArchiveHost.Content = a;
        Archive = a;
#else
        ArchiveArrow.Visibility = ArchiveBox.Visibility = Visibility.Collapsed;
#endif
    }

    /// <summary>归档那一段（没有自动归档时是 null）。</summary>
    public ISettingsPart? Archive { get; }

    public string? Check()
    {
        if (!int.TryParse(ColdDays.Text, out _cold) || _cold < 1 || _cold > 365) return L.T("冷却天数要在 1–365 之间");
#if FEAT_ARCHIVE
        if (!int.TryParse(ArchiveDays.Text, out _arch) || _arch <= _cold || _arch > 3650) return L.T("归档天数要大于冷却天数");
#endif
        return Archive?.Check();
    }

    public void Store(AppConfig cfg)
    {
        cfg.ColdDays = _cold;
#if FEAT_ARCHIVE
        cfg.ArchiveDays = _arch;
#endif
        Archive?.Store(cfg);
    }
}
