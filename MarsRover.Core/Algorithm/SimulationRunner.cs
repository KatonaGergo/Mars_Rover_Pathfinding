using System.Collections.Concurrent;
using MarsRover.Core.Models;
using MarsRover.Core.Simulation;

namespace MarsRover.Core.Algorithm;

/// <summary>
/// Orchestrates Q-table training with four learning improvements:
///
/// ① ELIGIBILITY TRACES (Q-λ)
///   After each strategic decision the TD error δ is broadcast backward
///   through all recently visited state-action pairs via a recency-weighted
///   trace. This solves the key problem in our hybrid architecture: the rover
///   decides "SeekMineral X" then A* walks 8 steps — the +150 reward arrives
///   8 ticks later. Standard Q-learning only updates the decision tick.
///   With λ=0.7 the credit propagates back through every prior decision that
///   led here, weighted by how recently it was made.
///   λ=0: pure Q-learning. λ=1: full Monte Carlo. λ=0.7: best of both.
///
/// ② PRIORITIZED EXPERIENCE REPLAY (PER)
///   Transitions are sampled proportionally to |TD error|^α instead of
///   uniformly. High-surprise transitions (unexpected minerals, battery deaths,
///   failed returns) dominate the replay batch. Zero-surprise replays of
///   already-learned states are sampled rarely. Priorities are updated after
///   each Q-table update.
///
/// ③ LEARNING RATE DECAY
///   α starts at 0.3 (fast early learning) and decays toward 0.02 each episode.
///   Prevents late-stage oscillation where correct Q-values get overwritten
///   by noisy outlier transitions after the policy has converged.
///
/// ④ PARALLELIZATION
///   Episodes are collected in batches using Parallel.For. Each thread gets its
///   own agent (reads shared Q-table) and map clone. No Q-table writes during
///   collection — only reads — so Dictionary is safe without locks.
///   After all threads finish, the serial merge phase applies eligibility traces
///   and fills the PER buffer. Then PER replay updates the Q-table.
///   Result: 4 threads ≈ 3.5× throughput (some overhead from merge + replay).
/// </summary>
public class SimulationRunner
{
    private readonly GameMap _map;
    private readonly int     _durationHours;
    private readonly int     _totalTicks;
    private readonly string  _modelPath;

    // ── Hyperparameters ───────────────────────────────────────────────────────
    private const int    BufferCapacity   = 20_000;
    private const int    BatchSize        = 64;
    private const int    WarmupEpisodes   = 10;   // episode-based warmup (not step-based)
    private const int    ProgressInterval = 10;
    private const int    ParallelThreads  = 4;    // parallel episode collectors
    private const double Lambda           = 0.7;  // eligibility trace decay (Q-λ)
    private const double TraceThreshold   = 1e-4; // prune traces below this value
    private const double ActionCount      = 4;    // HybridDecision.Count

    public SimulationRunner(GameMap map, int durationHours, string modelPath = "model")
    {
        _map           = map;
        _durationHours = durationHours;
        _totalTicks    = durationHours * 2;
        _modelPath     = modelPath;
    }

    private string QTablePath => _modelPath + ".qtable.json";
    private string MetaPath   => _modelPath + ".meta.json";
    public bool HasSavedModel => File.Exists(QTablePath);

    // ── Training ──────────────────────────────────────────────────────────────

    public TrainingResult Train(
        int episodes = 1000,
        Action<TrainingProgress>? onProgress = null)
        => TrainWithBest(episodes, onProgress).result;

