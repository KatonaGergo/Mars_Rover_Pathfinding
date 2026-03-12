using MarsRover.Core.Models;
using MarsRover.Core.Simulation;
using MarsRover.Core.Utils;

namespace MarsRover.Console;

/// <summary>
/// Thin wrapper around MarsRover.Core.Utils.MissionLogger.
/// Extracts the fields AppConfig holds and forwards to Core so the Console and
/// UI projects always produce identical output from the same underlying writer.
/// </summary>
public static class RunLogger
{
    public static string? Save(
        List<SimulationLogEntry> log,
        AppConfig                cfg,
        GameMap                  map)
        => MissionLogger.Save(log, cfg.MapPath, cfg.Hours, cfg.Episodes, cfg.ModelPath, map);
}
