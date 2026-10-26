using System.ComponentModel;
using System.Windows.Media;
using DeskHub.Core;

namespace DeskHub.UI;

public sealed class ItemVM : INotifyPropertyChanged
{
    public DesktopItem Item { get; }
    public ItemVM(DesktopItem item, bool showNew, bool showDays)
    {
        Item = item;
        IsNew = showNew;
        Badge = showDays ? (item.DaysLeft <= 0 ? L.T("待归档") : L.F("{0}天", item.DaysLeft)) : null;
        Faded = item.Zone == Zone.Cold;
        Tip = BuildTip(item);
    }

    public string Name => Item.DisplayName;
    public bool IsNew { get; }
    public string? Badge { get; }
    public bool HasBadge => Badge != null;
    public bool Faded { get; }
    public bool Pinned => Item.Pinned;
    public string Tip { get; }

    ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { _icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); } }

    bool _selected;
    public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }

    // 拖着东西停在它上面、它能接收（文件夹、回收站、程序）：亮起来，和桌面一样
    bool _dropHover;
    public bool DropHover { get => _dropHover; set { if (_dropHover == value) return; _dropHover = value; PropertyChanged?.Invoke(this, new(nameof(DropHover))); } }

    public event PropertyChangedEventHandler? PropertyChanged;

    static string BuildTip(DesktopItem it)
    {
        if (it.IsSystem) return it.Name;
        var lines = new List<string> { it.Name };
        if (!it.IsDir && !it.IsShortcut) lines.Add(L.F("大小：{0}", Size(it.Size)));
        lines.Add(L.F("修改：{0:yyyy-MM-dd HH:mm}", it.LastWrite));
        if (it.Zone is Zone.Today or Zone.Active or Zone.Cold)
        {
            lines.Add(L.F("事项：{0}{1}", it.Topic, (it.Pinned ? L.T("（已钉住，不归档）") : "")));
            lines.Add(L.F("最后碰过：{0:yyyy-MM-dd HH:mm}", it.LastTouch));
        }
        if (it.IsPublic) lines.Add(L.T("位置：公用桌面"));
        return string.Join("\n", lines);
    }

    public static string Size(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:0.##} GB",
    };
}

/// <summary>分区里的事项标签页。</summary>
public sealed class TabVM : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    /// <summary>显示用（"全部""其他"和默认事项名按当前语言显示，Name 仍是原来的值）。</summary>
    public string Display => L.T(Name);
    public int Count { get; init; }
    public Brush Dot { get; init; } = Brushes.Transparent;
    bool _on;
    public bool On { get => _on; set { _on = value; PropertyChanged?.Invoke(this, new(nameof(On))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
