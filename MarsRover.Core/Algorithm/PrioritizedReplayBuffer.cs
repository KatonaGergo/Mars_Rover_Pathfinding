using System.Linq;

namespace MarsRover.Core.Algorithm;

/// <summary>
/// Prioritized Experience Replay buffer. (PER)
///
/// Instead of sampling uniformly, transitions are sampled proportionally to
/// their priority, where priority = |TD error| + epsilon_priority.
///
///   With only ~1,200 states and ~15 strategic decisions per episode, most
///   transitions in a uniform buffer are from well-known states with TD error ≈ 0,
///   so they teach nothing. PER ensures each replay batch is dominated by the
///   transitions where the agent was genuinely surprised: an unexpected +150 mineral
///   reward, a −200 failed-return penalty, a battery death. These are the experiences
///   that most need to propagate through the Q-table.
///   
/// Priority formula:
///   priority(i) = (|δ_i| + PriorityEpsilon) ^ PriorityExponent
///   where δ_i is the TD error at last update.
///   New transitions get max_priority so they are always sampled at least once.
/// </summary>
public class PrioritizedReplayBuffer
{
    // PER hyperparameters
    public double PriorityExponent { get; set; } = 0.6;   // α: 0=uniform, 1=fully prioritised
    public double PriorityEpsilon  { get; set; } = 1e-4;  // prevents zero-probability transitions

    private readonly PrioritizedTransition[] _buffer;
    private readonly int                     _capacity;
    private readonly Random                  _random;

    private int    _head        = 0;
    private int    _count       = 0;
    private double _maxPriority = 1.0; // new transitions start at max so they are sampled first

    public int  Count    => _count;
    public int  Capacity => _capacity;
    public bool IsReady(int minSamples) => _count >= minSamples;

    public PrioritizedReplayBuffer(int capacity = 20_000, int seed = 42)
    {
        _capacity = capacity;
        _buffer   = new PrioritizedTransition[capacity];
        _random   = new Random(seed);
    }

    // Push

    /// <summary>
    /// Push a new transition. New entries always get max_priority so they
    /// are guaranteed to be sampled at least once before any priority update.
    /// </summary>
    public void Push(string stateKey, int actionIdx, double reward,
                     string nextStateKey, bool isTerminal, double initialPriority = -1)
    {
        double p = initialPriority > 0 ? initialPriority : _maxPriority;
        _buffer[_head] = new PrioritizedTransition(
            StateKey:     stateKey,
            ActionIdx:    actionIdx,
            Reward:       reward,
            NextStateKey: nextStateKey,
            IsTerminal:   isTerminal,
            Priority:     p,
            Index:        _head);

        _maxPriority = Math.Max(_maxPriority, p);
        _head        = (_head + 1) % _capacity;
        if (_count < _capacity) _count++;
    }

    // Sample

    /// <summary>
    /// Samples <paramref name="batchSize"/> transitions proportional to priority.
    /// Sampling is without replacement for better batch diversity.
    /// Returns sampled items, their buffer indices, and IS-weights.
    /// </summary>
    public (PrioritizedTransition[] batch, int[] indices, double[] isWeights)
        Sample(int batchSize, double beta = 0.4)
    {
        int n = Math.Min(batchSize, _count);
        if (n <= 0) return (Array.Empty<PrioritizedTransition>(), Array.Empty<int>(), Array.Empty<double>());

        // Build priority^α weights for live entries
        var weights = new double[_count];
        double totalWeight = 0;
        for (int i = 0; i < _count; i++)
        {
            double w  = Math.Pow(_buffer[i].Priority + PriorityEpsilon, PriorityExponent);
            weights[i] = w;
            totalWeight += w;
        }

        if (totalWeight <= 0)
        {
            var fallbackBatch   = new PrioritizedTransition[n];
            var fallbackIndices = new int[n];
            var fallbackWeights = new double[n];
            for (int i = 0; i < n; i++)
            {
                int idx = _random.Next(_count);
                fallbackBatch[i] = _buffer[idx];
                fallbackIndices[i] = idx;
                fallbackWeights[i] = 1.0;
            }
            return (fallbackBatch, fallbackIndices, fallbackWeights);
        }

        var batch      = new PrioritizedTransition[n];
        var indices    = new int[n];
        var isWeights  = new double[n];
        double remain  = totalWeight;
        double maxIsW  = double.MinValue;

        for (int i = 0; i < n; i++)
        {
            double sample    = _random.NextDouble() * remain;
            double cumulated = 0;
            int    chosen    = -1;

            for (int j = 0; j < _count; j++)
            {
                if (weights[j] <= 0) continue;
                cumulated += weights[j];
                if (cumulated >= sample) { chosen = j; break; }
            }

            if (chosen < 0)
            {
                for (int j = 0; j < _count; j++)
                {
                    if (weights[j] > 0) { chosen = j; break; }
                }
                if (chosen < 0) chosen = _random.Next(_count);
            }

            batch[i]   = _buffer[chosen];
            indices[i] = chosen;

            double prob = weights[chosen] / totalWeight;
            double isw  = Math.Pow(Math.Max(_count * prob, 1e-12), -Math.Clamp(beta, 0.0, 1.0));
            isWeights[i] = isw;
            if (isw > maxIsW) maxIsW = isw;

            remain -= weights[chosen];
            weights[chosen] = 0; // no replacement

            if (remain <= 0 && i + 1 < n)
            {
                for (int k = i + 1; k < n; k++)
                {
                    int idx = _random.Next(_count);
                    batch[k] = _buffer[idx];
                    indices[k] = idx;
                    isWeights[k] = 1.0;
                }
                break;
            }
        }

        if (maxIsW <= 0) maxIsW = 1.0;
        for (int i = 0; i < isWeights.Length; i++)
            isWeights[i] /= maxIsW; // normalize to [0,1]

        return (batch, indices, isWeights);
    }

