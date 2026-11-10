using System.Windows;
using System.Windows.Controls;
using DeskHub.Core;

namespace DeskHub.UI.Settings;

/// <summary>常规：语言、系统图标、开机自启、时钟、底色；有日历时还有 CalendarSection。</summary>
public partial class GeneralPage : UserControl, ISettingsPart
{
    readonly DeskController _c;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = Brand.Id;

    public GeneralPage(DeskController c)
    {
        InitializeComponent();
        _c = c;
        var cfg = c.Cfg;
        HideIcons.IsChecked = cfg.HideSystemIcons;
        ShowSeconds.IsChecked = cfg.ShowSeconds;
        Glass.IsChecked = cfg.Glass ?? Interop.Backdrop.DefaultOn;
        ClockNote.Text = cfg.ClockNote;
        // 语言：第一项"跟随系统"，其余用各自语言的名字显示（不懂当前语言也能找到自己的）
        LangBox.Items.Add(new ComboBoxItem { Content = L.T("跟随系统") + "（" + L.Languages.First(l => l.Code == L.FromSystem()).Name + "）", Tag = "" });
        foreach (var l in L.Languages) LangBox.Items.Add(new ComboBoxItem { Content = l.Name, Tag = l.Code });
        LangBox.SelectedItem = LangBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == cfg.Language) ?? LangBox.Items[0];
        AutoStart.IsChecked = IsAutoStart();
        AutoStart.IsEnabled = !c.Sandbox;
        ResetLayout.Click += (_, _) => _c.ResetLayout();
        LookBtn.Click += (_, _) => LookPopup.Open(null, "", Window.GetWindow(this), LookBtn);
        Layouts.Click += (_, _) => ((App)Application.Current).OpenLayouts();
#if FEAT_CALENDAR
        var cal = new CalendarSection(c);
        CalendarHost.Content = cal;
        Calendar = cal;
#endif
    }

    /// <summary>日历那一段（没有日历时是 null）。</summary>
    public ISettingsPart? Calendar { get; }

    /// <summary>这次保存改了界面语言（要问是否重启）。</summary>
    public bool LangChanged { get; private set; }

    public string? Check() => Calendar?.Check();

    public void Store(AppConfig cfg)
    {
        cfg.ShowSeconds = ShowSeconds.IsChecked == true;
        if ((Glass.IsChecked == true) != (cfg.Glass ?? Interop.Backdrop.DefaultOn)) cfg.Glass = Glass.IsChecked == true;
        Interop.Backdrop.Set(cfg.Glass ?? Interop.Backdrop.DefaultOn ? Interop.Backdrop.Kind.Acrylic : Interop.Backdrop.Kind.None);
        cfg.ClockNote = string.Join(" ", ClockNote.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        var newLang = (string)((ComboBoxItem)LangBox.SelectedItem).Tag;
        LangChanged = newLang != cfg.Language;
        cfg.Language = newLang;
        Calendar?.Store(cfg);
    }

    public string? Applied()
    {
        if (_c.Cfg.HideSystemIcons != (HideIcons.IsChecked == true)) _c.SetHideSystemIcons(HideIcons.IsChecked == true);
        if (!_c.Sandbox) SetAutoStart(AutoStart.IsChecked == true);
        return Calendar?.Applied();
    }

    static bool IsAutoStart()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunName) is string;
    }

    static void SetAutoStart(bool on)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(RunName, false);
    }
}
