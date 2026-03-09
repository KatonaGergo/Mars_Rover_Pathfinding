namespace MarsRover.Core.Algorithm;

/// <summary>
/// Public summary of a saved model's metadata.
/// Returned by SimulationRunner.GetModelInfo().
/// </summary>
public record ModelInfo(
    string ModelPath,
    int    EpisodesCompleted,
    int    BestMinerals,
    double Epsilon,
    string SavedAt,
    int    StatesKnown);