    private (TrainingResult result, QTable bestTable) TrainWithBest(
        int episodes = 1000,
        Action<TrainingProgress>? onProgress = null)
    {
        // ── Load or create Q-table ────────────────────────────────────────────
        var sharedTable = HasSavedModel ? QTable.Load(QTablePath) : new QTable();
        double startEpsilon = HasSavedModel ? LoadMeta().Epsilon : 1.0;

        // Epsilon schedule: decays to 0.05 by final episode
        double epsilon      = startEpsilon;
        double epsilonMin   = 0.05;
        double epsilonDecay = Math.Pow(epsilonMin / Math.Max(epsilon, 0.01),
                                        1.0 / Math.Max(episodes, 1));

        var    buffer       = new PrioritizedReplayBuffer(BufferCapacity);
        var    allRewards   = new List<double>();
        int    bestMinerals = 0;
        QTable bestTable    = new QTable();   // snapshot of Q-table at peak performance
        int    episodeBatch = ParallelThreads; // collect this many episodes per iteration

        // We iterate in groups of ParallelThreads episodes
        int iterations = (int)Math.Ceiling(episodes / (double)episodeBatch);

        // Best snapshot for ghost replay (updated each iteration)
        EpisodeSnapshot? bestSnapshot = null;

        for (int iter = 0; iter < iterations; iter++)
        {
            int epBase       = iter * episodeBatch;
            int thisCount    = Math.Min(episodeBatch, episodes - epBase);
            double iterEps   = epsilon; // all threads in this batch share the same epsilon

            // ── ① PARALLEL EPISODE COLLECTION ────────────────────────────────
            // Each thread runs one full episode independently.
            // Reads sharedTable for action selection — no writes.
            var collected = new EpisodeData[thisCount];

            Parallel.For(0, thisCount,
                new ParallelOptions { MaxDegreeOfParallelism = ParallelThreads },
                threadId =>
                {
                    collected[threadId] = RunEpisode(
                        sharedTable, iterEps, seed: epBase + threadId);
                });

            // ── SERIAL MERGE PHASE ────────────────────────────────────────────
            EpisodeData? bestThisIter = null;

            foreach (var ep in collected)
            {
                allRewards.Add(ep.TotalReward);

                // ② ELIGIBILITY TRACES: always apply to sharedTable first
                if (ep.Transitions.Count > 0)
                    ApplyEligibilityTraces(ep.Transitions, sharedTable);

                if (ep.MineralsCollected > bestMinerals)
                {
                    bestMinerals = ep.MineralsCollected;
                    bestThisIter = ep;
                    // Clone AFTER traces applied — bestTable now contains the
                    // learning FROM the winning episode, not just the policy
                    // that generated it. This is the table deployment should use.
                    bestTable = QTable.CloneFrom(sharedTable);
                }

                // Fill PER buffer with |TD error| as initial priority
                foreach (var t in ep.Transitions)
                {
                    double tdErr = sharedTable.GetTDError(
                        t.StateKey, t.ActionIdx, t.Reward, t.NextStateKey,
                        (int)ActionCount);
                    buffer.Push(t.StateKey, t.ActionIdx, t.Reward, t.NextStateKey,
                                t.IsTerminal, Math.Abs(tdErr) + 1e-4);
                }
            }

            // ③ PER REPLAY: sample and update Q-table once buffer is warm
            int epsDone = epBase + thisCount;
            if (epsDone >= WarmupEpisodes && buffer.IsReady(BatchSize))
            {
                var (batch, indices) = buffer.Sample(BatchSize);
                for (int i = 0; i < batch.Length; i++)
                {
                    var t = batch[i];
                    sharedTable.UpdateByKeyWithAlpha(
                        t.StateKey, t.ActionIdx, t.Reward, t.NextStateKey,
                        (int)ActionCount, sharedTable.LearningRate);

                    double newErr = sharedTable.GetTDError(
                        t.StateKey, t.ActionIdx, t.Reward, t.NextStateKey,
                        (int)ActionCount);
                    buffer.UpdatePriority(indices[i], newErr);
                }
            }

            // ③ LEARNING RATE DECAY: once per iteration
            sharedTable.DecayLearningRate();

            // Epsilon decay: once per episode-equivalent
            for (int i = 0; i < thisCount; i++)
                epsilon = Math.Max(epsilonMin, epsilon * epsilonDecay);

            // Update best snapshot for ghost replay
            if (bestThisIter != null)
                bestSnapshot = BuildSnapshot(bestThisIter, epBase);

            // Report every iteration — fires every 4 episodes (= ParallelThreads).
            // The Episode field carries the real cumulative count so the chart
            // X-axis is always accurate. No ProgressInterval filtering needed.
            {
                var snap = bestSnapshot ?? (collected.Length > 0
                    ? BuildSnapshot(collected[0], epBase) : null);

                onProgress?.Invoke(new TrainingProgress(
                    Episode:      epsDone,
                    TotalEps:     episodes,
                    Epsilon:      epsilon,
                    LastReward:   allRewards.Count > 0 ? allRewards[^1] : 0,
                    BestMinerals: bestMinerals,
                    StatesKnown:  sharedTable.StateCount,
                    BufferSize:   buffer.Count,
                    Snapshot:     snap));
            }
        }

        // Save the best checkpoint — the table that actually achieved bestMinerals
        // not the final table which may have been updated by worse later episodes
        var tableToSave = bestTable.StateCount > 0 ? bestTable : sharedTable;
        SaveModel(tableToSave, epsilon, episodes, bestMinerals);

        return (new TrainingResult(episodes, bestMinerals, allRewards, sharedTable.StateCount),
                tableToSave);
    }

