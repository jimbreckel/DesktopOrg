# DesktopOrg User Guide

This guide explains how to use **DesktopOrg**, including how to use the interactive **`?` hover tooltips**, select your target monitor on a multi-screen setup, separate **App Shortcuts**, **Folders & Local Files**, and **Internet Shortcuts (`.url`)** into distinct column groups, access your desktop items from the **Monitor-organized System Tray & Desktop Right-Click Menus**, and delete or create custom **Desktop Gray Boxes**.

---

## 1. Plain-English Terminology Guide (ReDesk vs. DesktopOrg)

Every section and setting in **DesktopOrg** replaces confusing **ReDesk** labels with plain English and includes a circular **`?` hover badge** that displays an instant explanation (`50ms` hover delay).

| Confusing ReDesk Term | DesktopOrg Plain-English Term | What It Actually Does |
| :--- | :--- | :--- |
| **`Save Change`** | **`Apply & Organize Desktop`** | Immediately snaps icons inside your Gray Boxes into clean grid cells, sorts all remaining icons on your desktop, and updates the System Tray and Desktop Right-Click menus. |
| **`Main Screen`** | **`1. Desktop Layout Preview`** | An interactive grid map of your selected monitor. Colored dots show your current desktop icons (**Blue** = App Shortcuts, **Orange** = Folders & Local Files, **Purple** = Internet Shortcuts `.url`), and gray rectangles show your Gray Boxes. |
| *(Not in ReDesk)* | **`Target Screen` & `Pull icons from all screens`** | Lets you pick which monitor in a multi-monitor setup you are viewing and configuring, and lets you place Gray Boxes on any monitor. |
| **`Custom Areas`** (`Fixed`, `Plan`, `Ongoing`, `Finish`) | **`2. Desktop Gray Boxes (Zones)`** | Shaded translucent gray boxes on your desktop wallpaper where you can group icons manually (by dragging icons into them) or automatically by file type. |
| **`System Area`** | **`3. Main Desktop Sorting (Outside Boxes)`** | Controls how all desktop icons outside your Gray Boxes are organized on the left side of your screen. |
| **`Separate Files and Shortcuts`** | **`Split App Shortcuts, Folders/Files & Web Links into Separate Column Groups`** | Separates your icons into up to 3 distinct column groups (**App Shortcuts `.lnk/.exe`**, **Folders & Local Files**, and **Internet Shortcuts `.url`**) with an optional **1-column empty gap** between groups. |
| **`Special Shortcut Setting of System Area`** | **`Pinned & Locked Desktop Icons`** | Pins specific icons (like `Recycle Bin` or `Microsoft Edge`) to the top-left spot, bottom-left corner, or locks them in place so auto-organize never moves them. |
| **`Areas Background Setting`** | **`4. Desktop Integration & Behavior`** | Controls how Gray Boxes are rendered on your desktop (`Show Gray Boxes (Desktop Hook Layer)`, `Wallpaper Integration`, `Both`, `Show Only When Unlocked`, or `Hide`), plus `Monitor Setup Switch Mode` and `Windows Right-Click Menu`. |

---

## 2. Multi-Monitor Setup, Per-Monitor Gray Boxes & Layout Memory per Monitor Setup

If you have multiple monitors (for example, a 4-monitor setup with `Monitor 1: Left - PHL 278E9Q (1920x1080)`, `Monitor 2: Main - Z34B (3440x1440) [Primary]`, `Monitor 3: Right 1 - PHL 278E9Q (1920x1080)`, and `Monitor 4: Right 2 (1536x864)`):

1. **Generated Friendly Monitor Names**:
   - DesktopOrg automatically queries Windows Display Configuration (`QueryDisplayConfig` / `DisplayConfigGetDeviceInfo`) to generate descriptive names for every monitor including its spatial position, hardware model, resolution, and `[Primary]` tag.
