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
    private string _modelPath = "q_table"; // base path — runner appends .weights.json / .meta.json
    private string _mapPath   = "";           // stored on map load — used for run log
    [ObservableProperty] private bool _hasSavedModel = false;

    /// <summary>Set by MainWindow to open the native file picker.</summary>
    public Func<Task<string?>>? PickFileAsync { get; set; }

    // ── Log & playback ───────────────────────────────────────────────────────
    private List<SimulationLogEntry> _log = new();
    private int                      _playbackIndex = 0;
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

    partial void OnShowTrainingChartChanged(bool _) => OnPropertyChanged(nameof(ShowMapView));
    partial void OnShowGhostReplayChanged(bool _)   => OnPropertyChanged(nameof(ShowMapView));

    // ── Simulation chart series (playback) ────────────────────────────────────
    public ISeries[] BatterySeries { get; }
    public ISeries[] MineralSeries { get; }
    public Axis[]    TimeAxis      { get; }

    private readonly ObservableCollection<LiveChartsCore.Defaults.ObservablePoint> _batteryPoints  = new();
    private readonly ObservableCollection<LiveChartsCore.Defaults.ObservablePoint> _mineralPoints  = new();

    // ── Training chart series (per-episode) ───────────────────────────────────
    public ISeries[] TrainingMineralSeries  { get; }
    public ISeries[] TrainingRewardSeries   { get; }
    public Axis[]    EpisodeAxis            { get; }
    public Axis[]    MineralYAxis           { get; }
    public Axis[]    RewardYAxis            { get; }

    private readonly ObservableCollection<LiveChartsCore.Defaults.ObservablePoint> _epMineralPoints = new();
    private readonly ObservableCollection<LiveChartsCore.Defaults.ObservablePoint> _epRewardPoints  = new();
    // Rolling average window for smoothed reward line
    private readonly Queue<double> _rewardWindow = new();
    private const    int           RewardWindowSize = 20;
    private const    int           ChartUpdateInterval = 4;  // matches SimulationRunner.ParallelThreads (episodes per iteration)

    public MainViewModel()
    {
        // Set up playback timer (interval adjusted by PlaybackSpeed)
        _playbackTimer          = new DispatcherTimer();
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(200);
        _playbackTimer.Tick    += OnPlaybackTick;

        // Ghost replay timer — fast, plays one step per tick
        _ghostTimer          = new DispatcherTimer();
        _ghostTimer.Interval = TimeSpan.FromMilliseconds(18); // ~55 fps ghost
        _ghostTimer.Tick    += OnGhostTick;

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

        // Training chart: smoothed total reward per episode
        TrainingRewardSeries = new ISeries[]
        {
            new LineSeries<LiveChartsCore.Defaults.ObservablePoint>
            {
                Name         = "Reward (smoothed)",
                Values       = _epRewardPoints,
                Stroke       = new SolidColorPaint(SKColor.Parse("#D4521A")) { StrokeThickness = 2 },
                Fill         = new SolidColorPaint(SKColor.Parse("#D4521A18")),
                GeometrySize = 0,
                LineSmoothness = 0.6
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

        // No map loaded on startup — user must click Load Map
        TrainingStatus = "No map loaded. Click '📂 LOAD MAP' to begin.";
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

        // If trail is loaded but timer not running (user said "no" to watch),
        // restart playback from beginning so they can watch on demand
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
            TrainingStatus = "No map loaded. Click '📂 LOAD MAP' first.";
            return;
        }
        // Show popup first — actual training starts on WatchTraining or SkipWatch
        ShowWatchPrompt = true;
    }

    [RelayCommand]
    private async Task WatchTraining()
    {
        _userWantsToWatch = true;
        ShowWatchPrompt   = false;
        await StartTraining();
    }

    [RelayCommand]
    private async Task SkipWatch()
    {
        _userWantsToWatch = false;
        ShowWatchPrompt   = false;
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

        // Clear previous training data
        _epMineralPoints.Clear();
        _epRewardPoints.Clear();
        _rewardWindow.Clear();

        ShowGhostReplay   = false;
        ShowTrainingChart = false;

        var runner = new SimulationRunner(GameMap, DurationHours, _modelPath);
        _hasSavedModel = runner.HasSavedModel;

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
                            TrainingProgress = p.PercentDone;
                            TrainingStatus   = $"[Q-table] Ep {p.Episode}/{p.TotalEps} (+4/iter) | " +
                                               $"ε={p.Epsilon:F3} | " +
                                               $"Best: {p.BestMinerals} ⛏ | " +
                                               $"Buffer: {p.BufferSize:N0} | " +
                                               $"States: {p.StatesKnown:N0}";

                            // Push live data to training chart
                            _epMineralPoints.Add(new(p.Episode, p.BestMinerals));

                            // Rolling average of reward for smooth curve
                            _rewardWindow.Enqueue(p.LastReward);
                            if (_rewardWindow.Count > RewardWindowSize)
                                _rewardWindow.Dequeue();
                            double smoothed = _rewardWindow.Average();
                            _epRewardPoints.Add(new(p.Episode, smoothed));

                            // Queue ghost replay snapshot
                            if (p.Snapshot != null)
                                StartGhostReplay(p.Snapshot);
                        });
                    });

                Dispatcher.UIThread.Post(() =>
                {
                    _log              = log;
                    _playbackIndex    = 0;
                    HasLog            = log.Count > 0;
                    IsTraining        = false;
                    TrainingProgress  = 100;
                    ShowGhostReplay   = false;
                    ShowTrainingChart = false;  // return to map for playback
                    _hasSavedModel    = true;

                    // Save run log to results/ — same output as Console project
                    string? logPath = null;
                    if (log.Count > 0 && GameMap != null)
                        logPath = MarsRover.Core.Utils.MissionLogger.Save(
                            log, _mapPath, DurationHours, TrainingEpisodes, _modelPath, GameMap);

                    TrainingStatus = $"✅ Training complete — " +
                                     $"{training.BestMinerals} peak minerals | " +
                                     $"Model saved to {SimulationRunner.ResolveModelPath(_modelPath)}.qtable.json" +
                                     (logPath != null ? $" | Log → {logPath}" : "");
                    ResetDisplayToStart();
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
            _hasSavedModel = File.Exists(SimulationRunner.ResolveModelPath(_modelPath) + ".qtable.json");

            TrainingStatus = _hasSavedModel
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
            GameMap  = MarsRover.Core.Simulation.GameMap.LoadFromFile(path);
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
        CurrentTime      = e.TimeLabel;
        DayNightLabel    = e.DayNightLabel;
        Battery          = e.Battery;
        MineralsB        = e.MineralsB;
        MineralsY        = e.MineralsY;
        MineralsG        = e.MineralsG;
        TotalMinerals    = e.TotalMinerals;
        DistanceTraveled = e.DistanceTraveled;
        LastAction       = e.Action.ToString();
        LastEvent        = e.EventNote;
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
        if (e.EventNote.Contains("Collected"))      rewardTag = " [+150 reward]";
        else if (e.EventNote.Contains("Battery died")) rewardTag = " [-300 penalty]";
        else if (e.EventNote.Contains("no mineral"))  rewardTag = " [-10 penalty]";
        else if (e.Battery < 5)                       rewardTag = " [-50 crit batt]";
        else if (e.Battery < 10)                      rewardTag = " [-15 low batt]";
        else if (!e.IsDay && e.Action.Type == MarsRover.Core.Models.RoverActionType.Move)
                                                      rewardTag = " [-0.5 night move]";
        var logLine = $"[{e.TimeLabel}] {e.EventNote}{rewardTag}";
        EventLog.Insert(0, logLine);
        while (EventLog.Count > 30) EventLog.RemoveAt(EventLog.Count - 1);
    }

    // ── Ghost replay ─────────────────────────────────────────────────────────

    private MarsRover.Core.Algorithm.EpisodeSnapshot? _pendingSnapshot;

    private void StartGhostReplay(MarsRover.Core.Algorithm.EpisodeSnapshot snap)
    {
        // If a replay is currently running, queue the new snapshot and let the
        // current one finish — the OnGhostTick end-handler will pick it up.
        // This prevents the trail from restarting mid-episode every time a new
        // best is found, which caused the replay to appear frozen.
        if (_ghostTimer.IsEnabled && GhostTrail != null &&
            GhostIndex < GhostTrail.Count)
        {
            _pendingSnapshot = snap;
            // Still update the status label so the user sees the new best
            GhostStatus = $"Episode {snap.Episode} | {snap.MineralsCollected} minerals | " +
                          (snap.BatteryDied  ? "💀 Battery died" :
                           snap.ReturnedHome ? "🏠 Returned home" : "⏱ Time up") +
                          " (queued)";
            return;
        }

        LoadSnapshot(snap);
    }

    private void LoadSnapshot(MarsRover.Core.Algorithm.EpisodeSnapshot snap)
    {
        _pendingSnapshot  = null;
        _ghostTimer.Stop();
        GhostTrail  = snap.Steps;
        GhostIndex  = 0;
        IsGhostMode = true;
        GhostEventLog.Clear();
        GhostStatus = $"Episode {snap.Episode} | {snap.MineralsCollected} minerals | " +
                      (snap.BatteryDied  ? "💀 Battery died" :
                       snap.ReturnedHome ? "🏠 Returned home" : "⏱ Time up");

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

            // If a better episode came in while we were playing, start it now
            if (_pendingSnapshot != null)
            {
                var next = _pendingSnapshot;
                Task.Delay(400).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
                    LoadSnapshot(next)));
                return;
            }

            Task.Delay(600).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
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
            while (GhostEventLog.Count > 40) GhostEventLog.RemoveAt(GhostEventLog.Count - 1);
        }

        GhostIndex++;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────



    private void ResetDisplayToStart()
    {
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
        // Speed 1x = 200ms | Speed 10x = 20ms
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, 200.0 / PlaybackSpeed));
    }
}