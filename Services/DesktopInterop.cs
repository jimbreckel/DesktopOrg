using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using MyReDesk.Models;

namespace MyReDesk.Services;

public static class DesktopInterop
{
    #region Win32 Constants & P/Invoke

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private const int GWL_STYLE = -16;
    private const int LVS_AUTOARRANGE = 0x00000100;

    private const int LVM_FIRST = 0x1000;
    private const int LVM_GETITEMCOUNT = LVM_FIRST + 4;
    private const int LVM_GETITEMPOSITION = LVM_FIRST + 16;
    private const int LVM_GETITEMSPACING = LVM_FIRST + 51;
    private const int LVM_SETITEMPOSITION32 = LVM_FIRST + 49;
    private const int LVM_GETITEMW = LVM_FIRST + 75;

    private const uint LVIF_TEXT = 0x0001;

    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    private const uint WM_COMMAND = 0x0111;
    private const int CMD_TOGGLE_AUTOARRANGE = 0x7071;

    private const uint SPI_GETDESKWALLPAPER = 0x0073;
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDWININICHANGE = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct LVITEM64
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, uint nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    #endregion

    #region COM Shell Interfaces for IFolderView

    private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");

    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;
    private const uint SVGIO_ALLVIEW = 0x00000002;
    private const uint SVSI_POSITIONITEM = 0x00000080;
    private const uint SHGDN_NORMAL = 0x0000;
    private const uint SHGDN_FORPARSING = 0x8000;

    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellWindows
    {
        void GetTypeInfoCount(out uint pctinfo);
        void GetTypeInfo(uint iTInfo, uint lcid, out IntPtr ppTInfo);
        void GetIDsOfNames(ref Guid riid, IntPtr rgszNames, uint cNames, uint lcid, IntPtr rgDispId);
        void Invoke(int dispIdMember, ref Guid riid, uint lcid, ushort wFlags, IntPtr pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);

        int get_Count();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object Item([MarshalAs(UnmanagedType.Struct)] object index);
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object _NewEnum();
        void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int plCookie);
        void RegisterPending(int lThreadId, ref object pvarloc, ref object pvarlocRoot, int swClass, out int plCookie);
        void Revoke(int lCookie);
        void OnNavigate(int lCookie, ref object pvarloc);
        void OnActivated(int lCookie, bool fActive);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(
            [MarshalAs(UnmanagedType.Struct)] ref object pvarloc,
            [MarshalAs(UnmanagedType.Struct)] ref object pvarlocRoot,
            int swClass,
            out int pHWND,
            int swfwOptions);
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        void RemoveMenusSB(IntPtr hmenuShared);
        void SetStatusTextSB(IntPtr pszStatusText);
        void EnableModelessSB(bool fEnable);
        void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        void BrowseObject(IntPtr pidl, uint wFlags);
        void GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        void GetControlWindow(uint id, out IntPtr phwnd);
        void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        void QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object ppshv);
    }

    [ComImport]
    [Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode(out uint pViewMode);
        void SetCurrentViewMode(uint ViewMode);
        void GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder ppv);
        void Item(int iItemIndex, out IntPtr ppidl);
        void ItemCount(uint uFlags, out int pcItems);
        void Items(uint uFlags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetSelectionMarkedItem(out int piItem);
        void GetFocusedItem(out int piItem);
        void GetItemPosition(IntPtr pidl, out POINT ppt);
        void GetSpacing(ref POINT ppt);
        void GetDefaultSpacing(out POINT ppt);
        void GetAutoArrange();
        void SelectItem(int iItem, uint dwFlags);
        void SelectAndPositionItems(uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] apidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] POINT[] apt, uint dwFlags);
    }

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        void EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] apidl, ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        void GetDisplayNameOf(IntPtr pidl, uint uFlags, out STRRET pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [StructLayout(LayoutKind.Explicit, Size = 272)]
    private struct STRRET
    {
        [FieldOffset(0)]
        public uint uType;
        [FieldOffset(8)]
        public IntPtr pOleStr;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void StrRetToBuf(ref STRRET pstr, IntPtr pidl, StringBuilder pszBuf, uint cchBuf);

    #endregion

    #region Desktop Handles & States

    public static IntPtr GetProgmanHandle()
    {
        return FindWindow("Progman", null);
    }

    public static (IntPtr DefView, IntPtr ListView) GetDesktopHandles()
    {
        IntPtr progman = FindWindow("Progman", null);
        IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);

        if (defView == IntPtr.Zero)
        {
            EnumWindows((hwnd, _) =>
            {
                IntPtr dv = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero)
                {
                    defView = dv;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }

        IntPtr listView = defView != IntPtr.Zero
            ? FindWindowEx(defView, IntPtr.Zero, "SysListView32", null)
            : IntPtr.Zero;

        return (defView, listView);
    }

    public static bool AreDesktopIconsVisible()
    {
        var (_, listView) = GetDesktopHandles();
        return listView == IntPtr.Zero || IsWindowVisible(listView);
    }

    public static void SetDesktopIconsVisible(bool visible)
    {
        var (_, listView) = GetDesktopHandles();
        if (listView != IntPtr.Zero)
        {
            ShowWindow(listView, visible ? SW_SHOW : SW_HIDE);
        }
    }

    public static bool IsAutoArrangeEnabled()
    {
        var (_, listView) = GetDesktopHandles();
        if (listView == IntPtr.Zero) return false;
        int style = GetWindowLong(listView, GWL_STYLE);
        return (style & LVS_AUTOARRANGE) != 0;
    }

    public static void EnsureAutoArrangeDisabled()
    {
        var (defView, listView) = GetDesktopHandles();
        if (listView == IntPtr.Zero || defView == IntPtr.Zero) return;

        if (IsAutoArrangeEnabled())
        {
            SendMessage(defView, (int)WM_COMMAND, (IntPtr)CMD_TOGGLE_AUTOARRANGE, IntPtr.Zero);
            Thread.Sleep(60);
        }
    }

    public static (int SpacingX, int SpacingY) GetDesktopGridSpacing()
    {
        var (_, listView) = GetDesktopHandles();
        if (listView != IntPtr.Zero)
        {
            long spacing = SendMessage(listView, LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero).ToInt64();
            int cx = (int)(spacing & 0xFFFF);
            int cy = (int)((spacing >> 16) & 0xFFFF);
            if (cx >= 48 && cy >= 48 && cx <= 300 && cy <= 300)
            {
                return (cx, cy);
            }
        }
        return (0, 0);
    }

    public static void RefreshDesktop()
    {
        var (_, listView) = GetDesktopHandles();
        if (listView != IntPtr.Zero)
        {
            InvalidateRect(listView, IntPtr.Zero, true);
        }
        SHChangeNotify(0x8000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
    }

    #endregion

    #region Read & Write Desktop Icons

    public static List<DesktopIconItem> GetDesktopIcons()
    {
        List<DesktopIconItem>? result = null;

        var staThread = new Thread(() =>
        {
            try
            {
                result = GetDesktopIconsViaFolderView();
            }
            catch
            {
                result = null;
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join(4000);

        if (result != null && result.Count > 0)
        {
            return result;
        }

        return GetDesktopIconsViaListView();
    }

    private static List<DesktopIconItem> GetDesktopIconsViaFolderView()
    {
        var items = new List<DesktopIconItem>();
        var shellWindowsType = Type.GetTypeFromCLSID(CLSID_ShellWindows);
        if (shellWindowsType == null) return items;

        var shellWindowsObj = Activator.CreateInstance(shellWindowsType);
        if (shellWindowsObj is not IShellWindows shellWindows) return items;

        object loc = new();
        object root = new();
        object disp = shellWindows.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
        if (disp is not IServiceProvider sp) return items;

        Guid sidTop = SID_STopLevelBrowser;
        Guid iidBrowser = IID_IShellBrowser;
        if (sp.QueryService(ref sidTop, ref iidBrowser, out object browserObj) != 0 || browserObj is not IShellBrowser browser)
            return items;

        browser.QueryActiveShellView(out object viewObj);
        if (viewObj is not IFolderView folderView) return items;

        Guid iidShellFolder = IID_IShellFolder;
        folderView.GetFolder(ref iidShellFolder, out IShellFolder shellFolder);

        folderView.ItemCount(SVGIO_ALLVIEW, out int count);
        var desktopFiles = BuildDesktopFileLookup();

        for (int i = 0; i < count; i++)
        {
            folderView.Item(i, out IntPtr pidl);
            if (pidl == IntPtr.Zero) continue;

            try
            {
                folderView.GetItemPosition(pidl, out POINT pt);
                string displayName = GetPidlName(shellFolder, pidl, SHGDN_NORMAL);
                string parsingName = GetPidlName(shellFolder, pidl, SHGDN_FORPARSING);

                if (string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(parsingName))
                    continue;

                var item = CreateDesktopIconItem(displayName, parsingName, pt.X, pt.Y, desktopFiles);
                items.Add(item);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }

        return items;
    }

    private static string GetPidlName(IShellFolder folder, IntPtr pidl, uint flags)
    {
        try
        {
            folder.GetDisplayNameOf(pidl, flags, out STRRET strRet);
            var sb = new StringBuilder(1024);
            StrRetToBuf(ref strRet, pidl, sb, (uint)sb.Capacity);
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static List<DesktopIconItem> GetDesktopIconsViaListView()
    {
        var items = new List<DesktopIconItem>();
        var (_, listView) = GetDesktopHandles();
        if (listView == IntPtr.Zero) return items;

        int count = (int)SendMessage(listView, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0) return items;

        GetWindowThreadProcessId(listView, out uint pid);
        IntPtr hProcess = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (hProcess == IntPtr.Zero) return items;

        var desktopFiles = BuildDesktopFileLookup();

        try
        {
            uint allocSize = 4096;
            IntPtr remoteMem = VirtualAllocEx(hProcess, IntPtr.Zero, allocSize, MEM_COMMIT, PAGE_READWRITE);
            if (remoteMem == IntPtr.Zero) return items;

            try
            {
                IntPtr remotePoint = remoteMem;
                IntPtr remoteLvItem = IntPtr.Add(remoteMem, 64);
                IntPtr remoteText = IntPtr.Add(remoteMem, 256);

                for (int i = 0; i < count; i++)
                {
                    SendMessage(listView, LVM_GETITEMPOSITION, (IntPtr)i, remotePoint);
                    POINT pt = new();
                    IntPtr localPt = Marshal.AllocHGlobal(Marshal.SizeOf<POINT>());
                    try
                    {
                        ReadProcessMemory(hProcess, remotePoint, localPt, (uint)Marshal.SizeOf<POINT>(), out _);
                        pt = Marshal.PtrToStructure<POINT>(localPt);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(localPt);
                    }

                    var lvItem = new LVITEM64
                    {
                        mask = LVIF_TEXT,
                        iItem = i,
                        iSubItem = 0,
                        pszText = remoteText,
                        cchTextMax = 512
                    };

                    int lvSize = Marshal.SizeOf<LVITEM64>();
                    IntPtr localLv = Marshal.AllocHGlobal(lvSize);
                    try
                    {
                        Marshal.StructureToPtr(lvItem, localLv, false);
                        WriteProcessMemory(hProcess, remoteLvItem, localLv, (uint)lvSize, out _);
                        SendMessage(listView, LVM_GETITEMW, (IntPtr)i, remoteLvItem);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(localLv);
                    }

                    byte[] textBytes = new byte[1024];
                    IntPtr localText = Marshal.AllocHGlobal(textBytes.Length);
                    string name = string.Empty;
                    try
                    {
                        ReadProcessMemory(hProcess, remoteText, localText, (uint)textBytes.Length, out _);
                        name = Marshal.PtrToStringUni(localText) ?? string.Empty;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(localText);
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        items.Add(CreateDesktopIconItem(name, name, pt.X, pt.Y, desktopFiles));
                    }
                }
            }
            finally
            {
                VirtualFreeEx(hProcess, remoteMem, 0, MEM_RELEASE);
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }

        return items;
    }

    public static int SetDesktopIconPositions(IEnumerable<DesktopIconItem> desiredPositions)
    {
        var desiredList = desiredPositions.ToList();
        if (desiredList.Count == 0) return 0;

        EnsureAutoArrangeDisabled();

        int movedCount = 0;
        var staThread = new Thread(() =>
        {
            try
            {
                movedCount = SetPositionsViaFolderView(desiredList);
            }
            catch
            {
                movedCount = 0;
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join(4000);

        if (movedCount > 0)
        {
            return movedCount;
        }

        return SetPositionsViaListView(desiredList);
    }

    private static int SetPositionsViaFolderView(List<DesktopIconItem> desiredPositions)
    {
        var shellWindowsType = Type.GetTypeFromCLSID(CLSID_ShellWindows);
        if (shellWindowsType == null) return 0;

        var shellWindowsObj = Activator.CreateInstance(shellWindowsType);
        if (shellWindowsObj is not IShellWindows shellWindows) return 0;

        object loc = new();
        object root = new();
        object disp = shellWindows.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
        if (disp is not IServiceProvider sp) return 0;

        Guid sidTop = SID_STopLevelBrowser;
        Guid iidBrowser = IID_IShellBrowser;
        if (sp.QueryService(ref sidTop, ref iidBrowser, out object browserObj) != 0 || browserObj is not IShellBrowser browser)
            return 0;

        browser.QueryActiveShellView(out object viewObj);
        if (viewObj is not IFolderView folderView) return 0;

        Guid iidShellFolder = IID_IShellFolder;
        folderView.GetFolder(ref iidShellFolder, out IShellFolder shellFolder);

        folderView.ItemCount(SVGIO_ALLVIEW, out int count);
        if (count <= 0) return 0;

        var byParsing = new Dictionary<string, DesktopIconItem>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, DesktopIconItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in desiredPositions)
        {
            if (!string.IsNullOrWhiteSpace(d.ParsingName))
                byParsing[d.ParsingName] = d;
            if (!string.IsNullOrWhiteSpace(d.Name))
                byName[d.Name] = d;
        }

        var pidlsToMove = new List<IntPtr>();
        var pointsToMove = new List<POINT>();

        try
        {
            for (int i = 0; i < count; i++)
            {
                folderView.Item(i, out IntPtr pidl);
                if (pidl == IntPtr.Zero) continue;

                string displayName = GetPidlName(shellFolder, pidl, SHGDN_NORMAL);
                string parsingName = GetPidlName(shellFolder, pidl, SHGDN_FORPARSING);

                DesktopIconItem? match = null;
                if (!string.IsNullOrWhiteSpace(parsingName) && byParsing.TryGetValue(parsingName, out var m1))
                {
                    match = m1;
                }
                else if (!string.IsNullOrWhiteSpace(displayName) && byName.TryGetValue(displayName, out var m2))
                {
                    match = m2;
                }
                else if (!string.IsNullOrWhiteSpace(parsingName))
                {
                    string fileNameNoExt = Path.GetFileNameWithoutExtension(parsingName);
                    if (!string.IsNullOrWhiteSpace(fileNameNoExt) && byName.TryGetValue(fileNameNoExt, out var m3))
                        match = m3;
                }

                if (match != null)
                {
                    pidlsToMove.Add(pidl);
                    pointsToMove.Add(new POINT { X = match.X, Y = match.Y });
                }
                else
                {
                    Marshal.FreeCoTaskMem(pidl);
                }
            }

            if (pidlsToMove.Count > 0)
            {
                var pidlArray = pidlsToMove.ToArray();
                var ptArray = pointsToMove.ToArray();

                // Pass 1: Move all icons to target coordinates
                folderView.SelectAndPositionItems(
                    (uint)pidlArray.Length,
                    pidlArray,
                    ptArray,
                    SVSI_POSITIONITEM);

                // Pass 2 (after 35ms): Re-apply exact coordinates so any icon bumped by Explorer's
                // "Align icons to grid" collision check (when target cell was still occupied during Pass 1)
                // snaps cleanly into its now-free target cell.
                Thread.Sleep(35);
                folderView.SelectAndPositionItems(
                    (uint)pidlArray.Length,
                    pidlArray,
                    ptArray,
                    SVSI_POSITIONITEM);
            }

            return pidlsToMove.Count;
        }
        finally
        {
            foreach (var pidl in pidlsToMove)
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    private static int SetPositionsViaListView(List<DesktopIconItem> desiredPositions)
    {
        var (_, listView) = GetDesktopHandles();
        if (listView == IntPtr.Zero) return 0;

        var currentIcons = GetDesktopIconsViaListView();
        if (currentIcons.Count == 0) return 0;

        var byName = new Dictionary<string, DesktopIconItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in desiredPositions)
        {
            if (!string.IsNullOrWhiteSpace(d.Name))
                byName[d.Name] = d;
        }

        GetWindowThreadProcessId(listView, out uint pid);
        IntPtr hProcess = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (hProcess == IntPtr.Zero) return 0;

        int moved = 0;
        try
        {
            IntPtr remotePt = VirtualAllocEx(hProcess, IntPtr.Zero, 64, MEM_COMMIT, PAGE_READWRITE);
            if (remotePt == IntPtr.Zero) return 0;

            try
            {
                IntPtr localPt = Marshal.AllocHGlobal(Marshal.SizeOf<POINT>());
                try
                {
                    for (int i = 0; i < currentIcons.Count; i++)
                    {
                        var curr = currentIcons[i];
                        if (byName.TryGetValue(curr.Name, out var target))
                        {
                            POINT pt = new() { X = target.X, Y = target.Y };
                            Marshal.StructureToPtr(pt, localPt, false);
                            WriteProcessMemory(hProcess, remotePt, localPt, (uint)Marshal.SizeOf<POINT>(), out _);
                            SendMessage(listView, LVM_SETITEMPOSITION32, (IntPtr)i, remotePt);
                            moved++;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(localPt);
                }
            }
            finally
            {
                VirtualFreeEx(hProcess, remotePt, 0, MEM_RELEASE);
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }

        InvalidateRect(listView, IntPtr.Zero, true);
        return moved;
    }

    #endregion

    #region Categorization & File Resolution

    private static Dictionary<string, string> BuildDesktopFileLookup()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

        foreach (string dir in new[] { publicDesktop, userDesktop })
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    string fileName = Path.GetFileName(entry);
                    string nameNoExt = Path.GetFileNameWithoutExtension(entry);
                    map[fileName] = entry;
                    if (!string.IsNullOrWhiteSpace(nameNoExt))
                    {
                        map[nameNoExt] = entry;
                    }
                }
            }
            catch
            {
                // Ignore permission errors
            }
        }
        return map;
    }

    private static DesktopIconItem CreateDesktopIconItem(
        string displayName,
        string parsingName,
        int x,
        int y,
        Dictionary<string, string> desktopFiles)
    {
        string? filePath = null;
        if (!string.IsNullOrWhiteSpace(parsingName) && (File.Exists(parsingName) || Directory.Exists(parsingName)))
        {
            filePath = parsingName;
        }
        else if (!string.IsNullOrWhiteSpace(displayName) && desktopFiles.TryGetValue(displayName, out var foundPath))
        {
            filePath = foundPath;
        }

        bool isVirtual = filePath == null || parsingName.StartsWith("::", StringComparison.Ordinal);
        string checkPath = filePath ?? parsingName;
        bool isFolder = !isVirtual && Directory.Exists(checkPath);
        string ext = isFolder ? string.Empty : Path.GetExtension(checkPath).ToLowerInvariant();
        bool isWebLink = !isFolder && ext == ".url";
        bool isScriptExecutable = !isFolder && !isVirtual && IsScriptOrCommandExecutable(ext, displayName, parsingName, checkPath);
        bool isShortcut = isVirtual || isScriptExecutable || ext is ".lnk" or ".appref-ms" or ".exe" or ".msi";

        string category = ClassifyIcon(displayName, parsingName, filePath, isVirtual);
        var (targetUrl, webDomain, webHost) = isWebLink
            ? ExtractUrlAndDomain(checkPath)
            : (null, null, null);

        return new DesktopIconItem
        {
            Name = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(parsingName) : displayName,
            ParsingName = string.IsNullOrWhiteSpace(parsingName) ? displayName : parsingName,
            X = x,
            Y = y,
            Category = category,
            IsSystemOrVirtual = isVirtual,
            IsFolder = isFolder,
            IsWebLink = isWebLink,
            IsShortcut = isShortcut,
            IsScriptExecutable = isScriptExecutable,
            FilePath = filePath,
            TargetUrl = targetUrl,
            WebDomain = webDomain,
            WebHost = webHost
        };
    }

    public static bool IsScriptOrCommandExecutable(string ext, string displayName, string parsingName, string? filePath = null)
    {
        if (ext is ".bat" or ".cmd" or ".ps1" or ".sh" or ".vbs" or ".wsf" or ".ahk" or ".py" or ".pyw" or ".reg" or ".jar")
            return true;

        if (ext == ".lnk")
        {
            string check = $"{displayName} {Path.GetFileNameWithoutExtension(parsingName)}";
            if (ContainsScriptIndicator(check))
                return true;

            string? pathToRead = filePath;
            if (string.IsNullOrWhiteSpace(pathToRead) || !File.Exists(pathToRead))
            {
                if (File.Exists(parsingName))
                    pathToRead = parsingName;
            }

            if (!string.IsNullOrWhiteSpace(pathToRead) && File.Exists(pathToRead))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(pathToRead);
                    string uni = Encoding.Unicode.GetString(bytes);
                    string ascii = Encoding.ASCII.GetString(bytes);
                    if (ContainsScriptIndicator(uni) || ContainsScriptIndicator(ascii))
                        return true;
                }
                catch
                {
                    // Ignore read errors
                }
            }
        }

        return false;
    }

    private static bool ContainsScriptIndicator(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains(".bat", StringComparison.OrdinalIgnoreCase) ||
               text.Contains(".ps1", StringComparison.OrdinalIgnoreCase) ||
               text.Contains(".cmd", StringComparison.OrdinalIgnoreCase) ||
               text.Contains(".sh", StringComparison.OrdinalIgnoreCase) ||
               text.Contains(".vbs", StringComparison.OrdinalIgnoreCase) ||
               text.Contains(".wsf", StringComparison.OrdinalIgnoreCase) ||
               text.Contains(".ahk", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("wscript.exe", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("cscript.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static (string? Url, string? BaseDomain, string? Host) ExtractUrlAndDomain(string? urlFilePath)
    {
        if (string.IsNullOrWhiteSpace(urlFilePath) || !File.Exists(urlFilePath))
            return (null, null, null);

        try
        {
            string? rawUrl = null;
            foreach (string line in File.ReadLines(urlFilePath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    rawUrl = trimmed.Substring(4).Trim();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(rawUrl))
                return (null, null, null);

            if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                string host = uri.Host.ToLowerInvariant();
                if (host.StartsWith("www.", StringComparison.Ordinal))
                {
                    host = host.Substring(4);
                }

                string[] parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
                string baseDomain = host;
                if (parts.Length >= 3)
                {
                    // Handle multi-part TLDs like .co.uk / .com.au vs standard subdomains like help.salesforce.com
                    string secondLast = parts[^2];
                    if (secondLast is "co" or "com" or "org" or "net" or "gov" or "edu" && parts[^1].Length == 2)
                    {
                        baseDomain = $"{parts[^3]}.{parts[^2]}.{parts[^1]}";
                    }
                    else
                    {
                        baseDomain = $"{parts[^2]}.{parts[^1]}";
                    }
                }

                return (rawUrl, baseDomain, host);
            }

            return (rawUrl, null, null);
        }
        catch
        {
            return (null, null, null);
        }
    }

    public static string ClassifyIcon(string displayName, string parsingName, string? filePath, bool isVirtual)
    {
        if (isVirtual || parsingName.StartsWith("::", StringComparison.Ordinal))
        {
            return "System";
        }

        string checkPath = filePath ?? parsingName;
        if (Directory.Exists(checkPath))
        {
            return "Folders";
        }

        string ext = Path.GetExtension(checkPath).ToLowerInvariant();
        return ext switch
        {
            ".url" => "Web Links (.url)",
            ".lnk" or ".exe" or ".appref-ms" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".sh" or ".vbs" or ".wsf" or ".ahk" or ".reg" or ".jar" => "Shortcuts",
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".md" or ".rtf" or ".csv" or ".epub" => "Documents",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".bmp" or ".ico" or ".mp4" or ".mkv" or ".mov" or ".mp3" or ".wav" => "Media",
            ".cs" or ".py" or ".js" or ".ts" or ".json" or ".xml" or ".yaml" or ".yml" or ".sql" or ".html" or ".css" or ".sln" or ".cfg" or ".http" => "Code & Archives",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".iso" => "Code & Archives",
            _ => "Other"
        };
    }

    #endregion

    #region Wallpaper Integration (Draws Gray Areas Directly Behind Desktop Icons!)

    public static string GetCurrentWallpaperPath()
    {
        var sb = new StringBuilder(1024);
        if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
        {
            string p = sb.ToString();
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                return p;
        }

        string transcoded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");
        return File.Exists(transcoded) ? transcoded : string.Empty;
    }

    public static bool ApplyWallpaperWithGrayAreas(AppState state, string appDataFolder)
    {
        try
        {
            string currentWall = GetCurrentWallpaperPath();
            string backupPath = Path.Combine(appDataFolder, "original_wallpaper_backup.png");
            string generatedPath = Path.Combine(appDataFolder, "myredesk_active_wallpaper.png");

            // Save original wallpaper backup if current wallpaper is not our generated one
            if (!string.IsNullOrWhiteSpace(currentWall) &&
                !currentWall.EndsWith("myredesk_active_wallpaper.png", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(currentWall))
            {
                File.Copy(currentWall, backupPath, overwrite: true);
                state.OriginalWallpaperBackupPath = backupPath;
            }

            string sourcePath = state.OriginalWallpaperBackupPath ?? currentWall;
            var primary = GetMonitors().FirstOrDefault(m => m.IsPrimary) ?? new MonitorInfo { Width = 1920, Height = 1080 };
            int screenW = Math.Max(800, primary.Width);
            int screenH = Math.Max(600, primary.Height);

            using var canvas = new Bitmap(screenW, screenH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
                {
                    using var srcImg = Image.FromFile(sourcePath);
                    // Cover/Fill scaling matching Windows default Fill mode
                    double scale = Math.Max((double)screenW / srcImg.Width, (double)screenH / srcImg.Height);
                    int drawW = (int)Math.Round(srcImg.Width * scale);
                    int drawH = (int)Math.Round(srcImg.Height * scale);
                    int drawX = (screenW - drawW) / 2;
                    int drawY = (screenH - drawH) / 2;
                    g.DrawImage(srcImg, new Rectangle(drawX, drawY, drawW, drawH));
                }
                else
                {
                    g.Clear(Color.FromArgb(30, 41, 59));
                }

                // Draw each visible Custom Area as a ReDesk rounded gray area
                foreach (var area in state.CustomAreas.Where(a => a.IsVisible))
                {
                    int x = state.GridOffsetX + area.GridCol * state.CellWidthPx;
                    int y = state.GridOffsetY + area.GridRow * state.CellHeightPx;
                    int w = Math.Max(60, area.GridWidthCols * state.CellWidthPx - 8);
                    int h = Math.Max(50, area.GridHeightRows * state.CellHeightPx - 8);

                    var rect = new Rectangle(x, y, w, h);
                    int radius = (int)Math.Clamp(area.CornerRadius, 2, 28);

                    Color baseGray = ParseDrawingColor(area.GrayHex, Color.FromArgb(140, 146, 156));
                    int alpha = Math.Clamp((int)(area.Opacity * 255), 35, 235);
                    using var fillBrush = new SolidBrush(Color.FromArgb(alpha, baseGray.R, baseGray.G, baseGray.B));

                    Color borderCol = ParseDrawingColor(area.BorderHex, Color.FromArgb(200, 206, 216));
                    using var borderPen = new Pen(Color.FromArgb(Math.Clamp(alpha + 35, 80, 240), borderCol.R, borderCol.G, borderCol.B), 1.5f);

                    using var path = CreateRoundedRectPath(rect, radius);
                    g.FillPath(fillBrush, path);
                    g.DrawPath(borderPen, path);

                    if (!string.Equals(area.LabelAlignment, "Hidden", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(area.Title))
                    {
                        using var font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
                        using var textBrush = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
                        var sf = new StringFormat
                        {
                            Alignment = area.LabelAlignment == "Top-Left" ? StringAlignment.Near : StringAlignment.Center,
                            LineAlignment = area.LabelAlignment == "Top-Left" ? StringAlignment.Near : StringAlignment.Center
                        };
                        var textRect = area.LabelAlignment == "Top-Left"
                            ? new RectangleF(x + 12, y + 8, w - 24, h - 16)
                            : new RectangleF(x, y, w, h);

                        g.DrawString(area.Title, font, textBrush, textRect, sf);
                    }
                }
            }

            canvas.Save(generatedPath, ImageFormat.Png);
            return SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, generatedPath, SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);
        }
        catch
        {
            return false;
        }
    }

    public static bool RestoreOriginalWallpaper(AppState state)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(state.OriginalWallpaperBackupPath) &&
                File.Exists(state.OriginalWallpaperBackupPath))
            {
                string currentWall = GetCurrentWallpaperPath();
                if (currentWall.EndsWith("myredesk_active_wallpaper.png", StringComparison.OrdinalIgnoreCase))
                {
                    return SystemParametersInfo(
                        SPI_SETDESKWALLPAPER,
                        0,
                        state.OriginalWallpaperBackupPath,
                        SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);
                }
            }
        }
        catch
        {
            // Ignore
        }
        return false;
    }

    private static GraphicsPath CreateRoundedRectPath(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color ParseDrawingColor(string hex, Color fallback)
    {
        try
        {
            return ColorTranslator.FromHtml(hex);
        }
        catch
        {
            return fallback;
        }
    }

    #endregion

    #region Monitor Signature & Screen Info

    private const int MONITORINFOF_PRIMARY = 0x00000001;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    private sealed class RawDisplayScreen
    {
        public string DeviceName { get; init; } = string.Empty;
        public bool Primary { get; init; }
        public Rectangle Bounds { get; init; }
        public Rectangle WorkingArea { get; init; }
    }

    private static List<RawDisplayScreen> EnumeratePhysicalScreens()
    {
        var screens = new List<RawDisplayScreen>();
        IntPtr prevDpiCtx = IntPtr.Zero;
        try
        {
            prevDpiCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch
        {
            // Ignore on older OS builds
        }

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var mi = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
                if (GetMonitorInfoW(hMon, ref mi))
                {
                    int x = mi.rcMonitor.Left;
                    int y = mi.rcMonitor.Top;
                    int w = Math.Max(1, mi.rcMonitor.Right - mi.rcMonitor.Left);
                    int h = Math.Max(1, mi.rcMonitor.Bottom - mi.rcMonitor.Top);

                    int wx = mi.rcWork.Left;
                    int wy = mi.rcWork.Top;
                    int ww = Math.Max(1, mi.rcWork.Right - mi.rcWork.Left);
                    int wh = Math.Max(1, mi.rcWork.Bottom - mi.rcWork.Top);

                    screens.Add(new RawDisplayScreen
                    {
                        DeviceName = mi.szDevice?.Trim() ?? string.Empty,
                        Primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                        Bounds = new Rectangle(x, y, w, h),
                        WorkingArea = new Rectangle(wx, wy, ww, wh)
                    });
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Fallback handled below
        }
        finally
        {
            if (prevDpiCtx != IntPtr.Zero)
            {
                try { SetThreadDpiAwarenessContext(prevDpiCtx); } catch { }
            }
        }

        if (screens.Count == 0)
        {
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
            {
                screens.Add(new RawDisplayScreen
                {
                    DeviceName = s.DeviceName,
                    Primary = s.Primary,
                    Bounds = s.Bounds,
                    WorkingArea = s.WorkingArea
                });
            }
        }

        return screens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();
    }

    public static (int LvOriginX, int LvOriginY) GetTargetMonitorListViewOrigin(MonitorInfo target, List<MonitorInfo> monitors)
    {
        int rawMinX = monitors.Count > 0 ? monitors.Min(m => m.X) : 0;
        int rawMinY = monitors.Count > 0 ? monitors.Min(m => m.Y) : 0;
        return (target.X - rawMinX, target.Y - rawMinY);
    }

    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        var allScreens = EnumeratePhysicalScreens();
        var primaryScreen = allScreens.FirstOrDefault(s => s.Primary) ?? allScreens.FirstOrDefault();
        var hardwareNames = GetMonitorHardwareFriendlyNames();

        var leftScreens = primaryScreen != null
            ? allScreens.Where(s => !s.Primary && s.Bounds.Right <= primaryScreen.Bounds.Left + 40).OrderBy(s => s.Bounds.X).ToList()
            : new List<RawDisplayScreen>();
        var rightScreens = primaryScreen != null
            ? allScreens.Where(s => !s.Primary && s.Bounds.Left >= primaryScreen.Bounds.Right - 40).OrderBy(s => s.Bounds.X).ToList()
            : new List<RawDisplayScreen>();

        int num = 1;
        foreach (var screen in allScreens)
        {
            string posLabel = "Display";
            if (screen.Primary)
            {
                posLabel = "Main";
            }
            else if (primaryScreen != null)
            {
                int leftIdx = leftScreens.IndexOf(screen);
                int rightIdx = rightScreens.IndexOf(screen);
                if (leftIdx >= 0)
                {
                    posLabel = leftScreens.Count > 1 ? $"Left {leftIdx + 1}" : "Left";
                }
                else if (rightIdx >= 0)
                {
                    posLabel = rightScreens.Count > 1 ? $"Right {rightIdx + 1}" : "Right";
                }
                else if (screen.Bounds.Bottom <= primaryScreen.Bounds.Top + 40)
                {
                    posLabel = "Top";
                }
                else if (screen.Bounds.Top >= primaryScreen.Bounds.Bottom - 40)
                {
                    posLabel = "Bottom";
                }
                else
                {
                    posLabel = $"Screen {num}";
                }
            }

            hardwareNames.TryGetValue(screen.DeviceName, out string? modelName);
            modelName = modelName?.Trim() ?? string.Empty;

            string primaryTag = screen.Primary ? " [Primary]" : "";
            string friendly = !string.IsNullOrWhiteSpace(modelName)
                ? $"Monitor {num}: {posLabel} - {modelName} ({screen.Bounds.Width}×{screen.Bounds.Height}){primaryTag}"
                : $"Monitor {num}: {posLabel} ({screen.Bounds.Width}×{screen.Bounds.Height}){primaryTag}";

            list.Add(new MonitorInfo
            {
                DeviceName = screen.DeviceName,
                FriendlyName = friendly,
                HardwareModel = modelName,
                MonitorNumber = num,
                X = screen.Bounds.X,
                Y = screen.Bounds.Y,
                Width = screen.Bounds.Width,
                Height = screen.Bounds.Height,
                WorkWidth = screen.WorkingArea.Width,
                WorkHeight = screen.WorkingArea.Height,
                IsPrimary = screen.Primary,
                IsConnected = true
            });
            num++;
        }
        return list;
    }

    public static string GetMonitorSignature()
    {
        var monitors = GetMonitors();
        return string.Join("|", monitors.Select(m => $"{m.Width}x{m.Height}@{m.X},{m.Y}{(m.IsPrimary ? "*" : "")}"));
    }

    public static string GetMonitorSummary()
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0) return "Unknown Display";
        if (monitors.Count == 1)
        {
            var m = monitors[0];
            return $"Single Display ({m.Width}×{m.Height})";
        }
        return $"{monitors.Count} Displays ({string.Join(" + ", monitors.Select(m => $"{m.Width}×{m.Height}"))})";
    }

    #region DisplayConfig Hardware Monitor Friendly Names

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
        public byte[] modeData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    public static Dictionary<string, string> GetMonitorHardwareFriendlyNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0 || pathCount == 0)
                return map;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return map;

            for (int i = 0; i < pathCount; i++)
            {
                var srcName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = paths[i].sourceInfo.adapterId,
                        id = paths[i].sourceInfo.id
                    }
                };

                var dstName = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = paths[i].targetInfo.adapterId,
                        id = paths[i].targetInfo.id
                    }
                };

                if (DisplayConfigGetDeviceInfo(ref srcName) == 0 &&
                    DisplayConfigGetDeviceInfo(ref dstName) == 0 &&
                    !string.IsNullOrWhiteSpace(srcName.viewGdiDeviceName) &&
                    !string.IsNullOrWhiteSpace(dstName.monitorFriendlyDeviceName))
                {
                    map[srcName.viewGdiDeviceName] = dstName.monitorFriendlyDeviceName.Trim();
                }
            }
        }
        catch
        {
            // Ignore DisplayConfig errors
        }
        return map;
    }

    #endregion

    #region Windows 10/11 Virtual Desktops & Item Launching

    private const string VirtualDesktopsRegPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    public static List<VirtualDesktopInfo> GetVirtualDesktops()
    {
        var result = new List<VirtualDesktopInfo>();
        string currentId = GetCurrentVirtualDesktopId();

        try
        {
            using var vdKey = Registry.CurrentUser.OpenSubKey(VirtualDesktopsRegPath, false);
            if (vdKey?.GetValue("VirtualDesktopIDs") is byte[] rawIds && rawIds.Length >= 16)
            {
                int count = rawIds.Length / 16;
                for (int i = 0; i < count; i++)
                {
                    byte[] guidBytes = new byte[16];
                    Array.Copy(rawIds, i * 16, guidBytes, 0, 16);
                    var guid = new Guid(guidBytes);
                    string idStr = guid.ToString("D");

                    string customName = string.Empty;
                    try
                    {
                        using var subKey = vdKey.OpenSubKey($@"Desktops\{{{idStr}}}", false);
                        customName = subKey?.GetValue("Name")?.ToString() ?? string.Empty;
                    }
                    catch
                    {
                        // Ignore
                    }

                    string displayName = !string.IsNullOrWhiteSpace(customName)
                        ? $"Desktop {i + 1}: {customName}"
                        : $"Desktop {i + 1}";

                    bool isCurrent = string.Equals(idStr, currentId, StringComparison.OrdinalIgnoreCase) ||
                                     (string.IsNullOrWhiteSpace(currentId) && i == 0);

                    result.Add(new VirtualDesktopInfo
                    {
                        Id = idStr,
                        Name = displayName,
                        Index = i + 1,
                        IsCurrent = isCurrent
                    });
                }
            }
        }
        catch
        {
            // Ignore registry errors
        }

        if (result.Count == 0)
        {
            result.Add(new VirtualDesktopInfo
            {
                Id = "default-1",
                Name = "Desktop 1",
                Index = 1,
                IsCurrent = true
            });
        }

        return result;
    }

    public static string GetCurrentVirtualDesktopId()
    {
        try
        {
            using var vdKey = Registry.CurrentUser.OpenSubKey(VirtualDesktopsRegPath, false);
            if (vdKey?.GetValue("CurrentVirtualDesktop") is byte[] curBytes && curBytes.Length == 16)
            {
                return new Guid(curBytes).ToString("D");
            }

            // Fallback: check SessionInfo\<sessionId>\VirtualDesktops\CurrentVirtualDesktop (Windows 10 / older Win11)
            int sessionId = Process.GetCurrentProcess().SessionId;
            using var sessionVdKey = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{sessionId}\VirtualDesktops", false);
            if (sessionVdKey?.GetValue("CurrentVirtualDesktop") is byte[] sessBytes && sessBytes.Length == 16)
            {
                return new Guid(sessBytes).ToString("D");
            }

            // Default to first GUID in VirtualDesktopIDs if CurrentVirtualDesktop is not yet written
            if (vdKey?.GetValue("VirtualDesktopIDs") is byte[] allBytes && allBytes.Length >= 16)
            {
                byte[] firstGuid = new byte[16];
                Array.Copy(allBytes, 0, firstGuid, 0, 16);
                return new Guid(firstGuid).ToString("D");
            }
        }
        catch
        {
            // Ignore
        }
        return "default-1";
    }

    public static bool LaunchDesktopItem(DesktopIconItem item)
    {
        if (item == null) return false;

        if (item.IsWebLink && !string.IsNullOrWhiteSpace(item.TargetUrl))
        {
            return LaunchTarget(item.TargetUrl);
        }

        if (!string.IsNullOrWhiteSpace(item.FilePath) &&
            (File.Exists(item.FilePath) || Directory.Exists(item.FilePath)))
        {
            return LaunchTarget(item.FilePath);
        }

        if (!string.IsNullOrWhiteSpace(item.ParsingName))
        {
            return LaunchTarget(item.ParsingName);
        }

        return false;
    }

    public static bool LaunchTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;

        try
        {
            string trimmed = target.Trim().Trim('"');

            // 1. Web URL (http:// or https://)
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(trimmed) { UseShellExecute = true });
                return true;
            }

            // 2. .url Internet Shortcut file -> open its URL directly or via ShellExecute
            if (trimmed.EndsWith(".url", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed))
            {
                var (url, _, _) = ExtractUrlAndDomain(trimmed);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    return true;
                }
            }

            // 3. Virtual shell item / CLSID (e.g. Recycle Bin ::{645FF040-...})
            if (trimmed.StartsWith("::", StringComparison.Ordinal) ||
                trimmed.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                string arg = trimmed.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                    ? trimmed
                    : $"shell:{trimmed}";
                Process.Start(new ProcessStartInfo("explorer.exe", arg) { UseShellExecute = true });
                return true;
            }

            // 4. Existing file or folder
            if (File.Exists(trimmed) || Directory.Exists(trimmed))
            {
                var psi = new ProcessStartInfo(trimmed)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Directory.Exists(trimmed)
                        ? trimmed
                        : (Path.GetDirectoryName(trimmed) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
                };
                Process.Start(psi);
                return true;
            }

            // 5. Fallback: try resolving relative to User or Public Desktop
            foreach (var dir in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                     })
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate = Path.Combine(dir, trimmed);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    Process.Start(new ProcessStartInfo(candidate) { UseShellExecute = true });
                    return true;
                }
            }
        }
        catch
        {
            // Ignore launch failures
        }

        return false;
    }

    public static Bitmap? ExtractIconBitmapSafe(DesktopIconItem item)
    {
        try
        {
            string? path = item.FilePath;
            if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(item.ParsingName) && File.Exists(item.ParsingName))
            {
                path = item.ParsingName;
            }

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                using var ico = Icon.ExtractAssociatedIcon(path);
                if (ico != null)
                {
                    return new Bitmap(ico.ToBitmap(), new Size(16, 16));
                }
            }
        }
        catch
        {
            // Ignore icon extraction errors
        }
        return null;
    }

    #endregion

    #endregion
}
