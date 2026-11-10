using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskHub.Core;
using static DeskHub.UI.Settings.Ui;

namespace DeskHub.UI.Settings;

/// <summary>快捷键（编译开关 FeatHotkeys）。下面顺带显示文件查找的索引状态。</summary>
public partial class KeysPage : UserControl, ISettingsPart
{
    readonly DeskController _c;
    readonly Dictionary<string, string> _keySpecs = new();

    public KeysPage(DeskController c)
    {
        InitializeComponent();
        _c = c;
        BuildKeys();
    }

    public void Shown() => _ = ShowIndexInfo();
    public void Closed() => _c.Tools?.ApplyHotkeys(); // 录快捷键时临时停用过，关窗时恢复

    public void Store(AppConfig cfg)
    {
        foreach (var (k, v) in _keySpecs) cfg.Hotkeys[k] = v;
    }

    public string? Applied()
    {
        if (_c.Tools?.ApplyHotkeys() is not { Count: > 0 } bad) return null;
        BuildKeys();
        return string.Join("；", bad.Select(kv => L.F("{0}快捷键{1}", ToolHub.Actions.First(a => a.Key == kv.Key).Name, kv.Value)));
    }

    static string Show(string spec) => spec.Length == 0 ? L.T("未设置") : spec == Interop.Hotkeys.DoubleCtrl ? L.T("连按两下 Ctrl") : spec.Replace("+", " + ");

    void BuildKeys()
    {
        KeyRows.Children.Clear();
        var tools = _c.Tools;
        foreach (var a in ToolHub.Actions)
        {
            var key = a.Key;
            if (!_keySpecs.ContainsKey(key)) _keySpecs[key] = tools?.HotkeyOf(key) ?? a.Default;
            var box = new TextBox
            {
                Width = 200, Height = 30, IsReadOnly = true, IsReadOnlyCaretVisible = false, Cursor = Cursors.Hand,
                Style = Res<Style>("WinTextBox"), TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Text = Show(_keySpecs[key]), ToolTip = L.T("点一下，然后按下新的组合键"),
            };
            var status = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var dbl = new CheckBox { Content = L.T("连按两下 Ctrl"), Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = key == "search" ? Visibility.Visible : Visibility.Collapsed, IsChecked = _keySpecs[key] == Interop.Hotkeys.DoubleCtrl };
            var reset = new Button { Content = L.T("恢复默认"), Style = Res<Style>("BtnW"), Margin = new Thickness(10, 0, 0, 0), Visibility = a.Default.Length == 0 ? Visibility.Collapsed : Visibility.Visible };

            void Set(string spec)
            {
                _keySpecs[key] = spec;
                box.Text = Show(spec);
                dbl.IsChecked = spec == Interop.Hotkeys.DoubleCtrl;
                box.IsEnabled = dbl.IsChecked != true;
                Check(key, spec, status);
            }

            box.GotKeyboardFocus += (_, _) => { tools?.SuspendHotkeys(); box.Text = L.T("请按下组合键…"); };
            box.LostKeyboardFocus += (_, _) => { box.Text = Show(_keySpecs[key]); tools?.ApplyHotkeys(); };
            box.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                var k = e.Key == Key.System ? e.SystemKey : e.Key;
                if (k is Key.Back or Key.Delete) { Set(""); Keyboard.ClearFocus(); return; }
                if (k == Key.Escape) { Keyboard.ClearFocus(); return; }
                if (k == Key.Tab) { e.Handled = false; return; }
                var spec = Interop.Hotkeys.Describe(Keyboard.Modifiers, k);
                if (spec == null)
                {
                    // 只按了修饰键：先显示着，等按下主键
                    if (Keyboard.Modifiers != ModifierKeys.None) box.Text = Show(Interop.Hotkeys.Describe(Keyboard.Modifiers, Key.F24)![..^4]) + " + …";
                    else { status.Text = L.T("单个按键会影响打字，请加上 Ctrl / Alt / Shift / Win"); status.Foreground = Brush("#E47600"); }
                    return;
                }
                Set(spec);
                Keyboard.ClearFocus();
            };
            dbl.Click += (_, _) => Set(dbl.IsChecked == true ? Interop.Hotkeys.DoubleCtrl : a.Default == Interop.Hotkeys.DoubleCtrl ? "" : a.Default);
            reset.Click += (_, _) => Set(a.Default);
            box.IsEnabled = dbl.IsChecked != true;
            Check(key, _keySpecs[key], status);

            var row = new DockPanel();
            row.Children.Add(new TextBlock { Text = a.Name, Width = 90, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            row.Children.Add(box);
            row.Children.Add(dbl);
            row.Children.Add(reset);
            row.Children.Add(status);
            KeyRows.Children.Add(new Border { BorderBrush = Brush("#F0F0F0"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8), Child = row });
        }
    }

    /// <summary>冲突检查：和本工具其他功能重复，或者被别的程序占用。</summary>
    void Check(string key, string spec, TextBlock status)
    {
        status.Text = "";
        if (spec.Length == 0) return;
        var dup = ToolHub.Actions.FirstOrDefault(a => a.Key != key && _keySpecs.GetValueOrDefault(a.Key) == spec);
        if (dup.Key != null) { status.Text = L.F("和“{0}”重复了", dup.Name); status.Foreground = Brush("#E5413C"); return; }
        var tools = _c.Tools;
        if (tools == null) return;
        if (tools.HotkeyErrors.TryGetValue(key, out var err) && spec == tools.HotkeyOf(key)) { status.Text = err; status.Foreground = Brush("#E5413C"); return; }
        // 本工具自己正在用的组合不算冲突
        bool ours = ToolHub.Actions.Any(a => tools.HotkeyOf(a.Key) == spec && !tools.HotkeyErrors.ContainsKey(a.Key));
        if (tools.Keys is { } hk && !ours && !hk.IsFree(spec)) { status.Text = L.T("已被别的程序占用，换一个"); status.Foreground = Brush("#E5413C"); return; }
        status.Text = L.T("✓ 可以用"); status.Foreground = Brush("#07C255");
    }

    async Task ShowIndexInfo()
    {
        IndexInfo.Text = L.T("文件查找：正在读取索引状态…");
        var r = await Search.IndexerHost.QueryAsync(new Search.QueryRequest { Q = "", Max = 0 }, CancellationToken.None);
        IndexInfo.Text = r == null ? L.T("文件查找：索引程序还没启动（第一次查找时会自动启动）")
            : !r.Ready ? L.T("文件查找：正在建立全盘索引…")
            : L.F("文件查找：已索引 {0:N0} 个文件，实时更新。索引程序单独运行，不需要管理员权限，不联网。", r.Indexed);
    }
}
