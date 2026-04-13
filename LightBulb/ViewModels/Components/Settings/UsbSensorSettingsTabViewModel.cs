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
    private double _simAmbientPct = 50.0;

    // Prevents showing the dark-reading warning more than once per sensor session.
    private bool _darkWarningShownForCurrentSession;

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

        // Dark-reading guard: when the first valid data arrives and the ambient
        // is very dark (LatestRawY < 200), ask the user before applying it to the screen.
        _usbEventRoot.Add(
            _usbSensorService.WatchProperty(
                o => o.HasReceivedValidData,
                () => _ = CheckDarkReadingAsync()
            )
        );

        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync);
        AutoDetectPortCommand = new AsyncRelayCommand(AutoDetectPortAsync);

        // "Şimdi Oku" is available whenever a port is configured, regardless of whether
        // the toggle is on. When the port is closed it opens a temporary connection.
        ReadNowCommand = new RelayCommand(
            () => _usbSensorService.ReadNow(),
            () => !string.IsNullOrWhiteSpace(SettingsService.UsbPortName)
        );

        // CanExecute depends on UsbPortName which is a settings property, not a sensor
        // property, so notify separately when it changes.
        _usbEventRoot.Add(
            SettingsService.WatchProperty(
                o => o.UsbPortName,
                () => ReadNowCommand.NotifyCanExecuteChanged()
            )
        );

        InjectSimulatedReadingCommand = new RelayCommand(() =>
            _usbSensorService.InjectSimulatedReading(SimulatedR, SimulatedG, SimulatedB, SimulatedAmbientPct));

        ResetSimulationCommand = new RelayCommand(() =>
            _usbSensorService.ResetSimulation());

        ResetInjectionCommand = new RelayCommand(() =>
        {
            _usbSensorService.ClearInjection();
            _simR          = 1200;
            _simG          = 800;
            _simB          = 400;
            _simAmbientPct = 50.0;
            OnAllPropertiesChanged();
        });

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
            {
                _darkWarningShownForCurrentSession = false; // reset for new session
                _usbSensorService.Start();
            }
            else
            {
                _usbSensorService.Stop();
            }
        }
    }

    /// <summary>
    /// Called each time HasReceivedValidData changes. If the first valid reading of a new
    /// session is very dark (LatestRawY &lt; 200), block gamma application and ask the user
    /// whether to proceed. If the user declines, the toggle is turned off.
    /// </summary>
    private async Task CheckDarkReadingAsync()
    {
        if (!_usbSensorService.HasReceivedValidData) return;
        if (_darkWarningShownForCurrentSession) return;
        if (!SettingsService.IsUsbSensorEnabled) return;
        if (_usbSensorService.LatestRawY >= 200) return;

        _darkWarningShownForCurrentSession = true;

        // Block gamma until the user confirms.
        _usbSensorService.GammaApplyBlocked = true;

        var rawY = _usbSensorService.LatestRawY;
        var dialog = _viewModelManager.CreateMessageBoxViewModel(
            title: "Çok Karanlık Ortam Uyarısı",
            message:
                $"Sensör çok düşük aydınlık değeri okudu (Ham L ≈ {rawY:F0}).\n" +
                "Bu değer ekranı aşırı karartabilir.\n\n" +
                "Yine de sensör moduna geçmek istiyor musunuz?",
            okButtonText: "Evet, Geçir",
            cancelButtonText: "Hayır, İptal"
        );

        var confirmed = await _dialogManager.ShowWindowDialogAsync(dialog);

        if (confirmed == true)
        {
            // User accepted — unblock so the dark values are applied.
            _usbSensorService.GammaApplyBlocked = false;
        }
        else
        {
            // User cancelled — turn off the toggle cleanly.
            _usbSensorService.GammaApplyBlocked = false;
            _usbSensorService.Stop();
            SettingsService.IsUsbSensorEnabled = false;
            _darkWarningShownForCurrentSession = false; // allow warning again next time
            OnPropertyChanged(nameof(IsEnabled));
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

            // Cancel the pending timer and reschedule immediately with the new interval
            // so the user doesn't have to wait for the old countdown to expire.
            _usbSensorService.RescheduleRead();
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

    public string LastReadCommand => _usbSensorService.LastReadCommand;

    /// <summary>The raw serial response line from the sensor (before parsing). Empty until first read attempt.</summary>
    public string LastRawResponse => _usbSensorService.LastRawResponse;

    /// <summary>Non-empty when the last read attempt failed. Shows why the read failed.</summary>
    public string LastReadError => _usbSensorService.LastReadError;

    /// <summary>True when LastReadError is non-empty — used by the UI to highlight the error row.</summary>
    public bool HasReadError => !string.IsNullOrEmpty(_usbSensorService.LastReadError);

    public double LatestCct => _usbSensorService.LatestCct;

    public string LatestCctText => $"{_usbSensorService.LatestCct:F0} K";

    public double LatestLuminance => _usbSensorService.LatestLuminance;

    // ── RGBL profile output ───────────────────────────────────────────────────

    public string RgblLoadStatus => _usbSensorService.RgblLoadStatus;

    public bool IsRgblCalibrationActive => _usbSensorService.IsRgblCalibrationActive;

    public double LatestRgblR => _usbSensorService.LatestRgblR;
    public double LatestRgblG => _usbSensorService.LatestRgblG;
    public double LatestRgblB => _usbSensorService.LatestRgblB;

    public string LatestRgblRText => $"{_usbSensorService.LatestRgblR:F1} %";
    public string LatestRgblGText => $"{_usbSensorService.LatestRgblG:F1} %";
    public string LatestRgblBText => $"{_usbSensorService.LatestRgblB:F1} %";
    public string LatestRgblLText => $"{_usbSensorService.LatestRgblL:F1} %";

    // ── Giren/Çıkan debug tablosu — modül proc değerleri ve ambient girdi ─────

    public string LatestProcRText     => $"{_usbSensorService.LatestProcR:F1} %";
    public string LatestProcGText     => $"{_usbSensorService.LatestProcG:F1} %";
    public string LatestProcBText     => $"{_usbSensorService.LatestProcB:F1} %";
    public string LatestAmbientPctText => $"{_usbSensorService.LatestAmbientPct:F1} %";
    public string RgblBoundaryMinLText => $"{_usbSensorService.RgblBoundaryMinL:F1} %";
    public string RgblBoundaryMaxLText => $"{_usbSensorService.RgblBoundaryMaxL:F1} %";

    // ── On-demand read ────────────────────────────────────────────────────────

    public IRelayCommand ReadNowCommand { get; }

    // ── Simulation ────────────────────────────────────────────────────────────

    public double SimulatedR { get => _simR; set => SetProperty(ref _simR, value); }
    public double SimulatedG { get => _simG; set => SetProperty(ref _simG, value); }
    public double SimulatedB { get => _simB; set => SetProperty(ref _simB, value); }

    /// <summary>Ambient light percentage (0-100) used as X-axis input to RGBL curves.</summary>
    public double SimulatedAmbientPct { get => _simAmbientPct; set => SetProperty(ref _simAmbientPct, value); }

    public IRelayCommand InjectSimulatedReadingCommand { get; }

    public IRelayCommand ResetSimulationCommand { get; }

    /// <summary>
    /// Clears only the active injection override and resets the input fields to defaults.
    /// Does not disturb Last Reading display or connection state.
    /// </summary>
    public IRelayCommand ResetInjectionCommand { get; }

    // ── RGBL Profile Editor ───────────────────────────────────────────────────

    public IRelayCommand OpenCalibrationWindowCommand { get; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _usbEventRoot.Dispose();

        base.Dispose(disposing);
    }
}
