using System.Runtime.InteropServices;
using System.Text;

namespace DeskHub.Core;

/// <summary>用 Windows 自带的 DPAPI 加密小段文字（只有本机当前账户解得开），存密钥用。</summary>
public static class Secret
{
    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr p);

    const int UiForbidden = 1;

    static byte[]? Run(byte[] data, bool protect)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var inp = new Blob { Size = data.Length, Data = h.AddrOfPinnedObject() };
            Blob outp;
            bool ok = protect
                ? CryptProtectData(ref inp, Brand.Id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outp)
                : CryptUnprotectData(ref inp, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outp);
            if (!ok) return null;
            var r = new byte[outp.Size];
            Marshal.Copy(outp.Data, r, 0, outp.Size);
            LocalFree(outp.Data);
            return r;
        }
        finally { h.Free(); }
    }

    public static string Protect(string plain) =>
        plain.Length == 0 ? "" : Run(Encoding.UTF8.GetBytes(plain), true) is { } b ? Convert.ToBase64String(b) : "";

    public static string Unprotect(string stored)
    {
        if (stored.Length == 0) return "";
        try { return Run(Convert.FromBase64String(stored), false) is { } b ? Encoding.UTF8.GetString(b) : ""; }
        catch { return ""; }
    }
}
