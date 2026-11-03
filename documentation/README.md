# DesktopOrg — Native Windows Desktop Icon & Gray Box Organizer

**DesktopOrg** is a native Windows 10 / Windows 11 (`.NET 9` WPF + WinForms System Tray + Win32/COM Shell Interop) desktop icon and translucent gray-box organizer engineered to replace **ReDesk**, with full support for **Windows 11 24H2 (OS Build 26100 / 26300+)**, multi-monitor setups with mixed DPIs and negative screen coordinates, and 3-way group separation (`App Shortcuts`, `Folders & Local Files`, and `Internet Shortcuts .url`).

- **Compiled Single-File Executable**: [`dist/DesktopOrg.exe`](../dist/DesktopOrg.exe)
- **Application Icons**: [`Assets/DesktopOrg.ico`](../Assets/DesktopOrg.ico) (`16–256px` multi-frame `.ico`) & [`Assets/DesktopOrg.png`](../Assets/DesktopOrg.png)
- **One-Click Installer & Uninstaller Scripts**: [`Install-DesktopOrg.ps1`](../Install-DesktopOrg.ps1) / [`Uninstall-DesktopOrg.ps1`](../Uninstall-DesktopOrg.ps1)
- **User Guide (Plain-English Guide, `?` Hover Tooltips, Custom Gray Boxes, Multi-Monitor & Group Splitting)**: [`USER_GUIDE.md`](./USER_GUIDE.md)
- **Architecture & Technical Deep-Dive**: [`ARCHITECTURE.md`](./ARCHITECTURE.md)
- **Architecture & Workflow Diagrams (Mermaid `.mmd` + 4x Resolution `.png`)**: [`diagrams/`](./diagrams/)

---

## 1. Key Capabilities & Improvements Over ReDesk

1. **Per-Monitor Gray Boxes + Per-Monitor-Topology Layout Memory + Offline Monitor Fallback**:
   - **Per-Monitor Gray Boxes**: Define custom translucent Gray Boxes on **any connected monitor** (not just the primary display). Switching `Target Screen` in `MainWindow` shows that monitor's grid, color-coded icon dots, and Gray Boxes, or you can drag Gray Boxes directly between monitors when unlocked.
   - **Per-Monitor-Topology Layout Memory**: Remembers your specific saved layouts (`SavedAreas`, `Icons`, `SavedMonitorCaches`) for each unique monitor configuration (`MonitorSignature`, e.g., 4-monitor desk setup vs. 1-monitor laptop/undocked setup). When displays change (`SystemEvents.DisplaySettingsChanged`), if a saved layout exists for the new monitor setup, DesktopOrg automatically restores that setup's layout; otherwise, it falls back to gracefully hiding Gray Boxes on unplugged monitors while preserving their configurations and cached items in the menus.
   - **Multi-Monitor Coordinate Translation ([`DeskManagerService.GetMonitorOrigins()`](../Services/DeskManagerService.cs) & [`DesktopInterop.GetTargetMonitorListViewOrigin()`](../Services/DesktopInterop.cs))**: Windows `SysListView32` (`IFolderView`) stores desktop icon coordinates in physical Virtual Screen pixels relative to `Min(X), Min(Y)` across all monitors (`target.X - rawMinX, target.Y - rawMinY`), whereas top-level overlay windows use physical screen coordinates (`0, 0` on the Primary Monitor) scaled by per-monitor DPI (`GetDpiForWindow`).
   - **High-DPI `Progman`-Owned Gray Box Overlays ([`DeskZoneWindow.xaml.cs`](../Views/DeskZoneWindow.xaml.cs))**: Sets `Progman` as the Win32 owner (`GWLP_HWNDPARENT = -8`) of each translucent `WS_POPUP` Gray Box window and queries `GetDpiForWindow(hwnd)` after moving to the target monitor.
2. **Hierarchical Monitor Menu (System Tray Menu & Desktop Right-Click Context Menu)**:
   - Generates human-friendly monitor names via Win32 `QueryDisplayConfig` / `DisplayConfigGetDeviceInfo` (e.g., `Monitor 1: Left - PHL 278E9Q (1920x1080)`, `Monitor 2: Main - Z34B (3440x1440) [Primary]`).
   - Organizes all desktop items in **both** the **System Tray Menu** and the **Windows Desktop Right-Click Context Menu** (`HKCU\Software\Classes\Directory\Background\shell\DesktopOrg`) directly under each monitor:
     - `Monitor Name` $\rightarrow$
       - `App Shortcuts (<count>)` $\rightarrow$ clickable application & system shortcuts with native icons
       - `Folders & Local Files (<count>)` $\rightarrow$ clickable folders & local documents/files with native icons
       - `Internet Shortcuts (.url) (<count>)` $\rightarrow$ clickable web bookmarks grouped by domain (`google.com`, `microsoft.com`, etc.) and `A–Z`
       - `Gray Box: <Title> (<count>)` (`Fixed`, `Plan`, `Ongoing`, `Finish`, or custom boxes on that monitor) $\rightarrow$ clickable items inside each Gray Box
   - Offline monitors remain accessible in both menus with an `(Offline — Cached)` badge so you can still open any file, folder, shortcut, or web link from an unplugged screen.
