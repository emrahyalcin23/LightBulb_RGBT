using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LightBulb.Models;

namespace LightBulb.Views.Components;

/// <summary>
/// Interactive calibration curve editor.
///
/// Shows up to 5 channel curves simultaneously (Brightness, R, G, B, L).
/// The active (SelectedChannel) curve is rendered thick; the rest are dim references.
///
/// All channels share the same X axis (sensor CIE-Y / rawY).
/// Each channel's values are normalized to [0,1] for display:
///   Brightness : 0–100 % → 0–1
///   R/G/B/L    : −1 … +1 → 0–1
/// The Y-axis labels reflect the active channel's real scale.
///
/// Mouse gestures
/// ──────────────
///   Left-click on empty area  → fire GraphPointAddRequested (add new calibration point)
///   Left-click+drag on point  → fire GraphPointMoved (drag that point)
///   Right-click on point      → fire GraphPointRemoveRequested (remove that point)
/// </summary>
public class CalibrationCurveControl : Control
{
    // ── Styled properties ──────────────────────────────────────────────────────

    public static readonly StyledProperty<IReadOnlyList<UsbCalibrationPoint>?> PointsProperty =
        AvaloniaProperty.Register<CalibrationCurveControl, IReadOnlyList<UsbCalibrationPoint>?>(nameof(Points));

    public static readonly StyledProperty<double> LiveRawYProperty =
        AvaloniaProperty.Register<CalibrationCurveControl, double>(nameof(LiveRawY), 0.0);

    public static readonly StyledProperty<CalibrationChannel> SelectedChannelProperty =
        AvaloniaProperty.Register<CalibrationCurveControl, CalibrationChannel>(
            nameof(SelectedChannel), CalibrationChannel.Brightness
        );

    public IReadOnlyList<UsbCalibrationPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public double LiveRawY
    {
        get => GetValue(LiveRawYProperty);
        set => SetValue(LiveRawYProperty, value);
    }

    public CalibrationChannel SelectedChannel
    {
        get => GetValue(SelectedChannelProperty);
        set => SetValue(SelectedChannelProperty, value);
    }

    static CalibrationCurveControl()
    {
        AffectsRender<CalibrationCurveControl>(PointsProperty, LiveRawYProperty, SelectedChannelProperty);
    }

    // ── Events ─────────────────────────────────────────────────────────────────

    /// <summary>Left-click on empty area. Args: (rawY, channelValue for active channel).</summary>
    public event Action<double, double>? GraphPointAddRequested;

    /// <summary>Right-click on a point. Arg: rawY of the point.</summary>
    public event Action<double>? GraphPointRemoveRequested;

    /// <summary>Point drag. Args: (previousRawY, newRawY, newChannelValue).</summary>
    public event Action<double, double, double>? GraphPointMoved;

    // ── Drag state ─────────────────────────────────────────────────────────────

    private bool   _isDragging;
    private double _dragRawY;

    // Cached from the last Render call so mouse handlers can use the same coordinate space.
    private double _cachedXMax  = 100.0;
    private double _cachedPlotW = 1.0;
    private double _cachedPlotH = 1.0;

    // ── Layout constants ───────────────────────────────────────────────────────

    private const double PadLeft   = 44;
    private const double PadRight  = 12;
    private const double PadTop    = 10;
    private const double PadBottom = 28;

    // ── Channel visual config ──────────────────────────────────────────────────

    private static readonly (CalibrationChannel Ch, Color Color)[] ChannelPalette =
    [
        (CalibrationChannel.Brightness, Color.FromRgb(0xFF, 0xD7, 0x40)), // amber
        (CalibrationChannel.R,          Color.FromRgb(0xEF, 0x53, 0x50)), // red
        (CalibrationChannel.G,          Color.FromRgb(0x66, 0xBB, 0x6A)), // green
        (CalibrationChannel.B,          Color.FromRgb(0x42, 0xA5, 0xF5)), // blue
        (CalibrationChannel.L,          Color.FromRgb(0xEE, 0xEE, 0xEE)), // light gray
    ];

    private static Color GetChannelColor(CalibrationChannel ch) =>
        Array.Find(ChannelPalette, e => e.Ch == ch).Color;

    // ── Render ────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var plotW = w - PadLeft - PadRight;
        var plotH = h - PadTop  - PadBottom;
        if (plotW <= 0 || plotH <= 0) return;

        var sorted  = (Points ?? []).OrderBy(p => p.RawY).ToList();
        var xMax    = sorted.Count > 0 ? Math.Max(sorted[^1].RawY * 1.1, 100.0) : 100.0;
        var ch      = SelectedChannel;

        // Cache for mouse handlers.
        _cachedXMax  = xMax;
        _cachedPlotW = plotW;
        _cachedPlotH = plotH;

        // ── Background ────────────────────────────────────────────────────────
        ctx.FillRectangle(
            new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            new Rect(PadLeft, PadTop, plotW, plotH)
        );