2. **Define Gray Boxes on Any Monitor**:
   - In **Section 1 (`Desktop Layout Preview`)**, select any monitor in the **`Target Screen`** dropdown.
   - The preview grid immediately switches to that monitor's exact grid dimensions, color-coded icon dots (`Blue` = App Shortcuts, `Orange` = Folders & Local Files, `Purple` = Internet Shortcuts `.url`), and Gray Boxes.
   - Any new Gray Box you add with **`+ Add Gray Box`** is created on the currently selected monitor.
   - You can also expand any existing Gray Box in **Section 2** and change its **`Screen / Monitor`** dropdown to move it to another monitor, or click **`Unlock Desktop Boxes`** to drag a Gray Box directly from one monitor to another on your wallpaper!
3. **Remember Layout per Monitor Setup + Graceful Disconnect Fallback (`Monitor Setup Switch Mode`)**:
   - In **Section 4 (`Desktop Integration & Behavior`)**, **`Monitor Setup Switch Mode`** defaults to **`Remember Layout per Monitor Setup (Fallback: Hide Offline)`**:
     - Whenever you click **`Apply & Organize Desktop`** (or save a snapshot in **Section 5** with **`Auto-Restore on Setup`** checked), DesktopOrg saves your complete icon positions, per-monitor caches, and Gray Boxes for your current display configuration (`MonitorSignature`).
     - If you switch between different monitor setups (for example, undocking from a 4-monitor desk setup to a 1-monitor or 2-monitor setup, and later reconnecting your 4 monitors), DesktopOrg automatically recognizes the connected monitor signature and restores the saved layout for that exact monitor setup.
     - **Fallback to Hiding Offline Monitors**: If no saved layout exists for a newly connected/disconnected monitor combination (or if you select **`Keep Current Layout & Hide Offline Monitor Boxes`**), DesktopOrg safely hides any Gray Boxes belonging to unplugged monitors without deleting them or clumping them onto your remaining screen, and automatically restores them the moment that monitor is plugged back in.
4. **Hierarchical Monitor Menu (System Tray & Desktop Right-Click Context Menu)**:
   - Both the **System Tray right-click menu** and the **Windows Desktop right-click context menu** (`DesktopOrg`) organize all of your desktop items directly under each monitor's generated name:
     - **`Monitor 2: Main - Z34B (3440x1440) [Primary]`**
       - **`App Shortcuts (45)`** $\rightarrow$ clickable application & system shortcuts with real icons
       - **`Folders & Local Files (11)`** $\rightarrow$ clickable folders & local documents/files with real icons
       - **`Internet Shortcuts (.url) (39)`** $\rightarrow$ clickable web bookmarks grouped by website domain (`google.com`, `microsoft.com`, etc.) and `All Internet Shortcuts (A-Z)`
       - **`Gray Box: Fixed`**, **`Gray Box: Plan`**, **`Gray Box: Ongoing`**, **`Gray Box: Finish`** $\rightarrow$ clickable items inside each Gray Box on that monitor
     - **`Monitor 1: Left - PHL 278E9Q (1920x1080)`**
       - **`App Shortcuts`**, **`Folders & Local Files`**, **`Internet Shortcuts (.url)`**, and any **`Gray Boxes`** on Monitor 1
   - Clicking any group or item in the Windows Desktop right-click menu or System Tray menu immediately opens the full icon-rendered popup or launches the selected application, folder, file, or website! Even if a monitor is currently unplugged (`Offline — Cached`), its cached Desktop Groups and Gray Box items remain accessible in the menu.
5. **Windows 10/11 Virtual Desktop Support**:
   - Expand any Gray Box in **Section 2** and use the **`Virtual Desktop`** dropdown to show that Gray Box on **`All Virtual Desktops (*)`** *(default)* or scope it to a specific Windows Virtual Desktop (`Desktop 1`, `Desktop 2`, etc.).
6. **`Pull icons from all screens`**:
   - When **unchecked** *(recommended when using multiple monitors)*, each monitor keeps and organizes its own desktop icons independently.
   - When **checked**, clicking **`Apply & Organize Desktop`** gathers loose desktop icons onto your selected Target Screen (while still respecting Gray Boxes placed on other monitors).
7. **`Start DesktopOrg automatically when computer starts (in System Tray)`**:
   - Located in the top Quick-Setup card under **Section 1** (as well as in **Section 4** and the **System Tray right-click menu**).
   - When **checked**, DesktopOrg registers itself in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (`DesktopOrg.exe --tray`).

