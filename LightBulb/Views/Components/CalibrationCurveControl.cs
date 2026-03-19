using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LightBulb.Models;

namespace LightBulb.Views.Components;

/// <summary>
/// Custom Avalonia control that draws the sensor-rawY → brightness calibration curve.
///
/// X axis : sensor CIE-Y (rawY) from 0 to max of calibration points.
/// Y axis : brightness 0–100 %.
///
/// Drawn elements
/// ──────────────
///   • background fill
///   • grid lines + axis labels
///   • piecewise linear calibration curve (blue)
///   • calibration point markers (white filled circles)
///   • live sensor dot (orange) that follows the current rawY
/// </summary>
public class CalibrationCurveControl : Control
{
    // ── Avalonia styled properties ─────────────────────────────────────────────

    public static readonly StyledProperty<IReadOnlyList<UsbCalibrationPoint>?> PointsProperty =
        AvaloniaProperty.Register<CalibrationCurveControl, IReadOnlyList<UsbCalibrationPoint>?>(
            nameof(Points)
        );

    public static readonly StyledProperty<double> LiveRawYProperty =
        AvaloniaProperty.Register<CalibrationCurveControl, double>(nameof(LiveRawY), 0.0);

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

    // Trigger a re-render whenever bound properties change.
    static CalibrationCurveControl()
    {
        PointsProperty.Changed.AddClassHandler<CalibrationCurveControl>(
            (c, _) => c.InvalidateVisual()
        );
        LiveRawYProperty.Changed.AddClassHandler<CalibrationCurveControl>(
            (c, _) => c.InvalidateVisual()
        );
        AffectsRender<CalibrationCurveControl>(PointsProperty, LiveRawYProperty);
    }

    // ── Layout constants ───────────────────────────────────────────────────────

    private const double PadLeft = 44;   // room for Y labels
    private const double PadRight = 12;
    private const double PadTop = 10;
    private const double PadBottom = 28; // room for X labels

    // ── Render ────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;

        var plotW = w - PadLeft - PadRight;
        var plotH = h - PadTop - PadBottom;

        if (plotW <= 0 || plotH <= 0)
            return;

        var sorted = (Points ?? []).OrderBy(p => p.RawY).ToList();

        // Axis range: x goes from 0 to at least the max defined rawY (min 100).
        var xMax = sorted.Count > 0 ? Math.Max(sorted[^1].RawY * 1.1, 100.0) : 100.0;

        // ── Background ────────────────────────────────────────────────────────

        ctx.FillRectangle(
            new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            new Rect(PadLeft, PadTop, plotW, plotH)
        );

