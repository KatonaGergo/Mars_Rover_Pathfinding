using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using MarsRover.Core.Algorithm;
using MarsRover.Core.Models;
using MarsRover.Core.Simulation;
using MarsRover.Core.Utils;

namespace MarsRover.UI.ViewModels;

/// <summary>
/// Central ViewModel. Drives training, playback, and all dashboard data.
/// Bound to MainWindow.axaml via DataContext.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private enum TrainingReplayMode
    {
        Fast,
        StepByStep
    }

    private const int MinDurationHours = 24;
    private const int MaxDurationHours = 10000;
    private const double MinMapZoom = 1.0;
    private const double MaxMapZoom = 2.6;
    private const double MapZoomStep = 0.15;

    // Simulation state
    [ObservableProperty] private double? _durationHours  = 48;
    [ObservableProperty] private int    _trainingEpisodes = 2000;
    [ObservableProperty] private double _playbackSpeed   = 1.0;

    [ObservableProperty] private bool   _isTraining      = false;
    [ObservableProperty] private bool   _isRunning       = false;
    [ObservableProperty] private bool   _isPaused        = false;
    [ObservableProperty] private bool   _hasLog          = false;

    [ObservableProperty] private double _trainingProgress = 0;
    [ObservableProperty] private string _trainingStatus   = "Ready";

    // Current tick display
    [ObservableProperty] private string _currentTime      = "Sol 1, 0.0h";
    [ObservableProperty] private string _dayNightLabel    = "☀ Day";
    [ObservableProperty] private double _battery          = 100.0;
    [ObservableProperty] private int    _mineralsB        = 0;
    [ObservableProperty] private int    _mineralsY        = 0;
    [ObservableProperty] private int    _mineralsG        = 0;
    [ObservableProperty] private int    _totalMinerals    = 0;
    [ObservableProperty] private double _distanceTraveled = 0;
    [ObservableProperty] private string _lastAction       = "—";
    [ObservableProperty] private string _lastEvent        = "—";
    [ObservableProperty] private string _missionPhase      = "Collection";
    [ObservableProperty] private int    _roverX           = 0;
    [ObservableProperty] private int    _roverY           = 0;
    [ObservableProperty] private double _simProgress      = 0;
    [ObservableProperty] private bool   _isNight          = false;

    // Map + model path
    [ObservableProperty] private GameMap? _gameMap;
    public bool HasMapLoaded => GameMap != null;
    public bool HasReplayData => HasLog || (GhostTrail?.Count > 0);
    public bool CanRunFullExcavation => HasMapLoaded && !IsTraining && !IsFullExcavationRunning;
    public string PauseButtonContent => IsPaused ? "▶" : "⏸";
        [ObservableProperty] private bool _showMineralsFoundPrompt = false;
    [ObservableProperty] private int _mineralsFoundCountdown = 0;
    [ObservableProperty] private double _mapTileRevealProgress = 1.0;
    [ObservableProperty] private bool _isMapLoadScanOverlayVisible = false;
    [ObservableProperty] private int _mapLoadScannerJumpTrigger = 0;
    [ObservableProperty] private bool _isMapScannerTargetActive = false;
    [ObservableProperty] private bool _isMapScannerHudVisible = false;
    [ObservableProperty] private double _mapZoom = 1.0;
    [ObservableProperty] private double _mapCameraCenterX = 0.5;
    [ObservableProperty] private double _mapCameraCenterY = 0.5;
    [ObservableProperty] private double _mapZoomFxFocusX = 0.5;
    [ObservableProperty] private double _mapZoomFxFocusY = 0.5;
    [ObservableProperty] private int _mapZoomFxTrigger = 0;
    public bool IsMineralsFoundPromptVisible => ShowMineralsFoundPrompt;
    public string MineralsFoundCountdownText => Math.Max(MineralsFoundCountdown, 0).ToString();
    private string _modelPath = "q_table"; // base path — runner appends .weights.json / .meta.json
    private string _mapPath   = "";           // stored on map load — used for run log
    [ObservableProperty] private bool _hasSavedModel = false;
    [ObservableProperty] private bool _hasTrainingCompletedInSession = false;
    [ObservableProperty] private bool _isFullExcavationRunning = false;
    [ObservableProperty] private bool _showFullExcavationPrompt = false;
    [ObservableProperty] private string _fullExcavationSummary = string.Empty;
    private List<SimulationLogEntry>? _pendingFullExcavationLog;
    private bool _deferFullExcavationPromptUntilPlaybackEnds;
    private bool _modelLoadedManually;
    private bool _allowResumeThisSession;

    /// <summary>Set by MainWindow to open the native file picker.</summary>
    public Func<Task<string?>>? PickMapFileAsync { get; set; }
    public Func<Task<string?>>? PickModelFileAsync { get; set; }
    public Func<Task>? BackToMenuAsync { get; set; }

    // Log & playback
    private List<SimulationLogEntry> _log = new();
    private int                      _playbackIndex = 0;
    private int                      _lastMoveLogTick = -1; // avoid duplicate move logs on expanded sub-steps
    private DispatcherTimer          _playbackTimer;

    // Event log (last 20 lines)
    public ObservableCollection<string> EventLog      { get; } = new();
    public ObservableCollection<string> GhostEventLog { get; } = new();

    // Ghost replay (training visualisation)
    [ObservableProperty] private List<MarsRover.Core.Algorithm.StepRecord>? _ghostTrail;
    [ObservableProperty] private int  _ghostIndex   = 0;
    [ObservableProperty] private bool _isGhostMode  = false;
    [ObservableProperty] private string _ghostStatus = "";

        private DispatcherTimer _ghostTimer = new();
    private readonly DispatcherTimer _mineralsFoundCountdownTimer = new();
    private readonly DispatcherTimer _mineralsFoundHideTimer = new();
    private readonly DispatcherTimer _mapRevealTimer = new();
    private DateTime _mapRevealStartedAtUtc;
    private static readonly TimeSpan MineralsFoundHideDelay = TimeSpan.FromMilliseconds(820);
    private static readonly TimeSpan MapRevealDuration = TimeSpan.FromSeconds(5.8);
    private const double HideScannerHudAtRevealProgress = 0.05;

    // View toggle
    [ObservableProperty] private bool _showTrainingChart = false;
    [ObservableProperty] private bool _showGhostReplay   = false;
    [ObservableProperty] private bool _showWatchPrompt   = false;
    private bool _userWantsToWatch = false;

    // Map is visible when neither chart nor ghost is showing
    public bool ShowMapView => !ShowTrainingChart && !ShowGhostReplay;

    partial void OnShowTrainingChartChanged(bool value) => OnPropertyChanged(nameof(ShowMapView));
    partial void OnShowGhostReplayChanged(bool value) => OnPropertyChanged(nameof(ShowMapView));
    partial void OnShowMineralsFoundPromptChanged(bool value) => OnPropertyChanged(nameof(IsMineralsFoundPromptVisible));
    partial void OnMineralsFoundCountdownChanged(int value) => OnPropertyChanged(nameof(MineralsFoundCountdownText));
    partial void OnGameMapChanged(GameMap? value)
    {
        if (value == null)
        {
            StopMineralsFoundPrompt();
            StopMapLoadRevealSequence(resetRevealToFull: false);
            IsMapLoadScanOverlayVisible = false;
            IsMapScannerTargetActive = false;
            IsMapScannerHudVisible = false;
            MapLoadScannerJumpTrigger = 0;
        }
        else
        {
            IsMapLoadScanOverlayVisible = true;
            IsMapScannerHudVisible = true;
        }

        OnPropertyChanged(nameof(HasMapLoaded));
        OnPropertyChanged(nameof(CanRunFullExcavation));
    }
    partial void OnHasLogChanged(bool value)            => OnPropertyChanged(nameof(HasReplayData));
    partial void OnGhostTrailChanged(List<MarsRover.Core.Algorithm.StepRecord>? value)
        => OnPropertyChanged(nameof(HasReplayData));
    partial void OnIsTrainingChanged(bool value) => OnPropertyChanged(nameof(CanRunFullExcavation));
    partial void OnHasTrainingCompletedInSessionChanged(bool value) => OnPropertyChanged(nameof(CanRunFullExcavation));
    partial void OnIsFullExcavationRunningChanged(bool value) => OnPropertyChanged(nameof(CanRunFullExcavation));
    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(PauseButtonContent));
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(PauseButtonContent));
    partial void OnMapZoomChanged(double value)
    {
        double clamped = ClampMapZoom(value);
        if (Math.Abs(clamped - value) > double.Epsilon)
            MapZoom = clamped;
    }
    partial void OnMapCameraCenterXChanged(double value)
    {
        double clamped = ClampUnit(value);
        if (Math.Abs(clamped - value) > double.Epsilon)
            MapCameraCenterX = clamped;
    }
    partial void OnMapCameraCenterYChanged(double value)
    {
        double clamped = ClampUnit(value);
        if (Math.Abs(clamped - value) > double.Epsilon)
            MapCameraCenterY = clamped;
    }
    partial void OnDurationHoursChanged(double? value)
    {
        if (!value.HasValue)
            return;

        double rawValue = value.Value;
        if (double.IsNaN(rawValue) || double.IsInfinity(rawValue))
        {
            DurationHours = MinDurationHours;
            return;
        }

        double clamped = Math.Clamp(rawValue, MinDurationHours, MaxDurationHours);
        double rounded = Math.Round(clamped, MidpointRounding.AwayFromZero);
        if (Math.Abs(rounded - rawValue) > double.Epsilon)
            DurationHours = rounded;
    }

    // Simulation chart series (playback)
    public ISeries[] BatterySeries { get; }
    public ISeries[] MineralSeries { get; }
    public Axis[]    TimeAxis      { get; }

    private readonly ObservableCollection<LiveChartsCore.Defaults.ObservablePoint> _batteryPoints  = new();
    private readonly ObservableCollection<LiveChartsCore.Defaults.ObservablePoint> _mineralPoints  = new();

    // Training chart series (per-episode)
    public ISeries[] TrainingMineralSeries  { get; }
    public ISeries[] TrainingRewardSeries   { get; }
    public ISeries[] TrainingBatterySeries  { get; }
    public Axis[]    EpisodeAxis            { get; }
    public Axis[]    MineralYAxis           { get; }
    public Axis[]    RewardYAxis            { get; }
    public Axis[]    BatteryYAxis           { get; }

    private readonly ObservableCollection<LiveChartsCore.Defaults.ObservablePoint> _epMineralPoints = new();
    private readonly ObservableCollection<LiveChartsCore.Defaults.ObservablePoint> _epRewardPoints  = new();
    private readonly ObservableCollection<LiveChartsCore.Defaults.ObservablePoint> _epBatteryPoints = new();
    private readonly Queue<TrainingChartSample> _pendingTrainingChartSamples = new();
    private readonly Queue<MarsRover.Core.Algorithm.EpisodeSnapshot> _pendingGhostSnapshots = new();
    private string? _pendingCompletionStatus;
    private bool _pendingResetDisplayToStart;
    private TrainingReplayMode _trainingReplayMode = TrainingReplayMode.StepByStep;

    // Fast replay batches chart points to mimic the old, averaged preview mode.
    private const int FastReplayBatchSize = 4;
    private int _fastReplayBatchCount;
    private double _fastReplayMineralsSum;
    private double _fastReplayRewardSum;
    private double _fastReplayBatterySum;
    private TrainingChartSample? _fastReplayLastSample;
    private MarsRover.Core.Algorithm.EpisodeSnapshot? _fastPendingSnapshot;
    private bool _isNavigatingAway;
    private int _runGeneration;
    private int _replayGeneration;

    private readonly record struct TrainingChartSample(
        int Episode,
        int RunMinerals,
        double LastReward,
        double LastBattery,
        int TotalEps,
        double Epsilon,
        int BestMinerals,
        int BufferSize,
        int StatesKnown);

    public MainViewModel()
    {
        // Set up playback timer (interval adjusted by PlaybackSpeed)
        _playbackTimer          = new DispatcherTimer();
        _playbackTimer.Tick    += OnPlaybackTick;

        // Ghost replay timer — fast, plays one step per tick
        _ghostTimer          = new DispatcherTimer();
        _ghostTimer.Tick    += OnGhostTick;

                _mineralsFoundCountdownTimer.Interval = TimeSpan.FromSeconds(1);
        _mineralsFoundCountdownTimer.Tick += OnMineralsFoundCountdownTick;

        _mineralsFoundHideTimer.Interval = MineralsFoundHideDelay;
        _mineralsFoundHideTimer.Tick += (_, _) => StopMineralsFoundPrompt();

        _mapRevealTimer.Interval = TimeSpan.FromMilliseconds(16);
        _mapRevealTimer.Tick += OnMapRevealTick;

        UpdateTimerInterval();

        // Chart setup
        BatterySeries = new ISeries[]
        {
            new LineSeries<LiveChartsCore.Defaults.ObservablePoint>
            {
                Name   = "Battery",
                Values = _batteryPoints,
                Stroke = new SolidColorPaint(SKColor.Parse("#D4521A")) { StrokeThickness = 2 },
                Fill   = new SolidColorPaint(SKColor.Parse("#D4521A20")),
                GeometrySize = 0
            }
        };

        MineralSeries = new ISeries[]
        {
            new StepLineSeries<LiveChartsCore.Defaults.ObservablePoint>
            {
                Name   = "Minerals",
                Values = _mineralPoints,
                Stroke = new SolidColorPaint(SKColor.Parse("#3DBA5C")) { StrokeThickness = 2 },
                Fill   = new SolidColorPaint(SKColor.Parse("#3DBA5C20")),
                GeometrySize = 0
            }
        };

        TimeAxis = new Axis[]
        {
            new Axis { Name = "Tick", LabelsPaint = new SolidColorPaint(SKColor.Parse("#687080")) }
        };

        // Training chart: minerals collected per episode
        TrainingMineralSeries = new ISeries[]
        {
            new LineSeries<LiveChartsCore.Defaults.ObservablePoint>
            {
                Name         = "Minerals / Episode",
                Values       = _epMineralPoints,
                Stroke       = new SolidColorPaint(SKColor.Parse("#3DBA5C")) { StrokeThickness = 2 },
                Fill         = new SolidColorPaint(SKColor.Parse("#3DBA5C18")),
                GeometrySize = 0,
                LineSmoothness = 0.4
            }
        };

        // Training chart: raw total reward per episode
        TrainingRewardSeries = new ISeries[]
        {
            new LineSeries<LiveChartsCore.Defaults.ObservablePoint>
            {
                Name         = "Reward / Episode",
                Values       = _epRewardPoints,
                Stroke       = new SolidColorPaint(SKColor.Parse("#D4521A")) { StrokeThickness = 2 },
                Fill         = new SolidColorPaint(SKColor.Parse("#D4521A18")),
                GeometrySize = 0,
                LineSmoothness = 0.6
            }
        };

        // Training chart: battery at end of episode
        TrainingBatterySeries = new ISeries[]
        {
            new LineSeries<LiveChartsCore.Defaults.ObservablePoint>
            {
                Name           = "Battery / Episode",
                Values         = _epBatteryPoints,
                Stroke         = new SolidColorPaint(SKColor.Parse("#FFD089")) { StrokeThickness = 2 },
                Fill           = new SolidColorPaint(SKColor.Parse("#FFD08918")),
                GeometrySize   = 0,
                LineSmoothness = 0.4
            }
        };

        EpisodeAxis = new Axis[]
        {
            new Axis { Name = "Episode", LabelsPaint = new SolidColorPaint(SKColor.Parse("#687080")) }
        };

        MineralYAxis = new Axis[]
        {
            new Axis { Name = "Minerals", LabelsPaint = new SolidColorPaint(SKColor.Parse("#3DBA5C")),
                       MinLimit = 0 }
        };

        RewardYAxis = new Axis[]
        {
            new Axis { Name = "Reward", LabelsPaint = new SolidColorPaint(SKColor.Parse("#D4521A")) }
        };

        BatteryYAxis = new Axis[]
        {
            new Axis { Name = "Battery", LabelsPaint = new SolidColorPaint(SKColor.Parse("#FFD089")),
                       MinLimit = 0, MaxLimit = 100 }
        };

        // No map loaded on startup — user must click Load Map
        TrainingStatus = "No map loaded. Click 'LOAD MAP' to begin.";
    }

    // Commands

    [RelayCommand]
    private void ShowChart()
    {
        ShowTrainingChart = true;
        ShowGhostReplay   = false;
    }

    [RelayCommand]
    private void ShowMap()
    {
        ShowTrainingChart = false;
        ShowGhostReplay   = false;
    }

    [RelayCommand]
    private void ShowGhost()
    {
        ShowGhostReplay   = true;
        ShowTrainingChart = false;

        // If trail is loaded but timer not running, restart playback from
        // beginning so it can be watched on demand.
        if (GhostTrail != null && GhostTrail.Count > 0 && !_ghostTimer.IsEnabled)
        {
            GhostIndex     = 0;
            IsGhostMode    = true;
            GhostEventLog.Clear();
            _ghostTimer.Start();
        }
    }

    [RelayCommand]
    private void TrainAndRun()
    {
        if (GameMap == null)
        {
            TrainingStatus = "No map loaded. Click 'LOAD MAP' first.";
            return;
        }
        // Show replay-mode popup first — training starts after mode selection.
        ShowWatchPrompt = true;
    }

    [RelayCommand]
    private async Task StartFastReplay()
    {
        _userWantsToWatch   = true;
        _trainingReplayMode = TrainingReplayMode.Fast;
        ShowWatchPrompt     = false;
        await StartTraining();
    }

    [RelayCommand]
    private async Task StartNoReplay()
    {
        _userWantsToWatch = false;
        ShowWatchPrompt = false;
        await StartTraining();
    }

    [RelayCommand]
    private async Task StartStepByStepReplay()
    {
        _userWantsToWatch   = true;
        _trainingReplayMode = TrainingReplayMode.StepByStep;
        ShowWatchPrompt     = false;
        await StartTraining();
    }

    private async Task StartTraining()
    {
        if (GameMap == null) return;

        _isNavigatingAway = false;
        int runGeneration = ++_runGeneration;
        int durationHours = EnsureValidDurationHours();
        bool shouldResumeModel = _modelLoadedManually || _allowResumeThisSession;
        bool resumeFromDisk = shouldResumeModel
                              && File.Exists(SimulationRunner.ResolveModelPath(_modelPath) + ".qtable.json");

        IsTraining       = true;
        TrainingStatus   = resumeFromDisk
            ? "▶ Resuming Q-table training from saved model..."
            : "🧠 Training Hybrid Q-table agent...";
        TrainingProgress = 0;
        _pendingCompletionStatus = null;
        _pendingResetDisplayToStart = false;
        ShowFullExcavationPrompt = false;
        _pendingFullExcavationLog = null;

        // Clear previous training data
        _epMineralPoints.Clear();
        _epRewardPoints.Clear();
        _epBatteryPoints.Clear();
        _pendingTrainingChartSamples.Clear();
        _pendingGhostSnapshots.Clear();
        _ghostTimer.Stop();
        ResetFastReplayBatch();
        _fastPendingSnapshot = null;

        ShowGhostReplay   = false;
        ShowTrainingChart = false;

        var runner = new SimulationRunner(GameMap, durationHours, _modelPath);
        HasSavedModel = runner.HasSavedModel;
        var trainingOptions = TrainingProfileFactory.CreateOptions(TrainingProfileFactory.DefaultProfile) with
        {
            ResumeSavedModel = shouldResumeModel,
            CaptureProgressSnapshots = _userWantsToWatch
        };

        try
        {
            await Task.Run(() =>
            {
                var (training, log) = runner.TrainAndRun(
                    episodes: TrainingEpisodes,
                    options: trainingOptions,
                    onProgress: p =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_isNavigatingAway || runGeneration != _runGeneration) return;

                            if (!_userWantsToWatch)
                            {
                                TrainingProgress = p.PercentDone;
                                TrainingStatus   = FormatTrainingStatus(
                                    p.Episode, p.TotalEps, p.Epsilon,
                                    p.BestMinerals, p.BufferSize, p.StatesKnown);
                            }
                            else if (_trainingReplayMode == TrainingReplayMode.Fast)
                            {
                                TrainingProgress = p.PercentDone;
                                TrainingStatus   = FormatTrainingStatus(
                                    p.Episode, p.TotalEps, p.Epsilon,
                                    p.BestMinerals, p.BufferSize, p.StatesKnown);
                            }

                            HandleTrainingRunCompleted(p);
                        });
                    });

                Dispatcher.UIThread.Post(() =>
                {
                    if (_isNavigatingAway || runGeneration != _runGeneration) return;

                    if (_trainingReplayMode == TrainingReplayMode.Fast)
                        FlushFastReplayBatch();

                    // If the user is watching ghost replay, keep the queue alive
                    // so no episodes are dropped and charts remain in lockstep.
                    bool keepGhostQueue = _userWantsToWatch
                                          && _trainingReplayMode == TrainingReplayMode.StepByStep
                                          && (_ghostTimer.IsEnabled || _pendingGhostSnapshots.Count > 0);
                    if (!keepGhostQueue)
                    {
                        _ghostTimer.Stop();
                        _pendingGhostSnapshots.Clear();
                        FlushPendingTrainingChartSamples();
                    }

                    _log              = log;
                    _playbackIndex    = 0;
                    HasLog            = log.Count > 0;
                    if (!keepGhostQueue)
                    {
                        IsTraining      = false;
                        TrainingProgress = 100;
                        ShowGhostReplay = false;
                    }
                    ShowTrainingChart = false;  // return to map for playback
                    HasSavedModel    = true;
                    HasTrainingCompletedInSession = true;
                    _allowResumeThisSession = true;

                    // Save run log to results/
                    string? logPath = null;
                    if (log.Count > 0 && GameMap != null)
                        logPath = MarsRover.Core.Utils.MissionLogger.Save(
                            log, _mapPath, durationHours, TrainingEpisodes, _modelPath, GameMap);

                    string completionStatus = $"✅ Training complete — " +
                                              $"{training.BestMinerals} peak minerals | " +
                                              $"Model saved to {SimulationRunner.ResolveModelPath(_modelPath)}.qtable.json" +
                                              (logPath != null ? $" | Log → {logPath}" : "");

                    if (keepGhostQueue)
                    {
                        _pendingCompletionStatus = completionStatus;
                        _pendingResetDisplayToStart = true;
                    }
                    else
                    {
                        TrainingStatus = completionStatus;
                        ResetDisplayToStart();
                    }
                });
            });
        }
        catch (Exception ex)
        {
            if (_isNavigatingAway || runGeneration != _runGeneration) return;
            IsTraining     = false;
            string msg = ex.Message;
            string inner = ex.InnerException?.Message ?? string.Empty;
            bool schemaMismatch = msg.Contains("schema mismatch", StringComparison.OrdinalIgnoreCase)
                               || msg.Contains("Retraining is required", StringComparison.OrdinalIgnoreCase)
                               || inner.Contains("schema mismatch", StringComparison.OrdinalIgnoreCase)
                               || inner.Contains("Retraining is required", StringComparison.OrdinalIgnoreCase);

            if (schemaMismatch)
            {
                TrainingStatus = "Model schema mismatch detected. Click RESET MODEL, then train again.";
            }
            else
            {
                TrainingStatus = $"ERROR: {ex.GetType().Name}: {ex.Message}";
                if (ex.InnerException != null)
                    TrainingStatus += $" | Inner: {ex.InnerException.Message}";
            }
        }
    }

    [RelayCommand]
    private async Task SaveModel()
    {
        if (PickModelFileAsync == null) return;
        TrainingStatus = $"Model saved at: {SimulationRunner.ResolveModelPath(_modelPath)}.qtable.json";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task LoadModel()
    {
        if (PickModelFileAsync == null) return;
        try
        {
            var path = await PickModelFileAsync();
            if (path == null) return;
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                TrainingStatus = "Please select a .json model file.";
                return;
            }

            // Strip extensions — user may select .weights.json or base name
            _modelPath     = path.Replace(".qtable.json", "").Replace(".meta.json", "");
            HasSavedModel = File.Exists(SimulationRunner.ResolveModelPath(_modelPath) + ".qtable.json");
            _modelLoadedManually = HasSavedModel;
            if (HasSavedModel)
                _allowResumeThisSession = false;

            TrainingStatus = HasSavedModel
                ? $"✅ Model loaded: {System.IO.Path.GetFileName(_modelPath)} — " +
                  $"click Train & Run to fine-tune or Run to deploy."
                : $"⚠ No model weights found at {SimulationRunner.ResolveModelPath(_modelPath)}.qtable.json";
        }
        catch (Exception ex)
        {
            TrainingStatus = $"Failed to load model: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ResetModel()
    {
        try
        {
            var result = SimulationRunner.ResetSavedModel(_modelPath, archive: true);
            HasSavedModel = File.Exists(SimulationRunner.ResolveModelPath(_modelPath) + ".qtable.json");
            _modelLoadedManually = false;
            _allowResumeThisSession = false;
            HasTrainingCompletedInSession = false;
            if (!result.HadModel)
            {
                TrainingStatus = "No saved model found to reset.";
                return;
            }

            if (result.Archived)
                TrainingStatus = $"Model reset. Archived to {result.ArchiveDirectory}. Train again to rebuild.";
            else
                TrainingStatus = "Model reset. Train again to rebuild.";
        }
        catch (Exception ex)
        {
            TrainingStatus = $"Failed to reset model: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Play()
    {
        UpdateTimerInterval();

        if (ShowGhostReplay && GhostTrail != null && GhostTrail.Count > 0)
        {
            if (GhostIndex >= GhostTrail.Count)
            {
                if (_pendingGhostSnapshots.Count > 0)
                    StartNextGhostReplayFromQueue();
            }

            if (GhostTrail == null || GhostTrail.Count == 0 || GhostIndex >= GhostTrail.Count)
                return;

            _ghostTimer.Start();
            IsGhostMode = true;
            IsRunning = true;
            IsPaused = false;
            return;
        }

        if (!HasLog) return;

        _playbackTimer.Start();
        IsRunning = true;
        IsPaused  = false;
    }

    [RelayCommand]
    private void Pause()
    {
        if (_playbackTimer.IsEnabled || _ghostTimer.IsEnabled)
        {
            _playbackTimer.Stop();
            _ghostTimer.Stop();
            IsPaused = true;
            IsRunning = false;
            return;
        }

        if (HasReplayData)
            Play();
    }

    [RelayCommand]
    private void StepForward()
    {
        if (ShowGhostReplay && GhostTrail != null)
        {
            _ghostTimer.Stop();
            _playbackTimer.Stop();
            IsPaused = true;
            IsRunning = false;
            IsGhostMode = true;
            AdvanceGhostStep();
            return;
        }

        if (_playbackIndex < _log.Count)
        {
            _playbackTimer.Stop();
            ApplyLogEntry(_log[_playbackIndex++]);
            IsPaused = true;
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void StepBackward()
    {
        _playbackTimer.Stop();
        _ghostTimer.Stop();
        IsRunning = false;
        IsPaused = true;

        if (ShowGhostReplay && GhostTrail != null && GhostTrail.Count > 0)
        {
            if (GhostIndex > 0)
                GhostIndex--;
            IsGhostMode = true;
            return;
        }

        if (_log.Count == 0)
            return;

        RebuildPlaybackToIndex(Math.Max(0, _playbackIndex - 1));
    }

    [RelayCommand]
    private void Reset()
    {
        _playbackTimer.Stop();
        _ghostTimer.Stop();
        _playbackIndex = 0;
        IsRunning      = false;
        IsPaused       = true;
        RebuildPlaybackToIndex(0);
    }

    [RelayCommand]
    private void ZoomInMap()
    {
        AdjustMapZoomBySteps(1, 0.5, 0.5, anchorToFocus: true);
    }

    [RelayCommand]
    private void ZoomOutMap()
    {
        AdjustMapZoomBySteps(-1, 0.5, 0.5, anchorToFocus: true);
    }

    [RelayCommand]
    private void ResetMapZoom()
    {
        if (!HasMapLoaded) return;
        bool changed = Math.Abs(MapZoom - MinMapZoom) > double.Epsilon;
        MapZoom = MinMapZoom;
        if (changed)
            TriggerZoomFx(0.5, 0.5);
    }

    public void AdjustMapZoomFromWheel(double deltaY, double focusX, double focusY)
    {
        if (!HasMapLoaded || deltaY == 0) return;
        AdjustMapZoomBySteps(deltaY > 0 ? 1 : -1, focusX, focusY, anchorToFocus: true);
    }

    [RelayCommand]
    private async Task RunFullExcavation()
    {
        if (GameMap == null)
        {
            TrainingStatus = "Load a map before running full excavation.";
            return;
        }

        if (IsFullExcavationRunning)
            return;

        IsFullExcavationRunning = true;
        ShowFullExcavationPrompt = false;
        _pendingFullExcavationLog = null;
        _deferFullExcavationPromptUntilPlaybackEnds = false;

        int targetMinerals = GameMap.RemainingMinerals.Count;
        int startHours = EnsureValidDurationHours();
        int currentHours = startHours;
        const int maxAttempts = 12;
        const int maxHours = 100_000;
        string failureReason = "Full excavation target was not reached.";

        try
        {
            string modelFile = SimulationRunner.ResolveModelPath(_modelPath) + ".qtable.json";
            bool hasModelOnDisk = File.Exists(modelFile);
            HasSavedModel = hasModelOnDisk;
            if (!hasModelOnDisk)
            {
                // Full excavation can now run without manual training.
                // If there is no saved policy, run a short warmup so the rover
                // still starts from a competent model before the hour-window search.
                int warmupEpisodes = Math.Clamp(Math.Max(300, targetMinerals * 20), 300, 2000);
                TrainingStatus = $"No saved model found. Auto-training warmup ({warmupEpisodes} episodes) for full excavation...";
                var warmupRunner = new SimulationRunner(GameMap.Clone(), startHours, _modelPath);
                var warmupOptions = TrainingProfileFactory.CreateOptions(TrainingProfile.Balanced) with
                {
                    ResumeSavedModel = false,
                    CaptureProgressSnapshots = false,
                    ProfileName = "FullExcavationAutoWarmup"
                };
                int lastReportedEpisode = 0;
                await Task.Run(() => warmupRunner.Train(
                    episodes: warmupEpisodes,
                    onProgress: p =>
                    {
                        if (p.Episode != warmupEpisodes
                            && p.Episode - lastReportedEpisode < 25)
                            return;
                        lastReportedEpisode = p.Episode;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!IsFullExcavationRunning) return;
                            TrainingStatus =
                                $"Auto-training warmup... {p.Episode}/{warmupEpisodes} episodes | " +
                                $"best={p.BestMinerals}";
                        });
                    },
                    options: warmupOptions));
                HasSavedModel = warmupRunner.HasSavedModel;
                HasTrainingCompletedInSession = true;
            }

            TrainingStatus = $"Running full excavation search from {startHours}h mission window (best-effort fastest completion)...";

            for (int attempt = 1; attempt <= maxAttempts && currentHours <= maxHours; attempt++)
            {
                int hoursForAttempt = currentHours;
                var runner = new SimulationRunner(GameMap.Clone(), hoursForAttempt, _modelPath);
                var log = await Task.Run(() =>
                    runner.RunSimulation(missionEndMode: MissionEndMode.ContinueUntilDeadline));

                if (log.Count == 0)
                {
                    failureReason = $"Attempt {attempt}: simulation produced no log data.";
                    break;
                }

                // Surface movement early so Full Excavation visibly starts.
                if (attempt == 1)
                {
                    LoadReplayLogForPlayback(log, autoPlay: true);
                    TrainingStatus = $"Full excavation attempt {attempt} running ({hoursForAttempt}h window)...";
                }

                int completionIndex = log.FindIndex(e => e.TotalMinerals >= targetMinerals);
                if (completionIndex >= 0)
                {
                    var completionEntry = log[completionIndex];
                    _pendingFullExcavationLog = log.Take(completionIndex + 1).ToList();
                    LoadReplayLogForPlayback(_pendingFullExcavationLog, autoPlay: true);
                    FullExcavationSummary =
                        $"Full excavation finished in {completionEntry.Tick / 2.0:F1} hours " +
                        $"({completionEntry.Tick} ticks). Save this run to results/?";
                    _deferFullExcavationPromptUntilPlaybackEnds = true;
                    TrainingStatus = "Full excavation complete. Replaying winning run...";
                    return;
                }

                var final = log[^1];
                if (final.Battery <= 0)
                {
                    failureReason = $"Attempt {attempt}: rover battery depleted before full excavation.";
                    break;
                }

                currentHours = Math.Min(maxHours + 1, hoursForAttempt * 2);
            }

            TrainingStatus = $"Full excavation not achieved. {failureReason}";
            ResetAfterFullExcavationPrompt();
        }
        finally
        {
            IsFullExcavationRunning = false;
        }
    }

    [RelayCommand]
    private void ConfirmFullExcavationSave()
    {
        if (_pendingFullExcavationLog == null || _pendingFullExcavationLog.Count == 0)
        {
            ShowFullExcavationPrompt = false;
            return;
        }

        try
        {
            string path = SaveFullExcavationLog(_pendingFullExcavationLog);
            TrainingStatus = $"Full excavation run saved to {path}";
        }
        catch (Exception ex)
        {
            TrainingStatus = $"Failed to save full excavation run: {ex.Message}";
        }
        finally
        {
            ResetAfterFullExcavationPrompt();
        }
    }

    [RelayCommand]
    private void CancelFullExcavationSave()
    {
        TrainingStatus = "Full excavation result discarded.";
        ResetAfterFullExcavationPrompt();
    }

    [RelayCommand]
    private async Task LoadMap()
    {
        if (PickMapFileAsync == null) return;

        var path = await PickMapFileAsync();
        if (path == null) return; // user cancelled

        try
        {
            var loadedMap = MarsRover.Core.Simulation.GameMap.LoadFromFile(path);
            StopMineralsFoundPrompt();
            StopMapLoadRevealSequence(resetRevealToFull: false);
            _isNavigatingAway = true;
            _runGeneration++;
            _userWantsToWatch = false;
            ShowWatchPrompt = false;
            _pendingCompletionStatus = null;
            _pendingResetDisplayToStart = false;
            StopReplayProcesses(clearLog: true, clearTrainingQueues: true);
            IsTraining = false;
            TrainingProgress = 0;
            _modelLoadedManually = false;
            _allowResumeThisSession = false;
            HasTrainingCompletedInSession = false;
            ShowFullExcavationPrompt = false;
            _pendingFullExcavationLog = null;
            _deferFullExcavationPromptUntilPlaybackEnds = false;

            GameMap  = loadedMap;
            _mapPath = path;
            RoverX  = GameMap.StartX;
            RoverY  = GameMap.StartY;
            MapZoom = MinMapZoom;
            MapCameraCenterX = 0.5;
            MapCameraCenterY = 0.5;
            TrainingStatus = $"Map loaded — {System.IO.Path.GetFileName(path)} " +
                             $"| {GameMap.RemainingMinerals.Count} minerals";
            ResetDisplayToStart();
            MapTileRevealProgress = 0;
            IsMapScannerTargetActive = true;
            IsMapScannerHudVisible = true;
            MapLoadScannerJumpTrigger = 0;
            IsMapLoadScanOverlayVisible = true;
            MapLoadScannerJumpTrigger++;
            StartMineralsFoundPrompt();
            _isNavigatingAway = false;
        }
        catch (Exception ex)
        {
            TrainingStatus = $"Failed to load map: {ex.Message}";
            StopMineralsFoundPrompt();
            StopMapLoadRevealSequence(resetRevealToFull: false);
            _isNavigatingAway = false;
        }
    }

    // Playback timer

    [RelayCommand]
    private async Task BackToMenu()
    {
        _isNavigatingAway = true;
        _runGeneration++;
        _userWantsToWatch = false;
        ShowWatchPrompt = false;
        ShowFullExcavationPrompt = false;
        StopMineralsFoundPrompt();
        StopMapLoadRevealSequence(resetRevealToFull: true);
        _pendingCompletionStatus = null;
        _pendingResetDisplayToStart = false;
        _pendingFullExcavationLog = null;
        _deferFullExcavationPromptUntilPlaybackEnds = false;

        StopReplayProcesses(clearLog: true, clearTrainingQueues: true);
        IsTraining = false;
        TrainingProgress = 0;

        if (BackToMenuAsync != null)
            await BackToMenuAsync();
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        if (_playbackIndex >= _log.Count)
        {
            _playbackTimer.Stop();
            IsRunning = false;
            if (_deferFullExcavationPromptUntilPlaybackEnds && _pendingFullExcavationLog != null)
            {
                _deferFullExcavationPromptUntilPlaybackEnds = false;
                ShowFullExcavationPrompt = true;
                TrainingStatus = "Full excavation complete. Choose YES to save or NO to discard.";
                return;
            }
            TrainingStatus = "Simulation complete.";
            return;
        }

        ApplyLogEntry(_log[_playbackIndex++]);
    }

    private void ApplyLogEntry(SimulationLogEntry e)
    {
        bool isMove = e.Action.Type == MarsRover.Core.Models.RoverActionType.Move;
        string eventText = e.EventNote;

        // Multi-step moves are expanded into several playback entries that share
        // the same Tick. Log movement once at decision time (first sub-step).
        if (isMove)
        {
            if (e.Tick != _lastMoveLogTick)
            {
                _lastMoveLogTick = e.Tick;
                eventText = $"Move decision — {SpeedToUiLabel(e.Action.Speed)}";
            }
            else
            {
                eventText = string.Empty;
            }
        }

        CurrentTime      = e.TimeLabel;
        DayNightLabel    = e.DayNightLabel;
        Battery          = e.Battery;
        MineralsB        = e.MineralsB;
        MineralsY        = e.MineralsY;
        MineralsG        = e.MineralsG;
        TotalMinerals    = e.TotalMinerals;
        DistanceTraveled = e.DistanceTraveled;
        LastAction       = e.Action.ToString();
        if (!string.IsNullOrWhiteSpace(eventText))
            LastEvent = eventText;
        MissionPhase     = e.Phase;
        RoverX           = e.X;
        RoverY           = e.Y;
        IsNight          = !e.IsDay;
        SimProgress      = (double)_playbackIndex / _log.Count;

        // Charts
        _batteryPoints.Add(new(e.Tick, e.Battery));
        _mineralPoints.Add(new(e.Tick, e.TotalMinerals));

        // Event log with reward/punishment context
        string rewardTag = "";
        if (eventText.Contains("Collected"))      rewardTag = " [+150 reward]";
        else if (eventText.Contains("Battery died")) rewardTag = " [-300 penalty]";
        else if (eventText.Contains("no mineral"))  rewardTag = " [-10 penalty]";
        else if (e.Battery < 5)                       rewardTag = " [-50 crit batt]";
        else if (e.Battery < 10)                      rewardTag = " [-15 low batt]";
        else if (!e.IsDay && isMove)
                                                      rewardTag = " [-0.5 night move]";

        if (!string.IsNullOrWhiteSpace(eventText) || !string.IsNullOrWhiteSpace(rewardTag))
        {
            var logLine = $"[{e.TimeLabel}] {eventText}{rewardTag}";
            EventLog.Insert(0, logLine);
        }
    }

    // Ghost replay

    private void HandleTrainingRunCompleted(TrainingProgress progress)
    {
        if (_isNavigatingAway) return;

        var sample = new TrainingChartSample(
            Episode:      progress.Episode,
            RunMinerals:  progress.LastMinerals,
            LastReward:   progress.LastReward,
            LastBattery:  progress.LastBattery,
            TotalEps:     progress.TotalEps,
            Epsilon:      progress.Epsilon,
            BestMinerals: progress.BestMinerals,
            BufferSize:   progress.BufferSize,
            StatesKnown:  progress.StatesKnown);

        if (_userWantsToWatch && _trainingReplayMode == TrainingReplayMode.StepByStep)
        {
            _pendingTrainingChartSamples.Enqueue(sample);
            if (progress.Snapshot != null)
                StartGhostReplay(progress.Snapshot);
            else
                ApplyNextPendingTrainingChartSample();
            return;
        }

        if (_userWantsToWatch && _trainingReplayMode == TrainingReplayMode.Fast)
        {
            AddFastReplaySample(sample);
            if (progress.Snapshot != null)
                StartFastGhostReplay(progress.Snapshot);
            return;
        }

        // Not watching live: apply chart points immediately.
        ApplyTrainingChartSample(sample);
        if (progress.Snapshot != null)
            CacheLatestGhostSnapshot(progress.Snapshot);
    }

    private void ApplyTrainingChartSample(TrainingChartSample sample)
    {
        _epMineralPoints.Add(new(sample.Episode, sample.RunMinerals));
        _epRewardPoints.Add(new(sample.Episode, sample.LastReward));
        _epBatteryPoints.Add(new(sample.Episode, sample.LastBattery));

        if (_userWantsToWatch)
        {
            TrainingProgress = sample.TotalEps > 0
                ? (double)sample.Episode / sample.TotalEps * 100.0
                : 0.0;
            TrainingStatus = FormatTrainingStatus(
                sample.Episode, sample.TotalEps, sample.Epsilon,
                sample.BestMinerals, sample.BufferSize, sample.StatesKnown);
        }
    }

    private void ApplyNextPendingTrainingChartSample()
    {
        if (_pendingTrainingChartSamples.Count == 0) return;
        ApplyTrainingChartSample(_pendingTrainingChartSamples.Dequeue());
    }

    private void FlushPendingTrainingChartSamples()
    {
        while (_pendingTrainingChartSamples.Count > 0)
            ApplyTrainingChartSample(_pendingTrainingChartSamples.Dequeue());
    }

    private void CacheLatestGhostSnapshot(MarsRover.Core.Algorithm.EpisodeSnapshot snap)
    {
        GhostTrail  = snap.Steps;
        GhostIndex  = 0;
        IsGhostMode = false;
        GhostStatus = FormatGhostStatus(snap);
    }

    private void StartGhostReplay(MarsRover.Core.Algorithm.EpisodeSnapshot snap)
    {
        if (_isNavigatingAway) return;
        _pendingGhostSnapshots.Enqueue(snap);
        if (_ghostTimer.IsEnabled) return;
        StartNextGhostReplayFromQueue();
    }

    private void StartFastGhostReplay(MarsRover.Core.Algorithm.EpisodeSnapshot snap)
    {
        if (_isNavigatingAway) return;

        if (_ghostTimer.IsEnabled && GhostTrail != null && GhostIndex < GhostTrail.Count)
        {
            _fastPendingSnapshot = snap;
            return;
        }

        LoadSnapshot(snap);
    }

    private void StartNextGhostReplayFromQueue()
    {
        if (_isNavigatingAway) return;
        if (IsPaused) return;
        if (_ghostTimer.IsEnabled) return;
        if (_pendingGhostSnapshots.Count == 0) return;

        var next = _pendingGhostSnapshots.Dequeue();
        LoadSnapshot(next);
    }

    private void LoadSnapshot(MarsRover.Core.Algorithm.EpisodeSnapshot snap)
    {
        if (_isNavigatingAway) return;
        _ghostTimer.Stop();
        GhostTrail  = snap.Steps;
        GhostIndex  = 0;
        IsGhostMode = true;
        GhostEventLog.Clear();
        GhostStatus = FormatGhostStatus(snap);

        if (_userWantsToWatch)
        {
            ShowGhostReplay   = true;
            ShowTrainingChart = false;
        }
        _ghostTimer.Start();
    }

    private void OnGhostTick(object? sender, EventArgs e)
    {
        if (_isNavigatingAway)
        {
            _ghostTimer.Stop();
            return;
        }

        if (IsPaused)
        {
            _ghostTimer.Stop();
            return;
        }

        if (!AdvanceGhostStep())
            HandleGhostReplayCompleted();
    }

    private bool AdvanceGhostStep()
    {
        var trail = GhostTrail;
        if (trail == null || GhostIndex >= trail.Count)
            return false;

        // Log the current step with reward context.
        var step = trail[GhostIndex];
        string evtLabel = step.Event switch
        {
            MarsRover.Core.Algorithm.StepEvent.MineSuccess => "⛏ Mineral collected",
            MarsRover.Core.Algorithm.StepEvent.BatteryLow => "⚡ Battery low",
            MarsRover.Core.Algorithm.StepEvent.BatteryCritical => "🔴 Battery critical",
            MarsRover.Core.Algorithm.StepEvent.BatteryDead => "💀 Battery died",
            MarsRover.Core.Algorithm.StepEvent.ReturnHome => "🏠 Returned to base",
            MarsRover.Core.Algorithm.StepEvent.Standby => "⏸ Standby",
            _ => "➡ Move"
        };

        string rewardLabel = step.Reward > 50 ? $"  [+{step.Reward:F0} reward]"
                           : step.Reward > 0 ? $"  [+{step.Reward:F1}]"
                           : step.Reward < -50 ? $"  [{step.Reward:F0} penalty]"
                           : step.Reward < 0 ? $"  [{step.Reward:F1}]"
                           : "";

        // Only log notable events (not every move, to keep it readable).
        if (step.Event != MarsRover.Core.Algorithm.StepEvent.Move || GhostIndex % 8 == 0)
        {
            var line = $"[Step {GhostIndex}] {evtLabel}{rewardLabel}";
            GhostEventLog.Insert(0, line);
        }

        GhostIndex++;
        return true;
    }

    private void HandleGhostReplayCompleted()
    {
        _ghostTimer.Stop();
        ApplyNextPendingTrainingChartSample();

        if (_trainingReplayMode == TrainingReplayMode.Fast && _fastPendingSnapshot != null)
        {
            var next = _fastPendingSnapshot;
            _fastPendingSnapshot = null;
            if (!IsPaused)
                LoadSnapshot(next);
            return;
        }

        // Queue-driven replay keeps charts synchronized with run completions.
        if (_pendingGhostSnapshots.Count > 0)
        {
            int gapMs = (int)Math.Clamp(_ghostTimer.Interval.TotalMilliseconds * 0.6, 60, 320);
            int replayGeneration = _replayGeneration;
            Task.Delay(gapMs).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                if (_isNavigatingAway || replayGeneration != _replayGeneration || IsPaused) return;
                StartNextGhostReplayFromQueue();
            }));
            return;
        }

        int completionGeneration = _replayGeneration;
        Task.Delay(600).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (_isNavigatingAway || completionGeneration != _replayGeneration || IsPaused) return;

            if (_pendingTrainingChartSamples.Count > 0)
                FlushPendingTrainingChartSamples();

            if (_pendingCompletionStatus != null)
            {
                IsTraining = false;
                TrainingProgress = 100;
                TrainingStatus = _pendingCompletionStatus;
                _pendingCompletionStatus = null;
            }
            if (_pendingResetDisplayToStart)
            {
                ResetDisplayToStart();
                _pendingResetDisplayToStart = false;
            }

            IsGhostMode = false;
            IsRunning = false;
            if (!_userWantsToWatch) return;
            ShowGhostReplay = false;
        }));
    }

    private void StartMapLoadRevealSequence()
    {
        _mapRevealTimer.Stop();
        _mapRevealStartedAtUtc = DateTime.UtcNow;
        _mapRevealTimer.Start();
    }

    private void StopMapLoadRevealSequence(bool resetRevealToFull)
    {
        _mapRevealTimer.Stop();
        if (resetRevealToFull)
            MapTileRevealProgress = 1.0;
    }

    private void OnMapRevealTick(object? sender, EventArgs e)
    {
        if (!HasMapLoaded)
        {
            StopMapLoadRevealSequence(resetRevealToFull: false);
            return;
        }

        var elapsed = DateTime.UtcNow - _mapRevealStartedAtUtc;
        double progress = Math.Clamp(elapsed.TotalSeconds / MapRevealDuration.TotalSeconds, 0.0, 1.0);
        MapTileRevealProgress = progress;

        if (IsMapScannerHudVisible && progress >= HideScannerHudAtRevealProgress)
        {
            IsMapScannerHudVisible = false;
        }

        if (progress >= 1.0)
            _mapRevealTimer.Stop();
    }

    private void StartMineralsFoundPrompt()
    {
        _mineralsFoundHideTimer.Stop();
        _mineralsFoundCountdownTimer.Stop();
        ShowMineralsFoundPrompt = true;
        MineralsFoundCountdown = 3;
        _mineralsFoundCountdownTimer.Start();
    }

    private void OnMineralsFoundCountdownTick(object? sender, EventArgs e)
    {
        if (MineralsFoundCountdown > 0)
            MineralsFoundCountdown--;

        if (MineralsFoundCountdown > 0)
            return;

        _mineralsFoundCountdownTimer.Stop();

        // Countdown finished: map cells start appearing while the scanner stays locked.
        StartMapLoadRevealSequence();

        _mineralsFoundHideTimer.Start();
    }

    private void StopMineralsFoundPrompt()
    {
        _mineralsFoundCountdownTimer.Stop();
        _mineralsFoundHideTimer.Stop();
        ShowMineralsFoundPrompt = false;
        MineralsFoundCountdown = 0;
    }
    // Helpers



    private int EnsureValidDurationHours()
    {
        double currentValue = DurationHours ?? MinDurationHours;
        if (double.IsNaN(currentValue) || double.IsInfinity(currentValue))
            currentValue = MinDurationHours;

        int clamped = (int)Math.Clamp(
            Math.Round(currentValue, MidpointRounding.AwayFromZero),
            MinDurationHours,
            MaxDurationHours);

        if (!DurationHours.HasValue || Math.Abs(DurationHours.Value - clamped) > double.Epsilon)
            DurationHours = clamped;

        return clamped;
    }

    private void RebuildPlaybackToIndex(int targetIndex)
    {
        targetIndex = Math.Clamp(targetIndex, 0, _log.Count);
        _playbackTimer.Stop();
        IsRunning = false;

        _playbackIndex = 0;
        ResetDisplayToStart(clearTrainingQueues: false);

        for (int i = 0; i < targetIndex; i++)
        {
            _playbackIndex = i + 1;
            ApplyLogEntry(_log[i]);
        }

        _playbackIndex = targetIndex;
        SimProgress = _log.Count > 0 ? (double)_playbackIndex / _log.Count : 0.0;
    }

    private static string SaveFullExcavationLog(List<SimulationLogEntry> log)
    {
        Directory.CreateDirectory("results");
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string path = Path.Combine("results", $"fullexcavationrun-{timestamp}.txt");

        using var writer = new StreamWriter(path);
        writer.WriteLine("FULL EXCAVATION RUN");
        writer.WriteLine($"Saved at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine();
        writer.WriteLine("Tick,Sol,Hour,Day,X,Y,Battery,TotalMinerals,Event");

        foreach (var entry in log)
        {
            writer.WriteLine(
                $"{entry.Tick},{entry.Sol + 1},{entry.HourOfSol:F1},{(entry.IsDay ? "day" : "night")}," +
                $"{entry.X},{entry.Y},{entry.Battery:F1},{entry.TotalMinerals},\"{entry.EventNote.Replace("\"", "\"\"")}\"");
        }

        return path;
    }

    private void LoadReplayLogForPlayback(List<SimulationLogEntry> log, bool autoPlay)
    {
        _playbackTimer.Stop();
        _ghostTimer.Stop();
        StopMapLoadRevealSequence(resetRevealToFull: true);
        IsRunning = false;
        IsPaused = false;
        IsGhostMode = false;
        ShowGhostReplay = false;
        ShowTrainingChart = false;

        _log = log;
        _playbackIndex = 0;
        HasLog = log.Count > 0;
        ResetDisplayToStart(clearTrainingQueues: false);

        if (autoPlay && HasLog)
            Play();
    }

    private void ResetAfterFullExcavationPrompt()
    {
        ShowFullExcavationPrompt = false;
        _pendingFullExcavationLog = null;
        _deferFullExcavationPromptUntilPlaybackEnds = false;

        StopReplayProcesses(clearLog: true, clearTrainingQueues: true);
        ResetDisplayToStart(clearTrainingQueues: false);
        ShowTrainingChart = false;
        ShowGhostReplay = false;
        IsGhostMode = false;
    }

    private void StopReplayProcesses(bool clearLog, bool clearTrainingQueues)
    {
        _replayGeneration++;
        _playbackTimer.Stop();
        _ghostTimer.Stop();
        StopMapLoadRevealSequence(resetRevealToFull: true);
        _playbackIndex = 0;
        IsRunning      = false;
        IsPaused       = false;
        IsGhostMode    = false;
        ShowGhostReplay = false;

        if (clearTrainingQueues)
        {
            _pendingGhostSnapshots.Clear();
            _pendingTrainingChartSamples.Clear();
            _fastPendingSnapshot = null;
            GhostTrail = null;
            GhostIndex = 0;
            GhostStatus = string.Empty;
            GhostEventLog.Clear();
            ResetFastReplayBatch();
        }

        if (clearLog)
        {
            _log.Clear();
            HasLog = false;
        }
    }

    private void ResetDisplayToStart(bool clearTrainingQueues = true)
    {
        _lastMoveLogTick = -1;
        if (clearTrainingQueues)
        {
            _pendingGhostSnapshots.Clear();
            _pendingTrainingChartSamples.Clear();
            _fastPendingSnapshot = null;
            ResetFastReplayBatch();
        }
        Battery          = 100;
        MineralsB        = MineralsY = MineralsG = TotalMinerals = 0;
        DistanceTraveled = 0;
        CurrentTime      = "Sol 1, 0.0h";
        DayNightLabel    = "☀ Day";
        LastAction       = "—";
        LastEvent        = "—";
        MissionPhase     = "Collection";
        SimProgress      = 0;
        IsNight          = false;

        _batteryPoints.Clear();
        _mineralPoints.Clear();
        EventLog.Clear();

        if (!IsMapLoadScanOverlayVisible)
            MapTileRevealProgress = 1.0;

        if (GameMap != null)
        {
            RoverX = GameMap.StartX;
            RoverY = GameMap.StartY;
        }
    }

    partial void OnPlaybackSpeedChanged(double value) => UpdateTimerInterval();

    private void UpdateTimerInterval()
    {
        // One speed slider controls both playback modes, with a nonlinear curve
        // so low values are meaningfully slow (watchable) for ghost replay.
        double speed = Math.Clamp(PlaybackSpeed, 1.0, 20.0);
        double t     = (speed - 1.0) / 19.0;         // 0..1
        double eased = Math.Pow(t, 1.35);            // expands low-speed range

        // Simulation playback: 1 => 550ms, 20 => 20ms
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(Lerp(550.0, 20.0, eased));

        // Ghost replay: use an exponential curve so speed=1 is truly slow.
        // This keeps high-end fast while making low-end values easy to watch.
        double ghostMs = 1000.0 * Math.Pow(0.006, t); // 1 => ~1000ms, 20 => ~6ms
        _ghostTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(ghostMs, 6.0, 1000.0));
    }

    private static double Lerp(double from, double to, double t)
        => from + ((to - from) * t);

    private void AdjustMapZoomBySteps(int stepDirection, double focusX, double focusY, bool anchorToFocus)
    {
        if (!HasMapLoaded || stepDirection == 0)
            return;

        double oldZoom = ClampMapZoom(MapZoom);
        double newZoom = ClampMapZoom(oldZoom + (stepDirection * MapZoomStep));
        if (Math.Abs(newZoom - oldZoom) <= double.Epsilon)
            return;

        double fx = ClampUnit(focusX);
        double fy = ClampUnit(focusY);

        if (anchorToFocus)
        {
            const double mapWidth = MarsRover.Core.Simulation.GameMap.Width;
            const double mapHeight = MarsRover.Core.Simulation.GameMap.Height;

            double oldViewportW = mapWidth / oldZoom;
            double oldViewportH = mapHeight / oldZoom;
            double oldLeft = CalculateViewportOrigin(MapCameraCenterX, oldViewportW, mapWidth);
            double oldTop = CalculateViewportOrigin(MapCameraCenterY, oldViewportH, mapHeight);

            double worldX = oldLeft + (fx * oldViewportW);
            double worldY = oldTop + (fy * oldViewportH);

            double newViewportW = mapWidth / newZoom;
            double newViewportH = mapHeight / newZoom;
            double newLeft = Math.Clamp(worldX - (fx * newViewportW), 0, mapWidth - newViewportW);
            double newTop = Math.Clamp(worldY - (fy * newViewportH), 0, mapHeight - newViewportH);

            MapCameraCenterX = ClampUnit((newLeft + (newViewportW * 0.5)) / mapWidth);
            MapCameraCenterY = ClampUnit((newTop + (newViewportH * 0.5)) / mapHeight);
        }

        MapZoom = newZoom;
        TriggerZoomFx(fx, fy);
    }

    private static double CalculateViewportOrigin(double centerNormalized, double viewportSize, double mapSize)
    {
        double center = ClampUnit(centerNormalized) * mapSize;
        return Math.Clamp(center - (viewportSize * 0.5), 0, mapSize - viewportSize);
    }

    private void TriggerZoomFx(double focusX, double focusY)
    {
        MapZoomFxFocusX = ClampUnit(focusX);
        MapZoomFxFocusY = ClampUnit(focusY);
        MapZoomFxTrigger++;
    }

    private static double ClampMapZoom(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return MinMapZoom;
        return Math.Clamp(value, MinMapZoom, MaxMapZoom);
    }

    private static double ClampUnit(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0.5;
        return Math.Clamp(value, 0.0, 1.0);
    }

    private static string FormatGhostStatus(MarsRover.Core.Algorithm.EpisodeSnapshot snap)
        => $"Episode {snap.Episode} | {snap.MineralsCollected} minerals | " +
           (snap.BatteryDied  ? "💀 Battery died" :
            snap.ReturnedHome ? "🏠 Returned home" : "⏱ Time up");

    private static string SpeedToUiLabel(MarsRover.Core.Models.RoverSpeed speed)
        => speed switch
        {
            MarsRover.Core.Models.RoverSpeed.Slow   => "Slow",
            MarsRover.Core.Models.RoverSpeed.Normal => "Medium",
            MarsRover.Core.Models.RoverSpeed.Fast   => "Fast",
            _                                       => speed.ToString()
        };

    private static string FormatTrainingStatus(
        int episode, int totalEps, double epsilon, int bestMinerals, int bufferSize, int statesKnown)
        => $"[Q-table] Ep {episode}/{totalEps} | " +
           $"ε={epsilon:F3} | " +
           $"Best: {bestMinerals} ⛏ | " +
           $"Buffer: {bufferSize:N0} | " +
           $"States: {statesKnown:N0}";

    private void AddFastReplaySample(TrainingChartSample sample)
    {
        _fastReplayBatchCount++;
        _fastReplayMineralsSum += sample.RunMinerals;
        _fastReplayRewardSum   += sample.LastReward;
        _fastReplayBatterySum  += sample.LastBattery;
        _fastReplayLastSample   = sample;

        if (_fastReplayBatchCount >= FastReplayBatchSize)
            FlushFastReplayBatch();
    }

    private void FlushFastReplayBatch()
    {
        if (_fastReplayBatchCount == 0 || _fastReplayLastSample == null) return;

        var last = _fastReplayLastSample.Value;
        double c = _fastReplayBatchCount;

        _epMineralPoints.Add(new(last.Episode, _fastReplayMineralsSum / c));
        _epRewardPoints.Add(new(last.Episode, _fastReplayRewardSum / c));
        _epBatteryPoints.Add(new(last.Episode, _fastReplayBatterySum / c));

        if (_userWantsToWatch && _trainingReplayMode == TrainingReplayMode.Fast)
        {
            TrainingProgress = last.TotalEps > 0
                ? (double)last.Episode / last.TotalEps * 100.0
                : 0.0;
            TrainingStatus = FormatTrainingStatus(
                last.Episode, last.TotalEps, last.Epsilon,
                last.BestMinerals, last.BufferSize, last.StatesKnown);
        }

        ResetFastReplayBatch();
    }

    private void ResetFastReplayBatch()
    {
        _fastReplayBatchCount = 0;
        _fastReplayMineralsSum = 0;
        _fastReplayRewardSum = 0;
        _fastReplayBatterySum = 0;
        _fastReplayLastSample = null;
    }
}
