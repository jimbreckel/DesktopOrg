<p align="center">
  <img src="Assets/DesktopOrg.png" alt="DesktopOrg Logo" width="96" />
</p>

<h1 align="center">DesktopOrg</h1>

<p align="center">
  <strong>Native Windows 10 / 11 Desktop Icon & Translucent Gray Box Organizer</strong><br/>
  Built with <code>.NET 9</code> (WPF + WinForms System Tray + Win32 / COM Shell Interop)
</p>

---

**DesktopOrg** is a fast, lightweight Windows desktop icon and translucent gray-box ("Desk Zone") organizer engineered for **Windows 10 and Windows 11 (including 24H2 Build 26100 / 26300+)**. It supports multi-monitor setups with mixed DPIs and negative screen coordinates, per-monitor-topology layout memory, 3-way group separation (`App Shortcuts`, `Folders & Local Files`, and `Internet Shortcuts .url`), and cascading System Tray & Desktop Right-Click menus.

## Documentation & Links

- **[User Guide (`documentation/USER_GUIDE.md`)](documentation/USER_GUIDE.md)** — Plain-English guide to every setting, `?` hover tooltips, custom Gray Boxes, multi-monitor behavior, and CLI flags.
- **[Architecture & Technical Deep-Dive (`documentation/ARCHITECTURE.md`)](documentation/ARCHITECTURE.md)** — Win32/COM `IFolderView` interop, `Progman` overlay ownership, multi-monitor coordinate math, and live watcher internals.
- **[Architecture Diagrams (`documentation/diagrams/`)](documentation/diagrams/)** — Editable Mermaid (`.mmd`) and 4x high-resolution `.png` diagrams.
- **[Installer](Install-DesktopOrg.ps1)** & **[Uninstaller](Uninstall-DesktopOrg.ps1)** — One-click PowerShell scripts (no Administrator rights required).

---

## Key Features

1. **Per-Monitor Translucent Gray Boxes ("Desk Zones")**
   - Place customizable translucent Gray Boxes on **any connected monitor**.
   - Drag, resize, rename, recolor, or lock boxes in place; double-click a box header to collapse/shade it, or hover to auto-expand when **Hover Auto-Expand** is enabled.
   - Assign auto-routing rules (`Folders`, `Files`, `Web Links .url`, `App Shortcuts`, and custom file extensions like `.pdf,.docx` or `*`) so matching desktop items automatically route into specific Gray Boxes.
   - Scope individual Gray Boxes to **All Virtual Desktops (`*`)** or a specific **Windows 10/11 Virtual Desktop**.

2. **Multi-Monitor Topology Memory & Offline Monitor Cache**
   - Automatically remembers separate icon and Gray Box layouts per monitor setup (`MonitorSignature`, e.g., docked multi-monitor desk vs. undocked laptop screen).
   - When a display is unplugged, its Gray Boxes and desktop icons remain accessible via an `(Offline — Cached)` submenu in both the System Tray and Desktop Right-Click menus.

3. **Three-Group Desktop Separation & Domain Sorting**
   - Organizes unboxed desktop icons into three distinct groups with an optional 1-column blank separator gap:
     1. **System & App Shortcuts** (`This PC`, `Recycle Bin`, `.lnk`, `.exe`)
     2. **Folders & Local Files** (directories, `.pdf`, `.docx`, `.txt`, `.ps1`, etc.)
     3. **Internet Shortcuts (`.url`)**
   - **Domain Grouping for `.url` Bookmarks**: Parses `URL=` targets inside `.url` files and groups web links by domain (`Domain (A-Z)` or `Domain (Most Links First)`).

4. **Hierarchical Monitor Menus (System Tray & Desktop Right-Click Menu)**
   - Queries hardware EDID monitor names via Win32 `QueryDisplayConfig` (e.g., `Monitor 1: Left - PHL 278E9Q`, `Monitor 2: Main - Z34B [Primary]`).
   - Provides instant access to every desktop item and Gray Box grouped by monitor directly from the **System Tray icon** and the **Windows Desktop Right-Click Context Menu**.

