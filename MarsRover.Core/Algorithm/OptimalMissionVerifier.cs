using System.Diagnostics;
using MarsRover.Core.Models;
using MarsRover.Core.Simulation;

namespace MarsRover.Core.Algorithm;

/// <summary>
/// Branch-and-bound verifier for the maximum mineral count on a fixed map and duration.
/// The verifier explores mineral collection sequences exactly under simulation battery rules,
/// uses admissible time-based upper bounds, and applies dominance pruning.
///
/// Assumptions:
/// - Transit between targets uses shortest-path step counts (exact A* step distances).
/// - Mining happens immediately on arrival to a chosen mineral target.
/// - Travel leg speed profiles are explored exactly via DP (all feasible speed mixes).
/// </summary>
public sealed class OptimalMissionVerifier
{
    private readonly GameMap _map;
    private readonly int _totalTicks;
    private readonly int _timeoutMs;
    private readonly int _progressIntervalNodes;
    private readonly bool _useHeuristicDominance;
    private readonly bool _useExactTransposition;
    private readonly int _maxTranspositionEntries;
    private readonly int _firstMineralPartitions;
    private readonly int _firstMineralRemainder;

    // Node 0 = base, nodes 1..N = minerals
    private readonly (int x, int y)[] _nodes;
    private readonly int _mineralCount;
    private readonly int[,] _dist;
    private readonly int[][] _sortedMineralsByDistance;

    // Search state
    private readonly bool[] _collected;
    private readonly ulong[] _collectedBits;
    private readonly List<int> _currentRoute = [];
    private List<int> _bestRoute = [];
    private int _bestMinerals;

    // Dominance table:
    // dominance[tick, node, batteryThreshold] = best count seen with battery >= threshold.
    private readonly int[,,]? _dominance;

    // Move DP cache: (startTick, startBattery, steps) -> Pareto outcomes (ticks, endBattery)
    private readonly Dictionary<(int tick, int battery, int steps), List<MoveOutcome>> _moveCache = [];

    // Exact transposition table:
    // key = (tick, node, exact collected bitset), value = best battery seen.
    private readonly Dictionary<StateTranspositionKey, byte>? _exactTransposition;

    // Metrics
    private readonly Stopwatch _sw = new();
    private long _nodesVisited;
    private long _prunedByUpperBound;
    private long _prunedByDominance;
    private long _prunedByTransposition;
    private long _nextProgressAt;
    private bool _timedOut;
    private int? _targetMinerals;
    private bool _targetReached;
    private Action<VerificationProgress>? _progress;

    public OptimalMissionVerifier(
        GameMap map,
        int totalTicks,
        int timeoutSeconds = 300,
        int progressIntervalNodes = 100_000,
        bool useHeuristicDominance = false,
        bool useExactTransposition = true,
        int maxTranspositionEntries = 300_000,
        int firstMineralPartitions = 1,
        int firstMineralRemainder = 0)
    {
        _map = map;
        _totalTicks = totalTicks;
        _timeoutMs = Math.Max(1, timeoutSeconds) * 1000;
        _progressIntervalNodes = Math.Max(1_000, progressIntervalNodes);
        _useHeuristicDominance = useHeuristicDominance;
        _useExactTransposition = useExactTransposition && !useHeuristicDominance;
        _maxTranspositionEntries = Math.Max(0, maxTranspositionEntries);
        _firstMineralPartitions = Math.Max(1, firstMineralPartitions);
        _firstMineralRemainder = Math.Clamp(firstMineralRemainder, 0, _firstMineralPartitions - 1);

        var minerals = map.RemainingMinerals
            .OrderBy(m => m.x)
            .ThenBy(m => m.y)
            .ToList();

        _mineralCount = minerals.Count;
        _nodes = new (int x, int y)[_mineralCount + 1];
        _nodes[0] = (map.StartX, map.StartY);
        for (int i = 0; i < minerals.Count; i++)
            _nodes[i + 1] = minerals[i];

        _dist = new int[_nodes.Length, _nodes.Length];
        BuildDistanceMatrix();
        _sortedMineralsByDistance = BuildSortedMineralLists();

        _collected = new bool[_mineralCount];
        _collectedBits = new ulong[WordsForBitset(_mineralCount)];
        if (_useHeuristicDominance)
        {
            _dominance = new int[_totalTicks + 1, _nodes.Length, 101];
            FillDominance(-1);
        }
        if (_useExactTransposition)
            _exactTransposition = new Dictionary<StateTranspositionKey, byte>(_maxTranspositionEntries);
    }

