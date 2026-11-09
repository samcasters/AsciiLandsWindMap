using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AsciiLandsWindMap;

public sealed class HexMapControl : FrameworkElement
{
    public const double HexSize = 36;

    private readonly HexLayout _layout = new(HexSize);
    private List<RegionRow> _regions = [];
    private Dictionary<(int Q, int R), RegionRow> _byCoord = new();
    private Dictionary<string, RegionRow> _byId = new(StringComparer.Ordinal);
    private Dictionary<string, WindSample> _arrows = new(StringComparer.Ordinal);
    private Drawing? _terrain;
    private bool _night;
    private bool _opened;
    private double _zoom = 0.85;
    private double _camX;
    private double _camY;
    private bool _panning;
    private bool _dragged;
    private Point _down;
    private Point _last;
    private RegionRow? _hover;
    private SolidColorBrush _mapBackground = Brushes.LightSteelBlue;

    public HexMapControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public string? SelectedId { get; private set; }

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters) =>
        new PointHitTestResult(this, hitTestParameters.HitPoint);

    public event EventHandler<RegionRow>? RegionActivated;
    public event EventHandler<RegionRow?>? HoverChanged;

    public void SetContent(IReadOnlyList<RegionRow> regions, IReadOnlyDictionary<string, WindSample> arrows, bool night)
    {
        _regions = regions.ToList();
        _byCoord = new Dictionary<(int Q, int R), RegionRow>();
        _byId = new Dictionary<string, RegionRow>(StringComparer.Ordinal);
        foreach (var region in _regions)
        {
            _byId[region.Id] = region;
            _byCoord[(region.Q, region.R)] = region;
        }
        _arrows = new Dictionary<string, WindSample>(arrows, StringComparer.Ordinal);
        _night = night;
        _mapBackground = new SolidColorBrush(night ? Color.FromRgb(14, 20, 27) : Color.FromRgb(213, 224, 230));
        _mapBackground.Freeze();
        RebuildTerrain();
        TryOpenWorkView();
        InvalidateVisual();
    }

    public void Select(string? id)
    {
        SelectedId = id;
        InvalidateVisual();
    }

    public void CenterOn(int q, int r)
    {
        var (x, y) = _layout.Center(q, r);
        _camX = x;
        _camY = y;
        if (_zoom < 0.85)
            _zoom = 0.85;
        _opened = true;
        InvalidateVisual();
    }

    public void FitAll()
    {
        if (_regions.Count == 0 || ActualWidth < 1 || ActualHeight < 1)
            return;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var region in _regions)
        {
            var (x, y) = _layout.Center(region.Q, region.R);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
        minX -= HexSize;
        maxX += HexSize;
        minY -= HexSize;
        maxY += HexSize;
        var width = Math.Max(1, maxX - minX);
        var height = Math.Max(1, maxY - minY);
        _zoom = Math.Clamp(Math.Min(ActualWidth / width, ActualHeight / height) * 0.96, 0.01, 8);
        _camX = (minX + maxX) / 2;
        _camY = (minY + maxY) / 2;
        _opened = true;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        TryOpenWorkView();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _down = e.GetPosition(this);
        _last = _down;
        _dragged = false;
        _panning = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_panning)
        {
            if ((point - _down).Length > 4)
                _dragged = true;
            if (_dragged)
            {
                _camX -= (point.X - _last.X) / _zoom;
                _camY -= (point.Y - _last.Y) / _zoom;
                _last = point;
                InvalidateVisual();
            }
            return;
        }
        UpdateHover(point);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_panning)
            return;
        _panning = false;
        ReleaseMouseCapture();
        if (!_dragged)
        {
            var region = Hit(e.GetPosition(this));
            if (region != null)
            {
                SelectedId = region.Id;
                RegionActivated?.Invoke(this, region);
            }
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_panning)
            return;
        if (_hover != null)
        {
            _hover = null;
            HoverChanged?.Invoke(this, null);
            Cursor = Cursors.Arrow;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var mouse = e.GetPosition(this);
        var world = ScreenToWorld(mouse);
        var factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        _zoom = Math.Clamp(_zoom * factor, 0.02, 8);
        _camX = world.X - (mouse.X - ActualWidth / 2) / _zoom;
        _camY = world.Y - (mouse.Y - ActualHeight / 2) / _zoom;
        _opened = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(_mapBackground, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_regions.Count == 0 || ActualWidth < 1 || _zoom <= 0)
            return;

        dc.PushTransform(new MatrixTransform(CameraMatrix()));
        if (_terrain != null)
            dc.DrawDrawing(_terrain);
        DrawArrows(dc);
        DrawRing(dc, _hover, Color.FromArgb(180, 255, 255, 255), 1.4);
        DrawRing(dc, SelectedId == null ? null : _byId.GetValueOrDefault(SelectedId), Color.FromRgb(230, 179, 37), 2.4);
        if (_zoom >= 0.75 && !_panning)
            DrawLabels(dc);
        dc.Pop();

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var north = new FormattedText("N", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.White, dpi);
        dc.DrawText(north, new Point(18, 8));
        dc.DrawLine(new Pen(Brushes.White, 1.5), new Point(24, 26), new Point(24, 12));
    }

    private void TryOpenWorkView()
    {
        if (_opened || _regions.Count == 0 || ActualWidth < 1 || ActualHeight < 1)
            return;
        double sx = 0, sy = 0;
        foreach (var region in _regions)
        {
            var (x, y) = _layout.Center(region.Q, region.R);
            sx += x;
            sy += y;
        }
        _camX = sx / _regions.Count;
        _camY = sy / _regions.Count;
        _zoom = 0.85;
        _opened = true;
        InvalidateVisual();
    }

    private void RebuildTerrain()
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var byBiome = new Dictionary<string, List<RegionRow>>(StringComparer.OrdinalIgnoreCase);
            foreach (var region in _regions)
            {
                if (!byBiome.TryGetValue(region.Biome, out var list))
                    byBiome[region.Biome] = list = [];
                list.Add(region);
            }
            foreach (var (biome, list) in byBiome)
                dc.DrawGeometry(BiomePalette.BrushFor(biome, _night), null, Outline(list));
            var border = new SolidColorBrush(_night ? Color.FromArgb(80, 255, 255, 255) : Color.FromArgb(110, 255, 255, 255));
            border.Freeze();
            dc.DrawGeometry(null, new Pen(border, HexSize * 0.045), Outline(_regions));
        }
        group.Freeze();
        _terrain = group;
    }

    private void DrawArrows(DrawingContext dc)
    {
        var fill = new SolidColorBrush(Color.FromRgb(20, 22, 24));
        fill.Freeze();
        var outline = new Pen(Brushes.White, HexSize * 0.035);
        foreach (var region in _regions)
        {
            var (x, y) = _layout.Center(region.Q, region.R);
            if (region.Tags.Contains("stormy") || region.Tags.Contains("gale_edge"))
                DrawDiamond(dc, new Point(x, y - HexSize * 0.62), HexSize * 0.11, Color.FromRgb(233, 168, 37));
            if (region.Tags.Contains("favorite"))
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(230, 179, 37)), null, new Point(x, y + HexSize * 0.62), HexSize * 0.07, HexSize * 0.07);
            if (!_arrows.TryGetValue(region.Id, out var sample) || sample.Cancelled || sample.Strength <= 0)
                continue;
            DrawArrow(dc, new Point(x, y), sample.DirectionDeg, WindRules.ArrowLength(sample.Strength, HexSize), fill, outline);
        }
    }

    private static void DrawArrow(DrawingContext dc, Point center, double degrees, double length, Brush fill, Pen outline)
    {
        var rad = degrees * Math.PI / 180.0;
        var dx = Math.Sin(rad);
        var dy = -Math.Cos(rad);
        var tip = new Point(center.X + dx * length * 0.58, center.Y + dy * length * 0.58);
        var tail = new Point(center.X - dx * length * 0.42, center.Y - dy * length * 0.42);
        dc.DrawLine(outline, tail, tip);
        dc.DrawLine(new Pen(fill, Math.Max(1.2, length * 0.16)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, tail, tip);
        var head = length * 0.36;
        var baseX = tip.X - dx * head;
        var baseY = tip.Y - dy * head;
        var left = new Point(baseX - dy * head * 0.55, baseY + dx * head * 0.55);
        var right = new Point(baseX + dy * head * 0.55, baseY - dx * head * 0.55);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(tip, true, true);
            ctx.LineTo(left, true, false);
            ctx.LineTo(right, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(fill, outline, geo);
    }

    private static void DrawDiamond(DrawingContext dc, Point center, double radius, Color color)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(center.X, center.Y - radius), true, true);
            ctx.LineTo(new Point(center.X + radius, center.Y), true, false);
            ctx.LineTo(new Point(center.X, center.Y + radius), true, false);
            ctx.LineTo(new Point(center.X - radius, center.Y), true, false);
        }
        geo.Freeze();
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    private void DrawRing(DrawingContext dc, RegionRow? region, Color color, double screenThickness)
    {
        if (region == null)
            return;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, Math.Max(screenThickness / _zoom, HexSize * 0.04));
        dc.DrawGeometry(null, pen, Outline([region]));
    }

    private void DrawLabels(DrawingContext dc)
    {
        var view = VisibleWorld();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");
        var brush = Brushes.White;
        var back = new SolidColorBrush(Color.FromArgb(170, 20, 24, 28));
        back.Freeze();
        foreach (var region in _regions)
        {
            var (x, y) = _layout.Center(region.Q, region.R);
            if (!view.Contains(x, y))
                continue;
            var text = new FormattedText(region.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, brush, dpi)
            {
                MaxTextWidth = HexSize * 1.7,
                TextAlignment = TextAlignment.Center,
            };
            var origin = new Point(x - text.Width / 2, y + HexSize * 0.08);
            dc.DrawRoundedRectangle(back, null, new Rect(origin.X - 2, origin.Y - 1, text.Width + 4, text.Height + 2), 3, 3);
            dc.DrawText(text, origin);
        }
    }

    private StreamGeometry Outline(IReadOnlyList<RegionRow> regions)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            foreach (var region in regions)
            {
                var (x0, y0) = _layout.Corner(region.Q, region.R, 0);
                ctx.BeginFigure(new Point(x0, y0), true, true);
                for (var i = 1; i < 6; i++)
                {
                    var (x, y) = _layout.Corner(region.Q, region.R, i);
                    ctx.LineTo(new Point(x, y), true, false);
                }
            }
        }
        geo.Freeze();
        return geo;
    }

    private void UpdateHover(Point point)
    {
        var region = Hit(point);
        if (region?.Id == _hover?.Id)
            return;
        _hover = region;
        Cursor = region == null ? Cursors.Arrow : Cursors.Hand;
        HoverChanged?.Invoke(this, region);
    }

    private RegionRow? Hit(Point screen)
    {
        if (_zoom <= 0)
            return null;
        var world = ScreenToWorld(screen);
        var axial = _layout.AxialAt(world.X, world.Y);
        return _byCoord.GetValueOrDefault(axial);
    }

    private Matrix CameraMatrix()
    {
        var ox = -_camX * _zoom + ActualWidth / 2;
        var oy = -_camY * _zoom + ActualHeight / 2;
        return new Matrix(_zoom, 0, 0, _zoom, ox, oy);
    }

    private Point ScreenToWorld(Point screen)
    {
        var matrix = CameraMatrix();
        matrix.Invert();
        return matrix.Transform(screen);
    }

    private Rect VisibleWorld()
    {
        var matrix = CameraMatrix();
        matrix.Invert();
        var topLeft = matrix.Transform(new Point(0, 0));
        var bottomRight = matrix.Transform(new Point(ActualWidth, ActualHeight));
        var rect = new Rect(topLeft, bottomRight);
        rect.Inflate(HexSize * 2, HexSize * 2);
        return rect;
    }
}
