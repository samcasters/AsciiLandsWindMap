using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AsciiLandsWindMap;

public sealed class CompassControl : FrameworkElement
{
    private double _direction;
    private int _strength;
    private bool _dragging;

    public CompassControl()
    {
        Cursor = Cursors.Hand;
        MinHeight = 200;
        MinWidth = 200;
    }

    public double DirectionDeg => _direction;

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters) =>
        new PointHitTestResult(this, hitTestParameters.HitPoint);
    public int Strength => _strength;

    public event EventHandler? Changed;

    public void SetReading(double directionDeg, int strength)
    {
        _direction = CompassText.Normalize(directionDeg);
        _strength = Math.Clamp(strength, 0, 3);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        Move(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
            Move(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        ReleaseMouseCapture();
        Move(e.GetPosition(this));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var (center, radius) = Layout();
        var face = new SolidColorBrush(Color.FromRgb(255, 252, 247));
        face.Freeze();
        var ink = new SolidColorBrush(Color.FromRgb(31, 111, 91));
        ink.Freeze();
        dc.DrawEllipse(face, new Pen(ink, 1.5), center, radius, radius);
        var ring = new Pen(new SolidColorBrush(Color.FromArgb(90, 31, 111, 91)), 1);
        foreach (var fraction in new[] { 0.34, 0.62, 0.90 })
            dc.DrawEllipse(null, ring, center, radius * fraction, radius * fraction);
        DrawRingLabel(dc, center, radius * 0.34, "~");
        DrawRingLabel(dc, center, radius * 0.62, "≈");
        DrawRingLabel(dc, center, radius * 0.90, "≋");
        DrawCardinal(dc, center, radius, "N", 0);
        DrawCardinal(dc, center, radius, "E", 90);
        DrawCardinal(dc, center, radius, "S", 180);
        DrawCardinal(dc, center, radius, "W", 270);

        if (_strength == 0)
        {
            dc.DrawEllipse(ink, null, center, 5, 5);
            return;
        }

        var markerFraction = _strength switch { 1 => 0.34, 2 => 0.62, _ => 0.90 };
        var rad = _direction * Math.PI / 180.0;
        var marker = new Point(
            center.X + Math.Sin(rad) * radius * markerFraction,
            center.Y - Math.Cos(rad) * radius * markerFraction);
        dc.DrawLine(new Pen(ink, 2), center, marker);
        dc.DrawEllipse(Brushes.White, new Pen(ink, 2), marker, 7, 7);
    }

    private void Move(Point point)
    {
        var (center, radius) = Layout();
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        var fraction = radius <= 0 ? 0 : dist / radius;
        _strength = fraction switch
        {
            < 0.18 => 0,
            < 0.48 => 1,
            < 0.76 => 2,
            _ => 3,
        };
        if (dist > 0.001)
            _direction = CompassText.Normalize(Math.Atan2(dx, -dy) * 180.0 / Math.PI);
        Changed?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private (Point Center, double Radius) Layout()
    {
        var radius = Math.Max(10, Math.Min(ActualWidth, ActualHeight) / 2 - 18);
        return (new Point(ActualWidth / 2, ActualHeight / 2), radius);
    }

    private void DrawCardinal(DrawingContext dc, Point center, double radius, string text, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        var point = new Point(center.X + Math.Sin(rad) * (radius + 2), center.Y - Math.Cos(rad) * (radius + 2));
        var formatted = Text(text, 12, Brushes.Gray);
        dc.DrawText(formatted, new Point(point.X - formatted.Width / 2, point.Y - formatted.Height / 2));
    }

    private void DrawRingLabel(DrawingContext dc, Point center, double ring, string text)
    {
        var formatted = Text(text, 11, Brushes.DimGray);
        dc.DrawText(formatted, new Point(center.X + 4, center.Y - ring - formatted.Height / 2));
    }

    private FormattedText Text(string value, double size, Brush brush)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, dpi);
    }
}
