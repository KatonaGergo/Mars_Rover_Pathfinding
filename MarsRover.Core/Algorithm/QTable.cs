using System.Text.Json;
using MarsRover.Core.Models;

namespace MarsRover.Core.Algorithm;

/// <summary>
/// Q[stateKey][actionIdx] = expected reward.
///
///   UpdateByKeyWithAlpha — takes an explicit alpha so the caller can pass
///     the current decayed learning rate.
///   GetTDError: returns the raw TD error (r + γ·maxQ(s') - Q(s,a)).
///     Used by PER to compute priorities and by eligibility traces for the δ signal.
///   DecayLearningRate -> call once per episode, alpha floors at LearningRateMin.
/// </summary>
public class QTable
{
    private static readonly RoverAction[] ActionList = RoverAction.AllActions().ToArray();
    public static int ActionCount => ActionList.Length;

    private readonly Dictionary<string, double[]> _table = new();

    // Hyperparameters
    public double LearningRate    { get; set; } = 0.3;    // α — start high first for exploration, then slowly decay it down for exploitation
    public double LearningRateMin { get; set; } = 0.02;   // α floor
    public double LearningRateDecayFactor { get; set; } = 0.9995; // per-episode decay
    public double Discount        { get; set; } = 0.95;   // γ

    // Learning rate decay

    /// <summary>
    /// Call once per episode. Decays alpha toward LearningRateMin.
    /// Fast early learning, stable late convergence.
    /// </summary>
    public void DecayLearningRate()
        => LearningRate = Math.Max(LearningRateMin, LearningRate * LearningRateDecayFactor);

    // Core string-keyed API

    public double GetByKey(string stateKey, int actionIdx, int actionCount)
    {
        if (_table.TryGetValue(stateKey, out var row) && actionIdx < row.Length)
            return row[actionIdx];
        return 0.0;
    }

    public void SetByKey(string stateKey, int actionIdx, double value, int actionCount)
    {
        if (!_table.TryGetValue(stateKey, out var row))
        {
            row = new double[actionCount];
            _table[stateKey] = row;
        }
        if (actionIdx < row.Length)
            row[actionIdx] = value;
    }

    public int BestActionIndex(string stateKey, int actionCount)
    {
        if (!_table.TryGetValue(stateKey, out var row)) return 0;
        int best = 0;
        for (int i = 1; i < Math.Min(actionCount, row.Length); i++)
            if (row[i] > row[best]) best = i;
        return best;
    }

    public double MaxQByKey(string stateKey, int actionCount)
        => GetByKey(stateKey, BestActionIndex(stateKey, actionCount), actionCount);

    // TD error

    /// <summary>
    /// Returns the raw TD error: δ = r + γ·maxQ(s') - Q(s,a).
    ///
    /// Used by:
    ///   - Eligibility traces: δ is a broadcast to all eligible state-action pairs for their updates.
    ///   - PER: |δ| + ε is stored as the transition's priority.
    /// </summary>
    public double GetTDError(string stateKey, int actionIdx, double reward,
                              string nextStateKey, int actionCount)
    {
        double current = GetByKey(stateKey, actionIdx, actionCount);
        double target  = reward + Discount * MaxQByKey(nextStateKey, actionCount);
        return target - current;
    }

    // Standard update (uses LearningRate)

    public void UpdateByKey(string stateKey, int actionIdx, double reward,
                             string nextStateKey, int actionCount)
        => UpdateByKeyWithAlpha(stateKey, actionIdx, reward, nextStateKey, actionCount, LearningRate);

    // Update with explicit alpha (used by eligibility traces and PER)

    /// <summary>
    /// Q(s,a) ← Q(s,a) + alpha · δ
    /// where δ = r + γ·maxQ(s') - Q(s,a).
    /// </summary>

    public void UpdateByKeyWithAlpha(string stateKey, int actionIdx, double reward,
                                      string nextStateKey, int actionCount, double alpha)
    {
        double delta = GetTDError(stateKey, actionIdx, reward, nextStateKey, actionCount);
        double old   = GetByKey(stateKey, actionIdx, actionCount);
        SetByKey(stateKey, actionIdx, old + alpha * delta, actionCount);
    }

    /// <summary>
    /// Direct delta-weighted update, used by eligibility traces where the delta
    /// is pre-computed and shared across multiple state-action pairs.
    /// Q(s,a) ← Q(s,a) + alpha · delta · eligibility
    /// </summary>
    public void ApplyWeightedDelta(string stateKey, int actionIdx, int actionCount,
                                    double alpha, double delta, double eligibility)
    {
        double old = GetByKey(stateKey, actionIdx, actionCount);
        SetByKey(stateKey, actionIdx, old + alpha * delta * eligibility, actionCount);
    }

    // Thread-safe merge (for parallel training)

    /// <summary>
    /// Merges a batch of (stateKey, actionIdx, deltaQ) directly into the table.
    /// Called from the serial merge phase after parallel episode collection.
    /// </summary>
    public void MergeDeltaBatch(List<(string key, int actionIdx, double deltaQ)> deltas,
                                 int actionCount)
    {
        foreach (var (key, idx, dq) in deltas)
        {
            double old = GetByKey(key, idx, actionCount);
            SetByKey(key, idx, old + dq, actionCount);
        }
    }

    // Legacy API (QLearningAgent compat)

    private static string LegacyKey(QLearningState s)
        => $"{s.X},{s.Y},{s.BatteryBucket},{(s.IsDay ? 1 : 0)}";

    public double Get(QLearningState state, int actionIdx)
        => GetByKey(LegacyKey(state), actionIdx, ActionCount);
    public void Set(QLearningState state, int actionIdx, double value)
        => SetByKey(LegacyKey(state), actionIdx, value, ActionCount);
    public int BestActionIndex(QLearningState state)
        => BestActionIndex(LegacyKey(state), ActionCount);
    public double MaxQ(QLearningState state)
        => MaxQByKey(LegacyKey(state), ActionCount);
    public RoverAction BestAction(QLearningState state) => ActionList[BestActionIndex(state)];
    public RoverAction GetAction(int idx)               => ActionList[idx];
    public void Update(QLearningState state, int actionIdx, double reward, QLearningState nextState)
        => UpdateByKey(LegacyKey(state), actionIdx, reward, LegacyKey(nextState), ActionCount);

    // Serialisation

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(_table,
                       new JsonSerializerOptions { WriteIndented = false });
        File.WriteAllText(path, json);
    }

    public static QTable Load(string path)
    {
        var qt   = new QTable();
        var json = File.ReadAllText(path);
        var dict = JsonSerializer.Deserialize<Dictionary<string, double[]>>(json);
        if (dict != null)
            foreach (var (k, v) in dict)
                qt._table[k] = v;
        return qt;
    }

    public int StateCount => _table.Count;

    // Deep clone

    /// <summary>
    /// Returns a full deep copy of this Q-table.
    /// Used to snapshot the table at peak training performance so deployment
    /// always uses the best-seen policy, not the final (potentially degraded) one.
    /// </summary>
    public static QTable CloneFrom(QTable source)
    {
        var clone = new QTable
        {
            LearningRate         = source.LearningRate,
            LearningRateMin      = source.LearningRateMin,
            LearningRateDecayFactor = source.LearningRateDecayFactor,
            Discount             = source.Discount
        };

        // Deep copy every row so future updates to source wont affect the clone
        foreach (var (key, row) in source._table)
            clone._table[key] = (double[])row.Clone();

        return clone;
    }
}
