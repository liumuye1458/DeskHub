using System.IO;

using DeskHub.Core;

namespace DeskHub.Search;

/// <summary>搜索结果的类型分组，扩展名表照搬 360 搜索的 search_file_type.json。</summary>
public static class FileKinds
{
    public const string All = "all", Doc = "doc", Pic = "pic", Zip = "zip", App = "app", Video = "video", Music = "music", Folder = "folder", Other = "other";

    /// <summary>界面上的分组标签（顺序即显示顺序）。</summary>
    public static readonly (string Key, string Name)[] Tabs =
    {
        (All, L.T("全部")), (Doc, L.T("文档")), (Pic, L.T("图片")), (Zip, L.T("压缩包")), (App, L.T("程序")), (Video, L.T("视频")), (Music, L.T("音乐")), (Folder, L.T("文件夹")),
    };

    static readonly Dictionary<string, string> Map = Build();

    static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string kind, string exts) { foreach (var e in exts.Split(' ', StringSplitOptions.RemoveEmptyEntries)) d.TryAdd("." + e, kind); }
        Add(Doc, "doc docx dot dotm dotx pdf xps htm html mht mhtml xml txt rtf odt xls xlsx xlsb xltx xltm xlt csv ods ppt pptx pptm pot potm potx ppsx pps ppsm ods odp wps et dps md ofd json log");
        Add(Pic, "bmp jpg jpeg png gif tif tiff dib psd eps raw tga ico cr2 crw webp heic heif svg ai psb");
        Add(Zip, "001 7z bz2 cab gz gzip iso jar lzh lzma rar tar tgz txz xz z zip zipx wim dmg");
        Add(App, "exe bat cmd msi msix appx lnk ps1 com scr");
        Add(Video, "mp4 3gp avi wmv mpeg mpg mov flv swf mkv webm m4v rm rmvb ts");
        Add(Music, "mp3 wav aac flac m4a ogg wma aif aiff mid ape opus");
        return d;
    }

    public static string Of(ReadOnlySpan<char> name, bool dir)
    {
        if (dir) return Folder;
        int dot = name.LastIndexOf('.');
        if (dot < 0) return Other;
        // 只看扩展名，避免为每个文件名分配字符串：扩展名都很短，查表前才转字符串
        var ext = name[dot..];
        if (ext.Length > 8) return Other;
        return Map.GetValueOrDefault(ext.ToString(), Other);
    }

    public static string Of(string path, bool dir) => Of(Path.GetFileName(path.AsSpan()), dir);
}
