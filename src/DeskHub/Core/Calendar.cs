using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace DeskHub.Core;

/// <summary>农历、节气、传统节日。农历用系统自带的 ChineseLunisolarCalendar（1901–2100），节气按太阳黄经算。</summary>
public static class ChinaCal
{
    static readonly ChineseLunisolarCalendar Cal = new();
    static readonly string[] Months = { "", "正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "十一月", "腊月" };
    static readonly string[] Days =
    {
        "", "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十", "十一", "十二", "十三", "十四", "十五",
        "十六", "十七", "十八", "十九", "二十", "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十",
    };

    public readonly record struct Lunar(int Month, bool Leap, int Day)
    {
        public string MonthName => (Leap ? "闰" : "") + Months[Month];
        public string DayName => Days[Day];
        public string Full => MonthName + DayName;
        /// <summary>格子里写的：初一写月份，其余写日子。</summary>
        public string Cell => Day == 1 ? MonthName : DayName;
    }

    public static bool InRange(DateTime d) => d >= Cal.MinSupportedDateTime && d < Cal.MaxSupportedDateTime.AddDays(-1);

    public static Lunar Of(DateTime d)
    {
        int y = Cal.GetYear(d), m = Cal.GetMonth(d), day = Cal.GetDayOfMonth(d);
        int leap = Cal.GetLeapMonth(y); // 有闰月的年份一年 13 个月：闰月排在它那个月后面
        bool isLeap = leap > 0 && m == leap;
        if (leap > 0 && m >= leap) m--;
        return new Lunar(m, isLeap, day);
    }

    /// <summary>这天是不是传统节日或节气；都不是返回 null。</summary>
    public static string? Festival(DateTime d)
    {
        if (!InRange(d)) return null;
        var l = Of(d);
        if (!l.Leap)
        {
            var f = (l.Month, l.Day) switch
            {
                (1, 1) => "春节", (1, 15) => "元宵", (5, 5) => "端午", (7, 7) => "七夕", (8, 15) => "中秋", (9, 9) => "重阳", (12, 8) => "腊八",
                _ => null,
            };
            if (f != null) return f;
            if (l.Month == 12 && Of(d.AddDays(1)) is { Month: 1, Day: 1, Leap: false }) return "除夕";
        }
        return Term(d);
    }

    // ---------------- 节气 ----------------
    static readonly string[] TermNames =
    {
        "春分", "清明", "谷雨", "立夏", "小满", "芒种", "夏至", "小暑", "大暑", "立秋", "处暑", "白露",
        "秋分", "寒露", "霜降", "立冬", "小雪", "大雪", "冬至", "小寒", "大寒", "立春", "雨水", "惊蛰",
    };
    static readonly Dictionary<int, Dictionary<DateTime, string>> _terms = new();

    public static string? Term(DateTime d) => TermsOf(d.Year).TryGetValue(d.Date, out var t) ? t : null;

    /// <summary>一年的 24 个节气落在哪天（按北京时间）。太阳视黄经每过 15° 一个节气。</summary>
    public static Dictionary<DateTime, string> TermsOf(int year)
    {
        if (_terms.TryGetValue(year, out var map)) return map;
        map = new();
        var day = new DateTime(year, 1, 1);
        double prev = SunLongitude(day.AddHours(-8));
        for (; day.Year == year; day = day.AddDays(1))
        {
            double next = SunLongitude(day.AddDays(1).AddHours(-8)); // 北京时间这一天结束时
            int a = (int)(prev / 15), b = (int)(next / 15);
            if (a != b) map[day] = TermNames[b % 24];
            prev = next;
        }
        return _terms[year] = map;
    }

    /// <summary>太阳视黄经（度，0–360），Meeus 低精度公式，误差约 0.01°（十几分钟）。utc 是世界时。</summary>
    static double SunLongitude(DateTime utc)
    {
        double jd = utc.ToOADate() + 2415018.5 + 69.0 / 86400; // 加 ΔT 换成力学时
        double t = (jd - 2451545.0) / 36525;
        double l0 = 280.46646 + 36000.76983 * t + 0.0003032 * t * t;
        double m = Rad(357.52911 + 35999.05029 * t - 0.0001537 * t * t);
        double c = (1.914602 - 0.004817 * t - 0.000014 * t * t) * Math.Sin(m) + (0.019993 - 0.000101 * t) * Math.Sin(2 * m) + 0.000289 * Math.Sin(3 * m);
        double omega = Rad(125.04 - 1934.136 * t);
        double lambda = l0 + c - 0.00569 - 0.00478 * Math.Sin(omega);
        return ((lambda % 360) + 360) % 360;
    }

    static double Rad(double deg) => deg * Math.PI / 180;
}

/// <summary>
/// 中国放假 / 调休上班、印尼红日子。数据每年由政府公布一次，这里内置，出新版本时补下一年。
/// 名字用中文写，显示时经 L.T 翻译。
/// </summary>
public static class Holidays
{
    public readonly record struct Mark(bool Off, string Name);

