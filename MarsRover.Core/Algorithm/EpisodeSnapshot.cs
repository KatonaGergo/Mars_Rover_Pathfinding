namespace MarsRover.Core.Algorithm;

/// <summary>
/// A lightweight snapshot of one training episode's path and key events.
/// Collected on the background training thread, then sent to the UI
/// for fast-forward ghost replay — the "inside the AI's brain" view.
/// </summary>
public record EpisodeSnapshot(
    int              Episode,
    List<StepRecord> Steps,
    int              MineralsCollected,
    double           TotalReward,
    bool             BatteryDied,
    bool             ReturnedHome
);

/// <summary>
/// One tick within a training episode.
/// Position + what happened + reward signal for colour coding.
/// </summary>
public record StepRecord(
    int          X,
    int          Y,
    StepEvent    Event,
    double       Reward      // negative = punishment, positive = reward
);

public enum StepEvent
{
    Move,           // normal movement
    MineSuccess,    // collected a mineral  → green flash
    BatteryLow,     // battery < 20%        → yellow warning
    BatteryCritical,// battery < 5%         → red warning
    ReturnHome,     // arrived at base       → blue flash
    BatteryDead,    // died here             → red X
    Standby         // waited a tick
}
