using MarsRover.Core.Algorithm;
using MarsRover.Core.Models;
using MarsRover.Core.Simulation;

namespace MarsRover.Console;

/// <summary>
/// Terminal interface for the Mars Rover Q-learning agent.
///
/// FLOW:
///   1. Print mission parameters
///   2. Train for N episodes (live progress bar)
///   3. Run one deployment simulation (pure exploitation, epsilon=0)
///   4. Print the full tick-by-tick log
///   5. Print final summary
///
/// RESUME:
///   If a saved model exists at the model path, training resumes from where
///   it left off (epsilon carried over, Q-table loaded).
/// </summary>
public class ConsoleRunner
{
    private readonly string _mapPath;
    private readonly int    _hours;
    private readonly int    _episodes;
    private readonly string _modelPath;

    // Terminal width for progress bar
    private const int BarWidth = 40;

    public ConsoleRunner(string mapPath, int hours, int episodes, string modelPath)
    {
        _mapPath   = mapPath;
        _hours     = hours;
        _episodes  = episodes;
        _modelPath = modelPath;
    }

    public void Run()
    {
        // ── Header ─────────────────────────────────────────────────────────────
        WriteSeparator('═');
        WriteColored("  MARS ROVER — Q-LEARNING AGENT", ConsoleColor.Cyan);
        WriteSeparator('═');
        System.Console.WriteLine();

        // ── Load map ───────────────────────────────────────────────────────────
        WriteColored("▶ Loading map...", ConsoleColor.Yellow);
        GameMap map;
        try
        {
            map = GameMap.LoadFromFile(_mapPath);
        }
        catch (Exception ex)
        {
            WriteColored($"  ERROR loading map: {ex.Message}", ConsoleColor.Red);
            return;
        }

        int totalMinerals = map.RemainingMinerals.Count;
        WriteColored($"  Map loaded: {_mapPath}", ConsoleColor.Green);
        System.Console.WriteLine($"  Grid:      {GameMap.Width}×{GameMap.Height}");
        System.Console.WriteLine($"  Start:     ({map.StartX}, {map.StartY})");
        System.Console.WriteLine($"  Minerals:  {totalMinerals}");
        System.Console.WriteLine($"  Duration:  {_hours}h = {_hours * 2} ticks");
        System.Console.WriteLine($"  Episodes:  {_episodes}");
        System.Console.WriteLine($"  Model:     {_modelPath}.qtable.json");

        bool resuming = File.Exists(_modelPath + ".qtable.json");
        if (resuming)
            WriteColored($"  Resuming from saved model.", ConsoleColor.Cyan);

        System.Console.WriteLine();

        // ── Training ───────────────────────────────────────────────────────────
        WriteSeparator('─');
        WriteColored(resuming ? "▶ RESUMING TRAINING" : "▶ TRAINING", ConsoleColor.Yellow);
        WriteSeparator('─');

        var runner      = new SimulationRunner(map, _hours, _modelPath);
        int bestSoFar   = 0;
        var trainStart  = DateTime.Now;

        runner.Train(_episodes, progress =>
        {
            // Overwrite the current line with a live progress bar
            int    filled  = (int)((double)progress.Episode / progress.TotalEps * BarWidth);
            string bar     = new string('█', filled) + new string('░', BarWidth - filled);
            double elapsed = (DateTime.Now - trainStart).TotalSeconds;
            double eta     = progress.Episode > 0
                             ? elapsed / progress.Episode * (progress.TotalEps - progress.Episode)
                             : 0;

            if (progress.BestMinerals > bestSoFar)
            {
                bestSoFar = progress.BestMinerals;
                // New best — print on its own line so it's visible in scroll history
                System.Console.WriteLine();
                WriteColored(
                    $"  ★ New best: {bestSoFar} minerals  (ep {progress.Episode})",
                    ConsoleColor.Green);
            }

            string line = $"\r  [{bar}] {progress.Episode}/{progress.TotalEps}" +
                          $"  ε={progress.Epsilon:F3}" +
                          $"  best={progress.BestMinerals}⛏" +
                          $"  states={progress.StatesKnown}" +
                          $"  ETA {eta:F0}s  ";

            System.Console.Write(line);
        });

        System.Console.WriteLine();
        System.Console.WriteLine();

        double trainSecs = (DateTime.Now - trainStart).TotalSeconds;
        WriteColored($"  Training complete in {trainSecs:F1}s.", ConsoleColor.Green);
        WriteColored($"  Best minerals during training: {bestSoFar}", ConsoleColor.Green);
        System.Console.WriteLine();

        // ── Deployment simulation ──────────────────────────────────────────────
        WriteSeparator('─');
        WriteColored("▶ RUNNING SIMULATION (epsilon=0, pure exploitation)", ConsoleColor.Yellow);
        WriteSeparator('─');

        var log = runner.RunSimulation();

        // Print tick-by-tick log
        System.Console.WriteLine();
        System.Console.WriteLine($"  {"Tick",-5} {"Sol",-4} {"Time",-8} {"Phase",-6} {"Pos",-10} {"Batt",-7} {"Minerals",-9} Event");
        System.Console.WriteLine($"  {new string('─', 72)}");

        int lastMinerals = 0;
        foreach (var entry in log)
        {
            bool newMineral = entry.TotalMinerals > lastMinerals;
            lastMinerals    = entry.TotalMinerals;

            string pos     = $"({entry.X},{entry.Y})";
            string batt    = $"{entry.Battery:F0}%";
            string phase   = entry.Phase[..Math.Min(6, entry.Phase.Length)];
            string daymark = entry.IsDay ? "☀" : "🌑";
            string time    = $"{daymark}{entry.HourOfSol:F1}h";
            string minerals = $"B{entry.MineralsB}/Y{entry.MineralsY}/G{entry.MineralsG}";

            // Colour-code notable events
            if (newMineral)
                System.Console.ForegroundColor = ConsoleColor.Green;
            else if (entry.Battery < 10)
                System.Console.ForegroundColor = ConsoleColor.Red;
            else if (entry.Battery < 25)
                System.Console.ForegroundColor = ConsoleColor.Yellow;
            else
                System.Console.ForegroundColor = ConsoleColor.Gray;

            System.Console.WriteLine($"  {entry.Tick,-5} {entry.Sol,-4} {time,-8} {phase,-6} {pos,-10} {batt,-7} {minerals,-9} {entry.EventNote}");
            System.Console.ResetColor();
        }

        // ── Final summary ──────────────────────────────────────────────────────
        System.Console.WriteLine();
        WriteSeparator('═');
        WriteColored("  MISSION SUMMARY", ConsoleColor.Cyan);
        WriteSeparator('─');

        if (log.Count == 0)
        {
            WriteColored("  No simulation data.", ConsoleColor.Red);
        }
        else
        {
            var last   = log[^1];
            bool home  = last.X == map.StartX && last.Y == map.StartY;
            bool alive = last.Battery > 0;

            System.Console.WriteLine($"  Total minerals:  {last.TotalMinerals}  " +
                                     $"(Blue={last.MineralsB}, Yellow={last.MineralsY}, Green={last.MineralsG})");
            System.Console.WriteLine($"  Final battery:   {last.Battery:F1}%");
            System.Console.WriteLine($"  Ticks used:      {last.Tick}/{_hours * 2}");
            System.Console.WriteLine($"  Returned home:   {(home  ? "✓ YES" : "✗ NO")}");
            System.Console.WriteLine($"  Rover survived:  {(alive ? "✓ YES" : "✗ NO")}");
            System.Console.WriteLine($"  Distance trav.:  {last.DistanceTraveled:F0} cells");

            System.Console.WriteLine();
            if (home && alive)
                WriteColored($"  ✅ MISSION SUCCESS — {last.TotalMinerals} minerals collected", ConsoleColor.Green);
            else if (!alive)
                WriteColored("  ❌ MISSION FAILED — battery died", ConsoleColor.Red);
            else
                WriteColored("  ⚠  MISSION INCOMPLETE — rover did not return home", ConsoleColor.Yellow);
        }

        WriteSeparator('═');
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void WriteSeparator(char ch)
        => System.Console.WriteLine(new string(ch, 60));

    private static void WriteColored(string text, ConsoleColor color)
    {
        System.Console.ForegroundColor = color;
        System.Console.WriteLine(text);
        System.Console.ResetColor();
    }
}
