using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskHub.Interop;

/// <summary>
/// 取 Windows 资源管理器同款图标 / 缩略图（走系统缓存，不自己解码图片）。
/// 结果按"路径+修改时间"缓存，文件没变就不重复取。
/// </summary>
static class ShellIcons
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }

    const int SIIGBF_ICONONLY = 0x4;
    static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    // 同一时刻只用一个后台线程取图标，避免启动时一下子开几十个线程
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ImageSource?> GetAsync(string path, DateTime stamp, int px, bool thumbnail)
    {
        var key = $"{path}|{stamp.Ticks}|{px}|{thumbnail}";
        if (Cache.TryGetValue(key, out var hit)) return hit;
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Cache.TryGetValue(key, out hit)) return hit;
            var img = await RunSta(() => Load(path, px, thumbnail)).ConfigureAwait(false);
            if (img != null) Cache[key] = img;
            return img;
        }
        finally { Gate.Release(); }
    }

    // 部分外壳扩展只在 STA 线程里能正常工作：用一条常驻的后台 STA 线程排队处理
    static readonly System.Collections.Concurrent.BlockingCollection<Action> Queue = new();
    static readonly Thread Worker = StartWorker();
    static Thread StartWorker()
    {
        var t = new Thread(() => { foreach (var a in Queue.GetConsumingEnumerable()) a(); })
        { IsBackground = true, Name = "ShellIcons", Priority = ThreadPriority.BelowNormal };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return t;
    }

    static Task<T?> RunSta<T>(Func<T?> fn) where T : class
    {
        _ = Worker;
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() => { try { tcs.SetResult(fn()); } catch { tcs.SetResult(null); } });
        return tcs.Task;
    }

    public static void Forget(string path)
    {
        foreach (var k in Cache.Keys.Where(k => k.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase)).ToList())
            Cache.TryRemove(k, out _);
    }

    static ImageSource? Load(string path, int px, bool thumbnail)
    {
        object? obj = null;
        try
        {
            Native.SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItemImageFactory, out obj);
            var f = (IShellItemImageFactory)obj;
            // 不加"允许更大"：让系统直接给出正好的像素尺寸，界面上不再二次缩放（缩放会让边缘发虚）
            int flags = thumbnail ? 0 : SIIGBF_ICONONLY;
            if (f.GetImage(new SIZE(px, px), flags, out var hbm) != 0 || hbm == IntPtr.Zero) return null;
            try { return FromHBitmap(hbm); }
            finally { Native.DeleteObject(hbm); }
        }
        catch { return null; }
        finally { if (obj != null) Marshal.ReleaseComObject(obj); }
    }

    /// <summary>
    /// 32 位位图转 WPF 图像并保留透明通道。
    /// （WPF 自带的 CreateBitmapSourceFromHBitmap 会丢掉透明度，图标边缘会发黑。）
    /// </summary>
    static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        if (Native.GetObject(hbm, Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0 || bm.bmBits == IntPtr.Zero)
        {
            var b = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            b.Freeze();
            return b;
        }
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight), stride = bm.bmWidthBytes;
        var buf = new byte[stride * h];
        Marshal.Copy(bm.bmBits, buf, 0, buf.Length);
        // 底朝上的 DIB 需要翻转
        if (bm.bmHeight > 0)
        {
            var flipped = new byte[buf.Length];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(buf, y * stride, flipped, (h - 1 - y) * stride, stride);
            buf = flipped;
        }
        // 系统给的图标多数是"未预乘"的透明度（颜色值可能大于透明度），
        // 当成预乘格式会把半透明边缘画亮，看起来一圈毛边；所以按实际数据判断
        bool anyAlpha = false, straight = false;
        for (int i = 3; i < buf.Length; i += 4)
        {
            byte a = buf[i];
            if (a != 0) anyAlpha = true;
            if (a < 255 && (buf[i - 1] > a || buf[i - 2] > a || buf[i - 3] > a)) { straight = true; break; }
        }
        var fmt = !anyAlpha ? PixelFormats.Bgr32 : straight ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
        var src = BitmapSource.Create(w, h, 96, 96, fmt, null, buf, stride);
        src.Freeze();
        return src;
    }
}
