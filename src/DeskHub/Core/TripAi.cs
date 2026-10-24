using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeskHub.Core;

/// <summary>识别出来的一条日程（还没存进日历）。</summary>
public sealed record TripDraft(DateTime Day, string Time, string Text);

/// <summary>
/// 智能识别：把截图、聊天记录、订单文字变成一条条日程。
/// 配了 AI 服务（OpenAI 兼容格式：公司一体机、OpenAI、Claude、DeepSeek、通义、Ollama……）就交给 AI；
/// 没配就在本机识字（Windows 自带 OCR）+ 规则找日期，准确度一般，但不联网。
/// </summary>
public static class TripAi
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    /// <summary>发图被拒过的模型（这次运行里不再发图，直接先识字）。</summary>
    static readonly HashSet<string> NoVision = new(StringComparer.OrdinalIgnoreCase);

    public static bool Configured(AppConfig c) => c.AiUrl.Trim().Length > 0 && c.AiModel.Trim().Length > 0;

    /// <summary>界面上显示用：“gpt-4o · api.openai.com”。</summary>
    public static string Describe(AppConfig c)
    {
        var host = Uri.TryCreate(c.AiUrl.Trim(), UriKind.Absolute, out var u) ? u.Authority : c.AiUrl.Trim();
        return $"{c.AiModel.Trim()} · {host}";
    }

    /// <summary>识别结果：条目 + 一句说明（比如“这个模型看不了图，先在本机识了字”）。</summary>
    public sealed record Result(List<TripDraft> Items, string Note);

    public static async Task<Result> Extract(AppConfig c, string text, IReadOnlyList<byte[]> images, DateTime today, CancellationToken ct)
    {
        text = text.Trim();
        if (!Configured(c))
        {
            var all = text;
            foreach (var img in images) all += "\n" + await Ocr(img);
            return new(TripRules.Parse(all, today), L.T("用的是本机识别（没配 AI 服务）"));
        }
        var key = Secret.Unprotect(c.AiKey);
        var model = c.AiModel.Trim();
        if (images.Count > 0 && !NoVision.Contains(model))
        {
            try { return new(await Ask(c.AiUrl, key, model, text, images, today, ct), ""); }
            catch (AiException e) when (e.Status is 400 or 415 or 422 or 500)
            {
                NoVision.Add(model); // 多半是模型看不了图：本机先识字，再把文字交给它
            }
        }
        var note = "";
        if (images.Count > 0)
        {
            foreach (var img in images) text += "\n" + await Ocr(img);
            note = L.T("这个模型看不了图，先在本机把截图识成文字再交给它");
        }
        return new(await Ask(c.AiUrl, key, model, text, Array.Empty<byte[]>(), today, ct), note);
    }

    // ================= AI 服务 =================
    public sealed class AiException(string message, int status = 0) : Exception(message)
    {
        public int Status { get; } = status;
    }

    /// <summary>地址可以填到 /v1，也可以填完整的 …/chat/completions。</summary>
    static string Endpoint(string url, string path)
    {
        url = url.Trim().TrimEnd('/');
        foreach (var tail in new[] { "/chat/completions", "/models" })
            if (url.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) { url = url[..^tail.Length]; break; }
        return url + path;
    }

    static HttpRequestMessage Req(HttpMethod m, string url, string key)
    {
        var r = new HttpRequestMessage(m, url);
        if (key.Length > 0) r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        // Claude 的 OpenAI 兼容地址：读模型列表要用它自己的写法
        if (r.RequestUri?.Host.EndsWith("anthropic.com", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (key.Length > 0) r.Headers.TryAddWithoutValidation("x-api-key", key);
            r.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        return r;
    }

    static async Task<string> Send(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AiException(L.T("等了 2 分钟没有回应，AI 服务可能太忙或者没开")); }
        catch (HttpRequestException) { throw new AiException(L.F("连不上 {0}：看看地址对不对、服务开了没有、这台电脑能不能访问它", req.RequestUri?.Authority ?? "")); }
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode) return body;
        int code = (int)resp.StatusCode;
        var msg = ErrorText(body);
        throw new AiException(code switch
        {
            401 or 403 => L.F("密钥不对或没有权限（{0}）", code),
            404 => L.F("地址或模型名不对（{0}）", code),
            429 => L.F("请求太多或额度用完了（{0}）", code),
            _ => L.F("AI 服务返回错误（{0}）", code),
        } + (msg.Length > 0 ? "：" + msg : ""), code);
    }

    static string ErrorText(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            var e = n?["error"];
            var s = e is JsonValue ? e.ToString() : e?["message"]?.ToString() ?? n?["message"]?.ToString() ?? "";
            return s.Length > 160 ? s[..160] + "…" : s;
        }
        catch { return body.Length > 160 ? body[..160] + "…" : body; }
    }

    /// <summary>测试连接：读模型列表（大多数 OpenAI 兼容服务都有 /models）。</summary>
    public static async Task<List<string>> ListModels(string url, string key, CancellationToken ct)
    {
        var body = await Send(Req(HttpMethod.Get, Endpoint(url, "/models"), key), ct);
        var list = new List<string>();
        try
        {
            var n = JsonNode.Parse(body);
            var arr = (n?["data"] ?? n?["models"]) as JsonArray;
            if (arr != null)
                foreach (var m in arr)
                    if ((m?["id"] ?? m?["name"] ?? m?["model"])?.ToString() is { Length: > 0 } id) list.Add(id);
        }
        catch { }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    static string Prompt(DateTime today) =>
        $$"""
        You turn the user's material (screenshots of booking apps such as Fliggy, Ctrip/Trip.com, Traveloka, airline or hotel apps; chat logs; emails; notes) into calendar items: flights, trains, buses, hotel check-in/check-out, meetings, appointments, deadlines, events, things to do.
        Today is {{today:yyyy-MM-dd}} ({{today.ToString("dddd", CultureInfo.InvariantCulture)}}). Resolve relative dates (tomorrow, next Wednesday, 明天, 后天, 下周三, besok, lusa…) against today. If a date has no year, use the nearest one that is not in the past.
        Reply with JSON only, no explanation:
        {"items":[{"date":"yyyy-MM-dd","time":"HH:mm or empty","text":"..."}]}
        Rules for "text": one line, at most 40 characters, in the same language as the material; keep what a person needs to see at a glance (flight/train number, from → to, hotel name, who/where for a meeting). Do not repeat the date or time in it.
        One item per leg / per event: a round trip is two items; a hotel stay is check-in (and check-out if given). Skip prices, order numbers, ads and boilerplate.
        If there is nothing schedulable, reply {"items":[]}.
        """;

    static async Task<List<TripDraft>> Ask(string url, string key, string model, string text, IReadOnlyList<byte[]> images, DateTime today, CancellationToken ct)
    {
        JsonNode user;
        if (images.Count == 0) user = text.Length > 0 ? text : "(empty)";
        else
        {
            var parts = new JsonArray();
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = text.Length > 0 ? text : "Find the schedule in these screenshots." });
            foreach (var img in images)
                parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(img) } });
            user = parts;
        }
        var payload = new JsonObject
        {
            // 不传 temperature / max_tokens：有些新模型只认默认值，传了反而报 400
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = Prompt(today) },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
        };
        var req = Req(HttpMethod.Post, Endpoint(url, "/chat/completions"), key);
        req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        var body = await Send(req, ct);
        string content;
        try
        {
            var msg = JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"];
            // 有的服务把 content 回成一段段的数组
            content = msg is JsonArray a ? string.Concat(a.Select(p => p?["text"]?.ToString() ?? "")) : msg?.ToString() ?? "";
        }
        catch { throw new AiException(L.T("AI 服务回的内容看不懂（不是 OpenAI 兼容格式？）")); }
        return ParseItems(content, today) ?? throw new AiException(L.T("AI 回的内容里没找到日程清单，可以再试一次"));
    }

    /// <summary>从 AI 的回复里取出清单（容忍 ```json 包裹、前后有废话、直接回数组）。null = 完全看不懂。</summary>
    internal static List<TripDraft>? ParseItems(string content, DateTime today)
    {
        int a = content.IndexOfAny(new[] { '{', '[' }), b = Math.Max(content.LastIndexOf('}'), content.LastIndexOf(']'));
        if (a < 0 || b <= a) return null;
        JsonNode? root;
        try { root = JsonNode.Parse(content[a..(b + 1)]); } catch { return null; }
        var arr = root as JsonArray ?? root?["items"] as JsonArray;
        if (arr == null) return null;
        var list = new List<TripDraft>();
        foreach (var it in arr)
        {
            var ds = it?["date"]?.ToString() ?? "";
            if (!DateTime.TryParseExact(ds.Trim(), new[] { "yyyy-MM-dd", "yyyy-M-d", "yyyy/M/d", "yyyy.M.d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            var time = Trip.NormTime(it?["time"]?.ToString() ?? "");
            var t = (it?["text"] ?? it?["title"])?.ToString()?.Trim() ?? "";
            if (t.Length == 0) continue;
            list.Add(new TripDraft(day, time, t.Length > 80 ? t[..80] : t));
        }
        return list;
    }

    // ================= 本机识字 =================
    /// <summary>Windows 自带的文字识别（离线）。识别不了就返回空。</summary>
    public static async Task<string> Ocr(byte[] png)
    {
        try
        {
            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            if (engine == null) return "";
            using var ras = new MemoryStream(png).AsRandomAccessStream();
            var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
            // 字小的图（电脑上截的小窗口）放大一倍再识，准不少；太大的缩到识别引擎的上限以内
            uint w = dec.PixelWidth, h = dec.PixelHeight, max = Math.Max(w, h);
            double k = max < 1200 ? 2 : Math.Min(1, Windows.Media.Ocr.OcrEngine.MaxImageDimension / (double)max);
            var tf = new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = (uint)(w * k), ScaledHeight = (uint)(h * k), InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant };
            using var bmp = await dec.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, tf,
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var r = await engine.RecognizeAsync(bmp);
            // 中文识别结果字和字之间带空格，去掉
            return string.Join("\n", r.Lines.Select(l => Regex.Replace(l.Text, @"(?<=[　-鿿＀-￯])\s+(?=[　-鿿＀-￯])", "")));
        }
        catch { return ""; }
    }
}

/// <summary>没有 AI 时的本机规则：找日期（2026-11-06、11月6日、6 Nov、明天、下周三……），日期后面那一段当作这件事。</summary>
public static class TripRules
{
    static readonly string[] MonEn = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
    static readonly Dictionary<string, int> MonMap = BuildMon();
    static Dictionary<string, int> BuildMon()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 12; i++) d[MonEn[i]] = i + 1;
        d["mei"] = 5; d["agu"] = 8; d["agt"] = 8; d["okt"] = 10; d["des"] = 12; // 印尼语
        return d;
    }
    const string MonRx = "(jan|feb|mar|apr|may|mei|jun|jul|aug|agu|agt|sep|oct|okt|nov|dec|des)[a-z]*\\.?";
    static readonly string WeekCn = "一二三四五六日天";

    static readonly Regex Full = new(@"(20\d{2})\s*[-/.年]\s*(\d{1,2})\s*[-/.月]\s*(\d{1,2})\s*[日号]?", RegexOptions.Compiled);
    static readonly Regex Cn = new(@"(?<!\d)(\d{1,2})\s*月\s*(\d{1,2})\s*[日号]?", RegexOptions.Compiled);
    static readonly Regex DMon = new(@"(?<![\d\w])(\d{1,2})\s+" + MonRx + @"(\s+(20\d{2}))?(?![a-z])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex MonD = new(@"(?<![\w])" + MonRx + @"\s+(\d{1,2})(?!\d)(,?\s+(20\d{2}))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Rel = new(@"大后天|后天|明天|今天|今晚|明早|明晚|tomorrow|today|tonight|besok|lusa|hari ini", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Week = new(@"(下下|下个?|这个?|本)?(周|星期|礼拜)([一二三四五六日天])", RegexOptions.Compiled);
    static readonly Regex TimeRx = new(@"(?<!\d)([01]?\d|2[0-3])\s*[:：]\s*([0-5]\d)(?!\d)", RegexOptions.Compiled);
    static readonly Regex Junk = new(@"(周|星期|礼拜)[一二三四五六日天]|上午|中午|下午|傍晚|晚上|早上|\b(Mon|Tue|Tues|Wed|Thu|Thur|Thurs|Fri|Sat|Sun)(day)?\b\.?,?|(Senin|Selasa|Rabu|Kamis|Jumat|Sabtu|Minggu),?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<TripDraft> Parse(string text, DateTime today)
    {
        text = Normalize(text);
        var hits = new List<(int Start, int End, DateTime Day)>();
        void Hit(Match m, DateTime? d) { if (d is { } day && !hits.Any(h => m.Index < h.End && h.Start < m.Index + m.Length)) hits.Add((m.Index, m.Index + m.Length, day)); }

        foreach (Match m in Full.Matches(text)) Hit(m, Make(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)));
        foreach (Match m in Cn.Matches(text)) Hit(m, Near(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), today));
        foreach (Match m in DMon.Matches(text))
            Hit(m, m.Groups[4].Success ? Make(int.Parse(m.Groups[4].Value), MonMap[m.Groups[2].Value[..3]], int.Parse(m.Groups[1].Value)) : Near(MonMap[m.Groups[2].Value[..3]], int.Parse(m.Groups[1].Value), today));
        foreach (Match m in MonD.Matches(text))
            Hit(m, m.Groups[4].Success ? Make(int.Parse(m.Groups[4].Value), MonMap[m.Groups[1].Value[..3]], int.Parse(m.Groups[2].Value)) : Near(MonMap[m.Groups[1].Value[..3]], int.Parse(m.Groups[2].Value), today));
        foreach (Match m in Rel.Matches(text))
        {
            var w = m.Value.ToLowerInvariant();
            int add = w switch { "大后天" => 3, "后天" or "lusa" => 2, "明天" or "明早" or "明晚" or "tomorrow" or "besok" => 1, _ => 0 };
            Hit(m, today.AddDays(add));
        }
        foreach (Match m in Week.Matches(text))
        {
            int want = WeekCn.IndexOf(m.Groups[3].Value[0]); want = want >= 6 ? 0 : want + 1; // 周一 = 1 … 周日 = 0
            int now = (int)today.DayOfWeek;
            int diff = (want - now + 7) % 7;
            var pre = m.Groups[1].Value;
            // “下周三”：下一个自然周里的周三；“周三”：今天以后最近的周三
            if (pre.StartsWith("下")) { int toMon = ((int)DayOfWeek.Monday - now + 7) % 7; if (toMon == 0) toMon = 7; diff = toMon + ((want + 6) % 7) + (pre == "下下" ? 7 : 0); }
            // 日期后面紧跟的“周五”只是星期几，前面已经有日期了，不算新的一天
            if (hits.Any(h => h.End <= m.Index && m.Index - h.End <= 3)) continue;
            Hit(m, today.AddDays(diff));
        }
        hits.Sort((a, b) => a.Start.CompareTo(b.Start));

        var list = new List<TripDraft>();
        for (int i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            int lineStart = text.LastIndexOf('\n', Math.Max(0, h.Start - 1)) + 1;
            int from = Math.Max(lineStart, i > 0 ? hits[i - 1].End : 0);
            int to = text.Length;
            if (i + 1 < hits.Count)
            {
                // 下一个日期在后面的行里：它那一行开头的字（“返程”“然后”）归它，不归这一条
                var next = hits[i + 1].Start;
                int nextLine = text.LastIndexOf('\n', Math.Max(0, next - 1)) + 1;
                to = nextLine > h.End ? nextLine : next;
            }
            to = Math.Min(to, h.End + 160);
            var seg = text[from..h.Start] + " " + text[h.End..to];
            var tm = TimeRx.Match(seg);
            var time = "";
            if (tm.Success)
            {
                int hh = int.Parse(tm.Groups[1].Value);
                // “下午 3:00”“晚上 8:00”“3:00 pm”
                var around = seg[Math.Max(0, tm.Index - 4)..Math.Min(seg.Length, tm.Index + tm.Length + 4)];
                if (hh < 12 && Regex.IsMatch(around, "下午|晚上|傍晚|pm|sore|malam", RegexOptions.IgnoreCase)) hh += 12;
                time = $"{hh:00}:{tm.Groups[2].Value}";
            }
            var s = TimeRx.Replace(seg, " ");
            s = Junk.Replace(s, " ");
            s = Regex.Replace(s, @"\s+", " ").Trim(' ', ',', '，', '、', '-', '—', '~', '～', '：', ':', '|', '/', '.', '。');
            if (s.Length == 0 && time.Length == 0) continue;
            if (s.Length > 40) s = s[..40].TrimEnd() + "…";
            if (s.Length == 0) s = L.T("（没认出是什么事）");
            var d = new TripDraft(h.Day, time, s);
            if (!list.Contains(d)) list.Add(d);
        }
        return list;
    }

    static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(ch is >= '０' and <= '９' ? (char)(ch - '０' + '0') : ch == '：' ? ':' : ch == '\r' ? '\n' : ch);
        // 截图识字常把“11 月 6 日”“07 : 45”拆开：月、日、冒号两边的数字粘回去，“1 1 月”拼成“11月”
        var r = Regex.Replace(sb.ToString(), @"(?<!\d)(\d) (\d)(?=\s*月)", "$1$2");
        r = Regex.Replace(r, @"(\d)\s+([月日号:])", "$1$2");
        r = Regex.Replace(r, @"([月:])\s+(\d)", "$1$2");
        return r;
    }

    static DateTime? Make(int y, int m, int d) => m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m) ? new DateTime(y, m, d) : null;

    /// <summary>没写年份：取今天以后最近的那个（过去一个月内的算今年，比如刚过去的行程）。</summary>
    static DateTime? Near(int m, int d, DateTime today)
    {
        if (Make(today.Year, m, d) is not { } x) return Make(today.Year + 1, m, d);
        return x < today.AddDays(-31) ? Make(today.Year + 1, m, d) ?? x : x;
    }
}

/// <summary>用 Windows 自带的 DPAPI 加密小段文字（只有本机当前账户解得开），存密钥用。</summary>
public static class Secret
{
    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr p);

    const int UiForbidden = 1;

    static byte[]? Run(byte[] data, bool protect)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var inp = new Blob { Size = data.Length, Data = h.AddrOfPinnedObject() };
            Blob outp;
            bool ok = protect
                ? CryptProtectData(ref inp, "DeskHub", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outp)
                : CryptUnprotectData(ref inp, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outp);
            if (!ok) return null;
            var r = new byte[outp.Size];
            Marshal.Copy(outp.Data, r, 0, outp.Size);
            LocalFree(outp.Data);
            return r;
        }
        finally { h.Free(); }
    }

    public static string Protect(string plain) =>
        plain.Length == 0 ? "" : Run(Encoding.UTF8.GetBytes(plain), true) is { } b ? Convert.ToBase64String(b) : "";

    public static string Unprotect(string stored)
    {
        if (stored.Length == 0) return "";
        try { return Run(Convert.FromBase64String(stored), false) is { } b ? Encoding.UTF8.GetString(b) : ""; }
        catch { return ""; }
    }
}
