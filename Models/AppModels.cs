using System;
using System.Collections.Generic;
using System.Linq;

namespace MyReDesk.Models;

public class DesktopIconItem
{
    public string Name { get; set; } = string.Empty;
    public string ParsingName { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int GridCol { get; set; }
    public int GridRow { get; set; }
    public string Category { get; set; } = "Other";
    public bool IsSystemOrVirtual { get; set; }
    public bool IsShortcut { get; set; }
    public bool IsScriptExecutable { get; set; }
    public bool IsWebLink { get; set; }
    public bool IsFolder { get; set; }
    public string? FilePath { get; set; }
    public string? TargetUrl { get; set; }
    public string? WebDomain { get; set; }
    public string? WebHost { get; set; }

    public DesktopIconItem Clone() => new()
    {
        Name = Name,
        ParsingName = ParsingName,
        X = X,
        Y = Y,
        GridCol = GridCol,
        GridRow = GridRow,
        Category = Category,
        IsSystemOrVirtual = IsSystemOrVirtual,
        IsShortcut = IsShortcut,
        IsScriptExecutable = IsScriptExecutable,
        IsWebLink = IsWebLink,
        IsFolder = IsFolder,
        FilePath = FilePath,
        TargetUrl = TargetUrl,
        WebDomain = WebDomain,
        WebHost = WebHost
    };
}

public class MonitorInfo
{
    public string DeviceName { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string HardwareModel { get; set; } = string.Empty;
    public int MonitorNumber { get; set; } = 1;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int WorkWidth { get; set; }
    public int WorkHeight { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsConnected { get; set; } = true;
}

public class VirtualDesktopInfo
{
    public string Id { get; set; } = "*";
    public string Name { get; set; } = "All Virtual Desktops";
    public int Index { get; set; } = 0;
    public bool IsCurrent { get; set; }
}

public class MonitorWorkspaceCache
{
    public string DeviceName { get; set; } = string.Empty;
    public string MonitorFriendlyName { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int GridCols { get; set; } = 20;
    public int GridRows { get; set; } = 10;
    public DateTime LastUpdated { get; set; } = DateTime.Now;
    public List<DesktopIconItem> AppShortcuts { get; set; } = new();
    public List<DesktopIconItem> FoldersAndFiles { get; set; } = new();
    public List<DesktopIconItem> WebLinks { get; set; } = new();

    public MonitorWorkspaceCache Clone() => new()
    {
        DeviceName = DeviceName,
        MonitorFriendlyName = MonitorFriendlyName,
        IsPrimary = IsPrimary,
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        GridCols = GridCols,
        GridRows = GridRows,
        LastUpdated = LastUpdated,
        AppShortcuts = AppShortcuts.Select(i => i.Clone()).ToList(),
        FoldersAndFiles = FoldersAndFiles.Select(i => i.Clone()).ToList(),
        WebLinks = WebLinks.Select(i => i.Clone()).ToList()
    };
}

public class IconLayout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Saved Layout";
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public string MonitorSignature { get; set; } = string.Empty;
    public string MonitorSummary { get; set; } = string.Empty;
    public bool AutoRestoreOnMonitorMatch { get; set; } = true;
    public List<DesktopIconItem> Icons { get; set; } = new();
    public List<CustomAreaConfig> SavedAreas { get; set; } = new();
    public Dictionary<string, MonitorWorkspaceCache> SavedMonitorCaches { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class CustomAreaConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Custom Area";

    // Target monitor & Virtual Desktop scope
    public string MonitorDeviceName { get; set; } = string.Empty;
    public string MonitorFriendlyName { get; set; } = string.Empty;
    public string VirtualDesktopId { get; set; } = "*"; // "*" = All Virtual Desktops
    public string VirtualDesktopName { get; set; } = "All Virtual Desktops";

    // Grid cell coordinates (0-indexed on the assigned monitor's desktop grid)
    public int GridCol { get; set; } = 13;
    public int GridRow { get; set; } = 0;
    public int GridWidthCols { get; set; } = 7;
    public int GridHeightRows { get; set; } = 2;

    // Visual appearance
    public string GrayHex { get; set; } = "#8C929C";
    public string BorderHex { get; set; } = "#B8BEC8";
    public string TextHex { get; set; } = "#FFFFFF";
    public double Opacity { get; set; } = 0.55;
    public double CornerRadius { get; set; } = 10;
    public string LabelAlignment { get; set; } = "Center"; // "Center", "Top-Left", "Hidden"
    public bool IsVisible { get; set; } = true;

    // Auto-rule or manual assignment
    public string AutoRule { get; set; } = "Manual (Drop Icons Here)"; // or "Folders", "Shortcuts", "Documents", "Media", "Code & Archives"
    public string SortOrder { get; set; } = "Keep Grid Position"; // "Keep Grid Position", "Name (A-Z)", "Date Modified"

    // Explicitly tracked icon parsing names or display names assigned to this Custom Area
    public List<string> AssignedIconKeys { get; set; } = new();

    // Cached full icon items inside this Custom Area (persisted even when the monitor is offline)
    public List<DesktopIconItem> CachedItems { get; set; } = new();

    public CustomAreaConfig Clone() => new()
    {
        Id = Id,
        Title = Title,
        MonitorDeviceName = MonitorDeviceName,
        MonitorFriendlyName = MonitorFriendlyName,
        VirtualDesktopId = VirtualDesktopId,
        VirtualDesktopName = VirtualDesktopName,
        GridCol = GridCol,
        GridRow = GridRow,
        GridWidthCols = GridWidthCols,
        GridHeightRows = GridHeightRows,
        GrayHex = GrayHex,
        BorderHex = BorderHex,
        TextHex = TextHex,
        Opacity = Opacity,
        CornerRadius = CornerRadius,
        LabelAlignment = LabelAlignment,
        IsVisible = IsVisible,
        AutoRule = AutoRule,
        SortOrder = SortOrder,
        AssignedIconKeys = new List<string>(AssignedIconKeys),
        CachedItems = CachedItems.Select(i => i.Clone()).ToList()
    };
}

public class SpecialShortcutConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string IconName { get; set; } = "Microsoft Edge";
    public string PlacementMode { get; set; } = "Pin First in System Area"; // or "Pin to Bottom-Left", "Ignore (Do Not Move)"
}

public class SystemAreaSettings
{
    public bool SeparateFilesAndShortcuts { get; set; } = true;
    public bool LeaveEmptyColumnBetweenGroups { get; set; } = true;
    public string WebLinksGroupMode { get; set; } = "Separate Group (Own Columns)"; // "Separate Group (Own Columns)", "Group with Files", "Group with App Shortcuts"
    public string WebLinkSortBy { get; set; } = "Domain (A-Z)"; // "Domain (A-Z)", "Domain (Most Links First)", "Name (A-Z)", "Date Modified"

    // Shortcut arrangement options
    public string ShortcutSortBy { get; set; } = "Name (A-Z)"; // "Name (A-Z)", "Date Modified", "Category"
    public string ShortcutDirection { get; set; } = "Top to Bottom"; // "Top to Bottom", "Left to Right"
    public bool SystemIconsFirst { get; set; } = true;

    // File arrangement options
    public string FileSortBy { get; set; } = "Category & Name"; // "Category & Name", "Name (A-Z)", "Date Modified", "Extension"
    public string FileDirection { get; set; } = "Top to Bottom"; // "Top to Bottom", "Left to Right"
    public bool FoldersBeforeFiles { get; set; } = true;

    public List<SpecialShortcutConfig> SpecialShortcuts { get; set; } = new();
}

public class DesktopProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default";
    public string Description { get; set; } = "Standard ReDesk layout";
    public string? AssociatedLayoutId { get; set; }
    public List<CustomAreaConfig> CustomAreas { get; set; } = new();
    public bool HideDesktopIcons { get; set; } = false;
    public bool HideGrayAreas { get; set; } = false;
}

public class AppState
{
    public string ActiveProfileId { get; set; } = string.Empty;

