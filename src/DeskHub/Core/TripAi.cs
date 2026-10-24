using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeskHub.Core;

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
    public sealed record Result(List<Trip> Items, string Note);

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
        You turn the user's material (screenshots of booking apps such as Fliggy, Ctrip/Trip.com, Traveloka, airline, rail or hotel apps; chat logs; emails; notes) into calendar items.
        Today is {{today:yyyy-MM-dd}} ({{today.ToString("dddd", CultureInfo.InvariantCulture)}}). Resolve relative dates (tomorrow, next Wednesday, 明天, 后天, 下周三, besok, lusa…) against today. If a date has no year, use the nearest one that is not in the past.
        Ignore booking / payment / order-creation dates and times: only the dates when things actually happen count.
        Reply with JSON only, no explanation:
        {"items":[{"kind":"flight|train|hotel|event","date":"yyyy-MM-dd","time":"HH:mm","end_date":"yyyy-MM-dd","end_time":"HH:mm","from":"","to":"","from_at":"","to_at":"","no":"","carrier":"","place":"","text":""}]}
        Fields:
        - flight, train (also bus / ferry → "train"): date+time = departure, end_date+end_time = arrival (local times as printed). from / to = short CITY names (雅加达, 新加坡, Guangzhou), never airport names. from_at / to_at = airport or station short name plus terminal, e.g. "苏加诺-哈达 T3", "广州南站". no = flight / train number without spaces (SQ951, G1234). carrier = airline / operator. text = seat, cabin or a short note, or "".
        - hotel: date = check-in day, end_date = check-out day, time = check-in time if printed. place = hotel name (short, no address). text = room type or "".
        - event (meetings, appointments, deadlines, anything else): date, time = start, end_time if given, place if given. text = what it is, at most 30 characters, without the date/time.
        Leave unknown fields as "". Write names and text in the same language as the material.
        One item per leg / per event: a round trip is two flights; a connecting journey is one item per flight. Skip prices, order numbers, ads and boilerplate.
        If there is nothing schedulable, reply {"items":[]}.
        """;

    static async Task<List<Trip>> Ask(string url, string key, string model, string text, IReadOnlyList<byte[]> images, DateTime today, CancellationToken ct)
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
        return ParseItems(content) ?? throw new AiException(L.T("AI 回的内容里没找到日程清单，可以再试一次"));
    }

    static DateTime? Day(string? s) =>
        DateTime.TryParseExact((s ?? "").Trim(), new[] { "yyyy-MM-dd", "yyyy-M-d", "yyyy/M/d", "yyyy.M.d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>从 AI 的回复里取出清单（容忍 ```json 包裹、前后有废话、直接回数组）。null = 完全看不懂。</summary>
    internal static List<Trip>? ParseItems(string content)
    {
        int a = content.IndexOfAny(new[] { '{', '[' }), b = Math.Max(content.LastIndexOf('}'), content.LastIndexOf(']'));
        if (a < 0 || b <= a) return null;
        JsonNode? root;
        try { root = JsonNode.Parse(content[a..(b + 1)]); } catch { return null; }
        var arr = root as JsonArray ?? root?["items"] as JsonArray;
        if (arr == null) return null;
        var list = new List<Trip>();
        foreach (var it in arr)
        {
            if (it == null || Day(it["date"]?.ToString()) is not { } day) continue;
            string S(string k) { var v = it[k]?.ToString()?.Trim() ?? ""; return v.Length > 60 ? v[..60] : v; }
            var kind = S("kind").ToLowerInvariant() switch
            {
                "flight" or "plane" or "air" => Trip.Flight,
                "train" or "bus" or "ferry" or "rail" => Trip.Train,
                "hotel" or "lodging" or "stay" => Trip.Hotel,
                _ => "",
            };
            var t = new Trip
            {
                Kind = kind, Date = TripStore.Key(day), Time = S("time"),
                EndDate = Day(S("end_date")) is { } e ? TripStore.Key(e) : "", EndTime = S("end_time"),
                From = S("from"), To = S("to"), FromAt = S("from_at"), ToAt = S("to_at"),
                No = S("no"), Carrier = S("carrier"), Place = S("place"), Text = S("text").Length > 0 ? S("text") : S("title"),
            };
            t.Tidy();
            if (!t.IsEmpty) list.Add(t);
        }
        list.Sort(TripStore.Order);
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
            double k = max < 1200 ? Math.Clamp(1800.0 / max, 2, 4) : Math.Min(1, Windows.Media.Ocr.OcrEngine.MaxImageDimension / (double)max);
            var tf = new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = (uint)(w * k), ScaledHeight = (uint)(h * k), InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant };
            using var bmp = await dec.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, tf,
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var r = await engine.RecognizeAsync(bmp);
            // 识别引擎是一栏一栏读的（左边读完才读右边），订单截图左右两栏的出发 / 到达就被拆散了；
            // 按坐标把同一高度的几段拼回一行，从左到右
            var rows = new List<(double Top, double Bottom, List<(double X, string Text)> Parts)>();
            foreach (var l in r.Lines.Where(l => l.Words.Count > 0).OrderBy(l => l.Words.Min(w => w.BoundingRect.Y)))
            {
                double top = l.Words.Min(w => w.BoundingRect.Y), bottom = l.Words.Max(w => w.BoundingRect.Bottom), x = l.Words.Min(w => w.BoundingRect.X);
                // 中文识别结果字和字之间带空格，去掉
                var text = Regex.Replace(l.Text, @"(?<=[　-鿿＀-￯])\s+(?=[　-鿿＀-￯])", "");
                int i = rows.FindIndex(row => Math.Min(bottom, row.Bottom) - Math.Max(top, row.Top) > 0.5 * Math.Min(bottom - top, row.Bottom - row.Top));
                if (i < 0) rows.Add((top, bottom, new() { (x, text) }));
                else { rows[i].Parts.Add((x, text)); rows[i] = (Math.Min(top, rows[i].Top), Math.Max(bottom, rows[i].Bottom), rows[i].Parts); }
            }
            return string.Join("\n", rows.OrderBy(row => row.Top).Select(row => string.Join("  ", row.Parts.OrderBy(p => p.X).Select(p => p.Text))));
        }
        catch { return ""; }
    }
}

//// <summary>
/// 没有 AI 时的本机规则：找日期（2026-11-06、11月6日、6 Nov、明天、下周三……），日期后面那一段当作这件事；
/// 那一段里有航班号 / 车次就按机票、火车拆出出发到达城市和时间，有酒店就记成入住。认不出像样内容的不要。
/// </summary>
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

    static readonly Regex Full = new(@"(20\d{2})\s*[-/.年·]\s*(\d{1,2})\s*[-/.月·\s]\s*(\d{1,2})(?!\d)\s*[日号]?", RegexOptions.Compiled);
    static readonly Regex Cn = new(@"(?<!\d)(\d{1,2})\s*月\s*(\d{1,2})\s*[日号]?", RegexOptions.Compiled);
    static readonly Regex DMon = new(@"(?<![\d\w])(\d{1,2})\s+" + MonRx + @"(\s+(20\d{2}))?(?![a-z])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex MonD = new(@"(?<![\w])" + MonRx + @"\s+(\d{1,2})(?!\d)(,?\s+(20\d{2}))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Rel = new(@"大后天|后天|明天|今天|今晚|明早|明晚|tomorrow|today|tonight|besok|lusa|hari ini", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Week = new(@"(下下|下个?|这个?|本)?(周|星期|礼拜)([一二三四五六日天])", RegexOptions.Compiled);
    static readonly Regex TimeRx = new(@"(?<!\d)([01]?\d|2[0-3])\s*[:：]\s*([0-5]\d)(?!\d)", RegexOptions.Compiled);
    /// <summary>口语的钟点：3点、3点半、3点15分、15点。</summary>
    static readonly Regex CnTimeRx = new(@"(?<!\d)([01]?\d|2[0-3])\s*点\s*(半|一刻|三刻|([0-5]?\d)\s*分?)?", RegexOptions.Compiled);
    static readonly Regex NightsRx = new(@"(\d{1,2})\s*(晚|nights?|malam)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex RoomRx = new(@"[\p{IsCJKUnifiedIdeographs}]{0,6}(大床房|双床房|标准间|标间|套房|单人间|双人间|家庭房)|\b(Deluxe|Superior|Standard|Premier|Executive)[\w ]{0,20}?(Room|Suite|King|Twin)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Junk = new(@"(周|星期|礼拜)[一二三四五六日天]|上午|中午|下午|傍晚|晚上|早上|\b(Mon|Tue|Tues|Wed|Thu|Thur|Thurs|Fri|Sat|Sun)(day)?\b\.?,?|(Senin|Selasa|Rabu|Kamis|Jumat|Sabtu|Minggu),?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>下单、付款这类时间不是出行时间：这些行里的日期不算。</summary>
    static readonly Regex OrderLine = new(@"下单|预订时间|预定时间|订单时间|创建时间|支付|付款|出票时间|booked|booking date|ordered|paid|dipesan|dibayar", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex FlightNo = new(@"(?<![A-Za-z0-9])([A-Za-z][A-Za-z0-9]|[0-9][A-Za-z])\s?(\d{2,4})(?![0-9])", RegexOptions.Compiled);
    static readonly Regex TrainNo = new(@"(?<![A-Za-z0-9])([GDCZTKYL])(\d{1,4})(?![0-9])", RegexOptions.Compiled);
    static readonly Regex HotelRx = new(@"[\p{L}\p{N}·&'\- ]{0,24}?(酒店|宾馆|饭店|民宿|公寓|Hotel|Resort|Hostel|Inn|Villa)[\p{L}\p{N}·&'\- ]{0,16}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex CarrierRx = new(@"([\p{IsCJKUnifiedIdeographs}]{2,6}航空)|([A-Z][a-z]+ (Airlines|Airways|Air))", RegexOptions.Compiled);

    /// <summary>常见城市（中文 / 英文 / 印尼文 / 三字码）。截图里按出现先后：第一个是出发，第二个是到达。</summary>
    static readonly string[] Cities =
    {
        "雅加达", "新加坡", "广州", "深圳", "上海", "北京", "香港", "澳门", "台北", "吉隆坡", "曼谷", "普吉", "清迈", "巴厘岛", "登巴萨", "泗水", "棉兰", "万隆", "日惹", "三宝垄", "望加锡", "巴淡",
        "杭州", "成都", "重庆", "西安", "厦门", "福州", "昆明", "南宁", "海口", "三亚", "武汉", "长沙", "南京", "青岛", "天津", "郑州", "济南", "沈阳", "大连", "哈尔滨", "贵阳",
        "汕头", "揭阳", "珠海", "温州", "宁波", "合肥", "南昌", "兰州", "乌鲁木齐", "东京", "大阪", "名古屋", "福冈", "首尔", "釜山", "胡志明", "河内", "岘港", "马尼拉", "宿务",
        "金边", "暹粒", "仰光", "新德里", "孟买", "迪拜", "多哈", "伊斯坦布尔", "伦敦", "巴黎", "法兰克福", "阿姆斯特丹", "罗马", "米兰", "悉尼", "墨尔本", "珀斯", "奥克兰",
        "纽约", "洛杉矶", "旧金山", "温哥华", "多伦多",
        "Jakarta", "Singapore", "Guangzhou", "Shenzhen", "Shanghai", "Beijing", "Hong Kong", "Macau", "Taipei", "Kuala Lumpur", "Bangkok", "Phuket", "Bali", "Denpasar",
        "Surabaya", "Medan", "Bandung", "Yogyakarta", "Semarang", "Makassar", "Batam", "Tokyo", "Osaka", "Seoul", "Ho Chi Minh", "Hanoi", "Manila", "Dubai", "London", "Paris", "Sydney", "Melbourne",
        "CGK", "HLP", "SIN", "CAN", "SZX", "PVG", "SHA", "PEK", "PKX", "HKG", "MFM", "TPE", "KUL", "BKK", "DMK", "HKT", "DPS", "SUB", "KNO", "BDO", "JOG", "YIA", "SRG", "UPG", "BTH",
        "HGH", "CTU", "TFU", "CKG", "XIY", "XMN", "FOC", "KMG", "NNG", "HAK", "SYX", "WUH", "CSX", "NKG", "TAO", "TSN", "NRT", "HND", "KIX", "ICN", "SGN", "HAN", "MNL", "DXB", "LHR", "CDG", "SYD", "MEL",
    };
    static readonly string CityAlt = string.Join("|", Cities.OrderByDescending(c => c.Length).Select(c => c.All(char.IsUpper) ? $@"\b{c}\b" : Regex.Escape(c)));
    static readonly Regex CityRx = new(CityAlt, RegexOptions.Compiled);
    /// <summary>“深圳 – 雅加达”这种一行的航线标题（飞猪、携程把它放在日期上面一行；识字常把“–”认成“一”）。</summary>
    static readonly Regex RouteRx = new(@"(" + CityAlt + @")\s*(?:[-–—一→~～>＞]+|至|到|to)\s*(" + CityAlt + @")", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>机场 + 航站楼：宝安国际机场 T3、樟宜机场 T2、Soekarno-Hatta Airport T3。</summary>
    static readonly Regex AirportRx = new(@"([\p{IsCJKUnifiedIdeographs}\-一]{2,12}机场|[A-Z][\w\-]+(?: [A-Z][\w\-]+)* (?:International )?Airport)\s*(T\s?\d)?", RegexOptions.Compiled);
    static bool IsRouteLine(string line) => RouteRx.IsMatch(line) && !Full.IsMatch(line) && !Cn.IsMatch(line);

    public static List<Trip> Parse(string text, DateTime today)
    {
        text = Normalize(text);
        var hits = new List<(int Start, int End, DateTime Day)>();
        bool OnOrderLine(int at)
        {
            int ls = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1, le = text.IndexOf('\n', at); if (le < 0) le = text.Length;
            return OrderLine.IsMatch(text[ls..le]);
        }
        void Hit(Match m, DateTime? d)
        {
            if (d is { } day && !OnOrderLine(m.Index) && !hits.Any(h => m.Index < h.End && h.Start < m.Index + m.Length)) hits.Add((m.Index, m.Index + m.Length, day));
        }

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

        var list = new List<Trip>();
        for (int i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            int lineStart = text.LastIndexOf('\n', Math.Max(0, h.Start - 1)) + 1;
            int from = Math.Max(lineStart, i > 0 ? hits[i - 1].End : 0);
            // 日期上面一行是航线标题（“深圳 – 雅加达”）：归这一条
            if (from == lineStart && lineStart > 0)
            {
                int prevStart = text.LastIndexOf('\n', Math.Max(0, lineStart - 2)) + 1;
                if ((i == 0 || prevStart >= hits[i - 1].End) && IsRouteLine(text[prevStart..(lineStart - 1)])) from = prevStart;
            }
            int to = text.Length;
            if (i + 1 < hits.Count)
            {
                // 下一个日期在后面的行里：它那一行开头的字（“返程”“然后”）归它，不归这一条
                var next = hits[i + 1].Start;
                int nextLine = text.LastIndexOf('\n', Math.Max(0, next - 1)) + 1;
                to = nextLine > h.End ? nextLine : next;
                // 下一个日期上面的航线标题归下一条
                if (to == nextLine && nextLine > h.End)
                {
                    int prevStart = text.LastIndexOf('\n', Math.Max(0, nextLine - 2)) + 1;
                    if (prevStart > h.End && IsRouteLine(text[prevStart..(nextLine - 1)])) to = prevStart;
                }
            }
            to = Math.Min(to, h.End + 220);
            var seg = text[from..h.Start] + " " + text[h.End..to];
            // 订单号、价格那几行不要
            seg = string.Join("\n", seg.Split('\n').Where(l => !OrderLine.IsMatch(l) && !Regex.IsMatch(l, @"订单号|order no|¥|￥|Rp\s?\d|CNY|IDR|\$\s?\d", RegexOptions.IgnoreCase)));
            if (Build(h.Day, seg) is { } t && !list.Any(x => x.Sig == t.Sig)) list.Add(t);
        }
        list.Sort(TripStore.Order);
        return list;
    }

    static List<string> Times(string seg)
    {
        var r = new List<(int At, string T)>();
        string Pm(Match tm, int hh, string mm)
        {
            // “下午 3:00”“晚上 8 点”“3:00 pm”
            var around = seg[Math.Max(0, tm.Index - 4)..Math.Min(seg.Length, tm.Index + tm.Length + 4)];
            if (hh < 12 && Regex.IsMatch(around, "下午|晚上|傍晚|pm|sore|malam", RegexOptions.IgnoreCase)) hh += 12;
            return $"{hh:00}:{mm}";
        }
        foreach (Match tm in TimeRx.Matches(seg)) r.Add((tm.Index, Pm(tm, int.Parse(tm.Groups[1].Value), tm.Groups[2].Value)));
        foreach (Match tm in CnTimeRx.Matches(seg))
        {
            var g = tm.Groups[2].Value;
            int mm = g == "半" ? 30 : g == "一刻" ? 15 : g == "三刻" ? 45 : tm.Groups[3].Success ? int.Parse(tm.Groups[3].Value) : 0;
            if (mm < 60) r.Add((tm.Index, Pm(tm, int.Parse(tm.Groups[1].Value), $"{mm:00}")));
        }
        return r.OrderBy(x => x.At).Select(x => x.T).ToList();
    }

    /// <summary>一段文字 → 一条行程（认不出像样内容就 null）。</summary>
    static Trip? Build(DateTime day, string seg)
    {
        var times = Times(seg);
        var t = new Trip { Date = TripStore.Key(day), Time = times.FirstOrDefault() ?? "" };
        var carrier = CarrierRx.Match(seg);
        // 城市：去掉“新加坡航空”这种航空公司名再找
        var forCity = carrier.Success ? seg.Replace(carrier.Value, " ") : seg;
        var cities = CityRx.Matches(forCity).Select(m => m.Value).Distinct().ToList();
        var route = RouteRx.Match(forCity);
        if (route.Success) cities = new() { route.Groups[1].Value, route.Groups[2].Value };
        var airports = AirportRx.Matches(seg).Select(m =>
        {
            // “雅加达苏加诺-哈达机场”前面的城市名去掉；“一”多半是识字把“-”认错了
            var a = CityRx.Replace(m.Groups[1].Value, "", 1, 0).Replace('一', '-').Trim('-', ' ');
            return a.Length < 2 ? "" : (a + (m.Groups[2].Success ? " " + m.Groups[2].Value.Replace(" ", "") : "")).Trim();
        }).Where(a => a.Length > 0).Distinct().ToList();

        var fno = FlightNo.Matches(seg).FirstOrDefault(m => !Regex.IsMatch(m.Value, @"^([Tt]\d|[Aa]3\d|[Bb]7\d)") && Regex.IsMatch(m.Groups[1].Value, "[A-Za-z]"));
        var tno = Regex.IsMatch(seg, "站|高铁|动车|火车|train|kereta", RegexOptions.IgnoreCase) ? TrainNo.Match(seg) : Match.Empty;
        bool flightWords = Regex.IsMatch(seg, "航班|机场|起飞|航空|flight|airport|airlines|airways|penerbangan", RegexOptions.IgnoreCase);
        // 有航班号；或者航班号没识出来，但有“深圳 – 雅加达”这样的航线 + 机场 / 航空公司 / 起降两个时间
        if (tno.Success || (fno != null && (cities.Count > 0 || times.Count >= 2 || flightWords)) || (route.Success && (flightWords || times.Count >= 2)))
        {
            t.Kind = tno.Success ? Trip.Train : Trip.Flight;
            t.No = tno.Success ? tno.Value : fno?.Value ?? "";
            if (!tno.Success && airports.Count > 0) t.FromAt = airports[0];
            if (!tno.Success && airports.Count > 1) t.ToAt = airports[1];
            if (times.Count >= 2) t.EndTime = times[1];
            if (cities.Count > 0) t.From = cities[0];
            if (cities.Count > 1) t.To = cities[1];
            if (carrier.Success) t.Carrier = carrier.Value;
            t.Tidy();
            return t;
        }
        var hotel = HotelRx.Match(seg);
        // 要有“入住 / 退房 / 几晚”这类字才算酒店；订单页上一排“酒店 退改签 报销”按钮不算
        if (hotel.Success && Regex.IsMatch(seg, @"入住|离店|退房|\d\s*晚|间夜|房型|check.?in|check.?out|\d\s*nights?|menginap", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(hotel.Value, "退改|报销|客服|发票"))
        {
            t.Kind = Trip.Hotel;
            var place = Regex.Replace(hotel.Value.Trim(), @"^((入住|预订|预定|订)\s*)+", "").Trim();
            // 中文店名到“酒店 / 宾馆……”为止，后面的“2晚 豪华大床房”不算店名
            var cut = Regex.Match(place, "酒店|宾馆|饭店|民宿|公寓");
            if (cut.Success && Regex.IsMatch(place[..cut.Index], @"\p{IsCJKUnifiedIdeographs}")) place = place[..(cut.Index + cut.Length)];
            t.Place = place;
            if (NightsRx.Match(seg) is { Success: true } n && int.Parse(n.Groups[1].Value) is > 0 and < 60 and var nights) t.EndDate = TripStore.Key(day.AddDays(nights));
            if (RoomRx.Match(seg) is { Success: true } room) t.Text = room.Value.Trim();
            t.Tidy();
            return t;
        }

        // 普通日程：去掉时间、星期、零碎符号，剩下的当这件事
        var s = CnTimeRx.Replace(TimeRx.Replace(seg, " "), " ");
        s = Junk.Replace(s, " ");
        s = Regex.Replace(s, @"(?<![\p{L}])[\d\s·•.,，、:：()（）\[\]【】/|\-—~～]+(?![\p{L}])", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', ',', '，', '、', '-', '—', '~', '～', '：', ':', '|', '/', '.', '。');
        // 有意思的字（汉字、字母）太少的，多半是识字识出来的碎片，不要
        if (Regex.Matches(s, @"[\p{IsCJKUnifiedIdeographs}A-Za-z]").Count < 2) return null;
        if (s.Length > 30) s = s[..30].TrimEnd() + "…";
        t.Text = s;
        t.Tidy();
        return t;
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

// <summary>用 Windows 自带的 DPAPI 加密小段文字（只有本机当前账户解得开），存密钥用。</summary>
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
