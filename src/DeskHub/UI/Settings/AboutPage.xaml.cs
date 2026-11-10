using System.Diagnostics;
using System.Windows.Controls;
using DeskHub.Core;

namespace DeskHub.UI.Settings;

/// <summary>关于：名字、版本、隐私说明（按本版本有的功能说）、数据位置。</summary>
public partial class AboutPage : UserControl, ISettingsPart
{
    public AboutPage(bool sandbox)
    {
        InitializeComponent();
        AboutName.Text = Brand.Name;
        VersionText.Text = L.F("版本 {0}{1}", typeof(App).Assembly.GetName().Version?.ToString(3), (sandbox ? L.T("（沙盒模式）") : ""));
        Privacy.Text =
#if FEAT_TRIPAI && FEAT_LOCALCLAUDE
            L.T("纯本机工具，不上报；只有你配了 AI 服务并点“识别”时，才会把那次的内容发给它。打开“本机 Claude 服务”后，只在本机 127.0.0.1 上等自家程序来问。");
#elif FEAT_TRIPAI
            L.T("纯本机工具，不上报、没有后台服务；只有你配了 AI 服务并点“识别”时，才会把那次的内容发给它。");
#elif FEAT_LOCALCLAUDE
            L.T("纯本机工具，不上报。打开“本机 Claude 服务”后，只在本机 127.0.0.1 上等自家程序来问。");
#else
            L.T("纯本机工具，不联网、不上报、没有后台服务。");
#endif
        DataDirRun.Text = AppConfig.DataDir;
        OpenData.Click += (_, _) => Process.Start("explorer.exe", AppConfig.DataDir);
    }
}
