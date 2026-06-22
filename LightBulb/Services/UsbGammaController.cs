using System;
using LightBulb.Core;
using LightBulb.Utils;
using LightBulb.Utils.Extensions;

namespace LightBulb.Services;

/// <summary>
/// Bridges UsbSensorService → GammaService.
/// Owns the gamma-invalidation subscriptions and the "which SetGamma to call" logic,
/// keeping DashboardViewModel free of direct USB sensor knowledge.
/// </summary>
public class UsbGammaController : IDisposable
{
    private readonly UsbSensorService _sensor;
    private readonly GammaService _gamma;
    private readonly SettingsService _settings;
    private readonly DisposableCollector _eventRoot = new();

    public UsbGammaController(
        UsbSensorService sensor,
        GammaService gamma,
        SettingsService settings
    )
    {
        _sensor = sensor;
        _gamma = gamma;
        _settings = settings;

        _eventRoot.Add(
            settings.WatchProperties(
                [o => o.IsUsbSensorEnabled],
                _gamma.InvalidateGamma
            )
        );

        _eventRoot.Add(
            sensor.WatchProperties(
                [
                    o => o.LatestCct,
                    o => o.LatestLuminance,
                    o => o.LatestRgblR,
                    o => o.LatestRgblG,
                    o => o.LatestRgblB,
                    o => o.HasReceivedValidData,
                    o => o.GammaApplyBlocked,
                    o => o.IsGammaPreviewActive,
                    o => o.GammaPreviewR,
                    o => o.GammaPreviewG,
                    o => o.GammaPreviewB,
                    o => o.GammaPreviewL,
                ],
                _gamma.InvalidateGamma
            )
        );
    }

    public bool IsUsbActive =>
        _settings.IsUsbSensorEnabled
        && _sensor.HasReceivedValidData
        && !_sensor.GammaApplyBlocked;

    /// <summary>
    /// Returns a USB-sensor-derived ColorConfiguration when USB is active,
    /// otherwise returns the supplied cycle target unchanged.
    /// </summary>
    public ColorConfiguration GetEffectiveTarget(ColorConfiguration cycleTarget)
    {
        if (!IsUsbActive)
            return cycleTarget;

        return new ColorConfiguration(
            Math.Clamp(
                _sensor.LatestCct,
                _settings.MinimumTemperature,
                _settings.MaximumTemperature
            ),
            Math.Clamp(
                _sensor.LatestLuminance,
                _settings.MinimumBrightness,
                _settings.MaximumBrightness
            )
        );
    }

    /// <summary>
    /// Applies the appropriate gamma based on USB sensor state.
    /// When USB is active and RGBL calibration is running, uses direct RGBL values.
    /// When USB is active without RGBL, applies bias-adjusted SetGamma.
    /// When USB is inactive, falls back to SetGamma(current).
    /// </summary>
    public void ApplyGamma(
        ColorConfiguration current,
        double cycleBrightness,
        double brightnessOffset
    )
    {
        if (IsUsbActive)
        {
            if (_sensor.IsGammaPreviewActive)
            {
                var dayB = Math.Clamp(
                    cycleBrightness + brightnessOffset,
                    _settings.MinimumBrightness,
                    2.0
                );
                var (fR, fG, fB) = RgblPipeline.MaxNormalize(
                    _sensor.GammaPreviewR,
                    _sensor.GammaPreviewG,
                    _sensor.GammaPreviewB,
                    _sensor.GammaPreviewL
                );
                _gamma.SetGammaRgbl(fR * dayB, fG * dayB, fB * dayB);
            }
            else if (_sensor.IsRgblCalibrationActive)
            {
                var dayB = Math.Clamp(
                    cycleBrightness + brightnessOffset,
                    _settings.MinimumBrightness,
                    2.0
                );
                _gamma.SetGammaRgbl(
                    _sensor.LatestRgblR * dayB,
                    _sensor.LatestRgblG * dayB,
                    _sensor.LatestRgblB * dayB
                );
            }
            else
            {
                _gamma.SetGammaWithBias(
                    current,
                    _sensor.LatestRBias,
                    _sensor.LatestGBias,
                    _sensor.LatestBBias,
                    _sensor.LatestLBias
                );
            }
        }
        else
        {
            _gamma.SetGamma(current);
        }
    }

    public void Dispose() => _eventRoot.Dispose();
}
