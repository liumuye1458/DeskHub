using System.Windows;
using System.Windows.Controls;
using DeskHub.Core;
using DeskHub.UI;

namespace DeskHub;

/// <summary>
/// 多标签盒子 + 摆法（方案 A/B/C）。
/// 盒子：几个分区合成一块，标题栏变成一排标签；成员还是各自的分区窗口，只是同一时间只显示当前标签那一个，位置大小共用。
/// 合并、拆开都只改显示方式，不动文件、不改分类规则。
/// </summary>
public sealed partial class DeskController
{
    bool _freshInstall;
    FenceWindow? _mergeTarget;

    FenceGroup? GroupOf(string key) => Cfg.Groups.FirstOrDefault(g => g.Keys.Contains(key));

    /// <summary>在盒子里、但不是当前标签（窗口藏着）。</summary>
    bool HiddenMember(string key) => GroupOf(key) is { } g && g.Active != key;

    /// <summary>同一个盒子的所有成员（不在盒子里就是它自己）。</summary>
    List<FenceWindow> Members(FenceWindow f) => GroupOf(f.Key) is { } g ? g.Keys.Select(k => _fences[k]).ToList() : new() { f };

    public FenceWindow? FenceByKey(string key) => _fences.TryGetValue(key, out var f) ? f : null;

    /// <summary>去掉已经不存在的成员、重复的成员；少于两个的盒子解散；当前标签无效的改成第一个。</summary>
    void NormalizeGroups()
    {
        var seen = new HashSet<string>();
        foreach (var g in Cfg.Groups)
        {
            g.Keys = g.Keys.Where(k => _fences.ContainsKey(k) && seen.Add(k)).ToList();
            if (!g.Keys.Contains(g.Active)) g.Active = g.Keys.FirstOrDefault() ?? "";
        }
        Cfg.Groups.RemoveAll(g => g.Keys.Count < 2);
        RefreshGroupTabs();
    }

    /// <summary>重画每个盒子标题栏上的标签（名字、数量、哪个是当前的）。</summary>
    void RefreshGroupTabs()
    {
        foreach (var f in _fences.Values)
        {
            var g = GroupOf(f.Key);
            if (g == null) { f.SetGroupTabs(null); continue; }
            f.SetGroupTabs(g.Keys.Select(k =>
            {
                var m = _fences[k];
                // 标签多（4 个以上）时用短名字，一排放得下
                var name = g.Keys.Count > 3 && k == "apps" ? L.T("软件") : g.Keys.Count > 3 && k == "folders" ? L.T("文件夹") : m.TitleText.Text;
                return new GroupTabVM { Key = k, Name = name, Count = m.ItemCount, On = k == g.Active, Tip = string.IsNullOrWhiteSpace(m.HintText.Text) ? null : m.HintText.Text };
            }).ToList());
        }
    }

    /// <summary>当前标签的位置大小同步给盒子里其他成员。</summary>
    void SyncGroup(FenceWindow f)
    {
        if (GroupOf(f.Key) is not { } g) return;
        var r = new Rect(f.Left, f.Top, f.Width, f.Height);
        foreach (var k in g.Keys) if (k != f.Key) Apply(k, _fences[k], r);
        Cfg.Save();
    }

    void SyncGroups()
    {
        foreach (var g in Cfg.Groups)
        {
            if (!_fences.TryGetValue(g.Active, out var a)) continue;
            var r = new Rect(a.Left, a.Top, a.Width, a.Height);
            foreach (var k in g.Keys) if (k != g.Active) Apply(k, _fences[k], r);
        }
    }

    /// <summary>换一套盒子分法：该显示的显示，该藏的藏，位置跟着当前标签走。</summary>
    void SetGroups(List<FenceGroup> groups)
    {
        Cfg.Groups = groups;
        NormalizeGroups();
        SyncGroups();
        foreach (var f in _fences.Values)
        {
            if (HiddenMember(f.Key)) { f.ClearSelection(); f.Hide(); }
            else if (!f.IsVisible) f.Show();
        }
    }