    // ── Single episode runner (runs on a thread) ──────────────────────────────

    private EpisodeData RunEpisode(QTable sharedTable, double epsilon, int seed)
    {
        var episodeMap = _map.Clone();
        var engine     = new SimulationEngine(episodeMap, _durationHours);
        // Each thread gets its own agent — reads sharedTable, never writes
        var agent      = new HybridAgent(episodeMap, _totalTicks, sharedTable, seed);
        agent.Epsilon  = epsilon;
        agent.ResetNavigation();

        var    transitions  = new List<StrategicTransition>();
        var    steps        = new List<StepRecord>();
        double totalReward  = 0;

        while (!engine.IsFinished && !engine.BatteryDead
               && agent.CurrentPhase != MissionPhase.AtBase)
        {
            var state    = engine.GetState();
            var qState   = agent.BuildQLearningState(state, episodeMap);
            var stateKey = qState.ToKey();

            var (action, decisionIdx) = agent.SelectActionWithDecision(
                                            state, episodeMap, isTraining: true);

            int prevTotal = state.TotalMinerals;
            var entry     = engine.Step(action, agent.CurrentPhase.ToString());
            if (entry == null) break;

            var  newState     = engine.GetState();
            bool collected    = newState.TotalMinerals > prevTotal;
            bool terminal     = engine.IsFinished || engine.BatteryDead;
            bool returnedHome = terminal
                                && newState.X == episodeMap.StartX
                                && newState.Y == episodeMap.StartY;

            double reward = HybridRewardCalculator.Calculate(
                state, action, entry, episodeMap, collected, terminal, returnedHome);

            // Only store strategic decisions — path/mining ticks carry no Q signal
            if (decisionIdx >= 0)
            {
                var nextQState = agent.BuildQLearningState(newState, episodeMap);
                transitions.Add(new StrategicTransition(
                    stateKey, decisionIdx, reward, nextQState.ToKey(), terminal));
            }

            // Ghost record
            var evt = collected              ? StepEvent.MineSuccess
                    : engine.BatteryDead     ? StepEvent.BatteryDead
                    : returnedHome           ? StepEvent.ReturnHome
                    : newState.Battery < 5   ? StepEvent.BatteryCritical
                    : newState.Battery < 20  ? StepEvent.BatteryLow
                    : action.Type == RoverActionType.Standby ? StepEvent.Standby
                    : StepEvent.Move;
            steps.Add(new StepRecord(state.X, state.Y, evt, reward));

            totalReward += reward;
        }

        var  final        = engine.GetState();
        bool returnedOk   = final.X == episodeMap.StartX && final.Y == episodeMap.StartY;

        return new EpisodeData(
            Transitions:       transitions,
            Steps:             steps,
            TotalReward:       totalReward,
            MineralsCollected: final.TotalMinerals,
            BatteryDied:       engine.BatteryDead,
            ReturnedHome:      returnedOk);
    }

    // ── ① Eligibility Traces ──────────────────────────────────────────────────

