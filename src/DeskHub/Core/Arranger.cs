using System.Windows;

namespace DeskHub.Core;

/// <summary>参与排列的一个窗口（只有几何信息，方便单独测试）。</summary>
public sealed class Block
{
    public string Key = "";
    public Rect R;
    /// <summary>分区：大小可以调；时钟、便签只挪位置。</summary>
    public bool Fit;
    public int Items;
    /// <summary>至少留几行（今日、进行中、冷却区每天有新文件进来，留两行）。</summary>
    public int MinRows = 1;
    /// <summary>分区里图标区以外占的宽、高（边框、标题栏、标签页）。</summary>
    public double ChromeW, ChromeH;
    /// <summary>推荐排版时放哪一列：0 左、1 中、2 右，9 便签。</summary>
    public int Group;
    /// <summary>自定义分区：可以在左、中两列之间调配，让两边一样满。</summary>
    public bool Spill;
}

/// <summary>
/// 桌面排列。审美规则（从用户自己摆的那版总结出来的）：
/// 1. 共用边线：左右边按列对齐，同一行的上下边落在同一条线上；
/// 2. 间距一致：相邻分区之间的缝都一样宽（默认 8，按你自己摆的版本学）；
/// 3. 拼成整块：所有分区合起来是一个完整的大矩形，四周留边一样，不参差；
/// 4. 宽度尽量是整列图标，右边不留半截空白。
///
/// Tidy（对齐）：不改你的布局结构，只把边吸到同一条线上、缝统一；
/// Auto（推荐排版）：按上面的规则把整块屏幕铺满。
/// Snap：拖动、拉大小时实时吸附到别的分区的边和统一的缝上。
/// </summary>
public static class Arranger
{
    public const double CellW = 76, CellH = 90, DefaultGap = 8, Tol = 28, SnapDist = 10;

    public static double MarginOf(double gap) => gap + 4;

    // ======================= 学你的间距 =======================
    /// <summary>从现在的摆法里量出你习惯的缝宽（相邻分区之间小于 40 的缝取中位数）。</summary>
    public static double LearnGap(List<Block> bs)
    {
        var gaps = new List<double>();
        foreach (var a in bs) foreach (var b in bs)
        {
            if (a == b) continue;
            double dx = b.R.Left - a.R.Right, dy = b.R.Top - a.R.Bottom;
            if (dx is >= 2 and <= 40 && Span(a.R.Top, a.R.Bottom, b.R.Top, b.R.Bottom) > 40) gaps.Add(dx);
            if (dy is >= 2 and <= 40 && Span(a.R.Left, a.R.Right, b.R.Left, b.R.Right) > 40) gaps.Add(dy);
        }
        if (gaps.Count == 0) return DefaultGap;
        gaps.Sort();
        return Math.Clamp(Math.Round(gaps[gaps.Count / 2]), 4, 24);
    }

    static double Span(double a0, double a1, double b0, double b1) => Math.Min(a1, b1) - Math.Max(a0, b0);

    // ======================= 对齐 =======================
    public static void Tidy(List<Block> bs, Rect wa, double gap, double scale)
    {
        // 缝宽、边线都放到屏幕整像素上（缩放 150% 时 1 个单位 = 1.5 像素，不取整就会一处宽一处窄）
        gap = Px(gap, scale);
        // 两遍：第一遍归线，第二遍在已经对齐的基础上把缝补成一样宽
        for (int i = 0; i < 2; i++) { Align(bs, wa, gap, true, scale); Align(bs, wa, gap, false, scale); }
        foreach (var b in bs) b.R = Round(b.R, scale);
    }

    sealed class Line
    {
        public bool Start; // true：左边 / 上边；false：右边 / 下边
        public double Pos;
        public List<Block> Members = new();
    }

