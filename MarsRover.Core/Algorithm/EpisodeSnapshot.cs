namespace MarsRover.Core.Algorithm;

/// <summary>
/// A lightweight snapshot of one training episodes path and key events.
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
/// Position + what happened + reward signal
/// </summary>
public record StepRecord(
    int          X,
    int          Y,
    StepEvent    Event,
    double       Reward
);

public enum StepEvent
{
    Move,           
    MineSuccess,    
    BatteryLow,     
    BatteryCritical,
    ReturnHome,     
    BatteryDead,    
    Standby         
}
