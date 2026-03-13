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
    private const double DetourThreshold     = 6.0;

    // ── Fields ────────────────────────────────────────────────────────────────
    private readonly QTable  _qTable;
    private readonly Random  _random;
    private readonly GameMap _map;
    private readonly int     _totalTicks;

    public double      Epsilon         { get; set; } = 1.0;
    public double      EpsilonMin      { get; set; } = 0.05;
    public double      EpsilonDecay    { get; set; } = 0.995;
    public QTable      QTable          => _qTable;
    public MissionPhase CurrentPhase   => _phase;
    public int         LastDecisionIdx { get; private set; } = -1;

    private Queue<PathStep> _currentPath = new();
    private MissionPhase    _phase       = MissionPhase.Collection;

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
            return (RoverAction.StandbyAction, -1);

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
            return (DequeueMove(state, liveMap), -1);
        }

        if (_phase == MissionPhase.Returning)
            return (NavigateHome(state, liveMap), -1);

        // Strategic Q-table decision
        var decision = MakeStrategicDecision(state, liveMap, isTraining);
        var action   = ExecuteDecision(decision, state, liveMap);
        LastDecisionIdx = DecisionToIndex(decision);
        return (action, LastDecisionIdx);
    }

    public RoverAction SelectAction(RoverState state, GameMap liveMap, bool isTraining = false)
        => SelectActionWithDecision(state, liveMap, isTraining).action;

    // ── Phase logic ───────────────────────────────────────────────────────────

    private bool MustReturnNow(RoverState state, GameMap liveMap)
    {
        int ticksRemaining = _totalTicks - state.Tick;

        double returnSteps = AStarPathfinder.PathCost(
            liveMap, state.X, state.Y, liveMap.StartX, liveMap.StartY);
        if (returnSteps >= double.MaxValue)
            returnSteps = liveMap.ChebyshevDistance(
                state.X, state.Y, liveMap.StartX, liveMap.StartY);

        var bestSpeed   = BestAffordableSpeedForLeg(state.Tick, (int)returnSteps, state.Battery);
        int returnTicks = EnergyCalculator.TripTicks((int)returnSteps, bestSpeed);

        return ticksRemaining <= returnTicks + ReturnSafetyBuffer;
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

    private (int x, int y)? FindDetourMineral(RoverState state, GameMap liveMap)
    {
        if (liveMap.RemainingMinerals.Count == 0) return null;
        int ticksRemaining = _totalTicks - state.Tick;

        foreach (var pos in liveMap.RemainingMinerals)
        {
            double distToMineral = liveMap.ChebyshevDistance(state.X, state.Y, pos.x, pos.y);
            if (distToMineral > DetourThreshold) continue;

            double stepsToMineral = AStarPathfinder.PathCost(
                liveMap, state.X, state.Y, pos.x, pos.y);
            if (stepsToMineral >= double.MaxValue) continue;

            double returnSteps = AStarPathfinder.PathCost(
                liveMap, pos.x, pos.y, liveMap.StartX, liveMap.StartY);
            if (returnSteps >= double.MaxValue) continue;

            // Use best affordable speed for each leg — same logic as PickBestMineral.
            // Previously hardcoded Slow for both legs, which was far too pessimistic:
            // the rover was rejecting reachable detours because Slow's battery math
            // said they were unaffordable when Normal or Fast actually had plenty of margin.
            var speedToMineral = BestAffordableSpeedForLeg(
                state.Tick, (int)stepsToMineral, state.Battery);
            int ticksToMineral = EnergyCalculator.TripTicks((int)stepsToMineral, speedToMineral);
            int tickAtMineral  = state.Tick + ticksToMineral;

            double battToMineral = -EnergyCalculator.ExactTripDelta(
                state.Tick, (int)stepsToMineral, speedToMineral, _totalTicks);
            bool   miningDay   = SimulationEngine.IsDay(tickAtMineral);
            double battMining  = EnergyCalculator.MiningDrain
                                 - (miningDay ? EnergyCalculator.DayChargePerTick : 0);
            double battAfterMine = state.Battery - battToMineral - battMining;

            if (battAfterMine < BatterySafetyMargin) continue;

            var speedHome  = BestAffordableSpeedForLeg(
                tickAtMineral + 1, (int)returnSteps, battAfterMine);
            int ticksHome  = EnergyCalculator.TripTicks((int)returnSteps, speedHome);

            if (ticksToMineral + 1 + ticksHome + ReturnSafetyBuffer > ticksRemaining) continue;

            double battHome = -EnergyCalculator.ExactTripDelta(
                tickAtMineral + 1, (int)returnSteps, speedHome, _totalTicks);
            if (battAfterMine - battHome >= BatterySafetyMargin)
                return pos;
        }
        return null;
    }

    // ── Strategic decision ────────────────────────────────────────────────────

    private HybridDecision MakeStrategicDecision(RoverState state, GameMap liveMap, bool isTraining)
    {
        var qState = BuildQLearningState(state, liveMap);
        if (isTraining && _random.NextDouble() < Epsilon)
            return ExploreRandomDecision(liveMap);
        return ExploitBestDecision(qState, liveMap);
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
            if (decision == HybridDecision.ReturnToBase) return HybridDecision.SeekMineral;
            if (decision == HybridDecision.Standby)      return HybridDecision.SeekMineral;
        }

        return decision;
    }

    private HybridDecision ExploreRandomDecision(GameMap liveMap)
    {
        if (liveMap.RemainingMinerals.Count == 0) return HybridDecision.ReturnToBase;
        double r = _random.NextDouble();
        if (r < 0.75) return HybridDecision.SeekMineral;
        if (r < 0.90) return HybridDecision.SeekNearestMineral;
        return HybridDecision.ReturnToBase;
    }

    private RoverAction ExecuteDecision(HybridDecision decision, RoverState state, GameMap liveMap)
    {
        (int tx, int ty)? target = decision switch
        {
            HybridDecision.SeekMineral        => PickBestMineral(state, liveMap),
            HybridDecision.SeekNearestMineral => PickNearestMineral(state, liveMap),
            HybridDecision.ReturnToBase       => (liveMap.StartX, liveMap.StartY),
            _                                 => null
        };

        if (target == null && decision != HybridDecision.ReturnToBase)
        {
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

    private (int x, int y)? PickBestMineral(RoverState state, GameMap liveMap)
    {
        var ranked = liveMap.MineralsByPathCost(state.X, state.Y);
        if (ranked.Count == 0) return null;

        // Local A* cache — valid for this single call only (map state doesn't
        // change between candidates). Eliminates duplicate path computations
        // for the same (from, to) pair within one target-selection decision.
        var pathCache = new Dictionary<(int, int, int, int), double>();
        double CachedPathCost(int fx, int fy, int tx, int ty)
        {
            var key = (fx, fy, tx, ty);
            if (!pathCache.TryGetValue(key, out double cost))
                pathCache[key] = cost = AStarPathfinder.PathCost(liveMap, fx, fy, tx, ty);
            return cost;
        }

        int    ticksRemaining = _totalTicks - state.Tick;
        (int x, int y)? best = null;
        double bestScore      = double.MinValue;

        foreach (var (pos, chebyDist) in ranked)
        {
            double stepsToMineral = CachedPathCost(
                state.X, state.Y, pos.x, pos.y);
            if (stepsToMineral >= double.MaxValue) continue;

            double stepsHome = CachedPathCost(
                pos.x, pos.y, liveMap.StartX, liveMap.StartY);
            if (stepsHome >= double.MaxValue) continue;

            // Forward leg speed: mirrors ChooseSpeed's night rule exactly.
            // Night: 2-step → Normal (saves 1 tick, 4% extra, dawn-recoverable).
            //        other  → Slow (no Fast at night).
            // Day:   fastest affordable speed within distance cap.
            RoverSpeed speedToMineral;
            if (SimulationEngine.IsDay(state.Tick))
            {
                speedToMineral = BestAffordableSpeedForLeg(state.Tick,
                                     (int)stepsToMineral, state.Battery);
            }
            else
            {
                speedToMineral = (int)stepsToMineral == 2
                    ? RoverSpeed.Normal
                    : RoverSpeed.Slow;
            }

            int ticksToMineral  = EnergyCalculator.TripTicks((int)stepsToMineral, speedToMineral);
            int tickAtMineral   = state.Tick + ticksToMineral;

            // EXACT battery after arriving and mining
            double battToMineral = -EnergyCalculator.ExactTripDelta(
                                        state.Tick, (int)stepsToMineral, speedToMineral, _totalTicks);
            bool   miningDay    = SimulationEngine.IsDay(tickAtMineral);
            double battMining   = EnergyCalculator.MiningDrain
                                  - (miningDay ? EnergyCalculator.DayChargePerTick : 0);
            double battAtMineral = state.Battery - battToMineral - battMining;

            if (battAtMineral < BatterySafetyMargin) continue;

            // Home leg: use best affordable speed (return is always urgent-optimal)
            var speedHome = BestAffordableSpeedForLeg(
                                tickAtMineral + 1, (int)stepsHome, battAtMineral);
            int ticksHome = EnergyCalculator.TripTicks((int)stepsHome, speedHome);

            // Time guard
            if (ticksToMineral + 1 + ticksHome + ReturnSafetyBuffer > ticksRemaining) continue;

            // Battery guard for full trip
            double battHome = -EnergyCalculator.ExactTripDelta(
                                   tickAtMineral + 1, (int)stepsHome, speedHome, _totalTicks);
            if (battAtMineral - battHome < BatterySafetyMargin) continue;

            double score = 100.0 / (chebyDist + 1) - stepsHome * 0.05;
            if (score > bestScore) { bestScore = score; best = (pos.x, pos.y); }
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
    private (int x, int y)? PickNearestMineral(RoverState state, GameMap liveMap)
    {
        var ranked = liveMap.MineralsByPathCost(state.X, state.Y);
        if (ranked.Count == 0) return null;

        // Same local A* cache as PickBestMineral — safe within one call
        var pathCache = new Dictionary<(int, int, int, int), double>();
        double CachedPathCost(int fx, int fy, int tx, int ty)
        {
            var key = (fx, fy, tx, ty);
            if (!pathCache.TryGetValue(key, out double cost))
                pathCache[key] = cost = AStarPathfinder.PathCost(liveMap, fx, fy, tx, ty);
            return cost;
        }

        int    ticksRemaining = _totalTicks - state.Tick;
        (int x, int y)? best = null;
        double bestScore      = double.MaxValue; // lower = closer

        foreach (var (pos, chebyDist) in ranked)
        {
            double stepsToMineral = CachedPathCost(
                state.X, state.Y, pos.x, pos.y);
            if (stepsToMineral >= double.MaxValue) continue;

            double stepsHome = CachedPathCost(
                pos.x, pos.y, liveMap.StartX, liveMap.StartY);
            if (stepsHome >= double.MaxValue) continue;

            // Same speed/battery/time feasibility guards as PickBestMineral
            RoverSpeed speedToMineral;
            if (SimulationEngine.IsDay(state.Tick))
                speedToMineral = BestAffordableSpeedForLeg(state.Tick,
                                     (int)stepsToMineral, state.Battery);
            else
                speedToMineral = (int)stepsToMineral == 2 ? RoverSpeed.Normal : RoverSpeed.Slow;

            int    ticksToMineral = EnergyCalculator.TripTicks((int)stepsToMineral, speedToMineral);
            int    tickAtMineral  = state.Tick + ticksToMineral;
            double battToMineral  = -EnergyCalculator.ExactTripDelta(
                                        state.Tick, (int)stepsToMineral, speedToMineral, _totalTicks);
            bool   miningDay      = SimulationEngine.IsDay(tickAtMineral);
            double battMining     = EnergyCalculator.MiningDrain
                                    - (miningDay ? EnergyCalculator.DayChargePerTick : 0);
            double battAtMineral  = state.Battery - battToMineral - battMining;

            if (battAtMineral < BatterySafetyMargin) continue;

            var    speedHome  = BestAffordableSpeedForLeg(tickAtMineral + 1, (int)stepsHome, battAtMineral);
            int    ticksHome  = EnergyCalculator.TripTicks((int)stepsHome, speedHome);

            if (ticksToMineral + 1 + ticksHome + ReturnSafetyBuffer > ticksRemaining) continue;

            double battHome = -EnergyCalculator.ExactTripDelta(
                                   tickAtMineral + 1, (int)stepsHome, speedHome, _totalTicks);
            if (battAtMineral - battHome < BatterySafetyMargin) continue;

            // Score = raw A* distance only — no return penalty
            if (stepsToMineral < bestScore) { bestScore = stepsToMineral; best = (pos.x, pos.y); }
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
            double stepsHome = AStarPathfinder.PathCost(
                liveMap, state.X, state.Y, liveMap.StartX, liveMap.StartY);
            if (stepsHome >= double.MaxValue)
                stepsHome = liveMap.ChebyshevDistance(
                    state.X, state.Y, liveMap.StartX, liveMap.StartY);
            return BestAffordableSpeedForLeg(state.Tick, (int)stepsHome, state.Battery);
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

        foreach (var speed in new[] { RoverSpeed.Fast, RoverSpeed.Normal, RoverSpeed.Slow })
        {
            if (speed > maxSpeedByDistance) continue;
            if (EnergyCalculator.CanAfford(state.Battery, RoverActionType.Move, speed,
                                            SimulationEngine.IsDay(state.Tick)))
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
        foreach (var speed in new[] { RoverSpeed.Fast, RoverSpeed.Normal, RoverSpeed.Slow })
        {
            double tripDelta  = EnergyCalculator.ExactTripDelta(
                                    startTick, pathSteps, speed, _totalTicks);
            if (battAtStart + tripDelta >= BatterySafetyMargin)
                return speed;
        }
        return RoverSpeed.Slow;
    }

    private double ExactBatteryCost(int startTick, int pathSteps, RoverSpeed speed)
        => Math.Max(0, -EnergyCalculator.ExactTripDelta(startTick, pathSteps, speed, _totalTicks));

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
    }

    // ── State builder — 1,800 states (was 1,200) ─────────────────────────────
    // Expanding IsDay (2 values) → SolPhaseBucket (3 values) so the Q-table
    // can learn distinct policies for early-day, late-day and night phases.
    // Without this, a 24h-trained model sees all night ticks as "last 16 ticks
    // of the mission" and panics, whereas multi-sol night ticks are routine.
    // State space: 5×3×5×4×3×2 = 1,800  (was 5×2×5×4×3×2 = 1,200)

    public HybridQLearningState BuildQLearningState(RoverState state, GameMap liveMap)
    {
        // 5 battery bands: 0-19, 20-39, 40-59, 60-79, 80-100
        int battBucket = Math.Clamp((int)(state.Battery / 20.0), 0, 4);

        // Nearest mineral distance — 5 bands
        var nearest    = liveMap.NearestMineral(state.X, state.Y);
        int distBucket = 4; // none
        int dirBucket  = 0;

        if (nearest.HasValue)
        {
            double dist = liveMap.ChebyshevDistance(state.X, state.Y,
                                                     nearest.Value.x, nearest.Value.y);
            distBucket = dist switch
            {
                <= 4  => 0,  // close
                <= 12 => 1,  // medium
                <= 25 => 2,  // far
                <= 40 => 3,  // very far
                _     => 4   // out of range
            };
            // 4 direction quadrants (NE/NW/SE/SW)
            int dx = nearest.Value.x - state.X;
            int dy = nearest.Value.y - state.Y;
            dirBucket = (dx >= 0 ? 0 : 1) + (dy >= 0 ? 0 : 2); // 0=SE,1=SW,2=NE,3=NW
        }

        // Urgency: 2 levels (relative — works the same for any duration)
        int ticksRemaining = _totalTicks - state.Tick;
        double distToBase  = liveMap.ChebyshevDistance(state.X, state.Y,
                                                        liveMap.StartX, liveMap.StartY);
        int urgencyBucket = (ticksRemaining <= distToBase * 2 + ReturnSafetyBuffer * 3) ? 0 : 1;

        // Minerals left: 3 levels
        int mineralsLeft = liveMap.RemainingMinerals.Count switch
        {
            0 => 0,
            <= 2 => 1,
            _ => 2
        };

        // Sol phase: 3 buckets — lets Q-table learn day-cycle behaviour across
        // multiple sols. Replaces the 2-value IsDay bool.
        //   0 = Night          (ticks 32-47 of each sol)  no solar, conserve
        //   1 = Late day       (ticks 16-31 of each sol)  plan before dark
        //   2 = Early day      (ticks  0-15 of each sol)  exploit solar freely
        int tickInSol   = state.Tick % RoverState.TicksPerSol;
        int solPhase    = tickInSol >= RoverState.DayTicksPerSol ? 0   // night
                        : tickInSol >= 16                         ? 1   // late day
                                                                  : 2;  // early day

        return new HybridQLearningState(battBucket, solPhase, distBucket,
                                        dirBucket, mineralsLeft, urgencyBucket);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HybridDecision IndexToDecision(int idx) => idx switch
    {
        0 => HybridDecision.SeekMineral,
        1 => HybridDecision.SeekNearestMineral,
        2 => HybridDecision.ReturnToBase,
        3 => HybridDecision.Standby,
        _ => HybridDecision.SeekMineral
    };

    private static int DecisionToIndex(HybridDecision d) => d switch
    {
        HybridDecision.SeekMineral        => 0,
        HybridDecision.SeekNearestMineral => 1,
        HybridDecision.ReturnToBase       => 2,
        HybridDecision.Standby            => 3,
        _                                 => 0
    };
}

// ── Supporting types ──────────────────────────────────────────────────────────

public enum MissionPhase { Collection, Returning, AtBase }

public enum HybridDecision
{
    SeekMineral,
    SeekNearestMineral,
    ReturnToBase,
    Standby,
    Count
}

/// <summary>
/// 1,800-state Q-table key: 5×3×5×4×3×2
/// Expanded from 1,200 (was 5×2×5×4×3×2) by replacing IsDay (2 values) with
/// SolPhase (3 values: 0=night / 1=late-day / 2=early-day).
/// This lets the Q-table learn distinct policies for each sol phase, which is
/// critical for multi-sol missions where night is routine, not end-of-mission.
/// All dimensions are relative — no absolute tick counts — so the same trained
/// model transfers across any mission duration.
/// </summary>
public record struct HybridQLearningState(
    int  BatteryBucket,    // 0-4 (5 bands of 20%)
    int  SolPhase,         // 0=night, 1=late-day (ticks 16-31), 2=early-day (ticks 0-15)
    int  DistBucket,       // 0-4 (close/medium/far/very far/none)
    int  DirBucket,        // 0-3 (quadrant: SE/SW/NE/NW)
    int  MineralsLeft,     // 0-2 (none/few/many)
    int  UrgencyBucket     // 0=urgent, 1=plenty of time
)
{
    public string ToKey() =>
        $"{BatteryBucket},{SolPhase},{DistBucket},{DirBucket},{MineralsLeft},{UrgencyBucket}";
}