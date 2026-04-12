using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightBulb.Models;

namespace LightBulb.Services;

/// <summary>
/// Evaluates the four RGBL calibration curves (R, G, B, L) loaded from a
/// rgbl_calibration.json file exported by the RGBL_CurveEditor HTML tool.
///
/// The "L" channel in the JSON is the pre-computed LT = L(T(x)) composition
/// from the editor (the purple dashed curve). T is never stored separately.
///
/// Pipeline for a given ambient light percentage (0-100):
///   ltVal = L(ambientPct)          — brightness multiplier (pre-computed LT)
///   fR    = R(ambientPct)          — raw red contribution
///   fG    = G(ambientPct)          — raw green contribution
///   fB    = B(ambientPct)          — raw blue contribution
///
/// calcMode = "absolute":  finalX = fX * ltVal / 100
/// calcMode = "ratio":     normalise fR/fG/fB by their sum, then scale by ltVal
/// </summary>
public sealed class RgblCurveEvaluator
{
    private readonly IReadOnlyList<CurvePoint> _r;
    private readonly IReadOnlyList<CurvePoint> _g;
    private readonly IReadOnlyList<CurvePoint> _b;
    private readonly IReadOnlyList<CurvePoint> _l;   // pre-computed LT from editor
    private readonly bool _isRatio;

    private RgblCurveEvaluator(RgblCalibration cal)
    {
        _r = SortedPoints(cal.Channels, "R");
        _g = SortedPoints(cal.Channels, "G");
        _b = SortedPoints(cal.Channels, "B");
        _l = SortedPoints(cal.Channels, "L");
        _isRatio = string.Equals(cal.CalcMode, "ratio", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Loads and constructs an evaluator from the given JSON file path.
    /// Returns null when the path is empty, the file does not exist, or parsing fails.
    /// </summary>
    public static RgblCurveEvaluator? Load(string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
            return null;

        try
        {
            var json = File.ReadAllText(jsonPath);
            var cal = JsonSerializer.Deserialize(json, RgblSerializerContext.Default.RgblCalibration);
            return cal is not null ? new RgblCurveEvaluator(cal) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Evaluates the RGBL pipeline for the given ambient light percentage.
    /// Returns (finalR, finalG, finalB, ltVal) each in the 0-100 range.
    /// ltVal is the pre-computed LT brightness multiplier at this ambient level.
    /// </summary>
    public (double R, double G, double B, double L) Evaluate(double ambientPct)
    {
        var ltVal = SampleAtX(_l, ambientPct);   // L = pre-computed LT
        var fR    = SampleAtX(_r, ambientPct);
        var fG    = SampleAtX(_g, ambientPct);
        var fB    = SampleAtX(_b, ambientPct);

        if (!_isRatio)
            return (fR * ltVal / 100.0, fG * ltVal / 100.0, fB * ltVal / 100.0, ltVal);

        var sum = fR + fG + fB;
        if (sum > 0.001)
            return (fR / sum * ltVal, fG / sum * ltVal, fB / sum * ltVal, ltVal);

        return (ltVal / 3.0, ltVal / 3.0, ltVal / 3.0, ltVal);
    }

    /// <summary>
    /// Piecewise linear interpolation — mirrors the JS sampleAtX function in d3-editor.js.
    /// Points must be pre-sorted by X ascending.
    /// </summary>
    private static double SampleAtX(IReadOnlyList<CurvePoint> points, double x)
    {
        if (points.Count < 2)
            return Math.Clamp(x, 0, 100);

        x = Math.Clamp(x, 0, 100);

        if (x <= points[0].X)
            return Math.Clamp(points[0].Y, 0, 100);
        if (x >= points[^1].X)
            return Math.Clamp(points[^1].Y, 0, 100);

        for (var i = 0; i < points.Count - 1; i++)
        {
            var p1 = points[i];
            var p2 = points[i + 1];
            if (x >= p1.X && x <= p2.X)
            {
                var t = (x - p1.X) / (p2.X - p1.X);
                return Math.Clamp(p1.Y + t * (p2.Y - p1.Y), 0, 100);
            }
        }

        return Math.Clamp(points[^1].Y, 0, 100);
    }

    private static IReadOnlyList<CurvePoint> SortedPoints(
        Dictionary<string, List<CurvePoint>> channels,
        string key
    )
    {
        if (!channels.TryGetValue(key, out var pts) || pts is null)
            return [];
        return pts.OrderBy(p => p.X).ToList();
    }

}

// Source-generated JSON context for trim-safe deserialization of RGBL calibration.
// Must be a top-level partial type — source generator requires the context and all
// containing types to be partial, so it cannot be nested inside a non-partial class.
[JsonSerializable(typeof(RgblCalibration))]
[JsonSerializable(typeof(CurvePoint))]
[JsonSerializable(typeof(List<CurvePoint>))]
[JsonSerializable(typeof(Dictionary<string, List<CurvePoint>>))]
internal partial class RgblSerializerContext : JsonSerializerContext;
