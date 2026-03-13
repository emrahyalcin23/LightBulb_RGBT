using System;
using CommunityToolkit.Mvvm.Input;
using LightBulb.Localization;
using LightBulb.Services;
using LightBulb.Utils;
using LightBulb.Utils.Extensions;

namespace LightBulb.ViewModels.Components.Settings;

public class UsbSensorSettingsTabViewModel : SettingsTabViewModelBase
{
    private readonly UsbSensorService _usbSensorService;
    private readonly DisposableCollector _usbEventRoot = new();

    private double _simR = 1200;
    private double _simG = 800;
    private double _simB = 400;

    public UsbSensorSettingsTabViewModel(
        SettingsService settingsService,
        LocalizationManager localizationManager,
        UsbSensorService usbSensorService
    ) : base(settingsService, localizationManager, 5)
    {
        _usbSensorService = usbSensorService;

        // Propagate live sensor readings to the UI
        _usbEventRoot.Add(
            _usbSensorService.WatchAllProperties(OnAllPropertiesChanged)
        );

        InjectSimulatedReadingCommand = new RelayCommand(() =>
            _usbSensorService.InjectSimulatedReading(SimulatedR, SimulatedG, SimulatedB));
    }

    public override string DisplayName => "USB Sensor";

    // ── Connection ────────────────────────────────────────────────────────────

    public string[] AvailablePortNames => UsbSensorService.GetAvailablePortNames();

    public bool IsEnabled
    {
        get => SettingsService.IsUsbSensorEnabled;
        set
        {
            SettingsService.IsUsbSensorEnabled = value;

            if (value)
                _usbSensorService.Start();
            else
                _usbSensorService.Stop();
        }
    }

    public string PortName
    {
        get => SettingsService.UsbPortName;
        set => SettingsService.UsbPortName = value ?? "COM3";
    }

    public double ReadIntervalMinutes
    {
        get => SettingsService.UsbReadIntervalMinutes;
        set => SettingsService.UsbReadIntervalMinutes = Math.Clamp(value, 1, 1440);
    }

    // ── Live readings (pass-through from service) ─────────────────────────────

    public bool IsConnected => _usbSensorService.IsConnected;

    public string LastRawReading => _usbSensorService.LastRawReading;

    public string LastReadTime => _usbSensorService.LastReadTime;

    public double LatestCct => _usbSensorService.LatestCct;

    public string LatestCctText => $"{_usbSensorService.LatestCct:F0} K";

    public double LatestLuminance => _usbSensorService.LatestLuminance;

    // ── Simulation ────────────────────────────────────────────────────────────

    public double SimulatedR { get => _simR; set => SetProperty(ref _simR, value); }
    public double SimulatedG { get => _simG; set => SetProperty(ref _simG, value); }
    public double SimulatedB { get => _simB; set => SetProperty(ref _simB, value); }

    public IRelayCommand InjectSimulatedReadingCommand { get; }

    // ── RGBL bias coefficients [-1.0, +1.0] ──────────────────────────────────

    public double RBias
    {
        get => SettingsService.UsbRBias;
        set => SettingsService.UsbRBias = Math.Clamp(value, -1.0, 1.0);
    }

    public double GBias
    {
        get => SettingsService.UsbGBias;
        set => SettingsService.UsbGBias = Math.Clamp(value, -1.0, 1.0);
    }

    public double BBias
    {
        get => SettingsService.UsbBBias;
        set => SettingsService.UsbBBias = Math.Clamp(value, -1.0, 1.0);
    }

    public double LBias
    {
        get => SettingsService.UsbLBias;
        set => SettingsService.UsbLBias = Math.Clamp(value, -1.0, 1.0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _usbEventRoot.Dispose();

        base.Dispose(disposing);
    }
}
