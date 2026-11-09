using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AsciiLandsWindMap;

public readonly record struct PlayerMark(int Q, int R, bool AtSea);

public sealed class HexMapControl : FrameworkElement
{
    private static readonly Brush ArrowFill = Frozen(Color.FromRgb(20, 22, 24));
    private static readonly Pen ArrowOutline = FrozenPen(Brushes.White, HexSize * 0.035);
    private static readonly Brush MarkBrush = Frozen(Color.FromRgb(233, 168, 37));
    private static readonly Brush FavoriteBrush = Frozen(Color.FromRgb(230, 179, 37));
    private static readonly Brush LabelBrush = Frozen(Color.FromArgb(77, 255, 255, 255));
    private static readonly Typeface LabelFace = new("Segoe UI");
    private static readonly Brush InkBrush = Frozen(Color.FromRgb(42, 24, 18));
    private static readonly Pen InkPen = FrozenPen(InkBrush, 1.5);
    private static readonly Pen DangerInk = FrozenPen(InkBrush, 1.8);
    private static readonly Brush HereBrush = Frozen(Color.FromRgb(242, 107, 58));
    private static readonly Pen HereHalo = RoundPen(InkBrush, 5);
    private static readonly Pen HereStroke = RoundPen(HereBrush, 3);
    private static readonly Brush CampBrush = Frozen(Color.FromRgb(106, 148, 78));
    private static readonly Brush HomeBrush = Frozen(Color.FromRgb(196, 112, 74));
    private static readonly Brush PortBrush = Frozen(Color.FromRgb(48, 120, 168));
    private static readonly Brush BoatBrush = Frozen(Color.FromRgb(70, 148, 176));
    private static readonly Brush DangerBrush = Frozen(Color.FromRgb(188, 64, 48));
    private static readonly Brush CacheBrush = Frozen(Color.FromRgb(186, 146, 64));
    private static readonly Brush StarBrush = Frozen(Color.FromRgb(220, 176, 64));
    private static readonly Brush ShopBrush = Frozen(Color.FromRgb(124, 96, 176));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static Pen RoundPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        return pen;
    }

    public const double HexSize = 36;

    private readonly HexLayout _layout = new(HexSize);
    private List<RegionRow> _regions = [];
    private Dictionary<(int Q, int R), RegionRow> _byCoord = new();
    private Dictionary<string, RegionRow> _byId = new(StringComparer.Ordinal);
    private Dictionary<string, WindSample> _arrows = new(StringComparer.Ordinal);
    private Drawing? _terrain;
    private DrawingGroup? _arrowLayer;
    private int? _arrowStamp;
    private bool _arrowDirty;
    private readonly Dictionary<string, LabelBlock> _labelText = new(StringComparer.Ordinal);
    private double _labelDpi = double.NaN;
    private DrawingGroup? _markLayer;
    private int? _markStamp;
    private bool _night;
    private PlayerMark? _player;
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
        _labelText.Clear();
        _arrowLayer = null;
        _arrowStamp = null;
        _arrowDirty = false;
        _markLayer = null;
        _markStamp = null;
        RebuildTerrain();
        TryOpenWorkView();
        InvalidateVisual();
    }

    public void SetFrame(IReadOnlyDictionary<string, WindSample> arrows, bool night, PlayerMark? player)
    {
        var playerMoved = _player != player;
        _player = player;
        var nightChanged = _terrain == null || _night != night;
        if (nightChanged)
        {
            _night = night;
            _mapBackground = new SolidColorBrush(night ? Color.FromRgb(14, 20, 27) : Color.FromRgb(213, 224, 230));
            _mapBackground.Freeze();
            if (_regions.Count > 0)
                RebuildTerrain();
        }

        var stamp = ArrowPicture.HashSamples(_regions, arrows);
        if (_arrowStamp == stamp && _arrowLayer != null && !_arrowDirty)
        {
            if (playerMoved || nightChanged)
                InvalidateVisual();
            return;
        }

        _arrows = arrows as Dictionary<string, WindSample> ?? new Dictionary<string, WindSample>(arrows, StringComparer.Ordinal);
        _arrowStamp = stamp;
        if (_panning)
        {
            _arrowDirty = true;
            return;
        }

        _arrowDirty = false;
        RebuildArrowLayer();
        InvalidateVisual();
    }

    public void Select(string? id)
    {
        SelectedId = id;
        InvalidateVisual();
    }

    public void NoteChanged()
    {
        _labelText.Clear();
        _markLayer = null;
        _markStamp = null;
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
        if (_arrowDirty)
        {
            _arrowDirty = false;
            _arrowStamp = ArrowPicture.HashSamples(_regions, _arrows);
            RebuildArrowLayer();
        }
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
        if (_zoom >= 0.75 && !_panning)
            DrawLabels(dc);
        DrawPlayer(dc);
        DrawMarks(dc);
        if (_arrowLayer != null)
            dc.DrawDrawing(_arrowLayer);
        DrawRing(dc, _hover, Color.FromArgb(180, 255, 255, 255), 1.4);
        DrawRing(dc, SelectedId == null ? null : _byId.GetValueOrDefault(SelectedId), Color.FromRgb(230, 179, 37), 2.4);
        dc.Pop();
        DrawHere(dc);

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

    private void RebuildArrowLayer()
    {
        var shafts = new StreamGeometry();
        using (var ctx = shafts.Open())
        {
            foreach (var region in _regions)
            {
                if (!_arrows.TryGetValue(region.Id, out var sample) || sample.Cancelled || sample.Strength <= 0)
                    continue;
                var (x, y) = _layout.Center(region.Q, region.R);
                AddArrow(ctx, x, y, sample.DirectionDeg, WindRules.ArrowLength(sample.Strength, HexSize));
            }
        }
        shafts.Freeze();

        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            foreach (var region in _regions)
            {
                var marked = region.Tags.Contains("stormy") || region.Tags.Contains("gale_edge");
                var favorite = region.Tags.Contains("favorite");
                if (!marked && !favorite)
                    continue;
                var (x, y) = _layout.Center(region.Q, region.R);
                if (marked)
                    DrawDiamond(dc, new Point(x, y - HexSize * 0.62), HexSize * 0.11, MarkBrush);
                if (favorite)
                {
                    var hasIcon = TileIcons.Canonical(region.Icon) != null;
                    var favoriteX = hasIcon ? x - HexSize * 0.34 : x;
                    dc.DrawEllipse(FavoriteBrush, null, new Point(favoriteX, y + HexSize * 0.62), HexSize * 0.07, HexSize * 0.07);
                }
            }
            dc.DrawGeometry(ArrowFill, ArrowOutline, shafts);
        }
        group.Freeze();
        _arrowLayer = group;
    }

    private static void AddArrow(StreamGeometryContext ctx, double x, double y, double degrees, double length)
    {
        var rad = degrees * Math.PI / 180.0;
        var dx = Math.Sin(rad);
        var dy = -Math.Cos(rad);
        var px = -dy;
        var py = dx;
        var tipX = x + dx * length * 0.58;
        var tipY = y + dy * length * 0.58;
        var tailX = x - dx * length * 0.42;
        var tailY = y - dy * length * 0.42;
        var head = length * 0.36;
        var baseX = tipX - dx * head;
        var baseY = tipY - dy * head;
        var half = Math.Max(1.2, length * 0.16) * 0.5;
        var headHalf = head * 0.55;
        ctx.BeginFigure(new Point(tailX + px * half, tailY + py * half), true, true);
        ctx.LineTo(new Point(baseX + px * half, baseY + py * half), true, false);
        ctx.LineTo(new Point(baseX - px * half, baseY - py * half), true, false);
        ctx.LineTo(new Point(tailX - px * half, tailY - py * half), true, false);
        ctx.BeginFigure(new Point(tipX, tipY), true, true);
        ctx.LineTo(new Point(baseX + px * headHalf, baseY + py * headHalf), true, false);
        ctx.LineTo(new Point(baseX - px * headHalf, baseY - py * headHalf), true, false);
    }

    private static void DrawDiamond(DrawingContext dc, Point center, double radius, Brush brush)
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
        if (dpi != _labelDpi)
        {
            _labelDpi = dpi;
            _labelText.Clear();
        }
        foreach (var region in _regions)
        {
            var (x, y) = _layout.Center(region.Q, region.R);
            if (!view.Contains(x, y))
                continue;
            if (!_labelText.TryGetValue(region.Id, out var block))
            {
                var caption = region.Caption.Trim();
                var name = MakeLabel(region.Name, dpi, caption.Length > 0 ? HexSize * 0.34 : HexSize * 0.55);
                FormattedText? sub = caption.Length > 0 ? MakeLabel(caption, dpi, HexSize * 0.30) : null;
                block = new LabelBlock(name, sub);
                _labelText[region.Id] = block;
            }
            var height = block.Name.Height + (block.Caption?.Height ?? 0);
            var top = y - height / 2;
            dc.DrawText(block.Name, new Point(x - block.Name.Width / 2, top));
            if (block.Caption != null)
                dc.DrawText(block.Caption, new Point(x - block.Caption.Width / 2, top + block.Name.Height));
        }
    }

    private static FormattedText MakeLabel(string value, double dpi, double maxHeight) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 8, LabelBrush, dpi)
        {
            MaxTextWidth = HexSize * 1.35,
            MaxTextHeight = maxHeight,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };

    private void DrawHere(DrawingContext dc)
    {
        if (_player is not PlayerMark player || !_byCoord.ContainsKey((player.Q, player.R)))
            return;
        var matrix = CameraMatrix();
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var (x0, y0) = _layout.Corner(player.Q, player.R, 0);
            ctx.BeginFigure(matrix.Transform(new Point(x0, y0)), false, true);
            for (var i = 1; i < 6; i++)
            {
                var (x, y) = _layout.Corner(player.Q, player.R, i);
                ctx.LineTo(matrix.Transform(new Point(x, y)), true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, HereHalo, geo);
        dc.DrawGeometry(null, HereStroke, geo);
    }

    private void DrawMarks(DrawingContext dc)
    {
        var stamp = MarkStamp();
        if (_markLayer == null || _markStamp != stamp)
        {
            _markStamp = stamp;
            _markLayer = BuildMarks();
        }
        dc.DrawDrawing(_markLayer);
    }

    private int MarkStamp()
    {
        var hash = new HashCode();
        foreach (var region in _regions)
        {
            hash.Add(region.Id);
            hash.Add(region.Icon);
        }
        return hash.ToHashCode();
    }

    private DrawingGroup BuildMarks()
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            foreach (var region in _regions)
            {
                var icon = TileIcons.Canonical(region.Icon);
                if (icon == null)
                    continue;
                var (x, y) = _layout.Center(region.Q, region.R);
                // South tip is one hex-size below the center. Keep the mark above that point.
                DrawMark(dc, icon, new Point(x, y + HexSize * 0.58));
            }
        }
        group.Freeze();
        return group;
    }

    private static void DrawMark(DrawingContext dc, string icon, Point center)
    {
        var s = HexSize * 0.17;
        switch (icon)
        {
            case "camp":
                DrawPoly(dc, CampBrush, center, (0, -s), (s * 0.85, s * 0.75), (-s * 0.85, s * 0.75));
                dc.DrawLine(InkPen, new Point(center.X, center.Y - s * 0.1), new Point(center.X, center.Y + s * 0.75));
                break;
            case "home":
                DrawPoly(dc, HomeBrush, center, (0, -s), (s * 0.85, -s * 0.15), (s * 0.85, s * 0.75), (-s * 0.85, s * 0.75), (-s * 0.85, -s * 0.15));
                dc.DrawRectangle(InkBrush, null, new Rect(center.X - s * 0.18, center.Y + s * 0.18, s * 0.36, s * 0.57));
                break;
            case "port":
                dc.DrawEllipse(PortBrush, InkPen, new Point(center.X, center.Y - s * 0.55), s * 0.28, s * 0.28);
                dc.DrawRectangle(PortBrush, InkPen, new Rect(center.X - s * 0.08, center.Y - s * 0.32, s * 0.16, s * 0.9));
                dc.DrawRectangle(PortBrush, InkPen, new Rect(center.X - s * 0.42, center.Y - s * 0.08, s * 0.84, s * 0.14));
                DrawPoly(dc, PortBrush, center, (-s * 0.05, s * 0.45), (-s * 0.5, s * 0.12), (-s * 0.05, s * 0.62));
                DrawPoly(dc, PortBrush, center, (s * 0.05, s * 0.45), (s * 0.5, s * 0.12), (s * 0.05, s * 0.62));
                break;
            case "boat":
                DrawShip(dc, center, BoatBrush, InkPen);
                break;
            case "danger":
                DrawPoly(dc, DangerBrush, center, (0, -s), (s * 0.82, s * 0.72), (-s * 0.82, s * 0.72));
                dc.DrawLine(DangerInk, new Point(center.X, center.Y - s * 0.35), new Point(center.X, center.Y + s * 0.15));
                dc.DrawEllipse(InkBrush, null, new Point(center.X, center.Y + s * 0.4), s * 0.08, s * 0.08);
                break;
            case "cache":
                dc.DrawRectangle(CacheBrush, InkPen, new Rect(center.X - s * 0.78, center.Y - s * 0.5, s * 1.56, s * 0.36));
                dc.DrawRectangle(CacheBrush, InkPen, new Rect(center.X - s * 0.68, center.Y - s * 0.16, s * 1.36, s * 0.86));
                dc.DrawRectangle(InkBrush, null, new Rect(center.X - s * 0.1, center.Y + s * 0.05, s * 0.2, s * 0.24));
                break;
            case "star":
                DrawStar(dc, center, s);
                break;
            case "shop":
                dc.DrawRectangle(ShopBrush, InkPen, new Rect(center.X - s * 0.82, center.Y - s * 0.62, s * 1.64, s * 0.4));
                dc.DrawRectangle(ShopBrush, InkPen, new Rect(center.X - s * 0.58, center.Y - s * 0.24, s * 1.16, s * 0.98));
                break;
        }
    }

    private static void DrawPoly(DrawingContext dc, Brush fill, Point origin, params (double X, double Y)[] offsets)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(origin.X + offsets[0].X, origin.Y + offsets[0].Y), true, true);
            for (var i = 1; i < offsets.Length; i++)
                ctx.LineTo(new Point(origin.X + offsets[i].X, origin.Y + offsets[i].Y), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(fill, InkPen, geo);
    }

    private static void DrawStar(DrawingContext dc, Point center, double radius)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            Point At(int index)
            {
                var outer = index % 2 == 0;
                var span = outer ? radius : radius * 0.42;
                var angle = -Math.PI / 2 + index * Math.PI / 5;
                return new Point(center.X + Math.Cos(angle) * span, center.Y + Math.Sin(angle) * span);
            }
            ctx.BeginFigure(At(0), true, true);
            for (var i = 1; i < 10; i++)
                ctx.LineTo(At(i), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(StarBrush, InkPen, geo);
    }

    private void DrawPlayer(DrawingContext dc)
    {
        if (_player is not PlayerMark player)
            return;
        var (x, y) = _layout.Center(player.Q, player.R);
        // North tip sits one hex-size above the center. Keep the figure below that point.
        var center = new Point(x, y - HexSize * 0.58);
        var fill = new SolidColorBrush(Color.FromRgb(242, 107, 58));
        fill.Freeze();
        var ink = new SolidColorBrush(Color.FromRgb(42, 24, 18));
        ink.Freeze();
        var pen = new Pen(ink, 1.6);
        pen.Freeze();
        if (player.AtSea)
            DrawShip(dc, center, fill, pen);
        else
            DrawPerson(dc, center, fill, pen);
    }

    private static void DrawShip(DrawingContext dc, Point center, Brush fill, Pen pen)
    {
        var s = HexSize * 0.16;
        var hull = new StreamGeometry();
        using (var ctx = hull.Open())
        {
            ctx.BeginFigure(new Point(center.X, center.Y - s), true, true);
            ctx.LineTo(new Point(center.X + s * 0.72, center.Y + s * 0.35), true, false);
            ctx.LineTo(new Point(center.X + s * 0.4, center.Y + s * 0.72), true, false);
            ctx.LineTo(new Point(center.X - s * 0.4, center.Y + s * 0.72), true, false);
            ctx.LineTo(new Point(center.X - s * 0.72, center.Y + s * 0.35), true, false);
        }
        hull.Freeze();
        dc.DrawGeometry(fill, pen, hull);
        var mastTop = new Point(center.X, center.Y - s * 0.85);
        var mastFoot = new Point(center.X, center.Y + s * 0.15);
        dc.DrawLine(pen, mastFoot, mastTop);
        dc.DrawLine(new Pen(fill, 1.2), mastFoot, mastTop);
    }

    private static void DrawPerson(DrawingContext dc, Point center, Brush fill, Pen pen)
    {
        var s = HexSize * 0.14;
        var body = new Pen(fill, 1.7);
        dc.DrawEllipse(fill, pen, new Point(center.X, center.Y - s * 0.85), s * 0.38, s * 0.38);
        DrawLimb(dc, pen, body, new Point(center.X, center.Y - s * 0.45), new Point(center.X, center.Y + s * 0.35));
        DrawLimb(dc, pen, body, new Point(center.X - s * 0.55, center.Y - s * 0.05), new Point(center.X + s * 0.55, center.Y - s * 0.05));
        DrawLimb(dc, pen, body, new Point(center.X, center.Y + s * 0.35), new Point(center.X - s * 0.4, center.Y + s));
        DrawLimb(dc, pen, body, new Point(center.X, center.Y + s * 0.35), new Point(center.X + s * 0.4, center.Y + s));
    }

    private static void DrawLimb(DrawingContext dc, Pen outline, Pen body, Point from, Point to)
    {
        dc.DrawLine(outline, from, to);
        dc.DrawLine(body, from, to);
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

    private sealed record LabelBlock(FormattedText Name, FormattedText? Caption);
}

