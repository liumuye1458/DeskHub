using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>别的程序来要密码时弹出的确认框：是谁、要哪个网站的、给哪一条。60 秒不点算拒绝。</summary>
public static class VaultConsent
{
    public enum Answer { Once, Always, Deny, Timeout, NotFound }

    /// <summary>测试用：设了就不弹窗，直接按这个回答（选第一条）。</summary>
    internal static Answer? TestAnswer;
    /// <summary>测试用：确认框画成图片存到这里（窗口放在屏幕外，不打扰使用者）。</summary>
    internal static string? TestShot;
    internal static Action<Window, string>? Render;

    static Brush B(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    public static async Task<(Answer, VaultEntry?)> Ask(ApiClient client, ApiRequest req, List<VaultEntry> cands)
    {
        if (TestAnswer is { } ta && TestShot == null) return (cands.Count == 0 ? Answer.NotFound : ta, cands.FirstOrDefault());

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 18), Width = 420 };
        panel.Children.Add(new TextBlock { Text = L.F("「{0}」想取用一条登录信息", client.Name), FontSize = 16, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = client.Exe, Foreground = B("#888"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var what = req.Url is { Length: > 0 } ? L.T("网址：") + req.Url : L.T("服务：") + req.Name;
        panel.Children.Add(new TextBlock { Text = what, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(req.Reason))
            panel.Children.Add(new TextBlock { Text = L.T("用途：") + req.Reason.Trim(), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });

        var radios = new List<(RadioButton R, VaultEntry E)>();
        if (cands.Count == 0)
            panel.Children.Add(new TextBlock { Text = L.T("密码箱里没有找到匹配的记录。"), Foreground = B("#C0392B"), Margin = new Thickness(0, 14, 0, 0) });
        else
        {
            panel.Children.Add(new TextBlock { Text = cands.Count == 1 ? L.T("将提供：") : L.T("选择提供哪一条："), Foreground = B("#666"), Margin = new Thickness(0, 14, 0, 6) });
            var list = new StackPanel();
            foreach (var e in cands.Take(8))
            {
                var r = new RadioButton
                {
                    GroupName = "c", IsChecked = radios.Count == 0, Margin = new Thickness(0, 3, 0, 3), VerticalContentAlignment = VerticalAlignment.Center,
                    Content = new TextBlock { Text = $"{e.Name}　{e.Account}", FontSize = 13.5 },
                };
                radios.Add((r, e));
                list.Children.Add(r);
            }
            panel.Children.Add(new Border { Background = B("#F5F6F8"), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6, 10, 6), Child = list });
        }
        panel.Children.Add(new TextBlock
        {
            Text = L.T("只在这台电脑上传给这个程序，不联网。不认识这个程序就点拒绝。"), Foreground = B("#999"), FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        });

        var tcs = new TaskCompletionSource<(Answer, VaultEntry?)>();
        VaultEntry? Chosen() => radios.FirstOrDefault(x => x.R.IsChecked == true).E;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var deny = new Button { Content = cands.Count == 0 ? L.T("关闭") : L.T("拒绝"), Style = (Style)Application.Current.FindResource("BtnW"), MinWidth = 80, IsCancel = true };
        bar.Children.Add(deny);
        Button? once = null, always = null;
        if (cands.Count > 0)
        {
            always = new Button { Content = L.T("总是允许"), Style = (Style)Application.Current.FindResource("BtnW"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), ToolTip = L.T("以后这个程序取这一条不再问（可以在 密码箱 → 更多 → 对外接口 里撤销）") };
            once = new Button { Content = L.T("允许这一次"), Style = (Style)Application.Current.FindResource("BtnG"), MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
            bar.Children.Add(always); bar.Children.Add(once);
        }
        panel.Children.Add(bar);

        var w = new Window
        {
            Title = L.T("密码箱 · 程序请求登录信息"), Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true, FontFamily = (FontFamily)Application.Current.FindResource("UiFont"), FontSize = 13,
            Background = Brushes.White,
        };
        int left = 60;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            left--;
            deny.Content = (cands.Count == 0 ? L.T("关闭") : L.T("拒绝")) + $"（{left}）";
            if (left <= 0) tcs.TrySetResult((Answer.Timeout, null));
        };
        deny.Click += (_, _) => tcs.TrySetResult((cands.Count == 0 ? Answer.NotFound : Answer.Deny, null));
        if (once != null) once.Click += (_, _) => tcs.TrySetResult((Answer.Once, Chosen()));
        if (always != null) always.Click += (_, _) => tcs.TrySetResult((Answer.Always, Chosen()));
        w.Closed += (_, _) => tcs.TrySetResult((cands.Count == 0 ? Answer.NotFound : Answer.Deny, null));

        if (TestShot != null)
        {
            // 测试：放到屏幕外画成图片，然后按设定的回答
            w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -10000; w.Top = -10000; w.ShowActivated = false; w.Topmost = false;
            w.Show();
            await Task.Delay(300);
            Render?.Invoke(w, TestShot);
            TestShot = null;
            w.Close();
            var a = TestAnswer ?? Answer.Deny;
            return (cands.Count == 0 ? Answer.NotFound : a, cands.FirstOrDefault());
        }

        w.Show();
        w.Activate();
        timer.Start();
        try { return await tcs.Task; }
        finally { timer.Stop(); if (w.IsVisible) w.Close(); }
    }
}

/// <summary>密码箱 → 更多 → 对外接口：开关、已授权的程序（可撤销）、最近取用记录。</summary>
public static class VaultApiPanel
{
    static Brush B(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    public static void Show(Window owner, VaultData data, bool on, Action<bool> setOn, Action save, string docPath)
    {
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 16), Width = 560 };
        var toggle = new CheckBox { Content = L.T("开启对外接口"), IsChecked = on, FontWeight = FontWeights.Bold, FontSize = 14 };
        toggle.Checked += (_, _) => setOn(true);
        toggle.Unchecked += (_, _) => setOn(false);
        root.Children.Add(toggle);
        root.Children.Add(new TextBlock
        {
            Text = L.T("开启后，这台电脑上的其他程序（比如以后的自动登录工具）可以向密码箱要某个网站的账号密码。\n") +
                   L.T("每次都会弹框让你确认：允许一次 / 总是允许 / 拒绝；密码箱锁着会先让你解锁。只在本机，不联网，只有你这个 Windows 账户能连。"),
            Foreground = B("#777"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 12,
        });

        root.Children.Add(new TextBlock { Text = L.T("已授权（总是允许）"), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 16, 0, 6) });
        var grants = new StackPanel();
        void BuildGrants()
        {
            grants.Children.Clear();
            if (data.Grants.Count == 0) grants.Children.Add(new TextBlock { Text = L.T("还没有"), Foreground = B("#999") });
            foreach (var g in data.Grants.ToList())
            {
                var e = data.Entries.FirstOrDefault(x => x.Id == g.EntryId);
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var revoke = new Button { Content = L.T("撤销"), Style = (Style)Application.Current.FindResource("BtnW"), MinWidth = 60, Padding = new Thickness(8, 2, 8, 2) };
                revoke.Click += (_, _) => { data.Grants.Remove(g); save(); BuildGrants(); };
                DockPanel.SetDock(revoke, Dock.Right);
                row.Children.Add(revoke);
                row.Children.Add(new TextBlock
                {
                    Text = L.F("{0} → {1}　{2}", System.IO.Path.GetFileName(g.Exe), (e == null ? L.T("（记录已删除）") : e.Name + "　" + e.Account), (g.LastUsed is { } lu ? L.T("上次 ") + lu.ToString("MM-dd HH:mm") : L.T("授权于 ") + g.Created.ToString("MM-dd"))),
                    ToolTip = g.Exe, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                });
                grants.Children.Add(row);
            }
        }
        BuildGrants();
        root.Children.Add(grants);

        root.Children.Add(new TextBlock { Text = L.T("最近取用记录"), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 16, 0, 6) });
        var log = new StackPanel();
        if (data.ApiLog.Count == 0) log.Children.Add(new TextBlock { Text = L.T("还没有"), Foreground = B("#999") });
        foreach (var l in Enumerable.Reverse(data.ApiLog).Take(30))
            log.Children.Add(new TextBlock
            {
                Text = $"{l.Time:MM-dd HH:mm}　{System.IO.Path.GetFileName(l.Exe)}　{l.Target}　{(l.Entry.Length > 0 ? "→ " + l.Entry + "　" : "")}{L.T(l.Result)}",
                ToolTip = l.Exe, Foreground = l.Result is "拒绝" or "超时" ? B("#C0392B") : Brushes.Black, Margin = new Thickness(0, 1, 0, 1),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        root.Children.Add(new ScrollViewer { Content = log, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        root.Children.Add(new TextBlock { Text = L.T("给开发者：接口说明见 ") + docPath, Foreground = B("#999"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) });
        var close = new Button { Content = L.T("关闭"), Style = (Style)Application.Current.FindResource("BtnW"), MinWidth = 80, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        root.Children.Add(close);

        var w = new Window
        {
            Title = L.T("密码箱 · 对外接口"), Content = root, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = owner.FontFamily, FontSize = 13, Background = Brushes.White,
        };
        close.Click += (_, _) => w.Close();
        w.ShowDialog();
    }
}
