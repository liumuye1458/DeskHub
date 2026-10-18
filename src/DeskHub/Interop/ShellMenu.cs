using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DeskHub.Interop;

/// <summary>
/// 右键菜单：Windows 原生菜单（打开方式、发送到、压缩、属性、WPS/7-Zip 等扩展项全都有），
/// 前面再加上本工具自己的几项。
/// </summary>
static class ShellMenu
{
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

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
    }
    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
        [PreserveSig] int HandleMenuMsg(int msg, IntPtr wParam, IntPtr lParam);
    }
    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
        [PreserveSig] int HandleMenuMsg(int msg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize; public uint fMask; public IntPtr hwnd; public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow; public uint dwHotKey; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public Native.POINT ptInvoke;
    }

    const uint CMF_NORMAL = 0, CMF_EXPLORE = 0x4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
    const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000;
    const uint ShellFirst = 1, ShellLast = 0x7FFF, OwnFirst = 0x8000;

    static IContextMenu2? _cm2;
    static IContextMenu3? _cm3;

    /// <summary>自己的菜单项：文字、是否勾选、是否可用、点击后的动作。null 表示分隔线。</summary>
    public sealed record Own(string Text, Action? Run, bool Checked = false, bool Enabled = true, List<Own>? Sub = null);

    [DllImport("shell32.dll")] static extern IntPtr ILFindLastID(IntPtr pidl);

    /// <summary>
    /// 弹出菜单。paths 可以是好几个（必须在同一个文件夹里），原生菜单的命令就对这一批生效；
    /// paths 为空时只显示本工具自己的菜单项。返回 true 表示执行了某个系统命令（例如删除、重命名），调用方可刷新。
    /// </summary>
    public static bool Show(Window owner, IList<string> paths, IList<Own?> own)
    {
        var hwnd = new WindowInteropHelper(owner).Handle;
        IntPtr parentPtr = IntPtr.Zero, cmPtr = IntPtr.Zero, menu = IntPtr.Zero;
        var pidls = new List<IntPtr>();
        IShellFolder? parent = null; IContextMenu? cm = null;
        var actions = new Dictionary<uint, Action>();
        HwndSource? src = HwndSource.FromHwnd(hwnd);
        try
        {
            foreach (var path in paths)
            {
                if (Native.SHParseDisplayName(path, IntPtr.Zero, out var p, 0, out _) != 0) return false;
                pidls.Add(p);
            }
            if (pidls.Count > 0)
            {
                if (Native.SHBindToParent(pidls[0], typeof(IShellFolder).GUID, out parentPtr, out _) != 0) return false;
                parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
                var children = pidls.Select(ILFindLastID).ToArray();
                parent.GetUIObjectOf(hwnd, (uint)children.Length, children, typeof(IContextMenu).GUID, IntPtr.Zero, out cmPtr);
                cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);
                _cm2 = cm as IContextMenu2; _cm3 = cm as IContextMenu3;
            }

            menu = Native.CreatePopupMenu();
            uint pos = 0, id = OwnFirst;
            foreach (var o in own) AddOwn(menu, ref pos, ref id, o, actions);
            if (cm != null)
            {
                Native.InsertMenu(menu, pos++, Native.MF_BYPOSITION | Native.MF_SEPARATOR, UIntPtr.Zero, null);
                bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
                cm.QueryContextMenu(menu, pos, ShellFirst, ShellLast, CMF_NORMAL | CMF_EXPLORE | (shift ? CMF_EXTENDEDVERBS : 0));
            }

            src?.AddHook(MenuHook);
            Native.GetCursorPos(out var pt);
            var cmd = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, pt.X, pt.Y, hwnd, IntPtr.Zero);
            src?.RemoveHook(MenuHook);
            if (cmd == 0) return false;

            if (cmd >= OwnFirst || cm == null)
            {
                if (actions.TryGetValue(cmd, out var a)) a();
                return false;
            }
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                hwnd = hwnd,
                lpVerb = new IntPtr(cmd - ShellFirst),
                lpVerbW = new IntPtr(cmd - ShellFirst),
                nShow = 1,
                ptInvoke = pt,
            };
            cm.InvokeCommand(ref info);
            return true;
        }
        catch { return false; }
        finally
        {
            _cm2 = null; _cm3 = null;
            if (menu != IntPtr.Zero) Native.DestroyMenu(menu);
            if (cm != null) Marshal.ReleaseComObject(cm);
            if (parent != null) Marshal.ReleaseComObject(parent);
            if (cmPtr != IntPtr.Zero) Marshal.Release(cmPtr);
            if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
            foreach (var p in pidls) Native.CoTaskMemFree(p);
        }
    }

    static void AddOwn(IntPtr menu, ref uint pos, ref uint id, Own? o, Dictionary<uint, Action> actions)
    {
        if (o == null) { Native.InsertMenu(menu, pos++, Native.MF_BYPOSITION | Native.MF_SEPARATOR, UIntPtr.Zero, null); return; }
        if (o.Sub != null)
        {
            var sub = Native.CreatePopupMenu();
            uint sp = 0;
            foreach (var s in o.Sub) AddOwn(sub, ref sp, ref id, s, actions);
            Native.InsertMenu(menu, pos++, Native.MF_BYPOSITION | Native.MF_POPUP, (UIntPtr)(ulong)sub, o.Text);
            return;
        }
        uint flags = Native.MF_BYPOSITION | Native.MF_STRING | (o.Checked ? Native.MF_CHECKED : 0) | (o.Enabled ? 0 : Native.MF_GRAYED);
        Native.InsertMenu(menu, pos++, flags, (UIntPtr)id, o.Text);
        if (o.Run != null) actions[id] = o.Run;
        id++;
    }

    /// <summary>"发送到""打开方式"等子菜单是动态生成的，需要把菜单消息转交给外壳处理。</summary>
    static IntPtr MenuHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is Native.WM_INITMENUPOPUP or Native.WM_DRAWITEM or Native.WM_MEASUREITEM or Native.WM_MENUCHAR)
        {
            if (_cm3 != null && _cm3.HandleMenuMsg2(msg, wParam, lParam, out var r) == 0) { handled = true; return r; }
            if (_cm2 != null && _cm2.HandleMenuMsg(msg, wParam, lParam) == 0) { handled = true; return IntPtr.Zero; }
        }
        return IntPtr.Zero;
    }

    public static void Open(string path) => Native.ShellExecute(IntPtr.Zero, null, path, null, System.IO.Path.GetDirectoryName(path), 1);

    public static void Reveal(string path)
    {
        if (Native.SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0) return;
        try { Native.SHOpenFolderAndSelectItems(pidl, 0, null, 0); }
        finally { Native.CoTaskMemFree(pidl); }
    }
}
