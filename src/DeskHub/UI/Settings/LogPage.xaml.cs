using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeskHub.Core;
using static DeskHub.UI.Settings.Ui;

namespace DeskHub.UI.Settings;

/// <summary>移动记录 / 撤回（FileMover 记的，归档和分区标题栏按钮挪走的都在这里）。</summary>
public partial class LogPage : UserControl, ISettingsPart
{
    readonly DeskController _c;

    public LogPage(DeskController c)
    {
        InitializeComponent();
        _c = c;
        Build();
    }

    public void Shown() => Build();

    void Build()
    {
        LogRows.Children.Clear();
        var batches = _c.Mover.Batches().ToList();
        LogEmpty.Visibility = batches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var g in batches)
        {
            var first = g.First();
            int live = g.Count(r => !r.Undone);
            var undo = new Button { Content = live == 0 ? L.T("已撤回") : L.T("撤回这批"), Style = Res<Style>("BtnW"), IsEnabled = live > 0 };
            var batch = g.Key;
            undo.Click += (_, _) =>
            {
                var (ok, fail) = _c.UndoMove(batch);
                MessageBox.Show(Window.GetWindow(this), fail == 0 ? L.F("已把 {0} 个搬回原来的位置", ok) : L.F("已搬回 {0} 个，{1} 个没搬回（挪走后又被移动或删除了）", ok, fail), L.T("撤回"));
                Build();
            };
            var names = string.Join("、", g.Take(4).Select(r => Path.GetFileName(r.From))) + (g.Count() > 4 ? L.F(" 等 {0} 个", g.Count()) : "");
            var to = Path.GetDirectoryName(first.To) ?? "";
            var dock = new DockPanel();
            DockPanel.SetDock(undo, Dock.Right);
            dock.Children.Add(undo);
            dock.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = L.F("{0:yyyy-MM-dd HH:mm}　{1} {2} 个", first.Time, first.Label ?? L.T("归档"), g.Count()), FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = names, Foreground = Brush("#999"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = L.F("挪到 {0}", to), Foreground = Brush("#BBB"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = to },
                },
            });
            LogRows.Children.Add(new Border { BorderBrush = Brush("#F0F0F0"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8), Child = dock });
        }
    }
}
