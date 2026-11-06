using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace MultiDir.Services;

/// <summary>
/// The real Explorer context menu (IContextMenu): every shell verb and third-party entry (VS Code, 7-Zip, Git…),
/// including owner-drawn submenus such as 傳送到 / 開啟檔案 via IContextMenu2/3 message forwarding.
/// Build it, then either <see cref="Track"/> it on screen or (automation) list / invoke its items directly.
/// </summary>
public sealed class ShellContextMenu : IDisposable
{
    private const int FirstId = 1, LastId = 0x7FFF;

    private readonly IContextMenu _menu;
    private readonly IntPtr _hmenu;

    private ShellContextMenu(IContextMenu menu, bool extended, bool background)
    {
        _menu = menu;
        _hmenu = CreatePopupMenu();
        uint flags = CMF_EXPLORE | CMF_CANRENAME | (extended ? CMF_EXTENDEDVERBS : 0) | (background ? 0 : CMF_ITEMMENU);
        _menu.QueryContextMenu(_hmenu, 0, FirstId, LastId, flags);
    }

    /// <summary>Menu for files/folders/drives. All paths should share one parent folder, as in Explorer.</summary>
    public static ShellContextMenu? ForItems(IReadOnlyList<string> paths, bool extended = false)
    {
        var pidls = paths.Select(p => SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) == 0 ? pidl : IntPtr.Zero).ToArray();
        try
        {
            if (pidls.Length == 0 || pidls.Any(p => p == IntPtr.Zero)) return null;
            if (SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out var array) != 0) return null;
            Guid bhid = BHID_SFUIObject, iid = IID_IContextMenu;
            array.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out var obj);
            return obj is IContextMenu cm ? new ShellContextMenu(cm, extended, background: false) : null;
        }
        catch { return null; }
        finally { foreach (var p in pidls) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>Menu for the empty area of a folder: 新增, 貼上, 內容…</summary>
    public static ShellContextMenu? ForFolderBackground(string folder, bool extended = false)
    {
        try
        {
            var iidItem = IID_IShellItem;
            if (SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iidItem, out var item) != 0) return null;
            Guid bhid = BHID_SFViewObject, iid = IID_IContextMenu;
            item.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out var obj);
            return obj is IContextMenu cm ? new ShellContextMenu(cm, extended, background: true) : null;
        }
        catch { return null; }
    }

    public sealed record Item(int Id, string Text, string? Verb, int Depth);

    /// <summary>Flattened menu (submenus indented). Used by automation; submenus that fill lazily may be empty.</summary>
    public List<Item> Items()
    {
        var list = new List<Item>();
        Walk(_hmenu, 0, list);
        return list;
    }

    private void Walk(IntPtr hmenu, int depth, List<Item> list)
    {
        int n = GetMenuItemCount(hmenu);
        for (int i = 0; i < n; i++)
        {
            var buffer = Marshal.AllocHGlobal(512 * 2);
            string text;
            var info = new MENUITEMINFO { cbSize = Marshal.SizeOf<MENUITEMINFO>(), fMask = MIIM_ID | MIIM_SUBMENU | MIIM_FTYPE | MIIM_STRING, dwTypeData = buffer, cch = 511 };
            try
            {
                if (!GetMenuItemInfo(hmenu, (uint)i, true, ref info)) continue;
                text = (info.fType & MFT_SEPARATOR) != 0 ? "----" : Marshal.PtrToStringUni(buffer) ?? "";
            }
            finally { Marshal.FreeHGlobal(buffer); }
            if (text == "----") { list.Add(new Item(0, text, null, depth)); continue; }
            list.Add(new Item((int)info.wID, text.Replace("&", ""), info.hSubMenu == IntPtr.Zero ? Verb((int)info.wID) : null, depth));
            if (info.hSubMenu != IntPtr.Zero) Walk(info.hSubMenu, depth + 1, list);
        }
    }

    /// <summary>Language-independent verb of a command ("open", "delete", "rename", "properties"…), if the handler has one.</summary>
    public string? Verb(int id)
    {
        if (id < FirstId || id >= CustomFirstId) return null;
        var sb = new StringBuilder(256);
        try { return _menu.GetCommandString((UIntPtr)(uint)(id - FirstId), GCS_VERBW, IntPtr.Zero, sb, sb.Capacity) == 0 ? sb.ToString() : null; }
        catch { return null; }
    }

    /// <summary>Shows the menu at a screen point (physical pixels) and returns the chosen command id, or 0.</summary>
    // ---------- MultiDir's own items (top of the menu, decision #12: 原生選單 + 自訂項目) ----------

    /// <summary>One of our own menu entries: either an action or a submenu.</summary>
    public sealed record CustomItem(string Text, Action? Run = null, IReadOnlyList<CustomItem>? Children = null, bool Enabled = true);

    private const int CustomFirstId = 0x8000; // above the shell's id range (FirstId..LastId)
    private readonly Dictionary<int, Action> _custom = new();

    /// <summary>Puts <paramref name="items"/> at the top of the menu, followed by a separator.</summary>
    public void AddCustomTop(IReadOnlyList<CustomItem> items)
    {
        if (items.Count == 0) return;
        uint pos = 0;
        foreach (var item in items) InsertCustom(_hmenu, pos++, item);
        InsertMenu(_hmenu, pos, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
    }

    private void InsertCustom(IntPtr menu, uint pos, CustomItem item)
    {
        uint flags = MF_BYPOSITION | MF_STRING | (item.Enabled ? 0 : MF_GRAYED);
        if (item.Children != null)
        {
            var sub = CreatePopupMenu();
            uint i = 0;
            foreach (var c in item.Children) InsertCustom(sub, i++, c);
            InsertMenu(menu, pos, flags | MF_POPUP, (UIntPtr)(ulong)sub, item.Text);
            return;
        }
        int id = CustomFirstId + _custom.Count;
        _custom[id] = item.Run ?? (() => { });
        InsertMenu(menu, pos, flags, (UIntPtr)(uint)id, item.Text);
    }

    /// <summary>Runs one of our own items; false if <paramref name="id"/> is a shell command.</summary>
    public bool RunCustom(int id)
    {
        if (!_custom.TryGetValue(id, out var run)) return false;
        run();
        return true;
    }

    public int Track(HwndSource owner, int x, int y)
    {
        // Owner-drawn / lazily filled submenus (傳送到, 開啟檔案, 新增) need these messages forwarded to the handler.
        HwndSourceHook hook = (IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
            {
                if (_menu is IContextMenu3 cm3 && cm3.HandleMenuMsg2((uint)msg, w, l, out var result) == 0) { handled = true; return result; }
                if (_menu is IContextMenu2 cm2 && cm2.HandleMenuMsg((uint)msg, w, l) == 0) { handled = true; return IntPtr.Zero; }
            }
            return IntPtr.Zero;
        };
        owner.AddHook(hook);
        try { return TrackPopupMenuEx(_hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, owner.Handle, IntPtr.Zero); }
        finally { owner.RemoveHook(hook); }
    }

    /// <summary>Runs a command the way Explorer does (delete → 資源回收筒 with confirmation, properties dialog…).</summary>
    public void Invoke(int id, IntPtr hwnd, string? folder, int x = 0, int y = 0, bool shift = false, bool control = false)
    {
        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE | (shift ? CMIC_MASK_SHIFT_DOWN : 0) | (control ? CMIC_MASK_CONTROL_DOWN : 0),
            hwnd = hwnd,
            lpVerb = (IntPtr)(id - FirstId),
            lpVerbW = (IntPtr)(id - FirstId),
            lpDirectoryW = folder,
            nShow = SW_SHOWNORMAL,
            ptInvoke = new POINT { x = x, y = y },
        };
        _menu.InvokeCommand(ref info);
    }

    /// <summary>Runs a verb by name without showing the menu (e.g. Delete key → "delete").</summary>
    public bool InvokeVerb(string verb, IntPtr hwnd, string? folder, bool shift = false)
    {
        var item = Items().FirstOrDefault(i => string.Equals(i.Verb, verb, StringComparison.OrdinalIgnoreCase));
        if (item == null) return false;
        Invoke(item.Id, hwnd, folder, shift: shift);
        return true;
    }

    public void Dispose()
    {
        DestroyMenu(_hmenu);
        Marshal.ReleaseComObject(_menu);
    }

    // ---------- interop ----------

    private const uint CMF_EXPLORE = 0x4, CMF_CANRENAME = 0x10, CMF_ITEMMENU = 0x80, CMF_EXTENDEDVERBS = 0x100;
    private const uint GCS_VERBW = 4;
    private const int CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000,
        CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000;
    private const int SW_SHOWNORMAL = 1;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
    private const int WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120;
    private const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_POPUP = 0x10, MF_SEPARATOR = 0x800, MF_BYPOSITION = 0x400;
    private const uint MIIM_ID = 0x2, MIIM_SUBMENU = 0x4, MIIM_STRING = 0x40, MIIM_FTYPE = 0x100, MFT_SEPARATOR = 0x800;

    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");
    private static readonly Guid BHID_SFUIObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");
    private static readonly Guid BHID_SFViewObject = new("3981e226-f559-11d3-8e3a-00c04f6837d5");

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public POINT ptInvoke;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MENUITEMINFO
    {
        public int cbSize;
        public uint fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData;
        public IntPtr dwTypeData;
        public int cch;
        public IntPtr hbmpItem;
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMax);
    }

    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);
    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint cidl, IntPtr[] rgpidl, out IShellItemArray ppsiItemArray);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool InsertMenu(IntPtr hMenu, uint position, uint flags, UIntPtr idNewItem, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMenuItemInfo(IntPtr hMenu, uint item, bool byPosition, ref MENUITEMINFO info);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr hmenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
}