    /// <summary>
    /// 一个方向上对齐：
    /// - 差不多的左边归成一条线、右边归成一条线（相差 28 以内算"想对齐"）；
    /// - 贴近屏幕边的统一留边；
    /// - 左边那条线前面紧挨着一条右边线（两边的分区确实相邻），就定在"右边线 + 缝"上。
    /// 右边线是基准，左边线跟着走，所以整列的缝都一样宽。
    /// </summary>
    static double Px(double v, double scale) => Math.Round(v * scale) / scale;

    static void Align(List<Block> bs, Rect wa, double gap, bool x, double scale)
    {
        double lo = Px(x ? wa.Left : wa.Top, scale), hi = Px(x ? wa.Right : wa.Bottom, scale), m = Px(MarginOf(gap), scale);
        double S(Block b) => x ? b.R.Left : b.R.Top;
        double E(Block b) => x ? b.R.Right : b.R.Bottom;

        var lines = new List<Line>();
        foreach (var start in new[] { true, false })
        {
            var edges = bs.Where(b => start || b.Fit).OrderBy(b => start ? S(b) : E(b)).ToList();
            int i = 0;
            while (i < edges.Count)
            {
                double first = start ? S(edges[i]) : E(edges[i]);
                var line = new Line { Start = start };
                while (i < edges.Count && (start ? S(edges[i]) : E(edges[i])) - first <= Tol) line.Members.Add(edges[i++]);
                var vals = line.Members.Select(b => start ? S(b) : E(b)).OrderBy(v => v).ToList();
                line.Pos = Px(vals[vals.Count / 2], scale);
                lines.Add(line);
            }
        }

        // 贴近屏幕边的统一留边
        foreach (var l in lines)
        {
            if (l.Start && l.Pos < lo + m + Tol) l.Pos = lo + m;
            if (!l.Start && l.Pos > hi - m - Tol) l.Pos = hi - m;
        }

        // 相邻的：左边线 = 前面的右边线 + 缝
        bool Neighbors(Block a, Block b) => x
            ? Span(a.R.Top, a.R.Bottom, b.R.Top, b.R.Bottom) > 20
            : Span(a.R.Left, a.R.Right, b.R.Left, b.R.Right) > 20;
        foreach (var l in lines.Where(l => l.Start).OrderBy(l => l.Pos))
        {
            var prev = lines.Where(r => !r.Start && Math.Abs(l.Pos - r.Pos) <= 40 &&
                                        r.Members.Any(a => l.Members.Any(b => a != b && Neighbors(a, b) && E(a) <= S(b) + 40)))
                            .Select(r => r.Pos).DefaultIfEmpty(double.NaN).Max();
            if (!double.IsNaN(prev)) l.Pos = prev + gap;
        }

        // 套回去：分区左右边都按线走；时钟、便签大小不变，只挪位置
        var startOf = new Dictionary<Block, double>();
        var endOf = new Dictionary<Block, double>();
        foreach (var l in lines) foreach (var b in l.Members) (l.Start ? startOf : endOf)[b] = l.Pos;
        foreach (var b in bs)
        {
            double s = startOf[b], len = x ? b.R.Width : b.R.Height;
            if (b.Fit && endOf.TryGetValue(b, out var e) && e - s >= (x ? 160 : 100)) len = e - s;
            b.R = x ? new Rect(s, b.R.Y, len, b.R.Height) : new Rect(b.R.X, s, b.R.Width, len);
        }
    }

    static Rect Round(Rect r, double scale)
    {
        // 对到屏幕的整像素，避免边线发虚、差一像素
        double F(double v) => Math.Round(v * scale) / scale;
        double l = F(r.Left), t = F(r.Top);
        return new Rect(l, t, Math.Max(1, F(r.Right) - l), Math.Max(1, F(r.Bottom) - t));
    }

    // ======================= 推荐排版：铺满屏幕的拼图 =======================
    static double Need(Block b, double width)
    {
        if (!b.Fit) return b.R.Height;
        int cols = Math.Max(1, (int)Math.Floor((width - b.ChromeW - 4 + 8) / CellW));
        int rows = Math.Max(b.MinRows, (int)Math.Ceiling(b.Items / (double)cols));
        return b.ChromeH + rows * CellH + 2;
    }

