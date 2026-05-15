using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace LightBulb.Views.Components;

/// <summary>
/// A lightweight equirectangular world-map picker.
/// Click anywhere to set Latitude/Longitude.
/// No external library required — continent outlines are hardcoded path data
/// in a 360×180 coordinate space (x = lon+180, y = 90-lat).
/// </summary>
public class WorldMapPickerControl : Control
{
    // ── Styled properties ─────────────────────────────────────────────────────

    public static readonly StyledProperty<double> LatitudeProperty =
        AvaloniaProperty.Register<WorldMapPickerControl, double>(
            nameof(Latitude), 0.0,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> LongitudeProperty =
        AvaloniaProperty.Register<WorldMapPickerControl, double>(
            nameof(Longitude), 0.0,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public double Latitude
    {
        get => GetValue(LatitudeProperty);
        set => SetValue(LatitudeProperty, value);
    }

    public double Longitude
    {
        get => GetValue(LongitudeProperty);
        set => SetValue(LongitudeProperty, value);
    }

    // ── Brushes & pens (created once) ────────────────────────────────────────

    private static readonly IBrush OceanBrush  = new SolidColorBrush(Color.FromRgb(0x1a, 0x52, 0x76));
    private static readonly IBrush LandBrush   = new SolidColorBrush(Color.FromRgb(0x4a, 0x7c, 0x46));
    private static readonly IBrush LandOutline = new SolidColorBrush(Color.FromRgb(0x35, 0x60, 0x30));
    private static readonly IBrush PinFill     = new SolidColorBrush(Color.FromRgb(0xff, 0x44, 0x44));
    private static readonly IBrush PinCore     = new SolidColorBrush(Color.FromRgb(0xff, 0xff, 0xff));
    private static readonly IPen   LandPen     = new Pen(LandOutline, 0.5);
    private static readonly IPen   GridPen     = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xff, 0xff, 0xff)), 0.3);
    private static readonly IPen   PinPen      = new Pen(PinFill, 1.5);

    // ── Continent geometry (360×180 map-space coordinates) ───────────────────
    // x = lon + 180,  y = 90 - lat

    private static readonly Geometry[] Continents;

    static WorldMapPickerControl()
    {
        string[] paths =
        [
            // North America (clockwise from Alaska NW)
            "M 12 18 L 25 30 L 45 32 L 55 42 L 56 52 L 65 62 L 90 74 " +
            "L 101 82 L 105 80 L 120 80 L 125 78 L 123 43 L 121 37 " +
            "L 117 27 L 104 22 L 100 17 L 85 7 L 60 10 L 40 14 Z",

            // South America
            "M 108 78 L 120 82 L 130 87 L 145 95 L 145 100 L 142 112 " +
            "L 128 122 L 123 128 L 115 145 L 112 145 L 108 140 " +
            "L 107 130 L 104 115 L 100 98 L 103 89 Z",

            // Europe (including Scandinavia)
            "M 170 55 L 185 54 L 195 52 L 202 53 L 210 51 L 220 48 " +
            "L 235 48 L 240 33 L 205 18 L 190 27 L 185 32 " +
            "L 175 32 L 175 40 Z",

            // Africa
            "M 165 56 L 217 60 L 222 79 L 231 79 L 224 102 L 215 115 " +
            "L 205 124 L 198 125 L 193 107 L 181 85 L 165 77 Z",

            // Asia (including Middle East, India, SE Asia, Russia)
            "M 207 50 L 220 47 L 235 48 L 240 20 L 280 15 L 320 15 " +
            "L 350 25 L 325 42 L 310 58 L 300 68 L 285 88 L 280 85 " +
            "L 260 82 L 253 70 L 240 68 L 235 78 L 224 75 " +
            "L 217 60 L 210 50 Z",

            // Australia
            "M 294 104 L 316 102 L 330 106 L 333 118 L 331 127 " +
            "L 328 131 L 295 124 L 294 115 Z",

            // Greenland
            "M 107 30 L 160 30 L 162 13 L 135 5 L 107 12 Z",

            // Antarctica (full-width southern band)
            "M 0 158 L 360 158 L 360 180 L 0 180 Z",
        ];

        Continents = new Geometry[paths.Length];
        for (int i = 0; i < paths.Length; i++)
            Continents[i] = Geometry.Parse(paths[i]);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        double sx = w / 360.0;
        double sy = h / 180.0;

        // Ocean background
        ctx.DrawRectangle(OceanBrush, null, new Rect(0, 0, w, h));

        // Continent polygons — drawn in map-space using a scale transform
        using (ctx.PushTransform(Matrix.CreateScale(sx, sy)))
        {
            foreach (var geo in Continents)
                ctx.DrawGeometry(LandBrush, LandPen, geo);
        }

        // Lat/lon grid lines (every 30°)
        for (int lon = -150; lon <= 180; lon += 30)
        {
            double px = (lon + 180) * sx;
            ctx.DrawLine(GridPen, new Point(px, 0), new Point(px, h));
        }
        for (int lat = -60; lat <= 60; lat += 30)
        {
            double py = (90 - lat) * sy;
            ctx.DrawLine(GridPen, new Point(0, py), new Point(w, py));
        }
        // Equator slightly more visible
        {
            double py = 90 * sy;
            ctx.DrawLine(
                new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xff, 0xff, 0xff)), 0.6),
                new Point(0, py), new Point(w, py));
        }

        // Location pin
        double pinX = (Longitude + 180) * sx;
        double pinY = (90 - Latitude)   * sy;
        ctx.DrawEllipse(PinFill, null,  new Point(pinX, pinY), 5, 5);
        ctx.DrawEllipse(PinCore, PinPen, new Point(pinX, pinY), 2, 2);
    }

    // ── Interaction ───────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        UpdateFromPointer(e.GetPosition(this));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured == this)
        {
            UpdateFromPointer(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Captured == this)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void UpdateFromPointer(Point pos)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        double lon = pos.X / Bounds.Width  * 360.0 - 180.0;
        double lat = 90.0  - pos.Y / Bounds.Height * 180.0;
        Longitude = Math.Clamp(lon, -180, 180);
        Latitude  = Math.Clamp(lat, -90,  90);
    }

    // Redraw whenever lat/lon changes
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LatitudeProperty || change.Property == LongitudeProperty)
            InvalidateVisual();
    }
}
