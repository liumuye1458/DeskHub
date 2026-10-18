using System.IO;

namespace DeskHub.Core;

/// <summary>Custom = 用户自己建的分区（具体是哪个看 DesktopItem.Fence）。</summary>
public enum Zone { Apps, Folders, Today, Active, Cold, Custom }

public enum Kind { Doc, Sheet, Slide, Pdf, Image, Archive, Video, Audio, Program, Other }

/// <summary>桌面上的一个文件 / 文件夹 / 快捷方式，以及算好的分区信息。</summary>
public sealed class DesktopItem
{
    public required string Path { get; init; }
    public required bool IsDir { get; init; }
    /// <summary>是否在"公用桌面"（C:\Users\Public\Desktop），这里的文件不归档。</summary>
    public required bool IsPublic { get; init; }
    public DateTime LastWrite { get; init; }
    public DateTime Created { get; init; }
    public long Size { get; init; }

    public string Name => System.IO.Path.GetFileName(Path);
    public string Ext => IsDir ? "" : System.IO.Path.GetExtension(Path).ToLowerInvariant();

    /// <summary>显示名：快捷方式和网址不显示扩展名（和资源管理器一致）。</summary>
    public string DisplayName => IsShortcut ? System.IO.Path.GetFileNameWithoutExtension(Path) : Name;

    public bool IsShortcut => !IsDir && Ext is ".lnk" or ".url" or ".appref-ms" or ".website";

    // ---- 由 Classifier 填写 ----
    public Zone Zone { get; set; }
    /// <summary>放在哪个自定义分区（Zone = Custom 时有值）。</summary>
    public string? Fence { get; set; }
    public string Topic { get; set; } = AppConfig.OtherTopic;
    public DateTime FirstSeen { get; set; }
    public DateTime LastTouch { get; set; }
    public bool Pinned { get; set; }
    /// <summary>距离归档还剩几天（只对冷却区有意义；≤0 表示已到期）。</summary>
    public int DaysLeft { get; set; }
    public Kind Kind => KindOf(Ext, IsDir);

    public static Kind KindOf(string ext, bool dir) => dir ? Kind.Other : ext switch
    {
        ".doc" or ".docx" or ".dot" or ".dotx" or ".rtf" or ".odt" or ".wps" or ".txt" or ".md" or ".html" or ".htm" or ".mht" => Kind.Doc,
        ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".csv" or ".ods" or ".et" => Kind.Sheet,
        ".ppt" or ".pptx" or ".pps" or ".ppsx" or ".odp" or ".dps" => Kind.Slide,
        ".pdf" or ".xps" or ".ofd" => Kind.Pdf,
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".heic" or ".tif" or ".tiff" or ".psd" or ".ico" or ".svg" or ".raw" or ".cr2" => Kind.Image,
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".iso" or ".cab" => Kind.Archive,
        ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".flv" or ".webm" or ".m4v" or ".3gp" => Kind.Video,
        ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".wma" => Kind.Audio,
        ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".appx" or ".msix" => Kind.Program,
        _ => Kind.Other,
    };

    public static DesktopItem? From(string path, bool isPublic)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Attributes.HasFlag(FileAttributes.Directory))
            {
                var di = new DirectoryInfo(path);
                return new DesktopItem { Path = path, IsDir = true, IsPublic = isPublic, LastWrite = di.LastWriteTime, Created = di.CreationTime };
            }
            if (!fi.Exists) return null;
            return new DesktopItem { Path = path, IsDir = false, IsPublic = isPublic, LastWrite = fi.LastWriteTime, Created = fi.CreationTime, Size = fi.Length };
        }
        catch { return null; }
    }
}
