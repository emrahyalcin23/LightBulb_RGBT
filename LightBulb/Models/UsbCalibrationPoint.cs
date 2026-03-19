namespace LightBulb.Models;

/// <summary>
/// A single point on the sensor → brightness calibration curve.
/// RawY             : CIE-Y value computed from the sensor (0 … ∞).
/// BrightnessPercent: target screen brightness in % (0–100).
/// RBias/GBias/BBias/LBias: per-point colour bias applied at this ambient level
///                           and linearly interpolated between points (−1 … +1).
/// </summary>
public record UsbCalibrationPoint(
    double RawY,
    double BrightnessPercent,
    double RBias = 0,
    double GBias = 0,
    double BBias = 0,
    double LBias = 0
);
