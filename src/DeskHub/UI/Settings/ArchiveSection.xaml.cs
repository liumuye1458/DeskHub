using System.IO;
using System.Windows.Controls;
using DeskHub.Core;

namespace DeskHub.UI.Settings;

/// <summary>归档位置、到期自动归档、立即检查（编译开关 FeatArchive）。</summary>
public partial class ArchiveSection : UserControl, ISettingsPart
{
    public ArchiveSection(DeskController c)
    {
        InitializeComponent();
        ArchiveRoot.Text = c.Cfg.ArchiveRoot;
        AutoArchive.IsChecked = c.Cfg.AutoArchive;
        ArchiveRoot.TextChanged += (_, _) => UpdateExample();
        UpdateExample();
        Browse.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = L.T("选择归档位置"), UseDescriptionForTitle = true, SelectedPath = ArchiveRoot.Text };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) ArchiveRoot.Text = dlg.SelectedPath;
        };
        CheckNow.Click += (_, _) => c.CheckArchive(userAsked: true);
    }

    void UpdateExample() =>
        ArchiveExample.Text = L.F("例：{0}", Path.Combine(ArchiveRoot.Text.Trim(), DateTime.Now.ToString("yyyy-MM"), L.T("报销财务"), "发票_xxx.pdf"));

    public string? Check()
    {
        var root = ArchiveRoot.Text.Trim();
        return root.Length < 3 || !Path.IsPathRooted(root) ? L.T("归档位置要写完整路径，例如 D:\\桌面归档") : null;
    }

    public void Store(AppConfig cfg)
    {
        cfg.ArchiveRoot = ArchiveRoot.Text.Trim();
        cfg.AutoArchive = AutoArchive.IsChecked == true;
    }
}
