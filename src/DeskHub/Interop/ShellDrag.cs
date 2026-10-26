using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DeskHub.Interop;

/// <summary>
/// 让分区里的图标在拖拽、复制粘贴时和真正的桌面图标一模一样。
///
/// 以前拖出去用的是自己拼的“文件列表”，只有最基本的文件路径。任务栏、开始菜单要的是 Windows 自己的
/// “外壳项目”数据，认不出来，所以拖到任务栏固定、拖到开始菜单都不成功；此电脑、回收站这类系统图标更是根本拖不出去。
/// 现在统一向 Windows 要它自己的数据对象（和资源管理器拖出去的是同一种），任何接收方都认得。
///
/// 反过来，拖到分区里的文件夹、回收站、程序图标上，也交给那个图标自己的系统处理程序：
/// 放进文件夹、扔进回收站、用这个程序打开——和在桌面上一样。
/// </summary>
static class ShellDrag
{
    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL { public int X, Y; }

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTarget
    {
        [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] ComDataObject data, int keys, POINTL pt, ref int effect);
        [PreserveSig] int DragOver(int keys, POINTL pt, ref int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] ComDataObject data, int keys, POINTL pt, ref int effect);
    }

    [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTargetHelper
    {
        [PreserveSig] int DragEnter(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] ComDataObject data, ref Native.POINT pt, int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int DragOver(ref Native.POINT pt, int effect);
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] ComDataObject data, ref Native.POINT pt, int effect);
        [PreserveSig] int Show(bool show);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eaten, out IntPtr pidl, IntPtr attrs);
        void EnumObjects(IntPtr hwnd, int flags, out IntPtr ppenum);
        void BindToObject(IntPtr pidl, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwnd, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr rgfReserved, out IntPtr ppv);
    }

    [DllImport("shell32.dll", PreserveSig = false)]
    static extern void SHCreateShellItemArrayFromIDLists(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, out IShellItemArray array);
    [DllImport("shell32.dll")]
    static extern int SHDoDragDrop(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] ComDataObject data, IntPtr source, int okEffects, out int effect);
    [DllImport("shell32.dll")] static extern IntPtr ILFindLastID(IntPtr pidl);
    [DllImport("ole32.dll")] static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] ComDataObject? data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);

    static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");
    static readonly Guid CLSID_DragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");
    const uint SFGAO_DROPTARGET = 0x100;
    public const int Copy = 1, Move = 2, Link = 4;

    /// <summary>正在从本程序里拖的东西（拖拽是一个模态过程，同一时间只有一次）：从哪个分区、哪几个路径。</summary>
    public sealed record Drag(string From, IReadOnlyList<string> Paths);
    public static Drag? Active { get; private set; }

    /// <summary>向 Windows 要这些路径的数据对象（和资源管理器里选中它们拖出去时一样）。可以来自不同文件夹，也可以是 "::{CLSID}" 系统图标。</summary>
    public static ComDataObject? Make(IReadOnlyList<string> paths)
    {
        var pidls = new List<IntPtr>();
        IShellItemArray? arr = null;
        try
        {
            foreach (var p in paths)
                if (Native.SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) == 0) pidls.Add(pidl);
            if (pidls.Count == 0) return null;
            SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out arr);
            arr.BindToHandler(IntPtr.Zero, BHID_DataObject, typeof(ComDataObject).GUID, out var ptr);
            try { return (ComDataObject)Marshal.GetObjectForIUnknown(ptr); }
            finally { Marshal.Release(ptr); }
        }
        catch { return null; }
        finally
        {
            if (arr != null) Marshal.ReleaseComObject(arr);
            foreach (var p in pidls) Native.CoTaskMemFree(p);
        }
    }

    /// <summary>
    /// 从本程序拖出去（会一直等到松手）。from 是分区的 Key（拖到别的分区算换分区）；不是从分区拖的传 null，拖进分区就和外面拖来的文件一样。
    /// 返回最后的效果（复制/移动/快捷方式/0 表示取消）。
    /// </summary>
    public static int Start(Window owner, IReadOnlyList<string> paths, string? from)
    {
        var data = Make(paths);
        if (data == null) return 0;
        Active = from == null ? null : new Drag(from, paths);
        try
        {
            SHDoDragDrop(new WindowInteropHelper(owner).Handle, data, IntPtr.Zero, Copy | Move | Link, out var effect);
            return effect;
        }
        catch { return 0; }
        finally { Active = null; Marshal.ReleaseComObject(data); }
    }

    // ---------------- 剪贴板 ----------------

    /// <summary>Ctrl+C / Ctrl+X：放到剪贴板上，可以在资源管理器、微信等任何地方粘贴。</summary>
    public static bool ToClipboard(IReadOnlyList<string> paths, bool cut)
    {
        var data = Make(paths);
        if (data == null) return false;
        // 和资源管理器一样告诉粘贴方：这是“剪切”（粘贴后原文件会被移走）还是“复制”
        SetDword(data, "Preferred DropEffect", cut ? Move : Copy | Link);
        return OleSetClipboard(data) == 0;
    }

    /// <summary>剪贴板上的文件，以及是不是“剪切”来的。</summary>
    public static (string[] Files, bool Cut)? FromClipboard()
    {
        try
        {
            var d = Clipboard.GetDataObject();
            if (d?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return null;
            bool cut = d.GetData("Preferred DropEffect") is System.IO.MemoryStream ms && ms.Length >= 4
                && (BitConverter.ToInt32(ms.ToArray(), 0) & Move) != 0;
            return (files, cut);
        }
        catch { return null; }
    }

    public static void ClearClipboard() { try { OleSetClipboard(null); } catch { } }

    static void SetDword(ComDataObject data, string format, int value)
    {
        var h = GlobalAlloc(0x0042 /*GHND*/, (UIntPtr)4);
        var p = GlobalLock(h);
        Marshal.WriteInt32(p, value);
        GlobalUnlock(h);
        var fe = new System.Runtime.InteropServices.ComTypes.FORMATETC
        {
            cfFormat = unchecked((short)RegisterClipboardFormat(format)),
            dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL,
        };
        var sm = new System.Runtime.InteropServices.ComTypes.STGMEDIUM { tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL, unionmember = h };
        data.SetData(ref fe, ref sm, true);
    }

    /// <summary>调试用：数据对象里有哪些格式。</summary>
    public static List<string> Formats(ComDataObject data)
    {
        var list = new List<string>();
        var en = data.EnumFormatEtc(System.Runtime.InteropServices.ComTypes.DATADIR.DATADIR_GET);
        var one = new System.Runtime.InteropServices.ComTypes.FORMATETC[1];
        var got = new int[1];
        while (en.Next(1, one, got) == 0 && got[0] == 1)
            list.Add(DataFormats.GetDataFormat((ushort)one[0].cfFormat).Name);
        return list;
    }

    // ---------------- 拖到某个图标上 ----------------

    /// <summary>
    /// 某个图标自己的“接收拖放”处理程序（文件夹、回收站、程序、快捷方式、压缩包……）。
    /// 普通文档没有，返回 null，这时照旧当作拖进分区。
    /// </summary>
    public sealed class ItemTarget
    {
        readonly IDropTarget _t;
        ItemTarget(IDropTarget t) => _t = t;

        public static ItemTarget? For(IntPtr hwnd, string path)
        {
            IntPtr pidl = IntPtr.Zero, parentPtr = IntPtr.Zero, tPtr = IntPtr.Zero;
            IShellFolder? parent = null;
            try
            {
                if (Native.SHParseDisplayName(path, IntPtr.Zero, out pidl, SFGAO_DROPTARGET, out var attrs) != 0) return null;
                if ((attrs & SFGAO_DROPTARGET) == 0) return null;
                if (Native.SHBindToParent(pidl, typeof(IShellFolder).GUID, out parentPtr, out _) != 0) return null;
                parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
                parent.GetUIObjectOf(hwnd, 1, new[] { ILFindLastID(pidl) }, typeof(IDropTarget).GUID, IntPtr.Zero, out tPtr);
                return new ItemTarget((IDropTarget)Marshal.GetObjectForIUnknown(tPtr));
            }
            catch { return null; }
            finally
            {
                if (parent != null) Marshal.ReleaseComObject(parent);
                if (tPtr != IntPtr.Zero) Marshal.Release(tPtr);
                if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
                if (pidl != IntPtr.Zero) Native.CoTaskMemFree(pidl);
            }
        }

        public int Enter(ComDataObject data, int keys, int allowed) { int e = allowed; return Try(() => _t.DragEnter(data, keys, Cursor(), ref e)) ? e : 0; }
        public int Over(int keys, int allowed) { int e = allowed; return Try(() => _t.DragOver(keys, Cursor(), ref e)) ? e : 0; }
        public void Leave() { Try(() => _t.DragLeave()); Release(); }
        public int Drop(ComDataObject data, int keys, int allowed) { int e = allowed; bool ok = Try(() => _t.Drop(data, keys, Cursor(), ref e)); Release(); return ok ? e : 0; }

        void Release() { try { Marshal.ReleaseComObject(_t); } catch { } }
        static bool Try(Func<int> f) { try { return f() >= 0; } catch { return false; } }
        static POINTL Cursor() { Native.GetCursorPos(out var p); return new POINTL { X = p.X, Y = p.Y }; }
    }

    /// <summary>
    /// 拖着东西经过本程序的窗口时，让 Windows 照常画出拖动的图标缩略图（不然一进分区缩略图就消失）。
    /// </summary>
    public sealed class ImageHelper
    {
        IDropTargetHelper? _h;
        bool _in;

        public void Enter(Window w, ComDataObject data, int effect)
        {
            if (_in) return;
            try
            {
                _h ??= (IDropTargetHelper)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_DragDropHelper)!)!;
                Native.GetCursorPos(out var p);
                _h.DragEnter(new WindowInteropHelper(w).Handle, data, ref p, effect);
                _in = true;
            }
            catch { }
        }
        public void Over(int effect) { if (!_in) return; try { Native.GetCursorPos(out var p); _h!.DragOver(ref p, effect); } catch { } }
        public void Leave() { if (!_in) return; _in = false; try { _h!.DragLeave(); } catch { } }
        public void Drop(ComDataObject data, int effect) { if (!_in) return; _in = false; try { Native.GetCursorPos(out var p); _h!.Drop(data, ref p, effect); } catch { } }
    }
}