    public VerificationResult Verify(
        int initialLowerBound = 0,
        int? targetMinerals = null,
        Action<VerificationProgress>? onProgress = null)
    {
        _bestMinerals = Math.Clamp(initialLowerBound, 0, _mineralCount);
        _targetMinerals = targetMinerals.HasValue
            ? Math.Clamp(targetMinerals.Value, 0, _mineralCount)
            : null;
        _targetReached = false;
        _progress = onProgress;
        _nextProgressAt = _progressIntervalNodes;
        _sw.Restart();

        Search(node: 0, tick: 0, battery: 100, mineralsCollected: 0);

        _sw.Stop();

        var routeCoords = _bestRoute.Select(idx => _nodes[idx]).ToList();
        bool proven = !_timedOut && (!_targetReached || _targetMinerals == null);
        bool targetProvenInfeasible = _targetMinerals.HasValue && !_targetReached && !_timedOut;
        bool targetProvenFeasible = _targetMinerals.HasValue && _targetReached;

        return new VerificationResult(
            TotalTicks: _totalTicks,
            MineralsOnMap: _mineralCount,
            BestMinerals: _bestMinerals,
            ProvenOptimal: proven,
            TargetMinerals: _targetMinerals,
            TargetReached: _targetReached,
            TargetProvenInfeasible: targetProvenInfeasible,
            TargetProvenFeasible: targetProvenFeasible,
            UsedHeuristicDominance: _useHeuristicDominance,
            TimedOut: _timedOut,
            NodesVisited: _nodesVisited,
            PrunedByUpperBound: _prunedByUpperBound,
            PrunedByDominance: _prunedByDominance,
            PrunedByTransposition: _prunedByTransposition,
            ElapsedSeconds: _sw.Elapsed.TotalSeconds,
            BestRoute: routeCoords);
    }