    static readonly Dictionary<DateTime, Mark> Cn = new(), Id = new();

    static Holidays()
    {
        // 国务院办公厅关于 2026 年部分节假日安排的通知（2025-11-04）
        CnOff("2026-01-01", 3, "元旦"); CnOff("2026-02-15", 9, "春节"); CnOff("2026-04-04", 3, "清明节"); CnOff("2026-05-01", 5, "劳动节");
        CnOff("2026-06-19", 3, "端午节"); CnOff("2026-09-25", 3, "中秋节"); CnOff("2026-10-01", 7, "国庆节");
        foreach (var d in new[] { "2026-01-04", "2026-02-14", "2026-02-28", "2026-05-09", "2026-09-20", "2026-10-10" }) Cn[D(d)] = new(false, "调休上班");

        // 印尼 SKB 3 Menteri（setneg.go.id）：L = 法定假日 libur nasional，C = 联合休假 cuti bersama
        const string id2026 =
            "2026-01-01 L 元旦|2026-01-16 L 登霄节|2026-02-16 C 春节|2026-02-17 L 春节|2026-03-18 C 静居日|2026-03-19 L 静居日|" +
            "2026-03-20 C 开斋节|2026-03-21 L 开斋节|2026-03-22 L 开斋节|2026-03-23 C 开斋节|2026-03-24 C 开斋节|2026-04-03 L 耶稣受难日|" +
            "2026-04-05 L 复活节|2026-05-01 L 劳动节|2026-05-14 L 耶稣升天节|2026-05-15 C 耶稣升天节|2026-05-27 L 宰牲节|2026-05-28 C 宰牲节|" +
            "2026-05-31 L 卫塞节|2026-06-01 L 建国五原则日|2026-06-16 L 伊斯兰新年|2026-08-17 L 独立日|2026-08-25 L 圣纪节|" +
            "2026-12-24 C 圣诞节|2026-12-25 L 圣诞节";
        const string id2027 =
            "2027-01-01 L 元旦|2027-01-05 L 登霄节|2027-02-05 C 春节|2027-02-06 L 春节|2027-03-08 L 静居日|2027-03-09 C 开斋节|" +
            "2027-03-10 L 开斋节|2027-03-11 L 开斋节|2027-03-12 C 开斋节|2027-03-15 C 开斋节|2027-03-25 C 耶稣受难日|2027-03-26 L 耶稣受难日|" +
            "2027-03-28 L 复活节|2027-05-01 L 劳动节|2027-05-06 L 耶稣升天节|2027-05-17 L 宰牲节|2027-05-18 C 宰牲节|2027-05-19 C 卫塞节|" +
            "2027-05-20 L 卫塞节|2027-06-01 L 建国五原则日|2027-06-06 L 伊斯兰新年|2027-08-15 L 圣纪节|2027-08-17 L 独立日|" +
            "2027-12-24 C 圣诞节|2027-12-25 L 圣诞节|2027-12-26 L 登霄节";
        foreach (var e in (id2026 + "|" + id2027).Split('|'))
        {
            var p = e.Split(' ');
            Id[D(p[0])] = new(p[1] == "L", p[2]);
        }
    }

    static DateTime D(string s) => DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    static void CnOff(string from, int days, string name) { for (int i = 0; i < days; i++) Cn[D(from).AddDays(i)] = new(true, name); }

    /// <summary>中国：Off = 放假，否则是调休上班。</summary>
    public static Mark? China(DateTime d) => Cn.TryGetValue(d.Date, out var m) ? m : null;
    /// <summary>印尼：Off = 法定假日（libur nasional），否则是联合休假（cuti bersama）。</summary>
    public static Mark? Indonesia(DateTime d) => Id.TryGetValue(d.Date, out var m) ? m : null;

