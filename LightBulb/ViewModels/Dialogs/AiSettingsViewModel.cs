using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

    public IRelayCommand       AddWaypointCommand  { get; }
    public IAsyncRelayCommand  TrainCommand        { get; }
    public IRelayCommand       ImportModelCommand  { get; }
    public IRelayCommand       ExportModelCommand  { get; }
    public IRelayCommand       ClearWaypointsCommand { get; }

    // ─────────────────────────────────────────────────────────────────────────

    public AiSettingsViewModel(SettingsService settings, UsbSensorService sensor)
    {
        _settings  = settings;
        _sensor    = sensor;
        _latitude  = settings.GeoLatitude;
        _longitude = settings.GeoLongitude;

        AddWaypointCommand    = new RelayCommand(AddCurrentWaypoint);
        TrainCommand          = new AsyncRelayCommand(TrainAsync,
            () => HasMinWaypoints && !IsTraining);
        ImportModelCommand    = new RelayCommand(ImportModel);
        ExportModelCommand    = new RelayCommand(ExportModel);
        ClearWaypointsCommand = new RelayCommand(ClearWaypoints);

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
        var inputs  = _sensor.GetCurrentNnInputs();
        var targets = new[]
        {
            _sensor.LatestRgblR,
            _sensor.LatestRgblG,
            _sensor.LatestRgblB,
            _sensor.LatestRgblL,
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

    // ── Import / Export ───────────────────────────────────────────────────────

    private async void ImportModel()
    {
        var topLevel = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt
                ? dt.MainWindow
                : null;
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Sinir Ağı Modeli İçe Aktar",
            AllowMultiple  = false,
            FileTypeFilter = [new FilePickerFileType("JSON Model") { Patterns = ["*.json"] }],
        });

        if (files.Count == 0) return;

        try
        {
            var json = await File.ReadAllTextAsync(files[0].Path.LocalPath);
            // Validate architecture via NnTrainer.FromJson
            var loaded = NnTrainer.FromJson(json);
            if (loaded is null) { TrainingStatus = "İçe aktarma başarısız: geçersiz model."; return; }

            File.WriteAllText(ModelPath, json);
            _trainer = loaded;
            _sensor.LoadNnEvaluator();
            HasNnModel     = true;
            TrainingStatus = "Model içe aktarıldı.";
        }
        catch (Exception ex)
        {
            TrainingStatus = $"İçe aktarma hatası: {ex.Message}";
        }
    }

    private async void ExportModel()
    {
        if (!File.Exists(ModelPath)) { TrainingStatus = "Dışa aktarılacak model yok."; return; }

        var topLevel = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt
                ? dt.MainWindow
                : null;
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title               = "Sinir Ağı Modelini Dışa Aktar",
            SuggestedFileName   = "nn_model.json",
            FileTypeChoices     = [new FilePickerFileType("JSON Model") { Patterns = ["*.json"] }],
        });

        if (file is null) return;

        try
        {
            var json = await File.ReadAllTextAsync(ModelPath);
            await File.WriteAllTextAsync(file.Path.LocalPath, json);
            TrainingStatus = "Model dışa aktarıldı.";
        }
        catch (Exception ex)
        {
            TrainingStatus = $"Dışa aktarma hatası: {ex.Message}";
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => _subs.Dispose();
}