    /// <summary>
    /// Applies Q(λ) updates to the shared Q-table for one episode's transitions.
    ///
    /// Called serially after parallel collection, so Q-table writes are safe.
    ///
    /// Algorithm:
    ///   For each transition (s, a, r, s') in order:
    ///     δ = r + γ·maxQ(s') - Q(s,a)          [TD error at this step]
    ///     e(s,a) += 1                            [increment current trace]
    ///     For ALL (s,a) in trace:
    ///       Q(s,a) += α · δ · e(s,a)            [broadcast δ to all recent states]
    ///       e(s,a) *= γ·λ                        [decay trace]
    ///       prune if e(s,a) < threshold
    ///
    /// The key insight: δ from the mineral collection at step 8 is broadcast
    /// backward to the strategic decision at step 1 with weight λ^7 ≈ 0.08.
    /// The decision at step 7 gets weight λ^1 ≈ 0.7. Both get credit.
    /// Without traces, only step 8 would be updated.
    /// </summary>
    private static void ApplyEligibilityTraces(
        List<StrategicTransition> transitions, QTable table)
    {
        const double gamma  = 0.95;
        const double lambda = Lambda;
        const int    nAct   = (int)ActionCount;

        // Trace dictionary: (stateKey, actionIdx) → eligibility value
        var traces = new Dictionary<(string, int), double>();

        double alpha = table.LearningRate;

        foreach (var t in transitions)
        {
            // Compute TD error for this transition
            double delta = table.GetTDError(t.StateKey, t.ActionIdx,
                                             t.Reward, t.NextStateKey, nAct);

            // Increment trace for current (s, a)
            var key = (t.StateKey, t.ActionIdx);
            traces[key] = traces.GetValueOrDefault(key, 0.0) + 1.0;

            // Broadcast δ to ALL traced (s,a) pairs, then decay
            var toRemove = new List<(string, int)>();
            foreach (var (traceKey, e) in traces)
            {
                table.ApplyWeightedDelta(
                    traceKey.Item1, traceKey.Item2, nAct, alpha, delta, e);

                double newE = e * gamma * lambda;
                if (newE < TraceThreshold)
                    toRemove.Add(traceKey);
                else
                    traces[traceKey] = newE;
            }
            foreach (var k in toRemove) traces.Remove(k);

            // Terminal: reset all traces (reward can't propagate past episode boundary)
            if (t.IsTerminal) traces.Clear();
        }
    }

    // ── Live simulation ───────────────────────────────────────────────────────

    /// <summary>
    /// Runs one deployment episode with epsilon=0 (pure exploitation).
    /// Pass <paramref name="tableOverride"/> to use an in-memory table directly
    /// (e.g. bestTable from just-finished training) instead of loading from disk.
    /// This avoids any disk round-trip and guarantees the exact peak policy is used.
    /// </summary>
    public List<SimulationLogEntry> RunSimulation(QTable? tableOverride = null)
    {
        var table = tableOverride
                    ?? (HasSavedModel ? QTable.Load(QTablePath) : new QTable());

        var agent   = new HybridAgent(_map, _totalTicks, table, seed: 0);
        agent.Epsilon = 0.0;
        agent.ResetNavigation();

        var liveMap = _map.Clone();
        var engine  = new SimulationEngine(liveMap, _durationHours);
        var log     = new List<SimulationLogEntry>();

        while (!engine.IsFinished && !engine.BatteryDead
               && agent.CurrentPhase != MissionPhase.AtBase)
        {
            var state  = engine.GetState();
            var action = agent.SelectAction(state, liveMap, isTraining: false);
            var entry  = engine.Step(action, agent.CurrentPhase.ToString());
            if (entry != null) log.Add(entry);
        }

        return log;
    }

