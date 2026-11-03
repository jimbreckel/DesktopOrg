using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using MyReDesk.Models;
using MyReDesk.Services;
using MyReDesk.Views;
using Forms = System.Windows.Forms;

namespace MyReDesk;

public partial class App : System.Windows.Application
{
    public static bool IsExitingApplication { get; private set; }

    private const string SingleInstanceMutexName = "Local\\DesktopOrg_SingleInstance_Mutex_v2";
    private const string ShowWindowEventName = "Local\\DesktopOrg_ShowWindow_Event_v2";
    private const string PopupMenuEventName = "Local\\DesktopOrg_PopupMenu_Event_v2";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private EventWaitHandle? _popupMenuEvent;
    private Forms.Form? _popupHostForm;
    private Forms.ContextMenuStrip? _activePopupMenu;
    private DeskManagerService _service = null!;
    private DesktopNativeHook? _desktopHook;
    private MainWindow _mainWindow = null!;
    private Forms.NotifyIcon? _trayIcon;
    private readonly Dictionary<string, DeskZoneWindow> _zoneWindows = new();
    private System.Windows.Threading.DispatcherTimer? _keepAliveOverlayTimer;
    private System.Windows.Threading.DispatcherTimer? _displaySettleTimer;
    private string _lastVirtualDesktopId = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string popupSpecFile = System.IO.Path.Combine(DeskManagerService.AppDataFolder, "popup_request.txt");

        // 1. Handle "--launch <target>" or "--popup <spec>" invoked from the Windows Desktop Right-Click Context Menu
        string? requestedPopupSpec = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (string.Equals(e.Args[i], "--launch", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
            {
                string launchTarget = string.Join(" ", e.Args.Skip(i + 1));
                DesktopInterop.LaunchTarget(launchTarget);
                Shutdown();
                return;
            }
            if (string.Equals(e.Args[i], "--popup", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
            {
                requestedPopupSpec = e.Args[i + 1];
            }
        }

        string triggerFile = System.IO.Path.Combine(DeskManagerService.AppDataFolder, "organize_now.trigger");
        bool isOrganizeArg = e.Args.Any(a =>
            a.Equals("--organize", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--apply", StringComparison.OrdinalIgnoreCase));

        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            if (!string.IsNullOrWhiteSpace(requestedPopupSpec))
            {
                try
                {
                    System.IO.Directory.CreateDirectory(DeskManagerService.AppDataFolder);
                    System.IO.File.WriteAllText(popupSpecFile, requestedPopupSpec);
                    using var popEvt = EventWaitHandle.OpenExisting(PopupMenuEventName);
                    popEvt.Set();
                }
                catch
                {
                    // Ignore
                }
                Shutdown();
                return;
            }

            if (isOrganizeArg)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(DeskManagerService.AppDataFolder);
                    System.IO.File.WriteAllText(triggerFile, DateTime.Now.ToString("O"));
                }
                catch
                {
                    // Ignore
                }
                Shutdown();
                return;
            }

            // Signal the already-running instance to show its main window (e.g. when launched from Desktop Right-Click Menu)
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ShowWindowEventName);
                evt.Set();
            }
            catch
            {
                // Ignore
            }
            Shutdown();
            return;
        }

        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _popupMenuEvent = new EventWaitHandle(false, EventResetMode.AutoReset, PopupMenuEventName);
        StartShowWindowSignalListener(popupSpecFile);

        _service = new DeskManagerService();
        _service.AreasChanged += SyncOverlayWindows;
        _service.StateChanged += RebuildTrayContextMenu;

        // Refresh monitor caches with the fixed coordinate origins so menus and preview dots are immediately 100% accurate
        _service.RefreshMonitorCachesFromCurrentDesktop();
        _service.SaveState();

        if (_service.State.UseDesktopContextMenu)
        {
            DesktopNativeHook.SetDesktopContextMenuEnabled(true, _service);
        }

        _desktopHook = new DesktopNativeHook(_service);

        _mainWindow = new MainWindow(_service);
        MainWindow = _mainWindow;

