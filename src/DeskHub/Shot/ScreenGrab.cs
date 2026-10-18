using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskHub.Shot;

/// <summary>一块显示器：位置用物理像素，Scale = DPI / 96。</summary>
public sealed record Monitor(Int32Rect Bounds, double Scale);

/// <summary>截屏前的准备：抓屏幕画面、记下所有窗口和控件的位置（用于"自动识别窗口"）。</summary>
static class ScreenGrab
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hMon, int type, out uint dx, out uint dy);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);

    const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

    public static List<Monitor> Monitors()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(h, ref mi);
            double scale = GetDpiForMonitor(h, 0, out var dx, out _) == 0 ? dx / 96.0 : 1;
            var r = mi.rcMonitor;
            list.Add(new Monitor(new Int32Rect(r.L, r.T, r.R - r.L, r.B - r.T), scale));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>抓一块屏幕区域（物理像素），返回带像素数据的位图。</summary>
    public static BitmapSource Capture(Int32Rect r, out byte[] pixels)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = r.Width, biHeight = -r.Height, biPlanes = 1, biBitCount = 32 };
        var dib = CreateDIBSection(screen, ref bi, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(mem, dib);
        try
        {
            BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, SRCCOPY | CAPTUREBLT);
            pixels = new byte[r.Width * r.Height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            // 屏幕没有透明度：把透明通道统一设成不透明
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var bmp = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Bgra32, null, pixels, r.Width * 4);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            SelectObject(mem, old); DeleteObject(dib); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// 屏幕上所有可见窗口的位置（从最上层到最下层），以及每个窗口里的子控件位置。
    /// 截图时鼠标停在哪里，就框出那里最小的窗口或控件。
    /// </summary>
    public static List<(Int32Rect Rect, List<Int32Rect> Children)> Windows(IntPtr exclude)
    {
        var list = new List<(Int32Rect, List<Int32Rect>)>();
        var cls = new StringBuilder(64);
        EnumWindows((h, _) =>
        {
            if (h == exclude || !IsWindowVisible(h) || IsIconic(h)) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            long ex = (long)GetWindowLongPtr(h, -20);
            if ((ex & WS_EX_TRANSPARENT) != 0 && (ex & WS_EX_LAYERED) != 0) return true; // 点不到的透明浮层
            cls.Clear(); GetClassName(h, cls, 64);
            var c = cls.ToString();
            if (c is "Progman" or "WorkerW") return true;
            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(h, out r);
            if (r.R - r.L < 8 || r.B - r.T < 8) return true;
            var kids = new List<Int32Rect>();
            EnumChildWindows(h, (k, _) =>
            {
                if (IsWindowVisible(k) && GetWindowRect(k, out var kr) && kr.R - kr.L >= 8 && kr.B - kr.T >= 8)
                    kids.Add(new Int32Rect(kr.L, kr.T, kr.R - kr.L, kr.B - kr.T));
                return kids.Count < 400;
            }, IntPtr.Zero);
            list.Add((new Int32Rect(r.L, r.T, r.R - r.L, r.B - r.T), kids));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