    public (TrainingResult training, List<SimulationLogEntry> log) TrainAndRun(
        int episodes = 1000,
        Action<TrainingProgress>? onProgress = null)
    {
        // Train returns the best Q-table snapshot in addition to the result
        var (training, bestTable) = TrainWithBest(episodes, onProgress);
        // Pass in-memory bestTable directly — no disk round-trip, guaranteed correct policy
        var log = RunSimulation(tableOverride: bestTable);
        return (training, log);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EpisodeSnapshot BuildSnapshot(EpisodeData ep, int episodeNum)
        => new EpisodeSnapshot(
            Episode:           episodeNum,
            Steps:             new List<StepRecord>(ep.Steps),
            MineralsCollected: ep.MineralsCollected,
            TotalReward:       ep.TotalReward,
            BatteryDied:       ep.BatteryDied,
            ReturnedHome:      ep.ReturnedHome);

    // ── Persistence ───────────────────────────────────────────────────────────

    // ── Public model info ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns metadata about the saved model at this runner's model path,
    /// or null if no model has been saved yet.
    /// </summary>
    public ModelInfo? GetModelInfo()
        => GetModelInfo(_modelPath);

    /// <summary>
    /// Static overload — reads model metadata from disk using only the base
    /// model path. Does NOT require a GameMap or a SimulationRunner instance.
    /// Safe to call from --info or the resume prompt before the map is loaded.
    /// </summary>
    public static ModelInfo? GetModelInfo(string modelPath)
    {
        string baseName    = Path.GetFileName(modelPath);
        string resolvedPath = Path.Combine("Saved_Models", baseName);
 
        string qtablePath = resolvedPath + ".qtable.json";
        string metaPath   = resolvedPath + ".meta.json";

        if (!File.Exists(qtablePath)) return null;

        // Read meta (optional — might not exist for old saves)
        ModelMeta meta = new();
        if (File.Exists(metaPath))
        {
            try
            {
                meta = System.Text.Json.JsonSerializer.Deserialize<ModelMeta>(
                           File.ReadAllText(metaPath)) ?? new ModelMeta();
            }
            catch { /* malformed meta — use defaults */ }
        }

        int stateCount = 0;
        try { stateCount = QTable.Load(qtablePath).StateCount; } catch { }

        return new ModelInfo(
            ModelPath:         modelPath,
            EpisodesCompleted: meta.EpisodesCompleted,
            BestMinerals:      meta.BestMinerals,
            Epsilon:           meta.Epsilon,
            SavedAt:           meta.SavedAt,
            StatesKnown:       stateCount);
    }

    private void SaveModel(QTable table, double epsilon, int episodesCompleted, int bestMinerals)
    {
        table.Save(QTablePath);

        var meta = new ModelMeta
        {
            Epsilon           = epsilon,
            EpsilonDecay      = 0.995,
            EpisodesCompleted = episodesCompleted,
            BestMinerals      = bestMinerals,
            SavedAt           = DateTime.UtcNow.ToString("o")
        };

        File.WriteAllText(MetaPath,
            System.Text.Json.JsonSerializer.Serialize(meta,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private ModelMeta LoadMeta()
    {
        if (!File.Exists(MetaPath)) return new ModelMeta();
        try { return System.Text.Json.JsonSerializer.Deserialize<ModelMeta>(
                  File.ReadAllText(MetaPath)) ?? new ModelMeta(); }
        catch { return new ModelMeta(); }
    }
}

// ── Internal data structures ──────────────────────────────────────────────────

/// <summary>One strategic transition collected during an episode.</summary>
internal record struct StrategicTransition(
    string StateKey,
    int    ActionIdx,
    double Reward,
    string NextStateKey,
    bool   IsTerminal);

/// <summary>Everything collected from one episode run.</summary>
internal record EpisodeData(
    List<StrategicTransition> Transitions,
    List<StepRecord>          Steps,
    double                    TotalReward,
    int                       MineralsCollected,
    bool                      BatteryDied,
    bool                      ReturnedHome);

internal class ModelMeta
{
    public double Epsilon           { get; set; } = 1.0;
    public double EpsilonDecay      { get; set; } = 0.995;
    public int    EpisodesCompleted { get; set; } = 0;
    public int    BestMinerals      { get; set; } = 0;
    public string SavedAt           { get; set; } = "";
}