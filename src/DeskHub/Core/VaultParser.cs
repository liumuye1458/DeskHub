using System.Text;
using System.Text.RegularExpressions;

namespace DeskHub.Core;

/// <summary>
/// 智能识别：把随手记的账号密码（粘贴的文字、Word/Excel 内容、截图识别出的文字）拆成一条条记录。
/// 完全在本机按规则识别，不联网。认得的写法：
///   带标签：  网址：xxx  账号：xxx  密码：xxx（中文 / 英文 / 印尼文标签都认，一行写几个也行）
///   不带标签：demo@gmail.com----Pass123、user / pass、user:pass、“名称 网址 账号 密码”一行
///   表格：    Excel 复制出来的、CSV，有没有表头都行
/// 记录之间用空行、标题行（如【TikTok 后台】、1. Gmail）或者字段重复来分开。
/// </summary>
public static class VaultParser
{
    enum F { Name, Url, Account, Email, Phone, Password, Secret, Extra, Note, Port }

    // 标签表：(写法, 字段)。越长的越先匹配（"支付密码" 先于 "密码"）。
    static readonly (string Label, F Field)[] Labels =
    {
        ("服务名", F.Name), ("名称", F.Name), ("平台", F.Name), ("网站名", F.Name), ("系统名", F.Name), ("应用", F.Name), ("服务", F.Name),
        ("name", F.Name), ("title", F.Name), ("platform", F.Name), ("nama", F.Name),

        ("登录网址", F.Url), ("登录地址", F.Url), ("后台地址", F.Url), ("后台网址", F.Url), ("网址", F.Url), ("网站", F.Url), ("链接", F.Url), ("地址", F.Url),
        ("服务器地址", F.Url), ("服务器", F.Url), ("主机", F.Url), ("域名", F.Url),
        ("login url", F.Url), ("website", F.Url), ("url", F.Url), ("link", F.Url), ("site", F.Url), ("situs", F.Url), ("alamat", F.Url), ("host", F.Url), ("ip", F.Url),
        ("端口", F.Port), ("port", F.Port),

        ("登录账号", F.Account), ("登录名", F.Account), ("用户名", F.Account), ("账号", F.Account), ("帐号", F.Account), ("账户", F.Account), ("帐户", F.Account), ("用户", F.Account), ("工号", F.Account),
        ("nama pengguna", F.Account), ("username", F.Account), ("user name", F.Account), ("user id", F.Account), ("login id", F.Account), ("account", F.Account),
        ("akun", F.Account), ("login", F.Account), ("user", F.Account), ("uid", F.Account), ("id", F.Account),

        ("电子邮箱", F.Email), ("邮箱", F.Email), ("e-mail", F.Email), ("email", F.Email), ("mail", F.Email), ("surel", F.Email),
        ("手机号码", F.Phone), ("手机号", F.Phone), ("手机", F.Phone), ("电话", F.Phone), ("联系电话", F.Phone),
        ("phone", F.Phone), ("mobile", F.Phone), ("no hp", F.Phone), ("telepon", F.Phone), ("whatsapp", F.Phone), ("hp", F.Phone), ("wa", F.Phone),

        ("支付密码", F.Secret), ("交易密码", F.Secret), ("二级密码", F.Secret), ("安全密码", F.Secret), ("取款密码", F.Secret), ("独立密码", F.Secret), ("资金密码", F.Secret),
        ("pin", F.Secret), ("mpin", F.Secret), ("kode pin", F.Secret),
        ("登录密码", F.Password), ("密码", F.Password), ("口令", F.Password),
        ("kata sandi", F.Password), ("password", F.Password), ("passwd", F.Password), ("sandi", F.Password), ("pass", F.Password), ("pwd", F.Password), ("pw", F.Password),

        ("安全问题", F.Extra), ("密保问题", F.Extra), ("密保", F.Extra), ("两步验证", F.Extra), ("谷歌验证", F.Extra), ("验证器", F.Extra), ("2fa", F.Extra), ("otp", F.Extra), ("api key", F.Extra), ("apikey", F.Extra), ("token", F.Extra), ("密钥", F.Extra),

        ("备注", F.Note), ("说明", F.Note), ("注意", F.Note), ("note", F.Note), ("notes", F.Note), ("remark", F.Note), ("keterangan", F.Note), ("catatan", F.Note),
    };

