using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>点「排列」后在工具条上方出现的小提示：排成了什么样、再点一下会怎样、可以撤销。10 秒后自己消失。</summary>
public sealed class ArrangeToast : Window
{
    readonly TextBlock _msg = new() { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold };
    readonly TextBlock _next = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xC0, 0xC8)), FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock _undo = new() { Text = L.T("撤销"), Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x7B)), FontSize = 13, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0), Padding = new Thickness(4, 2, 4, 2) };
    readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(10) };

    public ArrangeToast(Action undo)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; ShowActivated = false; Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var text = new StackPanel();
        text.Children.Add(_msg);
        text.Children.Add(_next);
        var row = new DockPanel();
        DockPanel.SetDock(_undo, Dock.Right);
        row.Children.Add(_undo);
        row.Children.Add(text);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x22, 0x27, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10, 10, 10), Child = row,
        };
        _undo.MouseLeftButtonUp += (_, _) => { undo(); Hide(); };
        _hide.Tick += (_, _) => { if (!IsMouseOver) { _hide.Stop(); Hide(); } };
        Closed += (_, _) => _hide.Stop();
    }

    /// <param name="near">工具条的位置：提示放在它正上方，右边对齐。</param>
    public void Show(string msg, string next, bool canUndo, Rect near)
    {
        _msg.Text = msg;
        _next.Text = next;
        _undo.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
        if (!IsVisible) Show();
        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        if (near.IsEmpty) near = new Rect(wa.Right - 24, wa.Bottom - 20, 0, 0);
        Left = Math.Max(wa.Left + 8, near.Right - ActualWidth);
        Top = Math.Max(wa.Top + 8, near.Top - ActualHeight - 10);
        _hide.Stop(); _hide.Start();
    }
}
