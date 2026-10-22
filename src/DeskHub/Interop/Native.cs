using System.Runtime.InteropServices;
using System.Text;

namespace DeskHub.Interop;

static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int GWLP_HWNDPARENT = -8;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_TIMECHANGE = 0x001E;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_INITMENUPOPUP = 0x0117;
    public const int WM_DRAWITEM = 0x002B;
    public const int WM_MEASUREITEM = 0x002C;
    public const int WM_MENUCHAR = 0x0120;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;

    public static readonly IntPtr HWND_BOTTOM = new(1);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);

    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);

    // WinEvent：系统把桌面图标层重新显示时通知我们（事件驱动，不轮询）
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObj, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    public const uint EVENT_OBJECT_SHOW = 0x8002, WINEVENT_OUTOFCONTEXT = 0;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017, EVENT_OBJECT_UNCLOAKED = 0x8018;

    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    public const uint GW_OWNER = 4;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    public const int DWMWA_CLOAKED = 14;
    public const long WS_EX_TOPMOST = 0x00000008, WS_EX_TRANSPARENT = 0x00000020;

    // 菜单
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool InsertMenu(IntPtr m, uint pos, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] public static extern uint TrackPopupMenuEx(IntPtr m, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr m);
    public const uint MF_BYPOSITION = 0x400, MF_STRING = 0, MF_SEPARATOR = 0x800, MF_POPUP = 0x10, MF_CHECKED = 0x8, MF_GRAYED = 0x1;
    public const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;

    // 外壳
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")]
    public static extern int SHBindToParent(IntPtr pidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv, out IntPtr ppidlLast);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(string path, IntPtr bc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
    [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr p);

    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)] // 64 位下按自然对齐（共 24 字节），按 4 字节打包会被系统拒绝
    public struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ShellExecute(IntPtr hwnd, string? verb, string file, string? args, string? dir, int show);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint flags);
}
