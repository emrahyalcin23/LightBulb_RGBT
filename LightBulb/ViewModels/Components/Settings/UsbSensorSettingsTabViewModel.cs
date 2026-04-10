using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using LightBulb.Framework;
using LightBulb.Localization;
using LightBulb.Services;
using LightBulb.Utils;
using LightBulb.Utils.Extensions;

namespace LightBulb.ViewModels.Components.Settings;

public class UsbSensorSettingsTabViewModel : SettingsTabViewModelBase
{
    private readonly UsbSensorService _usbSensorService;
    private readonly DialogManager _dialogManager;
    private readonly ViewModelManager _viewModelManager;
    private readonly DisposableCollector _usbEventRoot = new();

    private double _simR = 1200;
    private double _simG = 800;
    private double _simB = 400;

    public UsbSensorSettingsTabViewModel(
        SettingsService settingsService,
        LocalizationManager localizationManager,
        UsbSensorService usbSensorService,
        DialogManager dialogManager,
        ViewModelManager viewModelManager
    ) : base(settingsService, localizationManager, 5)
    {
        _usbSensorService = usbSensorService;
        _dialogManager = dialogManager;
        _viewModelManager = viewModelManager;

        // Propagate live sensor readings to the UI;
        // also re-evaluate ReadNowCommand.CanExecute when IsConnected changes.
        _usbEventRoot.Add(
            _usbSensorService.WatchAllProperties(() =>
            {
                OnAllPropertiesChanged();
                ReadNowCommand.NotifyCanExecuteChanged();
            })
        );

        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync);
        AutoDetectPortCommand = new AsyncRelayCommand(AutoDetectPortAsync);

        ReadNowCommand = new RelayCommand(
            () => _usbSensorService.ReadNow(),
            () => _usbSensorService.IsConnected
        );

        InjectSimulatedReadingCommand = new RelayCommand(() =>
            _usbSensorService.InjectSimulatedReading(SimulatedR, SimulatedG, SimulatedB));

