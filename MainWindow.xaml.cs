using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MyReDesk.Models;
using MyReDesk.Services;

namespace MyReDesk;

public partial class MainWindow : Window
{
    private readonly DeskManagerService _service;
    private bool _isPopulatingUi;
    private List<DesktopIconItem> _cachedDesktopIcons = new();

    // Dragging state on the Main Screen Grid Canvas
    private CustomAreaConfig? _draggingArea;
    private bool _isResizingArea;
    private Point _dragStartMouse;
    private int _dragStartCol;
    private int _dragStartRow;
    private int _dragStartW;
    private int _dragStartH;

    public MainWindow(DeskManagerService service)
    {
        InitializeComponent();
        _service = service;

        _service.StateChanged += OnServiceStateChanged;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshDesktopIconsCache();
        PopulateAllUi();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_service.State.MinimizeToTrayOnClose && !App.IsExitingApplication)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnServiceStateChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!_isPopulatingUi)
            {
                _isPopulatingUi = true;
                try
                {
                    bool startupEnabled = _service.IsRunAtStartupEnabled();
                    if (TopRunAtStartupCheck != null) TopRunAtStartupCheck.IsChecked = startupEnabled;
                    if (RunAtStartupCheck != null) RunAtStartupCheck.IsChecked = startupEnabled;
                }
                finally
                {
                    _isPopulatingUi = false;
                }
                UpdateLockButtonUi();
                DrawMainScreenGrid();
            }
        });
    }

    public void RefreshDesktopIconsCache()
    {
        try
        {
            var activeTarget = _service.GetTargetMonitor();
            _cachedDesktopIcons = _service.GetDesktopIconsOnMonitor(activeTarget);
        }
        catch
        {
            _cachedDesktopIcons = new List<DesktopIconItem>();
        }
    }

    public void PopulateAllUi()
    {
        _isPopulatingUi = true;
        try
        {
            GridInfoText.Text = $"{_service.State.GridCols}×{_service.State.GridRows} Grid ({_cachedDesktopIcons.Count} Icons on Screen)";
            UpdateLockButtonUi();

            // Populate Multi-Monitor Target Screen Combo using generated FriendlyName + box count
            TargetMonitorCombo.Items.Clear();
            var monitors = DesktopInterop.GetMonitors();
            var activeTarget = _service.GetTargetMonitor();
            foreach (var mon in monitors)
            {
                int boxCount = _service.State.CustomAreas.Count(a =>
                    string.Equals(a.MonitorDeviceName, mon.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                    (mon.IsPrimary && string.IsNullOrWhiteSpace(a.MonitorDeviceName)));

                string label = $"{mon.FriendlyName} — {boxCount} {(boxCount == 1 ? "Box" : "Boxes")}";
                var cbi = new ComboBoxItem
                {
                    Content = label,
                    Tag = mon.DeviceName
                };
                TargetMonitorCombo.Items.Add(cbi);
                if (string.Equals(mon.DeviceName, activeTarget.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    TargetMonitorCombo.SelectedItem = cbi;
                }
            }
            if (TargetMonitorCombo.SelectedItem == null && TargetMonitorCombo.Items.Count > 0)
            {
                TargetMonitorCombo.SelectedIndex = 0;
            }
            GatherAllMonitorsCheck.IsChecked = _service.State.GatherAllMonitorsIconsToTarget;

            var sys = _service.State.SystemArea;
            SeparateFilesShortcutsCheck.IsChecked = sys.SeparateFilesAndShortcuts;
            LeaveGapBetweenGroupsCheck.IsChecked = sys.LeaveEmptyColumnBetweenGroups;
            SelectComboByContent(WebLinksGroupCombo, sys.WebLinksGroupMode);
            SelectComboByContent(WebLinkSortCombo, sys.WebLinkSortBy);
            SelectComboByContent(ShortcutSortCombo, sys.ShortcutSortBy);
            SelectComboByContent(ShortcutDirectionCombo, sys.ShortcutDirection);
            SystemIconsFirstCheck.IsChecked = sys.SystemIconsFirst;

            SelectComboByContent(FileSortCombo, sys.FileSortBy);
            SelectComboByContent(FileDirectionCombo, sys.FileDirection);
            FoldersBeforeFilesCheck.IsChecked = sys.FoldersBeforeFiles;

            SelectComboByTag(AreaBgModeCombo, _service.State.AreaBackgroundMode);
            SelectComboByTag(UseDesktopMenuCombo, DesktopNativeHook.IsDesktopContextMenuEnabled() ? "Yes" : "No");
            if (DisconnectedMonitorModeCombo != null)
            {
                SelectComboByTag(DisconnectedMonitorModeCombo, _service.State.DisconnectedMonitorMode);
            }
            LiveDesktopHookCheck.IsChecked = _service.State.AutoArrangeOnDesktopChange;
            AutoRestoreMonitorsCheck.IsChecked = _service.State.AutoRestoreOnDisplayChange;
            bool startupEnabled = _service.IsRunAtStartupEnabled();
            if (TopRunAtStartupCheck != null) TopRunAtStartupCheck.IsChecked = startupEnabled;
            if (RunAtStartupCheck != null) RunAtStartupCheck.IsChecked = startupEnabled;
            UpdateInstallButtonUi();

            ProfileCombo.Items.Clear();
            foreach (var prof in _service.State.Profiles)
            {
                var item = new ComboBoxItem
                {
                    Content = prof.Name,
                    Tag = prof.Id
                };
                ProfileCombo.Items.Add(item);
                if (prof.Id == _service.State.ActiveProfileId)
                {
                    ProfileCombo.SelectedItem = item;
                }
            }
            if (ProfileCombo.SelectedItem == null && ProfileCombo.Items.Count > 0)
            {
                ProfileCombo.SelectedIndex = 0;
            }

            RebuildCustomAreasAccordionList();
            RebuildSpecialShortcutsList();
            RebuildSavedLayoutsList();
            DrawMainScreenGrid();
        }
        finally
        {
            _isPopulatingUi = false;
        }
    }

    private void UpdateLockButtonUi()
    {
        bool locked = _service.State.AreZonesLocked;
        ToggleDesktopDragText.Text = locked ? "Unlock Desktop Boxes" : "✓ Lock Desktop Boxes";
        ToggleDesktopDragBtn.BorderBrush = locked
            ? new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB))
            : new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    }

    private void UpdateInstallButtonUi()
    {
        if (InstallAppBtn == null) return;
        bool installed = _service.IsInstalledInLocalPrograms();
        InstallAppBtn.Content = installed
            ? "✓ Installed in Programs & Start Menu"
            : "Install to Programs & Start Menu";
    }

    private Border CreateHelpBadge(string tooltipText)
    {
        var border = new Border
        {
            Style = (Style)FindResource("HelpCircleBadge"),
            ToolTip = tooltipText
        };
        border.Child = new TextBlock
        {
            Style = (Style)FindResource("HelpCircleText")
        };
        return border;
    }

    private void AddFormLabelWithHelp(Grid grid, int row, string labelText, string helpTooltip)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 8)
        };
        sp.Children.Add(new TextBlock
        {
            Text = labelText,
            Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        });
        sp.Children.Add(CreateHelpBadge(helpTooltip));

        Grid.SetRow(sp, row);
        Grid.SetColumn(sp, 0);
        grid.Children.Add(sp);
    }

    #region 1. Interactive "Desktop Layout Preview" Grid Canvas

    private void GridPreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawMainScreenGrid();
    }

    public void DrawMainScreenGrid()
    {
        if (GridPreviewCanvas == null) return;
        GridPreviewCanvas.Children.Clear();

        double canvasW = GridPreviewCanvas.ActualWidth;
        double canvasH = GridPreviewCanvas.ActualHeight;
        if (canvasW < 50 || canvasH < 50) return;

        int cols = Math.Max(1, _service.State.GridCols);
        int rows = Math.Max(1, _service.State.GridRows);

        double cellW = canvasW / cols;
        double cellH = canvasH / rows;

        var gridLineBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0xE0, 0xE6));

        for (int c = 0; c <= cols; c++)
        {
            GridPreviewCanvas.Children.Add(new Line
            {
                X1 = c * cellW, Y1 = 0, X2 = c * cellW, Y2 = canvasH,
                Stroke = gridLineBrush, StrokeThickness = 1
            });
        }

        for (int r = 0; r <= rows; r++)
        {
            GridPreviewCanvas.Children.Add(new Line
            {
                X1 = 0, Y1 = r * cellH, X2 = canvasW, Y2 = r * cellH,
                Stroke = gridLineBrush, StrokeThickness = 1
            });
        }

        // Draw color-coded dots for current desktop icons on the grid:
        // Blue = App Shortcuts, Orange = Folders & Local Files, Purple = Web Bookmarks (.url)
        foreach (var icon in _cachedDesktopIcons)
        {
            if (icon.GridCol < 0 || icon.GridCol >= cols || icon.GridRow < 0 || icon.GridRow >= rows)
                continue;

            Brush dotBrush;
            if (icon.IsWebLink)
                dotBrush = new SolidColorBrush(Color.FromRgb(0xA8, 0x55, 0xF7)); // Purple = Web Links (.url)
            else if (icon.IsFolder || !icon.IsShortcut)
                dotBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // Orange = Folders & Files
            else
                dotBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)); // Blue = App Shortcuts

            string dotTip = icon.IsWebLink && !string.IsNullOrWhiteSpace(icon.WebDomain)
                ? $"{icon.Name} (Domain: {icon.WebDomain})"
                : $"{icon.Name} ({icon.Category})";

            var dot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = dotBrush,
                ToolTip = dotTip
            };
            Canvas.SetLeft(dot, icon.GridCol * cellW + (cellW - 6) / 2);
            Canvas.SetTop(dot, icon.GridRow * cellH + (cellH - 6) / 2);
            GridPreviewCanvas.Children.Add(dot);
        }

        var activeTarget = _service.GetTargetMonitor();
        var areasOnTarget = _service.State.CustomAreas
            .Where(a => a.IsVisible &&
                        (string.Equals(a.MonitorDeviceName, activeTarget.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                         (activeTarget.IsPrimary && string.IsNullOrWhiteSpace(a.MonitorDeviceName))))
            .ToList();

        foreach (var area in areasOnTarget)
        {
            double x = area.GridCol * cellW + 2;
            double y = area.GridRow * cellH + 2;
            double w = Math.Max(cellW - 4, area.GridWidthCols * cellW - 4);
            double h = Math.Max(cellH - 4, area.GridHeightRows * cellH - 4);

            Color baseGray = ParseColor(area.GrayHex, Color.FromRgb(0x9E, 0xA3, 0xAE));
            var boxBorder = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(215, baseGray.R, baseGray.G, baseGray.B)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.SizeAll,
                Tag = area,
                ToolTip = $"{area.Title} ({area.GridWidthCols}×{area.GridHeightRows} cells on {activeTarget.FriendlyName})\n• Drag center to move box\n• Drag bottom-right corner to resize"
            };

            var innerGrid = new Grid();
            var label = new TextBlock
            {
                Text = area.Title,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            innerGrid.Children.Add(label);

            var resizeCorner = new Border
            {
                Width = 12,
                Height = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(105, 0, 0, 0)),
                CornerRadius = new CornerRadius(2, 0, 5, 0),
                Cursor = Cursors.SizeNWSE,
                Tag = area,
                ToolTip = "Drag to resize this gray box"
            };
            resizeCorner.MouseLeftButtonDown += AreaResizeCorner_MouseLeftButtonDown;
            innerGrid.Children.Add(resizeCorner);

            boxBorder.Child = innerGrid;
            boxBorder.MouseLeftButtonDown += AreaBoxOnGrid_MouseLeftButtonDown;

            Canvas.SetLeft(boxBorder, x);
            Canvas.SetTop(boxBorder, y);
            GridPreviewCanvas.Children.Add(boxBorder);
        }

        GridPreviewCanvas.MouseMove -= GridPreviewCanvas_MouseMove;
        GridPreviewCanvas.MouseMove += GridPreviewCanvas_MouseMove;
        GridPreviewCanvas.MouseLeftButtonUp -= GridPreviewCanvas_MouseLeftButtonUp;
        GridPreviewCanvas.MouseLeftButtonUp += GridPreviewCanvas_MouseLeftButtonUp;
    }

    private void AreaBoxOnGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is CustomAreaConfig area)
        {
            _draggingArea = area;
            _isResizingArea = false;
            _dragStartMouse = e.GetPosition(GridPreviewCanvas);
            _dragStartCol = area.GridCol;
            _dragStartRow = area.GridRow;
            GridPreviewCanvas.CaptureMouse();
            e.Handled = true;
        }
    }

    private void AreaResizeCorner_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is CustomAreaConfig area)
        {
            _draggingArea = area;
            _isResizingArea = true;
            _dragStartMouse = e.GetPosition(GridPreviewCanvas);
            _dragStartW = area.GridWidthCols;
            _dragStartH = area.GridHeightRows;
            GridPreviewCanvas.CaptureMouse();
            e.Handled = true;
        }
    }

    private void GridPreviewCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingArea == null || e.LeftButton != MouseButtonState.Pressed) return;

        int cols = Math.Max(1, _service.State.GridCols);
        int rows = Math.Max(1, _service.State.GridRows);
        double cellW = GridPreviewCanvas.ActualWidth / cols;
        double cellH = GridPreviewCanvas.ActualHeight / rows;
        if (cellW <= 1 || cellH <= 1) return;

        Point cur = e.GetPosition(GridPreviewCanvas);
        int dCols = (int)Math.Round((cur.X - _dragStartMouse.X) / cellW);
        int dRows = (int)Math.Round((cur.Y - _dragStartMouse.Y) / cellH);

        if (_isResizingArea)
        {
            int newW = Math.Clamp(_dragStartW + dCols, 2, cols - _draggingArea.GridCol);
            int newH = Math.Clamp(_dragStartH + dRows, 1, rows - _draggingArea.GridRow);
            if (newW != _draggingArea.GridWidthCols || newH != _draggingArea.GridHeightRows)
            {
                _draggingArea.GridWidthCols = newW;
                _draggingArea.GridHeightRows = newH;
                DrawMainScreenGrid();
            }
        }
        else
        {
            int newCol = Math.Clamp(_dragStartCol + dCols, 0, cols - _draggingArea.GridWidthCols);
            int newRow = Math.Clamp(_dragStartRow + dRows, 0, rows - _draggingArea.GridHeightRows);
            if (newCol != _draggingArea.GridCol || newRow != _draggingArea.GridRow)
            {
                _service.MoveCustomAreaOnGrid(_draggingArea, newCol, newRow, moveIconsOnDesktopImmediately: false);
                DrawMainScreenGrid();
            }
        }
    }

    private void GridPreviewCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingArea != null)
        {
            _draggingArea = null;
            _isResizingArea = false;
            GridPreviewCanvas.ReleaseMouseCapture();
            _service.SaveState();
            _service.ApplyAreaBackgrounds();
            RebuildCustomAreasAccordionList();
            StatusText.Text = "Updated gray box on grid. Click 'Apply & Organize Desktop' to snap icons.";
        }
    }

    #endregion

    #region 2. Desktop Gray Boxes Accordion List (With Clear Labels & ? Hovers)

    private void RebuildCustomAreasAccordionList()
    {
        CustomAreasContainer.Children.Clear();

        if (_service.State.CustomAreas.Count == 0)
        {
            var emptyBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 14, 16, 14)
            };
            emptyBorder.Child = new TextBlock
            {
                Text = "No Gray Boxes currently active. Type a name above and click '+ Add Gray Box', or click 'Load Category Boxes'.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                FontSize = 12,
                TextAlignment = TextAlignment.Center
            };
            CustomAreasContainer.Children.Add(emptyBorder);
            return;
        }

        var allMonitors = _service.GetAllMonitorsIncludingCachedOffline();
        var virtualDesktops = DesktopInterop.GetVirtualDesktops();
        var activeTarget = _service.GetTargetMonitor();

        // Order so boxes on the currently selected Target Screen appear first, followed by boxes on other monitors
        var orderedAreas = _service.State.CustomAreas
            .OrderByDescending(a =>
            {
                var m = _service.ResolveMonitorForArea(a);
                return m != null && string.Equals(m.DeviceName, activeTarget.DeviceName, StringComparison.OrdinalIgnoreCase);
            })
            .ThenBy(a => a.MonitorFriendlyName ?? string.Empty)
            .ThenBy(a => a.GridRow)
            .ToList();

        foreach (var area in orderedAreas)
        {
            var resolvedMon = _service.ResolveMonitorForArea(area);
            bool isOnSelectedMonitor = resolvedMon != null &&
                                       string.Equals(resolvedMon.DeviceName, activeTarget.DeviceName, StringComparison.OrdinalIgnoreCase);
            bool isOffline = resolvedMon == null;
            string monShortLabel = resolvedMon != null
                ? $"Monitor {resolvedMon.MonitorNumber}"
                : (!string.IsNullOrWhiteSpace(area.MonitorFriendlyName) ? $"{area.MonitorFriendlyName} (Offline)" : "Offline Monitor");

            var cardBorder = new Border
            {
                Background = isOnSelectedMonitor
                    ? Brushes.White
                    : new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB)),
                BorderBrush = isOnSelectedMonitor
                    ? new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB))
                    : new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var mainStack = new StackPanel();

            var headerGrid = new Grid
            {
                Height = 42,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

            var deleteBtn = new Button
            {
                Content = "✕",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"Delete '{area.Title}' gray box"
            };
            deleteBtn.Click += (_, ev) =>
            {
                ev.Handled = true;
                _service.State.CustomAreas.Remove(area);
                _service.SaveState();
                _service.ApplyAreaBackgrounds();
                RebuildCustomAreasAccordionList();
                DrawMainScreenGrid();
                StatusText.Text = $"Deleted gray box '{area.Title}'.";
            };
            Grid.SetColumn(deleteBtn, 0);
            headerGrid.Children.Add(deleteBtn);

            var titleBlock = new TextBlock
            {
                Text = area.Title,
                FontWeight = FontWeights.Bold,
                FontSize = 13.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleBlock, 1);
            headerGrid.Children.Add(titleBlock);

            string vdBadge = string.IsNullOrWhiteSpace(area.VirtualDesktopId) || area.VirtualDesktopId == "*"
                ? string.Empty
                : $" • {area.VirtualDesktopName}";
            var subtitleBadge = new TextBlock
            {
                Text = $"{monShortLabel}{vdBadge} • {area.AutoRule} • {area.GridWidthCols}×{area.GridHeightRows}",
                FontSize = 11,
                Foreground = isOffline
                    ? new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06))
                    : new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(subtitleBadge, 2);
            headerGrid.Children.Add(subtitleBadge);

            var chevronBlock = new TextBlock
            {
                Text = "⌄",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -4, 0, 0),
                ToolTip = "Click to expand or collapse settings for this gray box"
            };
            Grid.SetColumn(chevronBlock, 3);
            headerGrid.Children.Add(chevronBlock);

            var detailsBorder = new Border
            {
                Visibility = Visibility.Collapsed,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(16, 12, 16, 14),
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFB))
            };

            var formGrid = new Grid();
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(185) });
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 7; i++)
                formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Row 0: Box Title
            AddFormLabelWithHelp(formGrid, 0, "Box Title:",
                "The label displayed inside this gray box on your desktop wallpaper.");
            var nameBox = new TextBox
            {
                Text = area.Title,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 8)
            };
            nameBox.TextChanged += (_, _) =>
            {
                area.Title = nameBox.Text;
                titleBlock.Text = area.Title;
                DrawMainScreenGrid();
                _service.SaveState();
                _service.ApplyAreaBackgrounds();
            };
            Grid.SetRow(nameBox, 0);
            Grid.SetColumn(nameBox, 1);
            formGrid.Children.Add(nameBox);

            // Row 1: Screen / Monitor Assignment
            AddFormLabelWithHelp(formGrid, 1, "Screen / Monitor:",
                "Choose which monitor displays this gray box. If that monitor is later unplugged, DesktopOrg safely hides the box (or switches to your saved monitor-setup layout) while keeping its items accessible in the menu.");
            var monitorCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(6, 4, 6, 4) };
            foreach (var m in allMonitors)
            {
                var cbi = new ComboBoxItem
                {
                    Content = m.FriendlyName,
                    Tag = m
                };
                monitorCombo.Items.Add(cbi);
                if (!string.IsNullOrWhiteSpace(area.MonitorDeviceName) &&
                    string.Equals(area.MonitorDeviceName, m.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    monitorCombo.SelectedItem = cbi;
                }
                else if (string.IsNullOrWhiteSpace(area.MonitorDeviceName) &&
                         resolvedMon != null &&
                         string.Equals(resolvedMon.DeviceName, m.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    monitorCombo.SelectedItem = cbi;
                }
            }
            if (monitorCombo.SelectedItem == null && monitorCombo.Items.Count > 0)
                monitorCombo.SelectedIndex = 0;
            monitorCombo.SelectionChanged += (_, _) =>
            {
                if (monitorCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is MonitorInfo selectedMon)
                {
                    area.MonitorDeviceName = selectedMon.DeviceName;
                    area.MonitorFriendlyName = selectedMon.FriendlyName;
                    var (mCols, mRows) = _service.GetMonitorGridDimensions(selectedMon);
                    area.GridCol = Math.Clamp(area.GridCol, 0, Math.Max(0, mCols - area.GridWidthCols));
                    area.GridRow = Math.Clamp(area.GridRow, 0, Math.Max(0, mRows - area.GridHeightRows));
                    _service.SaveState();
                    _service.ApplyAreaBackgrounds();
                    RebuildCustomAreasAccordionList();
                    DrawMainScreenGrid();
                }
            };
            Grid.SetRow(monitorCombo, 1);
            Grid.SetColumn(monitorCombo, 1);
            formGrid.Children.Add(monitorCombo);

            // Row 2: Virtual Desktop Assignment
            AddFormLabelWithHelp(formGrid, 2, "Virtual Desktop:",
                "Choose whether this gray box appears on All Virtual Desktops or only on a specific Windows 10/11 Virtual Desktop (Win+Tab).");
            var vdCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(6, 4, 6, 4) };
            var allVdItem = new ComboBoxItem { Content = "All Virtual Desktops (*)", Tag = "*" };
            vdCombo.Items.Add(allVdItem);
            if (string.IsNullOrWhiteSpace(area.VirtualDesktopId) || area.VirtualDesktopId == "*")
                vdCombo.SelectedItem = allVdItem;

            foreach (var vd in virtualDesktops)
            {
                var vdItem = new ComboBoxItem
                {
                    Content = vd.IsCurrent ? $"{vd.Name} (Current)" : vd.Name,
                    Tag = vd.Id
                };
                vdCombo.Items.Add(vdItem);
                if (string.Equals(area.VirtualDesktopId, vd.Id, StringComparison.OrdinalIgnoreCase))
                    vdCombo.SelectedItem = vdItem;
            }
            if (vdCombo.SelectedItem == null) vdCombo.SelectedIndex = 0;
            vdCombo.SelectionChanged += (_, _) =>
            {
                if (vdCombo.SelectedItem is ComboBoxItem vdItem && vdItem.Tag is string vdId)
                {
                    area.VirtualDesktopId = vdId;
                    var matchedVd = virtualDesktops.FirstOrDefault(v => string.Equals(v.Id, vdId, StringComparison.OrdinalIgnoreCase));
                    area.VirtualDesktopName = matchedVd != null ? matchedVd.Name : "All Virtual Desktops";
                    _service.SaveState();
                    _service.ApplyAreaBackgrounds();
                    RebuildCustomAreasAccordionList();
                }
            };
            Grid.SetRow(vdCombo, 2);
            Grid.SetColumn(vdCombo, 1);
            formGrid.Children.Add(vdCombo);

            // Row 3: Which Icons Go Here?
            AddFormLabelWithHelp(formGrid, 3, "Which Icons Go Here?",
                "• Manual (Drop Icons Here): Any icon you drag onto this gray box on your desktop stays in this box.\n• Category Rules (Folders & Files, Web Links (.url), Folders, Shortcuts, Documents, Media, Code & Archives): Automatically pulls matching desktop icons into this gray box when you click 'Apply & Organize Desktop'.");
            var ruleCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(6, 4, 6, 4) };
            foreach (string r in new[] { "Manual (Drop Icons Here)", "Folders & Files", "Web Links (.url)", "Folders", "Shortcuts", "Documents", "Media", "Code & Archives" })
            {
                ruleCombo.Items.Add(r);
            }
            ruleCombo.SelectedItem = area.AutoRule;
            if (ruleCombo.SelectedItem == null) ruleCombo.SelectedIndex = 0;
            ruleCombo.SelectionChanged += (_, _) =>
            {
                if (ruleCombo.SelectedItem is string s)
                {
                    area.AutoRule = s;
                    subtitleBadge.Text = $"{monShortLabel}{vdBadge} • {area.AutoRule} • {area.GridWidthCols}×{area.GridHeightRows}";
                    _service.SaveState();
                }
            };
            Grid.SetRow(ruleCombo, 3);
            Grid.SetColumn(ruleCombo, 1);
            formGrid.Children.Add(ruleCombo);

            // Row 4: Sort Order & Fill Direction Inside Box
            AddFormLabelWithHelp(formGrid, 4, "Sort & Fill Direction:",
                "• Sort Order: Keep Grid Position, Name (A-Z), Domain (A-Z), Domain (Most Links First), or Date Modified.\n• Fill Direction: 'Left to Right' (default) fills icons across each row inside the box; 'Top to Bottom' fills down each column inside the box.");
            var sortRowGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            sortRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sortRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) });

            var sortCombo = new ComboBox { Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(6, 4, 6, 4) };
            foreach (string s in new[] { "Keep Grid Position", "Name (A-Z)", "Domain (A-Z)", "Domain (Most Links First)", "Date Modified" })
            {
                sortCombo.Items.Add(s);
            }
            sortCombo.SelectedItem = area.SortOrder;
            if (sortCombo.SelectedItem == null) sortCombo.SelectedIndex = 0;
            sortCombo.SelectionChanged += (_, _) =>
            {
                if (sortCombo.SelectedItem is string s)
                {
                    area.SortOrder = s;
                    _service.SaveState();
                }
            };
            Grid.SetColumn(sortCombo, 0);
            sortRowGrid.Children.Add(sortCombo);

            var fillDirCombo = new ComboBox { Padding = new Thickness(6, 4, 6, 4), ToolTip = "Fill direction inside this Gray Box ('Left to Right' default or 'Top to Bottom')" };
            foreach (string fd in new[] { "Left to Right", "Top to Bottom" })
            {
                fillDirCombo.Items.Add(fd);
            }
            fillDirCombo.SelectedItem = string.IsNullOrWhiteSpace(area.FillDirection) ? "Left to Right" : area.FillDirection;
            if (fillDirCombo.SelectedItem == null) fillDirCombo.SelectedIndex = 0;
            fillDirCombo.SelectionChanged += (_, _) =>
            {
                if (fillDirCombo.SelectedItem is string fd)
                {
                    area.FillDirection = fd;
                    _service.SaveState();
                }
            };
            Grid.SetColumn(fillDirCombo, 1);
            sortRowGrid.Children.Add(fillDirCombo);

            Grid.SetRow(sortRowGrid, 4);
            Grid.SetColumn(sortRowGrid, 1);
            formGrid.Children.Add(sortRowGrid);

            // Row 5: Gray Shade Preset
            AddFormLabelWithHelp(formGrid, 5, "Gray Shade & Color:",
                "Choose the background shade for this box on your desktop wallpaper.");
            var colorCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(6, 4, 6, 4) };
            var presets = new (string Label, string Hex)[]
            {
                ("Classic ReDesk Gray (#8C929C)", "#8C929C"),
                ("Smoked Charcoal (#282C34)", "#282C34"),
                ("Slate Gray (#475569)", "#475569"),
                ("Deep Graphite (#1E2028)", "#1E2028"),
                ("Cool Silver (#A1A8B3)", "#A1A8B3")
            };
            foreach (var p in presets)
            {
                var cbi = new ComboBoxItem { Content = p.Label, Tag = p.Hex };
                colorCombo.Items.Add(cbi);
                if (string.Equals(area.GrayHex, p.Hex, StringComparison.OrdinalIgnoreCase))
                    colorCombo.SelectedItem = cbi;
            }
            if (colorCombo.SelectedItem == null) colorCombo.SelectedIndex = 0;
            colorCombo.SelectionChanged += (_, _) =>
            {
                if (colorCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string hex)
                {
                    area.GrayHex = hex;
                    DrawMainScreenGrid();
                    _service.SaveState();
                    _service.ApplyAreaBackgrounds();
                }
            };
            Grid.SetRow(colorCombo, 5);
            Grid.SetColumn(colorCombo, 1);
            formGrid.Children.Add(colorCombo);

            // Row 6: Opacity & Label Alignment
            AddFormLabelWithHelp(formGrid, 6, "Transparency & Title:",
                "Adjust how see-through the gray box is on your wallpaper and where its title label is shown.");
            var bottomRowGrid = new Grid();
            bottomRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottomRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

            var opacitySlider = new Slider
            {
                Minimum = 0.15,
                Maximum = 0.90,
                Value = area.Opacity,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                ToolTip = "Drag to adjust box opacity"
            };
            opacitySlider.ValueChanged += (_, _) =>
            {
                area.Opacity = Math.Round(opacitySlider.Value, 2);
                _service.SaveState();
                _service.ApplyAreaBackgrounds();
            };
            Grid.SetColumn(opacitySlider, 0);
            bottomRowGrid.Children.Add(opacitySlider);

            var alignCombo = new ComboBox { Padding = new Thickness(6, 4, 6, 4) };
            foreach (string al in new[] { "Center", "Top-Left", "Hidden" })
            {
                alignCombo.Items.Add(al);
            }
            alignCombo.SelectedItem = area.LabelAlignment;
            if (alignCombo.SelectedItem == null) alignCombo.SelectedIndex = 0;
            alignCombo.SelectionChanged += (_, _) =>
            {
                if (alignCombo.SelectedItem is string al)
                {
                    area.LabelAlignment = al;
                    _service.SaveState();
                    _service.ApplyAreaBackgrounds();
                }
            };
            Grid.SetColumn(alignCombo, 1);
            bottomRowGrid.Children.Add(alignCombo);

            Grid.SetRow(bottomRowGrid, 6);
            Grid.SetColumn(bottomRowGrid, 1);
            formGrid.Children.Add(bottomRowGrid);

            detailsBorder.Child = formGrid;

            headerGrid.MouseLeftButtonDown += (_, _) =>
            {
                detailsBorder.Visibility = detailsBorder.Visibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            };

            mainStack.Children.Add(headerGrid);
            mainStack.Children.Add(detailsBorder);
            cardBorder.Child = mainStack;

            CustomAreasContainer.Children.Add(cardBorder);
        }
    }

    #endregion

    #region 3. Pinned & Locked Icons List

    private void RebuildSpecialShortcutsList()
    {
        SpecialShortcutsContainer.Children.Clear();
        var desktopIconNames = _cachedDesktopIcons
            .Select(i => i.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var spec in _service.State.SystemArea.SpecialShortcuts)
        {
            var cardBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var stack = new StackPanel();
            var headerGrid = new Grid
            {
                Height = 40,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

            var delBtn = new Button
            {
                Content = "✕",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Cursor = Cursors.Hand,
                ToolTip = "Remove this pinned icon rule"
            };
            delBtn.Click += (_, ev) =>
            {
                ev.Handled = true;
                _service.State.SystemArea.SpecialShortcuts.Remove(spec);
                _service.SaveState();
                RebuildSpecialShortcutsList();
            };
            Grid.SetColumn(delBtn, 0);
            headerGrid.Children.Add(delBtn);

            var titleText = new TextBlock
            {
                Text = spec.IconName,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleText, 1);
            headerGrid.Children.Add(titleText);

            var modeBadge = new TextBlock
            {
                Text = spec.PlacementMode,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(modeBadge, 2);
            headerGrid.Children.Add(modeBadge);

            var chev = new TextBlock
            {
                Text = "⌄",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -4, 0, 0)
            };
            Grid.SetColumn(chev, 3);
            headerGrid.Children.Add(chev);

            var detailsBorder = new Border
            {
                Visibility = Visibility.Collapsed,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(14, 10, 14, 12),
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFB))
            };

            var form = new Grid();
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(175) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddFormLabelWithHelp(form, 0, "Desktop Icon Name:",
                "Pick any icon currently on your desktop (or type its name).");
            var iconCombo = new ComboBox
            {
                IsEditable = true,
                Text = spec.IconName,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(6, 4, 6, 4)
            };
            foreach (var dn in desktopIconNames)
                iconCombo.Items.Add(dn);

            iconCombo.AddHandler(TextBoxBase.TextChangedEvent, new RoutedEventHandler((_, _) =>
            {
                spec.IconName = iconCombo.Text;
                titleText.Text = spec.IconName;
                _service.SaveState();
            }));
            Grid.SetRow(iconCombo, 0);
            Grid.SetColumn(iconCombo, 1);
            form.Children.Add(iconCombo);

            AddFormLabelWithHelp(form, 1, "Where to Pin / Lock:",
                "• Pin First in System Area: Places this icon right at the start of your shortcuts.\n• Pin to Bottom-Left: Anchors this icon at the bottom-left corner of your screen.\n• Ignore (Do Not Move): Locks this icon where it currently sits so auto-organize never moves it.");
            var modeCombo = new ComboBox { Padding = new Thickness(6, 4, 6, 4) };
            foreach (var m in new[] { "Pin First in System Area", "Pin to Bottom-Left", "Ignore (Do Not Move)" })
                modeCombo.Items.Add(m);
            modeCombo.SelectedItem = spec.PlacementMode;
            if (modeCombo.SelectedItem == null) modeCombo.SelectedIndex = 0;
            modeCombo.SelectionChanged += (_, _) =>
            {
                if (modeCombo.SelectedItem is string s)
                {
                    spec.PlacementMode = s;
                    modeBadge.Text = s;
                    _service.SaveState();
                }
            };
            Grid.SetRow(modeCombo, 1);
            Grid.SetColumn(modeCombo, 1);
            form.Children.Add(modeCombo);

            detailsBorder.Child = form;
            headerGrid.MouseLeftButtonDown += (_, _) =>
            {
                detailsBorder.Visibility = detailsBorder.Visibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            };

            stack.Children.Add(headerGrid);
            stack.Children.Add(detailsBorder);
            cardBorder.Child = stack;

            SpecialShortcutsContainer.Children.Add(cardBorder);
        }
    }

    #endregion

    #region 4. Saved Layouts List

    private void RebuildSavedLayoutsList()
    {
        SavedLayoutsContainer.Children.Clear();
        string currentSig = DesktopInterop.GetMonitorSignature();

        foreach (var layout in _service.State.Layouts)
        {
            bool matchesCurrentMonitors = string.Equals(layout.MonitorSignature, currentSig, StringComparison.OrdinalIgnoreCase);

            var rowBorder = new Border
            {
                Background = matchesCurrentMonitors
                    ? new SolidColorBrush(Color.FromRgb(0xF0, 0xFD, 0xF4))
                    : new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB)),
                BorderBrush = matchesCurrentMonitors
                    ? new SolidColorBrush(Color.FromRgb(0xBB, 0xF7, 0xD0))
                    : new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var infoStack = new StackPanel();
            string matchBadge = matchesCurrentMonitors ? "  [✓ Current Monitor Setup]" : string.Empty;
            infoStack.Children.Add(new TextBlock
            {
                Text = layout.Name + matchBadge,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27))
            });
            infoStack.Children.Add(new TextBlock
            {
                Text = $"{layout.Icons.Count} icons • {layout.SavedAreas.Count} gray boxes • {layout.MonitorSummary} • {layout.SavedAt:MMM dd, HH:mm}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))
            });
            Grid.SetColumn(infoStack, 0);
            g.Children.Add(infoStack);

            var autoMatchCheck = new CheckBox
            {
                Content = "Auto-Restore on Setup",
                IsChecked = layout.AutoRestoreOnMonitorMatch,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
                ToolTip = "When checked, automatically restores this saved layout (both icons and gray boxes) whenever this exact monitor configuration is connected."
            };
            autoMatchCheck.Checked += (_, _) =>
            {
                layout.AutoRestoreOnMonitorMatch = true;
                _service.SaveState();
            };
            autoMatchCheck.Unchecked += (_, _) =>
            {
                layout.AutoRestoreOnMonitorMatch = false;
                _service.SaveState();
            };
            Grid.SetColumn(autoMatchCheck, 1);
            g.Children.Add(autoMatchCheck);

            var restoreBtn = new Button
            {
                Content = "Restore",
                Style = (Style)FindResource("OutlineButton"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(6, 0, 6, 0)
            };
            restoreBtn.Click += (_, _) =>
            {
                int count = _service.RestoreLayout(layout, restoreAreasToo: true);
                RefreshDesktopIconsCache();
                PopulateAllUi();
                StatusText.Text = $"Restored backup '{layout.Name}' ({count} icons positioned).";
            };
            Grid.SetColumn(restoreBtn, 2);
            g.Children.Add(restoreBtn);

            var delBtn = new Button
            {
                Content = "✕",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                Cursor = Cursors.Hand,
                Padding = new Thickness(6, 2, 6, 2)
            };
            delBtn.Click += (_, _) =>
            {
                _service.State.Layouts.Remove(layout);
                _service.SaveState();
                RebuildSavedLayoutsList();
            };
            Grid.SetColumn(delBtn, 3);
            g.Children.Add(delBtn);

            rowBorder.Child = g;
            SavedLayoutsContainer.Children.Add(rowBorder);
        }
    }

    #endregion

    #region Button & Control Event Handlers

    private void SaveChangeBtn_Click(object sender, RoutedEventArgs e)
    {
        int moved = _service.ApplyAndSaveChange();
        RefreshDesktopIconsCache();
        RebuildCustomAreasAccordionList();
        RebuildSavedLayoutsList();
        DrawMainScreenGrid();

        SaveChangeBtnText.Text = $"✓ Applied ({moved} Icons)";
        StatusText.Text = $"Organized {moved} desktop icons across your Monitors & Gray Boxes.";
    }

    private void UndoBtn_Click(object sender, RoutedEventArgs e)
    {
        int restored = _service.RestoreUndoSnapshot();
        if (restored > 0)
        {
            RefreshDesktopIconsCache();
            PopulateAllUi();
            StatusText.Text = $"Undid last change ({restored} icons restored).";
        }
        else
        {
            StatusText.Text = "No undo snapshot available yet.";
        }
    }

    private void ToggleDesktopDragBtn_Click(object sender, RoutedEventArgs e)
    {
        _service.State.AreZonesLocked = !_service.State.AreZonesLocked;
        _service.SaveState();
        _service.ApplyAreaBackgrounds();
        UpdateLockButtonUi();

        StatusText.Text = _service.State.AreZonesLocked
            ? "Desktop gray boxes locked (clicks pass straight through to desktop icons)."
            : "Desktop gray boxes UNLOCKED — drag or resize them directly on any monitor wallpaper!";
    }

    private void RefreshPreviewBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshDesktopIconsCache();
        var activeMon = _service.GetTargetMonitor();
        GridInfoText.Text = $"{activeMon.FriendlyName} • {_service.State.GridCols}×{_service.State.GridRows} Grid ({_cachedDesktopIcons.Count} Icons)";
        DrawMainScreenGrid();
        StatusText.Text = $"Refreshed {_cachedDesktopIcons.Count} icons from Windows Desktop.";
    }

    private void TargetMonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingUi) return;
        if (TargetMonitorCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string devName)
        {
            _service.State.TargetMonitorDeviceName = devName;
            _service.CalibrateDesktopGrid();
            _service.SaveState();
            _service.ApplyAreaBackgrounds();
            RefreshDesktopIconsCache();
            var activeMon = _service.GetTargetMonitor();
            var onThisMon = _cachedDesktopIcons.Count(i =>
                string.Equals(_service.FindMonitorForListViewPoint(i.X, i.Y).DeviceName, activeMon.DeviceName, StringComparison.OrdinalIgnoreCase));
            GridInfoText.Text = $"{activeMon.FriendlyName} • {_service.State.GridCols}×{_service.State.GridRows} Grid ({onThisMon} Icons on Screen)";
            RebuildCustomAreasAccordionList();
            DrawMainScreenGrid();
            StatusText.Text = $"Switched Target Screen to '{cbi.Content}'. You can now add or drag Gray Boxes directly on this monitor!";
        }
    }

    private void GatherAllMonitorsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isPopulatingUi) return;
        _service.State.GatherAllMonitorsIconsToTarget = GatherAllMonitorsCheck.IsChecked == true;
        _service.SaveState();
    }

    private void AddCustomAreaBtn_Click(object sender, RoutedEventArgs e)
    {
        var activeTarget = _service.GetTargetMonitor();
        var (mCols, mRows) = _service.GetMonitorGridDimensions(activeTarget);
        int areasOnThisMonitor = _service.State.CustomAreas.Count(a =>
        {
            var m = _service.ResolveMonitorForArea(a);
            return m != null && string.Equals(m.DeviceName, activeTarget.DeviceName, StringComparison.OrdinalIgnoreCase);
        });

        int nextNum = _service.State.CustomAreas.Count + 1;
        string requestedName = NewAreaNameBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(requestedName))
            requestedName = $"Box {nextNum}";

        string rule = GetSelectedComboContent(NewAreaRuleCombo, "Manual (Drop Icons Here)");

        int wCols = Math.Clamp(mCols / 3, 4, 7);
        int startCol = Math.Max(0, mCols - wCols);
        int startRow = Math.Min(Math.Max(0, mRows - 2), (areasOnThisMonitor * 2) % Math.Max(2, mRows));

        var newArea = new CustomAreaConfig
        {
            Title = requestedName,
            MonitorDeviceName = activeTarget.DeviceName,
            MonitorFriendlyName = activeTarget.FriendlyName,
            VirtualDesktopId = "*",
            VirtualDesktopName = "All Virtual Desktops",
            GridCol = startCol,
            GridRow = startRow,
            GridWidthCols = wCols,
            GridHeightRows = 2,
            GrayHex = "#8C929C",
            BorderHex = "#C0C6D0",
            Opacity = 0.52,
            AutoRule = rule
        };

        _service.State.CustomAreas.Add(newArea);
        _service.SaveState();
        _service.ApplyAreaBackgrounds();
        RebuildCustomAreasAccordionList();
        DrawMainScreenGrid();

        if (NewAreaNameBox != null)
            NewAreaNameBox.Text = $"Zone {_service.State.CustomAreas.Count + 1}";

        StatusText.Text = $"Created gray box '{newArea.Title}' on {activeTarget.FriendlyName}. Drag it on the Desktop Layout Preview above to position it.";
    }

    private void ClearAllAreasBtn_Click(object sender, RoutedEventArgs e)
    {
        _service.ClearAllCustomAreas();
        _service.ApplyAreaBackgrounds();
        RebuildCustomAreasAccordionList();
        DrawMainScreenGrid();
        StatusText.Text = "Cleared all gray boxes. Use '+ Add Gray Box' above to create your own custom boxes.";
    }

    private void LoadCategoryBoxesBtn_Click(object sender, RoutedEventArgs e)
    {
        _service.LoadCategoryGrayAreasPreset();
        _service.ApplyAreaBackgrounds();
        RebuildCustomAreasAccordionList();
        DrawMainScreenGrid();
        StatusText.Text = "Loaded 'Folders & Files' and 'Web Links (.url)' gray boxes. Click 'Apply & Organize Desktop' to sort icons into them!";
    }

    private void ResetDefaultAreasBtn_Click(object sender, RoutedEventArgs e)
    {
        _service.ResetToDefaultReDeskAreas(saveImmediately: true);
        _service.ApplyAreaBackgrounds();
        RebuildCustomAreasAccordionList();
        DrawMainScreenGrid();
        StatusText.Text = "Restored default Fixed, Plan, Ongoing, and Finish gray boxes.";
    }

    private void AddSpecialShortcutBtn_Click(object sender, RoutedEventArgs e)
    {
        string defaultName = _cachedDesktopIcons.FirstOrDefault()?.Name ?? "Recycle Bin";
        _service.State.SystemArea.SpecialShortcuts.Add(new SpecialShortcutConfig
        {
            IconName = defaultName,
            PlacementMode = "Pin First in System Area"
        });
        _service.SaveState();
        RebuildSpecialShortcutsList();
    }

    private void SystemAreaOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_isPopulatingUi) return;

        var sys = _service.State.SystemArea;
        sys.SeparateFilesAndShortcuts = SeparateFilesShortcutsCheck.IsChecked == true;
        sys.LeaveEmptyColumnBetweenGroups = LeaveGapBetweenGroupsCheck.IsChecked == true;
        sys.WebLinksGroupMode = GetSelectedComboContent(WebLinksGroupCombo, "Separate Group (Own Columns)");
        sys.WebLinkSortBy = GetSelectedComboContent(WebLinkSortCombo, "Domain (A-Z)");
        sys.ShortcutSortBy = GetSelectedComboContent(ShortcutSortCombo, "Name (A-Z)");
        sys.ShortcutDirection = GetSelectedComboContent(ShortcutDirectionCombo, "Left to Right");
        sys.SystemIconsFirst = SystemIconsFirstCheck.IsChecked == true;

        sys.FileSortBy = GetSelectedComboContent(FileSortCombo, "Category & Name");
        sys.FileDirection = GetSelectedComboContent(FileDirectionCombo, "Top to Bottom");
        sys.FoldersBeforeFiles = FoldersBeforeFilesCheck.IsChecked == true;

        _service.SaveState();
    }

    private void AreaBgModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingUi) return;
        if (AreaBgModeCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string mode)
        {
            _service.State.AreaBackgroundMode = mode;
            _service.SaveState();
            _service.ApplyAreaBackgrounds();
            StatusText.Text = $"Updated Gray Box Display Mode to '{cbi.Content}'.";
        }
    }

    private void UseDesktopMenuCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingUi) return;
        if (UseDesktopMenuCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string val)
        {
            bool enable = string.Equals(val, "Yes", StringComparison.OrdinalIgnoreCase);
            _service.State.UseDesktopContextMenu = enable;
            DesktopNativeHook.SetDesktopContextMenuEnabled(enable, _service);
            _service.SaveState();
            StatusText.Text = enable
                ? "Added DesktopOrg hierarchical monitor menu to Windows Desktop right-click menu."
                : "Removed DesktopOrg from Windows Desktop right-click menu.";
        }
    }

    private void DisconnectedMonitorModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingUi) return;
        if (DisconnectedMonitorModeCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string mode)
        {
            _service.State.DisconnectedMonitorMode = mode;
            _service.SaveState();
            StatusText.Text = mode == "RememberSetupOrHide"
                ? "Monitor Setup Mode: Remembers & auto-restores saved layouts per monitor configuration, with fallback to hiding offline monitor boxes."
                : "Monitor Setup Mode: Keeps current layout and hides gray boxes on unplugged monitors.";
        }
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingUi) return;
        if (ProfileCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string profId)
        {
            var prof = _service.State.Profiles.FirstOrDefault(p => p.Id == profId);
            if (prof != null)
            {
                _service.SwitchProfile(prof);
                RefreshDesktopIconsCache();
                PopulateAllUi();
                StatusText.Text = $"Switched active workspace profile to '{prof.Name}'.";
            }
        }
    }

    private void GeneralCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isPopulatingUi) return;
        _service.State.AutoArrangeOnDesktopChange = LiveDesktopHookCheck.IsChecked == true;
        _service.State.AutoRestoreOnDisplayChange = AutoRestoreMonitorsCheck.IsChecked == true;
        _service.SaveState();
    }

    private void RunAtStartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isPopulatingUi) return;

        bool enable = sender is CheckBox cb
            ? cb.IsChecked == true
            : (TopRunAtStartupCheck?.IsChecked == true || RunAtStartupCheck?.IsChecked == true);

        _isPopulatingUi = true;
        try
        {
            if (TopRunAtStartupCheck != null) TopRunAtStartupCheck.IsChecked = enable;
            if (RunAtStartupCheck != null) RunAtStartupCheck.IsChecked = enable;
        }
        finally
        {
            _isPopulatingUi = false;
        }

        _service.SetRunAtStartup(enable);
        StatusText.Text = enable
            ? "Enabled: DesktopOrg will start automatically in the System Tray when your computer starts."
            : "Disabled automatic startup with Windows.";
    }

    private void InstallAppBtn_Click(object sender, RoutedEventArgs e)
    {
        bool ok = _service.InstallToLocalPrograms(out string msg);
        UpdateInstallButtonUi();
        bool startupEnabled = _service.IsRunAtStartupEnabled();
        _isPopulatingUi = true;
        try
        {
            if (TopRunAtStartupCheck != null) TopRunAtStartupCheck.IsChecked = startupEnabled;
            if (RunAtStartupCheck != null) RunAtStartupCheck.IsChecked = startupEnabled;
        }
        finally
        {
            _isPopulatingUi = false;
        }
        StatusText.Text = msg;
    }

    private void SaveSnapshotBtn_Click(object sender, RoutedEventArgs e)
    {
        string name = NewLayoutNameBox.Text;
        var layout = _service.SaveCurrentLayout(name, autoRestoreOnMonitor: true);
        RebuildSavedLayoutsList();
        StatusText.Text = $"Saved desktop backup '{layout.Name}' ({layout.Icons.Count} icons).";
    }

    private void HideToTrayBtn_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    #endregion

    #region Helpers

    private static void SelectComboByContent(ComboBox combo, string value)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string GetSelectedComboContent(ComboBox? combo, string fallback)
    {
        if (combo?.SelectedItem is ComboBoxItem cbi && cbi.Content != null)
            return cbi.Content.ToString() ?? fallback;
        return fallback;
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color c)
                return c;
        }
        catch
        {
            // Ignore
        }
        return fallback;
    }

    #endregion
}
