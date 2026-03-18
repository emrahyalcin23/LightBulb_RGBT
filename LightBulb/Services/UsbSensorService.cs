using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

    // PiColor firmware identity handshake
    private const string KimsinCommand = "KIMSIN";
    private const string PicoIdentity = "BENIM_OZEL_PICOM_V1";

    // OKU = single instantaneous reading (OKU_N only when N>0, i.e. periodic interval)
    private const string InstantReadCommand = "OKU";

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
    /// COM ports where a PiColor device was detected during the last auto-detect scan.
    /// Empty until the first scan is completed.
    /// </summary>
    [ObservableProperty]
    public partial string[] DetectedPicoPortNames { get; private set; } = [];

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

    /// <summary>
    /// Returns all available serial port names on this system.
    /// Combines the standard registry map with USB device enumeration so that
    /// CDC/VCP devices (e.g. Raspberry Pi Pico) are found even when they are
    /// not yet reflected in HARDWARE\DEVICEMAP\SERIALCOMM.
    /// </summary>
    public static string[] GetAvailablePortNames()
    {
        var ports = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);

        // Also scan USB devices — some CDC drivers register PortName here but
        // do not always update HARDWARE\DEVICEMAP\SERIALCOMM in time.
        try
        {
            using var usbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usbKey is not null)
            {
                foreach (var vidPid in usbKey.GetSubKeyNames())
                {
                    using var vidPidKey = usbKey.OpenSubKey(vidPid);
                    if (vidPidKey is null) continue;

                    foreach (var instance in vidPidKey.GetSubKeyNames())
                    {
                        using var deviceParams = vidPidKey.OpenSubKey($"{instance}\\Device Parameters");
                        var portName = deviceParams?.GetValue("PortName")?.ToString();
                        if (!string.IsNullOrEmpty(portName))
                            ports.Add(portName);
                    }
                }
            }
        }
        catch { /* registry unavailable or permission denied — fall back to standard list */ }

        return [.. ports.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Opens the serial port and starts periodic sensor reads.
    /// Safe to call multiple times — stops any previous session first.
    /// </summary>
    public void Start()
    {
        Stop();

        if (string.IsNullOrWhiteSpace(_settingsService.UsbPortName))
            return; // No port configured yet — wait for auto-detect

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

            // Candidate list: registry/GetPortNames + brute-force COM1-COM30.
            // Ports that do not physically exist fail instantly with IOException
            // ("file not found" / "device not ready") and are silently skipped.
            // This is the only reliable way to find CDC/VCP devices whose drivers
            // do not register in HARDWARE\DEVICEMAP\SERIALCOMM.
            var candidates = new HashSet<string>(GetAvailablePortNames(), StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i <= 30; i++)
                candidates.Add($"COM{i}");

            var portNames = candidates.OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            var triedPorts = new List<(string Port, string Outcome)>();
            string? foundPort = null;
            string foundRaw = "";

            // Phase 1 — open all ports up-front so any DTR-reset starts simultaneously.
            // Non-existent ports throw IOException immediately and are silently ignored.
            var opened = new List<(string Name, SerialPort Port)>();
            foreach (var portName in portNames)
            {
                try
                {
                    var p = new SerialPort(portName, baud) { ReadTimeout = 3000, WriteTimeout = 1000 };
                    p.Open();
                    opened.Add((portName, p));
                }
                catch (UnauthorizedAccessException)
                {
                    triedPorts.Add((portName, "✗ Port meşgul — Arduino IDE veya başka bir uygulama bu portu açık tutuyor"));
                }
                catch (IOException)
                {
                    // Port does not exist — skip silently (expected for most COM1-COM30 entries)
                }
                catch (Exception ex)
                {
                    triedPorts.Add((portName, $"✗ {ex.Message}"));
                }
            }

            // Phase 2 — short wait to let the device settle after port open.
            if (opened.Count > 0)
                System.Threading.Thread.Sleep(1500);

            // Phase 3 — identify device with KIMSIN, then get an instant reading.
            foreach (var (portName, p) in opened)
            {
                try
                {
                    // Discard any buffered data before sending commands.
                    p.DiscardInBuffer();

                    // Step 1: Verify this is a PiColor device before sending any read command.
                    p.WriteLine(KimsinCommand);
                    var identity = p.ReadLine().Trim();
                    if (!identity.Equals(PicoIdentity, StringComparison.OrdinalIgnoreCase))
                    {
                        triedPorts.Add((portName, $"✗ Yabancı cihaz: {identity}"));
                        continue;
                    }

                    // Step 2: Request an instant reading to confirm the sensor works.
                    p.WriteLine(InstantReadCommand);
                    var raw = p.ReadLine().Trim();
                    if (ReadingPattern.IsMatch(raw))
                    {
                        foundPort = portName;
                        foundRaw = raw;
                        triedPorts.Add((portName, $"✓ PiColor bulundu — Yanıt: {raw}"));
                        break;
                    }
                    triedPorts.Add((portName, $"✗ Kimlik doğrulandı ama sensör yanıtı geçersiz: {raw}"));
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

            // Collect all ports that positively identified as PiColor (✓ prefix).
            var picoPortNames = triedPorts
                .Where(t => t.Outcome.StartsWith("✓"))
                .Select(t => t.Port)
                .ToArray();

            if (foundPort is not null)
                Dispatcher.UIThread.Post(() =>
                {
                    _settingsService.UsbPortName = foundPort;
                    DetectedPicoPortNames = picoPortNames;
                    IsTestingConnection = false;
                    ConnectionTestMessage = $"✓ Sensör {foundPort} portunda bulundu";
                });
            else
                Dispatcher.UIThread.Post(() =>
                {
                    if (picoPortNames.Length > 0)
                        DetectedPicoPortNames = picoPortNames;
                    IsTestingConnection = false;
                    ConnectionTestMessage = portNames.Length == 0
                        ? "✗ Sistemde hiç seri port bulunamadı"
                        : "✗ Sensör hiçbir portta bulunamadı";
                });

            return new PortScanResult($"{KimsinCommand} → {InstantReadCommand}", baud, triedPorts, foundPort, foundRaw);
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

        if (string.IsNullOrWhiteSpace(portName))
        {
            var noPortResult = new ConnectionTestResult("", baud, KimsinCommand, false, "",
                "Port seçilmedi — önce Otomatik Bul'u çalıştırın");
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = false;
                IsTestingConnection = false;
                ConnectionTestMessage = "✗ Port seçilmedi";
            });
            return noPortResult;
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
                // Short settle wait after port open.
                System.Threading.Thread.Sleep(1500);
            }

            // Discard any buffered data before sending commands.
            port.DiscardInBuffer();

            // Step 1: Verify device identity — must respond with PicoIdentity.
            port.WriteLine(KimsinCommand);
            var identity = port.ReadLine().Trim();
            if (!identity.Equals(PicoIdentity, StringComparison.OrdinalIgnoreCase))
            {
                var badResult = new ConnectionTestResult(portName, baud, KimsinCommand, false, identity,
                    $"Kimlik doğrulanamadı — beklenen: {PicoIdentity}, gelen: {identity}");
                Dispatcher.UIThread.Post(() =>
                {
                    IsConnected = false;
                    IsTestingConnection = false;
                    ConnectionTestMessage = "✗ PiColor cihazı değil";
                });
                return badResult;
            }

            // Step 2: Request an instant reading to confirm the sensor works.
            port.WriteLine(InstantReadCommand);
            var raw = port.ReadLine().Trim();
            var ok = ReadingPattern.IsMatch(raw);

            var result = new ConnectionTestResult(portName, baud, InstantReadCommand, ok, raw,
                ok ? "" : "Yanıt formatı beklenenle eşleşmedi");

            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = ok;
                IsTestingConnection = false;
                ConnectionTestMessage = ok ? "✓ Bağlantı başarılı" : "✗ Geçersiz yanıt formatı";
            });

            return result;
        }
        catch (UnauthorizedAccessException)
        {
            var result = new ConnectionTestResult(portName, baud, KimsinCommand, false, "",
                "Port meşgul — Arduino IDE veya başka bir uygulama bu portu açık tutuyor");
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = false;
                IsTestingConnection = false;
                ConnectionTestMessage = "✗ Port meşgul — diğer uygulamayı kapatın";
            });
            return result;
        }
        catch (TimeoutException)
        {
            var result = new ConnectionTestResult(portName, baud, KimsinCommand, false, "",
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
            var result = new ConnectionTestResult(portName, baud, KimsinCommand, false, "",
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
