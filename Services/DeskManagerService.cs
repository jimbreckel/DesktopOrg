using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;
using MyReDesk.Models;

namespace MyReDesk.Services;

public class DeskManagerService
{
    public static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopOrg");

    private static readonly string LegacyAppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyReDesk");

    private static readonly string StateFilePath = Path.Combine(AppDataFolder, "desktoporg_state_v2.json");
    private static readonly string LegacyStateFilePath = Path.Combine(LegacyAppDataFolder, "myredesk_state_v2.json");

    private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "DesktopOrg";
    private const string LegacyStartupValueName = "MyReDesk";

    public AppState State { get; private set; } = new();
    public bool IsApplyingInternalChange { get; private set; }

    public event Action? StateChanged;
    public event Action? AreasChanged;
    public event Action? InternalChangeCompleted;

    public DeskManagerService()
    {
        LoadState();
    }

    public void LoadState()
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);

            // Seamless migration from legacy MyReDesk state if DesktopOrg state does not exist yet
            if (!File.Exists(StateFilePath) && File.Exists(LegacyStateFilePath))
            {
                File.Copy(LegacyStateFilePath, StateFilePath, overwrite: true);
            }

            if (File.Exists(StateFilePath))
            {
                string json = File.ReadAllText(StateFilePath);
                var loaded = JsonSerializer.Deserialize<AppState>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (loaded != null)
                {
                    State = loaded;
                }
            }
        }
        catch
        {
            State = new AppState();
        }

        CalibrateDesktopGrid();
        EnsureDefaults();

        bool migrated = false;
        if (!State.SystemArea.MigratedShortcutDirectionDefaultV2)
        {
            State.SystemArea.ShortcutDirection = "Left to Right";
            State.SystemArea.MigratedShortcutDirectionDefaultV2 = true;
            migrated = true;
        }
        foreach (var area in State.CustomAreas)
        {
            if (string.IsNullOrWhiteSpace(area.FillDirection))
            {
                area.FillDirection = "Left to Right";
                migrated = true;
            }
        }
        if (migrated)
        {
            SaveState();
        }
    }

    public void SaveState()
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            var activeProfile = GetActiveProfile();
            if (activeProfile != null)
            {
                activeProfile.CustomAreas = State.CustomAreas.Select(a => a.Clone()).ToList();
            }

            string json = JsonSerializer.Serialize(State, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(StateFilePath, json);
            StateChanged?.Invoke();
        }
        catch
        {
            // Ignore write errors
        }
    }

    public void CalibrateDesktopGrid()
    {
        var (spacingX, spacingY) = DesktopInterop.GetDesktopGridSpacing();
        if (spacingX >= 48 && spacingY >= 48)
        {
            State.CellWidthPx = Math.Clamp(spacingX, 64, 200);
            State.CellHeightPx = Math.Clamp(spacingY, 64, 200);
        }
        else if (State.CellWidthPx < 48 || State.CellHeightPx < 48)
        {
            State.CellWidthPx = 76;
            State.CellHeightPx = 113;
        }
        State.GridOffsetX = 17;
        State.GridOffsetY = 2;

        var primary = GetTargetMonitor();
        var (cols, rows) = GetMonitorGridDimensions(primary);
        State.GridCols = cols;
        State.GridRows = rows;
    }

    public (int Cols, int Rows) GetMonitorGridDimensions(MonitorInfo mon)
    {
        int workH = mon.WorkHeight > 0 ? mon.WorkHeight : (mon.Height - 60);
        int usableW = Math.Max(600, mon.Width - State.GridOffsetX);
        int usableH = Math.Max(400, workH - State.GridOffsetY - (State.CellHeightPx / 3));

        int cols = Math.Clamp(usableW / Math.Max(48, State.CellWidthPx), 8, 48);
        int rows = Math.Clamp(usableH / Math.Max(48, State.CellHeightPx), 4, 24);
        return (cols, rows);
    }

    public MonitorInfo GetTargetMonitor()
    {
        var monitors = DesktopInterop.GetMonitors();
        if (!string.IsNullOrWhiteSpace(State.TargetMonitorDeviceName))
        {
            var matched = monitors.FirstOrDefault(m =>
                string.Equals(m.DeviceName, State.TargetMonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (matched != null) return matched;
        }
        return monitors.FirstOrDefault(m => m.IsPrimary)
               ?? monitors.FirstOrDefault()
               ?? new MonitorInfo
               {
                   DeviceName = @"\\.\DISPLAY1",
                   FriendlyName = "Monitor 1: Main (1920×1080) [Primary]",
                   X = 0,
                   Y = 0,
                   Width = 1920,
                   Height = 1080,
                   WorkWidth = 1920,
                   WorkHeight = 1032,
                   IsPrimary = true,
                   IsConnected = true
               };
    }

    public MonitorInfo? ResolveMonitorForArea(CustomAreaConfig area, List<MonitorInfo>? connectedMonitors = null)
    {
        connectedMonitors ??= DesktopInterop.GetMonitors();
        if (connectedMonitors.Count == 0) return null;

        if (string.IsNullOrWhiteSpace(area.MonitorDeviceName))
        {
            var primary = connectedMonitors.FirstOrDefault(m => m.IsPrimary) ?? connectedMonitors[0];
            area.MonitorDeviceName = primary.DeviceName;
            area.MonitorFriendlyName = primary.FriendlyName;
            return primary;
        }

        var match = connectedMonitors.FirstOrDefault(m =>
            string.Equals(m.DeviceName, area.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            area.MonitorFriendlyName = match.FriendlyName;
            return match;
        }

        // The monitor assigned to this area is currently disconnected / offline
        return null;
    }

    public bool IsAreaActiveOnCurrentDisplayAndDesktop(
        CustomAreaConfig area,
        List<MonitorInfo>? connectedMonitors = null,
        string? currentVirtualDesktopId = null)
    {
        if (!area.IsVisible) return false;

        var mon = ResolveMonitorForArea(area, connectedMonitors);
        if (mon == null) return false; // Monitor is disconnected -> gracefully hide without losing config!

        if (!string.IsNullOrWhiteSpace(area.VirtualDesktopId) &&
            !string.Equals(area.VirtualDesktopId, "*", StringComparison.OrdinalIgnoreCase))
        {
            string activeVd = currentVirtualDesktopId ?? DesktopInterop.GetCurrentVirtualDesktopId();
            if (!string.Equals(area.VirtualDesktopId, activeVd, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public List<MonitorInfo> GetAllMonitorsIncludingCachedOffline()
    {
        var connected = DesktopInterop.GetMonitors();
        var result = new List<MonitorInfo>(connected);
        var connectedDevices = connected.Select(m => m.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Include any offline monitors that have cached desktop groups or custom gray boxes
        var offlineDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in State.MonitorCaches.Keys)
        {
            if (!string.IsNullOrWhiteSpace(k) && !connectedDevices.Contains(k))
                offlineDevices.Add(k);
        }
        foreach (var a in State.CustomAreas)
        {
            if (!string.IsNullOrWhiteSpace(a.MonitorDeviceName) && !connectedDevices.Contains(a.MonitorDeviceName))
                offlineDevices.Add(a.MonitorDeviceName);
        }

        foreach (var dev in offlineDevices)
        {
            State.MonitorCaches.TryGetValue(dev, out var cache);
            var areaRef = State.CustomAreas.FirstOrDefault(a =>
                string.Equals(a.MonitorDeviceName, dev, StringComparison.OrdinalIgnoreCase));

            string friendly = cache?.MonitorFriendlyName
                              ?? areaRef?.MonitorFriendlyName
                              ?? $"Disconnected Monitor ({dev})";
            if (!friendly.Contains("Offline", StringComparison.OrdinalIgnoreCase))
            {
                friendly += " (Offline)";
            }

            result.Add(new MonitorInfo
            {
                DeviceName = dev,
                FriendlyName = friendly,
                X = cache?.X ?? 0,
                Y = cache?.Y ?? 0,
                Width = cache?.Width ?? 1920,
                Height = cache?.Height ?? 1080,
                WorkWidth = cache?.Width ?? 1920,
                WorkHeight = cache?.Height ?? 1032,
                IsPrimary = false,
                IsConnected = false
            });
        }

        return result;
    }

    /// <summary>
    /// Windows SysListView32 (Desktop) uses coordinates relative to the top-left of the Virtual Screen
    /// (Min(X), Min(Y) across all monitors), whereas top-level windows use physical Screen coordinates.
    /// </summary>
    public (int ListViewOriginX, int ListViewOriginY, int ScreenOriginX, int ScreenOriginY) GetMonitorOrigins()
    {
        var monitors = DesktopInterop.GetMonitors();
        if (monitors.Count == 0)
            return (0, 0, 0, 0);

        var target = GetTargetMonitor();
        return GetMonitorOriginsFor(target, monitors);
    }

    public (int ListViewOriginX, int ListViewOriginY, int ScreenOriginX, int ScreenOriginY) GetMonitorOriginsFor(
        MonitorInfo mon,
        List<MonitorInfo>? monitors = null)
    {
        monitors ??= DesktopInterop.GetMonitors();
        if (monitors.Count == 0)
            return (0, 0, mon.X, mon.Y);

        var (lvOriginX, lvOriginY) = DesktopInterop.GetTargetMonitorListViewOrigin(mon, monitors);
        return (lvOriginX, lvOriginY, mon.X, mon.Y);
    }

    private void EnsureDefaults()
    {
        var connectedMonitors = DesktopInterop.GetMonitors();
        var primaryMon = connectedMonitors.FirstOrDefault(m => m.IsPrimary) ?? GetTargetMonitor();

        if (State.CustomAreas.Count == 0)
        {
            ResetToDefaultReDeskAreas(saveImmediately: false);
        }

        // Bind any legacy CustomAreas that lack a MonitorDeviceName to the Primary Monitor
        foreach (var area in State.CustomAreas)
        {
            if (string.IsNullOrWhiteSpace(area.MonitorDeviceName))
            {
                area.MonitorDeviceName = primaryMon.DeviceName;
                area.MonitorFriendlyName = primaryMon.FriendlyName;
            }
            else
            {
                var matched = connectedMonitors.FirstOrDefault(m =>
                    string.Equals(m.DeviceName, area.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
                if (matched != null)
                {
                    area.MonitorFriendlyName = matched.FriendlyName;
                }
            }

            if (string.IsNullOrWhiteSpace(area.VirtualDesktopId))
            {
                area.VirtualDesktopId = "*";
                area.VirtualDesktopName = "All Virtual Desktops";
            }
        }

        if (State.SystemArea.SpecialShortcuts.Count == 0)
        {
            State.SystemArea.SpecialShortcuts.Add(new SpecialShortcutConfig
            {
                IconName = "Microsoft Edge",
                PlacementMode = "Pin First in System Area"
            });
        }

        if (State.Layouts.Count == 0)
        {
            var initialIcons = GetDesktopIconsWithGridCoords();
            if (initialIcons.Count > 0)
            {
                State.Layouts.Add(new IconLayout
                {
                    Name = "Initial Desktop Backup",
                    SavedAt = DateTime.Now,
                    MonitorSignature = DesktopInterop.GetMonitorSignature(),
                    MonitorSummary = DesktopInterop.GetMonitorSummary(),
                    AutoRestoreOnMonitorMatch = false,
                    Icons = initialIcons.Select(i => i.Clone()).ToList(),
                    SavedAreas = State.CustomAreas.Select(a => a.Clone()).ToList()
                });
            }
        }
        else
        {
            // Ensure the pre-organization Initial Desktop Backup never auto-restores over organized layouts on lock/unlock
            foreach (var backup in State.Layouts.Where(l => string.Equals(l.Name, "Initial Desktop Backup", StringComparison.OrdinalIgnoreCase)))
            {
                backup.AutoRestoreOnMonitorMatch = false;
            }
        }

        if (State.Profiles.Count == 0)
        {
            var defaultProfile = new DesktopProfile
            {
                Name = "Default (Fixed / Plan / Ongoing / Finish)",
                Description = "Classic ReDesk gray areas on the right + organized System Area on the left",
                AssociatedLayoutId = State.Layouts.FirstOrDefault()?.Id,
                CustomAreas = State.CustomAreas.Select(a => a.Clone()).ToList()
            };

            var categoryProfile = new DesktopProfile
            {
                Name = "Category Gray Areas (Apps / Folders / Docs)",
                Description = "Auto-sorts Folders, Shortcuts, Documents, and Media into dedicated gray areas",
                CustomAreas = CreateCategoryGrayAreas()
            };

            var cleanProfile = new DesktopProfile
            {
                Name = "Clean Presentation Mode",
                Description = "Temporarily hides desktop icons and gray areas for screen sharing",
                HideDesktopIcons = true,
                HideGrayAreas = true,
                CustomAreas = State.CustomAreas.Select(a => a.Clone()).ToList()
            };

            State.Profiles.Add(defaultProfile);
            State.Profiles.Add(categoryProfile);
            State.Profiles.Add(cleanProfile);
            State.ActiveProfileId = defaultProfile.Id;
        }

        RefreshMonitorCachesFromCurrentDesktop(saveState: false);
        SaveState();
    }

    public void ResetToDefaultReDeskAreas(bool saveImmediately = true)
    {
        CalibrateDesktopGrid();
        var targetMon = GetTargetMonitor();
        var (cols, rows) = GetMonitorGridDimensions(targetMon);

        int areaWidthCols = Math.Clamp(cols / 3, 5, 8);
        int startCol = Math.Max(4, cols - areaWidthCols);
        int rowsPerBox = Math.Max(2, rows / 4);

        // Keep boxes on OTHER monitors intact; replace only the boxes on the currently selected Target Monitor
        State.CustomAreas.RemoveAll(a =>
            string.IsNullOrWhiteSpace(a.MonitorDeviceName) ||
            string.Equals(a.MonitorDeviceName, targetMon.DeviceName, StringComparison.OrdinalIgnoreCase));

        var defaultBoxes = new List<CustomAreaConfig>
        {
            new CustomAreaConfig
            {
                Title = "Fixed",
                MonitorDeviceName = targetMon.DeviceName,
                MonitorFriendlyName = targetMon.FriendlyName,
                GridCol = startCol,
                GridRow = 0,
                GridWidthCols = areaWidthCols,
                GridHeightRows = rowsPerBox,
                GrayHex = "#8C929C",
                BorderHex = "#C0C6D0",
                Opacity = 0.52,
                CornerRadius = 10,
                LabelAlignment = "Center",
                AutoRule = "Manual (Drop Icons Here)",
                SortOrder = "Keep Grid Position"
            },
            new CustomAreaConfig
            {
                Title = "Plan",
                MonitorDeviceName = targetMon.DeviceName,
                MonitorFriendlyName = targetMon.FriendlyName,
                GridCol = startCol,
                GridRow = Math.Min(rows - 1, rowsPerBox),
                GridWidthCols = areaWidthCols,
                GridHeightRows = rowsPerBox,
                GrayHex = "#8C929C",
                BorderHex = "#C0C6D0",
                Opacity = 0.52,
                CornerRadius = 10,
                LabelAlignment = "Center",
                AutoRule = "Manual (Drop Icons Here)",
                SortOrder = "Keep Grid Position"
            },
            new CustomAreaConfig
            {
                Title = "Ongoing",
                MonitorDeviceName = targetMon.DeviceName,
                MonitorFriendlyName = targetMon.FriendlyName,
                GridCol = startCol,
                GridRow = Math.Min(rows - 1, rowsPerBox * 2),
                GridWidthCols = areaWidthCols,
                GridHeightRows = rowsPerBox,
                GrayHex = "#8C929C",
                BorderHex = "#C0C6D0",
                Opacity = 0.52,
                CornerRadius = 10,
                LabelAlignment = "Center",
                AutoRule = "Manual (Drop Icons Here)",
                SortOrder = "Keep Grid Position"
            },
            new CustomAreaConfig
            {
                Title = "Finish",
                MonitorDeviceName = targetMon.DeviceName,
                MonitorFriendlyName = targetMon.FriendlyName,
                GridCol = startCol,
                GridRow = Math.Min(rows - 1, rowsPerBox * 3),
                GridWidthCols = areaWidthCols,
                GridHeightRows = Math.Max(1, rows - rowsPerBox * 3),
                GrayHex = "#8C929C",
                BorderHex = "#C0C6D0",
                Opacity = 0.52,
                CornerRadius = 10,
                LabelAlignment = "Center",
                AutoRule = "Manual (Drop Icons Here)",
                SortOrder = "Keep Grid Position"
            }
        };

        State.CustomAreas.AddRange(defaultBoxes);

        if (saveImmediately)
        {
            SaveState();
            AreasChanged?.Invoke();
        }
    }

    public void LoadCategoryGrayAreasPreset()
    {
        CalibrateDesktopGrid();
        var targetMon = GetTargetMonitor();
        State.CustomAreas.RemoveAll(a =>
            string.IsNullOrWhiteSpace(a.MonitorDeviceName) ||
            string.Equals(a.MonitorDeviceName, targetMon.DeviceName, StringComparison.OrdinalIgnoreCase));
        State.CustomAreas.AddRange(CreateCategoryGrayAreas(targetMon));
        SaveState();
        AreasChanged?.Invoke();
    }

    public void ClearAllCustomAreas(bool currentMonitorOnly = true)
    {
        if (currentMonitorOnly)
        {
            var targetMon = GetTargetMonitor();
            State.CustomAreas.RemoveAll(a =>
                string.IsNullOrWhiteSpace(a.MonitorDeviceName) ||
                string.Equals(a.MonitorDeviceName, targetMon.DeviceName, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            State.CustomAreas.Clear();
        }
        SaveState();
        AreasChanged?.Invoke();
    }

    private List<CustomAreaConfig> CreateCategoryGrayAreas(MonitorInfo? targetMon = null)
    {
        targetMon ??= GetTargetMonitor();
        var (cols, rows) = GetMonitorGridDimensions(targetMon);
        int areaWidthCols = Math.Clamp(cols / 4, 6, 10);
        int startCol = Math.Max(4, cols - areaWidthCols);
        int topBoxRows = Math.Max(3, rows / 3);
        int midBoxRows = Math.Max(5, rows - topBoxRows);

        return new List<CustomAreaConfig>
        {
            new CustomAreaConfig
            {
                Title = "Folders & Files",
                MonitorDeviceName = targetMon.DeviceName,
                MonitorFriendlyName = targetMon.FriendlyName,
                GridCol = startCol,
                GridRow = 0,
                GridWidthCols = areaWidthCols,
                GridHeightRows = topBoxRows,
                GrayHex = "#64748B",
                BorderHex = "#CBD5E1",
                Opacity = 0.62,
                AutoRule = "Folders & Files",
                SortOrder = "Name (A-Z)"
            },
            new CustomAreaConfig
            {
                Title = "Web Links (.url)",
                MonitorDeviceName = targetMon.DeviceName,
                MonitorFriendlyName = targetMon.FriendlyName,
                GridCol = startCol,
                GridRow = topBoxRows,
                GridWidthCols = areaWidthCols,
                GridHeightRows = midBoxRows,
                GrayHex = "#64748B",
                BorderHex = "#CBD5E1",
                Opacity = 0.62,
                AutoRule = "Web Links (.url)",
                SortOrder = "Domain (A-Z)"
            }
        };
    }

    #region Coordinate Conversions & Desktop Icon Reading

    /// <summary>
    /// Returns physical Screen pixel bounds for positioning DeskZoneWindow on its assigned monitor.
    /// </summary>
    public (int ScreenX, int ScreenY, int Width, int Height) GetAreaScreenPixelBounds(CustomAreaConfig area)
    {
        var mon = ResolveMonitorForArea(area) ?? GetTargetMonitor();
        int screenOriginX = mon.X;
        int screenOriginY = mon.Y;
        int x = screenOriginX + State.GridOffsetX + area.GridCol * State.CellWidthPx;
        int y = screenOriginY + State.GridOffsetY + area.GridRow * State.CellHeightPx;
        int w = Math.Max(State.CellWidthPx, area.GridWidthCols * State.CellWidthPx - 6);
        int h = Math.Max(State.CellHeightPx, area.GridHeightRows * State.CellHeightPx - 6);
        return (x, y, w, h);
    }

    public (int Col, int Row) ScreenPixelToGridCell(int screenX, int screenY, CustomAreaConfig? area = null)
    {
        var monitors = DesktopInterop.GetMonitors();
        MonitorInfo? mon = null;

        // Check if the user dragged the DeskZoneWindow onto another monitor!
        int centerCheckX = screenX + 40;
        int centerCheckY = screenY + 30;
        mon = monitors.FirstOrDefault(m =>
            centerCheckX >= m.X && centerCheckX < (m.X + m.Width) &&
            centerCheckY >= m.Y && centerCheckY < (m.Y + m.Height));

        if (mon != null && area != null)
        {
            area.MonitorDeviceName = mon.DeviceName;
            area.MonitorFriendlyName = mon.FriendlyName;
        }

        mon ??= (area != null ? ResolveMonitorForArea(area, monitors) : null) ?? GetTargetMonitor();
        var (cols, rows) = GetMonitorGridDimensions(mon);

        int col = (int)Math.Round((double)(screenX - mon.X - State.GridOffsetX) / State.CellWidthPx);
        int row = (int)Math.Round((double)(screenY - mon.Y - State.GridOffsetY) / State.CellHeightPx);
        col = Math.Clamp(col, 0, Math.Max(0, cols - 1));
        row = Math.Clamp(row, 0, Math.Max(0, rows - 1));
        return (col, row);
    }

    public (int Col, int Row) PixelToGridCellOnMonitor(int listViewX, int listViewY, MonitorInfo mon, List<MonitorInfo>? monitors = null)
    {
        var (lvOriginX, lvOriginY, _, _) = GetMonitorOriginsFor(mon, monitors);
        var (cols, rows) = GetMonitorGridDimensions(mon);
        int col = (int)Math.Round((double)(listViewX - lvOriginX - State.GridOffsetX) / State.CellWidthPx);
        int row = (int)Math.Round((double)(listViewY - lvOriginY - State.GridOffsetY) / State.CellHeightPx);
        col = Math.Clamp(col, 0, Math.Max(0, cols - 1));
        row = Math.Clamp(row, 0, Math.Max(0, rows - 1));
        return (col, row);
    }

    public (int X, int Y) GridCellToPixelOnMonitor(int col, int row, MonitorInfo mon, List<MonitorInfo>? monitors = null)
    {
        var (lvOriginX, lvOriginY, _, _) = GetMonitorOriginsFor(mon, monitors);
        int x = lvOriginX + State.GridOffsetX + col * State.CellWidthPx;
        int y = lvOriginY + State.GridOffsetY + row * State.CellHeightPx;
        return (x, y);
    }

    /// <summary>
    /// Converts SysListView32 / IFolderView icon coordinates (VirtualScreen-relative) to Target Monitor Grid (Col, Row).
    /// </summary>
    public (int Col, int Row) PixelToGridCell(int listViewX, int listViewY)
    {
        var monitors = DesktopInterop.GetMonitors();
        var target = GetTargetMonitor();
        return PixelToGridCellOnMonitor(listViewX, listViewY, target, monitors);
    }

    /// <summary>
    /// Converts Target Monitor Grid (Col, Row) to SysListView32 / IFolderView icon coordinates (VirtualScreen-relative).
    /// </summary>
    public (int X, int Y) GridCellToPixel(int col, int row)
    {
        var monitors = DesktopInterop.GetMonitors();
        var target = GetTargetMonitor();
        return GridCellToPixelOnMonitor(col, row, target, monitors);
    }

    public MonitorInfo FindMonitorForListViewPoint(int listViewX, int listViewY, List<MonitorInfo>? monitors = null)
    {
        monitors ??= DesktopInterop.GetMonitors();
        if (monitors.Count == 0) return GetTargetMonitor();

        int centerX = listViewX + 16;
        int centerY = listViewY + 16;

        // Pass 1: Strict non-overlapping monitor bounds check using icon center
        foreach (var mon in monitors)
        {
            var (lvOx, lvOy) = DesktopInterop.GetTargetMonitorListViewOrigin(mon, monitors);
            if (centerX >= lvOx && centerX < (lvOx + mon.Width) &&
                centerY >= lvOy && centerY < (lvOy + mon.Height))
            {
                return mon;
            }
        }

        // Pass 2: Horizontal range match if icon sits slightly above/below monitor Y bounds
        foreach (var mon in monitors)
        {
            var (lvOx, lvOy) = DesktopInterop.GetTargetMonitorListViewOrigin(mon, monitors);
            if (centerX >= lvOx && centerX < (lvOx + mon.Width) &&
                centerY >= (lvOy - 48) && centerY < (lvOy + mon.Height + 48))
            {
                return mon;
            }
        }

        // Fallback: closest monitor horizontally
        MonitorInfo best = monitors[0];
        double bestDist = double.MaxValue;
        foreach (var mon in monitors)
        {
            var (lvOx, lvOy) = DesktopInterop.GetTargetMonitorListViewOrigin(mon, monitors);
            double cx = lvOx + mon.Width / 2.0;
            double cy = lvOy + mon.Height / 2.0;
            double dist = Math.Abs(centerX - cx) + Math.Abs(centerY - cy);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = mon;
            }
        }
        return best;
    }

    public List<DesktopIconItem> GetDesktopIconsWithGridCoords(MonitorInfo? forMonitor = null)
    {
        CalibrateDesktopGrid();
        var monitors = DesktopInterop.GetMonitors();
        var target = forMonitor ?? GetTargetMonitor();
        var icons = DesktopInterop.GetDesktopIcons();
        foreach (var icon in icons)
        {
            var (c, r) = PixelToGridCellOnMonitor(icon.X, icon.Y, target, monitors);
            icon.GridCol = c;
            icon.GridRow = r;
        }
        return icons;
    }

    public List<DesktopIconItem> GetDesktopIconsOnMonitor(MonitorInfo mon, List<MonitorInfo>? monitors = null)
    {
        monitors ??= DesktopInterop.GetMonitors();
        var allIcons = DesktopInterop.GetDesktopIcons();
        var result = new List<DesktopIconItem>();
        foreach (var ic in allIcons)
        {
            var ownerMon = FindMonitorForListViewPoint(ic.X, ic.Y, monitors);
            if (string.Equals(ownerMon.DeviceName, mon.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                var (c, r) = PixelToGridCellOnMonitor(ic.X, ic.Y, mon, monitors);
                ic.GridCol = c;
                ic.GridRow = r;
                result.Add(ic);
            }
        }
        return result;
    }

    public static bool IsCellInsideArea(int col, int row, CustomAreaConfig area)
    {
        return col >= area.GridCol && col < (area.GridCol + area.GridWidthCols) &&
               row >= area.GridRow && row < (area.GridRow + area.GridHeightRows);
    }

    /// <summary>
    /// Non-destructively populates State.MonitorCaches and each active CustomArea's CachedItems
    /// from the live Windows Desktop so the System Tray Menu and Desktop Right-Click Context Menu
    /// always have up-to-date items organized by Monitor -> Desktop Groups & Gray Boxes.
    /// </summary>
    public void RefreshMonitorCachesFromCurrentDesktop(bool saveState = true)
    {
        try
        {
            var monitors = DesktopInterop.GetMonitors();
            if (monitors.Count == 0) return;

            var allIcons = DesktopInterop.GetDesktopIcons();
            if (allIcons.Count == 0) return;

            string currentVd = DesktopInterop.GetCurrentVirtualDesktopId();
            var settings = State.SystemArea;

            foreach (var mon in monitors)
            {
                var (cols, rows) = GetMonitorGridDimensions(mon);
                var iconsOnMon = allIcons
                    .Where(i => string.Equals(FindMonitorForListViewPoint(i.X, i.Y, monitors).DeviceName, mon.DeviceName, StringComparison.OrdinalIgnoreCase))
                    .Select(i =>
                    {
                        var clone = i.Clone();
                        var (c, r) = PixelToGridCellOnMonitor(clone.X, clone.Y, mon, monitors);
                        clone.GridCol = c;
                        clone.GridRow = r;
                        return clone;
                    })
                    .ToList();

                var activeAreasOnMon = State.CustomAreas
                    .Where(a => IsAreaActiveOnCurrentDisplayAndDesktop(a, monitors, currentVd) &&
                                string.Equals(a.MonitorDeviceName, mon.DeviceName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var claimedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var area in activeAreasOnMon)
                {
                    var inside = iconsOnMon.Where(i => IsCellInsideArea(i.GridCol, i.GridRow, area)).ToList();
                    area.AssignedIconKeys = inside.Select(GetIconKey).ToList();
                    area.CachedItems = inside.Select(i => i.Clone()).ToList();
                    foreach (var ic in inside)
                        claimedKeys.Add(GetIconKey(ic));
                }

                var desktopIconsOnMon = iconsOnMon.Where(i => !claimedKeys.Contains(GetIconKey(i))).ToList();
                SplitIconsIntoThreeGroups(desktopIconsOnMon, settings, out var appShortcuts, out var foldersAndFiles, out var webLinks);

                State.MonitorCaches[mon.DeviceName] = new MonitorWorkspaceCache
                {
                    DeviceName = mon.DeviceName,
                    MonitorFriendlyName = mon.FriendlyName,
                    IsPrimary = mon.IsPrimary,
                    X = mon.X,
                    Y = mon.Y,
                    Width = mon.Width,
                    Height = mon.Height,
                    GridCols = cols,
                    GridRows = rows,
                    LastUpdated = DateTime.Now,
                    AppShortcuts = appShortcuts,
                    FoldersAndFiles = foldersAndFiles,
                    WebLinks = webLinks
                };
            }

            if (State.UseDesktopContextMenu)
            {
                DesktopNativeHook.SyncDesktopContextMenuRegistry(this);
            }

            if (saveState)
            {
                SaveState();
            }
        }
        catch
        {
            // Ignore transient shell read errors
        }
    }

    public static void SplitIconsIntoThreeGroups(
        List<DesktopIconItem> icons,
        SystemAreaSettings settings,
        out List<DesktopIconItem> shortcutsGroup,
        out List<DesktopIconItem> filesGroup,
        out List<DesktopIconItem> webLinksGroup)
    {
        var scList = new List<DesktopIconItem>();
        if (settings.SystemIconsFirst)
        {
            scList.AddRange(icons.Where(i => i.IsSystemOrVirtual).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase));
        }

        var regularShortcuts = icons
            .Where(i => (!settings.SystemIconsFirst || !i.IsSystemOrVirtual) &&
                        i.IsShortcut && !i.IsWebLink && !i.IsFolder);
        scList.AddRange(SortShortcutGroup(regularShortcuts, settings.ShortcutSortBy));

        if (string.Equals(settings.WebLinksGroupMode, "Group with App Shortcuts", StringComparison.OrdinalIgnoreCase))
        {
            var webWithShortcuts = icons.Where(i => i.IsWebLink);
            scList.AddRange(SortShortcutGroup(webWithShortcuts, settings.ShortcutSortBy));
        }

        var rawFiles = icons
            .Where(i => !scList.Contains(i) &&
                        (!i.IsWebLink || string.Equals(settings.WebLinksGroupMode, "Group with Files", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var flList = SortFileGroup(rawFiles, settings.FileSortBy, settings.FoldersBeforeFiles);

        var rawWebLinks = icons.Where(i => !scList.Contains(i) && !flList.Contains(i));
        var wlList = SortWebLinkGroup(rawWebLinks, settings.WebLinkSortBy).ToList();

        shortcutsGroup = scList;
        filesGroup = flList;
        webLinksGroup = wlList;
    }

    #endregion

    #region Core ReDesk "Save Change" / Apply Arrangement Engine

    /// <summary>
    /// Executes the full multi-monitor ReDesk arrangement:
    /// 1. Captures an Undo snapshot.
    /// 2. Organizes Gray Boxes and Desktop Groups across all connected monitors.
    /// 3. Populates CachedItems for each Gray Box and MonitorWorkspaceCache for each Monitor.
    /// 4. Updates the Desktop Gray Area Backgrounds and syncs the Desktop Right-Click Context Menu.
    /// </summary>
    public int ApplyAndSaveChange()
    {
        IsApplyingInternalChange = true;
        try
        {
            CaptureUndoSnapshot();
            CalibrateDesktopGrid();

            var monitors = DesktopInterop.GetMonitors();
            var targetMonitor = GetTargetMonitor();
            string currentVd = DesktopInterop.GetCurrentVirtualDesktopId();
            var allDesktopIcons = DesktopInterop.GetDesktopIcons();

            if (allDesktopIcons.Count == 0 || monitors.Count == 0)
            {
                ApplyAreaBackgrounds();
                SaveState();
                return 0;
            }

            // Map each icon to the monitor where it currently sits
            var iconMonitorMap = new Dictionary<DesktopIconItem, MonitorInfo>();
            foreach (var ic in allDesktopIcons)
            {
                var ownerMon = FindMonitorForListViewPoint(ic.X, ic.Y, monitors);
                var (c, r) = PixelToGridCellOnMonitor(ic.X, ic.Y, ownerMon, monitors);
                ic.GridCol = c;
                ic.GridRow = r;
                iconMonitorMap[ic] = ownerMon;
            }

            // Active Gray Boxes on currently connected monitors & current Virtual Desktop
            var activeAreas = State.CustomAreas
                .Where(a => IsAreaActiveOnCurrentDisplayAndDesktop(a, monitors, currentVd))
                .ToList();

            var claimedIcons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var areaIconLists = activeAreas.ToDictionary(a => a.Id, _ => new List<DesktopIconItem>());

            // Pass 1: Icons physically located inside any active Gray Box on ANY connected monitor stay in that Gray Box!
            foreach (var ic in allDesktopIcons)
            {
                string key = GetIconKey(ic);
                var ownerMon = iconMonitorMap[ic];
                var physicalArea = activeAreas.FirstOrDefault(a =>
                    string.Equals(a.MonitorDeviceName, ownerMon.DeviceName, StringComparison.OrdinalIgnoreCase) &&
                    IsCellInsideArea(ic.GridCol, ic.GridRow, a));

                if (physicalArea != null)
                {
                    areaIconLists[physicalArea.Id].Add(ic);
                    claimedIcons.Add(key);
                }
            }

            // Pass 2: Icons matching an active Gray Box's AutoRule
            foreach (var ic in allDesktopIcons)
            {
                string key = GetIconKey(ic);
                if (claimedIcons.Contains(key)) continue;

                var ownerMon = iconMonitorMap[ic];
                // Prefer an AutoRule box on the icon's current monitor first, or on any monitor if GatherAllMonitorsIconsToTarget is enabled
                var ruleArea = activeAreas.FirstOrDefault(a =>
                                   string.Equals(a.MonitorDeviceName, ownerMon.DeviceName, StringComparison.OrdinalIgnoreCase) &&
                                   DoesIconMatchAutoRule(ic, a.AutoRule))
                               ?? (State.GatherAllMonitorsIconsToTarget
                                   ? activeAreas.FirstOrDefault(a => DoesIconMatchAutoRule(ic, a.AutoRule))
                                   : null);

                if (ruleArea != null)
                {
                    areaIconLists[ruleArea.Id].Add(ic);
                    claimedIcons.Add(key);
                }
            }

            // Pass 3: Place icons inside each active Gray Box on its assigned monitor and cache them
            foreach (var area in activeAreas)
            {
                var areaMon = ResolveMonitorForArea(area, monitors) ?? targetMonitor;
                var iconsInArea = areaIconLists[area.Id];
                area.AssignedIconKeys = iconsInArea.Select(GetIconKey).ToList();

                if (iconsInArea.Count > 0)
                {
                    PlaceIconsInsideCustomArea(area, iconsInArea, areaMon, monitors);
                }
                area.CachedItems = iconsInArea.Select(i => i.Clone()).ToList();
            }

            // Pass 4: Arrange unclaimed System Area icons per monitor
            foreach (var mon in monitors)
            {
                var (cols, rows) = GetMonitorGridDimensions(mon);
                bool[,] occupied = new bool[cols, rows];

                var areasOnMon = activeAreas
                    .Where(a => string.Equals(a.MonitorDeviceName, mon.DeviceName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var area in areasOnMon)
                {
                    for (int c = area.GridCol; c < Math.Min(cols, area.GridCol + area.GridWidthCols); c++)
                    {
                        for (int r = area.GridRow; r < Math.Min(rows, area.GridRow + area.GridHeightRows); r++)
                        {
                            if (c >= 0 && r >= 0)
                                occupied[c, r] = true;
                        }
                    }
                }

                List<DesktopIconItem> systemIconsForThisMon;
                if (State.GatherAllMonitorsIconsToTarget)
                {
                    if (string.Equals(mon.DeviceName, targetMonitor.DeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        systemIconsForThisMon = allDesktopIcons
                            .Where(i => !claimedIcons.Contains(GetIconKey(i)))
                            .ToList();
                    }
                    else
                    {
                        systemIconsForThisMon = new List<DesktopIconItem>();
                    }
                }
                else
                {
                    systemIconsForThisMon = allDesktopIcons
                        .Where(i => !claimedIcons.Contains(GetIconKey(i)) &&
                                    string.Equals(iconMonitorMap[i].DeviceName, mon.DeviceName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                ArrangeSystemAreaIconsOnMonitor(
                    systemIconsForThisMon,
                    occupied,
                    cols,
                    rows,
                    mon,
                    monitors,
                    out var shortcutsGroup,
                    out var filesGroup,
                    out var webLinksGroup);

                State.MonitorCaches[mon.DeviceName] = new MonitorWorkspaceCache
                {
                    DeviceName = mon.DeviceName,
                    MonitorFriendlyName = mon.FriendlyName,
                    IsPrimary = mon.IsPrimary,
                    X = mon.X,
                    Y = mon.Y,
                    Width = mon.Width,
                    Height = mon.Height,
                    GridCols = cols,
                    GridRows = rows,
                    LastUpdated = DateTime.Now,
                    AppShortcuts = shortcutsGroup.Select(i => i.Clone()).ToList(),
                    FoldersAndFiles = filesGroup.Select(i => i.Clone()).ToList(),
                    WebLinks = webLinksGroup.Select(i => i.Clone()).ToList()
                };
            }

            // Pass 5: Apply new pixel coordinates to Windows Desktop
            int moved = DesktopInterop.SetDesktopIconPositions(allDesktopIcons);

            // Pass 6: Save/update Active Organized Layout for current monitor configuration
            UpsertActiveOrganizedLayout(allDesktopIcons);

            // Pass 7: Apply Gray Area Backgrounds & sync Desktop Right-Click Context Menu
            ApplyAreaBackgrounds();
            if (State.UseDesktopContextMenu)
            {
                DesktopNativeHook.SyncDesktopContextMenuRegistry(this);
            }

            SaveState();
            AreasChanged?.Invoke();
            return moved;
        }
        finally
        {
            IsApplyingInternalChange = false;
            InternalChangeCompleted?.Invoke();
        }
    }

    public void ApplyAreaBackgrounds()
    {
        if (State.AreaBackgroundMode is "Wallpaper" or "Both")
        {
            DesktopInterop.ApplyWallpaperWithGrayAreas(State, AppDataFolder);
        }
        else
        {
            DesktopInterop.RestoreOriginalWallpaper(State);
        }
        AreasChanged?.Invoke();
    }

    /// <summary>
    /// Moves a Custom Area to a new (newCol, newRow) on its monitor's grid and shifts any desktop icons inside it accordingly.
    /// </summary>
    public void MoveCustomAreaOnGrid(CustomAreaConfig area, int newCol, int newRow, bool moveIconsOnDesktopImmediately = false)
    {
        var monitors = DesktopInterop.GetMonitors();
        var mon = ResolveMonitorForArea(area, monitors) ?? GetTargetMonitor();
        var (cols, rows) = GetMonitorGridDimensions(mon);

        int maxCol = Math.Max(0, cols - area.GridWidthCols);
        int maxRow = Math.Max(0, rows - area.GridHeightRows);
        newCol = Math.Clamp(newCol, 0, maxCol);
        newRow = Math.Clamp(newRow, 0, maxRow);

        int dCol = newCol - area.GridCol;
        int dRow = newRow - area.GridRow;
        if (dCol == 0 && dRow == 0) return;

        var oldAreaClone = area.Clone();

        area.GridCol = newCol;
        area.GridRow = newRow;

        if (moveIconsOnDesktopImmediately)
        {
            var iconsOnMon = GetDesktopIconsOnMonitor(mon, monitors);
            var insideOld = iconsOnMon.Where(i => IsCellInsideArea(i.GridCol, i.GridRow, oldAreaClone)).ToList();
            if (insideOld.Count > 0)
            {
                foreach (var ic in insideOld)
                {
                    ic.GridCol = Math.Clamp(ic.GridCol + dCol, 0, cols - 1);
                    ic.GridRow = Math.Clamp(ic.GridRow + dRow, 0, rows - 1);
                    var (px, py) = GridCellToPixelOnMonitor(ic.GridCol, ic.GridRow, mon, monitors);
                    ic.X = px;
                    ic.Y = py;
                }
                DesktopInterop.SetDesktopIconPositions(insideOld);
            }
        }
    }

    private void PlaceIconsInsideCustomArea(
        CustomAreaConfig area,
        List<DesktopIconItem> icons,
        MonitorInfo mon,
        List<MonitorInfo> monitors)
    {
        int startC = area.GridCol;
        int startR = area.GridRow;
        int wCols = Math.Max(1, area.GridWidthCols);
        int hRows = Math.Max(1, area.GridHeightRows);
        bool fillTopToBottom = string.Equals(area.FillDirection, "Top to Bottom", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(area.SortOrder, "Keep Grid Position", StringComparison.OrdinalIgnoreCase))
        {
            bool[,] cellUsed = new bool[wCols, hRows];
            var orderedByCurrent = fillTopToBottom
                ? icons.OrderBy(i => i.GridCol).ThenBy(i => i.GridRow).ToList()
                : icons.OrderBy(i => i.GridRow).ThenBy(i => i.GridCol).ToList();

            foreach (var icon in orderedByCurrent)
            {
                int localC = Math.Clamp(icon.GridCol - startC, 0, wCols - 1);
                int localR = Math.Clamp(icon.GridRow - startR, 0, hRows - 1);

                if (!cellUsed[localC, localR])
                {
                    cellUsed[localC, localR] = true;
                    SetIconToCellOnMonitor(icon, startC + localC, startR + localR, mon, monitors);
                }
                else
                {
                    var (freeC, freeR) = FindFirstFreeLocalCell(cellUsed, wCols, hRows, fillTopToBottom);
                    cellUsed[freeC, freeR] = true;
                    SetIconToCellOnMonitor(icon, startC + freeC, startR + freeR, mon, monitors);
                }
            }
        }
        else
        {
            IEnumerable<DesktopIconItem> sorted = area.SortOrder switch
            {
                "Domain (A-Z)" or "Domain (Most Links First)" => SortWebLinkGroup(icons, area.SortOrder),
                "Date Modified" => icons
                    .OrderBy(i => i.IsScriptExecutable ? 1 : 0)
                    .ThenByDescending(i => GetLastWriteTimeSafe(i.FilePath)),
                _ => icons
                    .OrderBy(i => i.IsScriptExecutable ? 1 : 0)
                    .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            };

            int idx = 0;
            foreach (var icon in sorted)
            {
                int localC = fillTopToBottom ? (idx / hRows) % wCols : idx % wCols;
                int localR = fillTopToBottom ? idx % hRows : (idx / wCols) % hRows;
                SetIconToCellOnMonitor(icon, startC + localC, startR + localR, mon, monitors);
                idx++;
            }
        }
    }

    private static (int c, int r) FindFirstFreeLocalCell(bool[,] used, int wCols, int hRows, bool topToBottom = false)
    {
        if (topToBottom)
        {
            for (int c = 0; c < wCols; c++)
            {
                for (int r = 0; r < hRows; r++)
                {
                    if (!used[c, r]) return (c, r);
                }
            }
        }
        else
        {
            for (int r = 0; r < hRows; r++)
            {
                for (int c = 0; c < wCols; c++)
                {
                    if (!used[c, r]) return (c, r);
                }
            }
        }
        return (wCols - 1, hRows - 1);
    }

    private void ArrangeSystemAreaIconsOnMonitor(
        List<DesktopIconItem> icons,
        bool[,] occupied,
        int cols,
        int rows,
        MonitorInfo mon,
        List<MonitorInfo> monitors,
        out List<DesktopIconItem> shortcutsGroupOut,
        out List<DesktopIconItem> filesGroupOut,
        out List<DesktopIconItem> webLinksGroupOut)
    {
        shortcutsGroupOut = new List<DesktopIconItem>();
        filesGroupOut = new List<DesktopIconItem>();
        webLinksGroupOut = new List<DesktopIconItem>();

        if (icons.Count == 0) return;

        var settings = State.SystemArea;

        var remaining = new List<DesktopIconItem>();
        var pinnedFirst = new List<DesktopIconItem>();
        var pinnedBottomLeft = new List<DesktopIconItem>();

        foreach (var icon in icons)
        {
            var special = settings.SpecialShortcuts.FirstOrDefault(s =>
                !string.IsNullOrWhiteSpace(s.IconName) &&
                (icon.Name.Equals(s.IconName, StringComparison.OrdinalIgnoreCase) ||
                 icon.Name.Contains(s.IconName, StringComparison.OrdinalIgnoreCase)));

            if (special != null)
            {
                if (special.PlacementMode == "Ignore (Do Not Move)")
                {
                    if (icon.GridCol >= 0 && icon.GridCol < cols && icon.GridRow >= 0 && icon.GridRow < rows)
                        occupied[icon.GridCol, icon.GridRow] = true;
                    SetIconToCellOnMonitor(icon, icon.GridCol, icon.GridRow, mon, monitors);
                    if (icon.IsWebLink) webLinksGroupOut.Add(icon);
                    else if (icon.IsFolder || !icon.IsShortcut) filesGroupOut.Add(icon);
                    else shortcutsGroupOut.Add(icon);
                    continue;
                }
                if (special.PlacementMode == "Pin to Bottom-Left")
                {
                    pinnedBottomLeft.Add(icon);
                    continue;
                }
                pinnedFirst.Add(icon);
                continue;
            }

            remaining.Add(icon);
        }

        // Place Bottom-Left pinned shortcuts
        foreach (var blIcon in pinnedBottomLeft)
        {
            for (int c = 0; c < cols; c++)
            {
                bool placed = false;
                for (int r = rows - 1; r >= 0; r--)
                {
                    if (!occupied[c, r])
                    {
                        occupied[c, r] = true;
                        SetIconToCellOnMonitor(blIcon, c, r, mon, monitors);
                        shortcutsGroupOut.Add(blIcon);
                        placed = true;
                        break;
                    }
                }
                if (placed) break;
            }
        }

        if (settings.SeparateFilesAndShortcuts)
        {
            int colGap = settings.LeaveEmptyColumnBetweenGroups ? 1 : 0;

            var shortcutsGroup = new List<DesktopIconItem>();
            if (settings.SystemIconsFirst)
            {
                shortcutsGroup.AddRange(remaining.Where(i => i.IsSystemOrVirtual).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase));
            }
            shortcutsGroup.AddRange(pinnedFirst);

            var regularShortcuts = remaining
                .Where(i => (!settings.SystemIconsFirst || !i.IsSystemOrVirtual) &&
                            i.IsShortcut && !i.IsWebLink && !i.IsFolder);
            shortcutsGroup.AddRange(SortShortcutGroup(regularShortcuts, settings.ShortcutSortBy));

            if (string.Equals(settings.WebLinksGroupMode, "Group with App Shortcuts", StringComparison.OrdinalIgnoreCase))
            {
                var webWithShortcuts = remaining.Where(i => i.IsWebLink);
                shortcutsGroup.AddRange(SortShortcutGroup(webWithShortcuts, settings.ShortcutSortBy));
            }

            var filesGroup = remaining
                .Where(i => !shortcutsGroup.Contains(i) &&
                            (!i.IsWebLink || string.Equals(settings.WebLinksGroupMode, "Group with Files", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var sortedFilesGroup = SortFileGroup(filesGroup, settings.FileSortBy, settings.FoldersBeforeFiles);

            var rawWebLinks = remaining
                .Where(i => !shortcutsGroup.Contains(i) && !sortedFilesGroup.Contains(i));
            var webLinksGroup = SortWebLinkGroup(rawWebLinks, settings.WebLinkSortBy).ToList();

            bool fullWidthRows = string.Equals(settings.ShortcutDirection, "Left to Right (Full Screen Width)", StringComparison.OrdinalIgnoreCase);

            if (fullWidthRows)
            {
                int currentStartRow = 0;
                if (shortcutsGroup.Count > 0)
                {
                    int lastRow = PlaceGroupFullWidthRowMajor(shortcutsGroup, occupied, cols, rows, currentStartRow, mon, monitors);
                    currentStartRow = Math.Min(rows - 1, lastRow + 1);
                }
                if (sortedFilesGroup.Count > 0)
                {
                    int lastRow = PlaceGroupFullWidthRowMajor(sortedFilesGroup, occupied, cols, rows, currentStartRow, mon, monitors);
                    currentStartRow = Math.Min(rows - 1, lastRow + 1);
                }
                if (webLinksGroup.Count > 0)
                {
                    PlaceGroupFullWidthRowMajor(webLinksGroup, occupied, cols, rows, currentStartRow, mon, monitors);
                }
            }
            else
            {
                int currentStartCol = 0;

                if (shortcutsGroup.Count > 0)
                {
                    int lastShortcutCol = PlaceGroupInSystemAreaOnMonitor(shortcutsGroup, occupied, cols, rows, currentStartCol, settings.ShortcutDirection, mon, monitors, boundedColumnBlock: true);
                    currentStartCol = Math.Min(cols - 1, lastShortcutCol + 1 + colGap);
                }

                if (sortedFilesGroup.Count > 0)
                {
                    int lastFileCol = PlaceGroupInSystemAreaOnMonitor(sortedFilesGroup, occupied, cols, rows, currentStartCol, settings.FileDirection, mon, monitors, boundedColumnBlock: true);
                    currentStartCol = Math.Min(cols - 1, lastFileCol + 1 + colGap);
                }

                if (webLinksGroup.Count > 0)
                {
                    PlaceGroupInSystemAreaOnMonitor(webLinksGroup, occupied, cols, rows, currentStartCol, settings.ShortcutDirection, mon, monitors, boundedColumnBlock: true);
                }
            }

            shortcutsGroupOut.AddRange(shortcutsGroup);
            filesGroupOut.AddRange(sortedFilesGroup);
            webLinksGroupOut.AddRange(webLinksGroup);
        }
        else
        {
            var allOrdered = new List<DesktopIconItem>();
            if (settings.SystemIconsFirst)
            {
                allOrdered.AddRange(remaining.Where(i => i.IsSystemOrVirtual).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase));
            }
            allOrdered.AddRange(pinnedFirst);
            var rest = remaining.Where(i => !allOrdered.Contains(i));
            allOrdered.AddRange(SortFileGroup(rest, settings.FileSortBy, settings.FoldersBeforeFiles));

            PlaceGroupInSystemAreaOnMonitor(allOrdered, occupied, cols, rows, 0, settings.ShortcutDirection, mon, monitors, boundedColumnBlock: false);
            SplitIconsIntoThreeGroups(allOrdered, settings, out shortcutsGroupOut, out filesGroupOut, out webLinksGroupOut);
        }
    }

    private int PlaceGroupFullWidthRowMajor(
        IEnumerable<DesktopIconItem> group,
        bool[,] occupied,
        int cols,
        int rows,
        int startRow,
        MonitorInfo mon,
        List<MonitorInfo> monitors)
    {
        int maxRowUsed = startRow;
        foreach (var icon in group)
        {
            bool placed = false;
            for (int r = startRow; r < rows && !placed; r++)
            {
                for (int c = 0; c < cols && !placed; c++)
                {
                    if (!occupied[c, r])
                    {
                        occupied[c, r] = true;
                        SetIconToCellOnMonitor(icon, c, r, mon, monitors);
                        maxRowUsed = Math.Max(maxRowUsed, r);
                        placed = true;
                    }
                }
            }
            for (int r = 0; r < startRow && !placed; r++)
            {
                for (int c = 0; c < cols && !placed; c++)
                {
                    if (!occupied[c, r])
                    {
                        occupied[c, r] = true;
                        SetIconToCellOnMonitor(icon, c, r, mon, monitors);
                        placed = true;
                    }
                }
            }
        }
        return maxRowUsed;
    }

    private int PlaceGroupInSystemAreaOnMonitor(
        IEnumerable<DesktopIconItem> group,
        bool[,] occupied,
        int cols,
        int rows,
        int startCol,
        string direction,
        MonitorInfo mon,
        List<MonitorInfo> monitors,
        bool boundedColumnBlock = true)
    {
        var groupList = group as IList<DesktopIconItem> ?? group.ToList();
        if (groupList.Count == 0) return startCol;

        int maxColUsed = startCol;
        bool topToBottom = string.Equals(direction, "Top to Bottom", StringComparison.OrdinalIgnoreCase);

        if (topToBottom)
        {
            foreach (var icon in groupList)
            {
                bool placed = false;
                for (int c = startCol; c < cols && !placed; c++)
                {
                    for (int r = 0; r < rows && !placed; r++)
                    {
                        if (!occupied[c, r])
                        {
                            occupied[c, r] = true;
                            SetIconToCellOnMonitor(icon, c, r, mon, monitors);
                            maxColUsed = Math.Max(maxColUsed, c);
                            placed = true;
                        }
                    }
                }
                for (int c = 0; c < startCol && !placed; c++)
                {
                    for (int r = 0; r < rows && !placed; r++)
                    {
                        if (!occupied[c, r])
                        {
                            occupied[c, r] = true;
                            SetIconToCellOnMonitor(icon, c, r, mon, monitors);
                            placed = true;
                        }
                    }
                }
            }
        }
        else
        {
            // Left to Right: determine the target column span for this group so separated groups stay side-by-side
            int targetMaxCol = cols - 1;
            if (boundedColumnBlock && !string.Equals(direction, "Left to Right (Full Screen Width)", StringComparison.OrdinalIgnoreCase))
            {
                int freeCellsFound = 0;
                int minColsDesired = groupList.Count > 1 ? Math.Min(groupList.Count, 4) : 1;
                int colsCounted = 0;
                targetMaxCol = startCol;

                for (int c = startCol; c < cols; c++)
                {
                    int colFree = 0;
                    for (int r = 0; r < rows; r++)
                    {
                        if (!occupied[c, r]) colFree++;
                    }
                    if (colFree > 0)
                    {
                        freeCellsFound += colFree;
                        colsCounted++;
                        targetMaxCol = c;
                        if (freeCellsFound >= groupList.Count && colsCounted >= minColsDesired)
                            break;
                    }
                }
            }

            foreach (var icon in groupList)
            {
                bool placed = false;

                // 1. Primary pass: fill Left-to-Right (row by row) across startCol .. targetMaxCol
                for (int r = 0; r < rows && !placed; r++)
                {
                    for (int c = startCol; c <= targetMaxCol && c < cols && !placed; c++)
                    {
                        if (!occupied[c, r])
                        {
                            occupied[c, r] = true;
                            SetIconToCellOnMonitor(icon, c, r, mon, monitors);
                            maxColUsed = Math.Max(maxColUsed, c);
                            placed = true;
                        }
                    }
                }

                // 2. Overflow fallback: any remaining columns to the right
                for (int r = 0; r < rows && !placed; r++)
                {
                    for (int c = targetMaxCol + 1; c < cols && !placed; c++)
                    {
                        if (!occupied[c, r])
                        {
                            occupied[c, r] = true;
                            SetIconToCellOnMonitor(icon, c, r, mon, monitors);
                            maxColUsed = Math.Max(maxColUsed, c);
                            placed = true;
                        }
                    }
                }

                // 3. Final fallback: any free cell to the left of startCol
                for (int r = 0; r < rows && !placed; r++)
                {
                    for (int c = 0; c < startCol && !placed; c++)
                    {
                        if (!occupied[c, r])
                        {
                            occupied[c, r] = true;
                            SetIconToCellOnMonitor(icon, c, r, mon, monitors);
                            placed = true;
                        }
                    }
                }
            }
        }

        return maxColUsed;
    }

    private static IEnumerable<DesktopIconItem> SortWebLinkGroup(IEnumerable<DesktopIconItem> items, string sortBy)
    {
        var list = items.ToList();
        if (list.Count == 0) return list;

        if (string.Equals(sortBy, "Domain (Most Links First)", StringComparison.OrdinalIgnoreCase))
        {
            var domainCounts = list
                .GroupBy(i => string.IsNullOrWhiteSpace(i.WebDomain) ? "zzzz" : i.WebDomain, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            return list
                .OrderByDescending(i => domainCounts.TryGetValue(string.IsNullOrWhiteSpace(i.WebDomain) ? "zzzz" : i.WebDomain, out int cnt) ? cnt : 0)
                .ThenBy(i => string.IsNullOrWhiteSpace(i.WebDomain) ? "zzzz" : i.WebDomain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.WebHost ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        }

        return sortBy switch
        {
            "Domain (A-Z)" => list
                .OrderBy(i => string.IsNullOrWhiteSpace(i.WebDomain) ? "zzzz" : i.WebDomain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.WebHost ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            "Date Modified" => list
                .OrderByDescending(i => GetLastWriteTimeSafe(i.FilePath))
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            _ => list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static IEnumerable<DesktopIconItem> SortShortcutGroup(IEnumerable<DesktopIconItem> items, string sortBy)
    {
        return sortBy switch
        {
            "Date Modified" => items
                .OrderBy(i => i.IsScriptExecutable ? 1 : 0)
                .ThenByDescending(i => GetLastWriteTimeSafe(i.FilePath))
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            "Category" => items
                .OrderBy(i => i.IsScriptExecutable ? 1 : 0)
                .ThenBy(i => i.Category)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            _ => items
                .OrderBy(i => i.IsScriptExecutable ? 1 : 0)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static List<DesktopIconItem> SortFileGroup(IEnumerable<DesktopIconItem> items, string sortBy, bool foldersFirst)
    {
        var list = items.ToList();
        return sortBy switch
        {
            "Name (A-Z)" => list
                .OrderBy(i => foldersFirst && i.IsFolder ? 0 : 1)
                .ThenBy(i => i.IsScriptExecutable ? 1 : 0)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "Date Modified" => list
                .OrderBy(i => foldersFirst && i.IsFolder ? 0 : 1)
                .ThenBy(i => i.IsScriptExecutable ? 1 : 0)
                .ThenByDescending(i => GetLastWriteTimeSafe(i.FilePath))
                .ToList(),
            "Extension" => list
                .OrderBy(i => foldersFirst && i.IsFolder ? 0 : 1)
                .ThenBy(i => i.IsScriptExecutable ? 1 : 0)
                .ThenBy(i => Path.GetExtension(i.FilePath ?? i.Name), StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => list
                .OrderBy(i => foldersFirst && i.IsFolder ? 0 : 1)
                .ThenBy(i => i.Category)
                .ThenBy(i => i.IsScriptExecutable ? 1 : 0)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private void SetIconToCell(DesktopIconItem icon, int col, int row)
    {
        icon.GridCol = col;
        icon.GridRow = row;
        var (x, y) = GridCellToPixel(col, row);
        icon.X = x;
        icon.Y = y;
    }

    private void SetIconToCellOnMonitor(
        DesktopIconItem icon,
        int col,
        int row,
        MonitorInfo mon,
        List<MonitorInfo> monitors)
    {
        icon.GridCol = col;
        icon.GridRow = row;
        var (x, y) = GridCellToPixelOnMonitor(col, row, mon, monitors);
        icon.X = x;
        icon.Y = y;
    }

    private static bool DoesIconMatchAutoRule(DesktopIconItem icon, string? autoRule)
    {
        if (string.IsNullOrWhiteSpace(autoRule) ||
            autoRule.StartsWith("Manual", StringComparison.OrdinalIgnoreCase))
            return false;

        return autoRule switch
        {
            "Folders & Files" => icon.IsFolder || (!icon.IsShortcut && !icon.IsWebLink && !icon.IsSystemOrVirtual),
            "Web Links (.url)" => icon.IsWebLink || icon.Category == "Web Links (.url)",
            "Folders" => icon.IsFolder,
            "Shortcuts" => icon.IsShortcut && !icon.IsWebLink && !icon.IsSystemOrVirtual,
            "Documents" => icon.Category == "Documents",
            "Media" => icon.Category is "Media" or "Code & Archives",
            "Code & Archives" => icon.Category == "Code & Archives",
            _ => false
        };
    }

    public static string GetIconKey(DesktopIconItem icon) =>
        !string.IsNullOrWhiteSpace(icon.ParsingName) ? icon.ParsingName : icon.Name;

    private static DateTime GetLastWriteTimeSafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return DateTime.MinValue;
        try
        {
            if (File.Exists(path)) return File.GetLastWriteTime(path);
            if (Directory.Exists(path)) return Directory.GetLastWriteTime(path);
        }
        catch
        {
            // Ignore
        }
        return DateTime.MinValue;
    }

    #endregion

    #region Snapshots, Saved Layouts & Undo

    public void CaptureUndoSnapshot()
    {
        var currentIcons = GetDesktopIconsWithGridCoords();
        if (currentIcons.Count > 0)
        {
            State.UndoSnapshot = new IconLayout
            {
                Name = "Undo Snapshot",
                SavedAt = DateTime.Now,
                MonitorSignature = DesktopInterop.GetMonitorSignature(),
                MonitorSummary = DesktopInterop.GetMonitorSummary(),
                Icons = currentIcons,
                SavedAreas = State.CustomAreas.Select(a => a.Clone()).ToList(),
                SavedMonitorCaches = State.MonitorCaches.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Clone(),
                    StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    public int RestoreUndoSnapshot()
    {
        if (State.UndoSnapshot == null || State.UndoSnapshot.Icons.Count == 0)
            return 0;

        IsApplyingInternalChange = true;
        try
        {
            if (State.UndoSnapshot.SavedAreas.Count > 0)
            {
                State.CustomAreas = State.UndoSnapshot.SavedAreas.Select(a => a.Clone()).ToList();
            }

            if (State.UndoSnapshot.SavedMonitorCaches.Count > 0)
            {
                State.MonitorCaches = State.UndoSnapshot.SavedMonitorCaches.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Clone(),
                    StringComparer.OrdinalIgnoreCase);
            }

            int count = DesktopInterop.SetDesktopIconPositions(State.UndoSnapshot.Icons);
            ApplyAreaBackgrounds();
            if (State.UseDesktopContextMenu)
            {
                DesktopNativeHook.SyncDesktopContextMenuRegistry(this);
            }
            SaveState();
            return count;
        }
        finally
        {
            IsApplyingInternalChange = false;
            InternalChangeCompleted?.Invoke();
        }
    }

    public IconLayout SaveCurrentLayout(string name, bool autoRestoreOnMonitor = true)
    {
        RefreshMonitorCachesFromCurrentDesktop(saveState: false);
        var icons = GetDesktopIconsWithGridCoords();
        var layout = new IconLayout
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Layout ({DesktopInterop.GetMonitorSummary()})" : name.Trim(),
            SavedAt = DateTime.Now,
            MonitorSignature = DesktopInterop.GetMonitorSignature(),
            MonitorSummary = DesktopInterop.GetMonitorSummary(),
            AutoRestoreOnMonitorMatch = autoRestoreOnMonitor,
            Icons = icons,
            SavedAreas = State.CustomAreas.Select(a => a.Clone()).ToList(),
            SavedMonitorCaches = State.MonitorCaches.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Clone(),
                StringComparer.OrdinalIgnoreCase)
        };

        State.Layouts.Insert(0, layout);
        SaveState();
        return layout;
    }

    public int RestoreLayout(IconLayout layout, bool restoreAreasToo = true)
    {
        IsApplyingInternalChange = true;
        try
        {
            CaptureUndoSnapshot();

            if (restoreAreasToo && layout.SavedAreas != null && layout.SavedAreas.Count > 0)
            {
                State.CustomAreas = layout.SavedAreas.Select(a => a.Clone()).ToList();
            }

            if (layout.SavedMonitorCaches != null && layout.SavedMonitorCaches.Count > 0)
            {
                foreach (var kvp in layout.SavedMonitorCaches)
                {
                    State.MonitorCaches[kvp.Key] = kvp.Value.Clone();
                }
            }

            int count = DesktopInterop.SetDesktopIconPositions(layout.Icons);
            ApplyAreaBackgrounds();
            if (State.UseDesktopContextMenu)
            {
                DesktopNativeHook.SyncDesktopContextMenuRegistry(this);
            }
            SaveState();
            return count;
        }
        finally
        {
            IsApplyingInternalChange = false;
            InternalChangeCompleted?.Invoke();
        }
    }

    private void UpsertActiveOrganizedLayout(List<DesktopIconItem> organizedIcons)
    {
        if (organizedIcons.Count == 0) return;

        string sig = DesktopInterop.GetMonitorSignature();
        string summary = DesktopInterop.GetMonitorSummary();
        const string activeLayoutName = "Active Organized Layout";

        var existing = State.Layouts.FirstOrDefault(l =>
            string.Equals(l.Name, activeLayoutName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(l.MonitorSignature, sig, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.SavedAt = DateTime.Now;
            existing.MonitorSummary = summary;
            existing.AutoRestoreOnMonitorMatch = true;
            existing.Icons = organizedIcons.Select(i => i.Clone()).ToList();
            existing.SavedAreas = State.CustomAreas.Select(a => a.Clone()).ToList();
            existing.SavedMonitorCaches = State.MonitorCaches.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            State.Layouts.Insert(0, new IconLayout
            {
                Name = activeLayoutName,
                SavedAt = DateTime.Now,
                MonitorSignature = sig,
                MonitorSummary = summary,
                AutoRestoreOnMonitorMatch = true,
                Icons = organizedIcons.Select(i => i.Clone()).ToList(),
                SavedAreas = State.CustomAreas.Select(a => a.Clone()).ToList(),
                SavedMonitorCaches = State.MonitorCaches.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Clone(),
                    StringComparer.OrdinalIgnoreCase)
            });
        }
    }

    /// <summary>
    /// Handles display topology changes (monitors plugged in / unplugged):
    /// 1. If DisconnectedMonitorMode == "RememberSetupOrHide" (default) and a saved layout matches the
    ///    new monitor topology signature, automatically restores that monitor setup's saved layout & boxes.
    /// 2. Fallback (or if DisconnectedMonitorMode == "HideOfflineOnly"): preserves all offline monitor
    ///    boxes & cached items and gracefully hides offline overlays without losing any configuration.
    /// </summary>
    public int TryAutoRestoreForCurrentMonitors()
    {
        if (State.AutoRestoreOnDisplayChange &&
            !string.Equals(State.DisconnectedMonitorMode, "HideOfflineOnly", StringComparison.OrdinalIgnoreCase))
        {
            string sig = DesktopInterop.GetMonitorSignature();
            var matching = State.Layouts
                .Where(l => l.AutoRestoreOnMonitorMatch &&
                            !string.Equals(l.Name, "Initial Desktop Backup", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(l.MonitorSignature, sig, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(l => l.SavedAt)
                .FirstOrDefault();

            if (matching != null)
            {
                return RestoreLayout(matching, restoreAreasToo: true);
            }
        }

        // Fallback: keep all existing Gray Boxes (offline monitor boxes are hidden automatically by IsAreaActiveOnCurrentDisplayAndDesktop)
        RefreshMonitorCachesFromCurrentDesktop(saveState: true);
        ApplyAreaBackgrounds();
        return 0;
    }

    #endregion

    #region Profiles

    public DesktopProfile? GetActiveProfile()
    {
        return State.Profiles.FirstOrDefault(p => p.Id == State.ActiveProfileId)
               ?? State.Profiles.FirstOrDefault();
    }

    public void SwitchProfile(DesktopProfile targetProfile)
    {
        var currentProfile = GetActiveProfile();
        if (currentProfile != null)
        {
            currentProfile.CustomAreas = State.CustomAreas.Select(a => a.Clone()).ToList();
        }

        State.ActiveProfileId = targetProfile.Id;
        if (targetProfile.CustomAreas != null && targetProfile.CustomAreas.Count > 0)
        {
            State.CustomAreas = targetProfile.CustomAreas.Select(a => a.Clone()).ToList();
        }

        DesktopInterop.SetDesktopIconsVisible(!targetProfile.HideDesktopIcons);

        if (targetProfile.HideGrayAreas)
        {
            State.AreaBackgroundMode = "Hidden";
            ApplyAreaBackgrounds();
        }
        else
        {
            if (State.AreaBackgroundMode == "Hidden")
                State.AreaBackgroundMode = "Overlay";
            ApplyAndSaveChange();
        }

        SaveState();
    }

    #endregion

    #region Windows Startup Registry & Local Programs Installation

    public static readonly string InstalledProgramFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "DesktopOrg");

    public static readonly string InstalledExePath = Path.Combine(InstalledProgramFolder, "DesktopOrg.exe");

    public static readonly string StartMenuShortcutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        "DesktopOrg.lnk");

    private static readonly string LegacyStartMenuShortcutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        "MyReDesk.lnk");

    public static string GetPreferredExecutablePath()
    {
        if (File.Exists(InstalledExePath))
            return InstalledExePath;
        return Environment.ProcessPath ?? InstalledExePath;
    }

    public bool IsInstalledInLocalPrograms()
    {
        return File.Exists(InstalledExePath) && File.Exists(StartMenuShortcutPath);
    }

    public bool InstallToLocalPrograms(out string message)
    {
        try
        {
            string currentExe = Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            {
                message = "Could not locate current DesktopOrg.exe binary.";
                return false;
            }

            Directory.CreateDirectory(InstalledProgramFolder);

            if (!string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(InstalledExePath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(currentExe, InstalledExePath, overwrite: true);
            }

            if (File.Exists(LegacyStartMenuShortcutPath))
            {
                try { File.Delete(LegacyStartMenuShortcutPath); } catch { }
            }

            // Create Windows Start Menu shortcut (%AppData%\Microsoft\Windows\Start Menu\Programs\DesktopOrg.lnk)
            CreateWindowsShortcut(
                shortcutPath: StartMenuShortcutPath,
                targetPath: InstalledExePath,
                workingDir: InstalledProgramFolder,
                description: "DesktopOrg — Desktop Icon & Gray Box Organizer");

            // Point Windows Startup & Desktop Right-Click Context Menu to %LocalAppData%\Programs\DesktopOrg\DesktopOrg.exe
            if (IsRunAtStartupEnabled() || State.RunAtWindowsStartup)
            {
                SetRunAtStartup(true);
            }

            if (State.UseDesktopContextMenu || DesktopNativeHook.IsDesktopContextMenuEnabled())
            {
                DesktopNativeHook.SetDesktopContextMenuEnabled(true, this);
            }

            SaveState();
            message = $"Installed to {InstalledExePath}, created Start Menu shortcut, and updated Startup & Right-Click Menu paths.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Install failed: {ex.Message}";
            return false;
        }
    }

    private static void CreateWindowsShortcut(string shortcutPath, string targetPath, string workingDir, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell == null) return;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDir;
        shortcut.Description = description;
        shortcut.IconLocation = $"{targetPath},0";
        shortcut.Save();
    }

    public bool IsRunAtStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, false);
            return key?.GetValue(StartupValueName) != null || key?.GetValue(LegacyStartupValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public void SetRunAtStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryKey, true);
            if (key == null) return;

            // Remove legacy startup value if present
            key.DeleteValue(LegacyStartupValueName, false);

            if (enable)
            {
                string exePath = GetPreferredExecutablePath();
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue(StartupValueName, $"\"{exePath}\" --tray");
                }
            }
            else
            {
                key.DeleteValue(StartupValueName, false);
            }

            State.RunAtWindowsStartup = enable;
            SaveState();
        }
        catch
        {
            // Ignore registry errors
        }
    }

    #endregion
}
