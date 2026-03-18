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

    // ── Simulation state ─────────────────────────────────────────────────────
    [ObservableProperty] private int    _durationHours   = 48;
    [ObservableProperty] private int    _trainingEpisodes = 1000;
    [ObservableProperty] private double _playbackSpeed   = 1.0;

    [ObservableProperty] private bool   _isTraining      = false;
    [ObservableProperty] private bool   _isRunning       = false;
    [ObservableProperty] private bool   _isPaused        = false;
    [ObservableProperty] private bool   _hasLog          = false;

    [ObservableProperty] private double _trainingProgress = 0;
    [ObservableProperty] private string _trainingStatus   = "Ready";

    // ── Current tick display ─────────────────────────────────────────────────
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

    // ── Map + model path ─────────────────────────────────────────────────────
    [ObservableProperty] private GameMap? _gameMap;
    public bool HasMapLoaded => GameMap != null;
    private string _modelPath = "q_table"; // base path — runner appends .weights.json / .meta.json
    private string _mapPath   = "";           // stored on map load — used for run log
    [ObservableProperty] private bool _hasSavedModel = false;

    /// <summary>Set by MainWindow to open the native file picker.</summary>
    public Func<Task<string?>>? PickFileAsync { get; set; }

    // ── Log & playback ───────────────────────────────────────────────────────
    private List<SimulationLogEntry> _log = new();
    private int                      _playbackIndex = 0;
    private int                      _lastMoveLogTick = -1; // avoid duplicate move logs on expanded sub-steps
    private DispatcherTimer          _playbackTimer;

    // ── Event log (last 20 lines) ─────────────────────────────────────────────
    public ObservableCollection<string> EventLog      { get; } = new();
    public ObservableCollection<string> GhostEventLog { get; } = new();

    // ── Ghost replay (training visualisation) ────────────────────────────────
    [ObservableProperty] private List<MarsRover.Core.Algorithm.StepRecord>? _ghostTrail;
    [ObservableProperty] private int  _ghostIndex   = 0;
    [ObservableProperty] private bool _isGhostMode  = false;
    [ObservableProperty] private string _ghostStatus = "";

    private DispatcherTimer _ghostTimer = new();

    // ── View toggle ───────────────────────────────────────────────────────────
    [ObservableProperty] private bool _showTrainingChart = false;
    [ObservableProperty] private bool _showGhostReplay   = false;
    [ObservableProperty] private bool _showWatchPrompt   = false;
    private bool _userWantsToWatch = false;

    // Map is visible when neither chart nor ghost is showing
    public bool ShowMapView => !ShowTrainingChart && !ShowGhostReplay;

    partial void OnShowTrainingChartChanged(bool value) => OnPropertyChanged(nameof(ShowMapView));
    partial void OnShowGhostReplayChanged(bool value)   => OnPropertyChanged(nameof(ShowMapView));
    partial void OnGameMapChanged(GameMap? value)       => OnPropertyChanged(nameof(HasMapLoaded));

    // ── Simulation chart series (playback) ────────────────────────────────────
    public ISeries[] BatterySeries { get; }
    public ISeries[] MineralSeries { get; }
    public Axis[]    TimeAxis      { get; }

    private readonly ObservableCollection<LiveChartsCore.Defaults.ObservablePoint> _batteryPoints  = new();
    private readonly ObservableCollection<LiveChartsCore.Defaults.ObservablePoint> _mineralPoints  = new();

    // ── Training chart series (per-episode) ───────────────────────────────────
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

    // ── Commands ──────────────────────────────────────────────────────────────

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

        IsTraining       = true;
        bool resuming    = File.Exists(SimulationRunner.ResolveModelPath(_modelPath) + ".qtable.json");
        TrainingStatus   = resuming
            ? "▶ Resuming Q-table training from saved model..."
            : "🧠 Training Hybrid Q-table agent...";
        TrainingProgress = 0;
        _pendingCompletionStatus = null;
        _pendingResetDisplayToStart = false;

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

        var runner = new SimulationRunner(GameMap, DurationHours, _modelPath);
        HasSavedModel = runner.HasSavedModel;

        try
        {
            await Task.Run(() =>
            {
                var (training, log) = runner.TrainAndRun(
                    episodes: TrainingEpisodes,
                    onProgress: p =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
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

                    // Save run log to results/ — same output as Console project
                    string? logPath = null;
                    if (log.Count > 0 && GameMap != null)
                        logPath = MarsRover.Core.Utils.MissionLogger.Save(
                            log, _mapPath, DurationHours, TrainingEpisodes, _modelPath, GameMap);

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
            IsTraining     = false;
            TrainingStatus = $"ERROR: {ex.GetType().Name}: {ex.Message}";
            if (ex.InnerException != null)
                TrainingStatus += $" | Inner: {ex.InnerException.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveModel()
    {
        if (PickFileAsync == null) return;
        // Use a save dialog — reuse PickFileAsync direction but guide user with status
        TrainingStatus = $"Model saved at: {SimulationRunner.ResolveModelPath(_modelPath)}.qtable.json";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task LoadModel()
    {
        if (PickFileAsync == null) return;
        try
        {
            var path = await PickFileAsync();
            if (path == null) return;

            // Strip extensions — user may select .weights.json or base name
            _modelPath     = path.Replace(".qtable.json", "").Replace(".meta.json", "");
            HasSavedModel = File.Exists(SimulationRunner.ResolveModelPath(_modelPath) + ".qtable.json");

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
    private void Play()
    {
        if (!HasLog) return;
        IsRunning = true;
        IsPaused  = false;
        UpdateTimerInterval();
        _playbackTimer.Start();
    }

    [RelayCommand]
    private void Pause()
    {
        _playbackTimer.Stop();
        IsPaused  = true;
        IsRunning = false;
    }

    [RelayCommand]
    private void StepForward()
    {
        if (_playbackIndex < _log.Count)
            ApplyLogEntry(_log[_playbackIndex++]);
    }

    [RelayCommand]
    private void Reset()
    {
        _playbackTimer.Stop();
        _playbackIndex = 0;
        IsRunning      = false;
        IsPaused       = false;
        ResetDisplayToStart();
    }

    [RelayCommand]
    private async Task LoadMap()
    {
        if (PickFileAsync == null) return;

        var path = await PickFileAsync();
        if (path == null) return; // user cancelled

        try
        {
            var loadedMap = MarsRover.Core.Simulation.GameMap.LoadFromFile(path);
            StopReplayProcesses(clearLog: true, clearTrainingQueues: true);
            GameMap  = loadedMap;
            _mapPath = path;
            RoverX  = GameMap.StartX;
            RoverY  = GameMap.StartY;
            TrainingStatus = $"Map loaded — {System.IO.Path.GetFileName(path)} " +
                             $"| {GameMap.RemainingMinerals.Count} minerals";
            ResetDisplayToStart();
        }
        catch (Exception ex)
        {
            TrainingStatus = $"Failed to load map: {ex.Message}";
        }
    }

    // ── Playback timer ────────────────────────────────────────────────────────

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        if (_playbackIndex >= _log.Count)
        {
            _playbackTimer.Stop();
            IsRunning = false;
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

    // ── Ghost replay ─────────────────────────────────────────────────────────

    private void HandleTrainingRunCompleted(TrainingProgress progress)
    {
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
        _pendingGhostSnapshots.Enqueue(snap);
        if (_ghostTimer.IsEnabled) return;
        StartNextGhostReplayFromQueue();
    }

    private void StartFastGhostReplay(MarsRover.Core.Algorithm.EpisodeSnapshot snap)
    {
        if (_ghostTimer.IsEnabled && GhostTrail != null && GhostIndex < GhostTrail.Count)
        {
            _fastPendingSnapshot = snap;
            return;
        }

        LoadSnapshot(snap);
    }

    private void StartNextGhostReplayFromQueue()
    {
        if (_ghostTimer.IsEnabled) return;
        if (_pendingGhostSnapshots.Count == 0) return;

        var next = _pendingGhostSnapshots.Dequeue();
        LoadSnapshot(next);
    }

    private void LoadSnapshot(MarsRover.Core.Algorithm.EpisodeSnapshot snap)
    {
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
        var trail = GhostTrail;
        if (trail == null || GhostIndex >= trail.Count)
        {
            _ghostTimer.Stop();
            ApplyNextPendingTrainingChartSample();

            if (_trainingReplayMode == TrainingReplayMode.Fast && _fastPendingSnapshot != null)
            {
                var next = _fastPendingSnapshot;
                _fastPendingSnapshot = null;
                LoadSnapshot(next);
                return;
            }

            // Queue-driven replay keeps charts synchronized with run completions.
            if (_pendingGhostSnapshots.Count > 0)
            {
                int gapMs = (int)Math.Clamp(_ghostTimer.Interval.TotalMilliseconds * 0.6, 60, 320);
                Task.Delay(gapMs).ContinueWith(_ => Dispatcher.UIThread.Post(StartNextGhostReplayFromQueue));
                return;
            }

            Task.Delay(600).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
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
                if (!_userWantsToWatch) return;
                ShowGhostReplay = false;
            }));
            return;
        }

        // Log the current step with reward context
        var step = trail[GhostIndex];
        string evtLabel = step.Event switch
        {
            MarsRover.Core.Algorithm.StepEvent.MineSuccess     => "⛏ Mineral collected",
            MarsRover.Core.Algorithm.StepEvent.BatteryLow      => "⚡ Battery low",
            MarsRover.Core.Algorithm.StepEvent.BatteryCritical => "🔴 Battery critical",
            MarsRover.Core.Algorithm.StepEvent.BatteryDead     => "💀 Battery died",
            MarsRover.Core.Algorithm.StepEvent.ReturnHome      => "🏠 Returned to base",
            MarsRover.Core.Algorithm.StepEvent.Standby         => "⏸ Standby",
            _                                                    => "➡ Move"
        };

        string rewardLabel = step.Reward > 50   ? $"  [+{step.Reward:F0} reward]"
                           : step.Reward > 0    ? $"  [+{step.Reward:F1}]"
                           : step.Reward < -50  ? $"  [{step.Reward:F0} penalty]"
                           : step.Reward < 0    ? $"  [{step.Reward:F1}]"
                           : "";

        // Only log notable events (not every move, to keep it readable)
        if (step.Event != MarsRover.Core.Algorithm.StepEvent.Move || GhostIndex % 8 == 0)
        {
            var line = $"[Step {GhostIndex}] {evtLabel}{rewardLabel}";
            GhostEventLog.Insert(0, line);
        }

        GhostIndex++;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────



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
