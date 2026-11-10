using System.Windows;
using System.Windows.Threading;
using DeskHub.Core;
using DeskHub.UI;

namespace DeskHub;

// 日历和定时便签（编译开关 FeatCalendar）：桌面日历、行程提前一天提醒、备忘录（贴在屏幕上的指引便签，可定时出现）
public sealed partial class ToolHub
{
    GuideManager? _memo;
    readonly Dictionary<string, GuideWindow> _guides = new();
    readonly DispatcherTimer _remind = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };

    partial void AddCalendarBuiltins(List<Builtin> list)
    {
        list.Add(new("memo", L.T("备忘录"), L.T("备忘录"), "M5,2 H15 V18 H5 Z M8,6.5 H12.5 M8,10 H12.5 M8,13.5 H11", Memo));
        list.Add(new("calendar", L.T("日历"), L.T("日历"), "M3,4 H17 V17 H3 Z M3,8 H17 M7,2 V5.5 M13,2 V5.5 M6.5,11.5 H7.5 M9.5,11.5 H10.5 M12.5,11.5 H13.5 M6.5,14.5 H7.5 M9.5,14.5 H10.5", () => Calendar()));
    }

    partial void StartCalendar()
    {
        // 备忘录：贴在屏幕上的指引便签。每 30 秒看一次：定时的到点出现、过了凌晨 5 点每天的重新出现
        GuideStore.Load();
        GuideStore.Changed += SyncGuides;
        // 行程：提前一天弹便签提醒（也是每 30 秒看一次）
        TripStore.Load();
        _remind.Tick += (_, _) => { CheckTrips(); SyncGuides(flash: true); };
        _remind.Start();
        CheckTrips();
        SyncGuides(flash: false);
    }

    partial void StopCalendar()
    {
        _remind.Stop();
        GuideStore.Changed -= SyncGuides;
        foreach (var g in _guides.Values) g.Close();
        _memo?.Close();
    }

    partial void AddDesktopWindows(List<Window> list) => list.AddRange(_guides.Values.Where(g => !g.IsTopmostNote));

    public void Memo()
    {
        if (_memo == null)
        {
            _memo = new GuideManager(() => SystemParameters.WorkArea);
            _memo.Closed += (_, _) => { _memo = null; App.TrimMemory(); };
        }
        _memo.Popup();
    }

    /// <summary>显示 / 隐藏桌面上的日历。</summary>
    public void Calendar() => _c.ToggleCalendar();

    /// <summary>设置改了：日历重画。</summary>
    public void RefreshCalendar() => _c.CalWidget?.Refresh();

    void CheckTrips() => TripStore.CheckReminders(DateTime.Now, SystemParameters.WorkArea);

    void SyncGuides() => SyncGuides(flash: false);

    /// <summary>让屏幕上的便签和数据一致：该显示的显示（到点出现的闪几下），该收起的收起，改过的重画。</summary>
    void SyncGuides(bool flash)
    {
        var now = DateTime.Now;
        foreach (var (id, w) in _guides.ToList())
        {
            var n = GuideStore.Find(id);
            bool keep = n != null && (GuideStore.Visible(n, now) || w.CountingDown) && w.IsTopmostNote == n.Topmost;
            if (!keep) { w.Close(); _guides.Remove(id); }
            else w.Refresh(n!);
        }
        foreach (var n in GuideStore.D.Notes.Where(n => !_guides.ContainsKey(n.Id) && GuideStore.Visible(n, now)))
        {
            var w = new GuideWindow(n, EditGuide, Memo);
            w.Show();
            _guides[n.Id] = w;
            if (flash) w.Flash();
        }
    }

    void EditGuide(GuideNote n)
    {
        if (GuideEditor.Ask(_memo, n, false)) GuideStore.Save();
    }

    /// <summary>测试用：屏幕上的便签窗口。</summary>
    public IReadOnlyDictionary<string, GuideWindow> Guides => _guides;
    public GuideManager OpenManagerForTest() { Memo(); return _memo!; }
}
