using System.Collections.Generic;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using LightBulb.Models;

namespace LightBulb.Services;

/// <summary>
/// Bağlantı önceliği: Auto → önce USB dener, başarısız olursa TCP'ye geçer.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SensorConnectionMode>))]
public enum SensorConnectionMode
{
    Auto,
    Usb,
    Tcp,
}

// USB sensor and RGBL calibration settings — isolated from the base SettingsService
// so that upstream merges don't conflict with this feature set.
public partial class SettingsService
{
    // USB Sensor

    [ObservableProperty]
    public partial bool IsUsbSensorEnabled { get; set; } = false;

    [ObservableProperty]
    [JsonPropertyName("UsbPortName")]
    public partial string UsbPortName { get; set; } = string.Empty;

    [ObservableProperty]
    [JsonPropertyName("UsbBaudRate")]
    public partial int UsbBaudRate { get; set; } = 115200;

    [ObservableProperty]
    [JsonPropertyName("UsbReadIntervalMinutes")]
    public partial double UsbReadIntervalMinutes { get; set; } = 15;

    [ObservableProperty]
    [JsonPropertyName("UsbReadCommand")]
    public partial string UsbReadCommand { get; set; } = "OKU";

    // Pre-curve: normalize ambient % from sensor before sending to curve
    [ObservableProperty]
    [JsonPropertyName("UsbAmbientMinPct")]
    public partial double UsbAmbientMinPct { get; set; } = 0.0;

    [ObservableProperty]
    [JsonPropertyName("UsbAmbientMaxPct")]
    public partial double UsbAmbientMaxPct { get; set; } = 100.0;

    // Post-curve: clamp R/G/B/L curve outputs to this range
    [ObservableProperty]
    [JsonPropertyName("UsbOutputMin")]
    public partial double UsbOutputMin { get; set; } = 0.0;

    [ObservableProperty]
    [JsonPropertyName("UsbOutputMax")]
    public partial double UsbOutputMax { get; set; } = 100.0;

    // Calibration curve
    [ObservableProperty]
    public partial bool IsUsbCalibrationEnabled { get; set; } = false;

    [ObservableProperty]
    public partial IReadOnlyList<UsbCalibrationPoint>? UsbCalibrationPoints { get; set; }

    // RGBL curve editor calibration
    [ObservableProperty]
    [JsonPropertyName("RgblCalibrationJsonPath")]
    public partial string RgblCalibrationJsonPath { get; set; } = string.Empty;

    // Neural network output mode
    [ObservableProperty]
    [JsonPropertyName("IsNnModeActive")]
    public partial bool IsNnModeActive { get; set; } = false;

    // Geographic location for NN inputs
    [ObservableProperty]
    [JsonPropertyName("GeoLatitude")]
    public partial double GeoLatitude { get; set; } = 41.0;

    [ObservableProperty]
    [JsonPropertyName("GeoLongitude")]
    public partial double GeoLongitude { get; set; } = 29.0;

    // TCP/WiFi Sensor
    [ObservableProperty]
    [JsonPropertyName("SensorConnectionMode")]
    public partial SensorConnectionMode SensorConnectionMode { get; set; } = SensorConnectionMode.Auto;

    [ObservableProperty]
    [JsonPropertyName("TcpSensorHost")]
    public partial string TcpSensorHost { get; set; } = string.Empty;

    [ObservableProperty]
    [JsonPropertyName("TcpSensorPort")]
    public partial int TcpSensorPort { get; set; } = 8266;

}

// SerializerContext is owned entirely here so that all [JsonSerializable] attributes
// are in one place — the source generator requires a single declaration per context.
public partial class SettingsService
{
    [JsonSerializable(typeof(SensorConnectionMode))]
    [JsonSerializable(typeof(SettingsService))]
    [JsonSerializable(typeof(UsbCalibrationPoint))]
    [JsonSerializable(typeof(List<UsbCalibrationPoint>))]
    [JsonSerializable(typeof(RgblCalibration))]
    [JsonSerializable(typeof(CurvePoint))]
    [JsonSerializable(typeof(List<CurvePoint>))]
    [JsonSerializable(typeof(Dictionary<string, List<CurvePoint>>))]
    private partial class SerializerContext : JsonSerializerContext;
}
