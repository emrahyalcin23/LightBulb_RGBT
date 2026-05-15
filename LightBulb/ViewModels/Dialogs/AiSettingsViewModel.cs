using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightBulb.Services;
using LightBulb.Utils;
using LightBulb.Utils.Extensions;

namespace LightBulb.ViewModels.Dialogs;

/// <summary>Single waypoint for NN training (captured sensor snapshot).</summary>
public partial class WaypointEntryViewModel : ObservableObject
{
    public double[] Inputs  { get; }
    public double[] Targets { get; }
    public string   Label   { get; }

    public WaypointEntryViewModel(double[] inputs, double[] targets, string label)
    {
        Inputs  = inputs;
        Targets = targets;
        Label   = label;
    }

    public IRelayCommand? RemoveCommand { get; set; }
}

/// <summary>
/// ViewModel for the native AI Settings window.
/// Manages location, live NN I/O display, waypoints, training, and model import/export.
/// </summary>
public partial class AiSettingsViewModel : ObservableObject, IDisposable
{
    private static readonly string ModelPath =
        Path.Combine(AppContext.BaseDirectory, "nn_model.json");

    private readonly SettingsService  _settings;
    private readonly UsbSensorService _sensor;
    private readonly DisposableCollector _subs = new();

    private NnTrainer? _trainer;

    // ── Location ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    private double _latitude;

    [ObservableProperty]
    private double _longitude;

    partial void OnLatitudeChanged(double value)  => _settings.GeoLatitude  = value;
    partial void OnLongitudeChanged(double value) => _settings.GeoLongitude = value;

    // ── Live NN state ─────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _inputsText  = "—";

