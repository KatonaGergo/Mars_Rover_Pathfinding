using MarsRover.Core.Models;
using MarsRover.Core.Simulation;

namespace MarsRover.Core.Algorithm;

/// <summary>
/// DQN AGENT — drops the Q-table dictionary, replaces it with a neural network.
///
/// WHAT STAYS THE SAME (from HybridAgent):
///   - A* navigation (optimal paths to targets)
///   - Phase system (Collection → Returning → AtBase)
///   - Hard return trigger (time-budget math)
///   - Detour mineral collection on return path
///   - ε-greedy exploration
///
/// WHAT CHANGES:
///   - State: 14 rich floats instead of 6 bucketed ints
///   - Q-function: NeuralNetwork instead of Dictionary lookup
///   - Target network: frozen copy synced every TargetSyncInterval steps
///   - Weights saved/loaded from disk
///
/// STATE VECTOR (14 floats, all normalised 0-1 or -1 to 1):
///   [0]  battery %           (0-1)
///   [1]  is day              (0 or 1)
///   [2]  time remaining %    (0-1)
///   [3]  urgency flag        (0 or 1)  — must return within 2×buffer ticks
///   [4]  dist to nearest mineral (0-1, normalised by map diagonal)
///   [5]  nearest mineral dx  (-1 to 1, normalised)
///   [6]  nearest mineral dy  (-1 to 1, normalised)
///   [7]  dist to 2nd nearest  (0-1)
///   [8]  2nd nearest dx      (-1 to 1)
///   [9]  2nd nearest dy      (-1 to 1)
///   [10] dist to 3rd nearest  (0-1)
///   [11] 3rd nearest dx      (-1 to 1)
///   [12] 3rd nearest dy      (-1 to 1)
///   [13] minerals remaining % (0-1, relative to map total)
///
/// ACTIONS (4): SeekMineral, SeekNearestMineral, ReturnToBase, Standby
/// </summary>
public class DQNAgent
{
    // ── Tuning ────────────────────────────────────────────────────────────────
    private const int    StateSize            = 14;
    private const int    ActionCount          = 4;
    private const int    Hidden1              = 128;
    private const int    Hidden2              = 64;
    private const int    TargetSyncInterval   = 200;  // steps between target net syncs
    private const int    ReturnSafetyBuffer   = 1;
    private const double DetourThreshold      = 4.0;

    // ── Networks ──────────────────────────────────────────────────────────────
    private readonly NeuralNetwork _onlineNet;
    private readonly NeuralNetwork _targetNet;  // frozen copy — synced every N steps
    private int _stepsSinceSync = 0;
    private int _totalSteps     = 0;

    // ── Exploration ───────────────────────────────────────────────────────────
    public double Epsilon      { get; set; } = 1.0;
    public double EpsilonMin   { get; set; } = 0.05;
    public double EpsilonDecay { get; set; } = 0.995;

    // ── Agent state ───────────────────────────────────────────────────────────
    private readonly Random   _random;
    private readonly GameMap  _map;
    private readonly int      _totalTicks;
    private readonly int      _totalMineralsOnMap;

    private Queue<PathStep> _currentPath = new();
    private MissionPhase    _phase       = MissionPhase.Collection;

    public MissionPhase CurrentPhase => _phase;
    public int          TotalSteps   => _totalSteps;

    // ─────────────────────────────────────────────────────────────────────────

    public DQNAgent(GameMap map, int totalTicks, NeuralNetwork? existingNet = null, int seed = 42)
    {
        _map                = map;
        _totalTicks         = totalTicks;
        _totalMineralsOnMap = Math.Max(1, map.RemainingMinerals.Count);
        _random             = new Random(seed);

        _onlineNet = existingNet ?? new NeuralNetwork(StateSize, Hidden1, Hidden2, ActionCount, seed);
        _targetNet = _onlineNet.Clone(); // target starts as exact copy
    }

    // ── Main tick ─────────────────────────────────────────────────────────────

