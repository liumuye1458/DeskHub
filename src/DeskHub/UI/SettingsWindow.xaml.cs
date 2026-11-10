using System.Windows;
using System.Windows.Controls;
using DeskHub.Core;
using DeskHub.UI.Settings;

namespace DeskHub.UI;

/// <summary>
/// 设置窗口。每页（或页里的一段）是 UI\Settings 里的一个文件，实现 ISettingsPart；
/// 可选功能关掉时那个文件不编译，这里对应的一页也就没有。
/// 页的键：rules 整理规则 / flow 流转与归档 / log 移动记录 / keys 快捷键 / general 常规 / ai 智能识别 / about 关于
/// </summary>
public partial class SettingsWindow : Window
{
    readonly DeskController _c;
    readonly List<(string Key, RadioButton Nav, ScrollViewer Page)> _pages = new();
    readonly List<(string Page, ISettingsPart Part)> _parts = new();
    readonly GeneralPage _general;

    public SettingsWindow(DeskController c, string page = "rules")
    {
        InitializeComponent();
        Title = L.F("{0} · 设置", Brand.Name);
        _c = c;

#if FEAT_FLOW
        AddPage("rules", L.T("整理规则"), new RulesPage(c.Cfg));
#if FEAT_ARCHIVE
        AddPage("flow", L.T("流转与归档"), new FlowPage(c));
#else
        AddPage("flow", L.T("文件流转"), new FlowPage(c));
#endif
#endif
        AddPage("log", L.T("移动记录"), new LogPage(c));
#if FEAT_HOTKEYS
        AddPage("keys", L.T("快捷键"), new KeysPage(c));
#endif
        _general = new GeneralPage(c);
        AddPage("general", L.T("常规"), _general);
#if FEAT_TRIPAI || FEAT_LOCALCLAUDE
        AddPage("ai", L.T("智能识别"), AiPage(c));
#endif
        AddPage("about", L.T("关于"), new AboutPage(c.Sandbox));

        ShowPage(page);
        Closed += (_, _) => { foreach (var (_, p) in _parts) p.Closed(); };
        Save.Click += (_, _) => { if (Apply()) Close(); };
    }

    void AddPage(string key, string title, UserControl content)
    {
        var sv = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(26, 18, 26, 18), Visibility = Visibility.Collapsed };
        var nav = new RadioButton { Style = (Style)FindResource("NavItem"), GroupName = "nav", Content = title };
        nav.Checked += (_, _) =>
        {
            foreach (var p in _pages) p.Page.Visibility = p.Nav == nav ? Visibility.Visible : Visibility.Collapsed;
            if (content is ISettingsPart part) part.Shown();
        };
        NavList.Children.Add(nav);
        PageHost.Children.Add(sv);
        _pages.Add((key, nav, sv));
        if (content is ISettingsPart sp) _parts.Add((key, sp));
    }

#if FEAT_TRIPAI || FEAT_LOCALCLAUDE
    /// <summary>智能识别：AI 识别行程用的服务、本机 Claude 小服务（各自有编译开关），最后是隐私说明。</summary>
    UserControl AiPage(DeskController c)
    {
        var stack = new StackPanel();
#if FEAT_TRIPAI
        var trip = new TripAiSection(c.Cfg);
        stack.Children.Add(trip);
        _parts.Add(("ai", trip));
#endif
#if FEAT_LOCALCLAUDE
#if FEAT_TRIPAI
        var lc = new LocalClaudeSection(c, trip.Fill);
#else
        var lc = new LocalClaudeSection(c, null);
        lc.LcTitle.Margin = new Thickness(0, 0, 0, 4);
#endif
        stack.Children.Add(lc);
        _parts.Add(("ai", lc));
#endif
#if FEAT_TRIPAI
        stack.Children.Add(new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("#222"), Margin = new Thickness(0, 22, 0, 4), Text = L.T("隐私") });
        stack.Children.Add(new TextBlock { Foreground = Ui.Brush("#666"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Text = L.T("只有你点“识别”的时候，丢进来的文字和截图才会发给上面这个服务，平时不联网。用公司一体机或本机 Ollama，内容不出公司 / 不出电脑；用外面的服务，内容会发到对方服务器。") });
#endif
        return new UserControl { Content = stack };
    }
#endif

    /// <summary>切到某一页（没有这一页就第一页）。</summary>
    public void ShowPage(string key)
    {
        var p = _pages.FirstOrDefault(x => x.Key == key);
        (p.Nav ?? _pages[0].Nav).IsChecked = true;
    }

    /// <summary>测试用：某一页的滚动区。</summary>
    public ScrollViewer? PageOf(string key) => _pages.FirstOrDefault(x => x.Key == key).Page;

    bool Apply()
    {
        SaveHint.Text = "";
        foreach (var (page, part) in _parts)
            if (part.Check() is { } err) { SaveHint.Text = err; ShowPage(page); return false; }

        var cfg = _c.Cfg;
        foreach (var (_, part) in _parts) part.Store(cfg);
        cfg.Save();
        _c.ApplyTexts();
        _c.Refresh();
        foreach (var (page, part) in _parts)
            if (part.Applied() is { } err) { SaveHint.Text = err; ShowPage(page); return false; }

        if (_general.LangChanged && !_c.Sandbox &&
            MessageBox.Show(this, L.T("界面语言改好了，重启本工具后生效。现在重启吗？"), L.T("设置"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Dispatcher.BeginInvoke(() => ((App)Application.Current).Restart());
        return true;
    }
}