    // Background mode:
    // "Overlay" = Show Background for All (Live Desktop Overlay)
    // "Wallpaper" = Show Background for All (Wallpaper Integration - Behind Icons)
    // "Both" = Both Overlay + Wallpaper
    // "EditOnly" = Show Background Only When Unlocked/Editing
    // "Hidden" = Hide All Backgrounds
    public string AreaBackgroundMode { get; set; } = "Overlay";

    public bool AreZonesLocked { get; set; } = true;
    public string TargetMonitorDeviceName { get; set; } = string.Empty;
    public bool GatherAllMonitorsIconsToTarget { get; set; } = true;
    public bool AutoArrangeOnDesktopChange { get; set; } = true;
    public bool UseDesktopContextMenu { get; set; } = true;
    public bool AutoRestoreOnDisplayChange { get; set; } = true;
    // "RememberSetupOrHide" = Switch to saved layout for the active monitor setup if one exists; fallback to hiding offline monitor boxes
    // "HideOfflineOnly" = Always keep current layout and simply hide gray boxes for disconnected monitors
    public string DisconnectedMonitorMode { get; set; } = "RememberSetupOrHide";
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool StartMinimizedToTray { get; set; } = false;
    public bool RunAtWindowsStartup { get; set; } = false;

    // Desktop Grid geometry (auto-calibrated from Windows SysListView32 spacing + screen work area)
    public int GridCols { get; set; } = 20;
    public int GridRows { get; set; } = 9;
    public int CellWidthPx { get; set; } = 96;
    public int CellHeightPx { get; set; } = 104;
    public int GridOffsetX { get; set; } = 12;
    public int GridOffsetY { get; set; } = 12;

    public SystemAreaSettings SystemArea { get; set; } = new();
    public List<CustomAreaConfig> CustomAreas { get; set; } = new();
    public Dictionary<string, MonitorWorkspaceCache> MonitorCaches { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<IconLayout> Layouts { get; set; } = new();
    public List<DesktopProfile> Profiles { get; set; } = new();
    public IconLayout? UndoSnapshot { get; set; }
    public string? OriginalWallpaperBackupPath { get; set; }
}
