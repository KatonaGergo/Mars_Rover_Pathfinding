using MarsRover.Core.Models;
using MarsRover.Core.Simulation;

namespace MarsRover.Core.Algorithm;

/// <summary>
/// THE HYBRID AGENT  (Q-table strategy + A* navigation)
///
/// REGRESSION FIXES (vs previous version):
///   1. NavigateTo now queues ALL steps including step 0 and returns DequeueMove.
///      Previously it returned a single-direction Move for the first step, paying
///      Fast energy (18%) but moving only 1 cell instead of 3. Now every tick
///      uses the full movement budget.
///   2. BestAffordableSpeedForLeg takes the actual battery-at-mineral as input.
///      Previously BestAffordableSpeedFrom re-estimated the forward leg cost using
///      Normal speed regardless of what speed was actually used, overestimating
///      battery by up to 12% and causing valid minerals to be rejected.
///   3. State space reduced from 76,800 → 1,800 states. The Q-table only makes
///      4 strategic decisions; it doesn't need 8 direction bins or 10 battery bins
///      to do that. Fewer states means every state gets visited ~10× per episode
///      instead of never, which is the difference between convergence and noise.
///      1,800 = 5×3×5×4×3×2 (SolPhase replaces IsDay for multi-sol awareness).
/// </summary>
public class HybridAgent
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const int    ReturnSafetyBuffer  = 1;
    private const double BatterySafetyMargin = 3.0;
    private const double LookaheadWeight     = 0.35;
    private const int    LookaheadTopK       = 15;
    private const int    LocalDensityRadius  = 2;
    private const double ObstaclePenaltyConservative = 4.0;
    private const double ObstaclePenaltyAggressive   = 2.5;
    private static readonly RoverSpeed[] FastToSlowSpeeds =
        [RoverSpeed.Fast, RoverSpeed.Normal, RoverSpeed.Slow];

    // ── Fields ────────────────────────────────────────────────────────────────
    private readonly QTable  _qTable;
    private readonly Random  _random;
    private readonly GameMap _map;
    private readonly int     _totalTicks;

    public double      Epsilon         { get; set; } = 1.0;
    public double      EpsilonMin      { get; set; } = 0.05;
    public double      EpsilonDecay    { get; set; } = 0.995;
    public bool        UseAdaptiveEpsilon { get; set; } = false;
    public double      AdaptiveEpsilonMax { get; set; } = 1.0;
    public QTable      QTable          => _qTable;
    public MissionPhase CurrentPhase   => _phase;
    public int         LastDecisionIdx { get; private set; } = -1;

    private Queue<PathStep> _currentPath = new();
    private MissionPhase    _phase       = MissionPhase.Collection;
    private int[,]?         _baseDistanceField;
    private int[,]?         _cachedDistanceField;
    private int             _cachedFieldX = int.MinValue;
    private int             _cachedFieldY = int.MinValue;
    private readonly Dictionary<string, int> _stateVisits = new();
    private double?         _prevDecisionBattery;

    public HybridAgent(GameMap map, int totalTicks, QTable? existingTable = null, int seed = 42)
    {
        _map        = map;
        _totalTicks = totalTicks;
        _qTable     = existingTable ?? new QTable();
        _random     = new Random(seed);
    }

    // ── Main entry point ──────────────────────────────────────────────────────

    public (RoverAction action, int decisionIdx) SelectActionWithDecision(
        RoverState state, GameMap liveMap, bool isTraining = false)
    {
        LastDecisionIdx = -1;

        if (_phase == MissionPhase.AtBase)
        {
            if (!ShouldLeaveBase(state, liveMap))
                return (RoverAction.StandbyAction, -1);
            _phase = MissionPhase.Collection;
        }

        if (_phase != MissionPhase.Returning && MustReturnNow(state, liveMap))
        {
            _phase = MissionPhase.Returning;
            _currentPath.Clear();
        }

        // Mine if standing on mineral
        if (liveMap.HasMineral(state.X, state.Y))
        {
            _currentPath.Clear();
            return (RoverAction.MineAction, -1);
        }

        // Follow existing path
        if (_currentPath.Count > 0)
        {
            if (_phase == MissionPhase.Returning)
            {
                var detour = FindDetourMineral(state, liveMap);
                if (detour.HasValue)
                {
                    _currentPath.Clear();
                    return (NavigateTo(detour.Value.x, detour.Value.y, state, liveMap), -1);
                }
            }
            else
            {
                // Collection mid-path interrupt: grab a target only if it is
                // strictly closer than continuing the currently queued path.
                var closer = FindCloserCollectionTarget(state, liveMap);
                if (closer.HasValue)
                {
                    _currentPath.Clear();
                    return (NavigateTo(closer.Value.x, closer.Value.y, state, liveMap), -1);
                }
            }
            return (DequeueMove(state, liveMap), -1);
        }

        if (_phase == MissionPhase.Returning)
            return (NavigateHome(state, liveMap), -1);

        // Strategic Q-table decision
        var decision = MakeStrategicDecision(state, liveMap, isTraining);
        var action   = ExecuteDecision(decision, state, liveMap);
        LastDecisionIdx = DecisionToIndex(decision);
        _prevDecisionBattery = state.Battery;
        return (action, LastDecisionIdx);
    }

    public RoverAction SelectAction(RoverState state, GameMap liveMap, bool isTraining = false)
        => SelectActionWithDecision(state, liveMap, isTraining).action;

    // ── Phase logic ───────────────────────────────────────────────────────────

    private bool ShouldLeaveBase(RoverState state, GameMap liveMap)
    {
        if (liveMap.RemainingMinerals.Count == 0) return false;
        int ticksRemaining = _totalTicks - state.Tick;
        if (ticksRemaining <= ReturnSafetyBuffer) return false;

        // Require minMarginTicks=1 so we only leave base if there is at least
        // one mineral whose full round-trip (travel + mine + return) still leaves
        // 1 tick to spare. This is stronger than checking MustReturnNow at base:
        // at base returnSteps=0, so MustReturnNow is nearly always false (marginTicks
        // = ticksRemaining-1 > 0), which means that guard does nothing.
        // A positive post-mine return slack guarantees the rover won't immediately
        // trigger MustReturnNow after launching, preventing the leave-return loop.
        var roverField = GetDistanceField(state.X, state.Y, liveMap);
        var baseField  = GetBaseDistanceField(liveMap);
        var conservative = EvaluateFeasibleMinerals(
            state, liveMap, roverField, baseField, RiskMode.Conservative,
            minMarginTicks: 1);
        if (conservative.Count > 0) return true;

        var aggressive = EvaluateFeasibleMinerals(
            state, liveMap, roverField, baseField, RiskMode.Aggressive,
            minMarginTicks: 1);
        return aggressive.Count > 0;
    }

    private static int MarginBucketFromTicks(int marginTicks) => marginTicks switch
    {
        <= 0 => 0,
        <= 2 => 1,
        <= 5 => 2,
        _    => 3
    };

    private int ComputeReturnMarginTicks(RoverState state, GameMap liveMap)
    {
        int ticksRemaining = _totalTicks - state.Tick;
        int returnSteps    = DistanceToBaseSteps(state, liveMap);
        if (returnSteps == int.MaxValue) return int.MinValue;

        var plan = SimulateReturnLegAggressive(state.Tick, returnSteps, state.Battery);
        if (!plan.IsFeasible) return int.MinValue;
        return ticksRemaining - (plan.Ticks + ReturnSafetyBuffer);
    }

    private bool MustReturnNow(RoverState state, GameMap liveMap)
    {
        int ticksRemaining = _totalTicks - state.Tick;
        int returnSteps = DistanceToBaseSteps(state, liveMap);
        if (returnSteps == int.MaxValue) return true;

        var plan = SimulateReturnLegAggressive(state.Tick, returnSteps, state.Battery);
        if (!plan.IsFeasible) return true;

        int marginTicks = ticksRemaining - (plan.Ticks + ReturnSafetyBuffer);
        return marginTicks <= 0;
    }

    private RoverAction NavigateHome(RoverState state, GameMap liveMap)
    {
        if (state.X == liveMap.StartX && state.Y == liveMap.StartY)
        {
            _phase = MissionPhase.AtBase;
            _currentPath.Clear();
            return RoverAction.StandbyAction;
        }
        return NavigateTo(liveMap.StartX, liveMap.StartY, state, liveMap);
    }

    private (int x, int y)? FindDetourMineral(
        RoverState state, GameMap liveMap, RiskMode risk = RiskMode.Conservative)
    {
        if (liveMap.RemainingMinerals.Count == 0) return null;
        int ticksRemaining = _totalTicks - state.Tick;
        var roverField     = GetDistanceField(state.X, state.Y, liveMap);
        var baseField      = GetBaseDistanceField(liveMap);
        int directSteps    = FieldDistance(roverField, liveMap.StartX, liveMap.StartY);
        if (directSteps == int.MaxValue) return null;

        (int x, int y)? best   = null;
        double bestScore       = double.MinValue;
        double requiredMargin  = RequiredBatteryMargin(risk);
        double detourPenalty   = risk == RiskMode.Aggressive ? 0.9 : 1.35;
        double obstaclePenalty = risk == RiskMode.Aggressive
            ? ObstaclePenaltyAggressive
            : ObstaclePenaltyConservative;
        var legToMineralCache = new Dictionary<int, LegPlan>();

        foreach (var pos in liveMap.RemainingMinerals)
        {
            int stepsToMineral = FieldDistance(roverField, pos.x, pos.y);
            if (stepsToMineral == int.MaxValue) continue;

            int stepsHome = FieldDistance(baseField, pos.x, pos.y);
            if (stepsHome == int.MaxValue) continue;

            int detourCost = Math.Max(0, stepsToMineral + stepsHome - directSteps);

            if (!legToMineralCache.TryGetValue(stepsToMineral, out var legToMineral))
            {
                legToMineral = SimulateLegWithPolicy(
                    state.Tick, stepsToMineral, state.Battery);
                legToMineralCache[stepsToMineral] = legToMineral;
            }
            if (!legToMineral.IsFeasible) continue;

            int tickAtMineral     = state.Tick + legToMineral.Ticks;
            double battAfterMine  = ApplyMiningTick(legToMineral.FinalBattery, tickAtMineral);
            if (battAfterMine < requiredMargin) continue;

            var legHome = SimulateReturnLegAggressive(tickAtMineral + 1, stepsHome, battAfterMine);
            if (!legHome.IsFeasible) continue;

            int missionTicks = legToMineral.Ticks + 1 + legHome.Ticks + ReturnSafetyBuffer;
            int marginTicks  = ticksRemaining - missionTicks;
            if (marginTicks < 0)
                continue;
            if (legHome.MinBattery < requiredMargin) continue;
            double localObstacleDensity = LocalObstacleDensity(
                pos.x, pos.y, liveMap, LocalDensityRadius);

            // Keep margin as a feasibility filter; rank by local efficiency only.
            double score = 100.0 / (stepsToMineral + 1.0)
                         - localObstacleDensity * obstaclePenalty
                         - detourCost * detourPenalty
                         - stepsHome * 0.05;
            if (score > bestScore)
            {
                bestScore = score;
                best = (pos.x, pos.y);
            }
        }

        return best;
    }

    /// <summary>
    /// During collection path-following, opportunistically switch only if a
    /// mineral is strictly closer than the current queued path remainder.
    /// Uses the same safety feasibility checks as normal target evaluation.
    /// </summary>
    private (int x, int y)? FindCloserCollectionTarget(RoverState state, GameMap liveMap)
    {
        if (liveMap.RemainingMinerals.Count == 0) return null;
        int remainingPathSteps = _currentPath.Count;
        if (remainingPathSteps <= 1) return null;

        int ticksRemaining = _totalTicks - state.Tick;
        var roverField = GetDistanceField(state.X, state.Y, liveMap);
        var baseField = GetBaseDistanceField(liveMap);

        (int x, int y)? best = null;
        int bestSteps = remainingPathSteps; // must beat this to qualify
        var legToMineralCache = new Dictionary<int, LegPlan>();

        foreach (var pos in liveMap.RemainingMinerals)
        {
            // Chebyshev is a lower bound on BFS distance — reject before
            // touching the distance field if geometry alone rules it out.
            if (liveMap.ChebyshevDistance(state.X, state.Y, pos.x, pos.y) >= bestSteps) continue;

            int stepsToMineral = FieldDistance(roverField, pos.x, pos.y);
            if (stepsToMineral == int.MaxValue) continue;
            if (stepsToMineral >= bestSteps) continue; // strict anti-thrash

            int stepsHome = FieldDistance(baseField, pos.x, pos.y);
            if (stepsHome == int.MaxValue) continue;

            if (!legToMineralCache.TryGetValue(stepsToMineral, out var legToMineral))
            {
                legToMineral = SimulateLegWithPolicy(
                    state.Tick, stepsToMineral, state.Battery);
                legToMineralCache[stepsToMineral] = legToMineral;
            }
            if (!legToMineral.IsFeasible) continue;

            int tickAtMineral = state.Tick + legToMineral.Ticks;
            double battAfterMine = ApplyMiningTick(legToMineral.FinalBattery, tickAtMineral);
            if (battAfterMine < BatterySafetyMargin) continue;

            var legHome = SimulateReturnLegAggressive(
                tickAtMineral + 1, stepsHome, battAfterMine);
            if (!legHome.IsFeasible) continue;
            if (legHome.MinBattery < BatterySafetyMargin) continue;

            int missionTicks = legToMineral.Ticks + 1 + legHome.Ticks + ReturnSafetyBuffer;
            if (ticksRemaining - missionTicks < 0) continue;

            bestSteps = stepsToMineral;
            best = (pos.x, pos.y);
        }

        return best;
    }

    // ── Strategic decision ────────────────────────────────────────────────────

    private HybridDecision MakeStrategicDecision(RoverState state, GameMap liveMap, bool isTraining)
    {
        var qState = BuildQLearningState(state, liveMap);
        string key = qState.ToKey();
        double eps = isTraining ? EffectiveEpsilonForState(key) : 0.0;
        if (isTraining && _random.NextDouble() < eps)
            return ExploreRandomDecision(liveMap);
        return ExploitBestDecision(qState, liveMap);
    }

    private double EffectiveEpsilonForState(string stateKey)
    {
        if (!UseAdaptiveEpsilon) return Epsilon;

        int visits = _stateVisits.GetValueOrDefault(stateKey, 0) + 1;
        _stateVisits[stateKey] = visits;

        double visitFactor = 1.0 / Math.Sqrt(visits);
        double actionGap = ActionGap(stateKey);
        double actionGapFactor = 1.0 / (1.0 + Math.Max(0.0, actionGap));
        double eps = Epsilon * visitFactor * actionGapFactor * 2.0;
        return Math.Clamp(eps, EpsilonMin, AdaptiveEpsilonMax);
    }

    private double ActionGap(string stateKey)
    {
        int n = (int)HybridDecision.Count;
        double best = double.MinValue;
        double second = double.MinValue;

        for (int i = 0; i < n; i++)
        {
            double q = _qTable.GetByKey(stateKey, i, n);
            if (q > best)
            {
                second = best;
                best = q;
            }
            else if (q > second)
            {
                second = q;
            }
        }

        if (best == double.MinValue || second == double.MinValue) return 0.0;
        return best - second;
    }

    private HybridDecision ExploitBestDecision(HybridQLearningState qState, GameMap liveMap)
    {
        int idx      = _qTable.BestActionIndex(qState.ToKey(), (int)HybridDecision.Count);
        var decision = IndexToDecision(idx);

        // Safety guards: the Q-table can learn suboptimal ReturnToBase or Standby
        // decisions from noisy early exploration. MustReturnNow handles the real
        // return trigger via exact battery/time math — the Q-table should never
        // override that with a premature ReturnToBase while minerals still exist.
        if (liveMap.RemainingMinerals.Count > 0)
        {
            if (decision == HybridDecision.ReturnToBase) return HybridDecision.SeekBestConservative;
            if (decision == HybridDecision.Standby)      return HybridDecision.SeekBestConservative;
        }

        return decision;
    }

    private HybridDecision ExploreRandomDecision(GameMap liveMap)
    {
        if (liveMap.RemainingMinerals.Count == 0) return HybridDecision.ReturnToBase;
        double r = _random.NextDouble();
        if (r < 0.45) return HybridDecision.SeekBestConservative;
        if (r < 0.70) return HybridDecision.SeekBestAggressive;
        if (r < 0.85) return HybridDecision.SeekNearestConservative;
        if (r < 0.95) return HybridDecision.SeekNearestAggressive;
        return HybridDecision.ReturnToBase;
    }

    private RoverAction ExecuteDecision(HybridDecision decision, RoverState state, GameMap liveMap)
    {
        (int tx, int ty)? target = decision switch
        {
            HybridDecision.SeekBestConservative    => PickBestMineral(state, liveMap, RiskMode.Conservative),
            HybridDecision.SeekBestAggressive      => PickBestMineral(state, liveMap, RiskMode.Aggressive),
            HybridDecision.SeekNearestConservative => PickNearestMineral(state, liveMap, RiskMode.Conservative),
            HybridDecision.SeekNearestAggressive   => PickNearestMineral(state, liveMap, RiskMode.Aggressive),
            HybridDecision.ReturnToBase            => (liveMap.StartX, liveMap.StartY),
            _                                      => null
        };

        if (target == null && decision != HybridDecision.ReturnToBase)
        {
            // Corridor opportunism: before committing to home, check if a nearby
            // margin-safe mineral has become viable along the return corridor.
            var corridor = FindDetourMineral(
                state, liveMap,
                decision is HybridDecision.SeekBestAggressive
                or HybridDecision.SeekNearestAggressive
                    ? RiskMode.Aggressive
                    : RiskMode.Conservative);

            if (corridor.HasValue)
                return NavigateTo(corridor.Value.x, corridor.Value.y, state, liveMap);

            _phase = MissionPhase.Returning;
            _currentPath.Clear();
            return NavigateHome(state, liveMap);
        }

        if (target == null || (target.Value.tx == state.X && target.Value.ty == state.Y))
            return RoverAction.StandbyAction;

        return NavigateTo(target.Value.tx, target.Value.ty, state, liveMap);
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    /// <summary>
    /// FIX: Now queues ALL path steps (including index 0) and calls DequeueMove,
    /// so the first tick gets the full multi-step movement budget.
    /// Previously returned a single-direction Move for step 0, paying Fast
    /// energy (18%) but moving only 1 cell.
    /// </summary>
    private RoverAction NavigateTo(int tx, int ty, RoverState state, GameMap liveMap)
    {
        var path = AStarPathfinder.FindPath(liveMap, state.X, state.Y, tx, ty);

        if (path.Count == 0)
        {
            if (_phase == MissionPhase.Returning) return RoverAction.StandbyAction;
            var fallback = liveMap.NearestMineral(state.X, state.Y);
            if (fallback == null) return RoverAction.StandbyAction;
            path = AStarPathfinder.FindPath(liveMap, state.X, state.Y,
                                             fallback.Value.x, fallback.Value.y);
            if (path.Count == 0) return RoverAction.StandbyAction;
        }

        // Queue ALL steps — DequeueMove will pack up to speed steps per tick
        _currentPath.Clear();
        foreach (var step in path)
            _currentPath.Enqueue(step);

        return DequeueMove(state, liveMap);
    }

    /// <summary>
    /// Packs up to (int)speed queued path steps into a single free-movement action.
    /// Each step can be in a different direction — this is the free-movement model.
    /// Stops early before a mineral so mining fires cleanly on the next tick.
    ///
    /// Passes the real queue depth to ChooseSpeed so speed is capped to the
    /// number of steps actually needed — no paying for Fast when 1 step remains.
    /// </summary>
    private RoverAction DequeueMove(RoverState state, GameMap liveMap)
    {
        var speed = ChooseSpeed(state, liveMap, stepsRemaining: _currentPath.Count);
        int steps = (int)speed;
        var dirs  = new List<Direction>(steps);

        int simX = state.X;
        int simY = state.Y;

        for (int i = 0; i < steps && _currentPath.Count > 0; i++)
        {
            var next     = _currentPath.Peek();
            var (nx, ny) = liveMap.ApplyDirection(simX, simY, next.Direction);
            if (!liveMap.IsPassable(nx, ny)) break;

            _currentPath.Dequeue();
            dirs.Add(next.Direction);
            simX = nx;
            simY = ny;

            // Stop at mineral so next tick triggers mining cleanly
            if (liveMap.HasMineral(simX, simY)) break;
        }

        if (dirs.Count == 0)
        {
            _currentPath.Clear();
            return RoverAction.StandbyAction;
        }

        return RoverAction.Move(dirs, speed);
    }

    // ── Target selection ──────────────────────────────────────────────────────

    private static double RequiredBatteryMargin(RiskMode mode)
        => mode == RiskMode.Aggressive ? BatterySafetyMargin : BatterySafetyMargin + 1.5;

    private static double LookaheadMultiplier(RiskMode mode)
        => mode == RiskMode.Aggressive ? 1.25 : 0.85;

    private (int x, int y)? PickBestMineral(
        RoverState state, GameMap liveMap, RiskMode mode)
    {
        if (liveMap.RemainingMinerals.Count == 0) return null;

        var roverField = GetDistanceField(state.X, state.Y, liveMap);
        var baseField  = GetBaseDistanceField(liveMap);
        var feasible   = EvaluateFeasibleMinerals(state, liveMap, roverField, baseField, mode);
        if (feasible.Count == 0) return null;

        feasible.Sort((a, b) => b.BaseScore.CompareTo(a.BaseScore));

        (int x, int y)? best = null;
        double bestScore = double.MinValue;

        int lookaheadCount = Math.Min(feasible.Count, LookaheadTopK);
        for (int i = 0; i < feasible.Count; i++)
        {
            var plan  = feasible[i];
            double score = plan.BaseScore;

            if (i < lookaheadCount)
            {
                score += (LookaheadWeight * LookaheadMultiplier(mode))
                       * BestSecondLegScore(plan, liveMap, baseField, state.Tick, mode);
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = (plan.X, plan.Y);
            }
        }

        return best;
    }

    /// <summary>
    /// SeekNearestMineral target: same feasibility guards as PickBestMineral
    /// but scores purely on A* steps to the mineral — no return-cost penalty.
    ///
    /// This gives the Q-table a genuine choice:
    ///   SeekMineral        — balances proximity with return cost (forward planning)
    ///   SeekNearestMineral — pure greedy grab (urgency/late-mission squeeze)
    ///
    /// The Q-table learns when each is appropriate: early-day/high-battery favours
    /// SeekMineral (plan ahead); low-battery/late-day favours SeekNearest (grab
    /// whatever is reachable before the return window closes).
    /// </summary>
    private (int x, int y)? PickNearestMineral(
        RoverState state, GameMap liveMap, RiskMode mode)
    {
        if (liveMap.RemainingMinerals.Count == 0) return null;

        var roverField = GetDistanceField(state.X, state.Y, liveMap);
        var baseField  = GetBaseDistanceField(liveMap);
        var feasible   = EvaluateFeasibleMinerals(state, liveMap, roverField, baseField, mode);
        if (feasible.Count == 0) return null;

        var nearest = feasible
            .OrderBy(p => p.StepsToMineral)
            .ThenBy(p => p.StepsHome)
            .First();

        return (nearest.X, nearest.Y);
    }

    private List<MineralCandidatePlan> EvaluateFeasibleMinerals(
        RoverState state, GameMap liveMap, int[,] roverField, int[,] baseField, RiskMode mode,
        int minMarginTicks = 0)
    {
        int ticksRemaining = _totalTicks - state.Tick;
        var candidates = new List<MineralCandidatePlan>();
        double requiredMargin = RequiredBatteryMargin(mode);
        double detourPenalty = mode == RiskMode.Aggressive ? 0.04 : 0.065;
        double obstaclePenalty = mode == RiskMode.Aggressive
            ? ObstaclePenaltyAggressive
            : ObstaclePenaltyConservative;
        var legToMineralCache = new Dictionary<int, LegPlan>();

        foreach (var pos in liveMap.RemainingMinerals)
        {
            int stepsToMineral = FieldDistance(roverField, pos.x, pos.y);
            if (stepsToMineral == int.MaxValue) continue;

            int stepsHome = FieldDistance(baseField, pos.x, pos.y);
            if (stepsHome == int.MaxValue) continue;

            if (!legToMineralCache.TryGetValue(stepsToMineral, out var legToMineral))
            {
                legToMineral = SimulateLegWithPolicy(
                    state.Tick, stepsToMineral, state.Battery);
                legToMineralCache[stepsToMineral] = legToMineral;
            }
            if (!legToMineral.IsFeasible) continue;

            int tickAtMineral = state.Tick + legToMineral.Ticks;
            double battAfterMine = ApplyMiningTick(legToMineral.FinalBattery, tickAtMineral);
            if (battAfterMine < requiredMargin) continue;

            var legHome = SimulateReturnLegAggressive(tickAtMineral + 1, stepsHome, battAfterMine);
            if (!legHome.IsFeasible) continue;
            if (legHome.MinBattery < requiredMargin) continue;

            int missionTicks = legToMineral.Ticks + 1 + legHome.Ticks + ReturnSafetyBuffer;
            int marginTicks = ticksRemaining - missionTicks;
            if (marginTicks < minMarginTicks)
                continue;

            double localObstacleDensity = LocalObstacleDensity(
                pos.x, pos.y, liveMap, LocalDensityRadius);
            // SCORING REWRITE: use ticks (physics) not steps (geometry).
            // A night trip costs more ticks than a day trip of equal step length,
            // so ticks-based scoring naturally penalises unfavourable timing.
            // Return penalty is urgency-scaled: low early (time abundant),
            // full in the final third of the mission.
            double missionProgress = 1.0 - (double)ticksRemaining / _totalTicks;
            double urgencyScale    = Math.Clamp(0.15 + 0.85 * missionProgress, 0.15, 1.0);
            double baseScore = 100.0 / (legToMineral.Ticks + 1.0)
                             - localObstacleDensity * obstaclePenalty
                             - legHome.Ticks * detourPenalty * urgencyScale;
            candidates.Add(new MineralCandidatePlan(
                X:               pos.x,
                Y:               pos.y,
                StepsToMineral:  stepsToMineral,
                StepsHome:       stepsHome,
                TicksToMineral:  legToMineral.Ticks,
                TickAfterMine:   tickAtMineral + 1,
                BatteryAfterMine:battAfterMine,
                BaseScore:       baseScore));
        }

        return candidates;
    }

    private double BestSecondLegScore(
        MineralCandidatePlan first, GameMap liveMap, int[,] baseField,
        int missionTickAtStart, RiskMode mode)
    {
        var fromFirstField = AStarPathfinder.BuildDistanceField(liveMap, first.X, first.Y);
        int ticksRemaining = _totalTicks - missionTickAtStart;
        double best = 0.0;
        double requiredMargin = RequiredBatteryMargin(mode);
        double returnPenalty = mode == RiskMode.Aggressive ? 0.04 : 0.06;
        var legAbCache = new Dictionary<int, LegPlan>();

        foreach (var pos in liveMap.RemainingMinerals)
        {
            if (pos.x == first.X && pos.y == first.Y) continue;
            if (liveMap.ChebyshevDistance(first.X, first.Y, pos.x, pos.y) > 15) continue;

            int stepsAB = FieldDistance(fromFirstField, pos.x, pos.y);
            if (stepsAB == int.MaxValue) continue;

            int stepsHome = FieldDistance(baseField, pos.x, pos.y);
            if (stepsHome == int.MaxValue) continue;

            if (!legAbCache.TryGetValue(stepsAB, out var legAB))
            {
                legAB = SimulateLegWithPolicy(
                    first.TickAfterMine, stepsAB, first.BatteryAfterMine);
                legAbCache[stepsAB] = legAB;
            }
            if (!legAB.IsFeasible) continue;

            int tickAtSecond = first.TickAfterMine + legAB.Ticks;
            double battAfterSecondMine = ApplyMiningTick(legAB.FinalBattery, tickAtSecond);
            if (battAfterSecondMine < requiredMargin) continue;

            var legHome = SimulateReturnLegAggressive(tickAtSecond + 1, stepsHome, battAfterSecondMine);
            if (!legHome.IsFeasible) continue;
            if (legHome.MinBattery < requiredMargin) continue;

            int totalTicks = first.TicksToMineral + 1 + legAB.Ticks + 1 + legHome.Ticks + ReturnSafetyBuffer;
            if (totalTicks > ticksRemaining) continue;

            double score = 100.0 / (legAB.Ticks + 1.0) - legHome.Ticks * returnPenalty;
            if (score > best) best = score;
        }

        return best;
    }

    // ── Speed selection ───────────────────────────────────────────────────────

    /// <summary>
    /// Picks the right speed for the current move tick.
    ///
    /// SPEED-TO-DISTANCE RULE:
    ///   Speed is capped at the number of steps actually remaining in the path.
    ///   If 1 step remains → Slow (pay 2%, not 18%).
    ///   If 2 steps remain → Normal max (pay 8%, not 18%).
    ///   If 3+ steps remain → Fast allowed.
    ///
    ///   This prevents paying Fast energy (18%/tick) to move a single cell,
    ///   which was pure waste — same 1 cell moved, 9× the battery cost.
    ///   With adjacent minerals this saves 16% battery per collection cycle.
    ///
    /// NIGHT CONSERVATISM RULE (multi-sol fix):
    ///   At night, cap speed by step count rather than affording the fastest speed:
    ///     1 step  → Slow   (2%/tick  — no benefit from faster)
    ///     2 steps → Normal (saves 1 tick vs Slow; extra 4% cost recovered at dawn)
    ///     3+steps → Slow   (Fast at night = 18%/tick, genuinely wasteful)
    ///
    ///   This replaces the old "always Slow at night" blanket rule which was
    ///   correct for 3+-step moves but wrong for 2-step moves: a 2-step Slow
    ///   move costs 2 ticks instead of 1, accumulating 3 lost ticks across the
    ///   mission and costing exactly 3 minerals on a 48h run.
    ///   The 4% extra battery for Normal over Slow on a 2-step night move is
    ///   recovered completely within the first 1 tick of the next day phase.
    ///
    /// <param name="stepsRemaining">
    ///   How many path steps are still queued. Pass 0 to skip the distance cap.
    /// </param>
    /// </summary>
    public RoverSpeed ChooseSpeed(RoverState state, GameMap liveMap, int stepsRemaining = 0)
    {
        if (_phase == MissionPhase.Returning)
        {
            int stepsHome = DistanceToBaseSteps(state, liveMap);
            if (stepsHome == int.MaxValue) return RoverSpeed.Slow;
            return TryPickAggressiveReturnSpeed(state.Tick, stepsHome, state.Battery)
                   ?? RoverSpeed.Slow;
        }

        // NIGHT SPEED CAP: cap by path length, not by affordability.
        // Never use Fast at night (18%/tick, not recovered until dawn).
        // Allow Normal for exactly 2-step moves (saves 1 tick, +4% cost, dawn-recoverable).
        if (!SimulationEngine.IsDay(state.Tick))
        {
            return stepsRemaining switch
            {
                2   => RoverSpeed.Normal,   // saves 1 tick, 4% extra recoverable at dawn
                _   => RoverSpeed.Slow      // 1-step and 3+-step: Slow
            };
        }

        // DAY: cap speed to path length, then pick fastest affordable.
        RoverSpeed maxSpeedByDistance = stepsRemaining switch
        {
            1   => RoverSpeed.Slow,
            2   => RoverSpeed.Normal,
            _   => RoverSpeed.Fast     // 0 = unknown, 3+ = Fast allowed
        };

        // FIX: require battery stays >= BatterySafetyMargin after the move,
        // not just >= 0. CanAfford only checked >= 0, so the rover could move
        // at Fast and arrive at 2.9% — below the 3% margin that the leg planner
        // guaranteed would be maintained, causing unexpected low-battery arrivals.
        bool isDay = SimulationEngine.IsDay(state.Tick);
        foreach (var speed in FastToSlowSpeeds)
        {
            if (speed > maxSpeedByDistance) continue;
            double nextBattery = EnergyCalculator.Apply(
                state.Battery, RoverActionType.Move, speed, isDay);
            if (nextBattery >= BatterySafetyMargin)
                return speed;
        }
        return RoverSpeed.Slow;
    }

    /// <summary>
    /// FIX: Replaces the broken BestAffordableSpeedFrom.
    /// Picks fastest speed where battery AFTER the full trip >= safety margin.
    /// Takes battery at the START of this leg as a direct parameter — no re-estimation.
    /// </summary>
    private RoverSpeed BestAffordableSpeedForLeg(int startTick, int pathSteps, double battAtStart)
    {
        if (pathSteps <= 0) return RoverSpeed.Slow;
        var maxSpeed = MaxSpeedByDistance(pathSteps);

        foreach (var speed in FastToSlowSpeeds)
        {
            if (speed > maxSpeed) continue;
            var sim = EnergyCalculator.SimulateTrip(
                startTick, pathSteps, speed, _totalTicks, battAtStart);
            if (sim.MinBattery >= BatterySafetyMargin
                && sim.FinalBattery >= BatterySafetyMargin)
                return speed;
        }
        return RoverSpeed.Slow;
    }

    private double ExactBatteryCost(int startTick, int pathSteps, RoverSpeed speed)
        => Math.Max(0, -EnergyCalculator.SimulateTrip(
            startTick, pathSteps, speed, _totalTicks, EnergyCalculator.MaxBattery).NetDelta);

    private LegPlan SimulateLegWithPolicy(int startTick, int pathSteps, double batteryAtStart)
    {
        if (pathSteps <= 0)
            return new LegPlan(true, 0, RoverSpeed.Slow, batteryAtStart, batteryAtStart);

        // FIX: use tick-by-tick simulation instead of one fixed speed for the
        // whole trip. The old approach was wrong near day/night boundaries:
        // a 12-step trip starting 4 ticks before nightfall might pick Fast
        // (IsDay=true at startTick -> 4 day ticks, net -32%) when Normal would
        // be far cheaper (6 ticks crossing into night, net -8% due to solar).
        // SimulateDynamicLeg picks speed each tick exactly as ChooseSpeed does.
        var sim = SimulateDynamicLeg(startTick, pathSteps, batteryAtStart);
        return new LegPlan(sim.IsFeasible, sim.Ticks, RoverSpeed.Slow,
                           sim.FinalBattery, sim.MinBattery);
    }

    private DynamicLegPlan SimulateDynamicLeg(int startTick, int pathSteps, double batteryAtStart)
    {
        if (pathSteps <= 0)
            return new DynamicLegPlan(true, 0, batteryAtStart, batteryAtStart);

        int stepsRemaining = pathSteps;
        int tick = startTick;
        int ticksUsed = 0;
        double battery = Math.Clamp(batteryAtStart, 0, EnergyCalculator.MaxBattery);
        double minBattery = battery;

        while (stepsRemaining > 0)
        {
            RoverSpeed? chosen = null;
            double chosenBattery = battery;
            bool isDay = SimulationEngine.IsDay(tick);
            if (!isDay)
            {
                if (NightSpeedPolicy(stepsRemaining) == RoverSpeed.Normal)
                {
                    if (TryApplyMoveAboveMargin(battery, RoverSpeed.Normal, isDay, out chosenBattery))
                        chosen = RoverSpeed.Normal;
                    else if (TryApplyMoveAboveMargin(battery, RoverSpeed.Slow, isDay, out chosenBattery))
                        chosen = RoverSpeed.Slow;
                }
                else if (TryApplyMoveAboveMargin(battery, RoverSpeed.Slow, isDay, out chosenBattery))
                {
                    chosen = RoverSpeed.Slow;
                }
            }
            else
            {
                RoverSpeed maxSpeed = MaxSpeedByDistance(stepsRemaining);
                if (maxSpeed == RoverSpeed.Fast)
                {
                    if (TryApplyMoveAboveMargin(battery, RoverSpeed.Fast, isDay, out chosenBattery))
                        chosen = RoverSpeed.Fast;
                    else if (TryApplyMoveAboveMargin(battery, RoverSpeed.Normal, isDay, out chosenBattery))
                        chosen = RoverSpeed.Normal;
                    else if (TryApplyMoveAboveMargin(battery, RoverSpeed.Slow, isDay, out chosenBattery))
                        chosen = RoverSpeed.Slow;
                }
                else if (maxSpeed == RoverSpeed.Normal)
                {
                    if (TryApplyMoveAboveMargin(battery, RoverSpeed.Normal, isDay, out chosenBattery))
                        chosen = RoverSpeed.Normal;
                    else if (TryApplyMoveAboveMargin(battery, RoverSpeed.Slow, isDay, out chosenBattery))
                        chosen = RoverSpeed.Slow;
                }
                else if (TryApplyMoveAboveMargin(battery, RoverSpeed.Slow, isDay, out chosenBattery))
                {
                    chosen = RoverSpeed.Slow;
                }
            }

            if (chosen == null)
                return new DynamicLegPlan(false, ticksUsed, battery, minBattery);

            stepsRemaining -= (int)chosen.Value;
            battery = chosenBattery;
            minBattery = Math.Min(minBattery, battery);
            tick++;
            ticksUsed++;
        }

        return new DynamicLegPlan(true, ticksUsed, battery, minBattery);
    }

    /// <summary>
    /// Return-feasibility simulator that mirrors the actual return movement
    /// policy: pick the fastest safe speed each tick, including night Fast.
    /// </summary>
    private DynamicLegPlan SimulateReturnLegAggressive(int startTick, int pathSteps, double batteryAtStart)
    {
        if (pathSteps <= 0)
            return new DynamicLegPlan(true, 0, batteryAtStart, batteryAtStart);

        int stepsRemaining = pathSteps;
        int tick = startTick;
        int ticksUsed = 0;
        double battery = Math.Clamp(batteryAtStart, 0, EnergyCalculator.MaxBattery);
        double minBattery = battery;

        while (stepsRemaining > 0)
        {
            RoverSpeed? chosen = TryPickAggressiveReturnSpeed(tick, stepsRemaining, battery);

            if (chosen == null)
                return new DynamicLegPlan(false, ticksUsed, battery, minBattery);

            bool isDay = SimulationEngine.IsDay(tick);
            double chosenBattery = EnergyCalculator.Apply(
                battery, RoverActionType.Move, chosen.Value, isDay);
            stepsRemaining -= (int)chosen.Value;
            battery = chosenBattery;
            minBattery = Math.Min(minBattery, battery);
            tick++;
            ticksUsed++;
        }

        return new DynamicLegPlan(true, ticksUsed, battery, minBattery);
    }

    private static RoverSpeed NightSpeedPolicy(int stepsRemaining)
        => stepsRemaining == 2 ? RoverSpeed.Normal : RoverSpeed.Slow;

    private static bool TryApplyMoveAboveMargin(
        double battery, RoverSpeed speed, bool isDay, out double nextBattery)
    {
        nextBattery = EnergyCalculator.Apply(battery, RoverActionType.Move, speed, isDay);
        return nextBattery >= BatterySafetyMargin;
    }

    private RoverSpeed? TryPickAggressiveReturnSpeed(
        int tick, int stepsRemaining, double battery)
    {
        bool isDay = SimulationEngine.IsDay(tick);
        RoverSpeed max = MaxSpeedByDistance(stepsRemaining);
        if (max == RoverSpeed.Fast)
        {
            if (TryApplyMoveAboveMargin(battery, RoverSpeed.Fast, isDay, out _))
                return RoverSpeed.Fast;
            if (TryApplyMoveAboveMargin(battery, RoverSpeed.Normal, isDay, out _))
                return RoverSpeed.Normal;
            if (TryApplyMoveAboveMargin(battery, RoverSpeed.Slow, isDay, out _))
                return RoverSpeed.Slow;
            return null;
        }
        if (max == RoverSpeed.Normal)
        {
            if (TryApplyMoveAboveMargin(battery, RoverSpeed.Normal, isDay, out _))
                return RoverSpeed.Normal;
            if (TryApplyMoveAboveMargin(battery, RoverSpeed.Slow, isDay, out _))
                return RoverSpeed.Slow;
            return null;
        }

        return TryApplyMoveAboveMargin(battery, RoverSpeed.Slow, isDay, out _)
            ? RoverSpeed.Slow
            : null;
    }

    private static RoverSpeed MaxSpeedByDistance(int stepsRemaining)
        => stepsRemaining switch
        {
            <= 1 => RoverSpeed.Slow,
            2    => RoverSpeed.Normal,
            _    => RoverSpeed.Fast
        };

    private static double ApplyMiningTick(double batteryBeforeMining, int tickAtMineral)
    {
        bool isDay = SimulationEngine.IsDay(tickAtMineral);
        return EnergyCalculator.Apply(
            batteryBeforeMining, RoverActionType.Mine, RoverSpeed.Slow, isDay);
    }

    // ── Q-table interface ─────────────────────────────────────────────────────

    public void UpdateQTableByKey(string stateKey, int actionIdx, double reward,
                                   string nextStateKey, double alpha = -1)
    {
        if (alpha < 0) alpha = _qTable.LearningRate;
        _qTable.UpdateByKeyWithAlpha(stateKey, actionIdx, reward, nextStateKey,
                                      (int)HybridDecision.Count, alpha);
    }

    public double GetTDError(string stateKey, int actionIdx, double reward, string nextStateKey)
        => _qTable.GetTDError(stateKey, actionIdx, reward, nextStateKey, (int)HybridDecision.Count);

    public void DecayEpsilon()
        => Epsilon = Math.Max(EpsilonMin, Epsilon * EpsilonDecay);

    public void SetDynamicEpsilonDecay(int totalEpisodes)
        => EpsilonDecay = Math.Pow(EpsilonMin / Math.Max(Epsilon, 0.01),
                                    1.0 / Math.Max(totalEpisodes, 1));

    public void ResetNavigation()
    {
        _currentPath.Clear();
        _phase = MissionPhase.Collection;
        _cachedDistanceField = null;
        _cachedFieldX = int.MinValue;
        _cachedFieldY = int.MinValue;
        _stateVisits.Clear();
        _prevDecisionBattery = null;
    }

    private int DistanceToBaseSteps(RoverState state, GameMap liveMap)
    {
        var baseField = GetBaseDistanceField(liveMap);
        int steps = FieldDistance(baseField, state.X, state.Y);
        if (steps != int.MaxValue) return steps;

        double fallback = AStarPathfinder.PathCost(
            liveMap, state.X, state.Y, liveMap.StartX, liveMap.StartY);
        return fallback >= double.MaxValue ? int.MaxValue : (int)fallback;
    }

    private int[,] GetBaseDistanceField(GameMap liveMap)
    {
        _baseDistanceField ??= AStarPathfinder.BuildDistanceField(
            liveMap, liveMap.StartX, liveMap.StartY);
        return _baseDistanceField;
    }

    private int[,] GetDistanceField(int x, int y, GameMap liveMap)
    {
        if (_cachedDistanceField == null || x != _cachedFieldX || y != _cachedFieldY)
        {
            _cachedDistanceField = AStarPathfinder.BuildDistanceField(liveMap, x, y);
            _cachedFieldX = x;
            _cachedFieldY = y;
        }

        return _cachedDistanceField;
    }

    private static int FieldDistance(int[,] field, int x, int y)
        => field[x, y];

    private static double LocalObstacleDensity(
        int centerX, int centerY, GameMap liveMap, int radius)
    {
        int blocked = 0;
        int total = 0;

        for (int y = centerY - radius; y <= centerY + radius; y++)
        {
            for (int x = centerX - radius; x <= centerX + radius; x++)
            {
                if (x == centerX && y == centerY) continue;
                if (!liveMap.InBounds(x, y)) continue;
                total++;
                if (!liveMap.IsPassable(x, y)) blocked++;
            }
        }

        return total == 0 ? 0.0 : blocked / (double)total;
    }

    // ── State builder — 1,800 states ─────────────────────────────────────────
    // State space: 5×3×5×4×3×2 = 1,800
    // (Battery × SolPhase × Dist × Dir × MineralsLeft × Urgency)
    //
    // ReturnMarginBucket (4 values) and BatteryTrendBucket (3 values) were
    // tried here, expanding to 10,800 states. This hurt 100h performance:
    //   - ReturnMarginBucket is almost always 3 ("6+ ticks margin") early
    //     on long missions, so most margin buckets never get training signal.
    //   - BatteryTrendBucket is noisy — the delta between two strategic
    //     decisions depends on how many A* path-following ticks occurred.
    //   - 6× more states with the same episode count = 6× fewer visits per
    //     state = Q-table mostly zeros = agent runs on heuristics anyway.
    // The 2-value UrgencyBucket captures the same time-pressure signal more
    // reliably and is fully duration-agnostic.

    public HybridQLearningState BuildQLearningState(RoverState state, GameMap liveMap)
    {
        // 5 battery bands: 0-19, 20-39, 40-59, 60-79, 80-100
        int battBucket = Math.Clamp((int)(state.Battery / 20.0), 0, 4);

        // Nearest mineral distance — based on exact shortest path steps
        int[,] roverField = GetDistanceField(state.X, state.Y, liveMap);
        (int x, int y)? nearest = null;
        int nearestSteps = int.MaxValue;

        foreach (var pos in liveMap.RemainingMinerals)
        {
            int steps = FieldDistance(roverField, pos.x, pos.y);
            if (steps < nearestSteps)
            {
                nearestSteps = steps;
                nearest = pos;
            }
        }

        int distBucket = 4; // none or unreachable
        int dirBucket  = 0;

        if (nearest.HasValue && nearestSteps < int.MaxValue)
        {
            distBucket = nearestSteps switch
            {
                <= 4  => 0,  // close
                <= 12 => 1,  // medium
                <= 25 => 2,  // far
                <= 40 => 3,  // very far
                _     => 4   // out of range
            };
            int dx = nearest.Value.x - state.X;
            int dy = nearest.Value.y - state.Y;
            dirBucket = (dx >= 0 ? 0 : 1) + (dy >= 0 ? 0 : 2); // 0=SE,1=SW,2=NE,3=NW
        }

        // Minerals left: 3 levels
        int mineralsLeft = liveMap.RemainingMinerals.Count switch
        {
            0    => 0,
            <= 2 => 1,
            _    => 2
        };

        // Sol phase: 3 buckets — lets Q-table learn day-cycle behaviour across
        // multiple sols. Replaces the 2-value IsDay bool.
        //   0 = Night          (ticks 32-47 of each sol)  no solar, conserve
        //   1 = Late day       (ticks 16-31 of each sol)  plan before dark
        //   2 = Early day      (ticks  0-15 of each sol)  exploit solar freely
        int tickInSol = state.Tick % RoverState.TicksPerSol;
        int solPhase  = tickInSol >= RoverState.DayTicksPerSol ? 0
                      : tickInSol >= 16                         ? 1
                                                                : 2;

        // Urgency: relative to mission duration — works identically for any duration
        int ticksRemaining = _totalTicks - state.Tick;
        double distToBase  = DistanceToBaseSteps(state, liveMap);
        int urgencyBucket  = (ticksRemaining <= distToBase * 2 + ReturnSafetyBuffer * 3) ? 0 : 1;

        return new HybridQLearningState(battBucket, solPhase, distBucket,
                                        dirBucket, mineralsLeft, urgencyBucket);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HybridDecision IndexToDecision(int idx) => idx switch
    {
        0 => HybridDecision.SeekBestConservative,
        1 => HybridDecision.SeekBestAggressive,
        2 => HybridDecision.SeekNearestConservative,
        3 => HybridDecision.SeekNearestAggressive,
        4 => HybridDecision.ReturnToBase,
        5 => HybridDecision.Standby,
        _ => HybridDecision.SeekBestConservative
    };

    private static int DecisionToIndex(HybridDecision d) => d switch
    {
        HybridDecision.SeekBestConservative    => 0,
        HybridDecision.SeekBestAggressive      => 1,
        HybridDecision.SeekNearestConservative => 2,
        HybridDecision.SeekNearestAggressive   => 3,
        HybridDecision.ReturnToBase            => 4,
        HybridDecision.Standby                 => 5,
        _                                 => 0
    };

    private readonly record struct LegPlan(
        bool       IsFeasible,
        int        Ticks,
        RoverSpeed Speed,
        double     FinalBattery,
        double     MinBattery);

    private readonly record struct DynamicLegPlan(
        bool   IsFeasible,
        int    Ticks,
        double FinalBattery,
        double MinBattery);

    private readonly record struct MineralCandidatePlan(
        int    X,
        int    Y,
        int    StepsToMineral,
        int    StepsHome,
        int    TicksToMineral,
        int    TickAfterMine,
        double BatteryAfterMine,
        double BaseScore);
}

// ── Supporting types ──────────────────────────────────────────────────────────

public enum MissionPhase { Collection, Returning, AtBase }

public enum HybridDecision
{
    SeekBestConservative    = 0,
    SeekBestAggressive      = 1,
    SeekNearestConservative = 2,
    SeekNearestAggressive   = 3,
    ReturnToBase            = 4,
    Standby                 = 5,

    // Legacy aliases for archived agents in Algorithm_Records.
    SeekMineral             = SeekBestConservative,
    SeekNearestMineral      = SeekNearestConservative,

    Count                   = 6
}

/// <summary>
/// 1,800-state Q-table key: 5×3×5×4×3×2
/// All dimensions are relative — no absolute tick counts — so the same trained
/// model transfers across any mission duration.
/// </summary>
public record struct HybridQLearningState(
    int  BatteryBucket,  // 0-4 (5 bands of 20%)
    int  SolPhase,       // 0=night, 1=late-day (ticks 16-31), 2=early-day (ticks 0-15)
    int  DistBucket,     // 0-4 (close/medium/far/very far/none)
    int  DirBucket,      // 0-3 (quadrant: SE/SW/NE/NW)
    int  MineralsLeft,   // 0-2 (none/few/many)
    int  UrgencyBucket   // 0=urgent, 1=plenty of time
)
{
    public string ToKey() =>
        $"{BatteryBucket},{SolPhase},{DistBucket},{DirBucket},{MineralsLeft},{UrgencyBucket}";
}

public enum RiskMode
{
    Conservative,
    Aggressive
}
