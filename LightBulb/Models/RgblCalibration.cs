using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LightBulb.Models;

/// <summary>
/// A single control point on an RGBL calibration curve.
/// X and Y are both in the 0-100 range (percent).
/// </summary>
public record CurvePoint(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("fixed")] bool Fixed
);

/// <summary>
/// Top-level model for rgbl_calibration.json exported by the RGBL_CurveEditor HTML tool.
/// Channels: R, G, B, L, T — each a list of CurvePoints sorted by X.
/// CalcMode: "absolute" or "ratio".
/// </summary>
public record RgblCalibration(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("calcMode")] string CalcMode,
    [property: JsonPropertyName("channels")] Dictionary<string, List<CurvePoint>> Channels
);
