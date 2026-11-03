using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MyReDesk.Models;
using MyReDesk.Services;

namespace MyReDesk.Views;

public partial class DeskZoneWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int GWLP_HWNDPARENT = -8;

    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private readonly DeskManagerService _service;
    public CustomAreaConfig Area { get; private set; }
    private bool _isLocked = true;

    public DeskZoneWindow(CustomAreaConfig area, DeskManagerService service)
    {
        InitializeComponent();
        Area = area;
        _service = service;
        _isLocked = service.State.AreZonesLocked;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            ApplyAreaToUi();
            UpdateWindowExtendedStyles();
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            IntPtr progman = DesktopInterop.GetProgmanHandle();
            if (progman != IntPtr.Zero)
            {
                SetWindowLongPtr64(hwnd, GWLP_HWNDPARENT, progman);
            }
        }

        UpdateWindowExtendedStyles();
        ApplyAreaToUi();
    }

    public void UpdateFromConfig(CustomAreaConfig area, bool isLocked)
    {
        Area = area;
        _isLocked = isLocked;
        UpdateWindowExtendedStyles();
        ApplyAreaToUi();
    }

    private (double ScaleX, double ScaleY) GetDpiScale()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi >= 96 && dpi <= 480)
                {
                    double s = dpi / 96.0;
                    return (s, s);
                }
            }

            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                return (source.CompositionTarget.TransformToDevice.M11,
                        source.CompositionTarget.TransformToDevice.M22);
            }
        }
        catch
        {
            // Ignore
        }
        return (1.25, 1.25);
    }

    private void ApplyAreaToUi()
    {
        var (pxX, pxY, pxW, pxH) = _service.GetAreaScreenPixelBounds(Area);
        int finalPxW = Math.Max(80, pxW);
        int finalPxH = Math.Max(60, pxH);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                pxX,
                pxY,
                finalPxW,
                finalPxH,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        var (scaleX, scaleY) = GetDpiScale();

        Left = pxX / scaleX;
        Top = pxY / scaleY;
        Width = finalPxW / scaleX;
        Height = finalPxH / scaleY;

        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                pxX,
                pxY,
                finalPxW,
                finalPxH,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        AreaTitleText.Text = Area.Title;
        if (string.Equals(Area.LabelAlignment, "Hidden", StringComparison.OrdinalIgnoreCase))
        {
            AreaTitleText.Visibility = Visibility.Collapsed;
        }
        else
        {
            AreaTitleText.Visibility = Visibility.Visible;
            if (string.Equals(Area.LabelAlignment, "Top-Left", StringComparison.OrdinalIgnoreCase))
            {
                AreaTitleText.HorizontalAlignment = HorizontalAlignment.Left;
                AreaTitleText.VerticalAlignment = VerticalAlignment.Top;
            }
            else if (string.Equals(Area.LabelAlignment, "Top-Right", StringComparison.OrdinalIgnoreCase))
            {
                AreaTitleText.HorizontalAlignment = HorizontalAlignment.Right;
                AreaTitleText.VerticalAlignment = VerticalAlignment.Top;
            }
            else if (string.Equals(Area.LabelAlignment, "Bottom-Left", StringComparison.OrdinalIgnoreCase))
            {
                AreaTitleText.HorizontalAlignment = HorizontalAlignment.Left;
                AreaTitleText.VerticalAlignment = VerticalAlignment.Bottom;
            }
            else if (string.Equals(Area.LabelAlignment, "Center", StringComparison.OrdinalIgnoreCase))
            {
                AreaTitleText.HorizontalAlignment = HorizontalAlignment.Center;
                AreaTitleText.VerticalAlignment = VerticalAlignment.Center;
            }
            else
            {
                // Default: Bottom-Right
                AreaTitleText.HorizontalAlignment = HorizontalAlignment.Right;
                AreaTitleText.VerticalAlignment = VerticalAlignment.Bottom;
            }
        }

        AreaBorder.CornerRadius = new CornerRadius(Area.CornerRadius);

        byte alpha = (byte)Math.Clamp((int)(Area.Opacity * 255), 55, 240);
        if (string.Equals(Area.GrayHex, "Transparent", StringComparison.OrdinalIgnoreCase))
        {
            AreaBorder.Background = _isLocked
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        }
        else
        {
            Color grayCol = ParseColor(Area.GrayHex, Color.FromRgb(0x3B, 0x42, 0x52));
            AreaBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, grayCol.R, grayCol.G, grayCol.B));
        }

        Color borderCol = ParseColor(Area.BorderHex, Color.FromRgb(0xE2, 0xE8, 0xF0));

        if (!_isLocked)
        {
            AreaBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
            AreaBorder.BorderThickness = new Thickness(2.5);
            AreaBorder.Cursor = Cursors.SizeAll;
            EditBadge.Visibility = Visibility.Visible;
            ResizeGrip.Visibility = Visibility.Visible;
            GridCoordsText.Text = $"Col {Area.GridCol}, Row {Area.GridRow} ({Area.GridWidthCols}×{Area.GridHeightRows})";
        }
        else
        {
            AreaBorder.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(alpha + 80, 175, 255), borderCol.R, borderCol.G, borderCol.B));
            AreaBorder.BorderThickness = new Thickness(2.0);
            AreaBorder.Cursor = Cursors.Arrow;
            EditBadge.Visibility = Visibility.Collapsed;
            ResizeGrip.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateWindowExtendedStyles()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_LAYERED;

        if (_isLocked)
        {
            exStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        }
        else
        {
            exStyle &= ~WS_EX_TRANSPARENT;
            exStyle &= ~WS_EX_NOACTIVATE;
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        }
    }

    public void EnsureVisibleOnDesktop()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void AreaBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isLocked) return;

        try
        {
            DragMove();
        }
        catch
        {
            // Ignore
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT rect))
        {
            var (newCol, newRow) = _service.ScreenPixelToGridCell(rect.Left, rect.Top, Area);
            _service.MoveCustomAreaOnGrid(Area, newCol, newRow, moveIconsOnDesktopImmediately: true);
            ApplyAreaToUi();
            _service.SaveState();
        }
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_isLocked) return;
        Width = Math.Max(80, Width + e.HorizontalChange);
        Height = Math.Max(60, Height + e.VerticalChange);
    }

    private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_isLocked) return;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT rect))
        {
            int pxW = rect.Right - rect.Left;
            int pxH = rect.Bottom - rect.Top;

            var mon = _service.ResolveMonitorForArea(Area) ?? _service.GetTargetMonitor();
            var (cols, rows) = _service.GetMonitorGridDimensions(mon);

            int wCols = Math.Clamp((int)Math.Round((double)pxW / _service.State.CellWidthPx), 1, Math.Max(1, cols - Area.GridCol));
            int hRows = Math.Clamp((int)Math.Round((double)pxH / _service.State.CellHeightPx), 1, Math.Max(1, rows - Area.GridRow));

            Area.GridWidthCols = wCols;
            Area.GridHeightRows = hRows;

            ApplyAreaToUi();
            _service.SaveState();
        }
    }

    private void LockButton_Click(object sender, RoutedEventArgs e)
    {
        _service.State.AreZonesLocked = true;
        _service.SaveState();
        _service.ApplyAreaBackgrounds();
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
}