    public static string ChinaYears => Years(Cn.Keys);
    public static string IndonesiaYears => Years(Id.Keys);
    static string Years(IEnumerable<DateTime> ds)
    {
        var ys = ds.Select(d => d.Year).Distinct().Order().ToList();
        return ys.Count == 0 ? "" : ys.Count == 1 ? ys[0].ToString() : $"{ys[0]}–{ys[^1]}";
    }
}

//// <summary>
/// 一条行程。机票、火车、酒店的要素是固定的，分字段存，显示时按固定模板排（一眼看清从哪到哪、几点）；其他事就是一句话。
/// 一天可以有好几条；酒店、跨天的航班有结束那天，日历上连成一段。存在 trips.json。
/// </summary>
public sealed class Trip
{
    public const string Flight = "flight", Train = "train", Hotel = "hotel";

    public string Id { get; set; } = "";
    /// <summary>"" 普通日程 / flight / train / hotel。</summary>
    public string Kind { get; set; } = "";
    /// <summary>yyyy-MM-dd：出发 / 入住 / 这件事那天。</summary>
    public string Date { get; set; } = "";
    /// <summary>HH:mm，没有就空着。</summary>
    public string Time { get; set; } = "";
    /// <summary>到达 / 退房那天（和 Date 同一天就空着）。</summary>
    public string EndDate { get; set; } = "";
    /// <summary>到达 / 退房时间。</summary>
    public string EndTime { get; set; } = "";
    /// <summary>出发、到达城市（短名：雅加达、新加坡）。</summary>
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>出发、到达的机场 / 车站 / 航站楼（苏加诺-哈达 T3）。</summary>
    public string FromAt { get; set; } = "";
    public string ToAt { get; set; } = "";
    /// <summary>航班号 / 车次。</summary>
    public string No { get; set; } = "";
    /// <summary>航空公司 / 铁路公司。</summary>
    public string Carrier { get; set; } = "";
    /// <summary>酒店名 / 地点。</summary>
    public string Place { get; set; } = "";
    /// <summary>普通日程：这件事；其他：补充一句（座位、订单备注）。</summary>
    public string Text { get; set; } = "";
    public bool Remind { get; set; } = true;
    /// <summary>提醒便签已经弹过了。</summary>
    public bool Reminded { get; set; }

    public DateTime Day => Parse(Date) ?? DateTime.Today;
    /// <summary>最后一天（没有结束日就是当天）。</summary>
    public DateTime LastDay => Parse(EndDate) is { } e && e > Day ? e : Day;
    public bool IsTransport => Kind is Flight or Train;
    public bool Covers(DateTime d) => d >= Day && d <= LastDay;

    static DateTime? Parse(string s) => DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public Trip Clone() => (Trip)MemberwiseClone();

    /// <summary>图标：✈ 🚄 🏨 •</summary>
    public string Icon => Kind switch { Flight => "✈", Train => "🚄", Hotel => "🏨", _ => "•" };

    /// <summary>短标题：雅加达 → 新加坡 / 酒店名 / 这件事。</summary>
    public string Title => Kind switch
    {
        Flight or Train when From.Length > 0 || To.Length > 0 => $"{Or(From)} → {Or(To)}",
        Flight or Train => No.Length > 0 ? No : Text,
        Hotel => Place.Length > 0 ? Place : Text,
        _ => Text,
    };
    static string Or(string s) => s.Length > 0 ? s : "?";

    /// <summary>住几晚（酒店）。</summary>
    public int Nights => Math.Max(0, (LastDay - Day).Days);

    /// <summary>日历格子里写的几个字：飞去哪、住哪、什么事。</summary>
    public string Chip(DateTime d) => Kind switch
    {
        Flight or Train => To.Length > 0 ? To : Title,
        Hotel => Title,   // 色条从入住连到退房，不用再写“入住”
        _ => Text,
    };

    /// <summary>一行写完（提醒便签、悬停提示、编辑框的列表）。</summary>
    public string Display => Kind switch
    {
        Flight or Train => Join(Time, Title, No, EndTime.Length > 0 ? L.F("{0} 到", EndTime + PlusDays) : ""),
        Hotel => Join(L.T("入住"), Title, Time, Nights > 0 ? L.F("住 {0} 晚", Nights) : ""),
        _ => Join(Time, Text),
    };
    /// <summary>某一天看到的那一行：酒店退房那天写“退房 xx”，其余同 Display。</summary>
    public string DisplayOn(DateTime d) => Kind == Hotel && Nights > 0 && d == LastDay ? Join(L.T("退房"), Title, EndTime) : Display;
    static string Join(params string[] parts) => string.Join(" ", parts.Where(p => p.Length > 0));

    /// <summary>到达比出发晚几天：“+1”。</summary>
    public string PlusDays => LastDay > Day && IsTransport ? $"+{(LastDay - Day).Days}" : "";

