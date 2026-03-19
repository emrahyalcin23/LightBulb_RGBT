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
/// Notifies its parent whenever RawY or BrightnessPercent changes.
/// </summary>
public partial class CalibrationPointViewModel : ObservableObject
{
    private readonly Action _onChanged;

    [ObservableProperty]
    public partial double RawY { get; set; }

    [ObservableProperty]
    public partial double BrightnessPercent { get; set; }

    public CalibrationPointViewModel(double rawY, double brightnessPercent, Action onChanged)
    {
        _onChanged = onChanged;
        RawY = rawY;
        BrightnessPercent = brightnessPercent;
    }

    partial void OnRawYChanged(double value) => _onChanged();

    partial void OnBrightnessPercentChanged(double value) => _onChanged();
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

    // ── RGBL bias (pass-through to SettingsService) ───────────────────────────

    public double RBias
    {
        get => _settingsService.UsbRBias;
        set => _settingsService.UsbRBias = Math.Clamp(value, -1.0, 1.0);
    }

    public double GBias
    {
        get => _settingsService.UsbGBias;
        set => _settingsService.UsbGBias = Math.Clamp(value, -1.0, 1.0);
    }

    public double BBias
    {
        get => _settingsService.UsbBBias;
        set => _settingsService.UsbBBias = Math.Clamp(value, -1.0, 1.0);
    }

    public double LBias
    {
        get => _settingsService.UsbLBias;
        set => _settingsService.UsbLBias = Math.Clamp(value, -1.0, 1.0);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public IRelayCommand AddPointCommand { get; }
    public IRelayCommand<CalibrationPointViewModel> RemovePointCommand { get; }
    public IRelayCommand<CalibrationPointViewModel> CaptureCurrentCommand { get; }
    public IRelayCommand ResetToDefaultCommand { get; }
    public IRelayCommand CloseCommand { get; }

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

        // Mirror settings changes (IsCalibrationEnabled, RGBL biases) to the UI.
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

        CloseCommand = new RelayCommand(() => Close(true));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void LoadPointsFromSettings()
    {
        CalibrationPoints.Clear();

        var persisted = _settingsService.UsbCalibrationPoints;
        if (persisted is { Count: > 0 })
        {
            foreach (var p in persisted.OrderBy(p => p.RawY))
                CalibrationPoints.Add(MakeRow(p.RawY, p.BrightnessPercent));
        }
        else
        {
            // Sensible defaults: darkest room → min brightness, reference bright → full.
            CalibrationPoints.Add(MakeRow(1.0, 10.0));
            CalibrationPoints.Add(MakeRow(100.0, 100.0));
            SyncToSettings();
        }
    }

    private CalibrationPointViewModel MakeRow(double rawY, double pct) =>
        new(rawY, pct, SyncToSettings);

    private void SyncToSettings()
    {
        _settingsService.UsbCalibrationPoints = CalibrationPoints
            .OrderBy(p => p.RawY)
            .Select(p => new UsbCalibrationPoint(p.RawY, Math.Clamp(p.BrightnessPercent, 0, 100)))
            .ToList();

        OnPropertyChanged(nameof(CalibrationCurvePoints));
    }

    private void AddPoint()
    {
        // Place new point at the midpoint of the existing range, or just append.
        var maxRawY = CalibrationPoints.Count > 0
            ? CalibrationPoints.Max(p => p.RawY)
            : 100.0;
        CalibrationPoints.Add(MakeRow(Math.Round(maxRawY * 1.5, 2), 100.0));
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
        CalibrationPoints.Add(MakeRow(1.0, 10.0));
        CalibrationPoints.Add(MakeRow(100.0, 100.0));
        SyncToSettings();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _eventRoot.Dispose();

        base.Dispose(disposing);
    }
}
