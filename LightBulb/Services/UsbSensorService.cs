using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LightBulb.PlatformInterop;
using Microsoft.Win32;

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
        @"R:\s*(?<r>[\d.]+)[\s,]+G:\s*(?<g>[\d.]+)[\s,]+B:\s*(?<b>[\d.]+)",
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
    /// Returns the set of COM port names that are associated with a Raspberry Pi Pico (VID 0x2E8A)
    /// by querying the Windows registry. Returns an empty set if no Pico is found or on error,
    /// in which case the caller should fall back to trying all ports.
    /// </summary>
    private static IReadOnlySet<string> GetPicoPortNames()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var usbKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\USB"
            );
            if (usbKey is null)
                return result;

            foreach (var vidPid in usbKey.GetSubKeyNames())
            {
                if (!vidPid.StartsWith("VID_2E8A", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var vidKey = usbKey.OpenSubKey(vidPid);
                if (vidKey is null)
                    continue;

                foreach (var instance in vidKey.GetSubKeyNames())
                {
                    using var deviceParams = vidKey.OpenSubKey($@"{instance}\Device Parameters");
                    var portName = deviceParams?.GetValue("PortName")?.ToString();
                    if (!string.IsNullOrWhiteSpace(portName))
                        result.Add(portName);
                }
            }
        }
        catch
        {
            // Registry unavailable — return empty (caller falls back to all ports)
        }

        return result;
    }

    /// <summary>
    /// Opens the serial port and starts periodic sensor reads.
    /// Safe to call multiple times — stops any previous session first.
    /// </summary>
    public void Start()
    {
        Stop();

        try
        {
            _port = new SerialPort(_settingsService.UsbPortName, _settingsService.UsbBaudRate)
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
    /// Scans all available serial ports and returns the first one that responds with a valid
    /// RGB reading. Updates <see cref="SettingsService.UsbPortName"/> on success.
    /// Each port is tried with a 1-second read timeout so the scan is fast.
    /// </summary>
    public Task<PortScanResult> AutoDetectPortAsync()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsTestingConnection = true;
            ConnectionTestMessage = "Port taranıyor...";
        });

        return Task.Run(() =>
        {
            var baud = _settingsService.UsbBaudRate;
            var prefix = string.IsNullOrWhiteSpace(_settingsService.UsbReadCommand)
                ? "OKU"
                : _settingsService.UsbReadCommand.Trim();
            var testCommand = $"{prefix}_1";

            // Prefer ports belonging to a Raspberry Pi Pico 2W (VID 0x2E8A).
            // If none are found in the registry, fall back to scanning all ports.
            var picoPortNames = GetPicoPortNames();
            var allPortNames = SerialPort.GetPortNames();
            var portNames = picoPortNames.Count > 0
                ? allPortNames.Where(picoPortNames.Contains).ToArray()
                : allPortNames;

            var triedPorts = new List<(string Port, string Outcome)>();
            string? foundPort = null;
            string foundRaw = "";

            // Phase 1 — open all candidate ports up-front so DTR-reset starts simultaneously.
            var opened = new List<(string Name, SerialPort Port)>();
            foreach (var portName in portNames)
            {
                try
                {
                    var p = new SerialPort(portName, baud) { ReadTimeout = 3000, WriteTimeout = 1000 };
                    p.Open();
                    opened.Add((portName, p));
                }
                catch (Exception ex)
                {
                    triedPorts.Add((portName, $"✗ {ex.Message}"));
                }
            }

            // Phase 2 — wait for the Pico to finish its boot sequence (~2 s).
            if (opened.Count > 0)
                System.Threading.Thread.Sleep(2500);

            // Phase 3 — flush boot messages, send command, read response on each open port.
            foreach (var (portName, p) in opened)
            {
                try
                {
                    // Discard any boot messages buffered during the DTR-reset wait.
                    p.DiscardInBuffer();
                    p.WriteLine(testCommand);
                    var raw = p.ReadLine().Trim();
                    if (ReadingPattern.IsMatch(raw))
                    {
                        foundPort = portName;
                        foundRaw = raw;
                        triedPorts.Add((portName, $"✓ Yanıt: {raw}"));
                        break;
                    }
                    triedPorts.Add((portName, $"✗ Geçersiz yanıt: {raw}"));
                }
                catch (TimeoutException)
                {
                    triedPorts.Add((portName, "✗ Zaman aşımı"));
                }
                catch (Exception ex)
                {
                    triedPorts.Add((portName, $"✗ {ex.Message}"));
                }
                finally
                {
                    try { p.Close(); } catch { }
                    p.Dispose();
                }
            }

            if (foundPort is not null)
                Dispatcher.UIThread.Post(() =>
                {
                    _settingsService.UsbPortName = foundPort;
                    IsTestingConnection = false;
                    ConnectionTestMessage = $"✓ Sensör {foundPort} portunda bulundu";
                });
            else
                Dispatcher.UIThread.Post(() =>
                {
                    IsTestingConnection = false;
                    ConnectionTestMessage = portNames.Length == 0
                        ? "✗ Sistemde hiç seri port bulunamadı"
                        : "✗ Sensör hiçbir portta bulunamadı";
                });

            return new PortScanResult(testCommand, baud, triedPorts, foundPort, foundRaw);
        });
    }

    /// <summary>
    /// Tests the hardware connection by sending a test command and waiting for a valid RGB
    /// response. Returns a <see cref="ConnectionTestResult"/> with full diagnostic details.
    /// </summary>
    public Task<ConnectionTestResult> TestConnectionAsync()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsTestingConnection = true;
            ConnectionTestMessage = "Test ediliyor...";
        });

        return Task.Run(RunTest);
    }

    private ConnectionTestResult RunTest()
    {
        var portName = _settingsService.UsbPortName;
        var baud = _settingsService.UsbBaudRate;
        var prefix = string.IsNullOrWhiteSpace(_settingsService.UsbReadCommand)
            ? "OKU"
            : _settingsService.UsbReadCommand.Trim();
        var testCommand = $"{prefix}_1";

        // Verify the configured port belongs to a Pico 2W (only when opening a new port).
        var picoPortNames = GetPicoPortNames();
        if (picoPortNames.Count > 0 && !picoPortNames.Contains(portName))
        {
            var result = new ConnectionTestResult(portName, baud, testCommand, false, "",
                "Bu port Raspberry Pi Pico 2W cihazına ait değil");
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = false;
                IsTestingConnection = false;
                ConnectionTestMessage = "✗ Seçili port Pico 2W değil";
            });
            return result;
        }

        // If already running use the existing open port.
        var useExisting = _port is { IsOpen: true };
        SerialPort? testPort = useExisting ? null : new SerialPort(portName, baud)
        {
            ReadTimeout = 3000,
            WriteTimeout = 1000,
        };

        try
        {
            var port = useExisting ? _port! : testPort!;
            if (!useExisting)
            {
                port.Open();
                // Wait for Pico to finish its boot sequence before sending the command.
                System.Threading.Thread.Sleep(2500);
            }

            // Discard any boot messages buffered during the DTR-reset wait.
            port.DiscardInBuffer();
            port.WriteLine(testCommand);
            var raw = port.ReadLine().Trim();
            var ok = ReadingPattern.IsMatch(raw);

            var result = new ConnectionTestResult(portName, baud, testCommand, ok, raw,
                ok ? "" : "Yanıt formatı beklenenle eşleşmedi");

            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = ok;
                IsTestingConnection = false;
                ConnectionTestMessage = ok ? "✓ Bağlantı başarılı" : "✗ Geçersiz yanıt formatı";
            });

            return result;
        }
        catch (TimeoutException)
        {
            var result = new ConnectionTestResult(portName, baud, testCommand, false, "",
                "Zaman aşımı — sensörden yanıt gelmedi");
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = false;
                IsTestingConnection = false;
                ConnectionTestMessage = "✗ Zaman aşımı";
            });
            return result;
        }
        catch (Exception ex)
        {
            var result = new ConnectionTestResult(portName, baud, testCommand, false, "",
                ex.Message);
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = false;
                IsTestingConnection = false;
                ConnectionTestMessage = $"✗ {ex.Message}";
            });
            return result;
        }
        finally
        {
            if (!useExisting)
            {
                try { testPort?.Close(); } catch { }
                testPort?.Dispose();
            }
        }
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

/// <summary>
/// Diagnostic result returned by <see cref="UsbSensorService.TestConnectionAsync"/>.
/// </summary>
public record ConnectionTestResult(
    string PortName,
    int BaudRate,
    string SentCommand,
    bool Success,
    string RawResponse,
    string ErrorMessage
);

/// <summary>
/// Result of <see cref="UsbSensorService.AutoDetectPortAsync"/>.
/// </summary>
public record PortScanResult(
    string SentCommand,
    int BaudRate,
    List<(string Port, string Outcome)> TriedPorts,
    string? FoundPort,
    string FoundRaw
);
