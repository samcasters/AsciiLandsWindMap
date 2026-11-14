using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AsciiLandsWindMap;

public readonly record struct PlayerMark(int Q, int R, bool AtSea);

public readonly record struct RouteMark(string RegionId, int EnterSide, int ExitSide, bool Passed, bool Current);

public sealed class HexMapControl : FrameworkElement
{
    private const double SheetBudgetPixels = 8_000_000;
    private static readonly Brush ArrowFill = Frozen(Color.FromRgb(20, 22, 24));
    private static readonly Pen ArrowOutline = FrozenPen(Brushes.White, HexSize * 0.035);
    private static readonly Brush MarkBrush = Frozen(Color.FromRgb(233, 168, 37));
    private static readonly Brush FavoriteBrush = Frozen(Color.FromRgb(230, 179, 37));
    private static readonly Brush LabelBrush = Frozen(Color.FromArgb(77, 238, 245, 216));
    private static readonly Brush NorthBrush = Frozen(Color.FromRgb(238, 245, 216));
    private static readonly Typeface LabelFace = new("Trebuchet MS");
    private static readonly Brush InkBrush = Frozen(Color.FromRgb(42, 24, 18));
    private static readonly Pen InkPen = FrozenPen(InkBrush, 1.5);
    private static readonly Pen DangerInk = FrozenPen(InkBrush, 1.8);
    private static readonly Brush HereBrush = Frozen(Color.FromRgb(242, 107, 58));
    private static readonly Pen HereHalo = RoundPen(InkBrush, 5);
    private static readonly Pen HereStroke = RoundPen(HereBrush, 3);
    private static readonly Pen PlayerPen = FrozenPen(InkBrush, 1.6);
    private static readonly Pen SailPen = FrozenPen(HereBrush, 1.2);
    private static readonly Pen BodyPen = FrozenPen(HereBrush, 1.7);
    private static readonly Brush DayBorderBrush = Frozen(Color.FromArgb(110, 255, 255, 255));
    private static readonly Brush NightBorderBrush = Frozen(Color.FromArgb(80, 255, 255, 255));
    private static readonly Pen DayBorder = FrozenPen(DayBorderBrush, HexSize * 0.045);
    private static readonly Pen NightBorder = FrozenPen(NightBorderBrush, HexSize * 0.045);
    private static readonly Pen NorthPen = FrozenPen(NorthBrush, 1.5);
    private static readonly Brush CampBrush = Frozen(Color.FromRgb(106, 148, 78));
    private static readonly Brush HomeBrush = Frozen(Color.FromRgb(196, 112, 74));
    private static readonly Brush PortBrush = Frozen(Color.FromRgb(48, 120, 168));
    private static readonly Brush BoatBrush = Frozen(Color.FromRgb(70, 148, 176));
    private static readonly Brush DangerBrush = Frozen(Color.FromRgb(188, 64, 48));
    private static readonly Brush CacheBrush = Frozen(Color.FromRgb(186, 146, 64));
    private static readonly Brush StarBrush = Frozen(Color.FromRgb(220, 176, 64));
    private static readonly Brush ShopBrush = Frozen(Color.FromRgb(124, 96, 176));
    private static readonly Brush MapBackground = Frozen(Color.FromRgb(0, 0, 0));
    private static readonly Color HoverColor = Color.FromArgb(180, 255, 255, 255);
    private static readonly Color SelectColor = Color.FromRgb(255, 209, 121);

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
    private readonly Point[] _corner = new Point[6];
    private readonly MatrixTransform _cameraTransform = new();
    private readonly Dictionary<string, List<int>> _biomeBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<int> _visible = new(512);
    private readonly Dictionary<string, LabelBlock> _labelText = new(StringComparer.Ordinal);
    private List<RegionRow> _regions = [];
    private Plot[] _plots = [];
    private Dictionary<(int Q, int R), RegionRow> _byCoord = new();
    private Dictionary<string, RegionRow> _byId = new(StringComparer.Ordinal);
    private Dictionary<string, WindSample> _arrows = new(StringComparer.Ordinal);
    private Dictionary<string, ImageSource> _pictures = new(StringComparer.Ordinal);
    private List<RouteMark> _route = [];
    private int? _arrowStamp;
    private bool _arrowDirty;
    private int _contentSerial;
    private RenderTargetBitmap? _sheet;
    private double _sheetZoom;
    private double _sheetLeft;
    private double _sheetTop;
    private double _sheetDipW;
    private double _sheetDipH;
    private double _sheetWorldW;
    private double _sheetWorldH;
    private int _sheetSerial = -1;
    private bool _sheetLabels;
    private double _sheetDpiX = double.NaN;
    private double _sheetDpiY = double.NaN;
    private bool _deferLabels;
    private bool _deferSheet;
    private DispatcherTimer? _labelTimer;
    private double _labelDpi = double.NaN;
    private FormattedText? _northText;
    private double _northDpi = double.NaN;
    private Pen? _hoverPen;
    private Pen? _selectPen;
    private double _ringZoom = double.NaN;
    private bool _labelTimerHooked;
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
    private bool _painting;

