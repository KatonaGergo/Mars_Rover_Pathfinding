using MarsRover.Core.Models;
using MarsRover.Core.Simulation;

namespace MarsRover.Core.Algorithm;

/// <summary>
/// Tabular Q-Learning agent.
///
/// TRAINING MODE: Runs many episodes with epsilon-greedy exploration.
///                Call Train() offline before the live simulation.
///
/// EXPLOITATION MODE: Uses the trained Q-table to select the best action each tick.
///                    Call SelectAction() during the live simulation.
/// </summary>
public class QLearningAgent
{
    private readonly QTable   _qTable;
    private readonly Random   _random;

    // ── Epsilon-greedy parameters ────────────────────────────────────────────
    public double Epsilon        { get; set; } = 1.0;   // exploration rate
    public double EpsilonMin     { get; set; }          = 0.05;
    public double EpsilonDecay   { get; set; }          = 0.995; // multiplied each episode

    public QTable QTable => _qTable;

    public QLearningAgent(QTable? existingTable = null, int seed = 42)
    {
        _qTable = existingTable ?? new QTable();
        _random = new Random(seed);
    }

    // ── Action selection ──────────────────────────────────────────────────────

    /// <summary>
    /// Epsilon-greedy action selection for training.
    /// Explores randomly with probability Epsilon, otherwise exploits.
    /// </summary>
    public (RoverAction action, int actionIdx) SelectTrainingAction(
        QLearningState state, GameMap map, int currentX, int currentY)
    {
        if (_random.NextDouble() < Epsilon)
            return SelectRandomAction(map, currentX, currentY);
        else
            return SelectBestAction(state);
    }

    /// <summary>Pure exploitation — used during the live simulation run.</summary>
    public (RoverAction action, int actionIdx) SelectAction(QLearningState state)
        => SelectBestAction(state);

    // ── Training ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Trains the agent for the given number of episodes.
    /// Each episode is a full simulation run from tick 0 to totalTicks (or battery death).
    /// Reports progress via the optional callback.
    /// </summary>
    public TrainingResult Train(
        GameMap      map,
        int          durationHours,
        int          episodes,
        Action<TrainingProgress>? onProgress = null)
    {
        var results       = new List<double>();
        int bestMinerals  = 0;

        for (int ep = 0; ep < episodes; ep++)
        {
            // Each episode gets a fresh engine (but same map layout)
            var engine = new SimulationEngine(map, durationHours);
            double totalReward = 0;

            while (!engine.IsFinished && !engine.BatteryDead)
            {
                var state  = engine.GetState();
                var qState = state.ToQLearningState();

                var (action, actionIdx) = SelectTrainingAction(qState, map, state.X, state.Y);

                int prevMinerals = state.TotalMinerals;
                var logEntry     = engine.Step(action);
                if (logEntry == null) break;

                var newState    = engine.GetState();
                var newQState   = newState.ToQLearningState();
                bool collected  = newState.TotalMinerals > prevMinerals;
                bool terminal   = engine.IsFinished || engine.BatteryDead;

                double reward = RewardCalculator.Calculate(
                    state, action, logEntry, map,
                    collectedMineral: collected,
                    hitObstacle:      false,
                    isTerminal:       terminal);

                _qTable.Update(qState, actionIdx, reward, newQState);
                totalReward += reward;
            }

            int mineralsThisEp = engine.GetState().TotalMinerals;
            if (mineralsThisEp > bestMinerals) bestMinerals = mineralsThisEp;
            results.Add(totalReward);

            // Decay epsilon
            Epsilon = Math.Max(EpsilonMin, Epsilon * EpsilonDecay);

            // Report progress every 50 episodes
            if (ep % 50 == 0)
            {
                onProgress?.Invoke(new TrainingProgress(
                    Episode:     ep,
                    TotalEps:    episodes,
                    Epsilon:     Epsilon,
                    LastReward:  totalReward,
                    BestMinerals: bestMinerals,
                    StatesKnown: _qTable.StateCount));
            }
        }

        return new TrainingResult(episodes, bestMinerals, results, _qTable.StateCount);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private (RoverAction, int) SelectBestAction(QLearningState state)
    {
        int idx = _qTable.BestActionIndex(state);
        return (_qTable.GetAction(idx), idx);
    }

    private (RoverAction, int) SelectRandomAction(GameMap map, int x, int y)
    {
        // Bias random selection toward passable move directions
        var all    = RoverAction.AllActions().ToArray();
        int maxTry = 10;

        for (int i = 0; i < maxTry; i++)
        {
            int idx    = _random.Next(all.Length);
            var action = all[idx];

            if (action.Type == RoverActionType.Move)
            {
                var (nx, ny) = map.ApplyDirection(x, y, action.Dir!.Value);
                if (map.IsPassable(nx, ny)) return (action, idx);
            }
            else
            {
                return (action, idx);
            }
        }

        // Fallback to standby
        int standbyIdx = Array.IndexOf(all, RoverAction.StandbyAction);
        return (RoverAction.StandbyAction, standbyIdx);
    }
}

// ── Data records ──────────────────────────────────────────────────────────────

public record TrainingProgress(
    int              Episode,
    int              TotalEps,
    double           Epsilon,
    double           LastReward,
    int              BestMinerals,
    int              StatesKnown,
    int              BufferSize    = 0,
    EpisodeSnapshot? Snapshot      = null)  // ghost replay data
{
    public double PercentDone => (double)Episode / TotalEps * 100.0;
}

public record TrainingResult(
    int           Episodes,
    int           BestMinerals,
    List<double>  RewardHistory,
    int           StatesLearned);
