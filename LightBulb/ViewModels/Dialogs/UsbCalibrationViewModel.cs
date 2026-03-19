using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightBulb.Framework;
using LightBulb.Models;
using LightBulb.Services;
using LightBulb.Utils;
using LightBulb.Utils.Extensions;

namespace LightBulb.ViewModels.Dialogs;

/// <summary>
/// ViewModel for a single row in the calibration points table.
/// Notifies its parent whenever any property changes.
/// </summary>
public partial class CalibrationPointViewModel : ObservableObject
{
    private readonly Action _onChanged;

    [ObservableProperty]
    public partial double RawY { get; set; }

    [ObservableProperty]
    public partial double BrightnessPercent { get; set; }

    [ObservableProperty]
    public partial double RBias { get; set; }

    [ObservableProperty]
    public partial double GBias { get; set; }

    [ObservableProperty]
    public partial double BBias { get; set; }

    [ObservableProperty]
    public partial double LBias { get; set; }

    public CalibrationPointViewModel(
        double rawY,
        double brightnessPercent,
        double rBias,
        double gBias,
        double bBias,
        double lBias,
        Action onChanged
    )
    {
        _onChanged = onChanged;
        RawY = rawY;
        BrightnessPercent = brightnessPercent;
        RBias = rBias;
        GBias = gBias;
        BBias = bBias;
        LBias = lBias;
    }

    partial void OnRawYChanged(double value)             => _onChanged();
    partial void OnBrightnessPercentChanged(double value) => _onChanged();
    partial void OnRBiasChanged(double value)             => _onChanged();
    partial void OnGBiasChanged(double value)             => _onChanged();
    partial void OnBBiasChanged(double value)             => _onChanged();
    partial void OnLBiasChanged(double value)             => _onChanged();
}

/// <summary>
/// ViewModel for the USB calibration window.
/// </summary>
public partial class UsbCalibrationViewModel : DialogViewModelBase
{
    private readonly SettingsService   _settingsService;
    private readonly UsbSensorService  _usbSensorService;
    private readonly DisposableCollector _eventRoot = new();

    // ── Calibration points ────────────────────────────────────────────────────

    public ObservableCollection<CalibrationPointViewModel> CalibrationPoints { get; } = new();

    /// <summary>Bound to CalibrationCurveControl.Points. Updated via SyncToSettings().</summary>
    public IReadOnlyList<UsbCalibrationPoint>? CalibrationCurvePoints =>
        _settingsService.UsbCalibrationPoints;

    // ── Active channel (which curve is shown / editable in the graph) ─────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChannelBrightness))]
    [NotifyPropertyChangedFor(nameof(IsChannelR))]
    [NotifyPropertyChangedFor(nameof(IsChannelG))]
    [NotifyPropertyChangedFor(nameof(IsChannelB))]
    [NotifyPropertyChangedFor(nameof(IsChannelL))]
    public partial CalibrationChannel SelectedChannel { get; set; } = CalibrationChannel.Brightness;

    public bool IsChannelBrightness
    {
        get => SelectedChannel == CalibrationChannel.Brightness;
        set { if (value) SelectedChannel = CalibrationChannel.Brightness; }
    }
    public bool IsChannelR
    {
        get => SelectedChannel == CalibrationChannel.R;
        set { if (value) SelectedChannel = CalibrationChannel.R; }
    }
    public bool IsChannelG
    {
        get => SelectedChannel == CalibrationChannel.G;
        set { if (value) SelectedChannel = CalibrationChannel.G; }
    }
    public bool IsChannelB
    {
        get => SelectedChannel == CalibrationChannel.B;
        set { if (value) SelectedChannel = CalibrationChannel.B; }
    }
    public bool IsChannelL
    {
        get => SelectedChannel == CalibrationChannel.L;
        set { if (value) SelectedChannel = CalibrationChannel.L; }
    }

    // ── Live sensor pass-through ──────────────────────────────────────────────

    public string LastRawReading  => _usbSensorService.LastRawReading;
    public double LatestRawY      => _usbSensorService.LatestRawY;
    public double LatestLuminance => _usbSensorService.LatestLuminance;
    public string LatestCctText   => $"{_usbSensorService.LatestCct:F0} K";

    // ── Calibration enabled toggle ────────────────────────────────────────────