    /// <summary>
    /// Diversity-constrained PER: reserve a fraction of the batch for
    /// stratified groups keyed by (SolPhase, BatteryBucket, ReturnMarginBucket),
    /// then fill the rest with standard global PER.
    /// </summary>
    public (PrioritizedTransition[] batch, int[] indices, double[] isWeights)
        SampleWithDiversity(int batchSize, double beta = 0.4, double stratifiedFraction = 0.4)
    {
        int n = Math.Min(batchSize, _count);
        if (n <= 0) return (Array.Empty<PrioritizedTransition>(), Array.Empty<int>(), Array.Empty<double>());

        var weights = new double[_count];
        double totalWeight = 0;
        for (int i = 0; i < _count; i++)
        {
            double w = Math.Pow(_buffer[i].Priority + PriorityEpsilon, PriorityExponent);
            weights[i] = w;
            totalWeight += w;
        }

        if (totalWeight <= 0)
            return Sample(batchSize, beta);

        int stratifiedTarget = (int)Math.Round(n * Math.Clamp(stratifiedFraction, 0.0, 1.0));
        var selected = new HashSet<int>();

        // Build strata from state-key features: battery(0), solPhase(1), returnMargin(5)
        var strata = new Dictionary<string, List<int>>();
        for (int i = 0; i < _count; i++)
        {
            if (weights[i] <= 0) continue;
            string key = BuildReplayGroupKey(_buffer[i].StateKey);
            if (!strata.TryGetValue(key, out var list))
            {
                list = new List<int>();
                strata[key] = list;
            }
            list.Add(i);
        }

        if (stratifiedTarget > 0 && strata.Count > 0)
        {
            var groups = strata.Keys.OrderBy(_ => _random.Next()).ToArray();
            while (selected.Count < stratifiedTarget)
            {
                bool added = false;
                foreach (var g in groups)
                {
                    int chosen = WeightedPickFromCandidates(strata[g], weights, selected);
                    if (chosen < 0) continue;
                    selected.Add(chosen);
                    added = true;
                    if (selected.Count >= stratifiedTarget) break;
                }

                if (!added) break;
            }
        }

        // Fill the remainder with regular PER from remaining transitions.
        while (selected.Count < n)
        {
            int chosen = WeightedPickFromCandidates(
                Enumerable.Range(0, _count), weights, selected);
            if (chosen < 0) break;
            selected.Add(chosen);
        }

        // Fallback fill if sampling got stuck.
        while (selected.Count < n)
            selected.Add(_random.Next(_count));

        var chosenIndices = selected.Take(n).ToArray();
        var batch = new PrioritizedTransition[n];
        var indices = new int[n];
        var isWeights = new double[n];

        double maxIsW = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            int idx = chosenIndices[i];
            batch[i] = _buffer[idx];
            indices[i] = idx;

            double prob = Math.Max(weights[idx] / totalWeight, 1e-12);
            double isw  = Math.Pow(Math.Max(_count * prob, 1e-12), -Math.Clamp(beta, 0.0, 1.0));
            isWeights[i] = isw;
            if (isw > maxIsW) maxIsW = isw;
        }

        if (maxIsW <= 0) maxIsW = 1.0;
        for (int i = 0; i < isWeights.Length; i++)
            isWeights[i] /= maxIsW;

        return (batch, indices, isWeights);
    }

    private int WeightedPickFromCandidates(
        IEnumerable<int> candidateIndices,
        IReadOnlyList<double> weights,
        HashSet<int> selected)
    {
        double sum = 0.0;
        var filtered = new List<int>();
        foreach (int idx in candidateIndices)
        {
            if (idx < 0 || idx >= _count) continue;
            if (selected.Contains(idx)) continue;
            double w = weights[idx];
            if (w <= 0) continue;
            filtered.Add(idx);
            sum += w;
        }

        if (filtered.Count == 0 || sum <= 0) return -1;

        double sample = _random.NextDouble() * sum;
        double cum = 0.0;
        foreach (int idx in filtered)
        {
            cum += weights[idx];
            if (cum >= sample) return idx;
        }

        return filtered[^1];
    }

    private static string BuildReplayGroupKey(string stateKey)
    {
        // Expected stateKey format:
        // battery,solPhase,dist,dir,minerals,returnMargin,batteryTrend
        // For legacy keys, returnMargin may be urgency at index 5.
        var parts = stateKey.Split(',');
        if (parts.Length < 2) return "unknown";
        string battery = parts[0];
        string solPhase = parts[1];
        string margin = parts.Length > 5 ? parts[5] : "0";
        return $"{solPhase}:{battery}:{margin}";
    }

    // Priority update

    /// <summary>
    /// Update the priority for a transition after computing its new TD error.
    /// Call after each Q-table update in the PER replay loop.
    /// </summary>
    public void UpdatePriority(int bufferIndex, double newTDError)
    {
        if (bufferIndex < 0 || bufferIndex >= _capacity) return;
        double p = Math.Abs(newTDError) + PriorityEpsilon;
        _buffer[bufferIndex] = _buffer[bufferIndex] with { Priority = p };
        _maxPriority = Math.Max(_maxPriority, p);
    }

    public void Clear()
    {
        _head  = 0;
        _count = 0;
        _maxPriority = 1.0;
    }
}

public readonly record struct PrioritizedTransition(
    string StateKey,
    int    ActionIdx,
    double Reward,
    string NextStateKey,
    bool   IsTerminal,
    double Priority,
    int    Index        // position in buffer
);