        // ── Grid lines + axis labels ──────────────────────────────────────────

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 200, 200, 200)), 1);
        var labelBrush = new SolidColorBrush(Color.FromArgb(160, 200, 200, 200));
        var labelTypeface = new Typeface("Monospace");
        const double labelSize = 10;

        // Y axis: 0%, 25%, 50%, 75%, 100%
        foreach (var pct in new[] { 0, 25, 50, 75, 100 })
        {
            var py = PadTop + plotH * (1.0 - pct / 100.0);
            ctx.DrawLine(gridPen, new Point(PadLeft, py), new Point(PadLeft + plotW, py));

            var label = FormattedText($"{pct}%", labelTypeface, labelSize, labelBrush);
            ctx.DrawText(label, new Point(PadLeft - label.Width - 4, py - label.Height / 2));
        }

        // X axis: 5 ticks
        for (var i = 0; i <= 5; i++)
        {
            var rawY = xMax * i / 5.0;
            var px = PadLeft + plotW * (rawY / xMax);
            ctx.DrawLine(gridPen, new Point(px, PadTop), new Point(px, PadTop + plotH));

            var labelStr = rawY >= 10 ? $"{rawY:F0}" : $"{rawY:F1}";
            var label = FormattedText(labelStr, labelTypeface, labelSize, labelBrush);
            ctx.DrawText(label, new Point(px - label.Width / 2, PadTop + plotH + 4));
        }

        // ── Calibration curve ─────────────────────────────────────────────────

        if (sorted.Count >= 2)
        {
            var curvePen = new Pen(Brushes.DodgerBlue, 2);

            for (var i = 0; i < sorted.Count - 1; i++)
            {
                var a = sorted[i];
                var b = sorted[i + 1];
                ctx.DrawLine(
                    curvePen,
                    ToPlot(a.RawY, a.BrightnessPercent, xMax, plotW, plotH),
                    ToPlot(b.RawY, b.BrightnessPercent, xMax, plotW, plotH)
                );
            }

            // Extend flat lines beyond the defined range.
            var first = sorted[0];
            var last = sorted[^1];

            // Left extension (flat at first point brightness)
            if (first.RawY > 0)
                ctx.DrawLine(
                    curvePen,
                    ToPlot(0, first.BrightnessPercent, xMax, plotW, plotH),
                    ToPlot(first.RawY, first.BrightnessPercent, xMax, plotW, plotH)
                );

            // Right extension (flat at last point brightness)
            if (last.RawY < xMax)
                ctx.DrawLine(
                    curvePen,
                    ToPlot(last.RawY, last.BrightnessPercent, xMax, plotW, plotH),
                    ToPlot(xMax, last.BrightnessPercent, xMax, plotW, plotH)
                );
        }

        // ── Calibration point markers ─────────────────────────────────────────

        var markerPen = new Pen(Brushes.DodgerBlue, 1.5);

        foreach (var pt in sorted)
        {
            var center = ToPlot(pt.RawY, pt.BrightnessPercent, xMax, plotW, plotH);
            ctx.DrawEllipse(Brushes.White, markerPen, center, 5, 5);
        }

        // ── Live sensor dot ───────────────────────────────────────────────────

        if (LiveRawY > 0)
        {
            // Interpolate brightness from the calibration curve.
            var livePct = sorted.Count >= 2
                ? InterpolatePct(LiveRawY, sorted)
                : 50.0;

            var liveCenter = ToPlot(LiveRawY, livePct, xMax, plotW, plotH);
            ctx.DrawEllipse(Brushes.Orange, new Pen(Brushes.White, 1.5), liveCenter, 6, 6);
        }

        // ── Axis border ───────────────────────────────────────────────────────

        var axisPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 200, 200, 200)), 1);
        ctx.DrawRectangle(null, axisPen, new Rect(PadLeft, PadTop, plotW, plotH));
    }

    // ── Coordinate helpers ────────────────────────────────────────────────────

    private Point ToPlot(double rawY, double pct, double xMax, double plotW, double plotH)
    {
        var px = PadLeft + plotW * Math.Clamp(rawY / xMax, 0, 1);
        var py = PadTop + plotH * (1.0 - Math.Clamp(pct / 100.0, 0, 1));
        return new Point(px, py);
    }

    private static double InterpolatePct(double rawY, List<UsbCalibrationPoint> sorted)
    {
        if (rawY <= sorted[0].RawY) return sorted[0].BrightnessPercent;
        if (rawY >= sorted[^1].RawY) return sorted[^1].BrightnessPercent;

        for (var i = 0; i < sorted.Count - 1; i++)
        {
            var lo = sorted[i];
            var hi = sorted[i + 1];
            if (rawY < lo.RawY || rawY > hi.RawY) continue;
            var t = (rawY - lo.RawY) / (hi.RawY - lo.RawY);
            return lo.BrightnessPercent + t * (hi.BrightnessPercent - lo.BrightnessPercent);
        }

        return sorted[^1].BrightnessPercent;
    }

    private static FormattedText FormattedText(
        string text,
        Typeface typeface,
        double size,
        IBrush brush
    ) => new(text, System.Globalization.CultureInfo.CurrentCulture,
             FlowDirection.LeftToRight, typeface, size, brush);
}
