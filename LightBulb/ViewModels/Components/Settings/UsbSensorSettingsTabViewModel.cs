using System;
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
    }

    private async Task TestConnectionAsync()
    {
        var result = await _usbSensorService.TestConnectionAsync();

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

    public static int[] AvailableBaudRates { get; } = [9600, 19200, 38400, 57600, 115200, 230400];

    public int BaudRate
    {
        get => SettingsService.UsbBaudRate;
        set => SettingsService.UsbBaudRate = value;
    }

    public double ReadIntervalMinutes
    {
        get => SettingsService.UsbReadIntervalMinutes;
        set
        {
            SettingsService.UsbReadIntervalMinutes = Math.Clamp(value, 1, 1440);
            OnPropertyChanged(nameof(FinalCommand));
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

    /// <summary>Final command that will be sent to the sensor, e.g. "OKU_15".</summary>
    public string FinalCommand =>
        $"{(string.IsNullOrWhiteSpace(SettingsService.UsbReadCommand) ? "OKU" : SettingsService.UsbReadCommand.Trim())}_{(int)Math.Max(1, SettingsService.UsbReadIntervalMinutes)}";

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
