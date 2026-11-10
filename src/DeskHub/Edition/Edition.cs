#if !SETUP
using DeskHub.Core;
using R = System.Windows.Rect;
#endif

namespace DeskHub;

// 版本差异只放在 Edition 文件夹的两个文件里：Edition.props（编译开关、程序名）和本文件。
// 专用版只改这两个文件；合并通用版时，其余文件直接收通用版的。
// 安装程序也编译本文件（Setup.csproj 里链接过来，定义了 SETUP），所以 Brand 不能用到 WPF 的东西。

/// <summary>
/// 名字和标识。两个版本各用各的，才能装在同一台电脑上互不干扰（安装目录、卸载项、数据目录、互斥量、管道、端口都分开）。
/// 程序文件名在 Edition.props 的 AssemblyName，要和 Id 一致。
/// </summary>
static class Brand
{
    /// <summary>显示的产品名，也是安装目录名、卸载项名、快捷方式名。</summary>
    public const string Name = "New B Desk";
    /// <summary>程序文件名（不带 .exe）、数据目录名、开机自启名，也是互斥量、管道名的前缀。</summary>
    public const string Id = "DeskHub";
    public const string Exe = Id + ".exe";

    public const string SingleMutex = @"Local\" + Id + ".Single";
    public const string SandboxMutex = @"Local\" + Id + ".Sandbox";
    public const string ExitSignal = @"Local\" + Id + ".Exit";
    public const string IndexerMutex = @"Local\" + Id + ".Indexer";

    /// <summary>本机 Claude 服务默认端口。</summary>
    public const int ClaudePort = 17860;

    /// <summary>另一个版本的标识：两个版本不能同时开着，启动时要看它在不在运行。</summary>
    public static class Other
    {
        public const string Name = "HOTA DESK";
        public const string Process = "HotaDesk";
        public const string SingleMutex = @"Local\HotaDesk.Single";
        public const string ExitSignal = @"Local\HotaDesk.Exit";
    }
}

#if !SETUP
/// <summary>
/// 本版本的可选项（运行时的默认值、行为开关）。要整个去掉的功能用 Edition.props 的编译开关。
/// </summary>
static partial class Edition
{
    /// <summary>
    /// 工具条上的按钮，从左到右。内置功能只写键名：search 文件查找、shot 截图、calc 计算器、memo 备忘录、calendar 日历、
    /// vault 密码箱、arrange 排列、settings 设置；本版本关掉的功能（Edition.props）对应的键自动跳过。
    /// 自己加的按钮：new DockButton("键", "按钮字", "20×20 线条图标路径", "说明", t => { 点了干什么 })。
    /// </summary>
    public static DockButton[] DockButtons => new DockButton[]
    {
        new("search"), new("shot"), new("calc"), new("memo"), new("calendar"), new("vault"), new("arrange"), new("settings"),
    };

    /// <summary>
    /// 桌面上的固定分区，从上到下就是创建顺序，也是“一个大盒子”里右边那组在前、左边那组在后的标签顺序。
    /// 每项：键、收什么（Zone）、标题、默认位置大小（按 1600×852 设计稿）；其余见 FenceSpec。
    /// 键名会存进用户的布局，改了键名老用户的位置就丢了，别随便改。用户自己建的分区不在这里。
    /// </summary>
    public static FenceSpec[] Fences => new FenceSpec[]
    {
        new("apps", Zone.Apps, L.T("常用软件"), new R(40, 190, 250, 470))
            { Hint = L.T("固定 · 不自动移动"), Sort = FenceSort.SystemFirst, EmptyHint = L.T("桌面上的快捷方式会放在这里"), Side = FenceSide.Left, Column = 0, Rank = 1 },
#if FEAT_FLOW
        new("today", Zone.Today, L.T("今天"), new R(320, 40, 470, 250))
            { Hint = L.T("今天放到桌面的都在这"), Sort = FenceSort.NewestFirst, EmptyHint = L.T("今天还没有新文件"), Side = FenceSide.Right, Column = 2, Rank = 0 },
#endif
        new("folders", Zone.Folders, L.T("项目文件夹"), new R(320, 310, 470, 150))
            { Hint = L.T("固定"), Sort = FenceSort.Name, EmptyHint = L.T("桌面上的文件夹会放在这里"), Side = FenceSide.Left, Column = 0 },
#if FEAT_FLOW
        new("active", Zone.Active, L.T("进行中"), new R(820, 40, 540, 340))
            { Sort = FenceSort.RecentFirst, Side = FenceSide.Right, Column = 1 },
        new("cold", Zone.Cold, L.T("冷却区"), new R(820, 400, 540, 200))
            { Sort = FenceSort.DueFirst, EmptyHint = L.T("没有冷却中的文件"), Side = FenceSide.Right, Column = 2 },
#else
        new("files", Zone.Files, L.T("文件"), new R(820, 40, 540, 560))
            { Hint = L.T("最近用过的在前"), Sort = FenceSort.RecentFirst, EmptyHint = L.T("桌面上的文件会放在这里"), Side = FenceSide.Right, Column = 1 },
#endif
    };

    /// <summary>
    /// 能不能把快捷方式、程序拖到工具条上变成按钮（右键能拿掉）。点这种按钮：已经开着就切过去，没开才启动；
    /// 在运行但只在托盘里的，不再开一个，只在工具条旁边轻轻提示一句。通用版关。
    /// 自己加的按钮也可以用同样的打开方式：new DockButton("键", "字", "图标", "说明", t => t.OpenApp(@"C:\路径\软件.lnk"))。
    /// </summary>
    public static bool DockAcceptsShortcuts => false;
    /// <summary>点工具条不抢别的窗口的焦点（比如正在打字的窗口）。通用版关。</summary>
    public static bool DockNoActivate => false;
    /// <summary>截图、录屏、共享屏幕时看不见工具条（Windows 10 2004 以上才有效，旧系统自动跳过）。通用版关。</summary>
    public static bool DockExcludeFromCapture => false;

    /// <summary>文件查找的索引要不要扫 USB 接的移动硬盘（系统把它当固定磁盘）。通用版扫（和以前一样）。</summary>
    public static bool IndexUsbDisks => true;
}
#endif
