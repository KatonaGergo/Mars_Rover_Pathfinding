namespace MarsRover.Core.Models;

/// <summary>
/// Full simulation state at a given tick.
/// Immutable snapshot — the SimulationEngine produces a new one each tick.
/// </summary>
public record RoverState(
    int   X,
    int   Y,
    double Battery,
    int   Tick,            // half-hour ticks elapsed since start
    int   MineralsB,
    int   MineralsY,
    int   MineralsG,
    bool  IsMining,
    RoverAction LastAction
)
{
    // ── Time helpers ─────────────────────────────────────────────────────────
    // A full Martian sol = 24h = 48 half-hour ticks
    // Day = 16h = 32 ticks | Night = 8h = 16 ticks
    public const int TicksPerSol   = 48;
    public const int DayTicksPerSol = 32;

    public int  Sol          => Tick / TicksPerSol;
    public int  TickInSol    => Tick % TicksPerSol;
    public bool IsDay        => TickInSol < DayTicksPerSol;
    public double HourOfSol  => TickInSol * 0.5;

    public int TotalMinerals => MineralsB + MineralsY + MineralsG;

    /// <summary>Battery bucketed into 0–9 for Q-table state key.</summary>
    public int BatteryBucket => (int)(Battery / 10.0);

    /// <summary>Compact key for Q-table lookup.</summary>
    public QLearningState ToQLearningState() => new(X, Y, BatteryBucket, IsDay);
}

/// <summary>
/// The minimal state representation used as a key in the Q-table.
/// Keeps the state space manageable: 50×50×10×2 = 50,000 states.
/// </summary>
public record struct QLearningState(int X, int Y, int BatteryBucket, bool IsDay);