    private void Search(int node, int tick, int battery, int mineralsCollected)
    {
        if (_timedOut) return;
        if (_targetReached) return;
        if (_sw.ElapsedMilliseconds >= _timeoutMs)
        {
            _timedOut = true;
            return;
        }

        _nodesVisited++;
        if (_progress != null && _nodesVisited >= _nextProgressAt)
        {
            _nextProgressAt += _progressIntervalNodes;
            _progress(new VerificationProgress(
                NodesVisited: _nodesVisited,
                BestMinerals: _bestMinerals,
                ElapsedSeconds: _sw.Elapsed.TotalSeconds,
                PrunedByUpperBound: _prunedByUpperBound,
                PrunedByDominance: _prunedByDominance,
                PrunedByTransposition: _prunedByTransposition));
        }

        if (battery <= 0 || tick > _totalTicks) return;

        // Any state at base is a feasible mission completion candidate.
        if (node == 0 && mineralsCollected > _bestMinerals)
        {
            _bestMinerals = mineralsCollected;
            _bestRoute = [.. _currentRoute];
        }

        if (_targetMinerals.HasValue
            && mineralsCollected >= _targetMinerals.Value
            && CanStillReturn(node, tick, battery))
        {
            _targetReached = true;
            return;
        }

        if (tick == _totalTicks) return;
        if (!CanStillReturn(node, tick, battery)) return;

        int required = _targetMinerals ?? (_bestMinerals + 1);
        int upperBound = ComputeUpperBound(node, tick, mineralsCollected);
        if (upperBound < required)
        {
            _prunedByUpperBound++;
            return;
        }

        if (_useHeuristicDominance && IsDominated(node, tick, battery, mineralsCollected))
        {
            _prunedByDominance++;
            return;
        }
        if (_useHeuristicDominance)
            RegisterDominance(node, tick, battery, mineralsCollected);

        if (_useExactTransposition && IsTranspositionDominated(node, tick, battery))
        {
            _prunedByTransposition++;
            return;
        }
        if (_useExactTransposition)
            RegisterTransposition(node, tick, battery);

        // Move to base is a valid branch (can help recharge and re-route later).
        if (node != 0)
            BranchMoveToBase(node, tick, battery, mineralsCollected);

        // Branch minerals in distance order to get strong lower bounds early.
        var ordered = _sortedMineralsByDistance[node];
        int ticksRemaining = _totalTicks - tick;

        for (int k = 0; k < ordered.Length; k++)
        {
            int mineralNode = ordered[k];
            int mineralIdx = mineralNode - 1;
            if (_collected[mineralIdx]) continue;
            if (mineralsCollected == 0 && !IsAllowedFirstMineral(mineralIdx)) continue;

            int steps = _dist[node, mineralNode];
            if (steps == int.MaxValue || steps <= 0) continue;

            int minMoveTicks = MinMoveTicksOptimistic(steps);
            int minReturnTicks = MinMoveTicksOptimistic(_dist[mineralNode, 0]);
            if (minReturnTicks == int.MaxValue) continue;
            if (minMoveTicks + 1 + minReturnTicks > ticksRemaining) continue;

            // Candidate-level optimistic pruning: even with fastest possible arrival
            // and optimistic continuation, this mineral cannot reach the required goal.
            if (required > mineralsCollected + 1)
            {
                int optimisticTickAfterMine = tick + minMoveTicks + 1;
                if (optimisticTickAfterMine > _totalTicks) continue;

                _collected[mineralIdx] = true;
                int candidateUpper = ComputeUpperBound(
                    node: mineralNode,
                    tick: optimisticTickAfterMine,
                    mineralsCollected: mineralsCollected + 1);
                _collected[mineralIdx] = false;

                if (candidateUpper < required)
                {
                    _prunedByUpperBound++;
                    continue;
                }
            }

            var outcomes = GetMoveOutcomes(tick, battery, steps);
            foreach (var mv in outcomes)
            {
                int arrivalTick = tick + mv.TicksUsed;
                if (arrivalTick >= _totalTicks) continue;

                int battAfterMine = ApplyMineBattery(
                    mv.EndBattery, SimulationEngine.IsDay(arrivalTick));
                if (battAfterMine <= 0) continue;

                int nextTick = arrivalTick + 1;
                if (!CanStillReturn(mineralNode, nextTick, battAfterMine)) continue;

                _collected[mineralIdx] = true;
                SetCollectedBit(mineralIdx, value: true);

                if (required > mineralsCollected + 1)
                {
                    int childUpper = ComputeUpperBound(
                        node: mineralNode,
                        tick: nextTick,
                        mineralsCollected: mineralsCollected + 1);
                    if (childUpper < required)
                    {
                        _prunedByUpperBound++;
                        SetCollectedBit(mineralIdx, value: false);
                        _collected[mineralIdx] = false;
                        continue;
                    }
                }

                _currentRoute.Add(mineralNode);

                Search(
                    node: mineralNode,
                    tick: nextTick,
                    battery: battAfterMine,
                    mineralsCollected: mineralsCollected + 1);

                _currentRoute.RemoveAt(_currentRoute.Count - 1);
                SetCollectedBit(mineralIdx, value: false);
                _collected[mineralIdx] = false;
            }
        }

        // Standby branch (wait/recharge/day-night alignment).
        BranchStandby(node, tick, battery, mineralsCollected);
    }

