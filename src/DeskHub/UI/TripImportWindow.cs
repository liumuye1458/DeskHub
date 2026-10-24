using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>
/// 智能识别日程：把截图拖进来 / Ctrl+V 粘贴截图、聊天记录、订单文字 → 点“识别” → 列出找到的几条（可改日期、时间、内容，可去掉勾）→ 加入日历。
/// 配了 AI 服务就交给 AI，没配就本机识字 + 规则。
/// </summary>
public sealed class TripImportWindow : Window
{
    const string Ink = "#222222", Mute = "#999999", Red = "#E0524C", Blue = "#3A7BE0";
    const double Shadow = 14, ThumbH = 64;
    static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    readonly AppConfig _cfg;
    readonly Func<DateTime> _today;
    readonly TextBox _input;
    readonly WrapPanel _thumbs = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly List<byte[]> _images = new();
    readonly TextBlock _status = new() { FontSize = 12, Foreground = B(Mute), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    readonly StackPanel _results = new();
    readonly ScrollViewer _resultScroll;
    readonly Border _resultBox;
    readonly Button _go, _add;
    readonly CheckBox _remind;
    readonly List<Row> _rows = new();
    readonly TextBlock _engine = new() { FontSize = 11.5, Foreground = B(Mute), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
    CancellationTokenSource? _cts;

    void ShowEngine()
    {
        _engine.Inlines.Clear();
        bool on = TripAi.Configured(_cfg);
        _engine.Inlines.Add(new System.Windows.Documents.Run(on ? L.F("用 AI 识别：{0}", TripAi.Describe(_cfg)) : L.T("还没配 AI 服务，先用本机识别（不联网，准确度一般）。")));
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(on ? L.T("换个 AI 服务") : L.T("去配置 AI"))) { Foreground = B(Blue), TextDecorations = null, Cursor = Cursors.Hand };
        link.Click += (_, _) => SettingsRequested?.Invoke();
        _engine.Inlines.Add(new System.Windows.Documents.Run(" "));
        _engine.Inlines.Add(link);
    }

    /// <summary>加进日历以后：参数是第一条的日子（日历翻到那个月）。</summary>
    public event Action<DateTime>? Added;
    /// <summary>点了“去配置 AI”。</summary>
    public event Action? SettingsRequested;

    sealed class Row
    {
        public required Trip Trip;
        public required CheckBox On;
    }

    public TripImportWindow(AppConfig cfg, Func<DateTime> today)
    {
        _cfg = cfg; _today = today;
        Title = L.T("智能识别日程");
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true; Topmost = true; SizeToContent = SizeToContent.Height; Width = 460 + Shadow * 2;
        FontFamily = Res<FontFamily>("UiFont"); FontSize = 12.5; UseLayoutRounding = true;
        AllowDrop = true;

        // ---- 标题（按住拖动）+ 关闭 ----
        var close = new TextBlock { Text = "✕", FontSize = 13, Foreground = B(Mute), Cursor = Cursors.Hand, Padding = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        close.MouseLeftButtonUp += (_, _) => Close();
        var head = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(new TextBlock { Text = "✨ " + L.T("智能识别日程"), FontWeight = FontWeights.Bold, FontSize = 14, Foreground = B(Ink) });
        head.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        // ---- 用的是哪个识别（去设置里改了，回到这个窗口时刷新）----
        var engine = _engine;
        ShowEngine();
        Activated += (_, _) => ShowEngine();

        // ---- 输入：文字 + 截图 ----
        var placeholder = new TextBlock
        {
            Text = L.T("把截图拖进来，或者 Ctrl+V 粘贴截图、聊天记录、订单短信……\n例如飞猪 / 携程订单截图、微信里聊好的安排。"),
            Foreground = B("#AAAAAA"), IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(9, 7, 9, 0), FontSize = 12, LineHeight = 19,
        };
        _input = new TextBox
        {
            Style = Res<Style>("WinTextBox"), Height = 110, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 12.5, MaxLength = 20000, AllowDrop = true,
        };
        _input.TextChanged += (_, _) => { placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; UpdateGo(); };
        _input.PreviewDragOver += OnDragOver;
        _input.PreviewDrop += OnDrop;
        var box = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        box.Children.Add(_input); box.Children.Add(placeholder);

        var paste = new Button { Content = L.T("粘贴"), Style = Res<Style>("BtnW"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("把剪贴板里的截图或文字放进来（Ctrl+V）") };
        paste.Click += (_, _) => PasteClipboard();
        var pick = new Button { Content = L.T("选图片…"), Style = Res<Style>("BtnW"), Padding = new Thickness(10, 2, 10, 2) };
        pick.Click += (_, _) => PickFiles();
        _go = new Button { Content = L.T("识别"), Style = Res<Style>("BtnG"), Padding = new Thickness(16, 2, 16, 2), IsEnabled = false };
        _go.Click += async (_, _) => await Run();
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(paste); left.Children.Add(pick);
        var bar = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(_go, Dock.Right);
        bar.Children.Add(_go); bar.Children.Add(left);

        // ---- 结果 ----
        _resultScroll = new ScrollViewer { Content = _results, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _remind = new CheckBox { Style = Res<Style>("Toggle"), IsChecked = true, Content = new TextBlock { Text = L.T("提前一天在桌面弹便签提醒"), FontSize = 12, Foreground = B("#666666") }, VerticalAlignment = VerticalAlignment.Center };
        _add = new Button { Style = Res<Style>("BtnG"), Padding = new Thickness(14, 2, 14, 2) };
        _add.Click += (_, _) => AddAll();
        var foot = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(_add, Dock.Right);
        foot.Children.Add(_add); foot.Children.Add(_remind);
        var resultPanel = new StackPanel();
        resultPanel.Children.Add(new TextBlock { Text = L.T("找到这些（可以直接改；不要的去掉勾）"), FontWeight = FontWeights.SemiBold, Foreground = B(Ink), Margin = new Thickness(0, 0, 0, 6) });
        resultPanel.Children.Add(_resultScroll);
        resultPanel.Children.Add(foot);
        _resultBox = new Border { BorderBrush = B("#EEEEEE"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0), Margin = new Thickness(0, 10, 0, 0), Child = resultPanel, Visibility = Visibility.Collapsed };

        var sp = new StackPanel();
        sp.Children.Add(head); sp.Children.Add(engine); sp.Children.Add(box); sp.Children.Add(_thumbs); sp.Children.Add(bar); sp.Children.Add(_status); sp.Children.Add(_resultBox);
        Content = new Border
        {
            Margin = new Thickness(Shadow), Background = Brushes.White, BorderBrush = B(Blue), BorderThickness = new Thickness(1.2), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 14), Child = sp,
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.35, RenderingBias = RenderingBias.Performance },
        };
        _status.Visibility = Visibility.Collapsed;

        DragOver += OnDragOver;
        Drop += OnDrop;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            // 剪贴板里是截图：收下当图片；是文字就让输入框照常粘贴
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList() || !_input.IsKeyboardFocusWithin))
            { PasteClipboard(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && _go.IsEnabled) { _ = Run(); e.Handled = true; }
        };
        Closed += (_, _) => _cts?.Cancel();
        UpdateGo();
    }

