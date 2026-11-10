using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskHub.Core;
using static DeskHub.UI.Settings.Ui;

namespace DeskHub.UI.Settings;

/// <summary>整理规则：事项名、关键词、学到的关键词（事项分类和流转，编译开关 FeatFlow）。</summary>
public partial class RulesPage : UserControl, ISettingsPart
{
    readonly List<RuleRow> _rows = new();

    sealed class RuleRow
    {
        public required Border Root;
        public required TextBox Name, Keywords, Learned;
        public required string Color;
    }

    static readonly string[] Palette = { "#f04b4b", "#0095fc", "#ffb800", "#9b59b6", "#4a5568", "#07c255", "#ff7a45", "#13c2c2" };

    public RulesPage(AppConfig cfg)
    {
        InitializeComponent();
        foreach (var t in cfg.Topics) AddRow(t.Name, t.Color, t.Keywords, t.Learned);
        AddTopic.Click += (_, _) => AddRow(L.T("新事项"), Palette[_rows.Count % Palette.Length], new(), new(), focus: true);
    }

    void AddRow(string name, string color, List<string> kws, List<string> learned, bool focus = false)
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = Brush(color), VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = L.T("点击换颜色") };
        var nameBox = new TextBox { Text = name, Width = 96, Style = Res<Style>("WinTextBox"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 10, 0) };
        var kwBox = new TextBox { Text = string.Join("，", kws), Style = Res<Style>("WinTextBox"), TextWrapping = TextWrapping.Wrap, MinHeight = 30 };
        var lrBox = new TextBox { Text = string.Join("，", learned), Style = Res<Style>("WinTextBox"), Background = Brush("#F3FCF6"), Foreground = Brush("#07A548"), MinHeight = 28, Margin = new Thickness(0, 6, 0, 0), ToolTip = L.T("学到的关键词") };
        var del = new Button { Content = L.T("删除"), Style = Res<Style>("BtnW"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };

        var right = new StackPanel();
        right.Children.Add(kwBox);
        right.Children.Add(new DockPanel { Children = { lrBox } });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
        head.Children.Add(dot); head.Children.Add(nameBox);
        Grid.SetColumn(head, 0); Grid.SetColumnSpan(head, 2); Grid.SetColumn(right, 2); Grid.SetColumn(del, 3);
        grid.Children.Add(head); grid.Children.Add(right); grid.Children.Add(del);

        var root = new Border { BorderBrush = Brush("#E8E8E8"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = grid };
        var row = new RuleRow { Root = root, Name = nameBox, Keywords = kwBox, Learned = lrBox, Color = color };
        dot.MouseLeftButtonUp += (_, _) =>
        {
            var i = Array.IndexOf(Palette, row.Color.ToLowerInvariant());
            row.Color = Palette[(i + 1) % Palette.Length];
            dot.Fill = Brush(row.Color);
        };
        del.Click += (_, _) => { RuleRows.Children.Remove(root); _rows.Remove(row); };
        _rows.Add(row);
        RuleRows.Children.Add(root);
        if (focus) { nameBox.Focus(); nameBox.SelectAll(); }
    }

    public string? Check()
    {
        var names = _rows.Select(r => r.Name.Text.Trim()).ToList();
        if (names.Any(n => n.Length == 0) || names.Distinct().Count() != names.Count || names.Contains(AppConfig.OtherTopic))
            return L.T("事项名称不能为空、不能重复，也不能叫“其他”");
        return null;
    }

    public void Store(AppConfig cfg) => cfg.Topics = _rows.Select(r => new TopicRule
    {
        Name = r.Name.Text.Trim(),
        Color = r.Color,
        Keywords = Split(r.Keywords.Text),
        Learned = Split(r.Learned.Text),
    }).ToList();

    static List<string> Split(string s) =>
        s.Split(new[] { ',', '，', '、', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