    public RoverAction SelectAction(RoverState state, GameMap liveMap, bool isTraining = false)
    {
        if (_phase == MissionPhase.AtBase)
            return RoverAction.StandbyAction;

        if (_phase != MissionPhase.Returning && MustReturnNow(state, liveMap))
        {
            _phase = MissionPhase.Returning;
            _currentPath.Clear();
        }

        if (liveMap.HasMineral(state.X, state.Y))
        {
            _currentPath.Clear();
            return RoverAction.MineAction;
        }

        if (_currentPath.Count > 0)
        {
            if (_phase == MissionPhase.Returning)
            {
                var detour = FindDetourMineral(state, liveMap);
                if (detour.HasValue)
                {
                    _currentPath.Clear();
                    return NavigateTo(detour.Value.x, detour.Value.y, state, liveMap);
                }
            }
            var next = _currentPath.Dequeue();
            return RoverAction.Move(next.Direction, ChooseSpeed(state));
        }

        if (_phase == MissionPhase.Returning)
            return NavigateHome(state, liveMap);

        // ── DQN strategic decision ────────────────────────────────────────────
        var decision = MakeDecision(state, liveMap, isTraining);
        return ExecuteDecision(decision, state, liveMap);
    }

    /// <summary>
    /// Atomic version used by the training loop.
    /// Returns both the chosen action AND the strategic decision index that caused it,
    /// so the replay buffer always stores the correct (state, action_index, reward, next_state)
    /// tuple — even when the agent is mid-path or in return phase.
    ///
    /// The decision index is only meaningful for strategic ticks (when MakeDecision was
    /// actually called). For path-following and mining ticks we return -1 so the runner
    /// can skip pushing those to the buffer (they carry no Q-learning signal).
    /// </summary>
    public (RoverAction action, int decisionIdx) SelectActionWithDecision(
        RoverState state, GameMap liveMap, bool isTraining = false)
    {
        // ── Phase: AtBase ─────────────────────────────────────────────────────
        if (_phase == MissionPhase.AtBase)
            return (RoverAction.StandbyAction, -1);

        // ── Hard rule: trigger return phase ──────────────────────────────────
        if (_phase != MissionPhase.Returning && MustReturnNow(state, liveMap))
        {
            _phase = MissionPhase.Returning;
            _currentPath.Clear();
        }

        // ── Mine if standing on mineral ───────────────────────────────────────
        if (liveMap.HasMineral(state.X, state.Y))
        {
            _currentPath.Clear();
            return (RoverAction.MineAction, -1); // not a strategic decision
        }

        // ── Follow existing path ──────────────────────────────────────────────
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
            var next = _currentPath.Dequeue();
            return (RoverAction.Move(next.Direction, ChooseSpeed(state)), -1);
        }

        // ── Navigate home if returning ────────────────────────────────────────
        if (_phase == MissionPhase.Returning)
            return (NavigateHome(state, liveMap), -1);