3. **Windows 10/11 Virtual Desktop Support**:
   - Each Gray Box can be displayed on `All Virtual Desktops (*)` (default) or scoped to a specific Windows Virtual Desktop (`Desktop 1`, `Desktop 2`, etc.).
4. **Three-Group Separation (`App Shortcuts`, `Folders & Local Files`, `Internet Shortcuts .url`) + Domain Sorting**:
   - Distinguishes **System & App Shortcuts (`.lnk` / `.exe`)**, **Folders & Local Files (`.pdf`, `.epub`, `.txt`, `.bat`, `.ps1`, Folders)**, and **Internet Shortcuts (`.url`)**, placing each into its own clean column block with an optional **1-column blank gap** between groups.
   - **Sort Internet Shortcuts by Domain (`Domain (A-Z)` & `Domain (Most Links First)`)**: Automatically parses `URL=...` inside each `.url` file and groups bookmarks by website domain (e.g., `google.com`, `microsoft.com`, `pypi.org`, `salesforce.com`, `stackoverflow.com`).
5. **Two-Pass Collision-Free Positioning & WorkArea Row Calibration**:
   - Calibrates grid rows against each monitor's usable `WorkHeight` and executes `IFolderView::SelectAndPositionItems` in two passes (35ms apart) so Windows Explorer's *"Align icons to grid"* never bumps icons into an overflow row at the bottom of the screen.
6. **Plain-English UI with Interactive Circle `?` Hover Tooltips**:
   - Replaces confusing ReDesk terms with clear English labels and instant circular **`?` hover badges** (`50ms` delay) on every section and setting.
7. **Delete Default Boxes (`Fixed`, `Plan`, `Ongoing`, `Finish`) & Create Custom Gray Boxes**:
   - Delete any default box with **`✕`**, click **`Clear All Boxes`**, load **`Load Category Boxes`** (`Folders & Files` + `Web Links (.url)`), or add your own custom Gray Boxes on any monitor with **`+ Add Gray Box`**.

---

## 2. Architecture & Workflow Diagrams (4x Resolution PNG + Mermaid)

All diagrams in [`documentation/diagrams/`](./diagrams/) are provided in both editable Mermaid source (`.mmd`) and 4x high-resolution PNG (`.png`):

| Diagram | Mermaid Source | 4x Resolution PNG | Description |
| :--- | :--- | :--- | :--- |
| **01. System Architecture** | [`01_system_architecture.mmd`](./diagrams/01_system_architecture.mmd) | [`01_system_architecture.png`](./diagrams/01_system_architecture.png) | Component architecture across UI, Core Arrangement Engine, Win32/COM Interop, and Windows 11 24H2 OS layers. |
| **02. Windows Desktop Hook & Multi-Monitor Coordinates** | [`02_windows_desktop_hook.mmd`](./diagrams/02_windows_desktop_hook.mmd) | [`02_windows_desktop_hook.png`](./diagrams/02_windows_desktop_hook.png) | How `Progman` ownership (`GWLP_HWNDPARENT`), per-monitor DPI (`GetDpiForWindow`), and VirtualScreen vs. Physical Screen coordinates work. |
| **03. 3-Group Icon Organization Pipeline** | [`03_icon_organization_pipeline.mmd`](./diagrams/03_icon_organization_pipeline.mmd) | [`03_icon_organization_pipeline.png`](./diagrams/03_icon_organization_pipeline.png) | End-to-end `Apply & Organize Desktop` pipeline: Custom Gray Boxes + 3-Group Main Desktop Separation (`App Shortcuts`, `Folders/Files`, `Internet Shortcuts .url`) + 2-Pass `IFolderView`. |
| **04. Live Desktop Watcher Sequence** | [`04_live_desktop_watcher_sequence.mmd`](./diagrams/04_live_desktop_watcher_sequence.mmd) | [`04_live_desktop_watcher_sequence.png`](./diagrams/04_live_desktop_watcher_sequence.png) | Real-time sequence when dragging icons on the desktop, adding files, or clicking `Undo` without self-trigger loops. |

---

## 3. Quick Start, Installation & CLI Flags

```powershell
# Install DesktopOrg to %LocalAppData%\Programs\DesktopOrg, Start Menu, Startup & Desktop Right-Click Menu
powershell -ExecutionPolicy Bypass -File .\Install-DesktopOrg.ps1

# Launch DesktopOrg GUI + System Tray + Desktop Gray Box Overlays
Start-Process "$env:LOCALAPPDATA\Programs\DesktopOrg\DesktopOrg.exe"

# Launch minimized directly to the Windows System Tray (used for Windows Startup)
Start-Process "$env:LOCALAPPDATA\Programs\DesktopOrg\DesktopOrg.exe" -ArgumentList "--tray"

# Trigger an immediate desktop organization from CLI / right-click menu
Start-Process "$env:LOCALAPPDATA\Programs\DesktopOrg\DesktopOrg.exe" -ArgumentList "--organize"
```

### Building a Self-Contained Portable Single-File Executable
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./dist
```