    /// <summary>空的（什么都没填）。</summary>
    public bool IsEmpty => (Kind switch { Flight or Train => From + To + No, Hotel => Place, _ => Text }).Trim().Length == 0 && Text.Trim().Length == 0;

    /// <summary>判断是不是同一条（识别重复加进来时去重）。</summary>
    public string Sig => $"{Kind}|{Date}|{Time}|{No}|{From}|{To}|{Place}|{Text}";

    static readonly Regex LeadTime = new(@"^\s*([01]?\d|2[0-3])\s*[:：]\s*([0-5]\d)\b\s*", RegexOptions.Compiled);
    static readonly Regex AnyTime = new(@"(?<!\d)([01]?\d|2[0-3])\s*[:：.]\s*([0-5]\d)(?!\d)", RegexOptions.Compiled);

    /// <summary>“7:45”“07:45 PM”“19.30” → “07:45”“19:45”“19:30”；认不出就空。</summary>
    public static string NormTime(string s)
    {
        var m = AnyTime.Match(s);
        if (!m.Success) return "";
        int h = int.Parse(m.Groups[1].Value);
        if (h < 12 && Regex.IsMatch(s, "pm|下午|晚上", RegexOptions.IgnoreCase)) h += 12;
        return $"{h:00}:{m.Groups[2].Value}";
    }

    /// <summary>把“08:30 和客户吃饭”拆成时间和事情（开头没写时间就只有事情）。</summary>
    public static (string Time, string Text) Split(string s)
    {
        s = s.Trim();
        var m = LeadTime.Match(s);
        if (!m.Success || m.Length == s.Length) return ("", s);
        return ($"{int.Parse(m.Groups[1].Value):00}:{m.Groups[2].Value}", s[m.Length..].Trim());
    }

    /// <summary>整理一下：时间统一成 HH:mm，结束日不早于开始日、和开始同一天就清掉。</summary>
    public void Tidy()
    {
        if (Kind is not (Flight or Train or Hotel) && Time.Trim().Length == 0) (Time, Text) = Split(Text); // “15:00 和客户吃饭”
        Time = NormTime(Time); EndTime = NormTime(EndTime);
        foreach (var p in new[] { nameof(From), nameof(To), nameof(FromAt), nameof(ToAt), nameof(No), nameof(Carrier), nameof(Place), nameof(Text) })
        {
            var pi = typeof(Trip).GetProperty(p)!;
            pi.SetValue(this, ((string?)pi.GetValue(this) ?? "").Trim());
        }
        No = No.Replace(" ", "").ToUpperInvariant();
        if (Kind is not (Flight or Train or Hotel)) Kind = "";
        if (Parse(EndDate) is not { } e || e <= Day) EndDate = "";
        // 机票、火车：到达时间比出发早又没写到达日，多半是第二天到
        if (IsTransport && EndDate.Length == 0 && Time.Length > 0 && EndTime.Length > 0 && string.CompareOrdinal(EndTime, Time) < 0)
            EndDate = TripStore.Key(Day.AddDays(1));
    }
}

public static class TripStore
{
    public static List<Trip> Trips { get; private set; } = new();
    public static event Action? Changed;
    static string FilePath => Path.Combine(AppConfig.DataDir, "trips.json");

    public static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public static void Load()
    {
        try { Trips = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Trip>>(File.ReadAllText(FilePath), AppConfig.Json) ?? new() : new(); }
        catch { Trips = new(); }
        // 老版本一天一条、时间写在文字里：补上 Id，把开头的时间拆出来
        foreach (var t in Trips)
        {
            if (string.IsNullOrEmpty(t.Id)) t.Id = NewId();
            if (t.Kind.Length == 0 && t.Time.Length == 0) (t.Time, t.Text) = Trip.Split(t.Text);
        }
        // 0.12.0 的本机识别会把订单页上“酒店 退改签 报销”那排按钮认成一家酒店：清掉
        Trips.RemoveAll(t => t.Kind == Trip.Hotel && Regex.IsMatch(t.Place, "退改|报销|客服|发票"));
        Trips.Sort(Order);
    }

    /// <summary>按出行时间排：先日子，再时间（没写时间的排在那天最前面）。</summary>
    public static int Order(Trip a, Trip b)
    {
        int c = string.CompareOrdinal(a.Date, b.Date);
        return c != 0 ? c : string.CompareOrdinal(a.Time, b.Time);
    }

    static void Save()
    {
        Trips.Sort(Order);
        try { AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(Trips, AppConfig.Json)); } catch { }
        Changed?.Invoke();
    }

