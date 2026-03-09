using MarsRover.Core.Models;
using MarsRover.Core.Simulation;

namespace MarsRover.Console;

/// <summary>
/// Writes the full simulation tick log to a timestamped file in results/.
///
/// Every time the agent runs a deployment simulation the output is saved
/// automatically so nothing is lost between terminal sessions.
///
/// Output path: results/run_YYYYMMDD_HHmmss.txt
///
/// The results/ folder is excluded from git via .gitignore — it is purely
/// local output and should not be committed.
/// </summary>
public static class RunLogger
{
    private const string ResultsDir = "results";

    /// <summary>
    /// Writes the log + mission summary to results/run_TIMESTAMP.txt.
    /// Creates the results/ directory if it does not exist.
    /// Returns the path of the file written, or null on failure.
    /// </summary>
    public static string? Save(
        List<SimulationLogEntry> log,
        AppConfig                cfg,
        GameMap                  map)
    {
        try
        {
            Directory.CreateDirectory(ResultsDir);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path      = Path.Combine(ResultsDir, $"run_{timestamp}.txt");

            using var w = new StreamWriter(path);

            // ── Header ─────────────────────────────────────────────────────────
            w.WriteLine("================================================================================");
            w.WriteLine("  MARS ROVER — SIMULATION RUN LOG");
            w.WriteLine("================================================================================");
            w.WriteLine($"  Date:      {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            w.WriteLine($"  Map:       {cfg.MapPath}");
            w.WriteLine($"  Duration:  {cfg.Hours}h = {cfg.Hours * 2} ticks");
            w.WriteLine($"  Episodes:  {cfg.Episodes}");
            w.WriteLine($"  Model:     {cfg.ModelPath}.qtable.json");
            w.WriteLine();

            if (log.Count == 0)
            {
                w.WriteLine("  No simulation data.");
                return path;
            }

            // ── Tick-by-tick log ───────────────────────────────────────────────
            w.WriteLine($"  {"Tick",-5} {"Sol",-4} {"Time",-8} {"Phase",-8} {"Pos",-10} {"Batt",-7} {"Minerals",-12} Event");
            w.WriteLine($"  {new string('─', 80)}");

            foreach (var entry in log)
            {
                string pos      = $"({entry.X},{entry.Y})";
                string batt     = $"{entry.Battery:F0}%";
                string phase    = entry.Phase;
                string daymark  = entry.IsDay ? "DAY" : "NGT";
                string time     = $"{daymark} {entry.HourOfSol:F1}h";
                string minerals = $"B{entry.MineralsB}/Y{entry.MineralsY}/G{entry.MineralsG}({entry.TotalMinerals})";

                w.WriteLine(
                    $"  {entry.Tick,-5} {entry.Sol,-4} {time,-8} {phase,-8} {pos,-10} {batt,-7} {minerals,-12} {entry.EventNote}");
            }

            // ── Summary ────────────────────────────────────────────────────────
            var last  = log[^1];
            bool home  = last.X == map.StartX && last.Y == map.StartY;
            bool alive = last.Battery > 0;

            w.WriteLine();
            w.WriteLine("================================================================================");
            w.WriteLine("  MISSION SUMMARY");
            w.WriteLine("────────────────────────────────────────────────────────────────────────────────");
            w.WriteLine($"  Total minerals:  {last.TotalMinerals}  (Blue={last.MineralsB}, Yellow={last.MineralsY}, Green={last.MineralsG})");
            w.WriteLine($"  Final battery:   {last.Battery:F1}%");
            w.WriteLine($"  Ticks used:      {last.Tick}/{cfg.Hours * 2}");
            w.WriteLine($"  Returned home:   {(home  ? "YES" : "NO")}");
            w.WriteLine($"  Rover survived:  {(alive ? "YES" : "NO")}");
            w.WriteLine($"  Distance trav.:  {last.DistanceTraveled:F0} cells");
            w.WriteLine();

            if (home && alive)
                w.WriteLine($"  RESULT: MISSION SUCCESS — {last.TotalMinerals} minerals collected");
            else if (!alive)
                w.WriteLine("  RESULT: MISSION FAILED — battery died");
            else
                w.WriteLine("  RESULT: MISSION INCOMPLETE — rover did not return home");

            w.WriteLine("================================================================================");

            return path;
        }
        catch (Exception ex)
        {
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine($"  WARNING: Could not save run log: {ex.Message}");
            System.Console.ResetColor();
            return null;
        }
    }
}
