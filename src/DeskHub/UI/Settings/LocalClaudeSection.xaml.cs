using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskHub.Core;

namespace DeskHub.UI.Settings;

/// <summary>本机 Claude 小服务（编译开关 FeatLocalClaude）。</summary>
public partial class LocalClaudeSection : UserControl, ISettingsPart
{
    readonly DeskController _c;
    int _port;
    bool _changed;

    /// <summary>useForTrips：点“日历识别改用本机 Claude”时把地址、模型、口令填到识别那一段；没有 AI 识别行程时为 null，按钮不显示。</summary>
    public LocalClaudeSection(DeskController c, Action<string, IReadOnlyList<string>, string>? useForTrips)
    {
        InitializeComponent();
        _c = c;
        var cfg = c.Cfg;
        LcOn.IsChecked = cfg.LocalClaude;
        LcPort.Text = cfg.LocalClaudePort.ToString();
        LcToken.Password = Secret.Unprotect(cfg.LocalClaudeToken);
        ShowState();
        if (useForTrips == null) LcUse.Visibility = Visibility.Collapsed;
        LcUse.Click += (_, _) =>
        {
            if (LcToken.Password.Trim().Length == 0) { LcHint.Foreground = Brushes.IndianRed; LcHint.Text = L.T("先填口令"); LcToken.Focus(); return; }
            if (!int.TryParse(LcPort.Text.Trim(), out var port) || port < 1024 || port > 65535) { LcHint.Foreground = Brushes.IndianRed; LcHint.Text = L.T("端口填 1024 到 65535 之间的数"); return; }
            LcOn.IsChecked = true;
            useForTrips?.Invoke($"http://127.0.0.1:{port}/v1/messages", LocalClaude.Models, LcToken.Password.Trim());
            LcHint.Foreground = Ui.Ok;
            LcHint.Text = L.T("已填到上面的地址、密钥、模型，点“保存”生效。");
        };
        LcDoc.Click += (_, _) =>
        {
            if (File.Exists(LocalClaude.DocPath)) Process.Start(new ProcessStartInfo(LocalClaude.DocPath) { UseShellExecute = true });
            else { LcHint.Foreground = Brushes.IndianRed; LcHint.Text = L.T("说明文件不见了") + "：" + LocalClaude.DocPath; }
        };
    }

    void ShowState()
    {
        var running = _c.Tools?.LocalClaude;
        var err = _c.Tools?.LocalClaudeError ?? "";
        var exe = LocalClaude.FindClaude();
        LcHint.Foreground = Brushes.Gray;
        if (exe == null) { LcHint.Foreground = Brushes.IndianRed; LcHint.Text = L.T("这台电脑上没找到 claude 命令行。装好后在命令行里运行一次 claude 登录，再试。"); }
        else if (running != null) { LcHint.Foreground = Ui.Ok; LcHint.Text = L.F("正在运行：http://127.0.0.1:{0}", running.Port); }
        else if (err.Length > 0) { LcHint.Foreground = Brushes.IndianRed; LcHint.Text = err; }
        else LcHint.Text = L.T("没打开。");
    }

    public string? Check()
    {
        if (!int.TryParse(LcPort.Text.Trim(), out _port) || _port < 1024 || _port > 65535) return L.T("端口填 1024 到 65535 之间的数");
        if (LcOn.IsChecked == true && LcToken.Password.Trim().Length == 0) { LcToken.Focus(); return L.T("打开本机 Claude 服务要先填口令"); }
        return null;
    }

    public void Store(AppConfig cfg)
    {
        bool on = LcOn.IsChecked == true;
        var token = LcToken.Password.Trim();
        _changed = on != cfg.LocalClaude || _port != cfg.LocalClaudePort || token != Secret.Unprotect(cfg.LocalClaudeToken);
        if (!_changed) return;
        cfg.LocalClaude = on;
        cfg.LocalClaudePort = _port;
        cfg.LocalClaudeToken = Secret.Protect(token);
    }

    public string? Applied()
    {
        if (!_changed) return null;
        _c.Tools?.ApplyLocalClaude();
        if (_c.Tools?.LocalClaudeError is not { Length: > 0 } err) return null;
        ShowState();
        return L.T("本机 Claude 服务没开起来") + "：" + err;
    }
}
