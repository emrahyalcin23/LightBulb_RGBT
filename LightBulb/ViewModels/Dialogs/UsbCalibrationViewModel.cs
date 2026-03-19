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

    partial void OnRawYChanged(double value) => _onChanged();
    partial void OnBrightnessPercentChanged(double value) => _onChanged();
    partial void OnRBiasChanged(double value) => _onChanged();
    partial void OnGBiasChanged(double value) => _onChanged();
    partial void OnBBiasChanged(double value) => _onChanged();
    partial void OnLBiasChanged(double value) => _onChanged();
}

/// <summary>
/// ViewModel for the USB calibration &amp; RGBL settings window.
/// </summary>
public partial class UsbCalibrationViewModel : DialogViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly UsbSensorService _usbSensorService;
    private readonly DisposableCollector _eventRoot = new();

    // ── Calibration points ────────────────────────────────────────────────────

    public ObservableCollection<CalibrationPointViewModel> CalibrationPoints { get; } = new();

    // ── Calibration curve points (for the graph binding) ─────────────────────

    /// <summary>
    /// Points currently stored in SettingsService — bound to CalibrationCurveControl.
    /// Updated via SyncToSettings() after every edit.
    /// </summary>
    public IReadOnlyList<UsbCalibrationPoint>? CalibrationCurvePoints =>
        _settingsService.UsbCalibrationPoints;

    // ── Live sensor pass-through ──────────────────────────────────────────────

    public string LastRawReading => _usbSensorService.LastRawReading;
    public double LatestRawY => _usbSensorService.LatestRawY;
    public double LatestLuminance => _usbSensorService.LatestLuminance;
    public string LatestCctText => $"{_usbSensorService.LatestCct:F0} K";
    public string LiveIntensityText =>
        _usbSensorService.LatestRawY > 0
            ? $"Y = {_usbSensorService.LatestRawY:F1}"
            : "—";

    // ── Calibration enabled toggle ────────────────────────────────────────────

    public bool IsCalibrationEnabled
    {
        get => _settingsService.IsUsbCalibrationEnabled;
        set => _settingsService.IsUsbCalibrationEnabled = value;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public IRelayCommand AddPointCommand { get; }
    public IRelayCommand<CalibrationPointViewModel> RemovePointCommand { get; }
    public IRelayCommand<CalibrationPointViewModel> CaptureCurrentCommand { get; }
    public IRelayCommand ResetToDefaultCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public UsbCalibrationViewModel(
        SettingsService settingsService,
        UsbSensorService usbSensorService
    )
    {
        _settingsService = settingsService;
        _usbSensorService = usbSensorService;

        // Load persisted calibration points into the observable collection.
        LoadPointsFromSettings();

        // Mirror sensor property changes to the UI.
        _eventRoot.Add(
            _usbSensorService.WatchAllProperties(() => OnAllPropertiesChanged())
        );

        // Mirror settings changes (IsCalibrationEnabled) to the UI.
        _eventRoot.Add(
            _settingsService.WatchAllProperties(() => OnAllPropertiesChanged())
        );

        AddPointCommand = new RelayCommand(AddPoint);

        RemovePointCommand = new RelayCommand<CalibrationPointViewModel>(
            pt => { if (pt is not null) RemovePoint(pt); }
        );

        CaptureCurrentCommand = new RelayCommand<CalibrationPointViewModel>(
            pt => { if (pt is not null) pt.RawY = Math.Round(_usbSensorService.LatestRawY, 2); },
            _ => _usbSensorService.LatestRawY > 0
        );

        ResetToDefaultCommand = new RelayCommand(ResetToDefault);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
            // Sensible defaults: darkest room → min brightness, reference bright → full.
            CalibrationPoints.Add(MakeRow(1.0, 10.0, 0, 0, 0, 0));
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
        var maxRawY = CalibrationPoints.Count > 0
            ? CalibrationPoints.Max(p => p.RawY)
            : 100.0;
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
        CalibrationPoints.Add(MakeRow(1.0, 10.0, 0, 0, 0, 0));
        CalibrationPoints.Add(MakeRow(100.0, 100.0, 0, 0, 0, 0));
        SyncToSettings();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _eventRoot.Dispose();

        base.Dispose(disposing);
    }
}