    static double MinH(Block b) => b.Fit ? b.ChromeH + CellH + 2 : b.R.Height;

    /// <param name="avoid">要让开的区域（右下角工具条）。</param>
    public static void Auto(List<Block> bs, Rect wa, Rect avoid, double gap, double scale)
    {
        gap = Px(gap, scale);
        double m = Px(MarginOf(gap), scale);
        var notes = bs.Where(b => b.Group == 9).ToList();
        double notesW = notes.Count == 0 ? 0 : notes.Max(n => n.R.Width) + gap;
        double top = wa.Top + m, bottom = wa.Bottom - m;
        // 整块的下边统一停在工具条上面（保持所有列底边一齐）
        if (!avoid.IsEmpty && avoid.Top < bottom && avoid.Bottom > top) bottom = avoid.Top - gap;
        double left = wa.Left + m, right = wa.Right - m - notesW;

        // 三列：左（时钟、常用软件、项目文件夹）、中（自定义分区、进行中）、右（今天、冷却区）
        var cols = new List<List<Block>> { new(), new(), new() };
        foreach (var b in bs.Where(b => b.Group != 9)) cols[Math.Clamp(b.Group, 0, 2)].Add(b);
        cols.RemoveAll(c => c.Count == 0);
        double[] weight = cols.Select(c => c.Max(b => b.Group) switch { 0 => 6.0, 1 => 5.0, _ => 8.0 }).ToArray();

        // 列宽：按比例分，再取整到整列图标，多出来的整列给最宽的列，零头给最后一列
        double usable = right - left - gap * (cols.Count - 1);
        double chrome = bs.Where(b => b.Fit).Select(b => b.ChromeW).DefaultIfEmpty(10).Max() + 4;
        var icons = weight.Select(w => Math.Max(2, (int)Math.Floor((usable * w / weight.Sum() - chrome) / CellW))).ToArray();
        double Used() => icons.Sum(c => chrome + c * CellW);
        while (Used() + CellW <= usable)
        {
            int k = 0; double best = double.MinValue;
            for (int i = 0; i < icons.Length; i++)
            {
                double want = usable * weight[i] / weight.Sum() - (chrome + icons[i] * CellW);
                if (want > best) { best = want; k = i; }
            }
            icons[k]++;
        }
        while (Used() > usable && icons.Max() > 2) icons[Array.IndexOf(icons, icons.Max())]--;
        var widths = icons.Select(c => chrome + c * CellW).ToArray();
        widths[^1] += usable - widths.Sum();

        // 自定义分区：左、中两列哪边空就往哪边放（放在常用软件和项目文件夹中间），让两列一样满
        if (cols.Count == 3)
        {
            double Fill(int i) => cols[i].Sum(b => Need(b, widths[i])) / (bottom - top);
            for (int guard = 0; guard < 20; guard++)
            {
                var mid = cols[1].Where(b => b.Spill).ToList();
                if (mid.Count < 2) break;
                var last = mid[^1];
                double before = Math.Max(Fill(0), Fill(1));
                cols[1].Remove(last);
                int at = cols[0].FindIndex(b => b.Key == "folders");
                cols[0].Insert(at < 0 ? cols[0].Count : at, last);
                if (Math.Max(Fill(0), Fill(1)) < before - 0.02) continue;
                cols[0].Remove(last); cols[1].Insert(cols[1].IndexOf(mid[^2]) + 1, last);
                break;
            }
        }

        // 每列从上铺到下：按内容多少分高度，底边齐平
        double x = left;
        for (int i = 0; i < cols.Count; i++)
        {
            var list = cols[i];
            double w = widths[i];
            double avail = bottom - top - gap * (list.Count - 1);
            var need = list.Select(b => Need(b, w)).ToArray();
            var hs = need.ToArray();
            double fixedH = list.Where(b => !b.Fit).Sum(b => b.R.Height);
            double flex = need.Where((_, j) => list[j].Fit).Sum();
            double room = avail - fixedH;
            if (flex > 0)
            {
                if (room >= flex)
                {
                    // 有富余：按内容多少分出去（东西多的分得多），每块都多出几行备用
                    double extra = room - flex;
                    for (int j = 0; j < list.Count; j++) if (list[j].Fit) hs[j] += extra * need[j] / flex;
                }
                else
                {
                    // 放不下：先保证每块至少一行，剩下的按比例（里面会出滚动条）
                    double mins = list.Where(b => b.Fit).Sum(MinH);
                    for (int j = 0; j < list.Count; j++)
                        if (list[j].Fit) hs[j] = MinH(list[j]) + Math.Max(0, room - mins) * (need[j] - MinH(list[j])) / Math.Max(1, flex - mins);
                }
            }
            double y = top;
            for (int j = 0; j < list.Count; j++)
            {
                var b = list[j];
                double h = j == list.Count - 1 && b.Fit ? bottom - y : hs[j]; // 最后一块正好到底
                b.R = Round(new Rect(x, y, b.Fit ? w : b.R.Width, h), scale);
                y += h + gap;
            }
            x += w + gap;
        }

        // 便签：右上角，从上往下排
        double ny = top;
        foreach (var n in notes)
        {
            n.R = Round(new Rect(wa.Right - m - n.R.Width, ny, n.R.Width, n.R.Height), scale);
            ny += n.R.Height + gap;
        }
    }