        // ── Grid + Y-axis labels (reflect active channel scale) ───────────────
        var gridPen       = new Pen(new SolidColorBrush(Color.FromArgb(40, 200, 200, 200)), 1);
        var labelBrush    = new SolidColorBrush(Color.FromArgb(160, 200, 200, 200));
        var labelTypeface = new Typeface("Monospace");
        const double labelSize = 10;

        if (ch == CalibrationChannel.Brightness)
        {
            foreach (var pct in new[] { 0, 25, 50, 75, 100 })
            {
                var py  = PadTop + plotH * (1.0 - pct / 100.0);
                ctx.DrawLine(gridPen, new Point(PadLeft, py), new Point(PadLeft + plotW, py));
                var lbl = MakeText($"{pct}%", labelTypeface, labelSize, labelBrush);
                ctx.DrawText(lbl, new Point(PadLeft - lbl.Width - 4, py - lbl.Height / 2));
            }
        }
        else
        {
            foreach (var val in new[] { -1.0, -0.5, 0.0, 0.5, 1.0 })
            {
                var py  = PadTop + plotH * (1.0 - (val + 1.0) / 2.0);
                ctx.DrawLine(gridPen, new Point(PadLeft, py), new Point(PadLeft + plotW, py));
                var lbl = MakeText($"{val:+0.0;-0.0; 0.0}", labelTypeface, labelSize, labelBrush);
                ctx.DrawText(lbl, new Point(PadLeft - lbl.Width - 4, py - lbl.Height / 2));
            }
        }

        // X-axis: 5 ticks
        for (var i = 0; i <= 5; i++)
        {
            var rawY     = xMax * i / 5.0;
            var px       = PadLeft + plotW * (rawY / xMax);
            ctx.DrawLine(gridPen, new Point(px, PadTop), new Point(px, PadTop + plotH));
            var labelStr = rawY >= 10 ? $"{rawY:F0}" : $"{rawY:F1}";
            var lbl      = MakeText(labelStr, labelTypeface, labelSize, labelBrush);
            ctx.DrawText(lbl, new Point(px - lbl.Width / 2, PadTop + plotH + 4));
        }

