namespace MarsRover.Core.Algorithm;

/// <summary>
/// Prioritized Experience Replay buffer.
///
/// Instead of sampling uniformly, transitions are sampled proportionally to
/// their priority, where priority = |TD error| + epsilon_priority.
///
/// WHY THIS HELPS HERE:
///   With only ~1,200 states and ~15 strategic decisions per episode, most
///   transitions in a uniform buffer are from well-known states with TD error ≈ 0
///   — they teach nothing. PER ensures each replay batch is dominated by the
///   transitions where the agent was genuinely surprised: an unexpected +150 mineral
///   reward, a −200 failed-return penalty, a battery death. These are the experiences
///   that most need to propagate through the Q-table.
///
/// IMPLEMENTATION:
///   Simple O(n) proportional sampling — acceptable for our 20k buffer.
///   A sum-tree would be O(log n) but the constant factor isn't worth the
///   complexity at this scale. Full rebuild of cumulative sums on every sample
///   would be expensive; instead we maintain a running max_priority and
///   only recompute when actually sampling.
///
/// PRIORITY FORMULA:
///   priority(i) = (|δ_i| + PriorityEpsilon) ^ PriorityExponent
///   where δ_i is the TD error at last update.
///   New transitions get max_priority so they are always sampled at least once.
/// </summary>
public class PrioritizedReplayBuffer
{
    // ── PER hyperparameters ───────────────────────────────────────────────────
    public double PriorityExponent { get; set; } = 0.6;   // α: 0=uniform, 1=fully prioritised
    public double PriorityEpsilon  { get; set; } = 1e-4;  // prevents zero-probability transitions

    private readonly PrioritizedTransition[] _buffer;
    private readonly int                     _capacity;
    private readonly Random                  _random;

    private int    _head        = 0;
    private int    _count       = 0;
    private double _maxPriority = 1.0; // new transitions start at max so they're sampled first

    public int  Count    => _count;
    public int  Capacity => _capacity;
    public bool IsReady(int minSamples) => _count >= minSamples;

    public PrioritizedReplayBuffer(int capacity = 20_000, int seed = 42)
    {
        _capacity = capacity;
        _buffer   = new PrioritizedTransition[capacity];
        _random   = new Random(seed);
    }

    // ── Push ─────────────────────────────────────────────────────────────────

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

    // ── Sample ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Samples <paramref name="batchSize"/> transitions proportional to priority.
    /// Returns the sampled items along with their buffer indices for priority updates.
    /// </summary>
    public (PrioritizedTransition[] batch, int[] indices) Sample(int batchSize)
    {
        int n = Math.Min(batchSize, _count);

        // Build priority^α weights for live entries
        var weights = new double[_count];
        double totalWeight = 0;
        for (int i = 0; i < _count; i++)
        {
            double w  = Math.Pow(_buffer[i].Priority + PriorityEpsilon, PriorityExponent);
            weights[i] = w;
            totalWeight += w;
        }

        var batch   = new PrioritizedTransition[n];
        var indices = new int[n];

        for (int i = 0; i < n; i++)
        {
            double sample    = _random.NextDouble() * totalWeight;
            double cumulated = 0;
            int    chosen    = 0;

            for (int j = 0; j < _count; j++)
            {
                cumulated += weights[j];
                if (cumulated >= sample) { chosen = j; break; }
            }

            batch[i]   = _buffer[chosen];
            indices[i] = chosen;
        }

        return (batch, indices);
    }

    // ── Priority update ───────────────────────────────────────────────────────

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
    int    Index        // position in buffer — needed for UpdatePriority
);
