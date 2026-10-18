using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>识别结果里的一行（右边表格，可以直接改）。</summary>
public sealed class ImportRow : INotifyPropertyChanged
{
    bool _use = true;
    public bool Use { get => _use; set { _use = value; PropertyChanged?.Invoke(this, new(nameof(Use))); } }
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Url { get; set; } = "";
    public string Account { get; set; } = "";
    public string Password { get; set; } = "";
    public string Note { get; set; } = "";
    public string Extra { get; init; } = "";
    public string Status { get; init; } = "";
    public List<VaultField> Fields { get; init; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 密码箱 · 智能识别：左边粘贴文字（或打开 Word / Excel / 文本 / 截图），右边自动拆成一条条记录，确认后一起保存。
/// </summary>
public sealed class SmartImportWindow : Window
{
    readonly VaultData _data;
    readonly TextBox _box;
    readonly TextBlock _hint, _count, _status;
    readonly DataGrid _grid;
    readonly ObservableCollection<ImportRow> _rows = new();
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    readonly Button _save;
    List<List<string>>? _tableRows;   // 从 Excel 读进来的表格
    string? _sourceFile;              // 从文件读进来的：保存后问要不要删掉明文文件
    bool _loading;

    public int Added { get; private set; }
    /// <summary>自动测试时不弹“要不要删掉明文文件”。</summary>
    internal bool NoAsk;

    public SmartImportWindow(Window owner, VaultData data)
    {
        _data = data;
        Owner = owner;
        Title = L.T("智能识别 · 密码箱");
        Width = 1120; Height = 680; MinWidth = 900; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = owner.FontFamily; FontSize = 13; Background = Brushes.White;
        ShowInTaskbar = false; UseLayoutRounding = true; AllowDrop = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        // ---- 左边：粘贴 / 打开 ----
        _box = new TextBox
        {
            AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 13, Padding = new Thickness(10, 8, 10, 8),
            BorderBrush = Brush("#DDDDDD"), VerticalContentAlignment = VerticalAlignment.Top,
        };
        _hint = new TextBlock
        {
            Foreground = Brush("#B5B5B5"), Margin = new Thickness(13, 10, 10, 0), IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap, LineHeight = 21,
            Text = L.T("把记账号密码的文字粘贴到这里，随便什么写法都行，例如：\n\n") +
                   L.T("TikTok 后台\n网址：seller.tiktokglobalshop.com\n账号：ops@hota.com   密码：Abc#2026\n\n") +
                   L.T("demo@gmail.com----Pass1234\n飞书 +62 812-0000-0000 Feishu_888\n\n") +
                   L.T("也可以：从 Excel 复制一块表格粘贴进来；\n点下面“打开文件”选 Word / Excel / 文本 / 截图图片；\n或者直接把文件拖进这个窗口。"),
        };
        _box.TextChanged += (_, _) =>
        {
            _hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_loading) { _tableRows = null; }
            _debounce.Stop(); _debounce.Start();
        };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Recognize(); };
        var boxGrid = new Grid();
        boxGrid.Children.Add(_box); boxGrid.Children.Add(_hint);

        var open = new Button { Content = L.T("打开文件…"), Style = Res<Style>("BtnW"), Margin = new Thickness(0, 0, 6, 0) };
        open.Click += async (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("选择包含账号密码的文件"), Filter = DocText.Filter };
            if (dlg.ShowDialog(this) == true) await LoadFile(dlg.FileName);
        };
        var paste = new Button { Content = L.T("粘贴（含截图）"), Style = Res<Style>("BtnW"), Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("剪贴板里是截图也可以，会自动识别图里的文字") };
        paste.Click += async (_, _) => await PasteClipboard();
        var clear = new Button { Content = L.T("清空"), Style = Res<Style>("BtnW") };
        clear.Click += (_, _) => { _sourceFile = null; _box.Clear(); };
        _status = new TextBlock { Foreground = Brush("#999"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var leftBar = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var lb = new StackPanel { Orientation = Orientation.Horizontal };
        lb.Children.Add(open); lb.Children.Add(paste); lb.Children.Add(clear);
        DockPanel.SetDock(lb, Dock.Left);
        leftBar.Children.Add(lb); leftBar.Children.Add(_status);

        var left = new DockPanel();
        var lt = new TextBlock { Text = L.T("① 粘贴或打开"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(lt, Dock.Top); DockPanel.SetDock(leftBar, Dock.Bottom);
        left.Children.Add(lt); left.Children.Add(leftBar); left.Children.Add(boxGrid);

        // ---- 右边：识别结果 ----
        _grid = new DataGrid
        {
            ItemsSource = _rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Brush("#F0F0F0"), BorderBrush = Brush("#DDDDDD"),
            Background = Brushes.White, RowBackground = Brushes.White, MinRowHeight = 30, SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            ColumnHeaderHeight = 32, VerticalGridLinesBrush = Brush("#F0F0F0"),
        };
        var chk = new DataGridCheckBoxColumn { Header = L.T("保存"), Binding = new Binding(nameof(ImportRow.Use)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 46 };
        _grid.Columns.Add(chk);
        Col(L.T("服务名"), nameof(ImportRow.Name), 1.3);
        Col(L.T("分组"), nameof(ImportRow.Group), 0.8);
        Col(L.T("网址"), nameof(ImportRow.Url), 1.4);
        Col(L.T("账号"), nameof(ImportRow.Account), 1.4);
        Col(L.T("密码"), nameof(ImportRow.Password), 1.1, mono: true);
        Col(L.T("备注"), nameof(ImportRow.Note), 1);
        Col(L.T("其他字段"), nameof(ImportRow.Extra), 1, readOnly: true);
        Col("", nameof(ImportRow.Status), 0.6, readOnly: true);
        _grid.CurrentCellChanged += (_, _) => UpdateCount();
        _rows.CollectionChanged += (_, _) => UpdateCount();

        _count = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        var right = new DockPanel();
        DockPanel.SetDock(_count, Dock.Top);
        var rtip = new TextBlock { Text = L.T("表格里可以直接改；不要的取消勾选，或选中后按 Delete 删掉。已经在密码箱里的会自动不勾。"), Foreground = Brush("#999"), FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(rtip, Dock.Bottom);
        right.Children.Add(_count); right.Children.Add(rtip); right.Children.Add(_grid);

        // ---- 底部 ----
        _save = new Button { Content = L.T("保存"), Style = Res<Style>("BtnG"), MinWidth = 120, IsEnabled = false };
        _save.Click += (_, _) => Save();
        var cancel = new Button { Content = L.T("取消"), Style = Res<Style>("BtnW"), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var bottom = new DockPanel { Margin = new Thickness(20, 10, 20, 12) };
        var bb = new StackPanel { Orientation = Orientation.Horizontal };
        bb.Children.Add(_save); bb.Children.Add(cancel);
        DockPanel.SetDock(bb, Dock.Right);
        bottom.Children.Add(bb);
        bottom.Children.Add(new TextBlock { Text = L.T("全部在本机识别，不联网。"), Foreground = Brush("#AAA"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

        var body = new Grid { Margin = new Thickness(20, 16, 20, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.75, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        body.Children.Add(left);
        Grid.SetColumn(right, 2); body.Children.Add(right);
        var root = new DockPanel();
        var line = new Border { BorderBrush = Brush("#E8E8E8"), BorderThickness = new Thickness(0, 1, 0, 0), Child = bottom };
        DockPanel.SetDock(line, Dock.Bottom);
        root.Children.Add(line); root.Children.Add(body);
        Content = root;

        // 文件直接拖进来
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        _box.PreviewDragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        async void OnDrop(object? s, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) { e.Handled = true; await LoadFile(f[0]); }
        }
        Drop += OnDrop;
        _box.PreviewDrop += OnDrop;
        Loaded += (_, _) => _box.Focus();
        // 关窗时把明文清掉
        Closed += (_, _) => { _box.Clear(); _rows.Clear(); _tableRows = null; };
        UpdateCount();
    }

    void Col(string header, string prop, double w, bool mono = false, bool readOnly = false)
    {
        var c = new DataGridTextColumn
        {
            Header = header, Binding = new Binding(prop) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = new DataGridLength(w, DataGridLengthUnitType.Star), IsReadOnly = readOnly,
        };
        var st = new Style(typeof(TextBlock));
        st.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        st.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(4, 0, 4, 0)));
        if (mono) st.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Consolas, Microsoft YaHei UI")));
        if (readOnly) st.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brush("#999")));
        c.ElementStyle = st;
        _grid.Columns.Add(c);
    }

    async Task LoadFile(string path)
    {
        _status.Text = L.T("正在读取 ") + Path.GetFileName(path) + " …";
        try
        {
            var r = await DocText.ReadAsync(path);
            _loading = true;
            _box.Text = r.Text;
            _tableRows = r.Rows;
            _loading = false;
            _sourceFile = path;
            _status.Text = L.T("已读取 ") + Path.GetFileName(path);
            if (r.Text.Trim().Length == 0) _status.Text = L.T("这个文件里没有读到文字");
            Recognize();
        }
        catch (Exception ex) { _loading = false; _status.Text = ""; MessageBox.Show(this, ex.Message, L.T("智能识别")); }
    }

    async Task PasteClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
            {
                _status.Text = L.T("正在识别图片里的文字…");
                var text = await DocText.OcrBest(img);
                _box.Text = string.IsNullOrWhiteSpace(_box.Text) ? text : _box.Text + "\n\n" + text;
                _status.Text = text.Trim().Length == 0 ? L.T("图片里没有识别到文字") : L.T("已识别截图里的文字，请核对右边结果（图片识别可能有错字）");
            }
            else if (Clipboard.ContainsText())
            {
                var t = Clipboard.GetText();
                _box.Text = string.IsNullOrWhiteSpace(_box.Text) ? t : _box.Text + "\n\n" + t;
                _status.Text = "";
            }
            else if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files) await LoadFile(files[0]!);
            else _status.Text = L.T("剪贴板里没有文字或图片");
        }
        catch (Exception ex) { _status.Text = L.T("读取剪贴板失败：") + ex.Message; }
    }

    void Recognize()
    {
        var groups = _data.Groups;
        List<VaultEntry> found;
        if (_tableRows != null)
        {
            found = VaultParser.ParseRows(_tableRows, groups);
            if (found.Count == 0) found = VaultParser.Parse(_box.Text, groups);
        }
        else found = VaultParser.Parse(_box.Text, groups);

        _rows.Clear();
        foreach (var e in found)
        {
            bool dup = _data.Entries.Any(x => x.Account == e.Account && x.Password == e.Password && (x.Url == e.Url || x.Name == e.Name));
            _rows.Add(new ImportRow
            {
                Use = !dup, Name = e.Name, Group = e.Group, Url = e.Url, Account = e.Account, Password = e.Password, Note = e.Note.Replace("\n", "  "),
                Fields = e.Fields, Extra = string.Join("；", e.Fields.Select(f => $"{f.Name}：{(f.Secret ? "••••" : f.Value)}")),
                Status = dup ? L.T("已存在") : "",
            });
        }
        UpdateCount();
    }

    void UpdateCount()
    {
        int n = _rows.Count(r => r.Use);
        _count.Text = _rows.Count == 0
            ? (_box.Text.Trim().Length == 0 ? L.T("② 识别结果") : L.T("② 没认出账号密码 —— 试试每条记录之间空一行，或写上“账号：”“密码：”"))
            : L.F("② 识别出 {0} 条", _rows.Count) + (_rows.Count != n ? L.F("（勾选了 {0} 条）", n) : "");
        _save.Content = n > 0 ? L.F("保存 {0} 条", n) : L.T("保存");
        _save.IsEnabled = n > 0;
    }

    void Save()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var use = _rows.Where(r => r.Use && (r.Account.Trim().Length > 0 || r.Password.Length > 0)).ToList();
        foreach (var r in use)
        {
            var g = r.Group.Trim();
            _data.Entries.Add(new VaultEntry
            {
                Name = r.Name.Trim().Length > 0 ? r.Name.Trim() : L.T("未命名"), Group = g, Url = r.Url.Trim(), Account = r.Account.Trim(), Password = r.Password,
                Note = r.Note.Trim(), Fields = r.Fields,
            });
            if (g.Length > 0 && !_data.Groups.Contains(g)) _data.Groups.Add(g);
        }
        Added = use.Count;
        var src = _sourceFile;
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); } // 非对话框方式打开时（自动测试）
        if (!NoAsk && src != null && File.Exists(src) && !DocText.ImageExts.Contains(Path.GetExtension(src).ToLowerInvariant()) &&
            MessageBox.Show(Owner, L.F("已保存 {0} 条。\n\n“{1}”里的密码是明文，任何人打开都能看到。要把这个文件删掉（放进回收站）吗？", Added, Path.GetFileName(src)),
                L.T("密码箱"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(src, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
            catch (Exception ex) { MessageBox.Show(Owner, L.T("删除失败：") + ex.Message, L.T("密码箱")); }
        }
    }

    // 自动测试用
    internal void TestSetText(string text) { _box.Text = text; Recognize(); }
    internal async Task TestLoad(string path) => await LoadFile(path);
    internal int TestCount => _rows.Count;
    internal IEnumerable<ImportRow> TestRows => _rows;
    internal void TestSave() => Save();

    static T Res<T>(string key) => (T)Application.Current.FindResource(key);
    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