    // ---------------- 切标签 ----------------
    public void GroupTabClicked(FenceWindow from, string key) => ShowTab(key);

    public void GroupWheel(FenceWindow f, int dir)
    {
        if (GroupOf(f.Key) is not { } g) return;
        int i = g.Keys.IndexOf(g.Active), n = g.Keys.Count;
        ShowTab(g.Keys[((i + dir) % n + n) % n]);
    }

    /// <summary>切到盒子里的某个成员：先在同一位置显示它，再藏起原来那个（不闪空白）。</summary>
    public void ShowTab(string key)
    {
        if (GroupOf(key) is not { } g || g.Active == key) return;
        var from = _fences[g.Active];
        var to = _fences[key];
        to.Left = from.Left; to.Top = from.Top; to.Width = from.Width; to.Height = from.Height;
        g.Active = key;
        RefreshGroupTabs();
        to.Show();
        from.ClearSelection();
        from.Hide();
        Cfg.Save();
    }

    // ---------------- 合并 ----------------
    /// <summary>拖着标题栏、鼠标停在哪个分区（盒子）的标题栏上：可以合并进去。</summary>
    FenceWindow? MergeTargetAt(FenceWindow f, Point c)
    {
        var mine = GroupOf(f.Key);
        return _fences.Values.FirstOrDefault(o => o != f && o.IsVisible && (mine == null || !mine.Keys.Contains(o.Key)) && o.HeaderRect.Contains(c));
    }

    public void MoveHover(FenceWindow f, Point c)
    {
        var t = MergeTargetAt(f, c);
        if (t == _mergeTarget) return;
        _mergeTarget?.MergeHint(false);
        _mergeTarget = t;
        t?.MergeHint(true);
    }

    public bool MoveDone(FenceWindow f, Point c)
    {
        _mergeTarget?.MergeHint(false);
        _mergeTarget = null;
        var t = MergeTargetAt(f, c);
        if (t == null) return false;
        Merge(f, t);
        return true;
    }

    /// <summary>把 f（它在盒子里就是整个盒子）并进 target 所在的盒子；盒子留在 target 的位置，显示刚并进来的那个。</summary>
    public void Merge(FenceWindow f, FenceWindow target)
    {
        var old = GroupOf(f.Key);
        var moving = old?.Keys.ToList() ?? new List<string> { f.Key };
        var g = GroupOf(target.Key);
        if (g == null) { g = new FenceGroup { Keys = { target.Key }, Active = target.Key }; Cfg.Groups.Add(g); }
        if (old != null && old != g) Cfg.Groups.Remove(old);
        foreach (var k in moving) if (!g.Keys.Contains(k)) g.Keys.Add(k);
        g.Active = f.Key;
        var r = new Rect(target.Left, target.Top, target.Width, target.Height);
        foreach (var k in g.Keys)
        {
            var w = _fences[k];
            Apply(k, w, r);
            if (k == g.Active) w.Show(); else { w.ClearSelection(); w.Hide(); }
        }
        RefreshGroupTabs();
        Touch();
        Cfg.Save();
    }

    // ---------------- 拆开 ----------------
    /// <summary>把一个成员从盒子里拆出来，放在盒子旁边的空地上。</summary>
    public void Split(FenceWindow f)
    {
        if (GroupOf(f.Key) is not { } g) return;
        var box = _fences[g.Active];
        var r = new Rect(box.Left, box.Top, box.Width, box.Height);
        LeaveGroup(f);
        Apply(f.Key, f, FreeSpotNear(r, f));
        f.Show();
        Touch();
        Cfg.Save();
    }