    [ObservableProperty]
    private string _outputsText = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NnModelStatusText))]
    private bool _hasNnModel;

    // ── Mode ──────────────────────────────────────────────────────────────────

    public bool IsNnModeActive
    {
        get => _settings.IsNnModeActive;
        set
        {
            _settings.IsNnModeActive = value;
            OnPropertyChanged();
        }
    }

    // ── Waypoints ─────────────────────────────────────────────────────────────

    public ObservableCollection<WaypointEntryViewModel> Waypoints { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrainCommand))]
    [NotifyPropertyChangedFor(nameof(HasWaypoints))]
    private int _waypointCount;

    public bool HasWaypoints    => WaypointCount > 0;
    private bool HasMinWaypoints => WaypointCount >= 2;

    public string NnModelStatusText => HasNnModel ? "Mevcut ✓" : "Yok";

    // ── Training ─────────────────────────────────────────────────────────────

    // Stored as double so Slider Value binding compiles cleanly; truncated to int at training time.
    [ObservableProperty]
    private double _trainingEpochs = 500;

    [ObservableProperty]
    private double _learningRate = 0.001;

    [ObservableProperty]
    private string _trainingStatus = "—";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrainCommand))]
    private bool _isTraining;

    // ── Commands ──────────────────────────────────────────────────────────────

    public IRelayCommand       AddWaypointCommand      { get; }
    public IRelayCommand       ImportWaypointsCommand  { get; }
    public IRelayCommand       ExportWaypointsCommand  { get; }
    public IRelayCommand       ClearWaypointsCommand   { get; }
    public IAsyncRelayCommand  TrainCommand            { get; }
    public IRelayCommand       ImportModelCommand      { get; }
    public IRelayCommand       ExportModelCommand      { get; }

    // ─────────────────────────────────────────────────────────────────────────

    public AiSettingsViewModel(SettingsService settings, UsbSensorService sensor)
    {
        _settings  = settings;
        _sensor    = sensor;
        _latitude  = settings.GeoLatitude;
        _longitude = settings.GeoLongitude;

        AddWaypointCommand     = new RelayCommand(AddCurrentWaypoint);
        ImportWaypointsCommand = new RelayCommand(ImportWaypoints);
        ExportWaypointsCommand = new RelayCommand(ExportWaypoints);
        ClearWaypointsCommand  = new RelayCommand(ClearWaypoints);
        TrainCommand           = new AsyncRelayCommand(TrainAsync,
            () => HasMinWaypoints && !IsTraining);
        ImportModelCommand     = new RelayCommand(ImportModel);
        ExportModelCommand     = new RelayCommand(ExportModel);

        _subs.Add(_sensor.WatchAllProperties(RefreshLiveState));
        RefreshLiveState();
    }

    // ── Live state refresh ────────────────────────────────────────────────────

    private void RefreshLiveState()
    {
        HasNnModel = File.Exists(ModelPath);

        var inp = _sensor.GetCurrentNnInputs();
        var now = DateTime.Now;
        InputsText = string.Create(CultureInfo.InvariantCulture,
            $"lat={inp[0]*90:F1}°  " +
            $"lon∠sin={inp[1]:F2}/cos={inp[2]:F2}\n" +
            $"doy∠sin={inp[3]:F2}/cos={inp[4]:F2}  " +
            $"h∠sin={inp[5]:F2}/cos={inp[6]:F2}\n" +
            $"procR={inp[7]*100:F1}%  procG={inp[8]*100:F1}%  procB={inp[9]*100:F1}%\n" +
            $"ambient={inp[10]*100:F1}%  [{now:HH:mm:ss}]");

        var pred = _sensor.GetCurrentNnPrediction();
        OutputsText = pred is { } p
            ? string.Create(CultureInfo.InvariantCulture,
                $"R={p.R:F1}%  G={p.G:F1}%  B={p.B:F1}%  L={p.L:F1}%")
            : "(model yok)";
    }

    // ── Add waypoint ──────────────────────────────────────────────────────────

    private void AddCurrentWaypoint()
    {
        var inputs = _sensor.GetCurrentNnInputs();

        // Use curve evaluator outputs as training targets regardless of current mode.
        // LatestRawCurveR/G/B/L are always the curve-based pre-norm values;
        // applying the same post-norm clamp gives what the curve path would output.
        double outMin = _settings.UsbOutputMin;
        double outMax = _settings.UsbOutputMax;
        double Clamp(double v) => Math.Clamp(v, outMin, outMax);

        var targets = new[]
        {
            Clamp(_sensor.LatestRawCurveR),
            Clamp(_sensor.LatestRawCurveG),
            Clamp(_sensor.LatestRawCurveB),
            Clamp(_sensor.LatestRawCurveL),
        };

        var label = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss}  " +
            $"R={targets[0]:F0}% G={targets[1]:F0}% B={targets[2]:F0}% L={targets[3]:F0}%  " +
            $"amb={inputs[10]*100:F0}%");

        var entry = new WaypointEntryViewModel(inputs, targets, label);
        entry.RemoveCommand = new RelayCommand(() =>
        {
            Waypoints.Remove(entry);
            WaypointCount = Waypoints.Count;
        });

        Waypoints.Add(entry);
        WaypointCount = Waypoints.Count;
    }

    // ── Clear waypoints ───────────────────────────────────────────────────────

    private void ClearWaypoints()
    {
        Waypoints.Clear();
        WaypointCount = 0;
    }

    // ── Waypoint import / export ──────────────────────────────────────────────

    private async void ExportWaypoints()
    {
        if (Waypoints.Count == 0) { TrainingStatus = "Dışa aktarılacak waypoint yok."; return; }

        var file = await PickSaveFile("Eğitim Noktalarını Dışa Aktar", "waypoints.json");
        if (file is null) return;

        try
        {
            using var ms  = new MemoryStream();
            using var w   = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });
            w.WriteStartArray();
            foreach (var wp in Waypoints)
            {
                w.WriteStartObject();
                w.WriteString("label", wp.Label);
                w.WritePropertyName("inputs");
                w.WriteStartArray();
                foreach (var v in wp.Inputs) w.WriteNumberValue(v);
                w.WriteEndArray();
                w.WritePropertyName("targets");
                w.WriteStartArray();
                foreach (var v in wp.Targets) w.WriteNumberValue(v);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.Flush();
            await File.WriteAllBytesAsync(file, ms.ToArray());
            TrainingStatus = $"Waypoints dışa aktarıldı ({Waypoints.Count} nokta).";
        }
        catch (Exception ex) { TrainingStatus = $"Dışa aktarma hatası: {ex.Message}"; }
    }

    private async void ImportWaypoints()
    {
        var path = await PickOpenFile("Eğitim Noktalarını İçe Aktar");
        if (path is null) return;

        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            using var doc = JsonDocument.Parse(bytes);
            int added = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var label   = el.GetProperty("label").GetString() ?? "?";
                var inArr   = el.GetProperty("inputs");
                var tgtArr  = el.GetProperty("targets");

                if (inArr.GetArrayLength() != 11 || tgtArr.GetArrayLength() != 4) continue;

                var inputs  = new double[11];
                var targets = new double[4];
                for (int i = 0; i < 11; i++) inputs[i]  = inArr[i].GetDouble();
                for (int i = 0; i < 4;  i++) targets[i] = tgtArr[i].GetDouble();

                var entry = new WaypointEntryViewModel(inputs, targets, label);
                entry.RemoveCommand = new RelayCommand(() =>
                {
                    Waypoints.Remove(entry);
                    WaypointCount = Waypoints.Count;
                });
                Waypoints.Add(entry);
                added++;
            }
            WaypointCount  = Waypoints.Count;
            TrainingStatus = $"{added} waypoint içe aktarıldı.";
        }
        catch (Exception ex) { TrainingStatus = $"İçe aktarma hatası: {ex.Message}"; }
    }

    // ── Training ─────────────────────────────────────────────────────────────

    private async Task TrainAsync()
    {
        if (Waypoints.Count < 2) return;
        IsTraining      = true;
        TrainingStatus  = "Eğitiliyor…";

        var samples = new (double[], double[])[Waypoints.Count];
        for (int i = 0; i < Waypoints.Count; i++)
            samples[i] = (Waypoints[i].Inputs, Waypoints[i].Targets);

        var epochs = (int)TrainingEpochs;
        var lr     = LearningRate;

        // Run training off the UI thread
        double mse = await Task.Run(() =>
        {
            _trainer ??= new NnTrainer();
            return _trainer.Train(samples, epochs, lr);
        });

        // Save model on completion
        try
        {
            File.WriteAllText(ModelPath, _trainer!.ExportJson());
            _sensor.LoadNnEvaluator();
            HasNnModel     = true;
            TrainingStatus = string.Create(CultureInfo.InvariantCulture,
                $"Tamamlandı  MSE={mse:F4}  ({epochs} epoch, lr={lr})");
        }
        catch (Exception ex)
        {
            TrainingStatus = $"Kaydetme hatası: {ex.Message}";
        }
        finally
        {
            IsTraining = false;
        }
    }

    // ── Model import / export ─────────────────────────────────────────────────

    private async void ImportModel()
    {
        var path = await PickOpenFile("Sinir Ağı Modeli İçe Aktar");
        if (path is null) return;

        try
        {
            var json   = await File.ReadAllTextAsync(path);
            var loaded = NnTrainer.FromJson(json);
            if (loaded is null) { TrainingStatus = "İçe aktarma başarısız: geçersiz model."; return; }

            File.WriteAllText(ModelPath, json);
            _trainer       = loaded;
            _sensor.LoadNnEvaluator();
            HasNnModel     = true;
            TrainingStatus = "Model içe aktarıldı.";
        }
        catch (Exception ex) { TrainingStatus = $"İçe aktarma hatası: {ex.Message}"; }
    }

    private async void ExportModel()
    {
        if (!File.Exists(ModelPath)) { TrainingStatus = "Dışa aktarılacak model yok."; return; }

        var dest = await PickSaveFile("Sinir Ağı Modelini Dışa Aktar", "nn_model.json");
        if (dest is null) return;

        try
        {
            var json = await File.ReadAllTextAsync(ModelPath);
            await File.WriteAllTextAsync(dest, json);
            TrainingStatus = "Model dışa aktarıldı.";
        }
        catch (Exception ex) { TrainingStatus = $"Dışa aktarma hatası: {ex.Message}"; }
    }

    // ── File picker helpers ───────────────────────────────────────────────────

    private static Avalonia.Controls.Window? GetMainWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt
                ? dt.MainWindow : null;

    private static async Task<string?> PickOpenFile(string title)
    {
        var win = GetMainWindow();
        if (win is null) return null;
        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = title,
            AllowMultiple  = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private static async Task<string?> PickSaveFile(string title, string suggestedName)
    {
        var win = GetMainWindow();
        if (win is null) return null;
        var file = await win.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices   = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        return file?.Path.LocalPath;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => _subs.Dispose();
}