    public bool IsCalibrationEnabled
    {
        get => _settingsService.IsUsbCalibrationEnabled;
        set => _settingsService.IsUsbCalibrationEnabled = value;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public IRelayCommand AddPointCommand   { get; }
    public IRelayCommand<CalibrationPointViewModel> RemovePointCommand  { get; }
    public IRelayCommand<CalibrationPointViewModel> CaptureCurrentCommand { get; }
    public IRelayCommand ResetToDefaultCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public UsbCalibrationViewModel(
        SettingsService settingsService,
        UsbSensorService usbSensorService
    )
    {
        _settingsService  = settingsService;
        _usbSensorService = usbSensorService;

        LoadPointsFromSettings();

        _eventRoot.Add(_usbSensorService.WatchAllProperties(() => OnAllPropertiesChanged()));
        _eventRoot.Add(_settingsService.WatchAllProperties(() => OnAllPropertiesChanged()));

        AddPointCommand = new RelayCommand(AddPoint);

        RemovePointCommand = new RelayCommand<CalibrationPointViewModel>(
            pt => { if (pt is not null) RemovePoint(pt); }
        );

        CaptureCurrentCommand = new RelayCommand<CalibrationPointViewModel>(
            pt => { if (pt is not null) pt.RawY = Math.Round(_usbSensorService.LatestRawY, 2); },
            _  => _usbSensorService.LatestRawY > 0
        );

        ResetToDefaultCommand = new RelayCommand(ResetToDefault);
    }

    // ── Graph event handlers (called from code-behind) ────────────────────────

    /// <summary>
    /// User left-clicked on empty graph space.
    /// Creates a new point at (rawY, channelValue) for the active channel;
    /// other channels are interpolated from the existing curve.
    /// </summary>
    public void AddPointFromGraph(double rawY, double channelValue)
    {
        rawY = Math.Round(rawY, 2);

        // If a point already exists at this X (within 0.5 units) just update its value.
        var existing = CalibrationPoints
            .OrderBy(p => Math.Abs(p.RawY - rawY))
            .FirstOrDefault();

        if (existing != null && Math.Abs(existing.RawY - rawY) < 0.5)
        {
            SetChannelValue(existing, channelValue);
            return; // _onChanged already called SyncToSettings
        }

        // New point: interpolate all other channels from the existing curve.
        var brightness = SelectedChannel == CalibrationChannel.Brightness
            ? channelValue
            : InterpolateChannelAt(rawY, CalibrationChannel.Brightness);

        var rBias = SelectedChannel == CalibrationChannel.R
            ? channelValue
            : InterpolateChannelAt(rawY, CalibrationChannel.R);

        var gBias = SelectedChannel == CalibrationChannel.G
            ? channelValue
            : InterpolateChannelAt(rawY, CalibrationChannel.G);

        var bBias = SelectedChannel == CalibrationChannel.B
            ? channelValue
            : InterpolateChannelAt(rawY, CalibrationChannel.B);

        var lBias = SelectedChannel == CalibrationChannel.L
            ? channelValue
            : InterpolateChannelAt(rawY, CalibrationChannel.L);

        CalibrationPoints.Add(MakeRow(rawY, brightness, rBias, gBias, bBias, lBias));
        SyncToSettings();
    }

    /// <summary>
    /// User right-clicked on a point. Removes the point whose RawY is closest to rawY.
    /// </summary>
    public void RemovePointFromGraph(double rawY)
    {
        var pt = CalibrationPoints.OrderBy(p => Math.Abs(p.RawY - rawY)).FirstOrDefault();
        if (pt is not null) RemovePoint(pt);
    }

    /// <summary>
    /// User dragged a point. Updates the point that previously had oldRawY.
    /// </summary>
    public void MovePointFromGraph(double oldRawY, double newRawY, double channelValue)
    {
        var pt = CalibrationPoints.OrderBy(p => Math.Abs(p.RawY - oldRawY)).FirstOrDefault();
        if (pt is null) return;

        // Update RawY and the active channel value.
        // Suppress double-SyncToSettings: change RawY first (triggers sync), then value.
        pt.RawY = Math.Round(newRawY, 2);
        SetChannelValue(pt, channelValue);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void LoadPointsFromSettings()
    {
        CalibrationPoints.Clear();
        var persisted = _settingsService.UsbCalibrationPoints;
        if (persisted is { Count: > 0 })
        {
            foreach (var p in persisted.OrderBy(p => p.RawY))
                CalibrationPoints.Add(MakeRow(p.RawY, p.BrightnessPercent, p.RBias, p.GBias, p.BBias, p.LBias));
        }
        else
        {
            CalibrationPoints.Add(MakeRow(1.0,   10.0,  0, 0, 0, 0));
            CalibrationPoints.Add(MakeRow(100.0, 100.0, 0, 0, 0, 0));
            SyncToSettings();
        }
    }

    private CalibrationPointViewModel MakeRow(
        double rawY, double pct,
        double rBias, double gBias, double bBias, double lBias
    ) => new(rawY, pct, rBias, gBias, bBias, lBias, SyncToSettings);

    private void SyncToSettings()
    {
        _settingsService.UsbCalibrationPoints = CalibrationPoints
            .OrderBy(p => p.RawY)
            .Select(p => new UsbCalibrationPoint(
                p.RawY,
                Math.Clamp(p.BrightnessPercent, 0, 100),
                Math.Clamp(p.RBias, -1, 1),
                Math.Clamp(p.GBias, -1, 1),
                Math.Clamp(p.BBias, -1, 1),
                Math.Clamp(p.LBias, -1, 1)
            ))
            .ToList();

        OnPropertyChanged(nameof(CalibrationCurvePoints));
    }

    private void AddPoint()
    {
        var maxRawY = CalibrationPoints.Count > 0 ? CalibrationPoints.Max(p => p.RawY) : 100.0;
        CalibrationPoints.Add(MakeRow(Math.Round(maxRawY * 1.5, 2), 100.0, 0, 0, 0, 0));
        SyncToSettings();
    }

    private void RemovePoint(CalibrationPointViewModel pt)
    {
        CalibrationPoints.Remove(pt);
        SyncToSettings();
    }

    private void ResetToDefault()
    {
        CalibrationPoints.Clear();
        CalibrationPoints.Add(MakeRow(1.0,   10.0,  0, 0, 0, 0));
        CalibrationPoints.Add(MakeRow(100.0, 100.0, 0, 0, 0, 0));
        SyncToSettings();
    }

    private void SetChannelValue(CalibrationPointViewModel pt, double value)
    {
        switch (SelectedChannel)
        {
            case CalibrationChannel.Brightness: pt.BrightnessPercent = Math.Round(value, 1); break;
            case CalibrationChannel.R:          pt.RBias = Math.Round(value, 3); break;
            case CalibrationChannel.G:          pt.GBias = Math.Round(value, 3); break;
            case CalibrationChannel.B:          pt.BBias = Math.Round(value, 3); break;
            case CalibrationChannel.L:          pt.LBias = Math.Round(value, 3); break;
        }
    }

    /// <summary>
    /// Interpolates a channel value at rawY from the existing calibration points.
    /// Falls back to sensible defaults when no points exist yet.
    /// </summary>
    private double InterpolateChannelAt(double rawY, CalibrationChannel ch)
    {
        var sorted = CalibrationPoints.OrderBy(p => p.RawY).ToList();
        if (sorted.Count == 0)
            return ch == CalibrationChannel.Brightness ? 50.0 : 0.0;
        if (sorted.Count == 1)
            return GetChannelValue(sorted[0], ch);

        if (rawY <= sorted[0].RawY)   return GetChannelValue(sorted[0],  ch);
        if (rawY >= sorted[^1].RawY)  return GetChannelValue(sorted[^1], ch);

        for (var i = 0; i < sorted.Count - 1; i++)
        {
            var lo = sorted[i];
            var hi = sorted[i + 1];
            if (rawY < lo.RawY || rawY > hi.RawY) continue;
            var t = (rawY - lo.RawY) / (hi.RawY - lo.RawY);
            return GetChannelValue(lo, ch) + t * (GetChannelValue(hi, ch) - GetChannelValue(lo, ch));
        }

        return GetChannelValue(sorted[^1], ch);
    }

    private static double GetChannelValue(CalibrationPointViewModel pt, CalibrationChannel ch) => ch switch
    {
        CalibrationChannel.Brightness => pt.BrightnessPercent,
        CalibrationChannel.R          => pt.RBias,
        CalibrationChannel.G          => pt.GBias,
        CalibrationChannel.B          => pt.BBias,
        CalibrationChannel.L          => pt.LBias,
        _                             => 0,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _eventRoot.Dispose();
        base.Dispose(disposing);
    }
}