---

## 3. Deleting Default Gray Boxes (`Fixed`, `Plan`, `Ongoing`, `Finish`) & Creating Custom Boxes

`Fixed`, `Plan`, `Ongoing`, and `Finish` are only the initial defaults. You have full control over them in **Section 2 (`2. Desktop Gray Boxes (Zones)`)**:

### Delete One or All Default Boxes
- **Delete a Single Box**: Click the red **`✕`** button on the left side of `Fixed`, `Plan`, `Ongoing`, or `Finish`.
- **Delete All Boxes at Once**: Click the red **`Clear All Boxes`** button in the Section 2 header.
- **Load Category Auto-Sort Boxes**: Click **`Load Category Boxes`** to replace the default boxes with two automatic boxes on the right side of your screen:
  - **`Folders & Files`** (automatically collects all folders and local documents/files)
  - **`Web Links (.url)`** (automatically collects all Chrome/Edge `.url` web bookmarks)
- **Restore Default 4**: Click **`Reset Default 4`** anytime to bring back `Fixed`, `Plan`, `Ongoing`, and `Finish`.

### Create Your Own Custom Gray Box
1. In the **New Box Name** bar in Section 2, type any title (e.g., `Projects`, `Work Docs`, `Bookmarks`, `Clients`).
2. Choose **Which Icons Go Here?**:
   - **`Manual (Drop Icons Here)`**: Any desktop icon you physically drag onto this Gray Box on your wallpaper will snap into and stay inside this box.
   - **`Folders & Files`**: Automatically pulls all desktop folders and local files (`.pdf`, `.epub`, `.txt`, `.docx`, scripts, etc.) into this box.
   - **`Web Links (.url)`**: Automatically pulls all browser web bookmarks (`.url`) into this box.
   - **`Folders`**, **`Shortcuts`**, **`Documents`**, **`Media`**, or **`Code & Archives`**: Automatically pulls that specific category into this box.
3. Click **`+ Add Gray Box`**.
4. Drag the new box on the **Desktop Layout Preview** grid (or click **`Unlock Desktop Boxes`** at the top right to drag and resize the box directly on your desktop wallpaper), then click **`Apply & Organize Desktop`**.

### Customize a Gray Box's Appearance, Monitor, Virtual Desktop & Sorting
Click any Gray Box row in Section 2 to expand its settings:
- **Box Title**: Rename the box at any time (updates live on your desktop).
- **Screen / Monitor**: Move the Gray Box to any connected monitor (`Monitor 1`, `Monitor 2`, `Monitor 3`, `Monitor 4`).
- **Virtual Desktop**: Show on `All Virtual Desktops (*)` or scope to a specific Windows 10/11 Virtual Desktop.
- **Which Icons Go Here?**: Switch between `Manual (Drop Icons Here)` and automatic category rules.
- **Sort Icons Inside Box**: Choose `Keep Grid Position` (preserves where you dropped each icon inside the box), `Name (A-Z)`, or `Date Modified`.
- **Box Shade (Gray Color)**: Choose `Classic Slate Gray`, `Charcoal Dark Gray`, `Deep Obsidian`, `Warm Gray`, `Cool Steel Gray`, or `Forest Slate`.
- **Box Opacity (25% – 90%)**: Adjust how translucent or solid the smoked-gray background appears over your wallpaper.
- **Title Position**: Choose `Center` (watermark style), `Top-Left` (header style), or `Hidden`.

---

## 4. Splitting App Shortcuts, Folders/Files & Internet Shortcuts on the Main Desktop

In **Section 3 (`3. Main Desktop Sorting (Outside Boxes)`)**, DesktopOrg separates browser bookmarks (`.url`), folders/files, and application shortcuts (`.lnk`):