    public HexMapControl()
    {
        ClipToBounds = true;
        Focusable = true;
        for (var i = 0; i < 6; i++)
        {
            var angle = Math.PI / 180.0 * (60 * i - 30);
            _corner[i] = new Point(HexSize * Math.Cos(angle), HexSize * Math.Sin(angle));
        }
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
        _plots = new Plot[_regions.Count];
        for (var i = 0; i < _regions.Count; i++)
        {
            var region = _regions[i];
            _byId[region.Id] = region;
            _byCoord[(region.Q, region.R)] = region;
            var (x, y) = _layout.Center(region.Q, region.R);
            _plots[i] = new Plot(region, x, y);
        }
        _arrows = new Dictionary<string, WindSample>(arrows, StringComparer.Ordinal);
        _night = night;
        _labelText.Clear();
        _arrowStamp = null;
        _arrowDirty = false;
        _contentSerial++;
        TryOpenWorkView();
        InvalidateVisual();
    }

    public void SetFrame(IReadOnlyDictionary<string, WindSample> arrows, bool night, PlayerMark? player, bool exact = false)
    {
        var playerMoved = _player != player;
        _player = player;
        var nightChanged = _night != night;
        if (nightChanged)
            _night = night;

        var stamp = ArrowPicture.HashSamples(_regions, arrows);
        var arrowsChanged = exact || _arrowStamp != stamp;
        if (!arrowsChanged && !nightChanged)
        {
            if (playerMoved)
                InvalidateVisual();
            return;
        }

        if (arrowsChanged)
        {
            _arrows = arrows as Dictionary<string, WindSample> ?? new Dictionary<string, WindSample>(arrows, StringComparer.Ordinal);
            _arrowStamp = stamp;
        }

        if (_panning && !nightChanged)
        {
            _arrowDirty = true;
            if (playerMoved)
                InvalidateVisual();
            return;
        }

        _arrowDirty = false;
        _contentSerial++;
        InvalidateVisual();
    }

    public void SetPlayer(PlayerMark? player)
    {
        if (_player == player)
            return;
        _player = player;
        InvalidateVisual();
    }

    public void Select(string? id)
    {
        SelectedId = id;
        InvalidateVisual();
    }

    public void SetPictures(IReadOnlyDictionary<string, ImageSource> pictures)
    {
        _pictures = new Dictionary<string, ImageSource>(pictures, StringComparer.Ordinal);
        _contentSerial++;
        InvalidateVisual();
    }

    public void SetRoute(IReadOnlyList<RouteMark>? marks)
    {
        _route = marks == null ? [] : marks.ToList();
        InvalidateVisual();
    }

