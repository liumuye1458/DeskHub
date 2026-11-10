using System.Windows;
using System.Windows.Media;
using DeskHub.Core;

namespace DeskHub.UI.Settings;

/// <summary>
/// 设置窗口里的一页或一段。每个可选功能的设置单独一个文件，功能关掉（Edition.props）就整个不编译。
/// 点“保存”时：先所有的 Check，都没问题再所有的 Store，存盘，然后按顺序 Applied。
/// </summary>
public interface ISettingsPart
{
    /// <summary>检查填的对不对；不对返回要提示的话（窗口会切到这一页）。</summary>
    string? Check() => null;
    /// <summary>写进配置（还没存盘）。</summary>
    void Store(AppConfig cfg) { }
    /// <summary>存盘以后让它生效；返回要提示的话就不关窗口。</summary>
    string? Applied() => null;
    /// <summary>切到这一页时。</summary>
    void Shown() { }
    /// <summary>设置窗口关掉时（不管保没保存）。</summary>
    void Closed() { }
}

static class Ui
{
    public static T Res<T>(string key) => (T)Application.Current.FindResource(key);
    public static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    public static readonly SolidColorBrush Ok = new(Color.FromRgb(0x07, 0xA5, 0x48));
}
