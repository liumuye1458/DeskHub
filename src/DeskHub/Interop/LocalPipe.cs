using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DeskHub.Interop;

/// <summary>
/// 只给本机、本用户用的命名管道（文件查找的索引程序、密码箱接口都用它）：
///   权限只给当前用户，并且明确拒绝从网络登录的身份（NetworkSid）；
///   建管道时带 PIPE_REJECT_REMOTE_CLIENTS，别的电脑连过来系统直接拒绝（.NET 自带的建法设不了这个，所以自己调 CreateNamedPipe）。
/// </summary>
public static class LocalPipe
{
    public static PipeSecurity Security()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return sec;
    }

    /// <summary>建一个双向、按字节读写的管道服务端。maxInstances 传 NamedPipeServerStream.MaxAllowedServerInstances 表示不限。</summary>
    public static NamedPipeServerStream Create(string name, int maxInstances, bool async, int inBuffer, int outBuffer)
    {
        var sd = Security().GetSecurityDescriptorBinaryForm();
        var pin = GCHandle.Alloc(sd, GCHandleType.Pinned);
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = pin.AddrOfPinnedObject(), bInheritHandle = 0 };
            uint open = PIPE_ACCESS_DUPLEX
                | (maxInstances == 1 ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0)
                | (async ? FILE_FLAG_OVERLAPPED : 0);
            uint mode = PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS;
            uint max = maxInstances is < 1 or > 254 ? PIPE_UNLIMITED_INSTANCES : (uint)maxInstances;
            var h = CreateNamedPipe(@"\\.\pipe\" + name, open, mode, max, (uint)outBuffer, (uint)inBuffer, 0, ref sa);
            if (h.IsInvalid) throw new IOException($"CreateNamedPipe 失败（{Marshal.GetLastWin32Error()}）");
            return new NamedPipeServerStream(PipeDirection.InOut, async, false, h);
        }
        finally { pin.Free(); }
    }

    const uint PIPE_ACCESS_DUPLEX = 0x3, FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000, FILE_FLAG_OVERLAPPED = 0x40000000;
    const uint PIPE_TYPE_BYTE = 0, PIPE_READMODE_BYTE = 0, PIPE_WAIT = 0, PIPE_REJECT_REMOTE_CLIENTS = 0x8, PIPE_UNLIMITED_INSTANCES = 255;

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateNamedPipeW")]
    static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuffer, uint inBuffer, uint timeout, ref SECURITY_ATTRIBUTES sa);
}
