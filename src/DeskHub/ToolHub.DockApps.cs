using System.IO;
using DeskHub.Core;
using DeskHub.Interop;

namespace DeskHub;

// 工具条上的软件按钮：用户拖上来的快捷方式（Edition.DockAcceptsShortcuts），以及 OpenApp 这个打开方式。
public sealed partial class ToolHub
{
    /// <summary>
    /// 打开一个软件：已经开着就切过去（最小化的先还原），没开才启动；
    /// 在运行但没有窗口（只在托盘里）的不再开一个，在工具条旁边轻提示一句。
    /// </summary>
    public void OpenApp(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        switch (AppLauncher.Open(path, out var err))
        {
            case AppLauncher.Result.RunningNoWindow:
                _dock?.ShowHint(L.F("{0} 已经在运行了，在右下角托盘里找它", name));
                break;
            case AppLauncher.Result.Failed:
                _dock?.ShowHint(L.F("打不开 {0}：{1}", name, err ?? ""));
                break;
        }
    }

    void AddUserApps()
    {
        foreach (var p in _c.Cfg.DockApps.ToList()) AddUserApp(p);
        _dock!.AcceptShortcuts(files =>
        {
            foreach (var f in files)
            {
                if (_c.Cfg.DockApps.Contains(f, StringComparer.OrdinalIgnoreCase)) continue;
                _c.Cfg.DockApps.Add(f);
                AddUserApp(f);
            }
            _c.Cfg.Save();
        });
    }

    void AddUserApp(string path)
    {
        var key = "app:" + path;
        var name = Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(path) : ShellIcons.DisplayName(path) ?? Path.GetFileNameWithoutExtension(path);
        _dock!.AddApp(key, name, path, name, () => OpenApp(path), () =>
        {
            _c.Cfg.DockApps.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _c.Cfg.Save();
            _dock.Remove(key);
        });
    }

    /// <summary>测试用：工具条旁边现在显示的轻提示。</summary>
    public string? TestDockHint => _dock?.HintShown;

    /// <summary>测试用：像拖上来一样加一个软件按钮（不管 Edition 开没开）。</summary>
    public void TestAddApp(string path) { if (!_c.Cfg.DockApps.Contains(path)) _c.Cfg.DockApps.Add(path); AddUserApp(path); }
}
