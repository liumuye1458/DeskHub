using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskHub.Core;

/// <summary>一个事项：名称、颜色、关键词。</summary>
public sealed class TopicRule
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4a5568";
    /// <summary>预设关键词（文件名包含即命中，不区分大小写）。</summary>
    public List<string> Keywords { get; set; } = new();
    /// <summary>用户拖动文件时学到的关键词。</summary>
    public List<string> Learned { get; set; } = new();
}

/// <summary>用户自己建的分区。</summary>
public sealed class CustomFence
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>分区在屏幕上的位置（相对主屏工作区左上角，单位：设备无关像素）。</summary>
public sealed class FenceLayout
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
}

public sealed class AppConfig
{
    public const string OtherTopic = "其他";

    public int ColdDays { get; set; } = 7;
    /// <summary>密码箱对外接口（本机其他程序经你确认后取账号密码）。默认关。</summary>
    public bool VaultApi { get; set; }
    /// <summary>界面语言：空 = 跟随 Windows；zh / en / id / th / vi / es / ja。改了重启生效。</summary>
    public string Language { get; set; } = "";

    /// <summary>启动时先读出语言（要在生成默认事项名之前定好语言）。</summary>
    public static string PeekLanguage()
    {
        try
        {
            if (!File.Exists(FilePath)) return "";
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(FilePath));
            return doc.RootElement.TryGetProperty("Language", out var v) ? v.GetString() ?? "" : "";
        }
        catch { return ""; }
    }
    /// <summary>分区之间的缝宽（「排列 → 对齐」时按你的摆法学，拖动吸附也用它）。</summary>
    public double ArrangeGap { get; set; } = 8;
    public int ArchiveDays { get; set; } = 30;
    /// <summary>归档根目录；实际路径 = 根目录\yyyy-MM\事项\</summary>
    public string ArchiveRoot { get; set; } = @"D:\桌面归档";
    /// <summary>首次自动归档前，必须让用户确认一次清单。</summary>
    public bool ArchiveConfirmed { get; set; }
    public bool AutoArchive { get; set; } = true;
    public bool HideSystemIcons { get; set; } = true;
    public bool ShowSeconds { get; set; }

    public List<TopicRule> Topics { get; set; } = DefaultTopics();
    public Dictionary<string, FenceLayout> Layout { get; set; } = new();
    public List<CustomFence> CustomFences { get; set; } = new();
    /// <summary>快捷键：shot / search / calc → "Ctrl+Alt+A" / "Ctrl Ctrl" / ""(不设)。没写的用默认值。</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    public static List<TopicRule> DefaultTopics() => new()
    {
        new() { Name = L.T("报销财务"), Color = "#f04b4b",
            Keywords = { "报销", "发票", "核销", "流水", "账单", "工行", "招行", "付款", "收据", "对账",
                         "invoice", "receipt", "kwitansi", "faktur", "tagihan", "pembayaran", "reimburse" } },
        new() { Name = L.T("直播运营"), Color = "#0095fc",
            Keywords = { "tiktok", "周报", "直播", "主播", "mca", "钻石", "排期", "赛事", "复盘", "streamer",
                         "live", "pk", "marathon", "coins", "mc岗位", "host" } },
        new() { Name = L.T("人事招募"), Color = "#ffb800",
            Keywords = { "招募", "简历", "jd", "薪资", "面试", "候选人", "岗位", "入职", "hr", "cv",
                         "resume", "portofolio", "portfolio", "gaji", "rekrut", "kandidat", "化妆师" } },
        new() { Name = L.T("合同法务"), Color = "#9b59b6",
            Keywords = { "合同", "协议", "盖章", "签署", "规章", "制度", "mou", "kontrak", "perjanjian",
                         "agreement", "contract", "peraturan", "denda", "pengumuman", "proposal", "政策" } },
        new() { Name = L.T("技术项目"), Color = "#4a5568",
            Keywords = { "源码", "日志", "改动清单", "方案", "nas", ".md", ".html", ".json", ".log", ".py", "截图分析" } },
    };

    // ---------- 读写 ----------
    public static string DataDir { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskHub");

    public static void UseDataDir(string dir) => DataDir = dir;

    static string FilePath => Path.Combine(DataDir, "config.json");

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Json) ?? new AppConfig();
        }
        catch { /* 配置损坏就用默认值，不阻止启动 */ }
        return new AppConfig();
    }

    public void Save() => AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));

    public TopicRule? FindTopic(string name) => Topics.FirstOrDefault(t => t.Name == name);
}

static class AtomicFile
{
    /// <summary>先写临时文件再替换，断电或崩溃时不会留下半截文件。</summary>
    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }
}