    private void BranchMoveToBase(int node, int tick, int battery, int mineralsCollected)
    {
        int stepsToBase = _dist[node, 0];
        if (stepsToBase == int.MaxValue || stepsToBase == 0) return;

        var outcomes = GetMoveOutcomes(tick, battery, stepsToBase);
        foreach (var mv in outcomes)
        {
            int nextTick = tick + mv.TicksUsed;
            if (nextTick > _totalTicks) continue;
            if (mv.EndBattery <= 0) continue;

            Search(
                node: 0,
                tick: nextTick,
                battery: mv.EndBattery,
                mineralsCollected: mineralsCollected);
        }
    }

    private void BranchStandby(int node, int tick, int battery, int mineralsCollected)
    {
        if (tick >= _totalTicks) return;
        bool isDay = SimulationEngine.IsDay(tick);

        // Proof-safe dominance: waiting at full battery during day cannot improve
        // any future objective because battery does not increase, while time is lost.
        if (isDay && battery >= 100) return;

        int nextBattery = ApplyStandbyBattery(battery, isDay);
        if (nextBattery <= 0) return;

        int nextTick = tick + 1;
        if (!CanStillReturn(node, nextTick, nextBattery)) return;

        Search(
            node: node,
            tick: nextTick,
            battery: nextBattery,
            mineralsCollected: mineralsCollected);
    }

    private bool CanStillReturn(int node, int tick, int battery)
    {
        int steps = _dist[node, 0];
        if (steps == int.MaxValue) return false;
        if (steps == 0) return true;

        int remainingTicks = _totalTicks - tick;
        if (remainingTicks <= 0) return false;

        int optimistic = MinMoveTicksOptimistic(steps);
        if (optimistic > remainingTicks) return false;

        var outcomes = GetMoveOutcomes(tick, battery, steps);
        foreach (var mv in outcomes)
        {
            if (mv.EndBattery > 0 && mv.TicksUsed <= remainingTicks)
                return true;
        }
        return false;
    }

    private int ComputeUpperBound(int node, int tick, int mineralsCollected)
    {
        int remainingMinerals = _mineralCount - mineralsCollected;
        if (remainingMinerals <= 0) return mineralsCollected;

        int remainingTicks = _totalTicks - tick;
        int budgetForCollections = remainingTicks;
        if (budgetForCollections <= 0) return mineralsCollected;

        // Admissible optimistic bound: each extra mineral needs at least
        // one move tick + one mine tick.
        int ubFromTime = (budgetForCollections + 1) / 2;
        int upper = mineralsCollected + Math.Min(remainingMinerals, ubFromTime);

        // Additional admissible tightening using nearest uncollected mineral.
        int nearestSteps = FindNearestUncollectedSteps(node);
        if (nearestSteps != int.MaxValue)
        {
            int firstTicks = MinMoveTicksOptimistic(nearestSteps) + 1;
            int ubFromNearest = firstTicks > budgetForCollections
                ? mineralsCollected
                : mineralsCollected + 1 + (budgetForCollections - firstTicks) / 2;
            upper = Math.Min(upper, ubFromNearest);
        }

        return Math.Min(upper, mineralsCollected + remainingMinerals);
    }

    private int FindNearestUncollectedSteps(int fromNode)
    {
        int best = int.MaxValue;
        var sorted = _sortedMineralsByDistance[fromNode];
        for (int i = 0; i < sorted.Length; i++)
        {
            int mNode = sorted[i];
            int mIdx = mNode - 1;
            if (_collected[mIdx]) continue;
            int steps = _dist[fromNode, mNode];
            if (steps < best) best = steps;
            break;
        }
        return best;
    }

    private bool IsDominated(int node, int tick, int battery, int mineralsCollected)
        => _dominance![tick, node, battery] >= mineralsCollected;

    private bool IsAllowedFirstMineral(int mineralIndex)
    {
        if (_firstMineralPartitions <= 1) return true;
        return mineralIndex % _firstMineralPartitions == _firstMineralRemainder;
    }

