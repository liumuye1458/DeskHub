using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace HotaSetup;

/// <summary>
/// New B Desk 安装 / 卸载程序（单个 exe，主程序打包在里面）。
/// 装在当前用户目录（%LOCALAPPDATA%\Programs\New B Desk），不需要管理员权限：
///   开始菜单 + 桌面快捷方式、开机自启（可选）、“应用和功能”里能卸载。
/// 命令行：
///   （无参数）                    安装界面
///   --uninstall                   卸载界面（安装目录里的 uninstall.exe 用）
///   --silent [--dir 目录]         静默安装；可加 --no-shortcuts --no-autostart --no-launch --no-register --no-close（测试用）
///   --uninstall --silent [--dir]  静默卸载
/// 用户数据（%APPDATA%\DeskHub，含密码箱）默认保留，卸载时可勾选删除。
/// </summary>
static class Program
{
    public const string Name = "New B Desk";
    public const string Exe = "DeskHub.exe";
    const string RunName = "DeskHub";                       // 和主程序设置里的“开机自启”是同一个
    const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\New B Desk";

    public static readonly bool Zh = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    public static string T(string zh, string en) => Zh ? zh : en;
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskHub");
    static string StartMenuLnk => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk");
    static string DesktopLnk => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk");

    [STAThread]
    static int Main(string[] args)
    {
        bool Has(string a) => args.Contains(a, StringComparer.OrdinalIgnoreCase);
        string? Arg(string a) { var i = Array.FindIndex(args, x => x.Equals(a, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

        // 装在 Program Files 这类要管理员权限的位置时，由 RunElevated 拉起的那一份：只管复制 / 删除文件
        if (Arg("--elevated") is { } what && Arg("--dir") is { } edir)
        {
            try
            {
                if (what == "install") { CloseRunning(); CopyFiles(edir); }
                else RemoveFiles(edir, selfDelete: true);
                return 0;
            }
            catch (Exception ex) { try { if (Arg("--err") is { } ef) File.WriteAllText(ef, ex.Message); } catch { } return 1; }
        }

        if (Has("--silent"))
        {
            try
            {
                var dir = Arg("--dir") ?? Installed()?.Dir ?? DefaultDir;
                if (Has("--uninstall")) Uninstall(dir, deleteData: false, close: !Has("--no-close"), selfDelete: true, _ => { });
                else Install(new Options(dir, !Has("--no-shortcuts"), !Has("--no-shortcuts"), !Has("--no-autostart"), !Has("--no-launch"), !Has("--no-register"), !Has("--no-close")), _ => { });
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        Window w = Has("--uninstall") ? new UninstallWindow(Path.GetDirectoryName(Environment.ProcessPath!)!) : new InstallWindow();
        if (Arg("--shot") is { } png) // 测试用：把界面画成图片，不显示窗口
        {
            var root = (FrameworkElement)w.Content;
            w.Content = null;
            var host = new Border { Child = root, Background = Brushes.White, Width = 560 };
            host.Measure(new Size(560, double.PositiveInfinity)); host.Arrange(new Rect(host.DesiredSize)); host.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)(host.ActualWidth * 1.5), (int)(host.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            bmp.Render(host);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(png); enc.Save(fs);
            return 0;
        }
        return app.Run(w);
    }

    // ================= 已安装信息 =================
    public record InstalledInfo(string Dir, string Version);

    public static InstalledInfo? Installed()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstKey);
            if (k?.GetValue("InstallLocation") is string d && File.Exists(Path.Combine(d, Exe)))
                return new InstalledInfo(d, k.GetValue("DisplayVersion") as string ?? "");
        }
        catch { }
        return null;
    }

    // ================= 安装 =================
    public record Options(string Dir, bool StartMenu, bool Desktop, bool AutoStart, bool Launch, bool Register = true, bool Close = true);

    public static void Install(Options o, Action<string> step)
    {
        if (o.Close) { step(T("正在关闭正在运行的 New B Desk…", "Closing running New B Desk…")); CloseRunning(); }

        step(T("正在复制文件…", "Copying files…"));
        if (CanWrite(o.Dir)) CopyFiles(o.Dir);
        else { step(T("这个位置要管理员权限，请在 Windows 弹出的窗口里点“是”…", "This location needs admin rights — click Yes in the Windows prompt…")); RunElevated("install", o.Dir); }

        var exe = Path.Combine(o.Dir, Exe);
        var uninst = Path.Combine(o.Dir, "uninstall.exe");
        step(T("正在创建快捷方式…", "Creating shortcuts…"));
        if (o.StartMenu) Shortcut(StartMenuLnk, exe, o.Dir);
        if (o.Desktop) Shortcut(DesktopLnk, exe, o.Dir);

        if (o.Register)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(UninstKey))
            {
                k.SetValue("DisplayName", Name);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "HOTA");
                k.SetValue("DisplayIcon", exe);
                k.SetValue("InstallLocation", o.Dir);
                k.SetValue("UninstallString", $"\"{uninst}\" --uninstall");
                k.SetValue("QuietUninstallString", $"\"{uninst}\" --uninstall --silent");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(DirSize(o.Dir) / 1024), RegistryValueKind.DWord);
            }
            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (o.AutoStart) run.SetValue(RunName, $"\"{exe}\"");
            else if (run.GetValue(RunName) is string v && v.Contains(o.Dir, StringComparison.OrdinalIgnoreCase)) run.DeleteValue(RunName, false);
        }

        if (o.Launch)
        {
            step(T("正在启动…", "Starting…"));
            // 安装程序被“以管理员身份运行”时，直接启动的主程序也带管理员权限，
            // Windows 就不让它和资源管理器、任务栏互相拖放了：交给资源管理器按普通权限启动
            if (IsElevated) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = o.Dir });
        }
        step(T("安装完成", "Done"));
    }

    static void CopyFiles(string dir)
    {
        Directory.CreateDirectory(dir);
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new InvalidOperationException("payload missing"))
        using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
        {
            foreach (var e in zip.Entries)
            {
                var dest = Path.GetFullPath(Path.Combine(dir, e.FullName));
                if (!dest.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue;
                if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                Retry(() => e.ExtractToFile(dest, true));
            }
        }
        // 卸载程序 = 安装程序自己的一份拷贝
        var uninst = Path.Combine(dir, "uninstall.exe");
        if (!string.Equals(Environment.ProcessPath, uninst, StringComparison.OrdinalIgnoreCase))
            Retry(() => File.Copy(Environment.ProcessPath!, uninst, true));
    }

    static bool IsElevated
    {
        get { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
    }

    /// <summary>当前权限能不能在这个目录（或它最近的已存在上级）里写文件。Program Files 之类的普通用户写不了。</summary>
    public static bool CanWrite(string dir)
    {
        try
        {
            var d = Path.GetFullPath(dir);
            while (!Directory.Exists(d)) { d = Path.GetDirectoryName(d); if (d == null) return true; }
            using (File.Create(Path.Combine(d, ".nbd_" + Guid.NewGuid().ToString("N")), 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch { return true; }
    }

    /// <summary>
    /// 以管理员身份再起一份自己，只做文件的复制 / 删除（Windows 会弹一次确认）。
    /// 快捷方式、注册表、启动主程序仍由当前这份按普通权限做，免得主程序带着管理员权限跑。
    /// </summary>
    static void RunElevated(string what, string dir)
    {
        var err = Path.Combine(Path.GetTempPath(), "nbd_setup_" + Guid.NewGuid().ToString("N") + ".txt");
        var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--elevated {what} --dir \"{dir.TrimEnd('\\')}\" --err \"{err}\"") { UseShellExecute = true, Verb = "runas" };
        Process p;
        try { p = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException(T($"没有拿到管理员权限，写不进 {dir}。可以再点一次并在弹窗里点“是”，或点“更改…”换个位置。",
                                                  $"Admin rights were not granted, so {dir} can't be written. Try again and click Yes, or choose another location."));
        }
        p.WaitForExit();
        if (p.ExitCode == 0) return;
        string m = "";
        try { m = File.ReadAllText(err); File.Delete(err); } catch { }
        throw new InvalidOperationException(m != "" ? m : T("管理员权限下的复制没有成功。", "Copying with admin rights failed."));
    }

    // ================= 卸载 =================
    public static void Uninstall(string dir, bool deleteData, bool close, bool selfDelete, Action<string> step)
    {
        if (close) { step(T("正在关闭 New B Desk…", "Closing New B Desk…")); CloseRunning(); }
        step(T("正在删除快捷方式和注册信息…", "Removing shortcuts…"));
        foreach (var l in new[] { StartMenuLnk, DesktopLnk }) try { if (File.Exists(l)) File.Delete(l); } catch { }
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (run?.GetValue(RunName) is string v && v.Contains(dir, StringComparison.OrdinalIgnoreCase)) run.DeleteValue(RunName, false);
        }
        catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); } catch { }
        if (deleteData) { step(T("正在删除数据…", "Removing data…")); try { Directory.Delete(DataDir, true); } catch { } }

        step(T("正在删除程序文件…", "Removing files…"));
        if (CanWrite(dir)) RemoveFiles(dir, selfDelete);
        else RunElevated("uninstall", dir);
        step(T("卸载完成", "Uninstalled"));
    }

    static void RemoveFiles(string dir, bool selfDelete)
    {
        var self = Environment.ProcessPath!;
        foreach (var f in Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories) : Array.Empty<string>())
            if (!string.Equals(f, self, StringComparison.OrdinalIgnoreCase)) try { File.Delete(f); } catch { }
        // 自己正在运行，删不掉：由命令行每秒试一次，等卸载窗口关掉（最多 5 分钟）再把整个目录删掉
        if (selfDelete && self.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c for /l %i in (1,1,300) do (ping 127.0.0.1 -n 2 > nul & rmdir /s /q \"{dir}\" 2> nul & if not exist \"{dir}\" exit)") { CreateNoWindow = true, UseShellExecute = false });
        else try { Directory.Delete(dir, true); } catch { }
    }

    /// <summary>先请正在运行的主程序自己退出（会恢复系统桌面图标、保存状态），10 秒没退再强制结束。</summary>
    static void CloseRunning()
    {
        var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Exe));
        if (procs.Length == 0) return;
        try { using var ev = EventWaitHandle.OpenExisting(@"Local\DeskHub.Exit"); ev.Set(); } catch { }
        foreach (var p in procs)
        {
            try { if (!p.WaitForExit(10000)) { p.Kill(true); p.WaitForExit(3000); } } catch { }
        }
    }

    static void Retry(Action a)
    {
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch (IOException) when (i < 10) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) when (i < 10) { Thread.Sleep(500); }
        }
    }

    static long DirSize(string d) { try { return Directory.GetFiles(d, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); } catch { return 0; } }

    static void Shortcut(string lnk, string target, string workDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        var t = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic sh = Activator.CreateInstance(t)!;
        try
        {
            dynamic s = sh.CreateShortcut(lnk);
            s.TargetPath = target;
            s.WorkingDirectory = workDir;
            s.IconLocation = target + ",0";
            s.Description = Name;
            s.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sh); }
    }

    // ================= 界面公用 =================
    public static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x07, 0xC2, 0x55));
    public static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    public static ImageSource Icon => BitmapFrame.Create(new Uri("pack://application:,,,/app.ico"));
    /// <summary>图标里最大的一档（界面上大图用；直接用 Icon 会取到小尺寸再放大，发糊）。</summary>
    public static ImageSource BigIcon => BitmapDecoder.Create(new Uri("pack://application:,,,/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.OrderByDescending(f => f.PixelWidth).First();

    public static Button Btn(string text, bool primary)
    {
        var b = new Button { Content = text, MinWidth = 88, Height = 34, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(8, 0, 0, 0), FontSize = 14, Cursor = System.Windows.Input.Cursors.Hand };
        if (primary) { b.Background = Green; b.Foreground = Brushes.White; b.BorderBrush = Green; b.IsDefault = true; }
        else { b.Background = Brushes.White; b.BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)); }
        return b;
    }

    public static Grid Header(string sub)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new Image { Source = BigIcon, Width = 64, Height = 64, Margin = new Thickness(0, 0, 16, 0) });
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = Name, FontSize = 24, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)) });
        sp.Children.Add(new TextBlock { Text = sub, Foreground = Gray, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(sp, 1);
        g.Children.Add(sp);
        return g;
    }

    public static void Style(Window w, string title)
    {
        w.Title = title;
        w.Icon = Icon;
        w.Width = 560; w.SizeToContent = SizeToContent.Height;
        w.ResizeMode = ResizeMode.NoResize;
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Background = Brushes.White;
        w.FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        w.FontSize = 13;
    }
}