    /// <summary>离开盒子（拆出来、删除分区时）：它是当前标签就换成下一个显示；只剩一个就解散。</summary>
    void LeaveGroup(FenceWindow f)
    {
        if (GroupOf(f.Key) is not { } g) return;
        var box = _fences[g.Active];
        var r = new Rect(box.Left, box.Top, box.Width, box.Height);
        int i = g.Keys.IndexOf(f.Key);
        g.Keys.Remove(f.Key);
        if (g.Active == f.Key) g.Active = g.Keys[Math.Min(i, g.Keys.Count - 1)];
        var rest = _fences[g.Active];
        Apply(g.Active, rest, r);
        rest.Show();
        if (g.Keys.Count < 2) Cfg.Groups.Remove(g);
        SyncGroups();
        RefreshGroupTabs();
    }

    /// <summary>盒子旁边找一块不压住别人的地方（先右、再左、再下面），都没有就错开一点放。</summary>
    Rect FreeSpotNear(Rect r, FenceWindow self)
    {
        var wa = WA;
        double gap = Cfg.ArrangeGap;
        var taken = ArrangeTargets().Where(t => t.W != self).Select(t => new Rect(t.W.Left, t.W.Top, t.W.ActualWidth, t.W.ActualHeight)).ToList();
        if (Tools?.Dock is { IsVisible: true } dk) taken.Add(new Rect(dk.Left, dk.Top, dk.ActualWidth, dk.ActualHeight));
        foreach (var c in new[]
        {
            new Rect(r.Right + gap, r.Top, r.Width, r.Height), new Rect(r.Left - gap - r.Width, r.Top, r.Width, r.Height),
            new Rect(r.Left, r.Bottom + gap, r.Width, r.Height), new Rect(r.Left, r.Top - gap - r.Height, r.Width, r.Height),
        })
            if (wa.Contains(c) && !taken.Any(t => t.IntersectsWith(c))) return c;
        double x = Math.Clamp(r.X + 40, wa.Left, Math.Max(wa.Left, wa.Right - r.Width)), y = Math.Clamp(r.Y + 40, wa.Top, Math.Max(wa.Top, wa.Bottom - r.Height));
        return new Rect(x, y, r.Width, r.Height);
    }

    /// <summary>分区菜单里的“拆出来 / 合并到…”。</summary>
    void AddGroupMenu(ContextMenu m, FenceWindow fence)
    {
        if (GroupOf(fence.Key) != null)
        {
            var split = new MenuItem { Header = L.F("把“{0}”拆出来单独放", fence.TitleText.Text) };
            split.Click += (_, _) => Split(fence);
            m.Items.Add(split);
        }
        var mine = GroupOf(fence.Key);
        var others = _fences.Values.Where(o => o.IsVisible && o != fence && (mine == null || !mine.Keys.Contains(o.Key))).ToList();
        if (others.Count == 0) return;
        var into = new MenuItem { Header = mine != null ? L.T("把这个盒子合并到…") : L.T("合并到…（变成多标签盒子）") };
        foreach (var o in others)
        {
            var mi = new MenuItem { Header = string.Join(" / ", Members(o).Select(x => x.TitleText.Text)) };
            mi.Click += (_, _) => Merge(fence, o);
            into.Items.Add(mi);
        }
        m.Items.Add(into);
    }

    // ================= 摆法 =================
    /// <summary>每种摆法的盒子分法。A 不合并；B 左边软件+文件夹（加自建分区），右边今天/进行中/冷却区；C 全部一个盒子。</summary>
    List<FenceGroup> GroupsFor(string scheme)
    {
        var customs = Cfg.CustomFences.Select(c => c.Id).ToList();
        return scheme switch
        {
            "B" => new()
            {
                new FenceGroup { Keys = new List<string> { "apps", "folders" }.Concat(customs).ToList(), Active = "apps" },
                new FenceGroup { Keys = { "today", "active", "cold" }, Active = "today" },
            },
            "C" => new() { new FenceGroup { Keys = new List<string> { "today", "active", "cold", "apps", "folders" }.Concat(customs).ToList(), Active = "today" } },
            _ => new(),
        };
    }

