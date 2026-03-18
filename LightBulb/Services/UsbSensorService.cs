using System;
using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LightBulb.PlatformInterop;

namespace LightBulb.Services;

/// <summary>
/// Manages a CDC/VCP USB RGB ambient light sensor.
/// Periodically sends "read" command and parses "R:xxx.xx, G:xxx.xx, B:xxx.xx" responses.
/// Converts raw RGB sensor values to CCT (Kelvin) and relative luminance.
/// </summary>
public partial class UsbSensorService : ObservableObject, IDisposable
{
    // Robertson's CCT formula reference luminance (auto-scales to sensor range)
    private const double ReferenceMax = 4000.0;

    private static readonly Regex ReadingPattern = new(
        @"R:\s*(?<r>[\d.]+)\s*,\s*G:\s*(?<g>[\d.]+)\s*,\s*B:\s*(?<b>[\d.]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private readonly SettingsService _settingsService;

    private SerialPort? _port;
    private IDisposable? _readTimerRegistration;
    private bool _isDisposed;

    [ObservableProperty]
    public partial double LatestCct { get; private set; } = 6500;

    [ObservableProperty]
    public partial double LatestLuminance { get; private set; } = 1.0;

    [ObservableProperty]
    public partial string LastRawReading { get; private set; } = "—";

    [ObservableProperty]
    public partial string LastReadTime { get; private set; } = "—";

    [ObservableProperty]
    public partial bool IsConnected { get; private set; }

    [ObservableProperty]
    public partial bool IsTestingConnection { get; private set; }

    /// <summary>
    /// Human-readable result of the last TestConnection() call.
    /// Empty string means no test has been run yet.
    /// </summary>
    [ObservableProperty]
    public partial string ConnectionTestMessage { get; private set; } = string.Empty;

    public UsbSensorService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>Returns all available serial port names on this system.</summary>
    public static string[] GetAvailablePortNames() => SerialPort.GetPortNames();

    /// <summary>
    /// Opens the serial port and starts periodic sensor reads.
    /// Safe to call multiple times — stops any previous session first.
    /// </summary>
    public void Start()
    {
        Stop();

        try
        {
            _port = new SerialPort(_settingsService.UsbPortName, 9600)
            {
                ReadTimeout = 2000,
                WriteTimeout = 1000,
            };
            _port.Open();
            IsConnected = true;
        }
        catch
        {
            IsConnected = false;
            _port?.Dispose();
            _port = null;
            return;
        }

        // Schedule first read immediately (background thread via Timer), then repeat.
        ScheduleNextRead(delay: TimeSpan.Zero);
    }

    /// <summary>Stops the periodic reads and closes the serial port.</summary>
    public void Stop()
    {
        _readTimerRegistration?.Dispose();
        _readTimerRegistration = null;

        if (_port is not null)
        {
            try
            {
                if (_port.IsOpen)
                    _port.Close();
            }
            catch { }
            _port.Dispose();
            _port = null;
        }

        // Update connected state on the UI thread (Stop may be called from UI thread directly)
        Dispatcher.UIThread.Post(() => IsConnected = false);
    }

    private void ScheduleNextRead(TimeSpan? delay = null)
    {
        var interval = delay ?? TimeSpan.FromMinutes(
            Math.Max(1, _settingsService.UsbReadIntervalMinutes)
        );

        _readTimerRegistration?.Dispose();

        // Timer callback runs on a thread-pool background thread — safe for blocking I/O.
        _readTimerRegistration = Timer.QueueDelayedAction(interval, () =>
        {
            if (_isDisposed)
                return;

            // Blocking serial I/O on background thread — intentional.
            PerformRead();

            if (!_isDisposed)
                ScheduleNextRead();
        });
    }

    /// <summary>
    /// Returns the current command string to send to the sensor (e.g. "OKU_15").
    /// </summary>
    private string BuildReadCommand()
    {
        var prefix = string.IsNullOrWhiteSpace(_settingsService.UsbReadCommand)
            ? "OKU"
            : _settingsService.UsbReadCommand.Trim();
        var interval = (int)Math.Max(1, _settingsService.UsbReadIntervalMinutes);
        return $"{prefix}_{interval}";
    }

    private void PerformRead()
    {
        if (_port is not { IsOpen: true })
            return;

        try
        {
            _port.WriteLine(BuildReadCommand());
            var response = _port.ReadLine();
            ParseAndDispatch(response);
        }
        catch
        {
            // On error, mark as disconnected and let the next scheduled read try again.
            Dispatcher.UIThread.Post(() => IsConnected = false);
        }
    }

    /// <summary>
    /// Immediately performs a single sensor read without waiting for the next scheduled interval.
    /// Only effective when the sensor is started and the port is open.
    /// </summary>
    public void ReadNow() => Task.Run(PerformRead);

    /// <summary>
    /// Tests the hardware connection by opening the port (if not already open), sending
    /// a test command with interval=1 and checking for a valid RGB response.
    /// Updates <see cref="IsConnected"/> with the result.
    /// </summary>
    public void TestConnection()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsTestingConnection = true;
            ConnectionTestMessage = "Test ediliyor...";
        });

        // If already running, use the open port for the test read.
        if (_port is { IsOpen: true })
        {
            Task.Run(() =>
            {
                try
                {
                    _port.WriteLine(BuildReadCommand());
                    var response = _port.ReadLine();
                    var ok = ReadingPattern.IsMatch(response);
                    Dispatcher.UIThread.Post(() =>
                    {
                        IsConnected = ok;
                        IsTestingConnection = false;
                        ConnectionTestMessage = ok
                            ? "✓ Bağlantı başarılı — RGB verisi alındı"
                            : $"✗ Geçersiz yanıt formatı: \"{response.Trim()}\"";
                    });
                }
                catch (TimeoutException)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        IsConnected = false;
                        IsTestingConnection = false;
                        ConnectionTestMessage = "✗ Zaman aşımı — sensörden yanıt gelmedi";
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        IsConnected = false;
                        IsTestingConnection = false;
                        ConnectionTestMessage = $"✗ Hata: {ex.Message}";
                    });
                }
            });
            return;
        }

        // Port not open — open a temporary connection just for the test.
        Task.Run(() =>
        {
            var prefix = string.IsNullOrWhiteSpace(_settingsService.UsbReadCommand)
                ? "OKU"
                : _settingsService.UsbReadCommand.Trim();
            var testCommand = $"{prefix}_1";
            var portName = _settingsService.UsbPortName;

            SerialPort? testPort = null;
            try
            {
                testPort = new SerialPort(portName, 9600)
                {
                    ReadTimeout = 3000,
                    WriteTimeout = 1000,
                };
                testPort.Open();
                testPort.WriteLine(testCommand);
                var response = testPort.ReadLine();
                var ok = ReadingPattern.IsMatch(response);
                Dispatcher.UIThread.Post(() =>
                {
                    IsConnected = ok;
                    IsTestingConnection = false;
                    ConnectionTestMessage = ok
                        ? "✓ Bağlantı başarılı — RGB verisi alındı"
                        : $"✗ Geçersiz yanıt formatı: \"{response.Trim()}\"";
                });
            }
            catch (TimeoutException)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    IsConnected = false;
                    IsTestingConnection = false;
                    ConnectionTestMessage = "✗ Zaman aşımı — sensörden yanıt gelmedi";
                });
            }
            catch (Exception ex) when (ex.Message.Contains("denied") || ex.Message.Contains("access", StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    IsConnected = false;
                    IsTestingConnection = false;
                    ConnectionTestMessage = $"✗ Port erişim engellendi: {portName}";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    IsConnected = false;
                    IsTestingConnection = false;
                    ConnectionTestMessage = $"✗ Port açılamadı ({portName}): {ex.Message}";
                });
            }
            finally
            {
                try { testPort?.Close(); } catch { }
                testPort?.Dispose();
            }
        });
    }

    private void ParseAndDispatch(string response)
    {
        var match = ReadingPattern.Match(response);
        if (!match.Success)
            return;

        if (
            !double.TryParse(
                match.Groups["r"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var r
            )
            || !double.TryParse(
                match.Groups["g"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var g
            )
            || !double.TryParse(
                match.Groups["b"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var b
            )
        )
            return;

        var cct = ComputeCct(r, g, b);
        var luminance = ComputeLuminance(r, g, b);
        var rawText = $"R:{r:F2}  G:{g:F2}  B:{b:F2}";
        var timeText = DateTime.Now.ToString("HH:mm:ss");

        // Update observable properties on the UI thread so bindings refresh correctly.
        Dispatcher.UIThread.Post(() =>
        {
            LatestCct = cct;
            LatestLuminance = luminance;
            LastRawReading = rawText;
            LastReadTime = timeText;
            IsConnected = true;
        });
    }

    /// <summary>
    /// Robertson's method: converts raw R, G, B sensor counts to
    /// Correlated Color Temperature in Kelvin.
    /// </summary>
    private static double ComputeCct(double r, double g, double b)
    {
        var total = r + g + b;
        if (total < 0.001)
            return 6500;

        var cx = r / total;
        var cy = g / total;

        var denominator = 0.1858 - cy;
        if (Math.Abs(denominator) < 1e-9)
            return 6500;

        var n = (cx - 0.3320) / denominator;
        var cct = -449 * n * n * n + 3525 * n * n - 6823.3 * n + 5520.33;

        return Math.Clamp(cct, 500, 20_000);
    }

    /// <summary>
    /// Computes CIE relative luminance from raw sensor values,
    /// normalised to [0.1, 1.0] against a reference maximum.
    /// </summary>
    private static double ComputeLuminance(double r, double g, double b)
    {
        // CIE Y weighting
        var rawY = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return Math.Clamp(rawY / ReferenceMax, 0.1, 1.0);
    }

    /// <summary>
    /// Injects a fake sensor reading directly — for testing without hardware.
    /// Uses the same CCT/luminance pipeline as real serial data.
    /// </summary>
    public void InjectSimulatedReading(double r, double g, double b)
    {
        var fakeResponse = string.Create(
            CultureInfo.InvariantCulture,
            $"R:{r:F2}, G:{g:F2}, B:{b:F2}"
        );
        ParseAndDispatch(fakeResponse);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        Stop();
    }
}
