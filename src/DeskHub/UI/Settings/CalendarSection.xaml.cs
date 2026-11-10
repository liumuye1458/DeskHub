using System.Windows.Controls;
using DeskHub.Core;

namespace DeskHub.UI.Settings;

/// <summary>日历的设置（编译开关 FeatCalendar）。</summary>
public partial class CalendarSection : UserControl, ISettingsPart
{
    readonly DeskController _c;

    public CalendarSection(DeskController c)
    {
        InitializeComponent();
        _c = c;
        var cfg = c.Cfg;
        CalShow.IsChecked = cfg.CalShow;
        CalLunar.IsChecked = cfg.CalLunar;
        CalChina.IsChecked = cfg.CalChina;
        CalIndonesia.IsChecked = cfg.CalIndonesia;
        CalWeekStart.SelectedIndex = cfg.CalMondayFirst ? 0 : 1;
        CalChinaHint.Text = L.F("已内置 {0} 年（国务院每年 11 月前后公布下一年的安排，公布后随新版本更新）", Holidays.ChinaYears);
        CalIndonesiaHint.Text = L.F("已内置 {0} 年（按印尼政府 SKB 3 Menteri 公布的日期）", Holidays.IndonesiaYears);
    }

    public void Store(AppConfig cfg)
    {
        cfg.CalLunar = CalLunar.IsChecked == true;
        cfg.CalChina = CalChina.IsChecked == true;
        cfg.CalIndonesia = CalIndonesia.IsChecked == true;
        cfg.CalMondayFirst = CalWeekStart.SelectedIndex != 1;
    }

    public string? Applied()
    {
        _c.SetCalendarShown(CalShow.IsChecked == true);
        _c.Tools?.RefreshCalendar();
        return null;
    }
}
