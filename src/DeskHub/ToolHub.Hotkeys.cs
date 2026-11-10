using DeskHub.Core;
using DeskHub.Interop;

namespace DeskHub;

// 全局快捷键（编译开关 FeatHotkeys）
public sealed partial class ToolHub
{
    /// <summary>可以设快捷键的功能：配置里的键名、显示名、默认快捷键。</summary>
    public static (string Key, string Name, string Default)[] Actions => new (string, string, string)[]
    {
        ("shot", L.T("截图"), "Ctrl+Alt+A"),          // 沿用 360 的设置
        ("search", L.T("文件查找"), Hotkeys.DoubleCtrl),
        ("calc", L.T("计算器"), ""),
#if FEAT_CALENDAR
        ("memo", L.T("备忘录"), ""),
        ("calendar", L.T("日历"), ""),
#endif
        ("vault", L.T("密码箱"), ""),
    };

    Hotkeys? _keys;
    public Dictionary<string, string> HotkeyErrors { get; private set; } = new();
    public Hotkeys? Keys => _keys;

    public string HotkeyOf(string key) =>
        _c.Cfg.Hotkeys.TryGetValue(key, out var v) ? v : Actions.First(a => a.Key == key).Default;

    partial void StartHotkeys()
    {
        _keys = new Hotkeys();
        ApplyHotkeys();
        if (HotkeyErrors.Count > 0)
            _c.ShowNote(string.Join("；", HotkeyErrors.Select(kv => L.F("{0}快捷键{1}", Actions.First(a => a.Key == kv.Key).Name, kv.Value))) + L.T("。可在设置 → 快捷键里修改"));
    }

    partial void StopHotkeys() => _keys?.Dispose();

    /// <summary>按配置重新注册快捷键；返回注册失败的（功能名 → 原因）。</summary>
    public Dictionary<string, string> ApplyHotkeys()
    {
        if (_keys == null) return new();
        var list = new List<(string, string, Action)>();
        foreach (var a in Actions)
            if (Builtins.FirstOrDefault(b => b.Key == a.Key) is { } bi) list.Add((a.Key, HotkeyOf(a.Key), bi.Run));
        HotkeyErrors = _keys.Apply(list);
        foreach (var a in Actions)
        {
            var spec = HotkeyOf(a.Key);
            _dock?.SetTip(a.Key, a.Name + (spec.Length == 0 ? "" : spec == Hotkeys.DoubleCtrl ? L.T("（连按两下 Ctrl）") : $"（{spec}）"));
        }
        return HotkeyErrors;
    }

    /// <summary>设置里录快捷键时先停用，免得按下去直接触发截图。</summary>
    public void SuspendHotkeys() => _keys?.Apply(Array.Empty<(string, string, Action)>());
}