/// <summary>安装界面：位置 + 三个选项 → 安装 → 完成。</summary>
sealed class InstallWindow : Window
{
    readonly TextBox _dir = new() { Height = 32, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0) };
    readonly CheckBox _desk = new() { IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    readonly CheckBox _auto = new() { IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    readonly CheckBox _run = new() { IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    readonly ProgressBar _bar = new() { Height = 6, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 16, 0, 0), Foreground = Program.Green };
    readonly TextBlock _status = new() { Foreground = Program.Gray, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly Button _ok, _cancel;
    readonly StackPanel _form = new();
    bool _done;

    public InstallWindow()
    {
        var old = Program.Installed();
        Program.Style(this, Program.T("安装 New B Desk", "Install New B Desk"));
        _dir.Text = old?.Dir ?? Program.DefaultDir;
        _desk.Content = Program.T("创建桌面快捷方式", "Create desktop shortcut");
        _auto.Content = Program.T("开机自动启动", "Start with Windows");
        _run.Content = Program.T("安装完成后立即运行", "Run after install");

        var sub = old == null
            ? Program.T($"版本 {Program.Version} · 桌面整理工作台", $"Version {Program.Version} · Desktop organizer")
            : Program.T($"已安装 {old.Version}，将更新到 {Program.Version}。你的数据和设置会保留。", $"{old.Version} is installed and will be updated to {Program.Version}. Your data and settings are kept.");

        var change = Program.Btn(Program.T("更改…", "Browse…"), false);
        change.Click += (_, _) =>
        {
            var d = new OpenFolderDialog { Title = Program.T("选择安装位置", "Choose install location") };
            if (d.ShowDialog(this) == true) _dir.Text = d.FolderName.TrimEnd('\\').EndsWith(Program.Name, StringComparison.OrdinalIgnoreCase) ? d.FolderName : Path.Combine(d.FolderName, Program.Name);
        };
        var dirRow = new DockPanel();
        DockPanel.SetDock(change, Dock.Right);
        dirRow.Children.Add(change); dirRow.Children.Add(_dir);

        _form.Children.Add(new TextBlock { Text = Program.T("安装位置", "Install location"), Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), Margin = new Thickness(0, 0, 0, 6) });
        _form.Children.Add(dirRow);
        _form.Children.Add(_desk); _form.Children.Add(_auto); _form.Children.Add(_run);
        var note = new TextBlock { Foreground = Program.Gray, FontSize = 12, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap };
        void Note() => note.Text = Program.CanWrite(_dir.Text.Trim())
            ? Program.T("装在当前用户下，不需要管理员权限；不联网。", "Installs for the current user only, no admin rights needed, no internet.")
            : Program.T("这个位置要管理员权限：开始后 Windows 会弹窗确认一次。不联网。", "This location needs admin rights: Windows will ask once. No internet.");
        Note();
        _dir.TextChanged += (_, _) => Note();
        _form.Children.Add(note);

        _cancel = Program.Btn(Program.T("取消", "Cancel"), false);
        _ok = Program.Btn(old == null ? Program.T("安装", "Install") : Program.T("更新", "Update"), true);
        _cancel.Click += (_, _) => Close();
        _ok.Click += async (_, _) =>
        {
            if (_done) { Close(); return; }
            var o = new Program.Options(_dir.Text.Trim(), true, _desk.IsChecked == true, _auto.IsChecked == true, _run.IsChecked == true);
            _form.IsEnabled = false; _ok.IsEnabled = false; _cancel.IsEnabled = false;
            _bar.Visibility = Visibility.Visible;
            try
            {
                await Task.Run(() => Program.Install(o, s => Dispatcher.Invoke(() => _status.Text = s)));
                _bar.Visibility = Visibility.Collapsed;
                _status.Text = Program.T("安装完成。开始菜单和桌面上都有 New B Desk。", "Installed. New B Desk is in the Start menu.");
                _status.Foreground = Program.Green;
                _ok.Content = Program.T("完成", "Finish"); _ok.IsEnabled = true; _done = true;
            }
            catch (Exception ex)
            {
                _bar.Visibility = Visibility.Collapsed;
                _status.Text = Program.T("安装失败：", "Install failed: ") + ex.Message;
                _status.Foreground = Brushes.IndianRed;
                _form.IsEnabled = true; _ok.IsEnabled = true; _cancel.IsEnabled = true;
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(_cancel); buttons.Children.Add(_ok);

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 22) };
        root.Children.Add(Program.Header(sub));
        root.Children.Add(_form);
        root.Children.Add(_bar);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        Content = root;
    }
}

/// <summary>卸载界面：确认 → 可选删除数据 → 卸载。</summary>
sealed class UninstallWindow : Window
{
    public UninstallWindow(string dir)
    {
        Program.Style(this, Program.T("卸载 New B Desk", "Uninstall New B Desk"));
        var data = new CheckBox
        {
            Content = Program.T("同时删除我的数据（设置、备忘录、密码箱等，删除后无法恢复）", "Also delete my data (settings, memos, password vault — cannot be undone)"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        var status = new TextBlock { Foreground = Program.Gray, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
        var cancel = Program.Btn(Program.T("取消", "Cancel"), false);
        var ok = Program.Btn(Program.T("卸载", "Uninstall"), true);
        bool done = false;
        cancel.Click += (_, _) => Close();
        ok.Click += async (_, _) =>
        {
            if (done) { Close(); return; }
            bool del = data.IsChecked == true;
            if (del && MessageBox.Show(this, Program.T("密码箱里的密码也会一起删除，确定吗？", "Passwords in the vault will be deleted too. Continue?"), Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            ok.IsEnabled = cancel.IsEnabled = data.IsEnabled = false;
            try
            {
                await Task.Run(() => Program.Uninstall(dir, del, close: true, selfDelete: true, s => Dispatcher.Invoke(() => status.Text = s)));
                status.Text = Program.T("已卸载。", "Uninstalled.");
                ok.Content = Program.T("关闭", "Close"); ok.IsEnabled = true; done = true;
            }
            catch (Exception ex)
            {
                status.Text = Program.T("卸载没有完成：", "Uninstall incomplete: ") + ex.Message;
                status.Foreground = Brushes.IndianRed;
                ok.IsEnabled = cancel.IsEnabled = data.IsEnabled = true;
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 22) };
        root.Children.Add(Program.Header(Program.T("要把 New B Desk 从这台电脑上卸载吗？桌面上的文件不会被动。", "Remove New B Desk from this PC? Files on your desktop are not touched.")));
        root.Children.Add(data);
        root.Children.Add(status);
        root.Children.Add(buttons);
        Content = root;
    }
}
