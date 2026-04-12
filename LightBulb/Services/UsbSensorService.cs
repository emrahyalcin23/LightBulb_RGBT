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
using LightBulb.Models;
using LightBulb.Utils.Extensions;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
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
    // Adaptive luminance reference: tracks the highest CIE-Y value seen so far.
    // Starts at 255.0 (= max CIE-Y when R=G=B=255) so the first reading
    // never incorrectly maps to 100 % brightness before the true peak is known.
    private double _peakRawY = 255.0;

    // Adaptive peak for the Clear channel (raw_c). Used to normalise raw_c to
    // the 0-100 % ambient range that feeds the RGBL calibration curves.
    // Starts at 1.0 to avoid division-by-zero on the very first reading.
    private double _peakRawC = 1.0;

    // RGBL curve evaluator loaded from rgbl_calibration.json.
    // Null when no path is configured or the file cannot be parsed.
    private RgblCurveEvaluator? _rgblEvaluator;

    // Last ambient percentage fed into the RGBL curves as X-input (0-100).
    // Stored so ReloadRgblEvaluator can immediately re-evaluate new curves.
    private double _lastAmbientPct = 50.0;

    // When set by InjectSimulatedReading, ParseAndDispatch uses this value
    // directly as ambientPct instead of deriving it from rawC/_peakRawC.
    // Cleared after a single use.
    private double? _simulatedAmbientOverride;

    // PiColor firmware identity handshake
    private const string KimsinCommand = "KIMSIN";
    // Firmware now replies to KIMSIN with a semicolon-delimited line that embeds
    // "IDENTITY=PICOM_<version>" (e.g. "1685106;4;0;0;0;0;IDENTITY=PICOM_V1").
    // We match on the prefix so any firmware version is accepted.
    private const string PicoIdentityPrefix = "IDENTITY=PICOM_";

    // OKU_0 = single instantaneous reading (interval=0 means no periodic streaming)
    private const string InstantReadCommand = "OKU_0";

    private readonly SettingsService _settingsService;

    private SerialPort? _port;
    private IDisposable? _readTimerRegistration;
    private bool _isDisposed;
    // Serialises all blocking port I/O so PerformRead and RunTest never race.
    private readonly System.Threading.SemaphoreSlim _portLock = new(1, 1);

    // Calibration HTTP server fields
    private System.Net.HttpListener? _httpListener;
    private System.Threading.CancellationTokenSource? _httpCts;

    [ObservableProperty]
    public partial double LatestCct { get; private set; } = 6500;

    /// <summary>CCT computed from raw uint16 sensor counts (raw_r, raw_g, raw_b).</summary>
    [ObservableProperty]
    public partial double LatestCctRaw { get; private set; } = 6500;

    /// <summary>CCT computed from firmware-processed 0-100 float values (proc_r, proc_g, proc_b).</summary>
    [ObservableProperty]
    public partial double LatestCctProc { get; private set; } = 6500;

    /// <summary>RGBL curve output: final red channel percent (0-100). Set only when a calibration JSON is loaded.</summary>
    [ObservableProperty]
    public partial double LatestRgblR { get; private set; } = 50.0;

    /// <summary>RGBL curve output: final green channel percent (0-100). Set only when a calibration JSON is loaded.</summary>
    [ObservableProperty]
    public partial double LatestRgblG { get; private set; } = 50.0;

    /// <summary>RGBL curve output: final blue channel percent (0-100). Set only when a calibration JSON is loaded.</summary>
    [ObservableProperty]
    public partial double LatestRgblB { get; private set; } = 50.0;

    [ObservableProperty]
    public partial double LatestLuminance { get; private set; } = 1.0;

    /// <summary>CIE-Y value from the last sensor read. Used by the calibration curve live dot.</summary>
    [ObservableProperty]
    public partial double LatestRawY { get; private set; } = 0;

    /// <summary>Per-point bias values interpolated at the current RawY.</summary>
    [ObservableProperty]
    public partial double LatestRBias { get; private set; } = 0;

    [ObservableProperty]
    public partial double LatestGBias { get; private set; } = 0;

    [ObservableProperty]
    public partial double LatestBBias { get; private set; } = 0;

    [ObservableProperty]
    public partial double LatestLBias { get; private set; } = 0;

    [ObservableProperty]
    public partial string LastRawReading { get; private set; } = "—";

    [ObservableProperty]
    public partial string LastReadTime { get; private set; } = "—";

    /// <summary>The command string sent to the sensor that produced the last reading (e.g. "OKU_S30").</summary>
    [ObservableProperty]
    public partial string LastReadCommand { get; private set; } = "—";

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

    /// <summary>True when the serial port is currently open (regardless of IsConnected).</summary>
    public bool IsPortOpen => _port is { IsOpen: true };

    /// <summary>True when an RGBL calibration JSON is loaded; the display pipeline uses this to decide routing.</summary>
    public bool IsRgblCalibrationActive => _rgblEvaluator is not null;

    /// <summary>
    /// Port of the local calibration HTTP server (127.0.0.1:PORT).
    /// 0 when the server is not running.
    /// </summary>
    public int CalibrationServerPort { get; private set; }

    public UsbSensorService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        // Auto-reload when the user changes the calibration JSON path.
        _ = settingsService.WatchProperty(o => o.RgblCalibrationJsonPath, ReloadRgblEvaluator);
        // Create default calibration file if it doesn't exist yet.
        WriteDefaultCalibrationIfMissing();
        // Load calibration immediately (regardless of whether the sensor is enabled).
        ReloadRgblEvaluator();
        // HTTP calibration server starts with the app; runs for the app's lifetime.
        StartCalibrationHttpServer();
    }

    /// <summary>
    /// Writes a rgbl_calibration.json with default curve values to AppContext.BaseDirectory
    /// if the file does not already exist. Values mirror defaultNodes() in d3-editor.js.
    /// The L channel is the pre-computed LT = L(T(x)) composite (piecewise linear, x = 0..100 step 2).
    /// </summary>
    private static void WriteDefaultCalibrationIfMissing()
    {
        var dest = Path.Combine(AppContext.BaseDirectory, "rgbl_calibration.json");
        if (File.Exists(dest)) return;

        // T nodes: [(0,3),(50,100),(100,3)]  L nodes: [(0,10),(50,55),(100,95)]
        static double Lerp((double X, double Y)[] pts, double x)
        {
            x = Math.Clamp(x, 0, 100);
            if (x <= pts[0].X) return Math.Clamp(pts[0].Y, 0, 100);
            if (x >= pts[^1].X) return Math.Clamp(pts[^1].Y, 0, 100);
            for (var i = 0; i < pts.Length - 1; i++)
            {
                if (x >= pts[i].X && x <= pts[i + 1].X)
                {
                    var t = (x - pts[i].X) / (pts[i + 1].X - pts[i].X);
                    return Math.Clamp(pts[i].Y + t * (pts[i + 1].Y - pts[i].Y), 0, 100);
                }
            }
            return Math.Clamp(pts[^1].Y, 0, 100);
        }

        (double X, double Y)[] tPts = [(0, 3), (50, 100), (100, 3)];
        (double X, double Y)[] lPts = [(0, 10), (50, 55), (100, 95)];

        var ltRows = new System.Text.StringBuilder();
        for (var xi = 0; xi <= 100; xi += 2)
        {
            var lVal = Lerp(lPts, Lerp(tPts, xi));
            if (xi > 0) ltRows.Append(",\n      ");
            ltRows.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"{{ \"x\": {xi:F2}, \"y\": {lVal:F2}, \"fixed\": false }}"));
        }

        var json = $$"""
{
  "version": 8,
  "calcMode": "absolute",
  "channels": {
    "R": [
      { "x": 0.00, "y": 20.00, "fixed": true },
      { "x": 100.00, "y": 88.00, "fixed": true }
    ],
    "G": [
      { "x": 0.00, "y": 15.00, "fixed": true },
      { "x": 100.00, "y": 82.00, "fixed": true }
    ],
    "B": [
      { "x": 0.00, "y": 28.00, "fixed": true },
      { "x": 100.00, "y": 68.00, "fixed": true }
    ],
    "L": [
      {{ltRows}}
    ]
  }
}
""";

        try { File.WriteAllText(dest, json); }
        catch { /* Non-fatal — app runs without default calibration */ }
    }

    /// <summary>
    /// (Re)loads the RGBL curve evaluator from the path stored in settings.
    /// Falls back to rgbl_calibration.json in the application base directory when no path is set.
    /// </summary>
    public void ReloadRgblEvaluator()
    {
        var path = string.IsNullOrEmpty(_settingsService.RgblCalibrationJsonPath)
            ? Path.Combine(AppContext.BaseDirectory, "rgbl_calibration.json")
            : _settingsService.RgblCalibrationJsonPath;
        _rgblEvaluator = RgblCurveEvaluator.Load(path);

        // Immediately re-evaluate with the last known ambient so the new curves
        // are applied to the screen without waiting for the next sensor reading.
        if (_rgblEvaluator is { } evaluator)
        {
            var (r, g, b) = evaluator.Evaluate(_lastAmbientPct);
            Dispatcher.UIThread.Post(() =>
            {
                LatestRgblR = r;
                LatestRgblG = g;
                LatestRgblB = b;
            });
        }
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
        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Query 1 — Win32_SerialPort: the most direct source for serial COM ports.
        // Returns DeviceID = "COM3", "COM5", etc. for everything Windows classifies
        // as a serial port, including USB CDC/VCP devices.
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_SerialPort");
            foreach (ManagementObject obj in searcher.Get())
            {
                var deviceId = obj["DeviceID"]?.ToString();
                if (!string.IsNullOrEmpty(deviceId))
                    ports.Add(deviceId);
            }
        }
        catch { }

        // Query 2 — Win32_PnPEntity: broader net that catches CDC devices whose
        // driver does not register in Win32_SerialPort (e.g. some composite USB
        // devices or non-standard CDC implementations). Friendly name contains "(COMx)".
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"
            );
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                if (name is null)
                    continue;
                var match = Regex.Match(name, @"\(COM(\d+)\)");
                if (match.Success)
                    ports.Add($"COM{match.Groups[1].Value}");
            }
        }
        catch { }

        return [.. ports.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Opens the serial port and starts periodic sensor reads.
    /// Safe to call multiple times — stops any previous session first.
    /// Also reloads the RGBL curve evaluator from the current settings path.
    /// </summary>
    public void Start()
    {
        Stop();
        ReloadRgblEvaluator();

        if (string.IsNullOrWhiteSpace(_settingsService.UsbPortName))
            return; // No port configured yet — wait for auto-detect

        try
        {
            _port = new SerialPort(_settingsService.UsbPortName, _settingsService.UsbBaudRate)
            {
                ReadTimeout = 2000,
                WriteTimeout = 1000,
                DtrEnable = true,  // Pico firmware ignores commands until DTR=HIGH
            };
            _port.Open();
            if (Dispatcher.UIThread.CheckAccess())
                IsConnected = true;
            else
                Dispatcher.UIThread.Post(() => IsConnected = true);
        }
        catch
        {
            if (Dispatcher.UIThread.CheckAccess())
                IsConnected = false;
            else
                Dispatcher.UIThread.Post(() => IsConnected = false);
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

        // When called from the UI thread (e.g. Start() calls Stop() first) set the property
        // synchronously so a later queued Post cannot overwrite it after Start() sets true.
        if (Dispatcher.UIThread.CheckAccess())
            IsConnected = false;
        else
            Dispatcher.UIThread.Post(() => IsConnected = false);
    }

    private void ScheduleNextRead(TimeSpan? delay = null)
    {
        TimeSpan interval;
        if (delay.HasValue)
        {
            interval = delay.Value;
        }
        else
        {
            var minutes = _settingsService.UsbReadIntervalMinutes;
            if (minutes < 1.0)
            {
                // Sub-minute mode: fractional minutes → seconds
                var secs = Math.Clamp((int)Math.Round(minutes * 60), 1, 59);
                interval = TimeSpan.FromSeconds(secs);
            }
            else
            {
                interval = TimeSpan.FromMinutes(Math.Max(1, minutes));
            }
        }

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
    /// Returns true when the KIMSIN response identifies our PiColor firmware.
    /// Three cases are accepted:
    ///   1. Firmware replies with a line containing "IDENTITY=PICOM_" (current protocol).
    ///      The full response is a semicolon-delimited string, e.g.:
    ///      "1685106;4;0;0;0;0;IDENTITY=PICOM_V1"
    ///   2. Firmware does not implement KIMSIN and replies with its help/command
    ///      list that mentions the known read commands (OKU + RAW).  This handles
    ///      existing devices without requiring a firmware update.
    ///   3. Firmware responds to any command with sensor data (dual-line format).
    /// </summary>
    private static bool IsKnownFirmware(string response) =>
        response.Contains(PicoIdentityPrefix, StringComparison.OrdinalIgnoreCase) ||
        (response.Contains("OKU", StringComparison.OrdinalIgnoreCase) &&
         response.Contains("RAW", StringComparison.OrdinalIgnoreCase)) ||
        TryParseDualLine(response, out _); // firmware responds to any command with sensor data

    /// <summary>
    /// Reads one response line from the serial port, handling \r\n, \n-only, and \r-only
    /// line terminators. SerialPort.ReadLine() only handles \n; firmware that terminates
    /// with bare \r would cause ReadLine() to hang until timeout.
    /// </summary>
    private static string ReadResponseLine(SerialPort port)
    {
        var sb = new System.Text.StringBuilder();
        var deadline = Environment.TickCount64 + port.ReadTimeout;
        while (Environment.TickCount64 < deadline)
        {
            int b;
            try { b = port.ReadByte(); }
            catch (TimeoutException) { break; }

            var ch = (char)b;
            if (ch == '\n')
                break; // end of line (\r\n or bare \n)
            if (ch == '\r')
            {
                // Peek: if next char is \n, consume it; then stop.
                // If no \n follows within a short window, stop anyway.
                try
                {
                    var saved = port.ReadTimeout;
                    port.ReadTimeout = 50;
                    try { var next = (char)port.ReadByte(); if (next != '\n') sb.Append(next); }
                    catch (TimeoutException) { }
                    port.ReadTimeout = saved;
                }
                catch { }
                break;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns the current command string to send to the sensor (e.g. "OKU_15").
    /// </summary>
    private string BuildReadCommand()
    {
        var prefix = string.IsNullOrWhiteSpace(_settingsService.UsbReadCommand)
            ? "OKU"
            : _settingsService.UsbReadCommand.Trim();
        var minutes = _settingsService.UsbReadIntervalMinutes;
        if (minutes < 1.0)
        {
            // Sub-minute mode: OKU_S{seconds}
            var secs = Math.Clamp((int)Math.Round(minutes * 60), 1, 59);
            return $"{prefix}_S{secs}";
        }
        var intervalMin = (int)Math.Max(1, minutes);
        return $"{prefix}_{intervalMin}";
    }

    private void PerformRead()
    {
        if (_port is not { IsOpen: true })
            return;

        _portLock.Wait();
        try
        {
            var cmd = BuildReadCommand();
            _port.WriteLine(cmd);
            var response = ReadResponseLine(_port).Trim();
            ParseAndDispatch(response, cmd);
        }
        catch
        {
            // On error, mark as disconnected and let the next scheduled read try again.
            Dispatcher.UIThread.Post(() => IsConnected = false);
        }
        finally
        {
            _portLock.Release();
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

                // WMI scan: Device Manager view — most comprehensive source.
                var wmiPorts = GetPortNamesViaWmi();

                // "Priority" ports are those positively identified by registry or WMI.
                // If these fail to open we report it explicitly; it means the device was
                // seen by the OS but is momentarily unavailable (e.g. re-enumerating).
                var priorityPorts = new HashSet<string>(
                    picoCandidates.Concat(wmiPorts),
                    StringComparer.OrdinalIgnoreCase
                );

                // Diagnostic header: show what each discovery source found.
                var regInfo = picoCandidates.Length > 0
                    ? string.Join(", ", picoCandidates)
                    : "—";
                var wmiInfo = wmiPorts.Length > 0
                    ? string.Join(", ", wmiPorts)
                    : "—";
                triedPorts.Add(("[ Keşif ]", $"Registry(VID_2E8A): {regInfo} | WMI: {wmiInfo}"));

                // Candidate list priority:
                //  1. Registry-identified active Pico ports (instant, most likely hit)
                //  2. WMI ports — Device Manager view, finds hub/USB-C connected devices
                //  3. Brute-force COM1-COM99 as final safety net
                var candidates = picoCandidates
                    .Concat(wmiPorts)
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
                            ReadTimeout = 3000,
                            WriteTimeout = 1000,
                            // Must be set explicitly — SerialPort default is false.
                            // Many Pico firmwares ignore incoming data until DTR=HIGH
                            // (signals "host terminal connected"). Without this the
                            // firmware never responds and every port times out.
                            DtrEnable = true,
                        };
                        p.Open();
                        // After the DTR rising edge some firmwares do a soft-reset.
                        // Priority ports (registry/WMI confirmed) get a longer settle wait.
                        System.Threading.Thread.Sleep(priorityPorts.Contains(portName) ? 1500 : 300);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        triedPorts.Add((portName, "✗ Port meşgul — Arduino IDE veya başka bir uygulama bu portu açık tutuyor"));
                        p?.Dispose();
                        continue;
                    }
                    catch (IOException)
                    {
                        // Priority ports (WMI/registry identified) failing to open is
                        // diagnostic information — the OS knows the device but it is
                        // temporarily unavailable (mid-reset / re-enumeration).
                        // Brute-force ports are silently skipped to keep output clean.
                        if (priorityPorts.Contains(portName))
                            triedPorts.Add((portName, "✗ Port açılamadı — cihaz yeniden başlatılıyor ya da sürücü henüz hazır değil"));
                        p?.Dispose();
                        continue;
                    }

                    // Port opened — query device, then close regardless of outcome.
                    try
                    {
                        p.DiscardInBuffer();

                        p.WriteLine(KimsinCommand);
                        var identity = ReadResponseLine(p).Trim();
                        if (!IsKnownFirmware(identity))
                        {
                            triedPorts.Add((portName, $"✗ Yabancı cihaz: '{identity}'"));
                            continue;
                        }

                        // If KIMSIN already returned sensor data, reuse it; otherwise request a reading.
                        string raw;
                        if (TryParseDualLine(identity, out _))
                        {
                            raw = identity;
                        }
                        else
                        {
                            p.WriteLine(InstantReadCommand);
                            raw = ReadResponseLine(p).Trim();
                        }

                        if (TryParseDualLine(raw, out _))
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
            DtrEnable = true,  // Pico firmware ignores commands until DTR=HIGH ("host connected")
        };

        // Pause the background reading loop while the test occupies the port.
        if (useExisting)
        {
            _readTimerRegistration?.Dispose();
            _readTimerRegistration = null;
        }

        // Wait for any in-progress PerformRead to finish before touching the port.
        _portLock.Wait();

        try
        {
            var port = useExisting ? _port! : testPort!;
            if (!useExisting)
            {
                port.Open();
                // Settle wait: give firmware time to boot if it reset on connect.
                System.Threading.Thread.Sleep(2500);
            }

            // Discard any buffered data before sending commands.
            port.DiscardInBuffer();

            // Step 1: Verify device identity.
            // Retry up to 3 times: firmware may still be booting after DTR rising edge.
            string identity = "";
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                {
                    System.Threading.Thread.Sleep(1000);
                    port.DiscardInBuffer();
                }
                port.WriteLine(KimsinCommand);
                identity = ReadResponseLine(port).Trim();
                if (IsKnownFirmware(identity) || !string.IsNullOrEmpty(identity))
                    break;
            }
            if (!IsKnownFirmware(identity))
            {
                var badResult = new ConnectionTestResult(portName, baud, KimsinCommand, false, identity,
                    $"Kimlik doğrulanamadı — beklenen: '...{PicoIdentityPrefix}...' veya firmware yardım mesajı, gelen: '{identity}'");
                Dispatcher.UIThread.Post(() =>
                {
                    IsConnected = false;
                    IsTestingConnection = false;
                    ConnectionTestMessage = "✗ PiColor cihazı değil";
                });
                return badResult;
            }

            // Step 2: Request an instant reading to confirm the sensor works.
            // If KIMSIN already returned sensor data, reuse it to avoid a second round-trip.
            string raw;
            if (TryParseDualLine(identity, out _))
            {
                raw = identity;
            }
            else
            {
                port.WriteLine(InstantReadCommand);
                raw = ReadResponseLine(port).Trim();
            }
            var ok = TryParseDualLine(raw, out _);

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
            _portLock.Release();

            if (!useExisting)
            {
                try { testPort?.Close(); } catch { }
                testPort?.Dispose();
            }
            else if (_port is { IsOpen: true } && _readTimerRegistration is null)
            {
                // Resume the background reading loop that was paused for the test.
                ScheduleNextRead(delay: TimeSpan.FromSeconds(2));
            }
        }
    }

    /// <summary>
    /// Parses a firmware output line into a <see cref="DualReading"/>.
    /// Two formats are supported:
    ///   Compact (type=1): timestamp;1;mode;proc_r;proc_g;proc_b          (6 fields)
    ///   Dual    (type=6): timestamp;6;mode;raw_r;raw_g;raw_b;raw_c;proc_r;proc_g;proc_b[;meta]
    /// Returns false when the line is malformed or has too few fields.
    /// </summary>
    private static bool TryParseDualLine(string line, out DualReading reading)
    {
        reading = default;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var parts = line.Split(';');
        if (parts.Length < 6)
            return false;

        // Compact format: timestamp;1;mode;proc_r;proc_g;proc_b
        if (parts[1] == "1")
        {
            if (!float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var procR) ||
                !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var procG) ||
                !float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var procB))
                return false;

            reading = new DualReading(0, 0, 0, 0, procR, procG, procB);
            return true;
        }

        // Dual format: timestamp;6;mode;raw_r;raw_g;raw_b;raw_c;proc_r;proc_g;proc_b[;meta]
        if (parts.Length < 10)
            return false;

        if (!ushort.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawR) ||
            !ushort.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawG) ||
            !ushort.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawB) ||
            !ushort.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawC) ||
            !float.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var procR2) ||
            !float.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var procG2) ||
            !float.TryParse(parts[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var procB2))
            return false;

        reading = new DualReading(rawR, rawG, rawB, rawC, procR2, procG2, procB2);
        return true;
    }

    private void ParseAndDispatch(string response, string cmd = "—")
    {
        if (!TryParseDualLine(response, out var dr))
            return;

        // CCT — two variants: raw uint16 counts and firmware-processed 0-100 floats.
        var cctRaw  = ComputeCct(dr.RawR,  dr.RawG,  dr.RawB);
        var cctProc = ComputeCct(dr.ProcR, dr.ProcG, dr.ProcB);

        // Existing luminance pipeline (unchanged): uses CIE-Y from raw RGB.
        var rawY = 0.2126 * dr.RawR + 0.7152 * dr.RawG + 0.0722 * dr.RawB;
        var luminance = ComputeLuminance(rawY);
        var (rBias, gBias, bBias, lBias) = InterpolateBiases(rawY);

        // Ambient percentage — X input for all RGBL curves.
        // When InjectSimulatedReading provides an explicit ambient override, use it directly
        // so the user can test any X position on the curves without affecting _peakRawC.
        double ambientPct;
        if (_simulatedAmbientOverride.HasValue)
        {
            ambientPct = Math.Clamp(_simulatedAmbientOverride.Value, 0, 100);
            _simulatedAmbientOverride = null;
        }
        else
        {
            if (dr.RawC > _peakRawC) _peakRawC = dr.RawC;
            ambientPct = Math.Clamp(dr.RawC / _peakRawC * 100.0, 0, 100);
        }
        _lastAmbientPct = ambientPct;

        // RGBL curve evaluation — only when a calibration JSON is loaded.
        double rgblR = LatestRgblR, rgblG = LatestRgblG, rgblB = LatestRgblB;
        if (_rgblEvaluator is not null)
            (rgblR, rgblG, rgblB) = _rgblEvaluator.Evaluate(ambientPct);

        var rawText = string.Create(
            CultureInfo.InvariantCulture,
            $"R:{dr.RawR}  G:{dr.RawG}  B:{dr.RawB}  C:{dr.RawC}  Amb:{ambientPct:F1}%  pR:{dr.ProcR:F1}  pG:{dr.ProcG:F1}  pB:{dr.ProcB:F1}"
        );
        var timeText = DateTime.Now.ToString("HH:mm:ss");

        Dispatcher.UIThread.Post(() =>
        {
            LatestCct = cctRaw;
            LatestCctRaw = cctRaw;
            LatestCctProc = cctProc;
            LatestLuminance = luminance;
            LatestRawY = rawY;
            LatestRBias = rBias;
            LatestGBias = gBias;
            LatestBBias = bBias;
            LatestLBias = lBias;
            LatestRgblR = rgblR;
            LatestRgblG = rgblG;
            LatestRgblB = rgblB;
            LastRawReading = rawText;
            LastReadTime = timeText;
            LastReadCommand = cmd;
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
    /// Maps a CIE-Y sensor value to a [0.1, 1.0] luminance fraction.
    /// When calibration is enabled and points are defined, uses piecewise
    /// linear interpolation over the user-defined curve.
    /// Falls back to adaptive peak scaling otherwise.
    /// </summary>
    private double ComputeLuminance(double rawY)
    {
        // Expand adaptive peak (used as fallback when calibration is off).
        if (rawY > _peakRawY)
            _peakRawY = rawY;

        if (_settingsService.IsUsbCalibrationEnabled)
        {
            var points = _settingsService.UsbCalibrationPoints;
            if (points is { Count: >= 2 })
                return InterpolateCalibration(rawY, points);
        }

        return Math.Clamp(rawY / _peakRawY, 0.1, 1.0);
    }

    /// <summary>
    /// Piecewise linear interpolation over the calibration curve.
    /// Returns brightness fraction [0.0, 1.0].
    /// </summary>
    private static double InterpolateCalibration(
        double rawY,
        IReadOnlyList<UsbCalibrationPoint> points
    )
    {
        var sorted = points.OrderBy(p => p.RawY).ToList();

        // Clamp beyond the defined range.
        if (rawY <= sorted[0].RawY)
            return Math.Clamp(sorted[0].BrightnessPercent / 100.0, 0.0, 1.0);
        if (rawY >= sorted[^1].RawY)
            return Math.Clamp(sorted[^1].BrightnessPercent / 100.0, 0.0, 1.0);

        for (var i = 0; i < sorted.Count - 1; i++)
        {
            var lo = sorted[i];
            var hi = sorted[i + 1];
            if (rawY < lo.RawY || rawY > hi.RawY)
                continue;

            var t = (rawY - lo.RawY) / (hi.RawY - lo.RawY);
            var pct = lo.BrightnessPercent + t * (hi.BrightnessPercent - lo.BrightnessPercent);
            return Math.Clamp(pct / 100.0, 0.0, 1.0);
        }

        return Math.Clamp(sorted[^1].BrightnessPercent / 100.0, 0.0, 1.0);
    }

    /// <summary>
    /// Piecewise linear interpolation of R/G/B/L bias values over the calibration curve.
    /// Returns (0, 0, 0, 0) when calibration is disabled or fewer than 2 points are defined.
    /// </summary>
    private (double R, double G, double B, double L) InterpolateBiases(double rawY)
    {
        if (!_settingsService.IsUsbCalibrationEnabled)
            return (0, 0, 0, 0);

        var points = _settingsService.UsbCalibrationPoints;
        if (points is not { Count: >= 2 })
            return (0, 0, 0, 0);

        var sorted = points.OrderBy(p => p.RawY).ToList();

        if (rawY <= sorted[0].RawY)
            return (sorted[0].RBias, sorted[0].GBias, sorted[0].BBias, sorted[0].LBias);
        if (rawY >= sorted[^1].RawY)
            return (sorted[^1].RBias, sorted[^1].GBias, sorted[^1].BBias, sorted[^1].LBias);

        for (var i = 0; i < sorted.Count - 1; i++)
        {
            var lo = sorted[i];
            var hi = sorted[i + 1];
            if (rawY < lo.RawY || rawY > hi.RawY)
                continue;

            var t = (rawY - lo.RawY) / (hi.RawY - lo.RawY);
            return (
                lo.RBias + t * (hi.RBias - lo.RBias),
                lo.GBias + t * (hi.GBias - lo.GBias),
                lo.BBias + t * (hi.BBias - lo.BBias),
                lo.LBias + t * (hi.LBias - lo.LBias)
            );
        }

        return (sorted[^1].RBias, sorted[^1].GBias, sorted[^1].BBias, sorted[^1].LBias);
    }

    /// <summary>
    /// Injects a fake sensor reading directly — for testing without hardware.
    /// <paramref name="ambientPct"/> (0-100) sets the RGBL curve X-input directly,
    /// bypassing the adaptive rawC/_peakRawC scaling so any curve position can be tested.
    /// R/G/B are raw sensor counts (0-65535) used for CCT and luminance calculation.
    /// </summary>
    public void InjectSimulatedReading(double r, double g, double b, double ambientPct)
    {
        _simulatedAmbientOverride = Math.Clamp(ambientPct, 0, 100);
        var rawR = (ushort)Math.Clamp(r, 0, 65535);
        var rawG = (ushort)Math.Clamp(g, 0, 65535);
        var rawB = (ushort)Math.Clamp(b, 0, 65535);
        var rawC = (ushort)Math.Clamp(0.2126 * r + 0.7152 * g + 0.0722 * b, 0, 65535);
        var procR = (float)Math.Clamp(r / 655.35, 0, 100);
        var procG = (float)Math.Clamp(g / 655.35, 0, 100);
        var procB = (float)Math.Clamp(b / 655.35, 0, 100);
        var fakeResponse = string.Create(
            CultureInfo.InvariantCulture,
            $"0;6;0;{rawR};{rawG};{rawB};{rawC};{procR:F1};{procG:F1};{procB:F1}"
        );
        ParseAndDispatch(fakeResponse, "Simülasyon (Inject)");
    }

    /// <summary>
    /// Clears only the active simulation override so the next real (or periodic) read
    /// uses the sensor's actual ambient light. Does not touch display properties.
    /// </summary>
    public void ClearInjection()
    {
        _simulatedAmbientOverride = null;
    }

    /// <summary>
    /// Clears all injected/sensor state and resets observable properties to their defaults.
    /// Use this to cancel a simulated reading and return the display to its normal schedule.
    /// </summary>
    public void ResetSimulation()
    {
        _simulatedAmbientOverride = null;
        Dispatcher.UIThread.Post(() =>
        {
            LatestCct      = 6500;
            LatestCctRaw   = 6500;
            LatestCctProc  = 6500;
            LatestLuminance = 1.0;
            LatestRawY     = 0;
            LatestRBias    = 0;
            LatestGBias    = 0;
            LatestBBias    = 0;
            LatestLBias    = 0;
            LatestRgblR    = 50.0;
            LatestRgblG    = 50.0;
            LatestRgblB    = 50.0;
            LastRawReading  = "—";
            LastReadTime    = "—";
            LastReadCommand = "—";
            IsConnected     = false;
        });
    }

    // ── Calibration HTTP server ───────────────────────────────────────────────

    private void StartCalibrationHttpServer()
    {
        StopCalibrationHttpServer();

        // Find a free loopback port
        int port;
        using (var tmp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            tmp.Start();
            port = ((System.Net.IPEndPoint)tmp.LocalEndpoint).Port;
            tmp.Stop();
        }

        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        _httpListener = listener;
        _httpCts     = new System.Threading.CancellationTokenSource();
        CalibrationServerPort = port;

        _ = Task.Run(() => RunHttpLoopAsync(listener, _httpCts.Token));
    }

    private void StopCalibrationHttpServer()
    {
        _httpCts?.Cancel();
        _httpCts = null;
        try { _httpListener?.Stop(); } catch { }
        _httpListener = null;
        CalibrationServerPort = 0;
    }

    private async Task RunHttpLoopAsync(
        System.Net.HttpListener listener,
        System.Threading.CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            System.Net.HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleCalibrationRequestAsync(ctx, ct), ct);
        }
    }

    private async Task HandleCalibrationRequestAsync(
        System.Net.HttpListenerContext ctx,
        System.Threading.CancellationToken ct)
    {
        var req  = ctx.Request;
        var resp = ctx.Response;

        if (req.HttpMethod == "OPTIONS")
        {
            resp.StatusCode = 204;
            resp.Close();
            return;
        }

        var urlPath = req.Url?.AbsolutePath ?? "/";

        // ── POST /rgbl-save ───────────────────────────────────────────────────
        if (req.HttpMethod == "POST" && urlPath == "/rgbl-save")
        {
            try
            {
                using var sr  = new StreamReader(req.InputStream, req.ContentEncoding);
                var json      = await sr.ReadToEndAsync();
                var dest      = Path.Combine(AppContext.BaseDirectory, "rgbl_calibration.json");
                await File.WriteAllTextAsync(dest, json, ct);

                // Reload evaluator after file is written
                Dispatcher.UIThread.Post(ReloadRgblEvaluator);

                var ok = System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}");
                resp.ContentType    = "application/json; charset=utf-8";
                resp.ContentLength64 = ok.Length;
                await resp.OutputStream.WriteAsync(ok, ct);
            }
            catch (Exception ex)
            {
                resp.StatusCode = 500;
                var msg = ex.Message.Replace("\"", "'");
                var err = System.Text.Encoding.UTF8.GetBytes(
                    $"{{\"ok\":false,\"error\":\"{msg}\"}}");
                resp.ContentType    = "application/json; charset=utf-8";
                resp.ContentLength64 = err.Length;
                await resp.OutputStream.WriteAsync(err, ct);
            }
            finally { resp.Close(); }
            return;
        }

        // ── POST /rgbl-backup-pick  (native Avalonia save dialog) ────────────────
        if (req.HttpMethod == "POST" && urlPath == "/rgbl-backup-pick")
        {
            using var sr    = new StreamReader(req.InputStream, req.ContentEncoding);
            var jsonContent = await sr.ReadToEndAsync();

            string? selectedPath = null;
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var sp = (Application.Current?.ApplicationLifetime
                          as IClassicDesktopStyleApplicationLifetime)
                         ?.MainWindow?.StorageProvider;
                if (sp is null) return;

                var startDir = await sp.TryGetFolderFromPathAsync(
                    new Uri(AppContext.BaseDirectory));

                var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title                  = "Yedek Konumu Seç",
                    SuggestedFileName      = "rgbl_calibration_yedek.json",
                    SuggestedStartLocation = startDir,
                    FileTypeChoices        = [new FilePickerFileType("JSON Kalibrasyon")
                                             { Patterns = ["*.json"] }],
                });

                if (file is not null)
                    selectedPath = file.Path.LocalPath;
            });

            byte[] body;
            if (selectedPath is null)
            {
                body = System.Text.Encoding.UTF8.GetBytes(
                    "{\"ok\":false,\"cancelled\":true}");
            }
            else
            {
                await File.WriteAllTextAsync(selectedPath, jsonContent, ct);
                var fname = Path.GetFileName(selectedPath)
                                .Replace("\\", "\\\\").Replace("\"", "\\\"");
                body = System.Text.Encoding.UTF8.GetBytes(
                    $"{{\"ok\":true,\"filename\":\"{fname}\"}}");
            }
            resp.ContentType     = "application/json; charset=utf-8";
            resp.ContentLength64 = body.Length;
            await resp.OutputStream.WriteAsync(body, ct);
            resp.Close();
            return;
        }

        // ── GET static files (/  →  RGBL_curve_editor.html, /*.js  →  js files)
        if (req.HttpMethod == "GET")
        {
            var fileName = urlPath == "/" ? "RGBL_curve_editor.html" : urlPath.TrimStart('/');

            // Security: no path traversal, only .html/.js files
            if (!fileName.Contains('/') && !fileName.Contains('\\') &&
                (fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                 fileName.EndsWith(".js",   StringComparison.OrdinalIgnoreCase)))
            {
                var filePath = Path.Combine(AppContext.BaseDirectory, fileName);
                if (File.Exists(filePath))
                {
                    resp.ContentType = fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                        ? "text/html; charset=utf-8"
                        : "application/javascript; charset=utf-8";
                    var bytes = await File.ReadAllBytesAsync(filePath, ct);
                    resp.ContentLength64 = bytes.Length;
                    await resp.OutputStream.WriteAsync(bytes, ct);
                    resp.Close();
                    return;
                }
            }
        }

        resp.StatusCode = 404;
        resp.Close();
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        Stop();
        StopCalibrationHttpServer();
        _portLock.Dispose();
    }
}

/// <summary>
/// Parsed representation of a firmware output line.
/// Proc values are firmware-normalised 0-100 floats (always present).
/// Raw values are uint16 sensor counts; zero when the compact (type=1) format is used.
/// </summary>
internal readonly record struct DualReading(
    ushort RawR,
    ushort RawG,
    ushort RawB,
    ushort RawC,
    float  ProcR,
    float  ProcG,
    float  ProcB
);

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
