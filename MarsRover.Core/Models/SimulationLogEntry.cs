namespace MarsRover.Core.Models;

/// <summary>
/// One log entry per half-hour tick. The full list of these drives all
/// dashboard charts and the map playback.
/// </summary>
public record SimulationLogEntry(
    int         Tick,
    int         Sol,
    double      HourOfSol,
    bool        IsDay,
    int         X,
    int         Y,
    double      Battery,
    RoverAction Action,
    int         MineralsB,
    int         MineralsY,
    int         MineralsG,
    int         TotalMinerals,
    double      DistanceTraveled,
    string      EventNote,
    string      Phase               // "Collection", "Returning", "AtBase"
)
{
    public string TimeLabel => $"Sol {Sol + 1}, {HourOfSol:F1}h";
    public string DayNightLabel => IsDay ? "☀ Day" : "🌑 Night";
}