        // ── Strategic DQN decision (the only tick that counts for learning) ───
        var decision = MakeDecision(state, liveMap, isTraining);
        var action   = ExecuteDecision(decision, state, liveMap);
        return (action, (int)decision);
    }

    // ── Decision making ───────────────────────────────────────────────────────

    private HybridDecision MakeDecision(RoverState state, GameMap liveMap, bool isTraining)
    {
        if (isTraining && _random.NextDouble() < Epsilon)
        {
            // Random exploration — but weight toward SeekMineral
            return _random.NextDouble() < 0.85
                ? HybridDecision.SeekMineral
                : (HybridDecision)_random.Next(ActionCount);
        }

        var stateVec = BuildStateVector(state, liveMap);
        var qValues  = _onlineNet.Forward(stateVec);

        // Mask ReturnToBase unless it makes sense
        if (_phase != MissionPhase.Returning && liveMap.RemainingMinerals.Count > 0)
            qValues[(int)HybridDecision.ReturnToBase] -= 1000; // suppress premature return

        int best = 0;
        for (int i = 1; i < ActionCount; i++)
            if (qValues[i] > qValues[best]) best = i;

        return (HybridDecision)best;
    }

    // ── DQN learning update ───────────────────────────────────────────────────

    /// <summary>
    /// Called by SimulationRunner with a sampled mini-batch from the replay buffer.
    /// Uses the target network for stable Bellman targets.
    /// </summary>
    public void LearnFromBatch(ReplayBuffer buffer, int batchSize, double gamma = 0.95)
    {
        if (!buffer.IsReady(batchSize)) return;

        var batch        = buffer.Sample(batchSize);
        var inputs       = new double[batch.Length][];
        var targets      = new double[batch.Length][];
        var actionIdxs   = new int[batch.Length];

        for (int b = 0; b < batch.Length; b++)
        {
            var t        = batch[b];
            var sVec     = t.StateVector  ?? StateKeyToVector(t.StateKey);
            var s2Vec    = t.NextStateVector ?? StateKeyToVector(t.NextStateKey);

            // Online net: current Q-values (we only update the one action taken)
            var currentQ = _onlineNet.Forward(sVec);

            // Target net: max Q for next state (stable, frozen)
            var nextQ    = _targetNet.Forward(s2Vec);
            double maxQ2 = nextQ.Max();

            // Bellman target: r + γ·maxQ(s') — or just r if terminal
            double bellman = t.IsTerminal
                ? t.Reward
                : t.Reward + gamma * maxQ2;

            // Clamp target to prevent exploding gradients
            bellman = Math.Clamp(bellman, -500, 500);

            currentQ[t.ActionIdx] = bellman; // update only the action taken

            inputs[b]     = sVec;
            targets[b]    = currentQ;
            actionIdxs[b] = t.ActionIdx;
        }

        _onlineNet.Train(inputs, targets, actionIdxs);

        _totalSteps++;
        _stepsSinceSync++;

        // Sync target net every N steps
        if (_stepsSinceSync >= TargetSyncInterval)
        {
            _targetNet.CopyWeightsFrom(_onlineNet);
            _stepsSinceSync = 0;
        }
    }

    // ── Rich state vector (Step 4) ────────────────────────────────────────────

    public double[] BuildStateVector(RoverState state, GameMap liveMap)
    {
        double diagLen = Math.Sqrt(GameMap.Width * GameMap.Width + GameMap.Height * GameMap.Height);
        var vec = new double[StateSize];

        // [0] battery normalised
        vec[0] = state.Battery / 100.0;

        // [1] day/night
        vec[1] = state.IsDay ? 1.0 : 0.0;

        // [2] time remaining fraction
        vec[2] = Math.Max(0, (_totalTicks - state.Tick)) / (double)_totalTicks;

        // [3] urgency flag
        double distBase = liveMap.ChebyshevDistance(state.X, state.Y, liveMap.StartX, liveMap.StartY);
        int ticksLeft   = _totalTicks - state.Tick;
        vec[3] = (ticksLeft <= distBase + ReturnSafetyBuffer * 2) ? 1.0 : 0.0;

        // [4-12] top 3 nearest minerals — distance + direction vector
        var sorted = liveMap.RemainingMinerals
            .OrderBy(m => liveMap.ChebyshevDistance(state.X, state.Y, m.x, m.y))
            .Take(3)
            .ToList();

        for (int k = 0; k < 3; k++)
        {
            int vecBase = 4 + k * 3;
            if (k < sorted.Count)
            {
                var m    = sorted[k];
                double d = liveMap.ChebyshevDistance(state.X, state.Y, m.x, m.y);
                double dx = (m.x - state.X) / (double)GameMap.Width;
                double dy = (m.y - state.Y) / (double)GameMap.Height;
                vec[vecBase]     = d / diagLen;          // [4,7,10] normalised dist
                vec[vecBase + 1] = Math.Clamp(dx, -1, 1); // [5,8,11] dx
                vec[vecBase + 2] = Math.Clamp(dy, -1, 1); // [6,9,12] dy
            }
            else
            {
                // No mineral — fill with sentinel (1.0 = very far, no direction)
                vec[vecBase] = 1.0;
            }
        }

        // [13] minerals remaining fraction
        vec[13] = liveMap.RemainingMinerals.Count / (double)_totalMineralsOnMap;

        return vec;
    }

    // Fallback for buffer entries that only have string keys (from Q-table era)
    private static double[] StateKeyToVector(string key)
    {
        var vec = new double[StateSize];
        var parts = key.Split(',');
        if (parts.Length >= 6)
        {
            vec[0]  = int.Parse(parts[0]) / 9.0;
            vec[1]  = int.Parse(parts[1]);
            vec[4]  = int.Parse(parts[2]) / 8.0;
            vec[13] = int.Parse(parts[4]) / 5.0;
            vec[3]  = int.Parse(parts[5]);
        }
        return vec;
    }

    // ── Navigation (identical to HybridAgent) ─────────────────────────────────

    private bool MustReturnNow(RoverState state, GameMap liveMap)
    {
        int ticksRemaining    = _totalTicks - state.Tick;
        double realReturnCost = AStarPathfinder.PathCost(
            liveMap, state.X, state.Y, liveMap.StartX, liveMap.StartY);

        if (realReturnCost >= double.MaxValue)
            realReturnCost = liveMap.ChebyshevDistance(
                state.X, state.Y, liveMap.StartX, liveMap.StartY);

        return ticksRemaining <= realReturnCost + ReturnSafetyBuffer;
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

        _currentPath.Clear();
        for (int i = 1; i < path.Count; i++)
            _currentPath.Enqueue(path[i]);

        return RoverAction.Move(path[0].Direction, ChooseSpeed(state));
    }

    private (int x, int y)? FindDetourMineral(RoverState state, GameMap liveMap)
    {
        int ticksRemaining = _totalTicks - state.Tick;
        foreach (var pos in liveMap.RemainingMinerals)
        {
            double distToMineral = liveMap.ChebyshevDistance(state.X, state.Y, pos.x, pos.y);
            if (distToMineral > DetourThreshold) continue;

            double returnCost = AStarPathfinder.PathCost(
                liveMap, pos.x, pos.y, liveMap.StartX, liveMap.StartY);
            if (returnCost >= double.MaxValue) continue;

            if (distToMineral + 1 + returnCost + ReturnSafetyBuffer <= ticksRemaining)
                return pos;
        }
        return null;
    }

    private RoverAction ExecuteDecision(HybridDecision decision, RoverState state, GameMap liveMap)
    {
        var target = decision switch
        {
            HybridDecision.SeekMineral        => PickBestMineral(state, liveMap),
            HybridDecision.SeekNearestMineral => PickBestMineral(state, liveMap),
            HybridDecision.ReturnToBase       => ((int x, int y)?)(liveMap.StartX, liveMap.StartY),
            _                                  => null
        };

        if (target == null)
        {
            _phase = MissionPhase.Returning;
            _currentPath.Clear();
            return NavigateHome(state, liveMap);
        }

        if (target.Value.x == state.X && target.Value.y == state.Y)
            return RoverAction.StandbyAction;

        return NavigateTo(target.Value.x, target.Value.y, state, liveMap);
    }

    private (int x, int y)? PickBestMineral(RoverState state, GameMap liveMap)
    {
        var ranked = liveMap.MineralsByPathCost(state.X, state.Y);
        if (ranked.Count == 0) return null;

        int ticksRemaining = _totalTicks - state.Tick;
        (int x, int y)? best = null;
        double bestScore = double.MinValue;

        foreach (var (pos, chebyDist) in ranked)
        {
            if (chebyDist * 2.0 >= state.Battery - 5) continue;

            double returnCost = AStarPathfinder.PathCost(
                liveMap, pos.x, pos.y, liveMap.StartX, liveMap.StartY);
            if (returnCost >= double.MaxValue) continue;

            double totalCost = chebyDist + 1 + returnCost;
            if (totalCost + ReturnSafetyBuffer > ticksRemaining) continue;

            double score = 100.0 / (chebyDist + 1) - returnCost * 0.05;
            if (score > bestScore) { bestScore = score; best = (pos.x, pos.y); }
        }
        return best;
    }

    public RoverSpeed ChooseSpeed(RoverState state)
    {
        if (_phase == MissionPhase.Returning)
            return state.IsDay && state.Battery >= 40 ? RoverSpeed.Fast : RoverSpeed.Normal;

        if (state.IsDay)
        {
            if (state.Battery >= 60) return RoverSpeed.Fast;
            if (state.Battery >= 30) return RoverSpeed.Normal;
            return RoverSpeed.Slow;
        }
        return RoverSpeed.Slow;
    }

    // ── Housekeeping ──────────────────────────────────────────────────────────

    public void DecayEpsilon()
        => Epsilon = Math.Max(EpsilonMin, Epsilon * EpsilonDecay);

    public void ResetNavigation()
    {
        _currentPath.Clear();
        _phase = MissionPhase.Collection;
    }

    public NeuralNetwork OnlineNet => _onlineNet;

    // ── Save / Load (Step 3) ──────────────────────────────────────────────────

    public void SaveWeights(string path) => _onlineNet.Save(path);

    public static DQNAgent LoadWeights(string path, GameMap map, int totalTicks)
    {
        var net   = NeuralNetwork.Load(path);
        var agent = new DQNAgent(map, totalTicks, net);
        agent.Epsilon = 0.0; // loaded model — pure exploitation
        return agent;
    }
}
