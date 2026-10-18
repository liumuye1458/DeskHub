using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DeskHub.Interop;

/// <summary>读取 .lnk 快捷方式指向的目标路径（不弹任何界面、不去找丢失的目标）。</summary>
static class ShellLink
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
        // 后面的方法用不到，但顺序必须声明到 GetPath 为止即可
    }

    const uint SLGP_RAWPATH = 0x4;
    const int STGM_READ = 0;

    public static string? Target(string lnk)
    {
        object? o = null;
        try
        {
            o = new CShellLink();
            ((IPersistFile)o).Load(lnk, STGM_READ);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)o).GetPath(sb, sb.Capacity, IntPtr.Zero, SLGP_RAWPATH);
            var s = sb.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : Environment.ExpandEnvironmentVariables(s);
        }
        catch { return null; }
        finally { if (o != null) Marshal.ReleaseComObject(o); }
    }
}