        // ── All channel curves ────────────────────────────────────────────────
        if (sorted.Count >= 2)
        {
            foreach (var (channel, color) in ChannelPalette)
            {
                var isActive  = channel == ch;
                var alpha     = (byte)(isActive ? 210 : 55);
                var thickness = isActive ? 2.5 : 1.0;
                var pen       = new Pen(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), thickness);

                for (var i = 0; i < sorted.Count - 1; i++)
                {
                    ctx.DrawLine(pen,
                        ToScreen(sorted[i].RawY,     ChannelValue(sorted[i],     channel), xMax, plotW, plotH, channel),
                        ToScreen(sorted[i + 1].RawY, ChannelValue(sorted[i + 1], channel), xMax, plotW, plotH, channel)
                    );
                }

                // Flat-line extensions outside the defined range.
                var first = sorted[0];
                var last  = sorted[^1];
                if (first.RawY > 0)
                    ctx.DrawLine(pen,
                        ToScreen(0,           ChannelValue(first, channel), xMax, plotW, plotH, channel),
                        ToScreen(first.RawY,  ChannelValue(first, channel), xMax, plotW, plotH, channel));
                if (last.RawY < xMax)
                    ctx.DrawLine(pen,
                        ToScreen(last.RawY, ChannelValue(last, channel), xMax, plotW, plotH, channel),
                        ToScreen(xMax,      ChannelValue(last, channel), xMax, plotW, plotH, channel));
            }
        }

        // ── Active-channel point markers ──────────────────────────────────────
        var markerColor = GetChannelColor(ch);
        var markerPen   = new Pen(new SolidColorBrush(markerColor), 1.5);
        foreach (var pt in sorted)
        {
            var center = ToScreen(pt.RawY, ChannelValue(pt, ch), xMax, plotW, plotH, ch);
            ctx.DrawEllipse(Brushes.White, markerPen, center, 5, 5);
        }

        // ── Live sensor dot ───────────────────────────────────────────────────
        if (LiveRawY > 0 && sorted.Count >= 2)
        {
            var liveVal    = Interpolate(LiveRawY, sorted, ch);
            var liveCenter = ToScreen(LiveRawY, liveVal, xMax, plotW, plotH, ch);
            ctx.DrawEllipse(Brushes.Orange, new Pen(Brushes.White, 1.5), liveCenter, 6, 6);
        }

        // ── Axis border ───────────────────────────────────────────────────────
        var axisPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 200, 200, 200)), 1);
        ctx.DrawRectangle(null, axisPen, new Rect(PadLeft, PadTop, plotW, plotH));
    }

    // ── Mouse interaction ─────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);
        if (!InPlot(pos)) return;

        var props  = e.GetCurrentPoint(this).Properties;
        var sorted = (Points ?? []).OrderBy(p => p.RawY).ToList();
        var ch     = SelectedChannel;

        if (props.IsLeftButtonPressed)
        {
            var idx = HitTest(pos, sorted, ch);
            if (idx >= 0)
            {
                _isDragging = true;
                _dragRawY   = sorted[idx].RawY;
                e.Pointer.Capture(this);
            }
            else
            {
                var (rawY, value) = FromScreen(pos, ch);
                GraphPointAddRequested?.Invoke(
                    Math.Max(0, rawY),
                    ClampValue(value, ch)
                );
            }
            e.Handled = true;
        }
        else if (props.IsRightButtonPressed)
        {
            var idx = HitTest(pos, sorted, ch);
            if (idx >= 0)
            {
                GraphPointRemoveRequested?.Invoke(sorted[idx].RawY);
                e.Handled = true;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isDragging) return;

        var ch            = SelectedChannel;
        var (rawY, value) = FromScreen(e.GetPosition(this), ch);

        rawY  = Math.Max(0, rawY);
        value = ClampValue(value, ch);

        GraphPointMoved?.Invoke(_dragRawY, rawY, value);
        _dragRawY = rawY;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isDragging) return;
        _isDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ── Coordinate helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Maps (rawY, channelValue) → canvas pixel.
    /// Each channel is normalized independently: Brightness 0-100→0-1, biases −1..+1→0-1.
    /// </summary>
    private static Point ToScreen(
        double rawY, double value,
        double xMax, double plotW, double plotH,
        CalibrationChannel ch
    )
    {
        var px   = PadLeft + plotW * Math.Clamp(rawY / xMax, 0, 1);
        var norm = ch == CalibrationChannel.Brightness ? value / 100.0 : (value + 1.0) / 2.0;
        var py   = PadTop + plotH * (1.0 - Math.Clamp(norm, 0, 1));
        return new Point(px, py);
    }

    /// <summary>Maps canvas pixel → (rawY, channelValue) for the active channel.</summary>
    private (double RawY, double Value) FromScreen(Point pos, CalibrationChannel ch)
    {
        var norm  = 1.0 - (pos.Y - PadTop)  / _cachedPlotH;
        var rawY  = (pos.X - PadLeft) / _cachedPlotW * _cachedXMax;
        var value = ch == CalibrationChannel.Brightness ? norm * 100.0 : norm * 2.0 - 1.0;
        return (rawY, value);
    }

    private bool InPlot(Point pos) =>
        pos.X >= PadLeft && pos.X <= PadLeft + _cachedPlotW &&
        pos.Y >= PadTop  && pos.Y <= PadTop  + _cachedPlotH;

    /// <summary>Returns the sorted-list index of the point nearest the cursor, or -1.</summary>
    private int HitTest(Point cursor, List<UsbCalibrationPoint> sorted, CalibrationChannel ch, double tol = 12.0)
    {
        for (var i = 0; i < sorted.Count; i++)
        {
            var pt     = sorted[i];
            var screen = ToScreen(pt.RawY, ChannelValue(pt, ch), _cachedXMax, _cachedPlotW, _cachedPlotH, ch);
            var dist   = Math.Sqrt(Math.Pow(cursor.X - screen.X, 2) + Math.Pow(cursor.Y - screen.Y, 2));
            if (dist <= tol) return i;
        }
        return -1;
    }

    // ── Channel value helpers ─────────────────────────────────────────────────

    internal static double ChannelValue(UsbCalibrationPoint pt, CalibrationChannel ch) => ch switch
    {
        CalibrationChannel.Brightness => pt.BrightnessPercent,
        CalibrationChannel.R          => pt.RBias,
        CalibrationChannel.G          => pt.GBias,
        CalibrationChannel.B          => pt.BBias,
        CalibrationChannel.L          => pt.LBias,
        _                             => 0,
    };

    private static double ClampValue(double v, CalibrationChannel ch) =>
        ch == CalibrationChannel.Brightness ? Math.Clamp(v, 0, 100) : Math.Clamp(v, -1, 1);

    private static double Interpolate(double rawY, List<UsbCalibrationPoint> sorted, CalibrationChannel ch)
    {
        if (rawY <= sorted[0].RawY)   return ChannelValue(sorted[0],   ch);
        if (rawY >= sorted[^1].RawY)  return ChannelValue(sorted[^1],  ch);
        for (var i = 0; i < sorted.Count - 1; i++)
        {
            if (rawY < sorted[i].RawY || rawY > sorted[i + 1].RawY) continue;
            var t = (rawY - sorted[i].RawY) / (sorted[i + 1].RawY - sorted[i].RawY);
            return ChannelValue(sorted[i], ch) + t * (ChannelValue(sorted[i + 1], ch) - ChannelValue(sorted[i], ch));
        }
        return ChannelValue(sorted[^1], ch);
    }

    private static FormattedText MakeText(string text, Typeface tf, double size, IBrush brush) =>
        new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, size, brush);
}
