namespace MarsRover.Core.Algorithm;

/// <summary>Live training progress payload used by UI/Console callbacks.</summary>
public record TrainingProgress(
    int              Episode,
    int              TotalEps,
    double           Epsilon,
    double           LastReward,
    int              BestMinerals,
    int              StatesKnown,
    int              BufferSize    = 0,
    double           LastBattery   = 100.0,
    int              LastMinerals  = 0,
    EpisodeSnapshot? Snapshot      = null)
{
    public double PercentDone => (double)Episode / TotalEps * 100.0;
}

/// <summary>Final aggregate result returned by training.</summary>
public record TrainingResult(
    int          Episodes,
    int          BestMinerals,
    List<double> RewardHistory,
    int          StatesLearned);