5. **Two-Pass Collision-Free Icon Positioning & Undo History**
   - Positions desktop icons via `IFolderView::SelectAndPositionItems` in two passes calibrated against each monitor's usable work area so Windows Explorer's *"Align icons to grid"* never scrambles bottom-row icons.
   - Automatically snapshots icon coordinates before every arrangement so you can revert with one click (`Undo`).

---

## Quick Start

### Option A: One-Click Installer from GitHub Releases (Recommended)

1. Download **`DesktopOrg-Setup.exe`** from the [**Latest GitHub Release**](https://github.com/jimbreckel/DesktopOrg/releases/latest).
2. Double-click **`DesktopOrg-Setup.exe`** — it automatically installs `DesktopOrg.exe` to `%LocalAppData%\Programs\DesktopOrg`, creates a Start Menu shortcut, registers Windows Startup (`--tray`), adds the cascading **DesktopOrg** menu to the Windows Desktop right-click menu, and launches DesktopOrg.

### Option B: Build from Source

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) on Windows:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o ./dist
```

### 2. Install for Current User (No Admin Required)

Copies `DesktopOrg.exe` to `%LocalAppData%\Programs\DesktopOrg`, creates a Start Menu shortcut, registers Windows Startup (`--tray`), and adds the cascading **DesktopOrg** menu to the Windows Desktop right-click menu:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-DesktopOrg.ps1
```

### 3. Command-Line Arguments

| Argument | Description |
| :--- | :--- |
| *(none)* | Launches the DesktopOrg settings window, System Tray icon, and Desktop Gray Box overlays. |
| `--tray` | Starts minimized directly to the Windows System Tray with Desktop Gray Box overlays active. |
| `--organize` | Immediately runs `Apply & Organize Desktop` using your saved rules (signals running instance if already open). |
| `--undo` | Restores desktop icons to their previous snapshot coordinates. |
| `--gather` | Gathers off-screen or secondary-monitor icons onto your target screen. |
| `--toggle-zones` | Toggles Desktop Gray Box visibility on/off. |
| `--toggle-icons` | Toggles native Windows Desktop icon visibility on/off. |

### 4. Uninstall

```powershell
# Remove installed app, Start Menu shortcut, Startup entry, and Desktop Right-Click menu
powershell -ExecutionPolicy Bypass -File .\Uninstall-DesktopOrg.ps1

# Also delete saved layouts & settings in %LocalAppData%\DesktopOrg
powershell -ExecutionPolicy Bypass -File .\Uninstall-DesktopOrg.ps1 -DeleteSavedLayoutsAndSettings
```

---

## Project Structure

```text
DesktopOrg/
├── Assets/                     # Multi-size application icon (.ico & .png)
├── Models/
│   └── AppModels.cs            # Serializable state, DeskArea, DesktopIconItem, and rule models
├── Services/
│   ├── DeskManagerService.cs   # Core arrangement engine, monitor layout memory & state persistence
│   ├── DesktopInterop.cs       # Win32 / COM Shell (IShellWindows, IFolderView, SysListView32) interop
│   └── DesktopNativeHook.cs    # Progman ownership, Virtual Desktops, DisplayConfig & Context Menu registry
├── Views/
│   └── DeskZoneWindow.xaml(.cs)# Translucent per-monitor Gray Box overlay windows
├── documentation/              # Detailed User Guide, Architecture docs, and Mermaid/PNG diagrams
├── App.xaml(.cs)               # Single-instance mutex, CLI router & System Tray context menu
├── MainWindow.xaml(.cs)        # WPF visual editor, monitor preview canvas & rules manager
├── Install-DesktopOrg.ps1      # Per-user installer script
├── Uninstall-DesktopOrg.ps1    # Per-user uninstaller script
└── LICENSE                     # MIT License
```

---

## License

This project is open source and available under the [MIT License](LICENSE).
