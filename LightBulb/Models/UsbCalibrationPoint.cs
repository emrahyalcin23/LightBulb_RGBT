namespace LightBulb.Models;

/// <summary>
/// A single point on the sensor → brightness calibration curve.
/// RawY  : CIE-Y value computed from the sensor (0 … ∞).
/// BrightnessPercent : target screen brightness in % (0–100).
/// </summary>
public record UsbCalibrationPoint(double RawY, double BrightnessPercent);