    // ================= 收东西 =================
    /// <summary>从外面直接塞进来（日历上拖进来的、粘贴的）。</summary>
    public void Take(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files) { foreach (var f in files) AddFile(f); }
        else if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bmp) AddImage(bmp);
        else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string s) AppendText(s);
        UpdateGo();
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap) || e.Data.GetDataPresent(DataFormats.UnicodeText) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e) { Take(e.Data); e.Handled = true; }

    void PasteClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img) AddImage(img);
            else if (Clipboard.ContainsFileDropList()) foreach (var f in Clipboard.GetFileDropList()) AddFile(f!);
            else if (Clipboard.ContainsText()) AppendText(Clipboard.GetText());
        }
        catch { Say(L.T("剪贴板正被别的程序占着，稍后再试"), true); }
        UpdateGo();
    }

    void PickFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = L.T("图片或文字") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.txt" };
        if (dlg.ShowDialog(this) == true) foreach (var f in dlg.FileNames) AddFile(f);
        UpdateGo();
    }

    static readonly string[] ImgExt = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    public void AddFile(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ImgExt.Contains(ext))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(path); bmp.EndInit();
                AddImage(bmp);
            }
            else if (ext is ".txt" or ".md" or ".csv" or ".eml" && new FileInfo(path).Length < 200_000) AppendText(File.ReadAllText(path));
            else Say(L.F("{0} 不是图片或文字，跳过了", Path.GetFileName(path)), true);
        }
        catch { Say(L.F("{0} 打不开，跳过了", Path.GetFileName(path)), true); }
    }

    void AppendText(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return;
        _input.Text = _input.Text.Length == 0 ? s : _input.Text.TrimEnd() + "\n" + s;
        _input.CaretIndex = _input.Text.Length;
    }

    /// <summary>收一张图：太大的缩到长边 2000 像素以内（够看清字，发给 AI 也省流量），转成 PNG。</summary>
    void AddImage(BitmapSource src)
    {
        if (_images.Count >= 6) { Say(L.T("一次最多 6 张图"), true); return; }
        double scale = Math.Min(1, 2000.0 / Math.Max(src.PixelWidth, src.PixelHeight));
        BitmapSource bmp = scale < 1 ? new TransformedBitmap(src, new ScaleTransform(scale, scale)) : src;
        // 剪贴板来的图有时带着奇怪的透明通道：统一转成不透明
        bmp = new FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        var bytes = ms.ToArray();
        _images.Add(bytes);

        var x = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = B("#99000000"), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0),
            Child = new TextBlock { Text = "✕", FontSize = 9, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var g = new Grid { Margin = new Thickness(0, 0, 6, 6), Height = ThumbH };
        g.Children.Add(new Border { BorderBrush = B("#DDDDDD"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = new Image { Source = bmp, Height = ThumbH - 2, Stretch = Stretch.Uniform } });
        g.Children.Add(x);
        x.MouseLeftButtonUp += (_, _) => { _images.Remove(bytes); _thumbs.Children.Remove(g); UpdateGo(); };
        _thumbs.Children.Add(g);
    }

    void UpdateGo() => _go.IsEnabled = _cts == null && (_images.Count > 0 || _input.Text.Trim().Length > 0);

    void Say(string text, bool bad = false)
    {
        _status.Text = text;
        _status.Foreground = B(bad ? Red : Mute);
        _status.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================= 识别 =================
    public async Task Run()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        UpdateGo();
        _go.Content = L.T("识别中…");
        ShowEngine();
        ShowResults(new());
        Say(TripAi.Configured(_cfg) ? L.T("正在交给 AI 看，一般几秒到几十秒……") : L.T("正在本机识别……"));
        try
        {
            var r = await TripAi.Extract(_cfg, _input.Text, _images.ToList(), _today(), _cts.Token);
            ShowResults(r.Items);
            Say(r.Items.Count == 0 ? L.T("没找到日期和要做的事。可以多给点内容再试，或者直接在日历上点那一天记一笔。") : r.Note, r.Items.Count == 0);
        }
        catch (TripAi.AiException e) { Say(e.Message, true); }
        catch (OperationCanceledException) { }
        catch (Exception e) { Say(L.F("出错了：{0}", e.Message), true); }
        finally
        {
            _cts = null;
            if (IsLoaded) { _go.Content = L.T("识别"); UpdateGo(); }
        }
    }

    /// <summary>识别出来的结果：按出行时间排好，每条一张卡片（和日历上一模一样），点“改”在下面展开表单。</summary>
    void ShowResults(List<Trip> items)
    {
        _rows.Clear(); _results.Children.Clear();
        items.Sort(TripStore.Order);
        foreach (var it in items) AddRow(it);
        _resultBox.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAdd();
    }

    void AddRow(Trip it)
    {
        var row = new Row { Trip = it, On = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 8, 0) } };
        var cardHost = new ContentControl();
        var edit = new TextBlock { Text = L.T("改"), Foreground = B(Blue), FontSize = 12, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 9, 0, 0) };
        var formHost = new Border { Visibility = Visibility.Collapsed, Margin = new Thickness(24, 4, 0, 4), Padding = new Thickness(10, 2, 10, 10), Background = B("#FAFBFC"), BorderBrush = B("#E6EAF0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        void ShowCard()
        {
            var card = TripCard.Card(row.Trip, dark: false, showDate: true);
            card.Opacity = row.On.IsChecked == true ? 1 : 0.45;
            // 过去的日子提醒一下（多半是识别错了，或者是下单时间）
            if (row.Trip.LastDay < _today()) card.ToolTip = L.T("这天已经过去了");
            cardHost.Content = card;
        }
        ShowCard();
        edit.MouseLeftButtonUp += (_, _) =>
        {
            if (formHost.Visibility == Visibility.Visible) { formHost.Visibility = Visibility.Collapsed; edit.Text = L.T("改"); return; }
            var form = new TripForm(row.Trip, showDate: true);
            form.Changed += () => { if (form.DateOk) { row.Trip = form.Value; ShowCard(); UpdateAdd(); } };
            form.Submit += () => { formHost.Visibility = Visibility.Collapsed; edit.Text = L.T("改"); };
            formHost.Child = form;
            formHost.Visibility = Visibility.Visible;
            edit.Text = L.T("收起");
            form.FocusFirst();
        };
        row.On.Click += (_, _) => { ShowCard(); UpdateAdd(); };

        var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(row.On, Dock.Left); DockPanel.SetDock(edit, Dock.Right);
        dp.Children.Add(row.On); dp.Children.Add(edit); dp.Children.Add(cardHost);
        var sp = new StackPanel();
        sp.Children.Add(dp); sp.Children.Add(formHost);
        _rows.Add(row);
        _results.Children.Add(sp);
    }

    IEnumerable<Row> Picked() => _rows.Where(r => r.On.IsChecked == true && !r.Trip.IsEmpty);

    void UpdateAdd()
    {
        int n = Picked().Count();
        _add.Content = L.F("加入日历（{0} 条）", n);
        _add.IsEnabled = n > 0;
    }

    void AddAll()
    {
        var items = Picked().Select(r => { var t = r.Trip.Clone(); t.Remind = _remind.IsChecked == true; return t; }).ToList();
        if (items.Count == 0) return;
        TripStore.AddMany(items);
        Added?.Invoke(items.Min(i => i.Day));
        Close();
    }


    // ================= 摆放 =================
    /// <summary>放在 owner 旁边（右边放不下就放左边），上沿和 owner 对齐。</summary>
    public void ShowBeside(Rect owner, Rect wa)
    {
        Opacity = 0;
        Show();
        UpdateLayout();
        double w = ActualWidth, h = ActualHeight;
        double x = owner.Right + 6 - Shadow;
        if (x + w - Shadow > wa.Right) x = owner.Left - w + Shadow - 6;
        Left = Math.Clamp(x, wa.Left - Shadow, Math.Max(wa.Left - Shadow, wa.Right - w + Shadow));
        Top = Math.Clamp(owner.Top - Shadow, wa.Top - Shadow, Math.Max(wa.Top - Shadow, wa.Bottom - h - 260 + Shadow));
        Opacity = 1;
        Activate();
        Dispatcher.BeginInvoke(() => _input.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ================= 测试用 =================
    internal void TestText(string s) { AppendText(s.Replace("\\n", "\n")); UpdateGo(); }
    internal string TestStatus => _status.Text + " | " + string.Join(" / ", _rows.Select(r => $"{(r.On.IsChecked == true ? "" : "-")}{r.Trip.Date} [{r.Trip.Kind}] {r.Trip.Display}"));
    internal void TestAdd() => AddAll();
}