    /// <summary>短的英文标签（id、pw、ip…）必须带冒号才算，免得把普通单词当成标签。</summary>
    static bool Strict(string label) => Regex.IsMatch(label, "^[a-z0-9 -]{1,4}$");

    static readonly Regex LabelRx;
    static readonly Dictionary<string, F> LabelMap = new(StringComparer.OrdinalIgnoreCase);

    static VaultParser()
    {
        foreach (var (l, f) in Labels) LabelMap[l] = f;
        var alts = Labels.Select(x => x.Label).OrderByDescending(x => x.Length).Select(Regex.Escape);
        // 标签前面是行首或非字母数字；后面跟冒号 / 等号 / 空白，或直接跟值（中文标签）
        LabelRx = new Regex($@"(?<![A-Za-z0-9_])(?<l>{string.Join("|", alts)})(?<sep>\s*[:：=．]\s*|\s+|(?=[^\s]))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    static readonly Regex UrlRx = new(@"(https?://[^\s，,；;]+|www\.[^\s，,；;]+|(?<![@\w.-])(?:[a-z0-9-]+\.)+(?:com|cn|net|org|id|io|co|me|app|cc|top|xyz|info|biz|vip|shop|gov|edu|tv|live|ai|so|link)(?::\d+)?(?:/[^\s，,；;]*)?(?![\w.-])|(?<![\d.])\d{1,3}(?:\.\d{1,3}){3}(?::\d+)?(?![\d.]))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex EmailRx = new(@"^[\w.+-]+@[\w-]+(\.[\w-]+)+$", RegexOptions.Compiled);
    static readonly Regex PhoneRx = new(@"^(\+?\d[\d -]{7,16}\d)$", RegexOptions.Compiled);
    static readonly Regex PhoneInline = new(@"\+?\d{1,4}(?:[ -]\d{2,5}){2,4}", RegexOptions.Compiled);
    static readonly Regex HeaderRx = new(@"^\s*(?:\d{1,3}\s*[.、)）]|[-*•#>]+|[【\[（(])\s*(?<t>.+?)\s*(?:[】\]）)])?\s*[:：]?\s*$", RegexOptions.Compiled);

    sealed class Draft
    {
        public string Name = "", Url = "", Account = "", Password = "", Note = "";
        public List<VaultField> Fields = new();
        public bool HasCred => Account.Length > 0 || Password.Length > 0;
        public bool Empty => Name.Length == 0 && Url.Length == 0 && !HasCred && Fields.Count == 0;
    }

    // ================= 入口 =================
    public static List<VaultEntry> Parse(string text, IList<string> groups)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ').Replace('　', ' ');
        var lines = text.Split('\n').Select(l => l.TrimEnd()).ToList();
        var drafts = LooksLikeTable(lines, out var rows) ? FromTable(rows) : FromLines(lines);
        return drafts.Where(d => d.HasCred).Select(d => Finish(d, groups)).ToList();
    }

    /// <summary>Excel 读出来的表格直接走这里。</summary>
    public static List<VaultEntry> ParseRows(List<List<string>> rows, IList<string> groups)
    {
        // 每个工作表单独认表头（工作表之间用空行隔开）
        var all = new List<VaultEntry>();
        var chunk = new List<List<string>>();
        foreach (var r in rows.Append(new List<string>()))
        {
            if (r.Count > 0) { chunk.Add(r); continue; }
            if (chunk.Count == 0) continue;
            // 只有一两列的“表格”其实是一行一行的笔记，按文字识别
            var drafts = chunk.Max(x => x.Count(c => c.Length > 0)) >= 3 ? FromTable(chunk) : FromLines(chunk.Select(x => string.Join(" ", x)).ToList());
            all.AddRange(drafts.Where(d => d.HasCred).Select(d => Finish(d, groups)));
            chunk = new();
        }
        return all;
    }

    /// <summary>
    /// 图片识别出来的文字修一修：小字英文常被拆成 “F ei sh u # 2026”，拼回 “Feishu#2026”；
    /// 中文标签后面的冒号常被认成 “．” 或 “'”。
    /// </summary>
    public static string TidyOcr(string text)
    {
        text = Regex.Replace(text, @"(?<![\w#@!$%&*.\-])[\w#@!$%&*.\-]{1,4}(?: [\w#@!$%&*.\-]{1,4}){2,}(?![\w#@!$%&*.\-])",
            m => Regex.IsMatch(m.Value, @"[\u4e00-\u9fff]") ? m.Value : m.Value.Replace(" ", ""));
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff])\s*[．']\s*(?=[\w+])", "：");
        return text;
    }

    /// <summary>识别结果好不好：认出的标签越多、记录越多越好（用来在两种图片放大倍数里挑一个）。</summary>
    public static int Score(string text)
    {
        int labels = text.Split('\n').Sum(l => LabelRx.Matches(l).Count(IsRealLabel));
        int junk = text.Count(c => c is '钅' or '贝' or '亻' or '扌' or '讠' or '冫');
        return labels * 2 + Parse(text, Array.Empty<string>()).Count * 3 - junk * 2;
    }

    // ================= 表格 =================
    static bool LooksLikeTable(List<string> lines, out List<List<string>> rows)
    {
        rows = new();
        var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToList();
        if (nonEmpty.Count < 2) return false;
        // 制表符分隔（从 Excel / 网页表格复制）
        if (nonEmpty.Count(l => l.Count(c => c == '\t') >= 1) >= nonEmpty.Count * 0.7)
        {
            rows = nonEmpty.Select(l => l.Split('\t').Select(c => c.Trim()).ToList()).ToList();
            return true;
        }
        // CSV：第一行有逗号、而且能认出密码列
        var head = nonEmpty[0].Split(',').Select(c => c.Trim().Trim('"')).ToList();
        if (head.Count >= 2 && head.Any(h => LabelMap.TryGetValue(h, out var f) && f == F.Password))
        {
            rows = Csv(string.Join("\n", nonEmpty));
            return true;
        }
        return false;
    }

    static List<Draft> FromTable(List<List<string>> rows)
    {
        rows = rows.Where(r => r.Any(c => c.Trim().Length > 0)).ToList();
        if (rows.Count == 0) return new();
        int width = rows.Max(r => r.Count);
        var cols = new F?[width];
        var colNames = new string[width];
        var skip = new bool[width];
        // 有表头：按表头认列（至少认出两列，而且这一行不像数据）
        var head = rows[0].Select(h => h.Trim().TrimEnd(':', '：')).ToList();
        var hf = head.Select(HeaderField).ToList();
        bool header = hf.Count(f => f != null) >= 2 && !head.Any(h => UrlRx.IsMatch(h) || h.Contains('@') || h.Any(char.IsDigit) && h.Any(char.IsLetter));
        for (int c = 0; c < head.Count; c++)
        {
            colNames[c] = head[c];
            if (!header) continue;
            cols[c] = hf[c];
            skip[c] = Regex.IsMatch(head[c], @"^(序号|编号|no\.?|#|id号)$", RegexOptions.IgnoreCase);
        }
        var data = header ? rows.Skip(1).ToList() : rows;
        if (!header) Guess(data, cols, width);

        var list = new List<Draft>();
        foreach (var r in data)
        {
            var d = new Draft();
            var notes = new List<string>();
            for (int c = 0; c < r.Count; c++)
            {
                var v = r[c].Trim();
                if (v.Length == 0 || skip[c]) continue;
                if (cols[c] is { } f) Set(d, f, v, colNames[c], allowNew: false);
                else if (header && colNames[c].Length > 0) d.Fields.Add(new VaultField { Name = colNames[c], Value = v });
                else notes.Add(v);
            }
            if (notes.Count > 0) d.Note = string.Join("  ", notes.Prepend(d.Note).Where(x => x.Length > 0));
            list.Add(d);
        }
        return list;
    }

    /// <summary>没有表头：按每一列的内容猜。</summary>
    static void Guess(List<List<string>> rows, F?[] cols, int width)
    {
        string Cell(List<string> r, int c) => c < r.Count ? r[c].Trim() : "";
        double Share(int c, Func<string, bool> test)
        {
            var vals = rows.Select(r => Cell(r, c)).Where(v => v.Length > 0).ToList();
            return vals.Count == 0 ? 0 : vals.Count(test) / (double)vals.Count;
        }
        var free = Enumerable.Range(0, width).ToList();
        int Take(Func<int, double> score, double min)
        {
            var best = free.Select(c => (c, s: score(c))).Where(x => x.s >= min).OrderByDescending(x => x.s).ThenBy(x => x.c).FirstOrDefault((-1, 0));
            if (best.Item1 >= 0) free.Remove(best.Item1);
            return best.Item1;
        }
        int url = Take(c => Share(c, v => UrlRx.Match(v) is { Success: true } m && m.Length >= v.Length - 1), 0.5);
        if (url >= 0) cols[url] = F.Url;
        var used = free.Where(c => Share(c, v => v.Length > 0) > 0.5).ToList();
        int acc = used.FirstOrDefault(c => Share(c, v => EmailRx.IsMatch(v) || PhoneRx.IsMatch(v)) >= 0.5, -1);
        int name = -1, pw = -1;
        if (acc >= 0)
        {
            name = used.FirstOrDefault(c => c < acc, -1);
            pw = used.FirstOrDefault(c => c > acc, -1);
        }
        else if (used.Count >= 3) { name = used[0]; acc = used[1]; pw = used[2]; }
        else if (used.Count == 2) { acc = used[0]; pw = used[1]; }
        if (name >= 0) cols[name] = F.Name;
        if (acc >= 0) cols[acc] = F.Account;
        if (pw >= 0) cols[pw] = F.Password;
    }

    // ================= 普通文字 =================
    static List<Draft> FromLines(List<string> lines)
    {
        var list = new List<Draft>();
        var d = new Draft();
        void Push() { if (!d.Empty) list.Add(d); d = new Draft(); }

        for (int li = 0; li < lines.Count; li++)
        {
            // 文字中间夹着的表格（从 Word / Excel 复制，制表符分格）：连续两行以上就按表格认
            if (lines[li].Contains('\t'))
            {
                int lj = li;
                while (lj < lines.Count && lines[lj].Contains('\t')) lj++;
                if (lj - li >= 2)
                {
                    Push();
                    list.AddRange(FromTable(lines.Skip(li).Take(lj - li).Select(l => l.Split('\t').Select(c => c.Trim()).ToList()).ToList()));
                    li = lj - 1;
                    continue;
                }
            }
            var line = lines[li].Trim();
            if (line.Length == 0) { if (d.HasCred) Push(); continue; }
            if (Regex.IsMatch(line, @"^[-=_*~—]{3,}$")) { Push(); continue; } // 分隔线

            var ms = LabelRx.Matches(line).Where(IsRealLabel).ToList();
            if (ms.Count > 0)
            {
                var prefix = Clean(line[..ms[0].Index]);
                // “U盾密码：888888”这种：前面已经有密码了，短前缀 + 密码 = 这条记录的另一个密码
                if (prefix.Length is > 0 and <= 6 && ms.Count == 1 && d.Password.Length > 0 && LabelMap[ms[0].Groups["l"].Value] == F.Password)
                {
                    var pv = Clean(line[(ms[0].Index + ms[0].Length)..]);
                    if (pv.Length > 0) { AddField(d, prefix + ms[0].Groups["l"].Value, pv, true); continue; }
                }
                if (prefix.Length > 0) StartName(ref d, list, prefix);
                for (int i = 0; i < ms.Count; i++)
                {
                    int s = ms[i].Index + ms[i].Length, e = i + 1 < ms.Count ? ms[i + 1].Index : line.Length;
                    var v = Clean(line[s..e]);
                    var label = ms[i].Groups["l"].Value;
                    var field = LabelMap[label];
                    // 备注后面的内容整段都是备注，不再拆
                    if (field == F.Note) { v = Clean(line[s..]); e = line.Length; }
                    if (v.Length > 0 && Set(d, field, v, label, allowNew: true)) { list.Add(d); d = new Draft(); Set(d, field, v, label, allowNew: false); }
                    if (field == F.Note) break;
                }
                continue;
            }

            // 标题行：【TikTok 后台】、1. Gmail、## 服务器、Gmail：
            if (IsHeader(line, out var title)) { StartName(ref d, list, title); continue; }

            Unlabeled(ref d, list, line);
        }
        Push();
        return list;
    }

    static bool IsRealLabel(Match m)
    {
        var label = m.Groups["l"].Value;
        var sep = m.Groups["sep"].Value;
        if (sep.Contains(':') || sep.Contains('：') || sep.Contains('=') || sep.Contains('．')) return true;
        if (Strict(label.ToLowerInvariant())) return false;          // id、pw、ip 这类必须带冒号
        var line = m.Result("$_");
        int after = m.Index + m.Length;
        if (after >= line.Length) return false;                       // 整行就是“密码”两个字
        char next = line[after];
        bool atStart = m.Index == 0 || char.IsWhiteSpace(line[m.Index - 1]);
        if (!HasCjk(label)) return sep.Length > 0 && atStart;          // username abc（password123 不算）
        // 中文标签没冒号：后面是英文数字（密码123、账号 abc）才算；
        // 在行首、后面隔空格跟中文也算（备注 运营主账号）；“网站后台”“主账号，”不算
        if (next < 128 && (char.IsLetterOrDigit(next) || "+@._-!#$%&*".Contains(next))) return true;
        return atStart && sep.Length > 0;
    }

    static bool IsHeader(string line, out string title)
    {
        title = "";
        if (UrlRx.IsMatch(line) || line.Contains('@')) return false;
        var m = HeaderRx.Match(line);
        if (m.Success && (line.TrimStart()[0] is '【' or '[' or '#' or '-' or '*' or '•' or '>' || char.IsDigit(line.TrimStart()[0]) || line.EndsWith('：') || line.EndsWith(':')))
        {
            title = Clean(m.Groups["t"].Value);
            return title.Length > 0 && title.Length <= 40;
        }
        if ((line.EndsWith('：') || line.EndsWith(':')) && line.Length <= 40) { title = Clean(line.TrimEnd(':', '：')); return title.Length > 0; }
        return false;
    }

    /// <summary>没有标签的一行：可能是 网址 / 账号----密码 / 名称 账号 密码 / 单独一个名称。</summary>
    static void Unlabeled(ref Draft d, List<Draft> list, string line)
    {
        // 先取出网址
        var urls = UrlRx.Matches(line).Select(m => m.Value).ToList();
        var rest = UrlRx.Replace(line, " ");
        // 带空格的手机号（+62 812-1234-5678）先整个取出来，免得被空格拆开
        var phones = PhoneInline.Matches(rest).Select(m => m.Value.Trim()).Where(p => p.Count(char.IsDigit) >= 9).ToList();
        foreach (var ph in phones) rest = rest.Replace(ph, " \u0001" + phones.IndexOf(ph) + " ");
        // 常见分隔：----、 | 、 / 、制表符、空白
        var tokens = Regex.Split(rest, @"-{2,}|\s+\|\s+|\s+/\s+|\t|\s+|，|,|；|;").Select(Clean).Where(t => t.Length > 0).ToList();
        // user:pass 写在一起
        if (tokens.Count == 1 && urls.Count == 0 && Regex.Match(tokens[0], @"^([^:\s]+):([^:\s]+)$") is { Success: true } up)
            tokens = new() { up.Groups[1].Value, up.Groups[2].Value };

        tokens = tokens.Select(t => t.StartsWith('\u0001') && int.TryParse(t[1..], out var k) && k < phones.Count ? phones[k] : t).ToList();
        var names = tokens.Where(t => HasCjk(t)).ToList();
        var creds = tokens.Where(t => !HasCjk(t)).ToList();

        // 整行都是普通单词（TikTok Shop 后台）：是名称，不是账号密码
        if (urls.Count == 0 && creds.Count > 0 && !creds.Any(LooksLikeCredential) && tokens.Count <= 6)
        { StartName(ref d, list, line); return; }
        // 这一行自带账号密码，而上一条已经有了：先结束上一条
        if (d.HasCred && (names.Count > 0 || creds.Count >= 2)) { list.Add(d); d = new Draft(); }

        // 只有一个纯文字短行、没有网址：当作新记录的名称
        if (urls.Count == 0 && creds.Count == 0) { StartName(ref d, list, string.Join(" ", names)); return; }
        if (urls.Count == 0 && creds.Count == 1 && names.Count == 0 && !d.HasCred && d.Name.Length == 0 && !LooksLikeCredential(creds[0]))
        { d.Name = creds[0]; return; }

        if (names.Count > 0) StartName(ref d, list, string.Join(" ", names));
        foreach (var u in urls) if (Set(d, F.Url, u, "网址", allowNew: true)) { list.Add(d); d = new Draft(); d.Url = u; }

        if (creds.Count >= 2)
        {
            // 第一个像名称（不像账号）且后面还有两个：名称 账号 密码
            int i = 0;
            if (creds.Count >= 3 && d.Name.Length == 0 && !EmailRx.IsMatch(creds[0]) && !PhoneRx.IsMatch(creds[0]) && (EmailRx.IsMatch(creds[1]) || PhoneRx.IsMatch(creds[1]) || !LooksLikeCredential(creds[0])))
                d.Name = creds[i++];
            Set(d, F.Account, creds[i++], "账号", false);
            Set(d, F.Password, creds[i++], "密码", false);
            if (i < creds.Count) d.Note = string.Join(" ", creds.Skip(i));
        }
        else if (creds.Count == 1)
        {
            var t = creds[0];
            if (EmailRx.IsMatch(t) || PhoneRx.IsMatch(t))
            {
                if (d.Account.Length > 0) { list.Add(d); d = new Draft(); }
                d.Account = t;
            }
            else if (d.Account.Length > 0 && d.Password.Length == 0) d.Password = t;
            else if (d.Account.Length == 0) d.Account = t;
            else { list.Add(d); d = new Draft { Account = t }; }
        }
    }

    static bool LooksLikeCredential(string t) =>
        EmailRx.IsMatch(t) || PhoneRx.IsMatch(t) || (t.Any(char.IsDigit) && t.Any(char.IsLetter)) || t.Any(c => !char.IsLetterOrDigit(c) && c != ' ' && c != '-');

    static void StartName(ref Draft d, List<Draft> list, string name)
    {
        name = Clean(name);
        if (name.Length == 0) return;
        name = Regex.Replace(name, @"^[一二三四五六七八九十]+\s*[、.．]\s*", "");
        if (d.HasCred || d.Name.Length > 0 && d.Url.Length > 0) { list.Add(d); d = new Draft(); }
        // 连着两行标题（文档大标题 + 小标题）：以后一行为准
        d.Name = name;
    }

    /// <summary>填一个字段。返回 true 表示这个字段已经有值了 → 应该是下一条记录开始了。</summary>
    static bool Set(Draft d, F f, string v, string label, bool allowNew)
    {
        switch (f)
        {
            case F.Name: if (d.Name.Length > 0 && allowNew && d.HasCred) return true; d.Name = d.Name.Length == 0 ? v : d.Name; return false;
            case F.Url:
                if (d.Url.Length > 0 && allowNew && d.HasCred) return true;
                if (d.Url.Length == 0) d.Url = v; else AddField(d, label, v, false);
                return false;
            case F.Port: d.Url = d.Url.Length > 0 && !Regex.IsMatch(d.Url, @":\d+$") ? d.Url + ":" + v : d.Url; if (d.Url.Length == 0) AddField(d, "端口", v, false); return false;
            case F.Account:
                if (d.Account.Length > 0 && allowNew && d.Password.Length > 0) return true;
                if (d.Account.Length == 0) d.Account = v; else AddField(d, label, v, false);
                return false;
            case F.Email:
            case F.Phone:
                if (d.Account.Length == 0) { d.Account = v; return false; }
                if (d.Account == v) return false;
                AddField(d, f == F.Email ? "邮箱" : "手机", v, false);
                return false;
            case F.Password:
                if (d.Password.Length > 0 && allowNew) return true;
                d.Password = v; return false;
            case F.Secret: AddField(d, label.Length > 0 ? label : "支付密码", v, true); return false;
            case F.Extra: AddField(d, label, v, Regex.IsMatch(label, "key|token|密钥|2fa|otp|验证", RegexOptions.IgnoreCase)); return false;
            case F.Note: d.Note = d.Note.Length == 0 ? v : d.Note + "\n" + v; return false;
        }
        return false;
    }

    static void AddField(Draft d, string name, string v, bool secret)
    {
        if (d.Fields.Any(x => x.Value == v)) return;
        d.Fields.Add(new VaultField { Name = name, Value = v, Secret = secret });
    }

    /// <summary>表头认列：“平台名称”“登录网址”这种包含标签的也算；英文短词要整词相等。</summary>
    static F? HeaderField(string h)
    {
        if (h.Length == 0 || h.Length > 10) return null;
        if (LabelMap.TryGetValue(h, out var exact)) return exact;
        foreach (var (l, f) in Labels.OrderByDescending(x => x.Label.Length))
            if (HasCjk(l) && h.Contains(l)) return f;
        return null;
    }

    static F? Lookup(string label) => LabelMap.TryGetValue(label.Trim(), out var f) ? f : null;

    static string Clean(string s) => s.Trim().Trim(',', '，', ';', '；', '、', '|', '/', '：', ':', '-', '—', '【', '】', '[', ']', '(', ')', '（', '）', '"', '“', '”', '\'').Trim();

    static bool IsCjk(char c) => c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿';
    static bool HasCjk(string s) => s.Any(IsCjk);

    // ================= 收尾：补名称、猜分组 =================
    static VaultEntry Finish(Draft d, IList<string> groups)
    {
        var name = d.Name;
        if (name.Length == 0) name = NameFromUrl(d.Url);
        if (name.Length == 0 && d.Account.Contains('@')) name = NameFromUrl(d.Account.Split('@')[1]);
        if (name.Length == 0) name = "未命名";
        var e = new VaultEntry { Name = name, Url = d.Url, Account = d.Account, Password = d.Password, Note = d.Note, Fields = d.Fields };
        e.Group = GuessGroup(e, groups);
        return e;
    }

    static string NameFromUrl(string url)
    {
        if (url.Length == 0) return "";
        var host = Regex.Replace(url, @"^[a-z]+://", "", RegexOptions.IgnoreCase).Split('/', ':')[0];
        if (Regex.IsMatch(host, @"^\d+(\.\d+){3}$")) return "服务器 " + host;
        // 取主域名：mca.hota.live → hota，livin.bankmandiri.co.id → bankmandiri
        var parts = host.Split('.').ToList();
        if (parts.Count >= 3 && parts[^2] is "co" or "com" or "ac" or "go" or "or" or "net" or "org" or "gov" or "edu") parts.RemoveAt(parts.Count - 1);
        var main = parts.Count >= 2 ? parts[^2] : parts[0];
        return main.Length == 0 ? host : char.ToUpperInvariant(main[0]) + main[1..];
    }

    static readonly (string Group, string Pattern)[] GroupRules =
    {
        ("银行支付", @"银行|网银|bank|bca|mandiri|bni|bri|cimb|支付|pay|dana|ovo|gopay|alipay|wechat ?pay|paypal|wise|信用卡"),
        ("邮箱", @"邮箱|^mail|gmail|outlook|hotmail|yahoo|foxmail|163\.com|mail\.qq"),
        ("服务器", @"服务器|server|ssh|root|vps|nas|数据库|database|mysql|\b\d{1,3}(\.\d{1,3}){3}\b"),
        ("直播账号", @"tiktok|抖音|直播|主播|live|bigo|youtube|instagram|快手|kwai|shopee|tokopedia"),
        ("公司后台", @"后台|admin|管理|企业微信|work\.weixin|飞书|feishu|lark|钉钉|dingtalk|erp|crm|oa"),
    };

    static string GuessGroup(VaultEntry e, IList<string> groups)
    {
        // 先看名称、网址、备注，再看账号（hota@gmail.com 不代表是邮箱类）
        foreach (var s in new[] { $"{e.Name} {e.Url} {e.Note}", e.Account })
            foreach (var (g, p) in GroupRules)
                if (groups.Contains(g) && Regex.IsMatch(s, p, RegexOptions.IgnoreCase)) return g;
        return groups.Contains("其他") ? "其他" : "";
    }

    static List<List<string>> Csv(string s)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); bool q = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (q) { if (c == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { cell.Append('"'); i++; } else q = false; } else cell.Append(c); }
            else if (c == '"') q = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new(); }
            else cell.Append(c);
        }
        row.Add(cell.ToString()); rows.Add(row);
        return rows;
    }
}
