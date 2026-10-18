using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DeskHub.Core;
using DeskHub.Interop;

namespace DeskHub.UI;

/// <summary>计算器：小浮窗，可以直接打整条算式，上方保留历史，点结果复制。只做四则运算和百分比。</summary>
public partial class CalcWindow : Window
{
    readonly ObservableCollection<string> _history = new();
    readonly List<string> _historyResults = new();
    readonly DispatcherTimer _tipReset = new() { Interval = TimeSpan.FromSeconds(1.5) };
    static string TipText => L.T("支持键盘直接输入 · Enter 计算 · 点结果复制");

    public event Action? Moved;

    public CalcWindow()
    {
        InitializeComponent();
        History.ItemsSource = _history;
        TitleBar.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) { DragMove(); Moved?.Invoke(); } };
        CloseBtn.Click += (_, _) => Hide();
        MinBtn.Click += (_, _) => Hide();
        Expr.TextChanged += (_, _) => Preview();
        Expr.PreviewKeyDown += OnKey;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { if (Expr.Text.Length > 0) Clear(); else Hide(); e.Handled = true; } };
        Result.MouseLeftButtonUp += (_, _) => CopyResult();
        History.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is string line && _history.IndexOf(line) is var i and >= 0)
                SetExpr(_historyResults[i]);
        };
        foreach (Button b in Keys.Children) b.Click += (_, _) => Press((string)b.Tag);
        _tipReset.Tick += (_, _) => { _tipReset.Stop(); Tip.Text = TipText; };
        Deactivated += (_, _) => { }; // 不自动收起：算账时经常要切去别的窗口看数字
        Loaded += (_, _) => Expr.Focus();
    }

    public void Toggle()
    {
        if (IsVisible && IsActive) { Hide(); return; }
        Show();
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Expr.Focus();
        Expr.CaretIndex = Expr.Text.Length;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Return) { Commit(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && Expr.SelectionLength == 0) { CopyResult(); e.Handled = true; }
    }

    void Press(string k)
    {
        switch (k)
        {
            case "C": Clear(); break;
            case "=": Commit(); break;
            default:
                int at = Expr.CaretIndex;
                if (Expr.SelectionLength > 0) { at = Expr.SelectionStart; Expr.Text = Expr.Text.Remove(at, Expr.SelectionLength); }
                Expr.Text = Expr.Text.Insert(at, k);
                Expr.CaretIndex = at + k.Length;
                break;
        }
        Expr.Focus();
    }

    void Clear() { Expr.Text = ""; Result.Text = ""; }

    void SetExpr(string s) { Expr.Text = s; Expr.CaretIndex = s.Length; Expr.Focus(); }

    /// <summary>边打边算：算式完整就在下面显示结果。</summary>
    void Preview()
    {
        if (Calc.TryEval(Expr.Text, out var v, out var err)) { Result.Text = Calc.Format(v); Result.Foreground = Brush("#222"); Result.FontSize = 30; }
        else if (string.IsNullOrEmpty(err)) Result.Text = "";
        else { Result.Text = err; Result.Foreground = Brush("#BBB"); Result.FontSize = 16; }
    }

    void Commit()
    {
        if (!Calc.TryEval(Expr.Text, out var v, out var err))
        {
            if (!string.IsNullOrEmpty(err)) { Result.Text = err; Result.Foreground = Brush("#E5413C"); Result.FontSize = 16; }
            return;
        }
        var res = Calc.Format(v);
        _history.Add($"{Calc.Pretty(Expr.Text)} = {res}");
        _historyResults.Add(res.Replace(",", ""));
        while (_history.Count > 30) { _history.RemoveAt(0); _historyResults.RemoveAt(0); }
        // 结果留在算式里，可以接着算
        SetExpr(res.Replace(",", ""));
        Result.Text = res;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (VisualTreeHelperFind<ScrollViewer>(History) is { } sv) sv.ScrollToEnd();
        });
    }

    void CopyResult()
    {
        if (!Calc.TryEval(Expr.Text, out var v, out _)) return;
        try
        {
            Clipboard.SetText(Calc.Format(v).Replace(",", ""));
            Tip.Text = L.F("已复制 {0}", Calc.Format(v));
            _tipReset.Stop(); _tipReset.Start();
        }
        catch { }
    }

    static T? VisualTreeHelperFind<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (VisualTreeHelperFind<T>(c) is { } r) return r;
        }
        return null;
    }

    static System.Windows.Media.SolidColorBrush Brush(string hex) => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
}
