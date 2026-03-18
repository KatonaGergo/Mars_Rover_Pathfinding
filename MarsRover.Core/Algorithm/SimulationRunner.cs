using MarsRover.Core.Models;
using MarsRover.Core.Simulation;
using MarsRover.Core.Utils;

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
    private const int PolicySchemaVersion = 2;
    public static int CurrentPolicySchemaVersion => PolicySchemaVersion;

    // ── Hyperparameters ───────────────────────────────────────────────────────
    private const int    BufferCapacity   = 20_000;
    private const int    BatchSize        = 64;
    private const int    WarmupEpisodes   = 10;   // episode-based warmup (not step-based)
    private static readonly int ParallelThreads = Math.Max(4, Environment.ProcessorCount);    // parallel episode collectors
    private static int ActionCount => (int)HybridDecision.Count;
    private const double PerBetaStart     = 0.4;  // PER importance-sampling beta (start)
    private const double PerBetaEnd       = 1.0;  // PER importance-sampling beta (end)

    public SimulationRunner(GameMap map, int durationHours, string modelPath = "model")
    {
        _map           = map;
        _durationHours = durationHours;
        _totalTicks    = durationHours * 2;

        // Strip any pre-existing Saved_Models prefix so callers that already
        // include it (e.g. LoadModel in the UI) don't double-nest the folder.
        string baseName = Path.GetFileName(modelPath);
        _modelPath = Path.Combine(SavedModelsDir, baseName);
    }

    private const  string SavedModelsDir = "Saved_Models";
    private string QTablePath => _modelPath + ".qtable.json";
    private string MetaPath   => _modelPath + ".meta.json";
    public bool HasSavedModel => File.Exists(QTablePath);

    /// <summary>
    /// Normalises any model base-name or path to the canonical
    /// Saved_Models/&lt;name&gt; form.  Used by the UI and Console so every
    /// File.Exists check and display string stays in sync with where
    /// SimulationRunner actually writes the files.
    /// </summary>
    public static string ResolveModelPath(string modelPath)
        => Path.Combine(SavedModelsDir, Path.GetFileName(modelPath));

    // ── Training ──────────────────────────────────────────────────────────────

    public TrainingResult Train(
        int episodes = 1000,
        Action<TrainingProgress>? onProgress = null)
        => Train(episodes, onProgress, options: null);

    public TrainingResult Train(
        int episodes,
        Action<TrainingProgress>? onProgress,
        TrainingOptions? options)
        => TrainWithBest(episodes, onProgress, GetEffectiveOptions(options)).result;

    private (TrainingResult result, QTable bestTable) TrainWithBest(
        int episodes = 1000,
        Action<TrainingProgress>? onProgress = null,
        TrainingOptions? options = null)
    {
        var effective = GetEffectiveOptions(options);

        if (effective.SeedSweep?.Enabled == true
            && effective.SeedSweep.SeedCount > 1)
        {
            return TrainWithSeedSweep(episodes, onProgress, effective);
        }

        if (effective.Curriculum?.Enabled == true
            && effective.Curriculum.RandomMapCount > 0
            && effective.Curriculum.PretrainEpisodes > 0)
        {
            return TrainWithCurriculum(episodes, onProgress, effective);
        }

        return TrainCore(episodes, onProgress, effective);
    }

    private (TrainingResult result, QTable bestTable) TrainWithCurriculum(
        int episodes,
        Action<TrainingProgress>? onProgress,
        TrainingOptions options)
    {
        var curriculum = options.Curriculum ?? new CurriculumOptions();
        QTable? warmStart = null;

        // Pretrain on generated maps
        for (int i = 0; i < curriculum.RandomMapCount; i++)
        {
            int mapSeed = curriculum.RandomMapSeedStart + i;
            var map = BuildGeneratedMap(mapSeed);
            var runner = new SimulationRunner(map, _durationHours, $"{_modelPath}_curriculum_{mapSeed}");
            var pretrainOptions = options with
            {
                Curriculum = new CurriculumOptions(false),
                SeedSweep = new SeedSweepOptions(false),
                ProfileName = $"{options.ProfileName}-curriculum-pretrain-{i}",
                EpisodeSeedOffset = options.EpisodeSeedOffset + (i * 10_000)
            };
            warmStart = runner.TrainCore(
                curriculum.PretrainEpisodes,
                onProgress: null,
                options: pretrainOptions,
                warmStartTable: warmStart,
                persistModel: false).bestTable;
        }

        // Fine-tune on official map
        var finetuneOptions = options with
        {
            Curriculum = new CurriculumOptions(false),
            SeedSweep = new SeedSweepOptions(false)
        };

        return TrainCore(
            episodes > 0 ? episodes : curriculum.FineTuneEpisodes,
            onProgress,
            finetuneOptions,
            warmStartTable: warmStart);
    }

    private (TrainingResult result, QTable bestTable) TrainWithSeedSweep(
        int episodes,
        Action<TrainingProgress>? onProgress,
        TrainingOptions options)
    {
        var seedSweep = options.SeedSweep ?? new SeedSweepOptions();
        int seedCount = Math.Max(1, seedSweep.SeedCount);

        QTable? best = null;
        double bestScore = double.MinValue;
        TrainingResult? bestResult = null;

        for (int seed = 0; seed < seedCount; seed++)
        {
            var seedOptions = options with
            {
                SeedSweep = new SeedSweepOptions(false),
                ProfileName = $"{options.ProfileName}-seed{seed}",
                EpisodeSeedOffset = options.EpisodeSeedOffset + (seed * 10_000)
            };

            var run = TrainCore(episodes, null, seedOptions, persistModel: false);
            var eval = EvaluatePolicy(run.bestTable, seedSweep);
            double score = eval.meanMinerals - 0.5 * eval.stdMinerals;
            if (eval.returnRate < 1.0) score -= 1_000.0;

            System.Diagnostics.Debug.WriteLine(
                $"[SeedSweep] seed={seed} mean={eval.meanMinerals:F2} std={eval.stdMinerals:F2} " +
                $"returnRate={eval.returnRate:P1} score={score:F2}");

            if (score > bestScore)
            {
                bestScore = score;
                best = run.bestTable;
                bestResult = run.result;
            }
        }

        if (best == null || bestResult == null)
            return TrainCore(episodes, onProgress, options);

        SaveModel(best, episodes, bestResult.BestMinerals, options.ProfileName);
        return (bestResult, best);
    }

    private (double meanMinerals, double stdMinerals, double returnRate) EvaluatePolicy(
        QTable table,
        SeedSweepOptions seedSweep)
    {
        int evalRepeats = Math.Max(1, seedSweep.EvalEpisodesPerSeed);
        var validationMaps = BuildValidationMaps(seedSweep).ToList();
        if (validationMaps.Count == 0)
            validationMaps.Add(_map.Clone());

        var samples = new List<double>(validationMaps.Count * evalRepeats);
        int returned = 0;

        for (int i = 0; i < validationMaps.Count; i++)
        {
            var evalMap = validationMaps[i];
            for (int rep = 0; rep < evalRepeats; rep++)
            {
                var evalRunner = new SimulationRunner(
                    evalMap.Clone(),
                    _durationHours,
                    $"{_modelPath}_eval_tmp");

                var log = evalRunner.RunSimulation(
                    tableOverride: table,
                    missionEndMode: MissionEndMode.ContinueUntilDeadline);

                if (log.Count == 0) continue;
                var final = log[^1];
                samples.Add(final.TotalMinerals);
                bool returnedHome = final.X == evalMap.StartX && final.Y == evalMap.StartY;
                if (returnedHome) returned++;
            }
        }

        double mean = samples.Count == 0 ? 0.0 : samples.Average();
        double var = 0.0;
        foreach (var s in samples)
            var += (s - mean) * (s - mean);
        double std = samples.Count == 0 ? 0.0 : Math.Sqrt(var / samples.Count);
        return (mean, std, samples.Count == 0 ? 0.0 : returned / (double)samples.Count);
    }

    private IEnumerable<GameMap> BuildValidationMaps(SeedSweepOptions seedSweep)
    {
        if (seedSweep.IncludeOfficialMapInValidation)
            yield return _map.Clone();

        var seeds = seedSweep.ValidationMapSeeds ?? [20_001, 20_101, 20_201];
        foreach (int seed in seeds.Distinct())
            yield return BuildGeneratedMap(seed);
    }

    private static GameMap BuildGeneratedMap(int seed)
    {
        string content = MapGenerator.GenerateMap(seed);
        string[] lines = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return GameMap.Parse(lines);
    }

    private (TrainingResult result, QTable bestTable) TrainCore(
        int episodes,
        Action<TrainingProgress>? onProgress,
        TrainingOptions options,
        QTable? warmStartTable = null,
        int seedOffset = 0,
        bool persistModel = true)
    {
        // ── Load or create Q-table ────────────────────────────────────────────
        // The Q-table values (the actual learned knowledge) are loaded from disk.
        // Epsilon is NOT restored from the saved meta — a resumed run should
        // re-explore at a moderate rate so it can improve on the existing table.
        // Restoring the saved epsilon (typically 0.05 after a converged run)
        // produces epsilonDecay = 1.0 for the entire run — the agent barely
        // explores and the Q-table barely changes, making the resume pointless.
        ValidateSavedModelCompatibility();
        var sharedTable  = warmStartTable
                         ?? (HasSavedModel ? QTable.Load(QTablePath) : new QTable());
        double startEpsilon = HasSavedModel ? 0.4 : 1.0;
        // Fresh run: 1.0 → 0.05 over N episodes (full exploration → convergence)
        // Resume:    0.4 → 0.05 over N episodes (re-explore without throwing away knowledge)

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
                        sharedTable,
                        iterEps,
                        seed: options.EpisodeSeedOffset + seedOffset + epBase + threadId,
                        options.MissionEndMode,
                        options.UseAdaptiveEpsilon,
                        options.AdaptiveEpsilonMax);
                });

            // ── SERIAL MERGE PHASE ────────────────────────────────────────────
            for (int i = 0; i < collected.Length; i++)
            {
                var ep = collected[i];
                int epsDone = epBase + i + 1;

                allRewards.Add(ep.TotalReward);

                // ② ELIGIBILITY TRACES: always apply to sharedTable first
                if (ep.Transitions.Count > 0)
                    ApplyEligibilityTraces(ep.Transitions, sharedTable,
                                           options.Lambda, options.TraceThreshold);

                if (ep.MineralsCollected > bestMinerals)
                {
                    bestMinerals = ep.MineralsCollected;
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
                        ActionCount);
                    buffer.Push(t.StateKey, t.ActionIdx, t.Reward, t.NextStateKey,
                                t.IsTerminal, Math.Abs(tdErr) + 1e-4);
                }

                // Epsilon decay: once per completed episode.
                epsilon = Math.Max(epsilonMin, epsilon * epsilonDecay);

                // Emit one progress item per completed run using current run metrics,
                // not batch averages or best-only values.
                onProgress?.Invoke(new TrainingProgress(
                    Episode:      epsDone,
                    TotalEps:     episodes,
                    Epsilon:      epsilon,
                    LastReward:   ep.TotalReward,
                    BestMinerals: bestMinerals,
                    StatesKnown:  sharedTable.StateCount,
                    BufferSize:   buffer.Count,
                    LastBattery:  ep.EndBattery,
                    LastMinerals: ep.MineralsCollected,
                    Snapshot:     BuildSnapshot(ep, epsDone)));
            }

            // ③ PER REPLAY: sample and update Q-table once buffer is warm
            int epsDoneBatch = epBase + thisCount;
            if (epsDoneBatch >= WarmupEpisodes && buffer.IsReady(BatchSize))
            {
                double betaProgress = Math.Clamp(epsDoneBatch / (double)Math.Max(episodes, 1), 0.0, 1.0);
                double beta = PerBetaStart + (PerBetaEnd - PerBetaStart) * betaProgress;
                var replayDiversity = options.ReplayDiversity ?? new ReplayDiversityOptions();
                var (batch, indices, isWeights) = ShouldUseDiversityReplay(options)
                    ? buffer.SampleWithDiversity(BatchSize, beta, replayDiversity.StratifiedFraction)
                    : buffer.Sample(BatchSize, beta);

                for (int i = 0; i < batch.Length; i++)
                {
                    var t = batch[i];
                    double weightedAlpha = sharedTable.LearningRate * isWeights[i];
                    sharedTable.UpdateByKeyWithAlpha(
                        t.StateKey, t.ActionIdx, t.Reward, t.NextStateKey,
                        ActionCount, weightedAlpha);

                    double newErr = sharedTable.GetTDError(
                        t.StateKey, t.ActionIdx, t.Reward, t.NextStateKey,
                        ActionCount);
                    buffer.UpdatePriority(indices[i], newErr);
                }
            }

            // ③ LEARNING RATE DECAY: once per iteration
            sharedTable.DecayLearningRate();
        }

        // Save the best checkpoint — the table that actually achieved bestMinerals
        // not the final table which may have been updated by worse later episodes
        var tableToSave = bestTable.StateCount > 0 ? bestTable : sharedTable;
        if (persistModel)
            SaveModel(tableToSave, episodes, bestMinerals, options.ProfileName);

        return (new TrainingResult(episodes, bestMinerals, allRewards, sharedTable.StateCount),
                tableToSave);
    }

    // ── Single episode runner (runs on a thread) ──────────────────────────────

    private EpisodeData RunEpisode(
        QTable sharedTable,
        double epsilon,
        int seed,
        MissionEndMode missionEndMode = MissionEndMode.ContinueUntilDeadline,
        bool useAdaptiveEpsilon = false,
        double adaptiveEpsilonMax = 1.0)
    {
        var episodeMap = _map.Clone();
        var engine     = new SimulationEngine(episodeMap, _durationHours);
        // Each thread gets its own agent — reads sharedTable, never writes
        var agent      = new HybridAgent(episodeMap, _totalTicks, sharedTable, seed);
        agent.Epsilon  = epsilon;
        agent.UseAdaptiveEpsilon = useAdaptiveEpsilon;
        agent.AdaptiveEpsilonMax = adaptiveEpsilonMax;
        agent.ResetNavigation();

        var    transitions  = new List<StrategicTransition>();
        var    steps        = new List<StepRecord>();
        double totalReward  = 0;

        while (!engine.IsFinished && !engine.BatteryDead
               && (missionEndMode == MissionEndMode.ContinueUntilDeadline
                   || agent.CurrentPhase != MissionPhase.AtBase))
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

            // Ghost record — expand multi-step moves into one record per cell
            // so the replay trail shows every individual cell the rover visited,
            // not just the tick start position (which skips 2 cells on Fast moves).
            if (action.Type == RoverActionType.Move && action.Directions is { Count: > 1 })
            {
                int rx = state.X, ry = state.Y;
                for (int s = 0; s < action.Directions.Count; s++)
                {
                    var (nx, ny) = episodeMap.ApplyDirection(rx, ry, action.Directions[s]);
                    bool isLast  = s == action.Directions.Count - 1;
                    var stepEvt  = isLast && collected    ? StepEvent.MineSuccess
                                 : isLast && engine.BatteryDead  ? StepEvent.BatteryDead
                                 : isLast && returnedHome ? StepEvent.ReturnHome
                                 : isLast && newState.Battery < 5  ? StepEvent.BatteryCritical
                                 : isLast && newState.Battery < 20 ? StepEvent.BatteryLow
                                 : StepEvent.Move;
                    steps.Add(new StepRecord(rx, ry, stepEvt, isLast ? reward : 0));
                    rx = nx; ry = ny;
                }
            }
            else
            {
                var evt = collected              ? StepEvent.MineSuccess
                        : engine.BatteryDead     ? StepEvent.BatteryDead
                        : returnedHome           ? StepEvent.ReturnHome
                        : newState.Battery < 5   ? StepEvent.BatteryCritical
                        : newState.Battery < 20  ? StepEvent.BatteryLow
                        : action.Type == RoverActionType.Standby ? StepEvent.Standby
                        : StepEvent.Move;
                steps.Add(new StepRecord(state.X, state.Y, evt, reward));
            }

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
            ReturnedHome:      returnedOk,
            EndBattery:        final.Battery);
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
        List<StrategicTransition> transitions,
        QTable table,
        double lambda,
        double traceThreshold)
    {
        const double gamma  = 0.95;
        int nAct = ActionCount;

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
                if (newE < traceThreshold)
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

    // ── Live simulation ───────────────────────────────────────────────────────

    /// <summary>
    /// Runs one deployment pass with epsilon=0 (pure exploitation, no randomness).
    ///
    /// Exploration passes were tried here (ε=0.05 with isTraining=true) to try
    /// to replicate lucky training episodes, but they bypassed the battery/time
    /// safety guards in PickBestMineral — random decisions don't check feasibility
    /// — causing the rover to miss the return window and die in the field.
    /// The correct answer to "simulation scores lower than training best" is
    /// more training episodes so the Q-table converges to the better policy.
    /// </summary>
    public List<SimulationLogEntry> RunSimulation(
        QTable? tableOverride = null,
        MissionEndMode missionEndMode = MissionEndMode.ContinueUntilDeadline)
    {
        if (tableOverride == null)
            ValidateSavedModelCompatibility();
        var table = tableOverride
                    ?? (HasSavedModel ? QTable.Load(QTablePath) : new QTable());

        var agent = new HybridAgent(_map, _totalTicks, table, seed: 0);
        agent.Epsilon = 0.0;
        agent.ResetNavigation();

        var liveMap = _map.Clone();
        var engine  = new SimulationEngine(liveMap, _durationHours);
        var log     = new List<SimulationLogEntry>();

        while (!engine.IsFinished && !engine.BatteryDead
               && (missionEndMode == MissionEndMode.ContinueUntilDeadline
                   || agent.CurrentPhase != MissionPhase.AtBase))
        {
            var state  = engine.GetState();
            var action = agent.SelectAction(state, liveMap, isTraining: false);
            var entry  = engine.Step(action, agent.CurrentPhase.ToString());
            if (entry == null) break;

            // Expand multi-step moves into one log entry per cell so the
            // map playback shows every individual cell the rover visited.
            if (action.Type == RoverActionType.Move
                && action.Directions is { Count: > 1 })
            {
                int rx = state.X, ry = state.Y;
                for (int s = 0; s < action.Directions.Count; s++)
                {
                    var (nx, ny) = liveMap.ApplyDirection(rx, ry, action.Directions[s]);
                    bool isLast  = s == action.Directions.Count - 1;
                    // Intermediate cells share the tick's battery/mineral state.
                    // Only the final cell gets the real entry data.
                    log.Add(isLast ? entry with { X = nx, Y = ny }
                                   : entry with { X = nx, Y = ny,
                                                  EventNote = "" });
                    rx = nx; ry = ny;
                }
            }
            else
            {
                log.Add(entry);
            }
        }

        return log;
    }

    public (TrainingResult training, List<SimulationLogEntry> log) TrainAndRun(
        int episodes = 1000,
        Action<TrainingProgress>? onProgress = null)
        => TrainAndRun(episodes, onProgress, options: null);

    public (TrainingResult training, List<SimulationLogEntry> log) TrainAndRun(
        int episodes,
        Action<TrainingProgress>? onProgress,
        TrainingOptions? options)
    {
        var effective = GetEffectiveOptions(options);
        // Train returns the best Q-table snapshot in addition to the result
        var (training, bestTable) = TrainWithBest(episodes, onProgress, effective);
        // Pass in-memory bestTable directly — no disk round-trip, guaranteed correct policy
        var log = RunSimulation(
            tableOverride: bestTable,
            missionEndMode: effective.MissionEndMode);
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

    private static TrainingOptions GetEffectiveOptions(TrainingOptions? options)
    {
        var effective = options ?? TrainingProfileFactory.CreateOptions(TrainingProfileFactory.DefaultProfile);
        if (string.IsNullOrWhiteSpace(effective.ProfileName))
            effective = effective with { ProfileName = TrainingProfileFactory.DefaultProfile.ToString() };
        return effective;
    }

    public static bool ShouldUseDiversityReplay(TrainingOptions options)
        => (options.ReplayDiversity ?? new ReplayDiversityOptions()).Enabled;

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
        // Normalise: strip any existing prefix then re-apply, same as constructor
        string resolvedPath = ResolveModelPath(modelPath);

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
            Epsilon:           0.4, // resume epsilon — always resets to this, not saved
            SavedAt:           meta.SavedAt,
            StatesKnown:       stateCount,
            PolicySchemaVersion: meta.PolicySchemaVersion,
            TrainingProfile:   string.IsNullOrWhiteSpace(meta.TrainingProfile) ? "legacy" : meta.TrainingProfile);
    }

    public static ModelResetResult ResetSavedModel(string modelPath, bool archive = true)
    {
        string resolvedPath = ResolveModelPath(modelPath);
        string qtablePath   = resolvedPath + ".qtable.json";
        string metaPath     = resolvedPath + ".meta.json";

        var modelFiles = new List<string>(2);
        if (File.Exists(qtablePath)) modelFiles.Add(qtablePath);
        if (File.Exists(metaPath)) modelFiles.Add(metaPath);

        if (modelFiles.Count == 0)
            return new ModelResetResult(
                HadModel: false,
                Archived: false,
                ArchiveDirectory: null,
                ProcessedFiles: Array.Empty<string>());

        if (archive)
        {
            string archiveDir = Path.Combine(
                SavedModelsDir,
                "archive",
                $"{Path.GetFileName(resolvedPath)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(archiveDir);

            foreach (string file in modelFiles)
            {
                string dest = Path.Combine(archiveDir, Path.GetFileName(file));
                File.Move(file, dest, overwrite: true);
            }

            return new ModelResetResult(
                HadModel: true,
                Archived: true,
                ArchiveDirectory: archiveDir,
                ProcessedFiles: modelFiles);
        }

        foreach (string file in modelFiles)
            File.Delete(file);

        return new ModelResetResult(
            HadModel: true,
            Archived: false,
            ArchiveDirectory: null,
            ProcessedFiles: modelFiles);
    }

    private void ValidateSavedModelCompatibility()
    {
        if (!HasSavedModel) return;
        var meta = LoadMeta();
        if (meta.PolicySchemaVersion != PolicySchemaVersion)
        {
            throw new InvalidOperationException(
                $"Saved model schema mismatch. Expected v{PolicySchemaVersion}, found v{meta.PolicySchemaVersion}. " +
                "Retraining is required.");
        }
    }

    private void SaveModel(
        QTable table,
        int episodesCompleted,
        int bestMinerals,
        string profileName)
    {
        Directory.CreateDirectory(SavedModelsDir);
        table.Save(QTablePath);

        var meta = new ModelMeta
        {
            EpisodesCompleted = episodesCompleted,
            BestMinerals      = bestMinerals,
            SavedAt           = DateTime.UtcNow.ToString("o"),
            PolicySchemaVersion = PolicySchemaVersion,
            TrainingProfile   = profileName
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

public record ModelResetResult(
    bool HadModel,
    bool Archived,
    string? ArchiveDirectory,
    IReadOnlyList<string> ProcessedFiles);

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
    bool                      ReturnedHome,
    double                    EndBattery);

internal class ModelMeta
{
    public int    EpisodesCompleted { get; set; } = 0;
    public int    BestMinerals      { get; set; } = 0;
    public string SavedAt           { get; set; } = "";
    public int    PolicySchemaVersion { get; set; } = 0;
    public string TrainingProfile   { get; set; } = "legacy";
}