    public void NoteChanged()
    {
        _labelText.Clear();
        _arrowStamp = ArrowPicture.HashSamples(_regions, _arrows);
        _contentSerial++;
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
        foreach (var plot in _plots)
        {
            minX = Math.Min(minX, plot.X);
            minY = Math.Min(minY, plot.Y);
            maxX = Math.Max(maxX, plot.X);
            maxY = Math.Max(maxY, plot.Y);
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
            _contentSerial++;
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
            InvalidateVisual();
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
        DeferLabels();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_painting)
            return;
        _painting = true;
        try
        {
            dc.DrawRectangle(MapBackground, null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (_regions.Count == 0 || ActualWidth < 1 || ActualHeight < 1 || _zoom <= 0)
            {
                DrawNorth(dc);
                return;
            }

            EnsureSheet();
            DrawSheet(dc);

            var matrix = CameraMatrix();
            _cameraTransform.Matrix = matrix;
            dc.PushTransform(_cameraTransform);
            EnsureRingPens();
            DrawRoute(dc);
            DrawRing(dc, _hover, _hoverPen!);
            DrawRing(dc, SelectedId == null ? null : _byId.GetValueOrDefault(SelectedId), _selectPen!);
            DrawPlayer(dc);
            dc.Pop();
            DrawHere(dc, matrix);
            DrawNorth(dc);
        }
        finally
        {
            _painting = false;
        }
    }

    private void DeferLabels()
    {
        _deferLabels = true;
        _deferSheet = _sheet != null;
        _labelTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        if (!_labelTimerHooked)
        {
            _labelTimer.Tick += (_, _) =>
            {
                _labelTimer?.Stop();
                if (!_deferLabels && !_deferSheet)
                    return;
                _deferLabels = false;
                _deferSheet = false;
                InvalidateVisual();
            };
            _labelTimerHooked = true;
        }
        _labelTimer.Stop();
        _labelTimer.Start();
    }

    private void EnsureSheet()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        var wantLabels = _zoom >= 0.75 && !_deferLabels;
        if (_deferSheet && _sheet != null && SheetCovers(NeededWorld()))
            return;
        if (_sheet != null
            && _sheetSerial == _contentSerial
            && _sheetZoom == _zoom
            && _sheetLabels == wantLabels
            && _sheetDpiX == dpi.PixelsPerInchX
            && _sheetDpiY == dpi.PixelsPerInchY
            && SheetCovers(NeededWorld()))
            return;

        var viewPx = ActualWidth * scaleX * ActualHeight * scaleY;
        var span = 2.0;
        if (viewPx > 0 && viewPx * span * span > SheetBudgetPixels)
            span = Math.Max(1, Math.Sqrt(SheetBudgetPixels / viewPx));

        var dipW = ActualWidth * span;
        var dipH = ActualHeight * span;
        var pxW = Math.Max(1, (int)Math.Ceiling(dipW * scaleX));
        var pxH = Math.Max(1, (int)Math.Ceiling(dipH * scaleY));
        _sheetDipW = pxW / scaleX;
        _sheetDipH = pxH / scaleY;
        _sheetWorldW = _sheetDipW / _zoom;
        _sheetWorldH = _sheetDipH / _zoom;
        _sheetLeft = _camX - _sheetWorldW / 2;
        _sheetTop = _camY - _sheetWorldH / 2;
        _sheetSerial = -1;
        var pixelsPerDip = dpi.PixelsPerDip;
        if (pixelsPerDip != _labelDpi)
        {
            _labelDpi = pixelsPerDip;
            _labelText.Clear();
        }

        var visual = new DrawingVisual();
        using (var sheet = visual.RenderOpen())
        {
            sheet.PushTransform(new MatrixTransform(_zoom, 0, 0, _zoom, -_sheetLeft * _zoom, -_sheetTop * _zoom));
            PaintWorld(sheet, new Rect(_sheetLeft, _sheetTop, _sheetWorldW, _sheetWorldH), wantLabels, pixelsPerDip);
            sheet.Pop();
        }

        var bitmap = new RenderTargetBitmap(pxW, pxH, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        _sheet = bitmap;
        _sheetZoom = _zoom;
        _sheetSerial = _contentSerial;
        _sheetLabels = wantLabels;
        _sheetDpiX = dpi.PixelsPerInchX;
        _sheetDpiY = dpi.PixelsPerInchY;
        _arrowDirty = false;
    }

    private bool SheetCovers(Rect need) =>
        _sheetLeft <= need.Left
        && _sheetTop <= need.Top
        && _sheetLeft + _sheetWorldW >= need.Right
        && _sheetTop + _sheetWorldH >= need.Bottom;

    private Rect NeededWorld()
    {
        var view = ViewportWorld();
        var slop = 1.0 / _zoom;
        if (view.Width <= slop * 2 || view.Height <= slop * 2)
            return view;
        view.Inflate(-slop, -slop);
        return view;
    }

    private Rect ViewportWorld()
    {
        var halfW = ActualWidth / (2 * _zoom);
        var halfH = ActualHeight / (2 * _zoom);
        return new Rect(_camX - halfW, _camY - halfH, halfW * 2, halfH * 2);
    }

    private void DrawSheet(DrawingContext dc)
    {
        if (_sheet == null)
            return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        var x = (_sheetLeft - _camX) * _zoom + ActualWidth / 2;
        var y = (_sheetTop - _camY) * _zoom + ActualHeight / 2;
        var w = _sheetWorldW * _zoom;
        var h = _sheetWorldH * _zoom;
        if (Math.Abs(w - _sheetDipW) < 0.5 && Math.Abs(h - _sheetDipH) < 0.5)
        {
            x = Math.Round(x * scaleX) / scaleX;
            y = Math.Round(y * scaleY) / scaleY;
            w = _sheetDipW;
            h = _sheetDipH;
        }
        dc.DrawImage(_sheet, new Rect(x, y, w, h));
    }

    private void PaintWorld(DrawingContext dc, Rect sheetWorld, bool labels, double pixelsPerDip)
    {
        var cull = sheetWorld;
        cull.Inflate(HexSize, HexSize);
        _visible.Clear();
        foreach (var bucket in _biomeBuckets.Values)
            bucket.Clear();

        for (var i = 0; i < _plots.Length; i++)
        {
            var plot = _plots[i];
            if (plot.X < cull.Left || plot.X > cull.Right || plot.Y < cull.Top || plot.Y > cull.Bottom)
                continue;
            _visible.Add(i);
            var biome = plot.Region.Biome;
            if (!_biomeBuckets.TryGetValue(biome, out var bucket))
                _biomeBuckets[biome] = bucket = [];
            bucket.Add(i);
        }

        foreach (var (biome, bucket) in _biomeBuckets)
        {
            if (bucket.Count == 0)
                continue;
            dc.DrawGeometry(BiomePalette.BrushFor(biome, _night), null, OutlineIndexes(bucket));
        }

        foreach (var index in _visible)
        {
            var plot = _plots[index];
            if (!_pictures.TryGetValue(plot.Region.Id, out var image))
                continue;
            var clip = OutlineAt(plot.X, plot.Y);
            var side = _layout.PictureSide();
            var rect = new Rect(plot.X - side / 2, plot.Y - side / 2, side, side);
            dc.PushClip(clip);
            if (_night)
            {
                dc.DrawRectangle(BiomePalette.NightLift, null, rect);
                dc.PushOpacity(BiomePalette.NightKeep);
                dc.DrawImage(image, rect);
                dc.Pop();
            }
            else
            {
                dc.DrawImage(image, rect);
            }
            dc.Pop();
        }

        if (_visible.Count > 0)
            dc.DrawGeometry(null, _night ? NightBorder : DayBorder, OutlineIndexes(_visible));

        if (labels)
        {
            foreach (var index in _visible)
                DrawLabel(dc, _plots[index], pixelsPerDip);
        }

        var shafts = new StreamGeometry();
        using (var ctx = shafts.Open())
        {
            foreach (var index in _visible)
            {
                var plot = _plots[index];
                if (!_arrows.TryGetValue(plot.Region.Id, out var sample) || sample.Cancelled || sample.Strength <= 0)
                    continue;
                AddArrow(ctx, plot.X, plot.Y, sample.DirectionDeg, WindRules.ArrowLength(sample.Strength, HexSize));
            }
        }
        shafts.Freeze();

        foreach (var index in _visible)
        {
            var plot = _plots[index];
            var icon = TileIcons.Canonical(plot.Region.Icon);
            if (icon != null)
            {
                // South tip is one hex-size below the center. Keep the mark above that point.
                DrawMark(dc, icon, new Point(plot.X, plot.Y + HexSize * 0.58));
            }
        }

        foreach (var index in _visible)
        {
            var plot = _plots[index];
            var region = plot.Region;
            if (region.Tags.Contains("stormy") || region.Tags.Contains("gale_edge"))
                DrawDiamond(dc, new Point(plot.X, plot.Y - HexSize * 0.62), HexSize * 0.11, MarkBrush);
            if (region.Tags.Contains("favorite"))
            {
                var hasIcon = TileIcons.Canonical(region.Icon) != null;
                var favoriteX = hasIcon ? plot.X - HexSize * 0.34 : plot.X;
                dc.DrawEllipse(FavoriteBrush, null, new Point(favoriteX, plot.Y + HexSize * 0.62), HexSize * 0.07, HexSize * 0.07);
            }
        }

        dc.DrawGeometry(ArrowFill, ArrowOutline, shafts);
    }

    private void TryOpenWorkView()
    {
        if (_opened || _regions.Count == 0 || ActualWidth < 1 || ActualHeight < 1)
            return;
        double sx = 0, sy = 0;
        foreach (var plot in _plots)
        {
            sx += plot.X;
            sy += plot.Y;
        }
        _camX = sx / _regions.Count;
        _camY = sy / _regions.Count;
        _zoom = 0.85;
        _opened = true;
        InvalidateVisual();
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

    private void DrawRoute(DrawingContext dc)
    {
        if (_route.Count == 0)
            return;
        var bright = RoutePen(Color.FromRgb(238, 245, 216), 2.1);
        var dim = RoutePen(Color.FromArgb(150, 106, 117, 96), 1.7);
        var go = RoutePen(Color.FromRgb(157, 228, 108), 2.5);
        foreach (var mark in _route)
        {
            if (!_byId.TryGetValue(mark.RegionId, out var region))
                continue;
            var (x, y) = _layout.Center(region.Q, region.R);
            if (mark.EnterSide >= 0)
                DrawHand(dc, x, y, mark.EnterSide, mark.Passed || mark.Current ? dim : bright, inward: true);
            if (mark.ExitSide >= 0)
            {
                var pen = mark.Passed ? dim : mark.Current ? go : bright;
                DrawHand(dc, x, y, mark.ExitSide, pen, inward: false);
            }
        }
    }

    private Pen RoutePen(Color color, double screenPx)
    {
        var brush = Frozen(color);
        var pen = new Pen(brush, Math.Max(screenPx / _zoom, HexSize * 0.03))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    private static void DrawHand(DrawingContext dc, double cx, double cy, int side, Pen pen, bool inward)
    {
        var (sx, sy) = Axial.SideVector(side);
        var reach = HexSize * Math.Sqrt(3) / 2 * 0.92;
        var end = new Point(cx + sx * reach, cy + sy * reach);
        dc.DrawLine(pen, new Point(cx, cy), end);
        var dirX = inward ? -sx : sx;
        var dirY = inward ? -sy : sy;
        var tip = inward
            ? new Point(cx + sx * reach * 0.46, cy + sy * reach * 0.46)
            : end;
        var len = HexSize * 0.16;
        var half = len * 0.55;
        var back = new Point(tip.X - dirX * len, tip.Y - dirY * len);
        var px = -dirY;
        var py = dirX;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(tip, true, true);
            ctx.LineTo(new Point(back.X + px * half, back.Y + py * half), true, false);
            ctx.LineTo(new Point(back.X - px * half, back.Y - py * half), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(pen.Brush, null, geo);
    }

    private void DrawRing(DrawingContext dc, RegionRow? region, Pen pen)
    {
        if (region == null)
            return;
        var (x, y) = _layout.Center(region.Q, region.R);
        dc.DrawGeometry(null, pen, OutlineAt(x, y));
    }

    private void EnsureRingPens()
    {
        if (_hoverPen != null && _selectPen != null && _ringZoom == _zoom)
            return;
        _hoverPen = RingPen(HoverColor, 1.4);
        _selectPen = RingPen(SelectColor, 2.4);
        _ringZoom = _zoom;
    }

    private Pen RingPen(Color color, double screenThickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, Math.Max(screenThickness / _zoom, HexSize * 0.04));
        pen.Freeze();
        return pen;
    }

    private void DrawLabel(DrawingContext dc, Plot plot, double dpi)
    {
        var region = plot.Region;
        if (!_labelText.TryGetValue(region.Id, out var block))
        {
            var caption = region.Caption.Trim();
            var name = MakeLabel(region.Name, dpi, caption.Length > 0 ? HexSize * 0.34 : HexSize * 0.55);
            FormattedText? sub = caption.Length > 0 ? MakeLabel(caption, dpi, HexSize * 0.30) : null;
            block = new LabelBlock(name, sub);
            _labelText[region.Id] = block;
        }
        var height = block.Name.Height + (block.Caption?.Height ?? 0);
        var top = plot.Y - height / 2;
        dc.DrawText(block.Name, new Point(plot.X - block.Name.Width / 2, top));
        if (block.Caption != null)
            dc.DrawText(block.Caption, new Point(plot.X - block.Caption.Width / 2, top + block.Name.Height));
    }

    private static FormattedText MakeLabel(string value, double dpi, double maxHeight) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 8, LabelBrush, dpi)
        {
            MaxTextWidth = HexSize * 1.35,
            MaxTextHeight = maxHeight,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };

    private void DrawHere(DrawingContext dc, Matrix matrix)
    {
        if (_player is not PlayerMark player || !_byCoord.ContainsKey((player.Q, player.R)))
            return;
        var (x, y) = _layout.Center(player.Q, player.R);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(matrix.Transform(new Point(x + _corner[0].X, y + _corner[0].Y)), false, true);
            for (var i = 1; i < 6; i++)
                ctx.LineTo(matrix.Transform(new Point(x + _corner[i].X, y + _corner[i].Y)), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, HereHalo, geo);
        dc.DrawGeometry(null, HereStroke, geo);
    }

    private void DrawNorth(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_northText == null || dpi != _northDpi)
        {
            _northDpi = dpi;
            _northText = new FormattedText("N", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 13, NorthBrush, dpi);
        }
        dc.DrawText(_northText, new Point(18, 8));
        dc.DrawLine(NorthPen, new Point(24, 26), new Point(24, 12));
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
                DrawShip(dc, center, BoatBrush, InkPen, null);
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
        if (player.AtSea)
            DrawShip(dc, center, HereBrush, PlayerPen, SailPen);
        else
            DrawPerson(dc, center, HereBrush, PlayerPen);
    }

    private static void DrawShip(DrawingContext dc, Point center, Brush fill, Pen pen, Pen? highlight)
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
        highlight ??= new Pen(fill, 1.2);
        dc.DrawLine(highlight, mastFoot, mastTop);
    }

