using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace DeskHub.Core;

/// <summary>
/// 把文档读成文字（给密码箱智能识别用）。只读本机文件，不联网。
/// 支持：txt / md / csv / log（自动认 UTF-8 和 GBK）、Word（.docx）、Excel（.xlsx，读成表格）、图片（用 Windows 自带的离线文字识别）。
/// 老格式 .doc / .xls 和 PDF 不支持：请另存为 .docx / .xlsx，或者打开后全选复制粘贴进来。
/// </summary>
public static class DocText
{
    public static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
    public static string Filter => L.T("可识别的文件") + "|*.txt;*.md;*.csv;*.log;*.docx;*.xlsx;*.png;*.jpg;*.jpeg;*.bmp|" + L.T("所有文件") + "|*.*";

    /// <summary>读出来的结果：Rows 有值说明是表格（Excel），否则用 Text。</summary>
    public sealed record Result(string Text, List<List<string>>? Rows);

    public static async Task<Result> ReadAsync(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".docx": return new(Docx(path), null);
            case ".xlsx":
                var rows = Xlsx(path);
                return new(string.Join("\n", rows.Select(r => string.Join("\t", r))), rows);
            case ".doc": case ".xls": case ".wps": case ".et":
                throw new NotSupportedException(L.T("这是旧版 Office 格式，请用 Word / Excel 打开后“另存为” .docx / .xlsx，或者全选复制，粘贴到左边。"));
            case ".pdf":
                throw new NotSupportedException(L.T("PDF 暂不支持：请打开 PDF，全选复制，粘贴到左边。"));
        }
        if (ImageExts.Contains(ext))
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return new(await OcrBest(bmp), null);
        }
        return new(ReadText(path), null);
    }

    /// <summary>图片识别：原尺寸和放大两倍各认一次，挑认得更好的那次（截图字的大小差别很大）。</summary>
    public static async Task<string> OcrBest(System.Windows.Media.Imaging.BitmapSource img)
    {
        var a = VaultParser.TidyOcr(await Shot.Ocr.RecognizeAsync(img));
        if (img.PixelWidth * 2 > 4000 || img.PixelHeight * 2 > 4000) return a;
        var big = new System.Windows.Media.Imaging.TransformedBitmap(img, new System.Windows.Media.ScaleTransform(2, 2));
        big.Freeze();
        var b = VaultParser.TidyOcr(await Shot.Ocr.RecognizeAsync(big));
        return VaultParser.Score(b) > VaultParser.Score(a) ? b : a;
    }

    /// <summary>文本文件：先按 UTF-8 读，读出乱码就按 GBK（中文 Windows 记事本的老格式）。</summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936).GetString(bytes);
        }
    }

    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Word：段落一行一行；表格每行用制表符分格（方便按表格识别）。</summary>
    static string Docx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException(L.T("不是有效的 Word 文件"));
        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);
        var sb = new StringBuilder();
        var body = doc.Root?.Element(W + "body");
        if (body == null) return "";
        foreach (var el in body.Elements())
        {
            if (el.Name == W + "p") sb.Append(Para(el)).Append('\n');
            else if (el.Name == W + "tbl")
            {
                foreach (var tr in el.Descendants(W + "tr"))
                    sb.Append(string.Join("\t", tr.Elements(W + "tc").Select(tc => string.Join(" ", tc.Elements(W + "p").Select(Para)).Trim()))).Append('\n');
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    static string Para(XElement p)
    {
        var sb = new StringBuilder();
        foreach (var n in p.Descendants())
        {
            if (n.Name == W + "t") sb.Append(n.Value);
            else if (n.Name == W + "tab") sb.Append(' ');
            else if (n.Name == W + "br") sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Excel：读所有工作表，每行一组单元格（空行跳过，不同工作表之间空一行）。</summary>
    static List<List<string>> Xlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } ss)
        {
            using var s = ss.Open();
            foreach (var si in XDocument.Load(s).Root!.Elements(S + "si"))
                shared.Add(string.Concat(si.Descendants(S + "t").Select(t => t.Value)));
        }
        var rows = new List<List<string>>();
        var sheets = zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet") && e.FullName.EndsWith(".xml"))
            .OrderBy(e => int.TryParse(Path.GetFileNameWithoutExtension(e.Name)[5..], out var n) ? n : 99).ToList();
        foreach (var sh in sheets)
        {
            XDocument doc;
            using (var s = sh.Open()) doc = XDocument.Load(s);
            foreach (var row in doc.Descendants(S + "row"))
            {
                var cells = new List<string>();
                foreach (var c in row.Elements(S + "c"))
                {
                    int col = ColIndex((string?)c.Attribute("r"));
                    while (col > cells.Count) cells.Add("");
                    var t = (string?)c.Attribute("t");
                    var v = c.Element(S + "v")?.Value ?? "";
                    var text = t switch
                    {
                        "s" => int.TryParse(v, out var i) && i < shared.Count ? shared[i] : "",
                        "inlineStr" => string.Concat(c.Descendants(S + "t").Select(x => x.Value)),
                        _ => v,
                    };
                    cells.Add(text.Trim());
                }
                if (cells.Any(x => x.Length > 0)) rows.Add(cells);
            }
            rows.Add(new List<string>()); // 工作表之间的分隔
        }
        return rows;
    }

    static int ColIndex(string? r)
    {
        if (string.IsNullOrEmpty(r)) return 0;
        int n = 0;
        foreach (var ch in r) { if (!char.IsLetter(ch)) break; n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1); }
        return Math.Max(0, n - 1);
    }
}
