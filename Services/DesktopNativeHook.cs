using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using MyReDesk.Models;

namespace MyReDesk.Services;

/// <summary>
/// Hooks directly into the Windows Desktop (Progman / WorkerW / SHELLDLL_DefView)
/// supporting both Windows 11 24H2+ (Build 26100/26300 layered Progman child architecture)
/// and classic Windows 10/11 23H2 WorkerW architecture, plus real-time desktop icon drag/drop
/// and filesystem monitoring and Desktop Right-Click Context Menu integration.
/// </summary>
public class DesktopNativeHook : IDisposable
{
    #region Win32 P/Invoke for Progman / WorkerW Desktop Injection

    private const uint WM_SPAWN_WORKER = 0x052C;
    private const uint SMTO_NORMAL = 0x0000;

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int GWLP_HWNDPARENT = -8;
    private const int WS_CHILD = 0x40000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;

    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint LWA_ALPHA = 0x00000002;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    private const int VK_LBUTTON = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    #endregion

    private readonly DeskManagerService _service;
    private readonly DispatcherTimer _desktopPollTimer;
    private FileSystemWatcher? _userDesktopWatcher;
    private FileSystemWatcher? _publicDesktopWatcher;

    private Dictionary<string, (int X, int Y)> _lastKnownIconPositions = new(StringComparer.OrdinalIgnoreCase);
    private bool _pendingDesktopRearrange;
    private DateTime _lastChangeDetectedTime = DateTime.MinValue;
    private bool _isApplyingArrangement;
    public bool IsPausedForSessionOrDisplay { get; set; }

    public DesktopNativeHook(DeskManagerService service)
    {
        _service = service;
        _service.InternalChangeCompleted += NotifyInternalArrangementCompleted;

        CaptureBaselineIconPositions();
        SetupFileSystemWatchers();

        // Poll every 350ms to detect when the user drags & drops an icon on the Windows Desktop
        _desktopPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _desktopPollTimer.Tick += OnDesktopPollTick;
        _desktopPollTimer.Start();
    }