    private bool IsTranspositionDominated(int node, int tick, int battery)
    {
        if (_exactTransposition == null) return false;
        var key = BuildStateKey(node, tick);
        return _exactTransposition.TryGetValue(key, out byte bestBattery) && bestBattery >= battery;
    }

    private void RegisterTransposition(int node, int tick, int battery)
    {
        if (_exactTransposition == null) return;
        var key = BuildStateKey(node, tick);
        byte batt = (byte)Math.Clamp(battery, 0, 100);

        if (_exactTransposition.TryGetValue(key, out byte best))
        {
            if (batt > best) _exactTransposition[key] = batt;
            return;
        }

        if (_exactTransposition.Count < _maxTranspositionEntries)
            _exactTransposition[key] = batt;
    }

    private StateTranspositionKey BuildStateKey(int node, int tick)
    {
        ulong w0 = _collectedBits.Length > 0 ? _collectedBits[0] : 0UL;
        ulong w1 = _collectedBits.Length > 1 ? _collectedBits[1] : 0UL;
        ulong w2 = _collectedBits.Length > 2 ? _collectedBits[2] : 0UL;
        ulong w3 = _collectedBits.Length > 3 ? _collectedBits[3] : 0UL;
        ulong w4 = _collectedBits.Length > 4 ? _collectedBits[4] : 0UL;
        ulong w5 = _collectedBits.Length > 5 ? _collectedBits[5] : 0UL;
        ulong w6 = _collectedBits.Length > 6 ? _collectedBits[6] : 0UL;
        return new StateTranspositionKey(tick, node, w0, w1, w2, w3, w4, w5, w6);
    }

    private void SetCollectedBit(int mineralIndex, bool value)
    {
        int word = mineralIndex >> 6;
        int bit = mineralIndex & 63;
        ulong mask = 1UL << bit;
        if (value) _collectedBits[word] |= mask;
        else _collectedBits[word] &= ~mask;
    }

    private void RegisterDominance(int node, int tick, int battery, int mineralsCollected)
    {
        for (int b = 0; b <= battery; b++)
        {
            if (_dominance![tick, node, b] < mineralsCollected)
                _dominance[tick, node, b] = mineralsCollected;
        }
    }

    private List<MoveOutcome> GetMoveOutcomes(int startTick, int startBattery, int steps)
    {
        if (steps <= 0) return [new MoveOutcome(0, startBattery)];

        var key = (startTick, startBattery, steps);
        if (_moveCache.TryGetValue(key, out var cached))
            return cached;

        int[] bestAtStep = new int[steps + 1];
        Fill(bestAtStep, -1);
        bestAtStep[0] = startBattery;

        var outcomes = new List<MoveOutcome>(capacity: steps);

        for (int tickUsed = 0; tickUsed < steps; tickUsed++)
        {
            int[] nextBest = new int[steps + 1];
            Fill(nextBest, -1);
            bool isDay = SimulationEngine.IsDay(startTick + tickUsed);

            for (int step = 0; step <= steps; step++)
            {
                int batt = bestAtStep[step];
                if (batt <= 0) continue;

                int remaining = steps - step;
                int maxSpeed = Math.Min(3, remaining);
                for (int speed = 1; speed <= maxSpeed; speed++)
                {
                    int ns = step + speed;
                    int nb = ApplyMoveBattery(batt, (RoverSpeed)speed, isDay);
                    if (nb <= 0) continue;
                    if (nb > nextBest[ns]) nextBest[ns] = nb;
                }
            }

            bestAtStep = nextBest;
            int endBatt = bestAtStep[steps];
            if (endBatt > 0)
                outcomes.Add(new MoveOutcome(tickUsed + 1, endBatt));
        }

        // Keep only non-dominated outcomes ordered by ticks (ascending):
        // for the same/lower ticks, keep highest battery.
        var pareto = new List<MoveOutcome>(outcomes.Count);
        int bestBatterySoFar = -1;
        foreach (var o in outcomes)
        {
            if (o.EndBattery > bestBatterySoFar)
            {
                pareto.Add(o);
                bestBatterySoFar = o.EndBattery;
            }
        }

        _moveCache[key] = pareto;
        return pareto;
    }