        _lastVirtualDesktopId = DesktopInterop.GetCurrentVirtualDesktopId();
        InitializeTrayIcon();
        SyncOverlayWindows();

        // Keep desktop gray box overlays visible on the desktop even if the user presses Win+D (Show Desktop),
        // and watch for Windows 10/11 Virtual Desktop switches so Virtual Desktop-scoped Gray Boxes swap immediately.
        _keepAliveOverlayTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _keepAliveOverlayTimer.Tick += (_, _) =>
        {
            string curVd = DesktopInterop.GetCurrentVirtualDesktopId();
            if (!string.Equals(curVd, _lastVirtualDesktopId, StringComparison.OrdinalIgnoreCase))
            {
                _lastVirtualDesktopId = curVd;
                SyncOverlayWindows();
                RebuildTrayContextMenu();
            }

            if (_service.State.AreZonesLocked)
            {
                foreach (var win in _zoneWindows.Values)
                {
                    win.EnsureVisibleOnDesktop();
                }
            }

            if (System.IO.File.Exists(triggerFile))
            {
                try { System.IO.File.Delete(triggerFile); } catch { }
                _desktopHook?.NotifyInternalArrangementStarted();
                try
                {
                    _service.ApplyAndSaveChange();
                    _mainWindow.RefreshDesktopIconsCache();
                    _mainWindow.PopulateAllUi();
                }
                finally
                {
                    _desktopHook?.NotifyInternalArrangementCompleted();
                }
            }
        };
        _keepAliveOverlayTimer.Start();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        _displaySettleTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _displaySettleTimer.Tick += (_, _) =>
        {
            _displaySettleTimer.Stop();
            if (_desktopHook != null) _desktopHook.IsPausedForSessionOrDisplay = true;
            try
            {
                _service.CalibrateDesktopGrid();
                _service.TryAutoRestoreForCurrentMonitors();
                SyncOverlayWindows();
                _mainWindow.RefreshDesktopIconsCache();
                _mainWindow.PopulateAllUi();
            }
            finally
            {
                if (_desktopHook != null)
                {
                    _desktopHook.CaptureBaselineIconPositions();
                    _desktopHook.IsPausedForSessionOrDisplay = false;
                }
            }
        };

        if (isOrganizeArg || System.IO.File.Exists(triggerFile))
        {
            try { if (System.IO.File.Exists(triggerFile)) System.IO.File.Delete(triggerFile); } catch { }
            _desktopHook?.NotifyInternalArrangementStarted();
            try
            {
                _service.ApplyAndSaveChange();
                _mainWindow.RefreshDesktopIconsCache();
                _mainWindow.PopulateAllUi();
            }
            finally
            {
                _desktopHook?.NotifyInternalArrangementCompleted();
            }
        }