    /// <summary>换成某种摆法：先按它合并好盒子，再按它的规则排版。</summary>
    public void ApplyScheme(string scheme)
    {
        Cfg.Scheme = scheme;
        SetGroups(GroupsFor(scheme));
        foreach (var f in _fences.Values) f.UpdateLayout();
        var dk = Tools?.Dock;
        var avoid = dk is { IsVisible: true } ? new Rect(dk.Left, dk.Top, dk.ActualWidth, dk.ActualHeight) : Rect.Empty;
        Arrange(true, CaptureLayout(), avoid, learnGap: false);
        Touch();
        Cfg.Save();
    }

    /// <summary>换摆法之前的样子（撤销用）：盒子分法 + 每个分区、时钟、日历的位置。</summary>
    public sealed record SchemeSnap(List<FenceGroup> Groups, string? Scheme, Dictionary<string, Rect> Rects);

    public SchemeSnap SnapScheme()
    {
        var rects = _fences.ToDictionary(kv => kv.Key, kv => new Rect(kv.Value.Left, kv.Value.Top, kv.Value.Width, kv.Value.Height));
        if (_clock != null) rects["clock"] = new Rect(_clock.Left, _clock.Top, 0, 0);
        if (_cal != null) rects["calendar"] = new Rect(_cal.Left, _cal.Top, _cal.Width, _cal.Height);
        return new(Cfg.Groups.Select(g => new FenceGroup { Keys = g.Keys.ToList(), Active = g.Active }).ToList(), Cfg.Scheme, rects);
    }

    public void RestoreScheme(SchemeSnap s)
    {
        Cfg.Scheme = s.Scheme;
        SetGroups(s.Groups.Select(g => new FenceGroup { Keys = g.Keys.ToList(), Active = g.Active }).ToList());
        foreach (var (k, r) in s.Rects)
        {
            Window? w = k == "clock" ? _clock : k == "calendar" ? _cal : _fences.TryGetValue(k, out var f) ? f : null;
            if (w != null) Apply(k, w, r);
        }
        SyncGroups();
        Touch();
        Cfg.Save();
    }

    /// <summary>打开“选个摆法”。选了马上换，提示框里能撤销。</summary>
    public void PickScheme()
    {
        var pick = SchemeWindow.Ask(Cfg.Scheme ?? "A");
        if (pick == null) return;
        var before = SnapScheme();
        ApplyScheme(pick);
        ShowSchemeToast(L.F("已换成「{0}」", SchemeWindow.NameOf(pick)), L.T("不喜欢可以撤销；以后在分区右上角「⋯ → 换个摆法」再换"), before);
    }

    /// <summary>全新安装第一次启动：直接按方案 B 摆好，提示一句在哪能换。</summary>
    void FirstLayout()
    {
        var before = SnapScheme();
        ApplyScheme("B");
        ShowSchemeToast(L.F("已按「{0}」摆好", SchemeWindow.NameOf("B")), L.T("想换摆法：分区右上角「⋯ → 换个摆法」"), before);
    }

    void ShowSchemeToast(string msg, string next, SchemeSnap before)
    {
        var dk = Tools?.Dock;
        var near = dk is { IsVisible: true } ? new Rect(dk.Left, dk.Top, dk.ActualWidth, dk.ActualHeight) : Rect.Empty;
        var toast = new ArrangeToast(() => RestoreScheme(before)) { Topmost = !Sandbox };
        toast.Show(msg, next, canUndo: true, near: near);
    }

    // ---------------- 测试用 ----------------
    public void TestFirstLayout() => FirstLayout();

    public string TestGroups() =>
        string.Join("  ", Cfg.Groups.Select(g => "[" + string.Join(",", g.Keys.Select(k => k == g.Active ? "*" + k : k)) + "]"))
        + "  visible: " + string.Join(",", _fences.Where(kv => kv.Value.IsVisible).Select(kv => kv.Key));
}
