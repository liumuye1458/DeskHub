namespace DeskHub;

/// <summary>
/// 程序入口。同一个 exe 有两种身份：
///   不带参数 → HOTA DESK（界面）
///   --indexer 进程号 → 文件索引程序（没有界面，由界面程序启动，界面程序退出它就退出）
/// 索引程序不加载 WPF，省内存。
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--indexer" && int.TryParse(args[1], out var parent))
            return Search.IndexerHost.Run(parent);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
