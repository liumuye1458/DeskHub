using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>小输入框（重命名用）；multiline = 多行大框（改待办文字用，回车换行，点确定保存）。</summary>
static class InputDialog
{
    public static string? Ask(Window owner, string title, string value, int? selectLength = null, bool multiline = false)
    {
        var box = new TextBox { Text = value, Style = (Style)Application.Current.FindResource("WinTextBox"), MinWidth = 360, Height = 32 };
        if (multiline)
        {
            box.AcceptsReturn = true; box.TextWrapping = TextWrapping.Wrap; box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            box.Width = 480; box.Height = 220; box.VerticalContentAlignment = VerticalAlignment.Top;
        }
        var ok = new Button { Content = L.T("确定"), Style = (Style)Application.Current.FindResource("BtnG"), IsDefault = true, Margin = new Thickness(8, 0, 0, 0), MinWidth = 72 };
        var cancel = new Button { Content = L.T("取消"), Style = (Style)Application.Current.FindResource("BtnW"), IsCancel = true, MinWidth = 72 };
        var w = new Window
        {
            Title = title, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"), Background = Brushes.White,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Children =
                {
                    new TextBlock { Text = multiline ? L.T("内容") : L.T("新名称"), Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)), Margin = new Thickness(0, 0, 0, 6) },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { cancel, ok } },
                },
            },
        };
        string? result = null;
        ok.Click += (_, _) => { result = box.Text.Trim(); w.DialogResult = true; };
        w.Loaded += (_, _) => { box.Focus(); box.Select(0, selectLength ?? value.Length); };
        return w.ShowDialog() == true ? result : null;
    }
}
