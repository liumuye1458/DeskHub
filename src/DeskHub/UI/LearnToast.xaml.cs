using System.Windows;
using System.Windows.Threading;

using DeskHub.Core;

namespace DeskHub.UI;

/// <summary>用户把文件拖进某个事项后，问一句要不要记成规则。15 秒不理就自动消失（当作"只这一次"）。</summary>
public partial class LearnToast : Window
{
    readonly DispatcherTimer _auto = new() { Interval = TimeSpan.FromSeconds(15) };

    public LearnToast(string fileName, string fromTopic, string toTopic, string keyword, Action<string> remember)
    {
        InitializeComponent();
        Line1.Text = L.F("你把「{0}」从「{1}」移到了「{2}」", fileName, fromTopic, toTopic);
        Line2.Text = L.F("的，都自动放进「{0}」？", toTopic);
        Keyword.Text = keyword;
        Yes.Click += (_, _) =>
        {
            var k = Keyword.Text.Trim();
            if (k.Length > 0) remember(k);
            Close();
        };
        No.Click += (_, _) => Close();
        _auto.Tick += (_, _) => { if (!IsKeyboardFocusWithin) Close(); };
        Loaded += (_, _) => _auto.Start();
        Closed += (_, _) => _auto.Stop();
    }

    public void PlaceNear(Window fence)
    {
        Left = fence.Left + 16;
        Top = fence.Top + fence.ActualHeight - 10;
        var wa = SystemParameters.WorkArea;
        UpdateLayout();
        if (Top + ActualHeight > wa.Bottom) Top = Math.Max(wa.Top, fence.Top - ActualHeight + 10);
    }
}