        bool startInTray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase))
                           || !string.IsNullOrWhiteSpace(requestedPopupSpec)
                           || _service.State.StartMinimizedToTray;

        if (!startInTray)
        {
            _mainWindow.Show();
        }

        if (!string.IsNullOrWhiteSpace(requestedPopupSpec))
        {
            Dispatcher.InvokeAsync(() => ShowPopupMenuAtCursor(requestedPopupSpec));
        }
    }

    private void StartShowWindowSignalListener(string popupSpecFile)
    {
        Task.Run(() =>
        {
            var handles = new WaitHandle[] { _showWindowEvent!, _popupMenuEvent! };
            while (!IsExitingApplication)
            {
                int signaled = WaitHandle.WaitAny(handles, 1000);
                if (IsExitingApplication) break;

                if (signaled == 0)
                {
                    ShowMainWindow();
                }
                else if (signaled == 1)
                {
                    string spec = "all";
                    try
                    {
                        if (System.IO.File.Exists(popupSpecFile))
                        {
                            spec = System.IO.File.ReadAllText(popupSpecFile).Trim();
                        }
                    }
                    catch
                    {
                        // Ignore
                    }
                    Dispatcher.InvokeAsync(() => ShowPopupMenuAtCursor(spec));
                }
            }
        });
    }

    private void ShowPopupMenuAtCursor(string spec)
    {
        try
        {
            _activePopupMenu?.Dispose();
            var popup = new Forms.ContextMenuStrip();

            string targetDevice = string.Empty;
            string groupType = "All";
            string boxId = string.Empty;

            foreach (var part in spec.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("mon:", StringComparison.OrdinalIgnoreCase))
                    targetDevice = part.Substring(4).Trim();
                else if (part.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
                    groupType = part.Substring(6).Trim();
                else if (part.StartsWith("box:", StringComparison.OrdinalIgnoreCase))
                    boxId = part.Substring(4).Trim();
            }

            var allMonitors = _service.GetAllMonitorsIncludingCachedOffline();
            var mon = allMonitors.FirstOrDefault(m => string.Equals(m.DeviceName, targetDevice, StringComparison.OrdinalIgnoreCase))
                      ?? allMonitors.FirstOrDefault(m => m.IsPrimary)
                      ?? allMonitors.FirstOrDefault();

            if (mon != null)
            {
                _service.State.MonitorCaches.TryGetValue(mon.DeviceName, out var cache);
                var appShortcuts = cache?.AppShortcuts ?? new List<DesktopIconItem>();
                var foldersAndFiles = cache?.FoldersAndFiles ?? new List<DesktopIconItem>();
                var webLinks = cache?.WebLinks ?? new List<DesktopIconItem>();
                var areasOnMon = _service.State.CustomAreas
                    .Where(a => string.Equals(a.MonitorDeviceName, mon.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                                (mon.IsPrimary && string.IsNullOrWhiteSpace(a.MonitorDeviceName)))
                    .ToList();

                if (!string.IsNullOrWhiteSpace(boxId))
                {
                    var area = areasOnMon.FirstOrDefault(a => string.Equals(a.Id, boxId, StringComparison.OrdinalIgnoreCase))
                               ?? _service.State.CustomAreas.FirstOrDefault(a => string.Equals(a.Id, boxId, StringComparison.OrdinalIgnoreCase));
                    if (area != null)
                    {
                        var header = new Forms.ToolStripMenuItem($"{mon.FriendlyName} — Gray Box: {area.Title} ({area.CachedItems.Count})")
                        {
                            Font = new Font(popup.Font, System.Drawing.FontStyle.Bold),
                            Enabled = false
                        };
                        popup.Items.Add(header);
                        popup.Items.Add(new Forms.ToolStripSeparator());
                        PopulateContextMenuRootItems(popup, area.CachedItems, groupByDomain: false);
                    }
                }
                else if (string.Equals(groupType, "AppShortcuts", StringComparison.OrdinalIgnoreCase))
                {
                    var header = new Forms.ToolStripMenuItem($"{mon.FriendlyName} — App Shortcuts ({appShortcuts.Count})")
                    {
                        Font = new Font(popup.Font, System.Drawing.FontStyle.Bold),
                        Enabled = false
                    };
                    popup.Items.Add(header);
                    popup.Items.Add(new Forms.ToolStripSeparator());
                    PopulateContextMenuRootItems(popup, appShortcuts, groupByDomain: false);
                }
                else if (string.Equals(groupType, "FoldersAndFiles", StringComparison.OrdinalIgnoreCase))
                {
                    var header = new Forms.ToolStripMenuItem($"{mon.FriendlyName} — Folders & Local Files ({foldersAndFiles.Count})")
                    {
                        Font = new Font(popup.Font, System.Drawing.FontStyle.Bold),
                        Enabled = false
                    };
                    popup.Items.Add(header);
                    popup.Items.Add(new Forms.ToolStripSeparator());
                    PopulateContextMenuRootItems(popup, foldersAndFiles, groupByDomain: false);
                }
                else if (string.Equals(groupType, "WebLinks", StringComparison.OrdinalIgnoreCase))
                {
                    var header = new Forms.ToolStripMenuItem($"{mon.FriendlyName} — Internet Shortcuts (.url) ({webLinks.Count})")
                    {
                        Font = new Font(popup.Font, System.Drawing.FontStyle.Bold),
                        Enabled = false
                    };
                    popup.Items.Add(header);
                    popup.Items.Add(new Forms.ToolStripSeparator());
                    PopulateContextMenuRootItems(popup, webLinks, groupByDomain: true);
                }
                else if (string.Equals(groupType, "GrayBoxes", StringComparison.OrdinalIgnoreCase))
                {
                    var header = new Forms.ToolStripMenuItem($"{mon.FriendlyName} — Gray Boxes ({areasOnMon.Count})")
                    {
                        Font = new Font(popup.Font, System.Drawing.FontStyle.Bold),
                        Enabled = false
                    };
                    popup.Items.Add(header);
                    popup.Items.Add(new Forms.ToolStripSeparator());
                    if (areasOnMon.Count == 0)
                    {
                        popup.Items.Add(new Forms.ToolStripMenuItem("(No Gray Boxes on this monitor)") { Enabled = false });
                    }
                    else
                    {
                        foreach (var area in areasOnMon)
                        {
                            var boxSubMenu = new Forms.ToolStripMenuItem($"Gray Box: {area.Title} ({area.CachedItems.Count})");
                            PopulateTrayDesktopItemSubmenu(boxSubMenu, area.CachedItems, groupByDomain: false);
                            popup.Items.Add(boxSubMenu);
                        }
                    }
                }
                else
                {
                    var header = new Forms.ToolStripMenuItem(mon.FriendlyName)
                    {
                        Font = new Font(popup.Font, System.Drawing.FontStyle.Bold),
                        Enabled = false
                    };
                    popup.Items.Add(header);
                    popup.Items.Add(new Forms.ToolStripSeparator());
                    PopulateMonitorDropDownItems(popup.Items, mon, appShortcuts, foldersAndFiles, webLinks, areasOnMon);
                }
            }

            if (_popupHostForm == null || _popupHostForm.IsDisposed)
            {
                _popupHostForm = new Forms.Form
                {
                    FormBorderStyle = Forms.FormBorderStyle.None,
                    ShowInTaskbar = false,
                    StartPosition = Forms.FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-30000, -30000),
                    Size = new System.Drawing.Size(1, 1),
                    Opacity = 0
                };
                _popupHostForm.Show();
            }

            _activePopupMenu = popup;
            var cursorPos = Forms.Cursor.Position;
            SetForegroundWindow(_popupHostForm.Handle);
            popup.Show(cursorPos);
        }
        catch
        {
            // Ignore popup errors
        }
    }

    private void PopulateContextMenuRootItems(
        Forms.ContextMenuStrip popup,
        List<DesktopIconItem> items,
        bool groupByDomain)
    {
        if (items == null || items.Count == 0)
        {
            popup.Items.Add(new Forms.ToolStripMenuItem("(Empty)") { Enabled = false });
            return;
        }

        if (groupByDomain && items.Count > 5)
        {
            var domainGroups = items
                .GroupBy(i => string.IsNullOrWhiteSpace(i.WebDomain) ? "other" : i.WebDomain.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var dg in domainGroups)
            {
                var domMenu = new Forms.ToolStripMenuItem($"{dg.Key} ({dg.Count()})");
                foreach (var linkItem in dg.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                {
                    domMenu.DropDownItems.Add(CreateLaunchableTrayItem(linkItem));
                }
                popup.Items.Add(domMenu);
            }

            popup.Items.Add(new Forms.ToolStripSeparator());
            var allLinksMenu = new Forms.ToolStripMenuItem($"All Internet Shortcuts (A-Z) ({items.Count})");
            foreach (var linkItem in items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                allLinksMenu.DropDownItems.Add(CreateLaunchableTrayItem(linkItem));
            }
            popup.Items.Add(allLinksMenu);
            return;
        }

        foreach (var item in items)
        {
            popup.Items.Add(CreateLaunchableTrayItem(item));
        }
    }

    private void PopulateMonitorDropDownItems(
        Forms.ToolStripItemCollection targetCollection,
        MonitorInfo mon,
        List<DesktopIconItem> appShortcuts,
        List<DesktopIconItem> foldersAndFiles,
        List<DesktopIconItem> webLinks,
        List<CustomAreaConfig> areasOnMon)
    {
        // 1. Desktop Groups directly under the Monitor
        var appShortcutsMenu = new Forms.ToolStripMenuItem($"App Shortcuts ({appShortcuts.Count})");
        PopulateTrayDesktopItemSubmenu(appShortcutsMenu, appShortcuts, groupByDomain: false);
        targetCollection.Add(appShortcutsMenu);

        var foldersFilesMenu = new Forms.ToolStripMenuItem($"Folders & Local Files ({foldersAndFiles.Count})");
        PopulateTrayDesktopItemSubmenu(foldersFilesMenu, foldersAndFiles, groupByDomain: false);
        targetCollection.Add(foldersFilesMenu);

        var webLinksMenu = new Forms.ToolStripMenuItem($"Internet Shortcuts (.url) ({webLinks.Count})");
        PopulateTrayDesktopItemSubmenu(webLinksMenu, webLinks, groupByDomain: true);
        targetCollection.Add(webLinksMenu);

        // 2. Each Gray Box on this Monitor
        targetCollection.Add(new Forms.ToolStripSeparator());
        if (areasOnMon.Count == 0)
        {
            var emptyBoxItem = new Forms.ToolStripMenuItem("(No Gray Boxes on this monitor)") { Enabled = false };
            targetCollection.Add(emptyBoxItem);
        }
        else
        {
            foreach (var area in areasOnMon)
            {
                string vdTag = !string.IsNullOrWhiteSpace(area.VirtualDesktopId) && area.VirtualDesktopId != "*"
                    ? $" [{area.VirtualDesktopName}]"
                    : "";
                var boxSubMenu = new Forms.ToolStripMenuItem($"Gray Box: {area.Title} ({area.CachedItems.Count}){vdTag}");
                PopulateTrayDesktopItemSubmenu(boxSubMenu, area.CachedItems, groupByDomain: false);

                boxSubMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
                var visItem = new Forms.ToolStripMenuItem(area.IsVisible ? "✓ Show This Gray Box" : "Show This Gray Box");
                visItem.Click += (_, _) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        area.IsVisible = !area.IsVisible;
                        _service.SaveState();
                        SyncOverlayWindows();
                        RebuildTrayContextMenu();
                    });
                };
                boxSubMenu.DropDownItems.Add(visItem);

                targetCollection.Add(boxSubMenu);
            }
        }

        // Quick shortcut to open settings pre-selected to this monitor
        if (mon.IsConnected)
        {
            targetCollection.Add(new Forms.ToolStripSeparator());
            var configMonItem = new Forms.ToolStripMenuItem("Configure This Monitor in DesktopOrg...", null, (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _service.State.TargetMonitorDeviceName = mon.DeviceName;
                    _service.CalibrateDesktopGrid();
                    _service.SaveState();
                    ShowMainWindow();
                });
            });
            targetCollection.Add(configMonItem);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_desktopHook != null)
            {
                _desktopHook.IsPausedForSessionOrDisplay = true;
            }
            _displaySettleTimer?.Stop();
            _displaySettleTimer?.Start();
        });
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                if (_desktopHook != null)
                {
                    _desktopHook.IsPausedForSessionOrDisplay = true;
                }
                _displaySettleTimer?.Stop();
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                if (_desktopHook != null)
                {
                    _desktopHook.IsPausedForSessionOrDisplay = true;
                }
                _displaySettleTimer?.Stop();
                _displaySettleTimer?.Start();
            }
        });
    }

    #region Live Desktop Gray Area Windows Sync

    private void SyncOverlayWindows()
    {
        Dispatcher.Invoke(() =>
        {
            string mode = _service.State.AreaBackgroundMode;
            bool isLocked = _service.State.AreZonesLocked;

            bool showOverlays = mode is "Overlay" or "Both" ||
                                (mode == "EditOnly" && !isLocked);

            if (!showOverlays)
            {
                foreach (var win in _zoneWindows.Values.ToList())
                {
                    win.Close();
                }
                _zoneWindows.Clear();
                return;
            }

            var connectedMonitors = DesktopInterop.GetMonitors();
            string currentVd = DesktopInterop.GetCurrentVirtualDesktopId();

            var visibleAreas = _service.State.CustomAreas
                .Where(a => _service.IsAreaActiveOnCurrentDisplayAndDesktop(a, connectedMonitors, currentVd))
                .ToList();
            var activeIds = visibleAreas.Select(a => a.Id).ToHashSet();

            // Close removed, hidden, disconnected-monitor, or inactive-Virtual-Desktop areas
            foreach (var kvp in _zoneWindows.Where(k => !activeIds.Contains(k.Key)).ToList())
            {
                kvp.Value.Close();
                _zoneWindows.Remove(kvp.Key);
            }

            // Create or update active areas
            foreach (var area in visibleAreas)
            {
                if (_zoneWindows.TryGetValue(area.Id, out var existingWin))
                {
                    existingWin.UpdateFromConfig(area, isLocked);
                }
                else
                {
                    var newWin = new DeskZoneWindow(area, _service);
                    _zoneWindows[area.Id] = newWin;
                    newWin.Show();
                    newWin.UpdateFromConfig(area, isLocked);
                }
            }
        });
    }

    #endregion

    #region System Tray Icon & Context Menu

    private void InitializeTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateDesktopOrgTrayIcon(),
            Text = "DesktopOrg — Desktop Icon & Gray Box Organizer",
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        RebuildTrayContextMenu();
    }

    private void RebuildTrayContextMenu()
    {
        if (_trayIcon == null) return;

        var menu = new Forms.ContextMenuStrip();

        var openItem = new Forms.ToolStripMenuItem("Open DesktopOrg Settings", null, (_, _) => ShowMainWindow())
        {
            Font = new Font(menu.Font, System.Drawing.FontStyle.Bold)
        };
        menu.Items.Add(openItem);

        var applyItem = new Forms.ToolStripMenuItem("Apply & Organize Desktop Now", null, (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                _desktopHook?.NotifyInternalArrangementStarted();
                try
                {
                    int moved = _service.ApplyAndSaveChange();
                    _trayIcon.ShowBalloonTip(2000, "DesktopOrg", $"Organized {moved} desktop icons.", Forms.ToolTipIcon.Info);
                }
                finally
                {
                    _desktopHook?.NotifyInternalArrangementCompleted();
                }
            });
        });
        menu.Items.Add(applyItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        // Hierarchical Monitor -> Desktop Groups (App Shortcuts, Folders & Local Files, Internet Shortcuts) & Gray Boxes Menu
        var allMonitors = _service.GetAllMonitorsIncludingCachedOffline()
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.MonitorNumber)
            .ToList();

        foreach (var mon in allMonitors)
        {
            var monMenu = new Forms.ToolStripMenuItem(mon.FriendlyName);
            if (mon.IsPrimary)
            {
                monMenu.Font = new Font(menu.Font, System.Drawing.FontStyle.Bold);
            }

            _service.State.MonitorCaches.TryGetValue(mon.DeviceName, out var cache);
            var appShortcuts = cache?.AppShortcuts ?? new List<DesktopIconItem>();
            var foldersAndFiles = cache?.FoldersAndFiles ?? new List<DesktopIconItem>();
            var webLinks = cache?.WebLinks ?? new List<DesktopIconItem>();
            var areasOnMon = _service.State.CustomAreas
                .Where(a => string.Equals(a.MonitorDeviceName, mon.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                            (mon.IsPrimary && string.IsNullOrWhiteSpace(a.MonitorDeviceName)))
                .ToList();

            PopulateMonitorDropDownItems(monMenu.DropDownItems, mon, appShortcuts, foldersAndFiles, webLinks, areasOnMon);
            menu.Items.Add(monMenu);
        }

        menu.Items.Add(new Forms.ToolStripSeparator());

        bool locked = _service.State.AreZonesLocked;
        var lockItem = new Forms.ToolStripMenuItem(
            locked ? "Unlock Gray Areas on Desktop (Drag/Resize)" : "✓ Lock Gray Areas on Desktop (Click-Through)",
            null,
            (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _service.State.AreZonesLocked = !_service.State.AreZonesLocked;
                    _service.SaveState();
                    SyncOverlayWindows();
                });
            });
        menu.Items.Add(lockItem);

        // Areas Background Mode submenu
        var bgMenu = new Forms.ToolStripMenuItem("Areas Background Setting");
        var modes = new (string Tag, string Title)[]
        {
            ("Overlay", "Show Background for All (Desktop Hook Layer)"),
            ("Wallpaper", "Show Background for All (Wallpaper Behind Icons)"),
            ("Both", "Show Background for All (Both)"),
            ("EditOnly", "Show Background Only When Editing"),
            ("Hidden", "Hide All Area Backgrounds")
        };
        foreach (var m in modes)
        {
            var mItem = new Forms.ToolStripMenuItem(m.Title)
            {
                Checked = string.Equals(_service.State.AreaBackgroundMode, m.Tag, StringComparison.OrdinalIgnoreCase)
            };
            mItem.Click += (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _service.State.AreaBackgroundMode = m.Tag;
                    _service.SaveState();
                    _service.ApplyAreaBackgrounds();
                    _mainWindow.PopulateAllUi();
                });
            };
            bgMenu.DropDownItems.Add(mItem);
        }
        menu.Items.Add(bgMenu);

        // Toggle Desktop Icons Visibility
        bool iconsVisible = DesktopInterop.AreDesktopIconsVisible();
        var toggleIconsItem = new Forms.ToolStripMenuItem(
            iconsVisible ? "Hide All Desktop Icons" : "Show All Desktop Icons",
            null,
            (_, _) =>
            {
                DesktopInterop.SetDesktopIconsVisible(!DesktopInterop.AreDesktopIconsVisible());
                RebuildTrayContextMenu();
            });
        menu.Items.Add(toggleIconsItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        // Profiles submenu
        var profilesMenu = new Forms.ToolStripMenuItem("Switch Profile");
        foreach (var prof in _service.State.Profiles)
        {
            var pItem = new Forms.ToolStripMenuItem(prof.Name)
            {
                Checked = prof.Id == _service.State.ActiveProfileId
            };
            pItem.Click += (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _service.SwitchProfile(prof);
                    _mainWindow.RefreshDesktopIconsCache();
                    _mainWindow.PopulateAllUi();
                });
            };
            profilesMenu.DropDownItems.Add(pItem);
        }
        menu.Items.Add(profilesMenu);

        // Restore Saved Layout submenu
        var layoutsMenu = new Forms.ToolStripMenuItem("Restore Saved Layout (By Monitor Setup)");
        foreach (var layout in _service.State.Layouts.Take(12))
        {
            var lItem = new Forms.ToolStripMenuItem($"{layout.Name} — {layout.MonitorSummary} ({layout.Icons.Count} icons)");
            lItem.Click += (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _service.RestoreLayout(layout, restoreAreasToo: true);
                    _mainWindow.RefreshDesktopIconsCache();
                    _mainWindow.PopulateAllUi();
                });
            };
            layoutsMenu.DropDownItems.Add(lItem);
        }
        menu.Items.Add(layoutsMenu);

        var undoItem = new Forms.ToolStripMenuItem("Undo Last Change", null, (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                _service.RestoreUndoSnapshot();
                _mainWindow.RefreshDesktopIconsCache();
                _mainWindow.PopulateAllUi();
            });
        });
        menu.Items.Add(undoItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var startupItem = new Forms.ToolStripMenuItem("Start Automatically When Computer Starts")
        {
            Checked = _service.IsRunAtStartupEnabled()
        };
        startupItem.Click += (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                bool nextState = !_service.IsRunAtStartupEnabled();
                _service.SetRunAtStartup(nextState);
                RebuildTrayContextMenu();
            });
        };
        menu.Items.Add(startupItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("Exit DesktopOrg", null, (_, _) => ExitApplication());
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;
    }

    private static void PopulateTrayDesktopItemSubmenu(
        Forms.ToolStripMenuItem parentMenu,
        List<DesktopIconItem> items,
        bool groupByDomain)
    {
        if (items == null || items.Count == 0)
        {
            parentMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("(Empty)") { Enabled = false });
            return;
        }

        if (groupByDomain && items.Count > 5)
        {
            var domainGroups = items
                .GroupBy(i => string.IsNullOrWhiteSpace(i.WebDomain) ? "other" : i.WebDomain.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var dg in domainGroups)
            {
                var domMenu = new Forms.ToolStripMenuItem($"{dg.Key} ({dg.Count()})");
                foreach (var linkItem in dg.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                {
                    domMenu.DropDownItems.Add(CreateLaunchableTrayItem(linkItem));
                }
                parentMenu.DropDownItems.Add(domMenu);
            }

            parentMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
            var allLinksMenu = new Forms.ToolStripMenuItem($"All Web Links (A-Z) ({items.Count})");
            foreach (var linkItem in items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                allLinksMenu.DropDownItems.Add(CreateLaunchableTrayItem(linkItem));
            }
            parentMenu.DropDownItems.Add(allLinksMenu);
            return;
        }

        foreach (var item in items)
        {
            parentMenu.DropDownItems.Add(CreateLaunchableTrayItem(item));
        }
    }

    private static Forms.ToolStripMenuItem CreateLaunchableTrayItem(DesktopIconItem item)
    {
        string label = item.IsWebLink && !string.IsNullOrWhiteSpace(item.WebDomain)
            ? $"{item.Name}  [{item.WebDomain}]"
            : item.Name;

        var menuItem = new Forms.ToolStripMenuItem(label)
        {
            Image = DesktopInterop.ExtractIconBitmapSafe(item),
            ToolTipText = item.IsWebLink && !string.IsNullOrWhiteSpace(item.TargetUrl)
                ? item.TargetUrl
                : (item.FilePath ?? item.ParsingName)
        };
        menuItem.Click += (_, _) => DesktopInterop.LaunchDesktopItem(item);
        return menuItem;
    }

    private void ShowMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }
            _mainWindow.Activate();
            _mainWindow.RefreshDesktopIconsCache();
            _mainWindow.PopulateAllUi();
        });
    }

    private void ExitApplication()
    {
        IsExitingApplication = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _displaySettleTimer?.Stop();
        _desktopHook?.Dispose();

        foreach (var win in _zoneWindows.Values.ToList())
        {
            win.Close();
        }
        _zoneWindows.Clear();

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        Shutdown();
    }

    private static Icon CreateDesktopOrgTrayIcon()
    {
        try
        {
            var resInfo = GetResourceStream(new Uri("pack://application:,,,/Assets/DesktopOrg.ico"));
            if (resInfo?.Stream != null)
            {
                using var stream = resInfo.Stream;
                return new Icon(stream, new System.Drawing.Size(32, 32));
            }
        }
        catch
        {
            // Fallback to extracted executable icon or drawn icon
        }

        try
        {
            string exePath = Environment.ProcessPath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(exePath) && System.IO.File.Exists(exePath))
            {
                var extracted = Icon.ExtractAssociatedIcon(exePath);
                if (extracted != null) return extracted;
            }
        }
        catch
        {
            // Ignore
        }

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bgBrush = new SolidBrush(Color.FromArgb(15, 23, 42));
            g.FillRectangle(bgBrush, 2, 2, 28, 28);

            using var borderPen = new Pen(Color.FromArgb(220, 56, 189, 248), 1.5f);
            g.DrawRectangle(borderPen, 2, 2, 27, 27);

            using var font = new Font("Segoe UI", 11f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString("DO", font, Brushes.White, new RectangleF(2, 2, 28, 28), sf);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    #endregion
}