    private void BuildDistanceMatrix()
    {
        for (int i = 0; i < _nodes.Length; i++)
        {
            var field = AStarPathfinder.BuildDistanceField(_map, _nodes[i].x, _nodes[i].y);
            for (int j = 0; j < _nodes.Length; j++)
            {
                _dist[i, j] = field[_nodes[j].x, _nodes[j].y];
            }
        }
    }

    private int[][] BuildSortedMineralLists()
    {
        var sorted = new int[_nodes.Length][];

        for (int i = 0; i < _nodes.Length; i++)
        {
            sorted[i] = Enumerable.Range(1, _mineralCount)
                .OrderBy(mNode => _dist[i, mNode])
                .ThenBy(mNode => _nodes[mNode].x)
                .ThenBy(mNode => _nodes[mNode].y)
                .ToArray();
        }

        return sorted;
    }

    private void FillDominance(int value)
    {
        if (_dominance == null) return;
        for (int t = 0; t <= _totalTicks; t++)
            for (int n = 0; n < _nodes.Length; n++)
                for (int b = 0; b <= 100; b++)
                    _dominance[t, n, b] = value;
    }

    private static void Fill(int[] array, int value)
    {
        for (int i = 0; i < array.Length; i++)
            array[i] = value;
    }

    private static int WordsForBitset(int bits)
        => bits <= 0 ? 0 : ((bits - 1) / 64) + 1;

    private static int MinMoveTicksOptimistic(int steps)
    {
        if (steps == int.MaxValue) return int.MaxValue;
        if (steps <= 0) return 0;
        return (steps + 2) / 3; // ceil(steps / 3)
    }

    private static int ApplyMoveBattery(int currentBattery, RoverSpeed speed, bool isDay)
    {
        int delta = (isDay ? 10 : 0) - 2 * (int)speed * (int)speed;
        int next = currentBattery + delta;
        if (next > 100) next = 100;
        if (next < 0) next = 0;
        return next;
    }

    private static int ApplyMineBattery(int currentBattery, bool isDay)
    {
        int delta = isDay ? 8 : -2;
        int next = currentBattery + delta;
        if (next > 100) next = 100;
        if (next < 0) next = 0;
        return next;
    }

    private static int ApplyStandbyBattery(int currentBattery, bool isDay)
    {
        int delta = isDay ? 9 : -1;
        int next = currentBattery + delta;
        if (next > 100) next = 100;
        if (next < 0) next = 0;
        return next;
    }

    private readonly record struct MoveOutcome(int TicksUsed, int EndBattery);

    private readonly record struct StateTranspositionKey(
        int Tick,
        int Node,
        ulong W0,
        ulong W1,
        ulong W2,
        ulong W3,
        ulong W4,
        ulong W5,
        ulong W6);
}

public readonly record struct VerificationProgress(
    long NodesVisited,
    int BestMinerals,
    double ElapsedSeconds,
    long PrunedByUpperBound,
    long PrunedByDominance,
    long PrunedByTransposition);

public readonly record struct VerificationResult(
    int TotalTicks,
    int MineralsOnMap,
    int BestMinerals,
    bool ProvenOptimal,
    int? TargetMinerals,
    bool TargetReached,
    bool TargetProvenInfeasible,
    bool TargetProvenFeasible,
    bool UsedHeuristicDominance,
    bool TimedOut,
    long NodesVisited,
    long PrunedByUpperBound,
    long PrunedByDominance,
    long PrunedByTransposition,
    double ElapsedSeconds,
    IReadOnlyList<(int x, int y)> BestRoute);
