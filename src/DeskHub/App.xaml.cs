using System.IO;
using System.Windows;
using DeskHub.Core;
using DeskHub.Interop;
using DeskHub.UI;
using Forms = System.Windows.Forms;

namespace DeskHub;

public partial class App : Application
{
    Mutex? _single;
    EventWaitHandle? _exitSignal;
    DeskController? _ctl;
    ToolHub? _tools;
    Forms.NotifyIcon? _tray;
    SettingsWindow? _settings;
    bool _scripted;
    readonly List<string> _notes = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 用 CPU 渲染：分区是静态界面，不需要显卡加速；
        // 走显卡会把整套显卡驱动缓存加载进进程，实测多占约 130MB 内存
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // 沙盒模式：用一个假的桌面目录测试，不碰真实桌面、不隐藏系统图标
        string? sandbox = null;
        for (int i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i] == "--sandbox") sandbox = Path.GetFullPath(e.Args[i + 1]);

        // 换语言重启时（--restart），等上一个进程退出再继续
        _single = new Mutex(false, sandbox == null ? @"Local\DeskHub.Single" : @"Local\DeskHub.Sandbox");
        bool first;
        try { first = _single.WaitOne(e.Args.Contains("--restart") ? 15000 : 0); }
        catch (AbandonedMutexException) { first = true; }
        if (!first) { Shutdown(); return; }
        // 安装 / 卸载程序要关掉正在运行的本工具时，会触发这个信号：正常退出（恢复系统桌面图标、保存状态）
        if (sandbox == null)
        {
            _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DeskHub.Exit");
            ThreadPool.RegisterWaitForSingleObject(_exitSignal, (_, _) => Dispatcher.BeginInvoke(Quit), null, -1, true);
        }

        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log(ex.ExceptionObject as Exception);
            if (sandbox == null) DesktopHost.ShowSystemIcons(); // 万一崩溃，先把桌面图标还回去
        };

        DesktopSource src;
        if (sandbox != null)
        {
            AppConfig.UseDataDir(Path.Combine(sandbox, "data"));
            src = new DesktopSource(Path.Combine(sandbox, "Desktop"), Path.Combine(sandbox, "PublicDesktop"), Path.Combine(sandbox, "Recent"));
        }
        else
        {
            src = new DesktopSource(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Recent));
        }

        // 界面语言：设置里选的，没选就跟随 Windows；测试可以用 --lang xx 指定
        string? lang = null;
        for (int i = 0; i < e.Args.Length - 1; i++) if (e.Args[i] == "--lang") lang = e.Args[i + 1];
        L.Init(lang ?? AppConfig.PeekLanguage());
        L.HookWindows();

        var cfg = AppConfig.Load();
        if (sandbox != null && !File.Exists(Path.Combine(AppConfig.DataDir, "config.json")))
            cfg.ArchiveRoot = Path.Combine(sandbox, "桌面归档");
        cfg.Save();

        _ctl = new DeskController(cfg, StateStore.Load(), src, sandbox != null);
        _ctl.Notify += msg =>
        {
            if (_scripted) { _notes.Add("提示: " + msg); return; } // 自动测试时不弹系统通知
            _tray?.ShowBalloonTip(4000, "New B Desk", msg, Forms.ToolTipIcon.Info);
        };
        _ctl.RequestSettings += () => OpenSettings("rules");
        _ctl.RequestArchiveLog += () => OpenSettings("flow");
        _ctl.RequestPage += OpenSettings;
        _ctl.RequestLayouts += OpenLayouts;
        _tools = new ToolHub(_ctl, sandbox != null);
        _ctl.Tools = _tools;
        BuildTray(sandbox != null);
        _ctl.Start();
        _tools.Start();
        if (_tools.HotkeyErrors.Count > 0)
            _ctl.ShowNote(string.Join("；", _tools.HotkeyErrors.Select(kv => L.F("{0}快捷键{1}", ToolHub.Actions.First(a => a.Key == kv.Key).Name, kv.Value))) + L.T("。可在设置 → 快捷键里修改"));

        // 启动时加载图标等用过的临时内存，15 秒后还给系统
        var trim = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(15) };
        trim.Tick += (_, _) => { trim.Stop(); TrimMemory(); };
        trim.Start();

        // 调试：--script 文件  →  按行执行测试步骤（只在沙盒模式可用）
        for (int i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i] == "--script" && sandbox != null)
                _scripted = true;
        for (int i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i] == "--script" && sandbox != null)
                _ = RunScript(File.ReadAllLines(e.Args[i + 1]), sandbox);

        // 调试：--snapshot 文件名  →  启动 4 秒后把桌面布局导出成图片（只在沙盒模式可用）
        for (int i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i] == "--snapshot" && sandbox != null)
            {
                var file = Path.GetFullPath(e.Args[i + 1]);
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t.Tick += (_, _) => { t.Stop(); try { _ctl.Snapshot(file); } catch (Exception ex) { Log(ex); } };
                t.Start();
            }
    }

    void BuildTray(bool sandbox)
    {
        var menu = new Forms.ContextMenuStrip { Font = new System.Drawing.Font("Microsoft YaHei UI", 9f) };
        menu.Items.Add(L.T("文件查找"), null, (_, _) => _tools!.Search());
        menu.Items.Add(L.T("截图"), null, (_, _) => _tools!.Shot(350)); // 等菜单收起再截
        menu.Items.Add(L.T("计算器"), null, (_, _) => _tools!.Calc());
        menu.Items.Add(L.T("备忘录"), null, (_, _) => _tools!.Memo());
        menu.Items.Add(L.T("密码箱"), null, (_, _) => _tools!.Vault());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("新建分区…"), null, (_, _) => _ctl!.NewFence());
        menu.Items.Add(L.T("排列桌面"), null, (_, _) => _tools!.Arrange(1));
        menu.Items.Add(L.T("设置…"), null, (_, _) => OpenSettings("rules"));
        menu.Items.Add(L.T("立即检查归档"), null, (_, _) => _ctl!.CheckArchive(userAsked: true));
        menu.Items.Add(L.T("归档记录 / 撤回…"), null, (_, _) => OpenSettings("flow"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        var hide = new Forms.ToolStripMenuItem(L.T("隐藏系统桌面图标")) { CheckOnClick = true, Checked = _ctl!.Cfg.HideSystemIcons, Enabled = !sandbox };
        hide.CheckedChanged += (_, _) => _ctl.SetHideSystemIcons(hide.Checked);
        menu.Items.Add(hide);
        menu.Items.Add(L.T("布局：保存 / 切换 / 导出 / 导入…"), null, (_, _) => OpenLayouts());
        menu.Items.Add(L.T("恢复默认布局"), null, (_, _) => _ctl.ResetLayout());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("退出（恢复系统桌面）"), null, (_, _) => Quit());

        var icon = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(icon),
            Text = sandbox ? L.T("New B Desk（沙盒）") : "New B Desk",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => OpenSettings("rules");
    }

    LayoutWindow? _layouts;
    public void OpenLayouts()
    {
        if (_layouts == null)
        {
            _layouts = new LayoutWindow(_ctl!);
            _layouts.Closed += (_, _) => _layouts = null;
        }
        _layouts.Popup();
    }

    void OpenSettings(string page)
    {
        if (_settings != null) { _settings.Activate(); return; }
        _settings = new SettingsWindow(_ctl!, page);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    /// <summary>换语言后重启。</summary>
    public void Restart()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = false }); }
        catch { return; }
        Quit();
    }

    void Quit()
    {
        _ctl?.Stop();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 关机 / 注销时也走这里：确保系统图标恢复、状态写盘
        try { _ctl?.Stop(); } catch { }
        _tray?.Dispose();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        try { _ctl?.State.Save(); } catch { }
        base.OnSessionEnding(e);
    }

    /// <summary>沙盒测试脚本。每行一条：wait 秒 / snapshot 文件 / touch 文件名 / topic 文件名 事项 / archive / undo / quit</summary>
    async Task RunScript(string[] lines, string sandbox)
    {
        var report = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(' ', 3);
            try
            {
                switch (parts[0])
                {
                    case "wait": await Task.Delay(TimeSpan.FromSeconds(double.Parse(parts[1]))); break;
                    case "snapshot": _ctl!.Snapshot(parts[1]); break;
                    case "touch": File.WriteAllText(Path.Combine(sandbox, "Desktop", parts[1]), ""); break;
                    case "topic":
                        var it = _ctl!.Find(Path.Combine(sandbox, "Desktop", parts[1]));
                        if (it == null) { report.Add($"找不到 {parts[1]}"); break; }
                        _ctl.MoveToTopic(it, parts[2], Windows.OfType<FenceWindow>().First(f => f.Key == "active"));
                        break;
                    case "place": // 模拟把文件拖到另一个分区
                        var pi = _ctl!.Find(Path.Combine(sandbox, "Desktop", parts[1]));
                        if (pi == null) { report.Add($"找不到 {parts[1]}"); break; }
                        _ctl.PlaceItem(pi, Windows.OfType<FenceWindow>().First(f => f.Key == parts[2]), null);
                        report.Add($"{parts[1]} → {_ctl.Find(pi.Path)?.Zone}");
                        break;
                    case "learn": // 模拟点"好，记住"
                        foreach (var t in Windows.OfType<LearnToast>().ToList()) t.Yes.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        break;
                    case "archive":
                        _ctl!.Cfg.ArchiveConfirmed = true;
                        _ctl.CheckArchive();
                        break;
                    case "undo":
                        var last = _ctl!.State.ArchiveLog.Where(r => !r.Undone).Select(r => r.Batch).LastOrDefault();
                        if (last != null) { var (ok, fail) = _ctl.Undo(last); report.Add($"撤回 {ok} 成功 {fail} 失败"); }
                        break;
                    case "dump":
                        report.AddRange(Windows.OfType<FenceWindow>().Select(f => $"{f.Key}: {f.CountText.Text}"));
                        report.Add("rules: " + string.Join(" | ", _ctl!.Cfg.Topics.Select(t => $"{t.Name}=[{string.Join(",", t.Learned)}]")));
                        break;
                    case "display": // display 组合名 x,y,宽,高（模拟换显示器）/ display real
                        if (parts[1] == "real") report.Add("display → " + _ctl!.TestDisplay(null, null));
                        else
                        {
                            var n = parts[2].Split(',').Select(double.Parse).ToArray();
                            report.Add("display → " + _ctl!.TestDisplay(parts[1], new Rect(n[0], n[1], n[2], n[3])));
                        }
                        break;
                    case "rects": // 报告每个分区、时钟的位置
                        report.AddRange(_ctl!.CaptureLayout().Select(kv => $"  {kv.Key}: {kv.Value.X:0},{kv.Value.Y:0} {kv.Value.Width:0}x{kv.Value.Height:0}"));
                        break;
                    case "settings": OpenSettings(parts[1]); break;
                    case "layout": // layout save 名字 / apply 名字 / export 文件 / import 文件 / list / shot 文件
                        {
                            var arg2 = parts.Length > 2 ? parts[2] : "";
                            switch (parts[1])
                            {
                                case "save": _ctl!.SaveCurrentLayout(arg2); break;
                                case "apply": _ctl!.ApplySaved(_ctl.Cfg.SavedLayouts.First(x => x.Name == arg2)); break;
                                case "export": _ctl!.ExportLayout(_ctl.CaptureSaved("导出测试"), arg2); break;
                                case "import": report.Add("import → " + _ctl!.ImportLayout(arg2).Name); break;
                                case "list": report.AddRange(_ctl!.Cfg.SavedLayouts.Select(x => $"  {x.Name} items={x.Profile.Items.Count} fences={x.Fences.Count} bind={x.BindSig}")); break;
                                case "bind": _ctl!.BindLayout(_ctl.Cfg.SavedLayouts.First(x => x.Name == arg2), true); break;
                                case "shot": OpenLayouts(); await Task.Delay(400); RenderWindow(_layouts!, arg2); break;
                            }
                        }
                        break;
                    case "sel": // sel 分区key click 序号 [ctrl|shift|ctrlshift] / band x,y,w,h [ctrl] / end / list / del / drag 目标分区key / all
                        {
                            var fw = Windows.OfType<FenceWindow>().First(f => f.Key == parts[1]);
                            var a = (parts.Length > 2 ? parts[2] : "list").Split(' ');
                            switch (a[0])
                            {
                                case "click": fw.TestClick(int.Parse(a[1]), a.Length > 2 && a[2].Contains("ctrl"), a.Length > 2 && a[2].Contains("shift")); break;
                                case "band": { var v = a[1].Split(',').Select(double.Parse).ToArray(); fw.TestBand(new Rect(v[0], v[1], v[2], v[3]), a.Length > 2); } break;
                                case "end": fw.TestEndBand(); break;
                                case "del": _ctl!.DeleteItems(fw.SelectedItems); break;
                                case "drag": fw.TestDragTo(Windows.OfType<FenceWindow>().First(f => f.Key == a[1])); break;
                                case "shot": await Task.Delay(200); RenderWindow(fw, a[1]); break;
                            }
                            foreach (var f in Windows.OfType<FenceWindow>())
                                if (f.SelectedItems.Count > 0) report.Add($"  选中[{f.Key}] {string.Join(", ", f.SelectedItems.Select(i => i.Name))}");
                        }
                        break;
                    case "clocknote": // clocknote 截图文件 文字（文字可含空格，空 = 清掉）
                        _ctl!.Cfg.ClockNote = parts.Length > 2 ? parts[2] : ""; _ctl.ApplyTexts(); await Task.Delay(300); RenderWindow(_ctl.Clock!, parts[1]); break;
                    case "winshot": // 把设置窗口画成图片
                        if (_settings is { } sw)
                        {
                            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(sw);
                            var el = (FrameworkElement)sw.Content;
                            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(el.ActualWidth * dpi.DpiScaleX), (int)(el.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
                            var dv = new System.Windows.Media.DrawingVisual();
                            using (var dc = dv.RenderOpen()) { dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight)); dc.DrawRectangle(new System.Windows.Media.VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight)); }
                            rtb.Render(dv);
                            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                            using var fs = File.Create(parts[1]); enc.Save(fs);
                        }
                        break;
                    case "calc": // 计算器：calc 算式 → 报告结果
                        report.Add(Core.Calc.TryEval(parts[1] + (parts.Length > 2 ? " " + parts[2] : ""), out var cv, out var ce) ? $"calc {parts[1]} = {Core.Calc.Format(cv)}" : $"calc {parts[1]} → {ce}");
                        break;
                    case "search": // search 关键词：启动索引程序并查询（测试用，会真的扫描本机磁盘）
                        Search.IndexerHost.EnsureStarted();
                        Search.QueryResult? qr = null;
                        for (int k = 0; k < 120 && qr is not { Ready: true }; k++)
                        {
                            qr = await Search.IndexerHost.QueryAsync(new Search.QueryRequest { Q = parts[1], Max = 8 }, CancellationToken.None);
                            if (qr is not { Ready: true }) await Task.Delay(1000);
                        }
                        report.Add($"search {parts[1]}: 已索引 {qr?.Indexed} 命中 {qr?.Counts.GetValueOrDefault("all")}");
                        if (qr != null) report.AddRange(qr.Items.Select(h => "  " + h.Path));
                        break;
                    case "tool": // tool search|calc|dock 文件 [输入]：打开小工具并画成图片
                        Window tw = parts[1] switch
                        {
                            "search" => Windows.OfType<SearchWindow>().FirstOrDefault() ?? Do(() => _tools!.Search(), () => Windows.OfType<SearchWindow>().First()),
                            "calc" => Windows.OfType<CalcWindow>().FirstOrDefault() ?? Do(() => _tools!.Calc(), () => Windows.OfType<CalcWindow>().First()),
                            _ => _tools!.Dock!,
                        };
                        if (!tw.IsVisible) tw.Show();
                        var sp = parts[2].Split(' ', 2);
                        if (sp.Length > 1)
                        {
                            if (tw is SearchWindow sw2) { sw2.Box.Text = sp[1]; await Task.Delay(2500); }
                            if (tw is CalcWindow cw2) { foreach (var e2 in sp[1].Split('|')) { cw2.Expr.Text = e2; cw2.Expr.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(cw2)!, 0, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent }); } cw2.Expr.Text = "1200+15%"; }
                        }
                        await Task.Delay(300);
                        RenderWindow(tw, sp[0]);
                        break;
                    case "shot": // shot 文件：进入截图，画出遮罩界面，然后退出
                        _tools!.Shot();
                        await Task.Delay(800);
                        var shw = Windows.OfType<Shot.ShotWindow>().FirstOrDefault(w => w.IsActive) ?? Windows.OfType<Shot.ShotWindow>().First();
                        RenderWindow(shw, parts[1]);
                        Shot.ShotSession.End();
                        break;
                    case "drop": // drop 外部文件路径 分区名：模拟从资源管理器拖进分区
                        var df = Windows.OfType<FenceWindow>().First(f => f.Key == parts[2] || f.TitleText.Text == parts[2]);
                        _ctl!.DropFiles(new[] { parts[1] }, df, null, false, false);
                        break;
                    case "placet": // placet 桌面文件名 分区名（按标题找分区）
                        var pt = _ctl!.Find(Path.Combine(sandbox, "Desktop", parts[1]));
                        if (pt == null) { report.Add($"找不到 {parts[1]}"); break; }
                        _ctl.PlaceItem(pt, Windows.OfType<FenceWindow>().First(f => f.TitleText.Text == parts[2]), null);
                        report.Add($"{parts[1]} → {_ctl.Find(pt.Path)?.Zone} {_ctl.Find(pt.Path)?.Fence}");
                        break;
                    case "ocr": // ocr 文件 倍数：测试文字识别
                        {
                            var bi = new System.Windows.Media.Imaging.BitmapImage(new Uri(parts[1].Split(' ')[0]));
                            System.Windows.Media.Imaging.BitmapSource src = bi;
                            var k = parts.Length > 2 ? double.Parse(parts[2]) : 1;
                            if (k != 1) src = new System.Windows.Media.Imaging.TransformedBitmap(bi, new System.Windows.Media.ScaleTransform(k, k));
                            report.Add($"ocr x{k}: " + Core.VaultParser.TidyOcr(await Shot.Ocr.RecognizeAsync(src)).Replace("\r", "").Replace("\n", " | "));
                        }
                        break;
                    case "parse": // parse 文件：测试智能识别，报告识别结果
                        {
                            var groups = new Core.VaultData().Groups;
                            var txt = File.ReadAllText(parts[1]);
                            foreach (var blk in txt.Replace("\r\n", "\n").Split("=====\n"))
                            {
                                report.Add("—— 样例 ——");
                                foreach (var pe in Core.VaultParser.Parse(blk, groups))
                                    report.Add($"  [{pe.Group}] {pe.Name} | {pe.Url} | {pe.Account} | {pe.Password} | {pe.Note.Replace("\n", " / ")} | {string.Join(" ; ", pe.Fields.Select(f => f.Name + "=" + f.Value + (f.Secret ? "*" : "")))}");
                            }
                        }
                        break;
                    case "vault": // vault 子命令
                        {
                            var vw = Windows.OfType<VaultWindow>().FirstOrDefault();
                            if (vw == null) { _tools!.Vault(); vw = Windows.OfType<VaultWindow>().First(); }
                            var sub = parts[1];
                            var arg = parts.Length > 2 ? parts[2] : "";
                            switch (sub)
                            {
                                case "unlock": report.Add("vault unlock → " + await vw.TestUnlock(arg)); break;
                                case "lock": vw.Lock(); break;
                                case "count": report.Add("vault count → " + vw.TestCount); break;
                                case "add": // add 名字|分组|网址|账号|密码|备注
                                    var f = arg.Split('|');
                                    vw.TestAdd(new Core.VaultEntry { Name = f[0], Group = f[1], Url = f[2], Account = f[3], Password = f[4], Note = f.Length > 5 ? f[5] : "", Fields = { new Core.VaultField { Name = L.T("支付密码"), Value = "135790", Secret = true } } });
                                    break;
                                case "select": var sp2 = arg.Split(' '); vw.TestSelect(int.Parse(sp2[0]), sp2.Length > 1); break;
                                case "edit": vw.TestEdit(); break;
                                case "shot": await Task.Delay(300); RenderWindow(vw, arg); break;
                                case "close": vw.Close(); break;
                                case "smart": // smart 文件|截图：用智能识别读文件，报告结果并保存
                                    {
                                        var a2 = arg.Split('|');
                                        var sw3 = vw.TestSmart();
                                        await sw3.TestLoad(a2[0]);
                                        await Task.Delay(400);
                                        report.Add($"smart {Path.GetFileName(a2[0])} → {sw3.TestCount} 条");
                                        foreach (var r in sw3.TestRows) report.Add($"  {(r.Use ? "✓" : " ")} [{r.Group}] {r.Name} | {r.Url} | {r.Account} | {r.Password} | {r.Note} | {r.Extra} {r.Status}");
                                        if (a2.Length > 1) RenderWindow(sw3, a2[1]);
                                        sw3.TestSave();
                                        vw.TestRefresh();
                                    }
                                    break;
                                case "file": report.Add("vault.dat 含明文? " + File.ReadAllText(Core.Vault.FilePath).Contains(arg)); break;
                            }
                        }
                        break;
                    case "guide": // guide 子命令（备忘录便签）
                        {
                            var sub = parts[1];
                            var arg = parts.Length > 2 ? parts[2] : "";
                            var D = Core.GuideStore.D;
                            switch (sub)
                            {
                                case "add": // add 文字|daily(1/0)|topmost(1/0)|定时|流程|步|x,y,w|tipX,tipY
                                    {
                                        var f = arg.Split('|');
                                        var n = new Core.GuideNote { Text = f[0].Replace("\\n", "\n"), Daily = f[1] == "1", Topmost = f[2] == "1", ShowAt = f[3], Flow = f[4], Step = f[5].Length > 0 ? int.Parse(f[5]) : 0 };
                                        var xy = f[6].Split(',').Select(double.Parse).ToArray(); n.X = xy[0]; n.Y = xy[1]; n.W = xy[2];
                                        if (f.Length > 7 && f[7].Length > 0) { var t = f[7].Split(',').Select(double.Parse).ToArray(); n.TipX = t[0]; n.TipY = t[1]; } else n.Arrow = false;
                                        if (f.Length > 8) n.Color = f[8];
                                        D.Notes.Add(n); Core.GuideStore.Save();
                                    }
                                    break;
                                case "list":
                                    report.AddRange(D.Notes.Select((n, i) => $"  [{i}] {Core.GuideStore.StateOf(n, DateTime.Now)} 窗口={_tools!.Guides.ContainsKey(n.Id)} {n.Text.Replace("\n", "/")} ok={n.OkDay} fin={n.Finished}"));
                                    break;
                                case "ok": Core.GuideStore.Ok(D.Notes[int.Parse(arg)]); break;
                                case "click": // 模拟点 OK 按钮（走 3 秒倒计时）
                                    {
                                        var w = _tools!.Guides.TryGetValue(D.Notes[int.Parse(arg)].Id, out var gw) ? gw : throw new InvalidOperationException("这张便签现在没显示");
                                        var btn = FindChild<System.Windows.Controls.Button>(w);
                                        btn!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                                    }
                                    break;
                                case "geom": foreach (var gw2 in _tools!.Guides.Values) report.Add($"  win {gw2.Left:0},{gw2.Top:0} {gw2.Width:0}x{gw2.Height:0} actual {gw2.ActualWidth:0}x{gw2.ActualHeight:0} content {((FrameworkElement)gw2.Content).ActualWidth:0}x{((FrameworkElement)gw2.Content).ActualHeight:0} dpi {System.Windows.Media.VisualTreeHelper.GetDpi(gw2).DpiScaleX} wa {SystemParameters.WorkArea}"); break;
                                case "lock": D.Locked = arg == "on"; Core.GuideStore.Save(); break;
                                case "yesterday": foreach (var n in D.Notes.Where(n => n.OkDay != null)) n.OkDay = "2000-01-01"; Core.GuideStore.Save(); break;
                                case "shot": await Task.Delay(300); RenderWindow(_tools!.Guides.TryGetValue(D.Notes[int.Parse(arg.Split(' ')[0])].Id, out var gsw) ? gsw : throw new InvalidOperationException("这张便签现在没显示"), arg.Split(' ', 2)[1]); break;
                                case "manager": { var m = _tools!.OpenManagerForTest(); await Task.Delay(400); RenderWindow(m, arg); } break;
                                case "editor": // editor [序号] 截图文件
                                    {
                                        var ea = arg.Split(' ', 2);
                                        int ei = ea.Length == 2 && int.TryParse(ea[0], out var eix) ? eix : 0;
                                        var ed = new GuideEditor(D.Notes[ei], false); ed.Show(); await Task.Delay(500); RenderWindow(ed, ea.Length == 2 && ei.ToString() == ea[0] ? ea[1] : arg); ed.Close();
                                    }
                                    break;
                                case "edit": // edit 序号 流程名(-=不接力) 位置(从0起) 时间(-=一直在)：模拟在编辑窗口里改完点保存
                                    {
                                        var ea = arg.Split(' ');
                                        var ed = new GuideEditor(D.Notes[int.Parse(ea[0])], false);
                                        ed.TestSet(ea[1] == "-" ? "" : ea[1], int.Parse(ea[2]), ea[3] == "-" ? "" : ea[3]);
                                        ed.TestCommit(); ed.Close(); Core.GuideStore.Save();
                                        report.AddRange(D.Notes.Select((n, i) => $"  [{i}] {n.Text} | flow={n.Flow} step={n.Step} at={n.ShowAt}"));
                                    }
                                    break;
                                case "export": Core.GuideStore.Export(arg, SystemParameters.WorkArea); break;
                                case "import": report.Add("import → " + Core.GuideStore.Import(arg, new Rect(0, 0, 1280, 700), true)); break;
                            }
                        }
                        break;
                    case "vaultapi": // vaultapi on|off / answer once|always|deny / shot 文件
                        var va = parts[1];
                        if (va is "on" or "off") { _ctl!.Cfg.VaultApi = va == "on"; _tools!.ApplyVaultApi(); }
                        else if (va == "answer") UI.VaultConsent.TestAnswer = Enum.Parse<UI.VaultConsent.Answer>(parts[2], true);
                        else if (va == "shot") { UI.VaultConsent.TestShot = parts[2]; UI.VaultConsent.Render = RenderWindow; }
                        break;
                    case "apiclient": // apiclient 请求JSON：另起一个 PowerShell 进程当"别的程序"连接口
                        {
                            var ps = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
                            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(sandbox, "client.ps1"), Core.VaultApiServer.PipeNameFor(true), line.Substring("apiclient ".Length) }) ps.ArgumentList.Add(a);
                            using var pr = System.Diagnostics.Process.Start(ps)!;
                            var outTask = pr.StandardOutput.ReadToEndAsync();
                            var errTask = pr.StandardError.ReadToEndAsync();
                            await pr.WaitForExitAsync();
                            report.Add("api → " + (await outTask).Trim() + (await errTask).Trim());
                        }
                        break;
                    case "snap": // snap 分区key或标题 x y：模拟拖到这里，报告吸附后的位置
                        {
                            var sv = parts[2].Split(' ').Select(double.Parse).ToArray();
                            report.Add("snap → " + _ctl!.TestSnap(parts[1], sv[0], sv[1]));
                        }
                        break;
                    case "move": // move 分区key或标题 x y w h：模拟手动摆放
                        var mv = parts[2].Split(' ').Select(double.Parse).ToArray();
                        _ctl!.TestMove(parts[1], mv[0], mv[1], mv[2], mv[3]);
                        break;
                    case "arrange": // arrange（连着点一下）/ arrange 1|2
                        _tools!.Arrange(parts.Length > 1 && int.TryParse(parts[1], out var stp) ? stp : null);
                        await Task.Delay(200);
                        report.Add("layout: " + string.Join("  ", _ctl!.CaptureLayout().Select(kv => $"{kv.Key}=({kv.Value.X:0.#},{kv.Value.Y:0.#},{kv.Value.Right:0.#},{kv.Value.Bottom:0.#})")));
                        break;
                    case "newfence": // newfence 名字：直接建一个自定义分区（跳过输入框）
                        _ctl!.NewFenceNamed(parts[1]);
                        break;
                    case "nav": // 设置窗口切换分页
                        if (_settings != null) ((System.Windows.Controls.RadioButton)_settings.FindName(parts[1])).IsChecked = true;
                        break;
                    case "quit":File.WriteAllLines(Path.Combine(sandbox, "report.txt"), report.Concat(_notes)); Quit(); return;
                }
                report.Add("ok: " + line);
            }
            catch (Exception ex) { report.Add($"失败: {line} → {ex.Message}"); }
        }
        File.WriteAllLines(Path.Combine(sandbox, "report.txt"), report);
    }

    static Window Do(Action a, Func<Window> get) { a(); return get(); }

    static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } r) return r;
        }
        return null;
    }

    static void RenderWindow(Window w, string file)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
        var el = (FrameworkElement)w.Content;
        el.UpdateLayout();
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(el.ActualWidth * dpi.DpiScaleX), (int)(el.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
        var dv = new System.Windows.Media.DrawingVisual();
        // 先铺一层桌面似的底色，再原样画窗口内容（不用 VisualBrush，它会把内容拉伸到铺满）
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3a, 0x5a, 0x7a)), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        rtb.Render(dv);
        rtb.Render(el);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(file); enc.Save(fs);
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);

    public static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        try { EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { }
    }

    static void Log(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDir);
            File.AppendAllText(Path.Combine(AppConfig.DataDir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
