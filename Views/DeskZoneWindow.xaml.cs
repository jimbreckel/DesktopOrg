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
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);
        }

        ApplyAreaToUi();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCCALCSIZE)
        {
            // Strip 100% of Win32 non-client frame/border when AllowsTransparency="False"
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WM_NCHITTEST && _isLocked)
        {
            handled = true;
            return new IntPtr(HTTRANSPARENT);
        }
        return IntPtr.Zero;
    }

    public void UpdateFromConfig(CustomAreaConfig area, bool isLocked)
    {
        Area = area;
        _isLocked = isLocked;
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

        Color grayCol = ParseColor(Area.GrayHex, Color.FromRgb(0x3B, 0x42, 0x52));
        byte alpha = (byte)Math.Clamp((int)(Area.Opacity * 255), 55, 240);

        // Because the window itself uses Win32 SetLayeredWindowAttributes(alpha) for true translucency
        // behind SHELLDLL_DefView (desktop icons), keep the WPF backbuffer solid grayCol so it blends cleanly
        var solidGrayBrush = new SolidColorBrush(Color.FromRgb(grayCol.R, grayCol.G, grayCol.B));
        Background = solidGrayBrush;
        AreaBorder.Background = solidGrayBrush;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            DesktopNativeHook.PositionAndAttachZoneWindow(
                hwnd,
                pxX,
                pxY,
                finalPxW,
                finalPxH,
                alpha,
                (int)Math.Round(Area.CornerRadius),
                _isLocked);
        }

        var (scaleX, scaleY) = GetDpiScale();
        int cornerRadiusPx = (int)Math.Round(Area.CornerRadius * scaleX);

        Width = finalPxW / scaleX;
        Height = finalPxH / scaleY;
        if (!_isLocked)
        {
            Left = pxX / scaleX;
            Top = pxY / scaleY;
        }

        if (hwnd != IntPtr.Zero)
        {
            DesktopNativeHook.PositionAndAttachZoneWindow(
                hwnd,
                pxX,
                pxY,
                finalPxW,
                finalPxH,
                alpha,
                cornerRadiusPx,
                _isLocked);
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
            else
            {
                AreaTitleText.HorizontalAlignment = HorizontalAlignment.Center;
                AreaTitleText.VerticalAlignment = VerticalAlignment.Center;
            }
        }

        AreaBorder.CornerRadius = new CornerRadius(Area.CornerRadius);

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
            AreaBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(borderCol.R, borderCol.G, borderCol.B));
            AreaBorder.BorderThickness = new Thickness(1.5);
            AreaBorder.Cursor = Cursors.Arrow;
            EditBadge.Visibility = Visibility.Collapsed;
            ResizeGrip.Visibility = Visibility.Collapsed;
        }
    }

    public void EnsureVisibleOnDesktop()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var (pxX, pxY, pxW, pxH) = _service.GetAreaScreenPixelBounds(Area);
        int finalPxW = Math.Max(80, pxW);
        int finalPxH = Math.Max(60, pxH);
        byte alpha = (byte)Math.Clamp((int)(Area.Opacity * 255), 55, 240);
        var (scaleX, _) = GetDpiScale();
        int cornerRadiusPx = (int)Math.Round(Area.CornerRadius * scaleX);

        DesktopNativeHook.PositionAndAttachZoneWindow(
            hwnd,
            pxX,
            pxY,
            finalPxW,
            finalPxH,
            alpha,
            cornerRadiusPx,
            _isLocked);
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
