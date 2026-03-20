namespace MarsRover.Core.Algorithm;

/// <summary>
/// Fixed-capacity circular replay buffer.
///
/// Upgraded for DQN: each transition now stores the actual float state vectors
/// alongside the legacy string keys. This avoids re-computing state vectors
/// during learning and allows the DQN to train without information loss.
///
/// Memory estimate: 50,000 transitions × (14+14) floats × 8 bytes ≈ 11 MB — fine.
/// </summary>
public class ReplayBuffer
{
    private readonly Transition[] _buffer;
    private readonly int          _capacity;
    private readonly Random       _random;

    private int _head  = 0;
    private int _count = 0;

    public int  Count    => _count;
    public int  Capacity => _capacity;
    public bool IsReady(int minSamples) => _count >= minSamples;

    public ReplayBuffer(int capacity = 50_000, int seed = 42)
    {
        _capacity = capacity;
        _buffer   = new Transition[capacity];
        _random   = new Random(seed);
    }

    // Push (DQN path — carries float vectors)

    public void Push(double[] stateVec, int actionIdx, double reward,
                     double[] nextStateVec, bool isTerminal)
    {
        _buffer[_head] = new Transition(
            StateKey:        "",
            ActionIdx:       actionIdx,
            Reward:          reward,
            NextStateKey:    "",
            IsTerminal:      isTerminal,
            StateVector:     stateVec,
            NextStateVector: nextStateVec);

        _head = (_head + 1) % _capacity;
        if (_count < _capacity) _count++;
    }

    // Push (legacy string-key path — kept for Q-table compat)

    public void Push(string stateKey, int actionIdx, double reward,
                     string nextStateKey, bool isTerminal = false)
    {
        _buffer[_head] = new Transition(
            StateKey:        stateKey,
            ActionIdx:       actionIdx,
            Reward:          reward,
            NextStateKey:    nextStateKey,
            IsTerminal:      isTerminal,
            StateVector:     null,
            NextStateVector: null);

        _head = (_head + 1) % _capacity;
        if (_count < _capacity) _count++;
    }

    // Sample

    public Transition[] Sample(int batchSize)
    {
        int n     = Math.Min(batchSize, _count);
        var batch = new Transition[n];
        for (int i = 0; i < n; i++)
            batch[i] = _buffer[_random.Next(_count)];
        return batch;
    }

    public void Clear()
    {
        _head  = 0;
        _count = 0;
    }
}

/// <summary>
/// One stored experience: (s, a, r, s', terminal).
/// StateVector/NextStateVector are set when using DQN; StateKey/NextStateKey
/// are set when using the legacy Q-table path.
/// </summary>
public readonly record struct Transition(
    string    StateKey,
    int       ActionIdx,
    double    Reward,
    string    NextStateKey,
    bool      IsTerminal,
    double[]? StateVector,
    double[]? NextStateVector
);