static class ArrowPicture
{
    private const double Step = 6;

    public static int HashSamples(IReadOnlyList<RegionRow> regions, IReadOnlyDictionary<string, WindSample> arrows)
    {
        var hash = new HashCode();
        foreach (var region in regions)
        {
            hash.Add(region.Tags.Contains("stormy") || region.Tags.Contains("gale_edge"));
            hash.Add(region.Tags.Contains("favorite"));
            hash.Add(region.Icon);
            if (arrows.TryGetValue(region.Id, out var sample) && !sample.Cancelled && sample.Strength > 0)
            {
                hash.Add(sample.Strength);
                hash.Add(Bucket(sample.DirectionDeg));
            }
            else
            {
                hash.Add(0);
            }
        }
        return hash.ToHashCode();
    }

    public static int HashSea(IReadOnlyList<RegionRow> regions, IReadOnlyDictionary<string, SeaWindArrow> arrows)
    {
        var hash = new HashCode();
        foreach (var region in regions)
        {
            if (arrows.TryGetValue(region.Id, out var arrow) && arrow.Strength > 0)
            {
                hash.Add(arrow.Strength);
                hash.Add(Bucket(arrow.DirectionDeg));
            }
            else
            {
                hash.Add(0);
            }
        }
        return hash.ToHashCode();
    }

    private static int Bucket(double degrees) =>
        (int)Math.Round(CompassText.Normalize(degrees) / Step, MidpointRounding.AwayFromZero);
}