    // ======================= 拖动 / 拉大小时的吸附 =======================
    /// <summary>
    /// 拖动时：左边吸到别人的左边或"别人右边 + 缝"，右边吸到别人的右边或"别人左边 - 缝"，上下同理；也吸屏幕留边。
    /// 拉大小时只动右边和下边。离得 10 以内才吸，按住 Alt 不吸。
    /// </summary>
    public static Rect Snap(Rect r, IEnumerable<Rect> others, Rect wa, double gap, bool resize, double scale)
    {
        gap = Px(gap, scale);
        double m = Px(MarginOf(gap), scale);
        var os = others.ToList();
        var xStart = os.SelectMany(o => new[] { o.Left, o.Right + gap }).Append(wa.Left + m).ToList();
        var xEnd = os.SelectMany(o => new[] { o.Right, o.Left - gap }).Append(wa.Right - m).ToList();
        var yStart = os.SelectMany(o => new[] { o.Top, o.Bottom + gap }).Append(wa.Top + m).ToList();
        var yEnd = os.SelectMany(o => new[] { o.Bottom, o.Top - gap }).Append(wa.Bottom - m).ToList();

        static double Best(double v, IEnumerable<double> cands)
        {
            double d = double.NaN;
            foreach (var c in cands) { var dd = c - v; if (Math.Abs(dd) <= SnapDist && (double.IsNaN(d) || Math.Abs(dd) < Math.Abs(d))) d = dd; }
            return d;
        }
        static double Pick(double a, double b) =>
            double.IsNaN(a) ? (double.IsNaN(b) ? 0 : b) : double.IsNaN(b) ? a : Math.Abs(a) <= Math.Abs(b) ? a : b;

        if (resize)
        {
            double dr = Best(r.Right, xEnd), db = Best(r.Bottom, yEnd);
            r = new Rect(r.X, r.Y, Math.Max(200, r.Width + (double.IsNaN(dr) ? 0 : dr)), Math.Max(120, r.Height + (double.IsNaN(db) ? 0 : db)));
        }
        else
        {
            double dx = Pick(Best(r.Left, xStart), Best(r.Right, xEnd));
            double dy = Pick(Best(r.Top, yStart), Best(r.Bottom, yEnd));
            r.Offset(dx, dy);
        }
        return Round(r, scale);
    }
}
