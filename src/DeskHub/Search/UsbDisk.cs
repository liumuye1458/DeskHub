using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskHub.Search;

/// <summary>判断一个盘是不是 USB 接的（移动硬盘系统也当“本地磁盘”，光看 DriveType 分不出来）。</summary>
public static class UsbDisk
{
    /// <summary>root 形如 "D:\"。查不出来时当作不是 USB（照常索引）。</summary>
    public static bool Is(string root) => BusType(root) == 7 /*BusTypeUsb*/;

    /// <summary>盘所在设备的总线类型（STORAGE_BUS_TYPE：7 USB、11 SATA、17 NVMe…）；查不出来为 -1。</summary>
    public static int BusType(string root)
    {
        try
        {
            using var h = CreateFile(@"\\.\" + root.TrimEnd('\\'), 0, 3 /*FILE_SHARE_READ|WRITE*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
            if (h.IsInvalid) return -1;
            var query = new STORAGE_PROPERTY_QUERY { PropertyId = 0 /*StorageDeviceProperty*/, QueryType = 0 /*PropertyStandardQuery*/ };
            var buf = Marshal.AllocHGlobal(1024);
            try
            {
                if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, ref query, Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(), buf, 1024, out _, IntPtr.Zero)) return -1;
                // STORAGE_DEVICE_DESCRIPTOR：BusType 在第 28 字节（Version, Size, DeviceType, DeviceTypeModifier, RemovableMedia, CommandQueueing, 4 个偏移量之后）
                return Marshal.ReadInt32(buf, 28);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return -1; }
    }

    const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    [StructLayout(LayoutKind.Sequential)]
    struct STORAGE_PROPERTY_QUERY { public int PropertyId; public int QueryType; public byte AdditionalParameters; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref STORAGE_PROPERTY_QUERY input, int inSize, IntPtr output, int outSize, out int returned, IntPtr overlapped);
}
