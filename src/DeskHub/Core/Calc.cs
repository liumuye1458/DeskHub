using System.Globalization;

namespace DeskHub.Core;

/// <summary>
/// 计算器的算式求值：四则运算、括号、百分号。用十进制小数计算，不会出现 0.1+0.2=0.30000000000000004。
/// 百分号和手机计算器一致：200+10% = 220，200×10% = 20。
/// </summary>
public static class Calc
{
    public static bool TryEval(string expr, out decimal value, out string? error)
    {
        value = 0; error = null;
        var s = Normalize(expr);
        if (s.Length == 0) { error = ""; return false; }
        // 末尾没写完的右括号自动补上
        int open = s.Count(c => c == '(') - s.Count(c => c == ')');
        if (open > 0) s += new string(')', open);
        try
        {
            var p = new Parser(s);
            var (v, _) = p.Expr();
            if (p.Pos != s.Length) { error = L.T("算式不完整"); return false; }
            value = v;
            return true;
        }
        catch (DivideByZeroException) { error = L.T("不能除以 0"); return false; }
        catch (OverflowException) { error = L.T("数字太大了"); return false; }
        catch (FormatException) { error = L.T("算式不完整"); return false; }
    }

    /// <summary>把全角符号、× ÷ − 换成标准符号，去掉空格和千分位逗号。</summary>
    static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            char c = ch switch
            {
                '×' or 'x' or 'X' or '＊' => '*',
                '÷' or '／' => '/',
                '−' or '－' or '—' => '-',
                '＋' => '+',
                '（' => '(', '）' => ')',
                '％' => '%',
                '。' => '.',
                >= '０' and <= '９' => (char)(ch - '０' + '0'),
                _ => ch,
            };
            if (c is ' ' or ',' or '，' or '\t' or '=') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    sealed class Parser
    {
        readonly string _s;
        public int Pos;
        public Parser(string s) { _s = s; }

        char Peek => Pos < _s.Length ? _s[Pos] : '\0';

        // expr := term (('+'|'-') term)*
        public (decimal V, bool Pct) Expr()
        {
            var (v, pct) = Term();
            while (Peek is '+' or '-')
            {
                char op = _s[Pos++];
                var (r, rp) = Term();
                // a + b% = a × (1 + b%)
                if (rp) r = v * r;
                v = op == '+' ? v + r : v - r;
                pct = false;
            }
            return (v, pct);
        }

        // term := factor (('*'|'/') factor)*
        (decimal, bool) Term()
        {
            var (v, pct) = Factor();
            while (Peek is '*' or '/')
            {
                char op = _s[Pos++];
                var (r, _) = Factor();
                if (op == '/' && r == 0) throw new DivideByZeroException();
                v = op == '*' ? v * r : v / r;
                pct = false;
            }
            return (v, pct);
        }

        // factor := ('-'|'+') factor | primary '%'*
        (decimal, bool) Factor()
        {
            if (Peek == '-') { Pos++; var (v, p) = Factor(); return (-v, p); }
            if (Peek == '+') { Pos++; return Factor(); }
            var x = Primary();
            bool pct = false;
            while (Peek == '%') { Pos++; x /= 100; pct = true; }
            return (x, pct);
        }

        decimal Primary()
        {
            if (Peek == '(')
            {
                Pos++;
                var (v, _) = Expr();
                if (Peek != ')') throw new FormatException();
                Pos++;
                return v;
            }
            int start = Pos;
            while (char.IsDigit(Peek) || Peek == '.') Pos++;
            if (start == Pos) throw new FormatException();
            return decimal.Parse(_s.AsSpan(start, Pos - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>结果显示：千分位，最多 10 位小数，去掉末尾的 0。</summary>
    public static string Format(decimal v)
    {
        v = Math.Round(v, 10, MidpointRounding.AwayFromZero);
        return v.ToString("#,##0.##########", CultureInfo.InvariantCulture);
    }

    /// <summary>算式显示：运算符换成 × ÷ −，两边加空格，数字加千分位。</summary>
    public static string Pretty(string expr)
    {
        var s = Normalize(expr);
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsDigit(c) || c == '.')
            {
                int st = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                var num = s[st..i];
                int dot = num.IndexOf('.');
                var intPart = dot < 0 ? num : num[..dot];
                if (intPart.Length > 3 && decimal.TryParse(intPart, NumberStyles.None, CultureInfo.InvariantCulture, out var ip))
                    num = ip.ToString("#,##0", CultureInfo.InvariantCulture) + (dot < 0 ? "" : num[dot..]);
                sb.Append(num);
                continue;
            }
            bool unary = c == '-' && (i == 0 || s[i - 1] is '(' or '*' or '/' or '+' or '-');
            sb.Append(c switch
            {
                '*' => " × ",
                '/' => " ÷ ",
                '+' => " + ",
                '-' => unary ? "−" : " − ",
                _ => c.ToString(),
            });
            i++;
        }
        // 和计算时一样，把没写完的右括号补上
        int open = s.Count(ch => ch == '(') - s.Count(ch => ch == ')');
        if (open > 0) sb.Append(')', open);
        return sb.ToString();
    }
}