    /// <summary>
    /// Positions and attaches a Gray Box window directly into the Windows Desktop hierarchy
    /// BEHIND SHELLDLL_DefView (SysListView32 desktop icons) and IN FRONT OF WorkerW (desktop wallpaper)
    /// when locked, or detaches to top-level when unlocked so the user can drag & resize it.
    /// </summary>
    public static bool PositionAndAttachZoneWindow(
        IntPtr overlayHwnd,
        int screenX,
        int screenY,
        int widthPx,
        int heightPx,
        byte alpha,
        int cornerRadiusPx,
        bool isLocked)
    {
        if (overlayHwnd == IntPtr.Zero) return false;

        try
        {
            IntPtr progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                SendMessageTimeout(progman, WM_SPAWN_WORKER, new IntPtr(0xD), new IntPtr(1), SMTO_NORMAL, 1000, out _);
                SendMessageTimeout(progman, WM_SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);
            }

            int diameter = Math.Max(4, cornerRadiusPx * 2);

            if (!isLocked)
            {
                // Unlocked / Edit Mode: detach to top-level popup so mouse drag & resize work freely
                if (GetParent(overlayHwnd) != IntPtr.Zero)
                {
                    SetParent(overlayHwnd, IntPtr.Zero);
                }

                int style = GetWindowLong(overlayHwnd, GWL_STYLE);
                style &= ~(WS_CHILD | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
                style |= WS_POPUP | WS_VISIBLE;
                SetWindowLong(overlayHwnd, GWL_STYLE, style);

                int exStyle = GetWindowLong(overlayHwnd, GWL_EXSTYLE);
                exStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
                exStyle &= ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
                SetWindowLong(overlayHwnd, GWL_EXSTYLE, exStyle);

                if (progman != IntPtr.Zero)
                {
                    SetWindowLongPtr64(overlayHwnd, GWLP_HWNDPARENT, progman);
                }

                SetLayeredWindowAttributes(overlayHwnd, 0, alpha, LWA_ALPHA);
                IntPtr hRgnUnlocked = CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, diameter, diameter);
                SetWindowRgn(overlayHwnd, hRgnUnlocked, true);

                SetWindowPos(
                    overlayHwnd,
                    IntPtr.Zero,
                    screenX,
                    screenY,
                    widthPx,
                    heightPx,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
                return true;
            }

            // Locked Mode: Embed BEHIND SHELLDLL_DefView (so desktop icons are ALWAYS in front of the gray box!)
            IntPtr defViewInProgman = progman != IntPtr.Zero
                ? FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null)
                : IntPtr.Zero;
            IntPtr workerWInProgman = progman != IntPtr.Zero
                ? FindWindowEx(progman, IntPtr.Zero, "WorkerW", null)
                : IntPtr.Zero;

            IntPtr hostParent = IntPtr.Zero;
            IntPtr insertAfter = HWND_TOP;

            if (defViewInProgman != IntPtr.Zero && progman != IntPtr.Zero)
            {
                // Windows 11 24H2 (Build 26100/26300+) or unsplit Progman:
                // SHELLDLL_DefView and WorkerW are children inside Progman
                hostParent = progman;
                insertAfter = defViewInProgman;
            }
            else
            {
                // Classic Windows 10 / 11 22H2-23H2: SHELLDLL_DefView is inside a top-level WorkerW,
                // and a second top-level WorkerW sits behind it rendering the wallpaper
                IntPtr iconWorkerW = IntPtr.Zero;
                IntPtr shellDefView = IntPtr.Zero;
                IntPtr classicWorkerW = IntPtr.Zero;

                EnumWindows((topHwnd, _) =>
                {
                    IntPtr dv = FindWindowEx(topHwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (dv != IntPtr.Zero)
                    {
                        iconWorkerW = topHwnd;
                        shellDefView = dv;
                        classicWorkerW = FindWindowEx(IntPtr.Zero, topHwnd, "WorkerW", null);
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                if (classicWorkerW != IntPtr.Zero)
                {
                    hostParent = classicWorkerW;
                    insertAfter = HWND_TOP;
                }
                else if (iconWorkerW != IntPtr.Zero && shellDefView != IntPtr.Zero)
                {
                    hostParent = iconWorkerW;
                    insertAfter = shellDefView;
                }
            }

            if (hostParent != IntPtr.Zero)
            {
                SetWindowLongPtr64(overlayHwnd, GWLP_HWNDPARENT, IntPtr.Zero);

                int style = GetWindowLong(overlayHwnd, GWL_STYLE);
                style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
                style |= WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS;
                SetWindowLong(overlayHwnd, GWL_STYLE, style);

                int exStyle = GetWindowLong(overlayHwnd, GWL_EXSTYLE);
                exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                SetWindowLong(overlayHwnd, GWL_EXSTYLE, exStyle);

                if (GetParent(overlayHwnd) != hostParent)
                {
                    SetParent(overlayHwnd, hostParent);
                }

                SetLayeredWindowAttributes(overlayHwnd, 0, alpha, LWA_ALPHA);
                IntPtr hRgn = CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, diameter, diameter);
                SetWindowRgn(overlayHwnd, hRgn, true);

                POINT clientPt = new() { X = screenX, Y = screenY };
                ScreenToClient(hostParent, ref clientPt);

                if (hostParent == progman && defViewInProgman != IntPtr.Zero)
                {
                    // Keep SHELLDLL_DefView (desktop icons) at the very front of Progman
                    SetWindowPos(defViewInProgman, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }

                SetWindowPos(
                    overlayHwnd,
                    insertAfter,
                    clientPt.X,
                    clientPt.Y,
                    widthPx,
                    heightPx,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);

                if (hostParent == progman && workerWInProgman != IntPtr.Zero)
                {
                    // Keep WorkerW (wallpaper) at the very back of Progman behind all Gray Boxes
                    SetWindowPos(workerWInProgman, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }

                return true;
            }
        }
        catch
        {
            // Fallback below
        }

        return false;
    }

    public static void DetachWindowFromDesktopLayer(IntPtr overlayHwnd)
    {
        if (overlayHwnd == IntPtr.Zero) return;
        try
        {
            SetParent(overlayHwnd, IntPtr.Zero);
            int style = GetWindowLong(overlayHwnd, GWL_STYLE);
            style = (style & ~WS_CHILD) | WS_POPUP;
            SetWindowLong(overlayHwnd, GWL_STYLE, style);
        }
        catch
        {
            // Ignore
        }
    }

    #region Live Desktop Drag-Drop & File Watcher Hook

    public void CaptureBaselineIconPositions()
    {
        try
        {
            var icons = DesktopInterop.GetDesktopIcons();
            _lastKnownIconPositions.Clear();
            foreach (var ic in icons)
            {
                string key = !string.IsNullOrWhiteSpace(ic.ParsingName) ? ic.ParsingName : ic.Name;
                _lastKnownIconPositions[key] = (ic.X, ic.Y);
            }
        }
        catch
        {
            // Ignore
        }
    }

    public void NotifyInternalArrangementStarted() => _isApplyingArrangement = true;

    public void NotifyInternalArrangementCompleted()
    {
        CaptureBaselineIconPositions();
        _pendingDesktopRearrange = false;
        _isApplyingArrangement = false;
    }

    private void SetupFileSystemWatchers()
    {
        try
        {
            string userDesk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (Directory.Exists(userDesk))
            {
                _userDesktopWatcher = new FileSystemWatcher(userDesk)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                _userDesktopWatcher.Created += OnDesktopFileChanged;
                _userDesktopWatcher.Deleted += OnDesktopFileChanged;
                _userDesktopWatcher.Renamed += OnDesktopFileChanged;
            }

            string pubDesk = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (!string.IsNullOrWhiteSpace(pubDesk) && Directory.Exists(pubDesk))
            {
                _publicDesktopWatcher = new FileSystemWatcher(pubDesk)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                _publicDesktopWatcher.Created += OnDesktopFileChanged;
                _publicDesktopWatcher.Deleted += OnDesktopFileChanged;
                _publicDesktopWatcher.Renamed += OnDesktopFileChanged;
            }
        }
        catch
        {
            // Ignore watcher errors
        }
    }

    private void OnDesktopFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_isApplyingArrangement || _service.IsApplyingInternalChange || !_service.State.AutoArrangeOnDesktopChange) return;
        _pendingDesktopRearrange = true;
        _lastChangeDetectedTime = DateTime.Now;
    }

    private void OnDesktopPollTick(object? sender, EventArgs e)
    {
        if (IsPausedForSessionOrDisplay || _isApplyingArrangement || _service.IsApplyingInternalChange || !_service.State.AutoArrangeOnDesktopChange)
            return;

        // Never interrupt the user while the left mouse button is held down (e.g. mid-drag!)
        bool isMouseButtonDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (isMouseButtonDown)
            return;

        try
        {
            var currentIcons = DesktopInterop.GetDesktopIcons();
            if (currentIcons.Count == 0) return;

            bool movedOrCountChanged = currentIcons.Count != _lastKnownIconPositions.Count;
            if (!movedOrCountChanged)
            {
                foreach (var ic in currentIcons)
                {
                    string key = !string.IsNullOrWhiteSpace(ic.ParsingName) ? ic.ParsingName : ic.Name;
                    if (!_lastKnownIconPositions.TryGetValue(key, out var oldPos) ||
                        Math.Abs(oldPos.X - ic.X) > 12 ||
                        Math.Abs(oldPos.Y - ic.Y) > 12)
                    {
                        movedOrCountChanged = true;
                        break;
                    }
                }
            }

            if (movedOrCountChanged)
            {
                _pendingDesktopRearrange = true;
                _lastChangeDetectedTime = DateTime.Now;
                // Update baseline so we wait for a brief settle period (450ms) after the user drops the icon
                _lastKnownIconPositions.Clear();
                foreach (var ic in currentIcons)
                {
                    string key = !string.IsNullOrWhiteSpace(ic.ParsingName) ? ic.ParsingName : ic.Name;
                    _lastKnownIconPositions[key] = (ic.X, ic.Y);
                }
                return;
            }

            if (_pendingDesktopRearrange && (DateTime.Now - _lastChangeDetectedTime).TotalMilliseconds >= 450)
            {
                _pendingDesktopRearrange = false;
                _isApplyingArrangement = true;
                try
                {
                    _service.ApplyAndSaveChange();
                    CaptureBaselineIconPositions();
                }
                finally
                {
                    _isApplyingArrangement = false;
                }
            }
        }
        catch
        {
            // Ignore transient Explorer COM busy states
        }
    }

    #endregion

    #region Windows Desktop Right-Click Context Menu Hook (Hierarchical Monitor -> Desktop Groups & Gray Boxes)

    private const string DesktopMenuKeyPath = @"Software\Classes\Directory\Background\shell\DesktopOrg";
    private const string LegacyDesktopMenuKeyPath = @"Software\Classes\Directory\Background\shell\MyReDesk";

    public static bool IsDesktopContextMenuEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DesktopMenuKeyPath, false);
            if (key != null) return true;
            using var legacyKey = Registry.CurrentUser.OpenSubKey(LegacyDesktopMenuKeyPath, false);
            return legacyKey != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetDesktopContextMenuEnabled(bool enable, DeskManagerService? service = null)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(LegacyDesktopMenuKeyPath, throwOnMissingSubKey: false);

            if (enable)
            {
                if (service != null)
                {
                    SyncDesktopContextMenuRegistry(service);
                }
                else
                {
                    string exePath = DeskManagerService.GetPreferredExecutablePath();
                    if (string.IsNullOrWhiteSpace(exePath)) return;

                    Registry.CurrentUser.DeleteSubKeyTree(DesktopMenuKeyPath, throwOnMissingSubKey: false);
                    using var rootKey = Registry.CurrentUser.CreateSubKey(DesktopMenuKeyPath);
                    if (rootKey == null) return;

                    rootKey.SetValue("MUIVerb", "DesktopOrg");
                    rootKey.SetValue("Icon", $"\"{exePath}\",0");
                    rootKey.SetValue("Position", "Top");
                    rootKey.SetValue("SubCommands", "");

                    using var shellKey = rootKey.CreateSubKey("shell");
                    if (shellKey != null)
                    {
                        WriteCommandVerb(shellKey, "00_OpenSettings", "Open DesktopOrg Settings", $"\"{exePath}\",0", $"\"{exePath}\"");
                        WriteCommandVerb(shellKey, "01_ApplyNow", "Apply && Organize Desktop Now", $"\"{exePath}\",0", $"\"{exePath}\" --organize");
                    }
                }
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(DesktopMenuKeyPath, throwOnMissingSubKey: false);
            }
        }
        catch
        {
            // Ignore registry errors
        }
    }

    public static void SyncDesktopContextMenuRegistry(DeskManagerService service)
    {
        try
        {
            string exePath = DeskManagerService.GetPreferredExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath)) return;

            Registry.CurrentUser.DeleteSubKeyTree(LegacyDesktopMenuKeyPath, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(DesktopMenuKeyPath, throwOnMissingSubKey: false);
            using var rootKey = Registry.CurrentUser.CreateSubKey(DesktopMenuKeyPath);
            if (rootKey == null) return;

            rootKey.SetValue("MUIVerb", "DesktopOrg");
            rootKey.SetValue("Icon", $"\"{exePath}\",0");
            rootKey.SetValue("Position", "Top");
            rootKey.SetValue("SubCommands", "");

            using var rootShell = rootKey.CreateSubKey("shell");
            if (rootShell == null) return;

            // IMPORTANT: Windows Explorer (shell32.dll CRegistryVerbsContextMenu) enforces a hard limit
            // of 16 total registry subkeys across the ENTIRE cascading tree under a single root verb!
            // If a tree exceeds 16 subkeys, Explorer silently truncates all subsequent subkeys and monitors.
            // We track totalSubKeysUsed (capped at 15) and wire each group/box to "--popup <spec>" so
            // clicking any group or box immediately opens the unlimited, icon-rendered native menu at the cursor.
            int totalSubKeysUsed = 0;
            const int MaxTotalExplorerSubKeys = 15;

            var allMonitors = service.GetAllMonitorsIncludingCachedOffline();

            // Order monitors so Primary and monitors with items/boxes get submenu budget first,
            // while preserving monitor number order in key names when possible.
            var monitorsWithCounts = allMonitors.Select(mon =>
            {
                service.State.MonitorCaches.TryGetValue(mon.DeviceName, out var cache);
                var appShortcuts = cache?.AppShortcuts ?? new List<DesktopIconItem>();
                var foldersAndFiles = cache?.FoldersAndFiles ?? new List<DesktopIconItem>();
                var webLinks = cache?.WebLinks ?? new List<DesktopIconItem>();
                var areasOnMon = service.State.CustomAreas
                    .Where(a => string.Equals(a.MonitorDeviceName, mon.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                                (mon.IsPrimary && string.IsNullOrWhiteSpace(a.MonitorDeviceName)))
                    .ToList();
                int itemCount = appShortcuts.Count + foldersAndFiles.Count + webLinks.Count + areasOnMon.Sum(a => a.CachedItems.Count);
                return (Monitor: mon, AppShortcuts: appShortcuts, FoldersAndFiles: foldersAndFiles, WebLinks: webLinks, Areas: areasOnMon, TotalCount: itemCount + areasOnMon.Count);
            }).ToList();

            // Reserve 2 keys at bottom for Apply & Settings + 1 key per monitor
            int baseKeysNeeded = 2 + monitorsWithCounts.Count;
            int remainingChildBudget = Math.Max(0, MaxTotalExplorerSubKeys - baseKeysNeeded);

            // Sort monitors so Primary monitor appears first in the menu, followed by Monitor 1, 2, 3, 4
            var orderedMonitors = monitorsWithCounts
                .OrderByDescending(m => m.Monitor.IsPrimary)
                .ThenBy(m => m.Monitor.MonitorNumber)
                .ToList();

            int monOrderIdx = 1;
            foreach (var entry in orderedMonitors)
            {
                if (totalSubKeysUsed >= MaxTotalExplorerSubKeys - 2)
                    break;

                var mon = entry.Monitor;
                string safeDev = mon.DeviceName.Replace("\"", "");
                bool expandMonitorChildren = (entry.TotalCount > 0 || mon.IsPrimary) && remainingChildBudget >= 4;

                if (expandMonitorChildren)
                {
                    using var monKey = CreateSubMenuKey(
                        rootShell,
                        $"10_Mon_{monOrderIdx:D2}",
                        EscapeMuiVerb(mon.FriendlyName),
                        "shell32.dll,15",
                        separatorBefore: monOrderIdx == 1);
                    totalSubKeysUsed++;

                    using var monShell = monKey?.CreateSubKey("shell");
                    if (monShell != null)
                    {
                        // 1. Desktop Groups on this monitor: App Shortcuts, Folders & Local Files, Internet Shortcuts (.url)
                        WriteCommandVerb(
                            monShell,
                            "01_AppShortcuts",
                            $"App Shortcuts ({entry.AppShortcuts.Count})",
                            "shell32.dll,2",
                            $"\"{exePath}\" --popup \"mon:{safeDev}|group:AppShortcuts\"");
                        totalSubKeysUsed++;
                        remainingChildBudget--;

                        WriteCommandVerb(
                            monShell,
                            "02_FoldersAndFiles",
                            $"Folders && Local Files ({entry.FoldersAndFiles.Count})",
                            "shell32.dll,3",
                            $"\"{exePath}\" --popup \"mon:{safeDev}|group:FoldersAndFiles\"");
                        totalSubKeysUsed++;
                        remainingChildBudget--;

                        WriteCommandVerb(
                            monShell,
                            "03_InternetShortcuts",
                            $"Internet Shortcuts (.url) ({entry.WebLinks.Count})",
                            "shell32.dll,13",
                            $"\"{exePath}\" --popup \"mon:{safeDev}|group:WebLinks\"");
                        totalSubKeysUsed++;
                        remainingChildBudget--;

                        // 2. Gray Boxes on this monitor
                        if (entry.Areas.Count > 0)
                        {
                            // If budget allows listing each Gray Box individually, list each one; otherwise list combined Gray Boxes item
                            if (remainingChildBudget >= entry.Areas.Count)
                            {
                                int boxIdx = 1;
                                foreach (var area in entry.Areas)
                                {
                                    string vdSuffix = !string.IsNullOrWhiteSpace(area.VirtualDesktopId) && area.VirtualDesktopId != "*"
                                        ? $" [{area.VirtualDesktopName}]"
                                        : "";
                                    WriteCommandVerb(
                                        monShell,
                                        $"10_Box_{boxIdx:D2}",
                                        $"Gray Box: {EscapeMuiVerb(area.Title)} ({area.CachedItems.Count}){vdSuffix}",
                                        "shell32.dll,43",
                                        $"\"{exePath}\" --popup \"mon:{safeDev}|box:{area.Id}\"",
                                        separatorBefore: boxIdx == 1);
                                    totalSubKeysUsed++;
                                    remainingChildBudget--;
                                    boxIdx++;
                                }
                            }
                            else
                            {
                                string boxNames = string.Join(", ", entry.Areas.Select(a => a.Title));
                                WriteCommandVerb(
                                    monShell,
                                    "10_AllBoxes",
                                    $"Gray Boxes ({entry.Areas.Count}: {EscapeMuiVerb(TruncateShort(boxNames, 28))})",
                                    "shell32.dll,43",
                                    $"\"{exePath}\" --popup \"mon:{safeDev}|group:GrayBoxes\"",
                                    separatorBefore: true);
                                totalSubKeysUsed++;
                                remainingChildBudget--;
                            }
                        }
                    }
                }
                else
                {
                    int totalMonItems = entry.AppShortcuts.Count + entry.FoldersAndFiles.Count + entry.WebLinks.Count;
                    string summarySuffix = $" — {totalMonItems} items, {entry.Areas.Count} boxes";
                    WriteCommandVerb(
                        rootShell,
                        $"10_Mon_{monOrderIdx:D2}",
                        EscapeMuiVerb(mon.FriendlyName + summarySuffix),
                        "shell32.dll,15",
                        $"\"{exePath}\" --popup \"mon:{safeDev}|group:All\"",
                        separatorBefore: monOrderIdx == 1);
                    totalSubKeysUsed++;
                }

                monOrderIdx++;
            }

            WriteCommandVerb(
                rootShell,
                "90_ApplyNow",
                "Apply && Organize Desktop Now",
                $"\"{exePath}\",0",
                $"\"{exePath}\" --organize",
                separatorBefore: true);

            WriteCommandVerb(
                rootShell,
                "91_OpenSettings",
                "Open DesktopOrg Settings",
                $"\"{exePath}\",0",
                $"\"{exePath}\"");
        }
        catch
        {
            // Ignore registry errors
        }
    }

    private static void WriteCascadingItemsMenu(
        RegistryKey parentShell,
        List<DesktopIconItem> items,
        string exePath,
        bool groupByDomain)
    {
        if (items == null || items.Count == 0)
        {
            WriteCommandVerb(parentShell, "00_Empty", "(Empty)", $"\"{exePath}\",0", $"\"{exePath}\"");
            return;
        }

        if (groupByDomain && items.Count > 6)
        {
            var domainGroups = items
                .GroupBy(i => string.IsNullOrWhiteSpace(i.WebDomain) ? "other" : i.WebDomain.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (domainGroups.Count <= 14)
            {
                int dIdx = 1;
                foreach (var dg in domainGroups)
                {
                    using var domKey = CreateSubMenuKey(parentShell, $"Dom_{dIdx:D2}", $"{EscapeMuiVerb(dg.Key)} ({dg.Count()})", "shell32.dll,13");
                    using var domShell = domKey?.CreateSubKey("shell");
                    if (domShell != null)
                    {
                        WriteCascadingItemsMenu(domShell, dg.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(), exePath, groupByDomain: false);
                    }
                    dIdx++;
                }
                return;
            }
            else
            {
                // Top 11 domains directly + "Other Domains" bucket so we stay under Windows Explorer's 16-item per-key limit
                int dIdx = 1;
                foreach (var dg in domainGroups.Take(11))
                {
                    using var domKey = CreateSubMenuKey(parentShell, $"Dom_{dIdx:D2}", $"{EscapeMuiVerb(dg.Key)} ({dg.Count()})", "shell32.dll,13");
                    using var domShell = domKey?.CreateSubKey("shell");
                    if (domShell != null)
                    {
                        WriteCascadingItemsMenu(domShell, dg.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(), exePath, groupByDomain: false);
                    }
                    dIdx++;
                }

                var remainingItems = domainGroups.Skip(11).SelectMany(g => g).OrderBy(i => i.WebDomain).ThenBy(i => i.Name).ToList();
                using var otherKey = CreateSubMenuKey(parentShell, "Dom_99_Other", $"Other Domains ({remainingItems.Count})", "shell32.dll,13");
                using var otherShell = otherKey?.CreateSubKey("shell");
                if (otherShell != null)
                {
                    WriteCascadingItemsMenu(otherShell, remainingItems, exePath, groupByDomain: false);
                }
                return;
            }
        }

        if (items.Count <= 14)
        {
            int idx = 1;
            foreach (var item in items)
            {
                WriteDesktopItemLeafVerb(parentShell, $"Item_{idx:D2}", item, exePath);
                idx++;
            }
        }
        else
        {
            // Chunk into buckets of 12 items so Windows Explorer displays all of them cleanly
            int bucketSize = 12;
            int totalBuckets = (int)Math.Ceiling((double)items.Count / bucketSize);
            for (int b = 0; b < Math.Min(14, totalBuckets); b++)
            {
                var chunk = items.Skip(b * bucketSize).Take(bucketSize).ToList();
                if (chunk.Count == 0) break;

                string firstLabel = TruncateShort(chunk.First().Name, 12);
                string lastLabel = TruncateShort(chunk.Last().Name, 12);
                string bucketTitle = $"{EscapeMuiVerb(firstLabel)} .. {EscapeMuiVerb(lastLabel)} ({chunk.Count})";

                using var bucketKey = CreateSubMenuKey(parentShell, $"Bucket_{b + 1:D2}", bucketTitle, "shell32.dll,3");
                using var bucketShell = bucketKey?.CreateSubKey("shell");
                if (bucketShell != null)
                {
                    int idx = 1;
                    foreach (var item in chunk)
                    {
                        WriteDesktopItemLeafVerb(bucketShell, $"Item_{idx:D2}", item, exePath);
                        idx++;
                    }
                }
            }
        }
    }

    private static void WriteDesktopItemLeafVerb(
        RegistryKey parentShell,
        string subKeyName,
        DesktopIconItem item,
        string exePath)
    {
        string target = item.IsWebLink && !string.IsNullOrWhiteSpace(item.TargetUrl)
            ? item.TargetUrl
            : (!string.IsNullOrWhiteSpace(item.FilePath) ? item.FilePath : item.ParsingName);

        string label = item.IsWebLink && !string.IsNullOrWhiteSpace(item.WebDomain)
            ? $"{EscapeMuiVerb(TruncateShort(item.Name, 44))} [{EscapeMuiVerb(item.WebDomain)}]"
            : EscapeMuiVerb(TruncateShort(item.Name, 52));

        string iconSpec = "shell32.dll,2";
        if (!string.IsNullOrWhiteSpace(item.FilePath))
        {
            if (Directory.Exists(item.FilePath))
                iconSpec = "shell32.dll,3";
            else if (item.IsWebLink)
                iconSpec = "shell32.dll,13";
            else if (File.Exists(item.FilePath))
                iconSpec = $"\"{item.FilePath}\",0";
        }
        else if (item.IsSystemOrVirtual)
        {
            iconSpec = "shell32.dll,15";
        }

        string safeTarget = target.Replace("\"", "");
        WriteCommandVerb(parentShell, subKeyName, label, iconSpec, $"\"{exePath}\" --launch \"{safeTarget}\"");
    }

    private static RegistryKey? CreateSubMenuKey(
        RegistryKey parentShell,
        string keyName,
        string muiVerb,
        string icon,
        bool separatorBefore = false)
    {
        var key = parentShell.CreateSubKey(keyName);
        if (key == null) return null;
        key.SetValue("MUIVerb", muiVerb);
        if (!string.IsNullOrWhiteSpace(icon))
            key.SetValue("Icon", icon);
        key.SetValue("SubCommands", "");
        if (separatorBefore)
            key.SetValue("CommandFlags", 0x20, RegistryValueKind.DWord);
        return key;
    }

    private static void WriteCommandVerb(
        RegistryKey parentShell,
        string keyName,
        string muiVerb,
        string icon,
        string commandLine,
        bool separatorBefore = false)
    {
        using var key = parentShell.CreateSubKey(keyName);
        if (key == null) return;
        key.SetValue("MUIVerb", muiVerb);
        if (!string.IsNullOrWhiteSpace(icon))
            key.SetValue("Icon", icon);
        if (separatorBefore)
            key.SetValue("CommandFlags", 0x20, RegistryValueKind.DWord);
        using var cmd = key.CreateSubKey("command");
        cmd?.SetValue("", commandLine);
    }

    private static string EscapeMuiVerb(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Item";
        return text.Replace("&", "&&");
    }

    private static string TruncateShort(string text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return text.Length <= maxLen ? text : text.Substring(0, maxLen - 1) + "…";
    }

    #endregion

    public void Dispose()
    {
        _desktopPollTimer.Stop();
        _userDesktopWatcher?.Dispose();
        _publicDesktopWatcher?.Dispose();
    }
}
