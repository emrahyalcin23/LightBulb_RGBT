using System;
using System.Diagnostics;
using LightBulb.Core;

namespace LightBulb.Services;

// RGBL-specific gamma extensions — isolated from the base GammaService
// so that upstream merges don't conflict with this feature set.
public partial class GammaService
{
    // Last RGBL curve values applied; -1 forces the first call to always write.
    private double _lastRgblR = -1, _lastRgblG = -1, _lastRgblB = -1;

    // Last values sent to SetGammaRgbl (0-100 range)
    public double LastScreenR => _lastRgblR < 0 ? 0 : _lastRgblR;
    public double LastScreenG => _lastRgblG < 0 ? 0 : _lastRgblG;
    public double LastScreenB => _lastRgblB < 0 ? 0 : _lastRgblB;

    /// <summary>
    /// Applies gamma with optional per-channel RGBL bias coefficients.
    /// Each bias is in [-1.0, +1.0]: 0 = no change, +1.0 = channel doubled (clamped),
    /// -1.0 = channel zeroed. L bias scales effective brightness before channel math.
    /// </summary>
    public void SetGammaWithBias(
        ColorConfiguration configuration,
        double rBias,
        double gBias,
        double bBias,
        double lBias
    )
    {
        if (!IsGammaStale() && !IsSignificantChange(configuration))
            return;

        EnsureValidDeviceContexts();
        _isUpdatingGamma = true;

        var effectiveBrightness = configuration.Brightness * (1.0 + lBias);

        foreach (var deviceContext in _deviceContexts)
        {
            deviceContext.SetGamma(
                Math.Clamp(GetRed(configuration) * effectiveBrightness * (1.0 + rBias), 0, 1),
                Math.Clamp(GetGreen(configuration) * effectiveBrightness * (1.0 + gBias), 0, 1),
                Math.Clamp(GetBlue(configuration) * effectiveBrightness * (1.0 + bBias), 0, 1)
            );
        }

        _isUpdatingGamma = false;
        _lastConfiguration = configuration;
        _lastUpdateTimestamp = DateTimeOffset.Now;
        Debug.WriteLine(
            $"Updated gamma to {configuration} (rBias={rBias:F2}, gBias={gBias:F2}, bBias={bBias:F2}, lBias={lBias:F2})."
        );
    }

    /// <summary>
    /// Applies gamma directly from RGBL curve output values (0-100 percent).
    /// Bypasses Kelvin→RGB conversion. Clears _lastConfiguration so that
    /// switching back to normal mode forces a re-apply.
    /// </summary>
    public void SetGammaRgbl(double r, double g, double b)
    {
        if (!IsGammaStale()
            && Math.Abs(r - _lastRgblR) < 0.1
            && Math.Abs(g - _lastRgblG) < 0.1
            && Math.Abs(b - _lastRgblB) < 0.1)
            return;

        EnsureValidDeviceContexts();
        _isUpdatingGamma = true;

        foreach (var deviceContext in _deviceContexts)
        {
            deviceContext.SetGamma(
                Math.Clamp(r / 100.0, 0, 1),
                Math.Clamp(g / 100.0, 0, 1),
                Math.Clamp(b / 100.0, 0, 1)
            );
        }

        _isUpdatingGamma = false;
        _lastRgblR = r;
        _lastRgblG = g;
        _lastRgblB = b;
        _lastConfiguration = null;
        _lastUpdateTimestamp = DateTimeOffset.Now;
        Debug.WriteLine($"RGBL gamma: R={r:F1} G={g:F1} B={b:F1}");
    }
}