    private static void DrawPerson(DrawingContext dc, Point center, Brush fill, Pen pen)
    {
        var s = HexSize * 0.14;
        dc.DrawEllipse(fill, pen, new Point(center.X, center.Y - s * 0.85), s * 0.38, s * 0.38);
        DrawLimb(dc, pen, BodyPen, new Point(center.X, center.Y - s * 0.45), new Point(center.X, center.Y + s * 0.35));
        DrawLimb(dc, pen, BodyPen, new Point(center.X - s * 0.55, center.Y - s * 0.05), new Point(center.X + s * 0.55, center.Y - s * 0.05));
        DrawLimb(dc, pen, BodyPen, new Point(center.X, center.Y + s * 0.35), new Point(center.X - s * 0.4, center.Y + s));
        DrawLimb(dc, pen, BodyPen, new Point(center.X, center.Y + s * 0.35), new Point(center.X + s * 0.4, center.Y + s));
    }

    private static void DrawLimb(DrawingContext dc, Pen outline, Pen body, Point from, Point to)
    {
        dc.DrawLine(outline, from, to);
        dc.DrawLine(body, from, to);
    }

    private StreamGeometry OutlineIndexes(List<int> indexes)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            foreach (var index in indexes)
            {
                var plot = _plots[index];
                ctx.BeginFigure(new Point(plot.X + _corner[0].X, plot.Y + _corner[0].Y), true, true);
                for (var i = 1; i < 6; i++)
                    ctx.LineTo(new Point(plot.X + _corner[i].X, plot.Y + _corner[i].Y), true, false);
            }
        }
        geo.Freeze();
        return geo;
    }

    private StreamGeometry OutlineAt(double x, double y)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(x + _corner[0].X, y + _corner[0].Y), true, true);
            for (var i = 1; i < 6; i++)
                ctx.LineTo(new Point(x + _corner[i].X, y + _corner[i].Y), true, false);
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
        InvalidateVisual();
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

    private readonly record struct Plot(RegionRow Region, double X, double Y);

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
