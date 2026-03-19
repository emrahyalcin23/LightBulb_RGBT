using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
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
        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Source 1: standard OS serial port list (HARDWARE\DEVICEMAP\SERIALCOMM)
        foreach (var p in SerialPort.GetPortNames())
            ports.Add(p);

        // Source 2: USB device registry — catches CDC/VCP devices that lag in SERIALCOMM
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
        catch { /* registry unavailable or permission denied */ }

        // Source 3: WMI — most comprehensive; finds devices through USB-C hubs and docks
        // that may not appear in the registry sources above.
        foreach (var p in GetPortNamesViaWmi())
            ports.Add(p);

        return [.. ports.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Looks up the Windows USB registry for devices matching the Raspberry Pi Pico VID
    /// (VID_2E8A, Raspberry Pi Ltd) and returns only the COM port names that are
    /// <b>currently active</b> — i.e. present in both the USB device registry and in
    /// <see cref="SerialPort.GetPortNames"/> (HARDWARE\DEVICEMAP\SERIALCOMM).
    /// <para>
    /// The USB device registry retains entries for every device ever connected, so a
    /// stale PortName entry (e.g. COM3 from a previous session) would otherwise cause
    /// the auto-detect scan to waste time on a port that has no Pico attached.
    /// Cross-referencing with <see cref="SerialPort.GetPortNames"/> eliminates those
    /// ghosts: Windows updates SERIALCOMM immediately when a device connects or
    /// disconnects, so only currently present devices appear there.
    /// </para>
    /// </summary>
    public static string[] GetPicoPortNamesFromRegistry()
    {
        // HARDWARE\DEVICEMAP\SERIALCOMM reflects only currently attached serial devices.
        var activePorts = new HashSet<string>(
            SerialPort.GetPortNames(),
            StringComparer.OrdinalIgnoreCase
        );

        var ports = new List<string>();
        try
        {
            using var usbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usbKey is null)
                return [];

            foreach (var vidPid in usbKey.GetSubKeyNames())
            {
                // Raspberry Pi Ltd VID is 2E8A — skip everything else immediately.
                if (!vidPid.StartsWith("VID_2E8A", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var vidPidKey = usbKey.OpenSubKey(vidPid);
                if (vidPidKey is null)
                    continue;

                foreach (var instance in vidPidKey.GetSubKeyNames())
                {
                    using var deviceParams = vidPidKey.OpenSubKey($"{instance}\\Device Parameters");
                    var portName = deviceParams?.GetValue("PortName")?.ToString();

                    // Only include the port if it is currently active — this filters out
                    // stale registry entries left over from previous connections.
                    if (!string.IsNullOrEmpty(portName) && activePorts.Contains(portName))
                        ports.Add(portName);
                }
            }
        }
        catch { /* registry unavailable or permission denied */ }

        return [.. ports.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Queries WMI (Win32_PnPEntity) for all COM ports currently visible to the OS.
    /// This is the same data source that Windows Device Manager uses and is the most
    /// comprehensive method: it finds CDC/VCP devices connected through USB hubs,
    /// USB-C docks, and composite devices that may not appear in SerialPort.GetPortNames()
    /// or the USB device registry.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "System.Management is Windows-only and excluded from trim analysis")]
    public static string[] GetPortNamesViaWmi()
    {
        var ports = new List<string>();
        try
        {
            // Win32_PnPEntity returns every plug-and-play device; the LIKE filter
            // keeps only entries whose friendly name contains "(COMx)".
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"
            );
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                if (name is null)
                    continue;

                // Friendly name format: "USB Serial Device (COM5)"
                var match = Regex.Match(name, @"\(COM(\d+)\)");
                if (match.Success)
                    ports.Add($"COM{match.Groups[1].Value}");
            }
        }
        catch { /* WMI unavailable — fall back to registry methods */ }

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
            string? foundPort = null;
            string foundRaw = "";

            // Scan may need to run twice: the first attempt can miss a Pico that is
            // mid-reset after another application (Arduino IDE, terminal) just closed
            // its connection. DTR going low causes the Pico to re-enumerate on USB,
            // which takes ~1.5 s. We try once immediately, then wait and retry once.
            const int MaxAttempts = 2;
            List<(string Port, string Outcome)>? lastTriedPorts = null;

            for (int attempt = 1; attempt <= MaxAttempts && foundPort is null; attempt++)
            {
                if (attempt == 2)
                {
                    Dispatcher.UIThread.Post(() =>
                        ConnectionTestMessage = "Bulunamadı — cihaz yeniden başlıyor olabilir, 3 sn sonra tekrar deneniyor..."
                    );
                    System.Threading.Thread.Sleep(3000);
                }

                var triedPorts = new List<(string Port, string Outcome)>();

                // Registry scan: Pico (VID_2E8A) ports that are currently active.
                var picoCandidates = GetPicoPortNamesFromRegistry();

                // Candidate list priority:
                //  1. Registry-identified active Pico ports (instant, most likely hit)
                //  2. WMI ports — Device Manager view, finds hub/USB-C connected devices
                //     that SerialPort.GetPortNames() and the USB registry may miss
                //  3. Brute-force COM1-COM99 as final safety net
                var candidates = picoCandidates
                    .Concat(GetPortNamesViaWmi())
                    .Concat(Enumerable.Range(1, 99).Select(i => $"COM{i}"))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                if (attempt == 1 && picoCandidates.Length > 0)
                    Dispatcher.UIThread.Post(() =>
                        ConnectionTestMessage =
                            $"Registry'de Pico bulundu ({string.Join(", ", picoCandidates)}) — port doğrulanıyor..."
                    );

                foreach (var portName in candidates)
                {
                    SerialPort? p = null;
                    try
                    {
                        p = new SerialPort(portName, baud)
                        {
                            ReadTimeout = 2000,
                            WriteTimeout = 1000,
                            // DtrEnable=false: opening the port must NOT send a DTR pulse.
                            // DTR low → Pico resets and re-enumerates (~1.5 s unavailable).
                            // With DTR disabled the already-running firmware stays alive and
                            // responds to KIMSIN immediately — no settle wait required.
                            DtrEnable = false,
                        };
                        p.Open();
                    }
                    catch (UnauthorizedAccessException)
                    {
                        triedPorts.Add((portName, "✗ Port meşgul — Arduino IDE veya başka bir uygulama bu portu açık tutuyor"));
                        p?.Dispose();
                        continue;
                    }
                    catch (IOException)
                    {
                        p?.Dispose();
                        continue; // Port does not exist — skip silently
                    }

                    // Port opened — query device, then close regardless of outcome.
                    try
                    {
                        p.DiscardInBuffer();

                        p.WriteLine(KimsinCommand);
                        var identity = p.ReadLine().Trim();
                        if (!identity.Equals(PicoIdentity, StringComparison.OrdinalIgnoreCase))
                        {
                            triedPorts.Add((portName, $"✗ Yabancı cihaz: {identity}"));
                            continue;
                        }

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
                    finally
                    {
                        p.Close();
                        p.Dispose();
                    }
                }

                lastTriedPorts = triedPorts;
            }

            var triedPortsFinal = lastTriedPorts ?? [];

            // Collect all ports that positively identified as PiColor (✓ prefix).
            var picoPortNames = triedPortsFinal
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
                    ConnectionTestMessage = "✗ Sensör hiçbir portta bulunamadı";
                });

            return new PortScanResult($"{KimsinCommand} → {InstantReadCommand}", baud, triedPortsFinal, foundPort, foundRaw);
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
