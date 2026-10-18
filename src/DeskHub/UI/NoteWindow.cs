using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DeskHub.Core;
using DeskHub.Interop;

namespace DeskHub.UI;

/// <summary>钉到桌面的便签：显示一个清单的待办，可以直接勾选、添加。和分区一样贴在桌面上。</summary>
public sealed class NoteWindow : Window
{
    public string ListId { get; }
    public event Action? Moved;
    readonly StackPanel _items = new();
    readonly TextBlock _title = new() { FontWeight = FontWeights.Bold, FontSize = 13.5, Foreground = Brush("#5C4A00"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox _add;
    readonly TextBlock _addHint;

    public NoteWindow(MemoList list, Action openMemo, Action unpin)
    {
        ListId = list.Id;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; ShowActivated = false;
        Width = 270; SizeToContent = SizeToContent.Height;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"); FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var menu = new TextBlock { Text = "⋯", FontSize = 16, Foreground = Brush("#8A7A30"), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 2, 2), ToolTip = L.T("便签菜单") };
        menu.MouseLeftButtonUp += (_, _) =>
        {
            var m = new ContextMenu { PlacementTarget = menu, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var a = new MenuItem { Header = L.T("打开备忘录") }; a.Click += (_, _) => openMemo(); m.Items.Add(a);
            var b = new MenuItem { Header = L.T("从桌面取下（清单还在）") }; b.Click += (_, _) => unpin(); m.Items.Add(b);
            m.IsOpen = true;
        };
        var head = new DockPanel { Margin = new Thickness(12, 9, 8, 6), Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
        DockPanel.SetDock(menu, Dock.Right);
        head.Children.Add(menu);
        head.Children.Add(_title);
        head.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { openMemo(); return; } DragMove(); Moved?.Invoke(); };

        _add = new TextBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(2, 4, 2, 4), Foreground = Brush("#333") };
        _addHint = new TextBlock { Text = L.T("＋ 添加，回车保存"), Foreground = Brush("#A8984F"), IsHitTestVisible = false, Margin = new Thickness(4, 4, 0, 0) };
        _add.TextChanged += (_, _) => _addHint.Visibility = _add.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _add.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || _add.Text.Trim().Length == 0 || MemoStore.I.Find(ListId) is not { } l) return;
            l.Items.Add(new MemoItem { Text = _add.Text.Trim() });
            _add.Text = "";
            MemoStore.I.Save();
        };
        var addGrid = new Grid { Margin = new Thickness(12, 4, 12, 10) };
        addGrid.Children.Add(_add); addGrid.Children.Add(_addHint);

        var scroll = new ScrollViewer { Content = _items, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(6, 0, 6, 0) };
        var stack = new StackPanel();
        stack.Children.Add(head); stack.Children.Add(scroll); stack.Children.Add(addGrid);
        Content = new Border
        {
            Child = stack, Background = Brush("#FFF6C7"), BorderBrush = Brush("#EADB8F"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(6),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.25, RenderingBias = RenderingBias.Performance },
        };

        SourceInitialized += (_, _) => DesktopHost.Pin(this);
        // 贴在桌面上的窗口默认不抢焦点；点添加框时要能打字
        _add.PreviewMouseLeftButtonDown += (_, _) => { Activate(); _add.Focus(); };
        MemoStore.Changed += Build;
        Closed += (_, _) => MemoStore.Changed -= Build;
        Build();
    }

    void Build()
    {
        if (MemoStore.I.Find(ListId) is not { } l) return;
        _title.Text = l.Name;
        _items.Children.Clear();
        foreach (var i in MemoUI.Ordered(l).Where(i => !i.Done || i.DoneAt?.Date == DateTime.Today))
            _items.Children.Add(MemoUI.Row(this, l, i, dark: true, showList: false));
        if (_items.Children.Count == 0)
            _items.Children.Add(new TextBlock { Text = L.T("没有待办 🎉"), Foreground = Brush("#A8984F"), Margin = new Thickness(8, 4, 0, 4) });
    }

    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}

/// <summary>到点提醒：右下角弹出，可以直接标完成或稍后再提醒。</summary>
public sealed class ReminderToast : Window
{
    static readonly List<ReminderToast> Open = new();

    public ReminderToast(MemoList list, MemoItem it)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; Topmost = true; ShowActivated = false;
        Width = 340; SizeToContent = SizeToContent.Height;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"); FontSize = 13;
        UseLayoutRounding = true;

        Button B(string t, string style, Action a)
        {
            var b = new Button { Content = t, Style = (Style)Application.Current.FindResource(style), Margin = new Thickness(6, 0, 0, 0), MinWidth = 64 };
            b.Click += (_, _) => { a(); Close(); };
            return b;
        }
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bar.Children.Add(B(L.T("10 分钟后"), "BtnW", () => { it.Remind = DateTime.Now.AddMinutes(10); it.Reminded = false; MemoStore.I.Save(); }));
        bar.Children.Add(B(L.T("知道了"), "BtnW", () => { }));
        bar.Children.Add(B(L.T("完成"), "BtnG", () => { MemoStore.I.SetDone(it, true); MemoStore.I.Save(); }));

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = L.F("⏰ 提醒 · {0}", list.Name), Foreground = Brush("#E47600"), FontSize = 12 });
        stack.Children.Add(new TextBlock { Text = it.Text, Foreground = Brush("#222"), FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        if (it.File != null) stack.Children.Add(new TextBlock { Text = "📎 " + System.IO.Path.GetFileName(it.File), Foreground = Brush("#07A548"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        stack.Children.Add(bar);
        Content = new Border
        {
            Child = stack, Background = Brushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 14, 16, 14), Margin = new Thickness(10),
            BorderBrush = Brush("#E8E8E8"), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.3, RenderingBias = RenderingBias.Performance },
        };
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 8;
            Top = wa.Bottom - ActualHeight - 8 - Open.Where(o => o != this).Sum(o => o.ActualHeight);
            SystemSounds.Asterisk.Play();
        };
        Open.Add(this);
        Closed += (_, _) => Open.Remove(this);
    }

    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