    /// <summary>这一天有的行程（含跨到这天的酒店、隔天到的航班），按时间排好。</summary>
    public static List<Trip> On(DateTime d) => Trips.Where(t => t.Covers(d)).ToList();
    public static bool Any(DateTime d) => Trips.Any(t => t.Covers(d));

    /// <summary>
    /// 存一条：edited 是改好的内容；original 是原来那条（新记的为 null）。内容全空就是删掉。
    /// 改了日子 / 时间 / 内容或打开提醒，提醒会重新弹（如果还没到日子）。
    /// </summary>
    public static Trip? Put(Trip edited, Trip? original)
    {
        edited.Tidy();
        if (edited.IsEmpty) { if (original != null) Remove(original); return null; }
        var t = original != null && Trips.Contains(original) ? original : null;
        if (t == null) { Trips.Add(t = edited); t.Id = NewId(); }
        else
        {
            bool changed = t.Sig != edited.Sig || (edited.Remind && !t.Remind);
            var id = t.Id;
            foreach (var p in typeof(Trip).GetProperties().Where(p => p.CanWrite)) p.SetValue(t, p.GetValue(edited));
            t.Id = id;
            if (changed) t.Reminded = false;
        }
        Save();
        return t;
    }

    /// <summary>一次加好几条（智能识别出来的）；和已有的一模一样的不重复加。返回加了几条。</summary>
    public static int AddMany(IEnumerable<Trip> items)
    {
        int n = 0;
        foreach (var it in items)
        {
            it.Tidy();
            if (it.IsEmpty || Trips.Any(t => t.Sig == it.Sig)) continue;
            it.Id = NewId(); it.Reminded = false;
            Trips.Add(it);
            n++;
        }
        if (n > 0) Save();
        return n;
    }

    public static void Remove(Trip t) { if (Trips.Remove(t)) Save(); }

    /// <summary>还没结束的（今天及以后，含正住着的酒店）。</summary>
    public static IEnumerable<Trip> Upcoming(DateTime today) => Trips.Where(t => t.LastDay >= today);

    /// <summary>
    /// 到了行程前一天（或者行程当天还没提醒过，比如前一天没开电脑）：在桌面弹一张便签，点 OK 就收起。
    /// 返回弹了几张。
    /// </summary>
    public static int CheckReminders(DateTime now, Rect workArea)
    {
        var today = now.Date;
        var due = Trips.Where(t => t.Remind && !t.Reminded && (t.Day == today.AddDays(1) || t.Day == today)).ToList();
        if (due.Count == 0) return 0;
        int i = 0;
        foreach (var t in due)
        {
            var head = t.Day == today ? L.F("今天 · {0}", CalText.Short(t.Day)) : L.F("明天 · {0}", CalText.Short(t.Day));
            GuideStore.D.Notes.Add(new GuideNote
            {
                Text = head + "\n" + t.Icon + " " + t.Display, Color = "blue", Arrow = false, Daily = false, Topmost = false,
                X = workArea.Right - 330 - i * 24, Y = workArea.Top + 120 + i * 24, W = 280,
            });
            t.Reminded = true;
            i++;
        }
        Save();
        GuideStore.Save();
        return i;
    }
}

// <summary>日历里的日期写法：中文用“12月24日 周四”，其他语言用该语言的习惯写法。</summary>
public static class CalText
{
    static readonly string[] ZhWeek = { "日", "一", "二", "三", "四", "五", "六" };

    public static string WeekShort(DayOfWeek w) => L.IsZh ? ZhWeek[(int)w] : L.Culture.DateTimeFormat.GetAbbreviatedDayName(w);
    public static string Short(DateTime d) => L.IsZh ? $"{d.Month}月{d.Day}日 周{ZhWeek[(int)d.DayOfWeek]}" : d.ToString("ddd d MMM", L.Culture);
    public static string MonthDay(DateTime d) => L.IsZh ? $"{d.Month}月{d.Day}日" : d.ToString("d MMM", L.Culture);
    public static string Weekday(DateTime d) => L.IsZh ? "周" + ZhWeek[(int)d.DayOfWeek] : L.Culture.DateTimeFormat.GetAbbreviatedDayName(d.DayOfWeek);
    public static string Long(DateTime d) => L.IsZh ? $"{d.Month}月{d.Day}日 星期{ZhWeek[(int)d.DayOfWeek]}" : d.ToString("dddd, d MMMM", L.Culture);
    public static string Month(DateTime d) => L.IsZh ? $"{d.Year}年{d.Month}月" : d.ToString("MMMM yyyy", L.Culture);
}
