using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AsciiLandsWindMap;

/// <summary>
/// Click-through window over the game minimap. Extended styles let clicks fall through to the page.
/// </summary>
public sealed class MinimapFloat : Window
{
    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;
    private const int WmWindowPosChanging = 0x0046;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly Brush WindBrush = Freeze(Color.FromRgb(0xee, 0xf5, 0xd8));
    private static readonly Brush GpsBrush = Freeze(Color.FromRgb(0x9d, 0xe4, 0x6c));
    private static readonly Brush EntryBrush = Freeze(Color.FromRgb(0x6a, 0x75, 0x60));
    private static readonly Brush ExitBrush = Freeze(Color.FromRgb(0xee, 0xf5, 0xd8));
    private static readonly Pen OutlinePen = FreezePen(Color.FromRgb(0x17, 0x25, 0x1a), 2.4);
    private static readonly Pen HeadPen = FreezePen(Color.FromRgb(0x17, 0x25, 0x1a), 1.2);
    private static readonly Pen CalmOutline = FreezePen(Color.FromRgb(0x17, 0x25, 0x1a), 3.6);
    private static readonly Pen CalmInk = FreezePen(Color.FromRgb(0xee, 0xf5, 0xd8), 1.8);

    private WindArrow _arrow = WindArrow.Calm;
    private int _enter = -1;
    private int _exit = -1;
    private int _x;
    private int _y;
    private int _w;
    private int _h;

    public MinimapFloat()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Width = 240;
        Height = 240;
        SourceInitialized += OnSourceInitialized;
    }

    public void ShowOver(Window owner, double x, double y, double width, double height, WindArrow arrow, int enter, int exit)
    {
        _arrow = arrow;
        _enter = enter;
        _exit = exit;
        _x = (int)Math.Round(x);
        _y = (int)Math.Round(y);
        _w = Math.Max(1, (int)Math.Round(width));
        _h = Math.Max(1, (int)Math.Round(height));
        if (Owner == null)
            Owner = owner;
        if (!IsVisible)
            Show();
        Place();
        InvalidateVisual();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var extra = WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate;
        if (IntPtr.Size == 8)
            SetWindowLong64(hwnd, GwlExStyle, GetWindowLong64(hwnd, GwlExStyle) | extra);
        else
            SetWindowLong32(hwnd, GwlExStyle, GetWindowLong32(hwnd, GwlExStyle) | extra);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(KeepPlace);
        Place();
    }

    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || _w <= 0)
            return;
        SetWindowPos(hwnd, HwndTopmost, _x, _y, _w, _h, SwpNoActivate | SwpShowWindow);
    }

    private IntPtr KeepPlace(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmWindowPosChanging || _w <= 0)
            return IntPtr.Zero;
        var pos = Marshal.PtrToStructure<WindowPos>(lParam);
        if ((pos.flags & SwpHideWindow) != 0)
            return IntPtr.Zero;
        pos.x = _x;
        pos.y = _y;
        pos.cx = _w;
        pos.cy = _h;
        pos.flags &= ~(SwpNoSize | SwpNoMove);
        Marshal.StructureToPtr(pos, lParam, false);
        return IntPtr.Zero;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 1 || height < 1)
            return;
        var scale = Math.Min(width, height) / 240.0;
        dc.PushTransform(new TranslateTransform((width - 240 * scale) / 2, (height - 240 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        DrawWind(dc, _arrow);
        DrawLabel(dc, "GPS", 232, 16, GpsBrush, end: true);
        DrawHand(dc, _enter, EntryBrush, inward: true);
        DrawHand(dc, _exit, ExitBrush, inward: false);
        dc.Pop();
        dc.Pop();
    }

    private static void DrawWind(DrawingContext dc, WindArrow arrow)
    {
        if (!arrow.Shown)
        {
            var ring = new Point(MinimapOverlay.CenterX, MinimapOverlay.CenterY);
            dc.DrawEllipse(null, CalmOutline, ring, 5, 5);
            dc.DrawEllipse(null, CalmInk, ring, 5, 5);
            return;
        }

        var outline = new Pen(Freeze(Color.FromRgb(0x17, 0x25, 0x1a)), 5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        var shaft = new Pen(WindBrush, 2.4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        outline.Freeze();
        shaft.Freeze();
        var tail = new Point(arrow.TailX, arrow.TailY);
        var neck = new Point(arrow.BaseX, arrow.BaseY);
        dc.DrawLine(outline, tail, neck);
        dc.DrawLine(shaft, tail, neck);
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(new Point(arrow.TipX, arrow.TipY), true, true);
            ctx.LineTo(new Point(arrow.LeftX, arrow.LeftY), true, false);
            ctx.LineTo(new Point(arrow.RightX, arrow.RightY), true, false);
        }
        head.Freeze();
        dc.DrawGeometry(WindBrush, HeadPen, head);
    }

    private void DrawLabel(DrawingContext dc, string text, double x, double baseline, Brush fill, bool end)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Trebuchet MS"),
            11,
            fill,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var origin = new Point(end ? x - formatted.Width : x, baseline - formatted.Baseline);
        var geometry = formatted.BuildGeometry(origin);
        dc.DrawGeometry(null, OutlinePen, geometry);
        dc.DrawGeometry(fill, null, geometry);
    }

    private static void DrawHand(DrawingContext dc, int side, Brush color, bool inward)
    {
        if (side is < 0 or > 5)
            return;
        var (dx, dy) = Axial.SideVector(side);
        const double reach = 92;
        var x2 = 120 + dx * reach;
        var y2 = 120 + dy * reach;
        var pen = new Pen(color, 3)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        dc.DrawLine(pen, new Point(120, 120), new Point(x2, y2));
        var dirX = inward ? -dx : dx;
        var dirY = inward ? -dy : dy;
        var along = inward ? 0.55 : 1;
        var tipX = 120 + dx * reach * along;
        var tipY = 120 + dy * reach * along;
        var backX = tipX - dirX * 12;
        var backY = tipY - dirY * 12;
        var px = -dirY;
        var py = dirX;
        const double wing = 5.5;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(tipX, tipY), true, true);
            ctx.LineTo(new Point(backX + px * wing, backY + py * wing), true, false);
            ctx.LineTo(new Point(backX - px * wing, backY - py * wing), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(color, null, geo);
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FreezePen(Color color, double thickness)
    {
        var pen = new Pen(Freeze(color), thickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLong64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLong64(IntPtr hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
