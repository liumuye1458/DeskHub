using System.Windows.Controls;
using System.Windows.Media;
using DeskHub.Core;

namespace DeskHub.UI.Settings;

/// <summary>智能识别日程用的 AI 服务（编译开关 FeatTripAi）。</summary>
public partial class TripAiSection : UserControl, ISettingsPart
{
    /// <summary>常用服务：选了就把地址填上（模型点“测试连接”读出来再选）。</summary>
    static readonly (string Name, string Url)[] AiPresets =
    {
        ("自己填（公司一体机等）", ""),
        ("OpenAI", "https://api.openai.com/v1"),
        ("Claude（Anthropic）", "https://api.anthropic.com/v1"),
        ("DeepSeek", "https://api.deepseek.com/v1"),
        ("通义千问（阿里云百炼）", "https://dashscope.aliyuncs.com/compatible-mode/v1"),
        ("Kimi（月之暗面）", "https://api.moonshot.cn/v1"),
        ("本机 Ollama", "http://localhost:11434/v1"),
    };
    CancellationTokenSource? _aiCts;

    public TripAiSection(AppConfig cfg)
    {
        InitializeComponent();
        foreach (var p in AiPresets) AiPreset.Items.Add(new ComboBoxItem { Content = L.T(p.Name), Tag = p.Url });
        AiUrl.Text = cfg.AiUrl;
        AiModel.Text = cfg.AiModel;
        AiKey.Password = Secret.Unprotect(cfg.AiKey);
        int Match() { for (int i = 1; i < AiPresets.Length; i++) if (string.Equals(AiPresets[i].Url, AiUrl.Text.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return i; return 0; }
        AiPreset.SelectedIndex = Match();
        bool syncing = false;
        AiPreset.SelectionChanged += (_, _) =>
        {
            if (syncing || AiPreset.SelectedItem is not ComboBoxItem { Tag: string url }) return;
            syncing = true;
            if (url.Length > 0 && !string.Equals(AiUrl.Text.Trim(), url, StringComparison.OrdinalIgnoreCase)) { AiUrl.Text = url; AiModel.ItemsSource = null; AiModel.Text = ""; }
            else if (url.Length == 0 && Match() > 0) AiUrl.Text = "";
            syncing = false;
            AiUrl.Focus();
        };
        AiUrl.TextChanged += (_, _) => { if (syncing) return; syncing = true; AiPreset.SelectedIndex = Match(); syncing = false; };
        AiTest.Click += async (_, _) =>
        {
            if (AiUrl.Text.Trim().Length == 0) { AiTestHint.Text = L.T("先填地址"); AiTestHint.Foreground = Brushes.IndianRed; return; }
            _aiCts?.Cancel();
            var cts = _aiCts = new CancellationTokenSource();
            AiTest.IsEnabled = false;
            AiTestHint.Foreground = Brushes.Gray;
            AiTestHint.Text = L.T("正在连接……");
            try
            {
                var models = await TripAi.ListModels(AiUrl.Text, AiKey.Password.Trim(), cts.Token);
                var keep = AiModel.Text;
                AiModel.ItemsSource = models;
                AiModel.Text = keep.Length > 0 ? keep : models.FirstOrDefault() ?? "";
                AiTestHint.Foreground = Ui.Ok;
                AiTestHint.Text = models.Count > 0 ? L.F("连上了，有 {0} 个模型，在左边下拉里选一个。", models.Count) : L.T("连上了，但没读到模型列表，手动填模型名就行。");
                if (models.Count > 0 && keep.Length == 0) AiModel.IsDropDownOpen = true;
            }
            catch (TripAi.AiException ex) { AiTestHint.Foreground = Brushes.IndianRed; AiTestHint.Text = ex.Message; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AiTestHint.Foreground = Brushes.IndianRed; AiTestHint.Text = ex.Message; }
            finally { AiTest.IsEnabled = true; }
        };
    }

    /// <summary>本机 Claude 那一段点了“日历识别改用本机 Claude”：把地址、模型、密钥填上。</summary>
    public void Fill(string url, IReadOnlyList<string> models, string key)
    {
        AiUrl.Text = url;
        AiModel.ItemsSource = models;
        AiModel.Text = models.FirstOrDefault() ?? "";
        AiKey.Password = key;
    }

    public void Closed() => _aiCts?.Cancel();

    public string? Check()
    {
        if (AiUrl.Text.Trim().Length > 0 && !Uri.TryCreate(AiUrl.Text.Trim(), UriKind.Absolute, out _)) return L.T("AI 服务地址要以 http:// 或 https:// 开头");
        if (AiUrl.Text.Trim().Length > 0 && AiModel.Text.Trim().Length == 0) return L.T("填了 AI 服务地址，还要填模型名（点“测试连接”可以读出来）");
        return null;
    }

    public void Store(AppConfig cfg)
    {
        cfg.AiUrl = AiUrl.Text.Trim();
        cfg.AiModel = AiModel.Text.Trim();
        cfg.AiKey = Secret.Protect(AiKey.Password.Trim());
    }
}