        OpenCalibrationWindowCommand = new RelayCommand(() =>
        {
            var port = _usbSensorService.CalibrationServerPort;
            var uri  = port > 0
                ? $"http://127.0.0.1:{port}/"
                : new Uri(Path.Combine(AppContext.BaseDirectory, "RGBL_curve_editor.html")).AbsoluteUri;
            Process.StartShellExecute(uri);
        });
    }

    private async Task TestConnectionAsync()
    {
        var result = await _usbSensorService.TestConnectionAsync();

        // await garantisi: arka plan thread'i tamamlanmış, testPort kapatılmış.
        // Şimdi Start() güvenle aynı portu açabilir; ReadNow hemen çalışır.
        if (result.Success && !_usbSensorService.IsConnected)
            _usbSensorService.Start();

        var sb = new StringBuilder();
        sb.AppendLine($"Port      :  {result.PortName}");
        sb.AppendLine($"Baud Rate :  {result.BaudRate}");
        sb.AppendLine($"Komut     :  {result.SentCommand}");
        sb.AppendLine();
        sb.AppendLine("── Alınan Yanıt ──");
        sb.AppendLine(string.IsNullOrEmpty(result.RawResponse) ? "(yanıt yok)" : result.RawResponse);
        sb.AppendLine();
        sb.Append(result.Success
            ? "✓ Bağlantı başarılı — RGB verisi alındı"
            : $"✗ {result.ErrorMessage}");

        await _dialogManager.ShowWindowDialogAsync(
            _viewModelManager.CreateMessageBoxViewModel(
                title: "USB Sensör Tanılama",
                message: sb.ToString(),
                okButtonText: "Tamam",
                cancelButtonText: null
            )
        );
    }

    private async Task AutoDetectPortAsync()
    {
        var result = await _usbSensorService.AutoDetectPortAsync();

        var sb = new StringBuilder();
        sb.AppendLine($"Baud Rate :  {result.BaudRate}");
        sb.AppendLine($"Komut     :  {result.SentCommand}");
        sb.AppendLine();
        sb.AppendLine("── Taranan Portlar ──");

        if (result.TriedPorts.Count == 0)
        {
            sb.AppendLine("(sistemde seri port bulunamadı)");
        }
        else
        {
            foreach (var (port, outcome) in result.TriedPorts)
                sb.AppendLine($"{port,-8} {outcome}");
        }

        sb.AppendLine();
        if (result.FoundPort is not null)
        {
            sb.Append($"✓ Sensör bulundu → {result.FoundPort} portuna geçildi");
            OnPropertyChanged(nameof(PortName));
        }
        else
        {
            sb.Append("✗ Sensör hiçbir portta bulunamadı");
        }

        await _dialogManager.ShowWindowDialogAsync(
            _viewModelManager.CreateMessageBoxViewModel(
                title: "Otomatik Port Tarama",
                message: sb.ToString(),
                okButtonText: "Tamam",
                cancelButtonText: null
            )
        );
    }

    public override string DisplayName => "USB Sensor";

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns only PiColor-detected ports after the first auto-detect scan;
    /// falls back to all system COM ports before the first scan.
    /// </summary>
    public string[] AvailablePortNames =>
        _usbSensorService.DetectedPicoPortNames.Length > 0
            ? _usbSensorService.DetectedPicoPortNames
            : UsbSensorService.GetAvailablePortNames();

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
        set { if (value is not null) SettingsService.UsbPortName = value; }
    }

    public static int[] AvailableBaudRates { get; } = [9600, 19200, 38400, 57600, 115200, 230400];

    public int BaudRate
    {
        get => SettingsService.UsbBaudRate;
        set => SettingsService.UsbBaudRate = value;
    }

    /// <summary>
    /// Unified slider position: 1-59 = saniye modu, 60-179 = dakika modu (1-120 dk).
    /// Dahili depolama: saniye &lt; 1dk → UsbReadIntervalMinutes = secs/60.0
    ///                  dakika       → UsbReadIntervalMinutes = minutes
    /// </summary>
    public int SliderPosition
    {
        get
        {
            var minutes = SettingsService.UsbReadIntervalMinutes;
            if (minutes < 1.0)
            {
                // Saniye modu: kesirli dakikadan saniyeye çevir
                var secs = (int)Math.Round(minutes * 60);
                return Math.Clamp(secs, 1, 59);
            }
            // Dakika modu: 60-179 aralığına kaydır
            var m = (int)Math.Round(minutes);
            return Math.Clamp(m + 59, 60, 179);
        }
        set
        {
            if (value <= 59)
                SettingsService.UsbReadIntervalMinutes = value / 60.0;   // saniye → kesirli dakika
            else
                SettingsService.UsbReadIntervalMinutes = value - 59;     // dakika modu

            OnPropertyChanged(nameof(IntervalLabel));
            OnPropertyChanged(nameof(FinalCommand));
        }
    }

    /// <summary>Slider'ın altında gösterilen birim etiketi, ör: "30 saniye" veya "15 dakika".</summary>
    public string IntervalLabel
    {
        get
        {
            var pos = SliderPosition;
            return pos <= 59 ? $"{pos} saniye" : $"{pos - 59} dakika";
        }
    }

    public string ReadCommand
    {
        get => SettingsService.UsbReadCommand;
        set
        {
            SettingsService.UsbReadCommand = string.IsNullOrWhiteSpace(value) ? "OKU" : value.Trim();
            OnPropertyChanged(nameof(FinalCommand));
        }
    }

    /// <summary>Sensöre gönderilecek final emir, ör: "OKU_15" veya "OKU_S30".</summary>
    public string FinalCommand
    {
        get
        {
            var pos = SliderPosition;
            var cmd = string.IsNullOrWhiteSpace(SettingsService.UsbReadCommand)
                ? "OKU"
                : SettingsService.UsbReadCommand.Trim();
            return pos <= 59 ? $"{cmd}_S{pos}" : $"{cmd}_{pos - 59}";
        }
    }

    // ── Connection test & port scan ───────────────────────────────────────────

    public IAsyncRelayCommand TestConnectionCommand { get; }

    public IAsyncRelayCommand AutoDetectPortCommand { get; }

    // ── Live readings (pass-through from service) ─────────────────────────────

    public bool IsConnected => _usbSensorService.IsConnected;

    public bool IsTestingConnection => _usbSensorService.IsTestingConnection;

    public string ConnectionStatusText => IsTestingConnection
        ? "● Test ediliyor..."
        : IsConnected ? "● Bağlı" : "● Bağlı Değil";

    public string ConnectionTestMessage => _usbSensorService.ConnectionTestMessage;

    public string LastRawReading => _usbSensorService.LastRawReading;

    public string LastReadTime => _usbSensorService.LastReadTime;

    public double LatestCct => _usbSensorService.LatestCct;

    public string LatestCctText => $"{_usbSensorService.LatestCct:F0} K";

    public double LatestLuminance => _usbSensorService.LatestLuminance;

    // ── On-demand read ────────────────────────────────────────────────────────

    public IRelayCommand ReadNowCommand { get; }

    // ── Simulation ────────────────────────────────────────────────────────────

    public double SimulatedR { get => _simR; set => SetProperty(ref _simR, value); }
    public double SimulatedG { get => _simG; set => SetProperty(ref _simG, value); }
    public double SimulatedB { get => _simB; set => SetProperty(ref _simB, value); }

    public IRelayCommand InjectSimulatedReadingCommand { get; }

    // ── RGBL Simulation ───────────────────────────────────────────────────────

    public IRelayCommand OpenCalibrationWindowCommand { get; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _usbEventRoot.Dispose();

        base.Dispose(disposing);
    }
}
