using System.Windows;
using DeskHub.UI;

namespace DeskHub.Core;

/// <summary>两翼留白摆法里放左边还是右边。</summary>
public enum FenceSide { Left, Right }

/// <summary>分区里的排序。</summary>
public enum FenceSort
{
    /// <summary>按名字。</summary>
    Name,
    /// <summary>系统图标（此电脑、回收站…）在前，其余按名字。</summary>
    SystemFirst,
    /// <summary>新放上桌面的在前。</summary>
    NewestFirst,
    /// <summary>最近碰过的在前。</summary>
    RecentFirst,
    /// <summary>快到期（快归档）的在前。</summary>
    DueFirst,
}

/// <summary>
/// 分区标题栏上多出来的一个按钮（可选）。Icon 是一个字：Segoe Fluent Icons 的图标字（如 ""）或普通字/emoji。
/// Click 拿到点的是哪个分区和控制器。
/// </summary>
public sealed record FenceButton(string Icon, string Tip, Action<FenceWindow, DeskController> Click);

/// <summary>
/// 一个固定分区的规格（Edition.Fences 里列出本版本有哪些）。
/// Zone 决定收什么东西：Apps 快捷方式和系统图标、Folders 文件夹、Today / Active / Cold 按时间流转的文件（要有事项分类和流转），
/// Files 全部散放的文件（没有流转时用）。本版本会出现的每种东西都要有一个分区收，否则不显示。
/// Default 是按 1600×852 设计稿的位置大小，实际按工作区等比缩放。
/// </summary>
public sealed record FenceSpec(string Key, Zone Zone, string Title, Rect Default)
{
    /// <summary>标题栏右边的短说明（不填时：进行中、冷却区按设置里的天数自动写）。在盒子里时挪到标签的提示里。</summary>
    public string? Hint { get; init; }
    /// <summary>标题下面一直显示的一行说明（可选，盒子里也显示）。</summary>
    public string? Note { get; init; }
    public string EmptyHint { get; init; } = "";
    public FenceSort Sort { get; init; } = FenceSort.Name;
    /// <summary>两翼留白、一个大盒子摆法里放哪边。</summary>
    public FenceSide Side { get; init; }
    /// <summary>“铺满”摆法里放第几列（0 左、1 中、2 右）。</summary>
    public int Column { get; init; }
    /// <summary>同一列里的先后（小的在上；时钟是 0，自建分区是 2）。</summary>
    public int Rank { get; init; } = 9;
    public FenceButton? Button { get; init; }
}
