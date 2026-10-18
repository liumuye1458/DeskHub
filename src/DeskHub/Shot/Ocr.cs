using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using DeskHub.Core;

namespace DeskHub.Shot;

/// <summary>
/// 提取文字：用 Windows 自带的离线文字识别（Windows.Media.Ocr），不联网。
/// 识别语言跟随 Windows 的语言设置（装了中文就能认中文）。
/// </summary>
static class Ocr
{
    public static async Task<string> RecognizeAsync(BitmapSource img)
    {
        var engine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
            ?? global::Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage).FirstOrDefault(e => e != null)
            ?? throw new InvalidOperationException(L.T("这台电脑没有安装文字识别语言包。\n可以在 Windows 设置 → 时间和语言 → 语言 里给中文添加“光学字符识别”。"));

        // 小图放大两倍再认，小字的识别率高很多
        BitmapSource src = img;
        if (img.PixelWidth < 800 && img.PixelHeight < 800)
        {
            var big = new TransformedBitmap(img, new ScaleTransform(2, 2));
            big.Freeze();
            src = big;
        }
        int max = (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension;
        if (src.PixelWidth > max || src.PixelHeight > max)
        {
            double k = Math.Min((double)max / src.PixelWidth, (double)max / src.PixelHeight);
            var small = new TransformedBitmap(src, new ScaleTransform(k, k));
            small.Freeze();
            src = small;
        }
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);

        using var sb = global::Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
            global::Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(px),
            global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, w, h, global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(sb);

        var text = new StringBuilder();
        foreach (var line in result.Lines) text.AppendLine(Tidy(line.Text));
        return text.ToString().TrimEnd();
    }

    /// <summary>识别中文时，系统会在每个字之间加空格，去掉中文之间的空格。</summary>
    static string Tidy(string s) =>
        Regex.Replace(s, @"(?<=[　-鿿＀-￯])\s+(?=[　-鿿＀-￯])", "");
}
