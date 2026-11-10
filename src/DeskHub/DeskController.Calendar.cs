using DeskHub.UI;

namespace DeskHub;

// 桌面日历（编译开关 FeatCalendar）
public sealed partial class DeskController
{
    /// <summary>桌面上的日历（隐藏了就是 null）。</summary>
    public CalendarWidget? CalWidget => _cal as CalendarWidget;

    partial void StartCalendar() { if (Cfg.CalShow) MakeCalendar(); }
    partial void CalendarToday() => CalWidget?.GoToday();
    partial void ClockClicked() { SetCalendarShown(true); CalWidget?.GoToday(); }

    void MakeCalendar()
    {
        var cal = new CalendarWidget(Cfg);
        _cal = cal;
        cal.Moved += () => { if (_cal != null) SaveLayout("calendar", _cal); };
        cal.HideRequested += () => SetCalendarShown(false);
#if FEAT_TRIPAI
        cal.AiSettingsRequested += () => OpenSettings("ai");
#endif
        Place(cal, "calendar");
        cal.Show();
    }

    /// <summary>显示 / 隐藏桌面上的日历（工具条“日历”、快捷键、设置、日历菜单都走这里）。</summary>
    public void SetCalendarShown(bool show)
    {
        if (Cfg.CalShow != show) { Cfg.CalShow = show; Cfg.Save(); }
        if (show && _cal == null) MakeCalendar();
        else if (!show && _cal != null) { _cal.Close(); _cal = null; }
    }

    public void ToggleCalendar() => SetCalendarShown(_cal == null);
}