1. **`Split App Shortcuts, Folders/Files & Web Links into Separate Column Groups`** *(checked by default)*:
   - **Group 1 — System Icons & App Shortcuts (`.lnk` / `.exe`)** *(Blue dots in preview)*: Placed in the left-most columns (`Control Panel`, `This PC`, `Recycle Bin`, followed by your application shortcuts sorted A–Z or by Date Modified).
   - **Group 2 — Folders & Local Files** *(Orange dots in preview)*: Starts in a new column to the right of your App Shortcuts, placing folders first (`devart`, `Elastic Tabstops`, etc.) followed by local files (`.pdf`, `.epub`, `.txt`, `.bat`, `.ps1`, `.cfg`).
   - **Group 3 — Internet Shortcuts (`.url`)** *(Purple dots in preview)*: Places all Chrome/Edge `.url` web links into their own dedicated columns to the right of your Folders & Local Files.
2. **`Leave 1 empty column gap between each group`** *(checked by default)*:
   - Leaves 1 blank vertical column on your wallpaper between **App Shortcuts**, **Folders & Local Files**, and **Internet Shortcuts (`.url`)** so you can visually distinguish each group at a glance.
3. **`Web Links (.url) Group` Dropdown**:
   - Choose between **`Separate Group (Own Columns)`** *(default)*, **`Group with Files`**, or **`Group with App Shortcuts`**.
4. **`Sort Web Links (.url) By` Dropdown**:
   - **`Domain (A-Z)`** *(default)*: Parses the target `URL=` inside each `.url` internet shortcut and groups your bookmarks alphabetically by website domain (e.g., all `google.com` links together, `microsoft.com` links together, `pypi.org` links together, `salesforce.com` links together, `stackoverflow.com` links together).
   - **`Domain (Most Links First)`**: Groups bookmarks by website domain with your most frequently bookmarked domains placed first.
   - **`Name (A-Z)`** / **`Date Modified`**: Sorts web links by title or file modification date.
   - *(Tip: Hover over any colored dot in the **Desktop Layout Preview** to see that icon's name, category, and parsed domain!)*

---

## 5. Installing on This Computer or Other Computers (`Install-DesktopOrg.ps1` & In-App Button)

DesktopOrg is compiled as a **self-contained single-file executable** (`dist\DesktopOrg.exe`), meaning you can copy `dist\DesktopOrg.exe` (and `Install-DesktopOrg.ps1`) to **any Windows 10 or Windows 11 computer** and run it immediately without needing to install .NET first.

To install DesktopOrg permanently into your user profile (no Administrator privileges required), you can use **either** the in-app button or the PowerShell installer script:

### Option A — Click `Install to Programs & Start Menu` Inside the App
1. Open `DesktopOrg.exe` on any computer.
2. In the top Quick-Setup card under **Section 1**, click **`Install to Programs & Start Menu`**.
3. DesktopOrg will automatically:
   - Copy `DesktopOrg.exe` to **`%LocalAppData%\Programs\DesktopOrg\DesktopOrg.exe`**
   - Create a **`DesktopOrg` shortcut in your Windows Start Menu** (`%AppData%\Microsoft\Windows\Start Menu\Programs\DesktopOrg.lnk`)
   - Point **Windows Startup (`HKCU\...\Run\DesktopOrg`)** and the **Windows Desktop Right-Click Context Menu (`HKCU\Software\Classes\Directory\Background\shell\DesktopOrg`)** to `%LocalAppData%\Programs\DesktopOrg\DesktopOrg.exe`

### Option B — Run `Install-DesktopOrg.ps1`
```powershell
powershell -ExecutionPolicy Bypass -File .\Install-DesktopOrg.ps1
```
To uninstall later:
```powershell
powershell -ExecutionPolicy Bypass -File .\Uninstall-DesktopOrg.ps1
```

---

## 6. Saving & Restoring Desktop Backups (`Undo` & `Saved Backups`)

- **Instant `Undo`**: Every time you click **`Apply & Organize Desktop`**, DesktopOrg saves an Undo snapshot first. Click **`Undo`** on the top bar (or in the System Tray menu) to revert to your previous icon positions.
- **Named Desktop Backups (Section 5 -> `Saved Desktop Backups & Layouts`)**:
  - When DesktopOrg first runs, it automatically preserves **`Initial Desktop Backup`** containing your original icon coordinates.
  - Each saved layout also records your monitor configuration (`MonitorSignature`) and includes an **`Auto-Restore on Setup`** checkbox so switching between docked and undocked monitor setups automatically restores your preferred layout.
