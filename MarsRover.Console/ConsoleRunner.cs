using MarsRover.Core.Algorithm;
using MarsRover.Core.Models;
using MarsRover.Core.Simulation;

namespace MarsRover.Console;

/// <summary>
/// Terminal interface for the Mars Rover Q-Learning agent.
///
/// The FLOW, so like how it goes:
///   1. Print mission parameters
///   2. If a saved model exists: show its info and ask to resume or run-only
///   3. Train for N episodes (live progress bar)
///   4. Run one deployment simulation (pure exploitation, epsilon=0)
///   5. Print the full tick-by-tick log
///   6. Print final summary
/// </summary>
public class ConsoleRunner
{
    private readonly AppConfig _cfg;
    private const int BarWidth = 40;

    public ConsoleRunner(AppConfig cfg) => _cfg = cfg;

    // ── --info entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Prints saved model metadata and exits. Called by Program.cs for --info.
    /// </summary>
    public static void PrintModelInfo(string modelPath)
    {
        WriteSeparator('═');
        WriteColored("  SAVED MODEL INFO", ConsoleColor.Cyan);
        WriteSeparator('═');

        var info = SimulationRunner.GetModelInfo(modelPath);

        if (info == null)
        {
            WriteColored($"  No saved model found at: {modelPath}.qtable.json", ConsoleColor.Yellow);
        }
        else
        {
            System.Console.WriteLine($"  Model path:   {info.ModelPath}.qtable.json");
            System.Console.WriteLine($"  Saved at:     {FormatDate(info.SavedAt)}");
            System.Console.WriteLine($"  Episodes:     {info.EpisodesCompleted:N0}");
            System.Console.WriteLine($"  Best result:  {info.BestMinerals} minerals");
            System.Console.WriteLine($"  Epsilon:      {info.Epsilon:F4}  (0=pure exploit, 1=random)");
            System.Console.WriteLine($"  States known: {info.StatesKnown:N0}");
        }

        WriteSeparator('═');
    }

    // ── Main run ──────────────────────────────────────────────────────────────

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
            map = GameMap.LoadFromFile(_cfg.MapPath);
        }
        catch (Exception ex)
        {
            WriteColored($"  ERROR loading map: {ex.Message}", ConsoleColor.Red);
            return;
        }

        WriteColored($"  Map:       {_cfg.MapPath}", ConsoleColor.Green);
        System.Console.WriteLine($"  Grid:      {GameMap.Width}×{GameMap.Height}");
        System.Console.WriteLine($"  Start:     ({map.StartX}, {map.StartY})");
        System.Console.WriteLine($"  Minerals:  {map.RemainingMinerals.Count}");
        System.Console.WriteLine($"  Duration:  {_cfg.Hours}h = {_cfg.Hours * 2} ticks");
        System.Console.WriteLine($"  Episodes:  {_cfg.Episodes}");
        System.Console.WriteLine($"  Model:     {_cfg.ModelPath}.qtable.json");
        System.Console.WriteLine();

        // ── Resume prompt ──────────────────────────────────────────────────────
        var simRunner  = new SimulationRunner(map, _cfg.Hours, _cfg.ModelPath);
        var savedModel = simRunner.GetModelInfo();
        bool skipTrain = false;

        if (savedModel != null)
        {
            WriteSeparator('─');
            WriteColored("  SAVED MODEL FOUND", ConsoleColor.Cyan);
            System.Console.WriteLine($"  Episodes completed:  {savedModel.EpisodesCompleted:N0}");
            System.Console.WriteLine($"  Best result:         {savedModel.BestMinerals} minerals");
            System.Console.WriteLine($"  Epsilon:             {savedModel.Epsilon:F4}");
            System.Console.WriteLine($"  States known:        {savedModel.StatesKnown:N0}");
            System.Console.WriteLine($"  Saved at:            {FormatDate(savedModel.SavedAt)}");
            WriteSeparator('─');
            System.Console.WriteLine();
            System.Console.Write("  [T] Train more    [R] Run only    [Q] Quit  → ");
            System.Console.ForegroundColor = ConsoleColor.White;

            while (true)
            {
                var key = System.Console.ReadKey(intercept: true).Key;
                if (key == ConsoleKey.T) { System.Console.WriteLine("Train"); break; }
                if (key == ConsoleKey.R) { System.Console.WriteLine("Run only"); skipTrain = true; break; }
                if (key == ConsoleKey.Q) { System.Console.WriteLine("Quit"); return; }
            }
            System.Console.ResetColor();
            System.Console.WriteLine();
        }

        // ── Training ───────────────────────────────────────────────────────────
        if (!skipTrain)
        {
            WriteSeparator('─');
            WriteColored(savedModel != null ? "▶ RESUMING TRAINING" : "▶ TRAINING",
                         ConsoleColor.Yellow);
            WriteSeparator('─');

            int    bestSoFar  = savedModel?.BestMinerals ?? 0;
            var    trainStart = DateTime.Now;

            simRunner.Train(_cfg.Episodes, progress =>
            {
                int    filled = (int)((double)progress.Episode / progress.TotalEps * BarWidth);
                string bar    = new string('█', filled) + new string('░', BarWidth - filled);
                double elapsed = (DateTime.Now - trainStart).TotalSeconds;
                double eta     = progress.Episode > 0
                                 ? elapsed / progress.Episode * (progress.TotalEps - progress.Episode)
                                 : 0;

                if (progress.BestMinerals > bestSoFar)
                {
                    bestSoFar = progress.BestMinerals;
                    System.Console.WriteLine();
                    WriteColored(
                        $"  ★ New best: {bestSoFar} minerals  (ep {progress.Episode})",
                        ConsoleColor.Green);
                }

                System.Console.Write(
                    $"\r  [{bar}] {progress.Episode}/{progress.TotalEps}" +
                    $"  ε={progress.Epsilon:F3}" +
                    $"  best={progress.BestMinerals}⛏" +
                    $"  states={progress.StatesKnown}" +
                    $"  ETA {eta:F0}s  ");
            });

            System.Console.WriteLine();
            System.Console.WriteLine();
            double trainSecs = (DateTime.Now - trainStart).TotalSeconds;
            WriteColored($"  Training complete in {trainSecs:F1}s.", ConsoleColor.Green);
            WriteColored($"  Best minerals during training: {bestSoFar}", ConsoleColor.Green);
            System.Console.WriteLine();
        }

        // ── Deployment simulation ──────────────────────────────────────────────
        WriteSeparator('─');
        WriteColored("▶ RUNNING SIMULATION (epsilon=0, pure exploitation)", ConsoleColor.Yellow);
        WriteSeparator('─');

        var log = simRunner.RunSimulation();

        // ── Tick-by-tick log ───────────────────────────────────────────────────
        System.Console.WriteLine();
        System.Console.WriteLine(
            $"  {"Tick",-5} {"Sol",-4} {"Time",-8} {"Phase",-6} {"Pos",-10} {"Batt",-7} {"Minerals",-9} Event");
        System.Console.WriteLine($"  {new string('─', 72)}");

        int lastMinerals = 0;
        foreach (var entry in log)
        {
            bool newMineral = entry.TotalMinerals > lastMinerals;
            lastMinerals    = entry.TotalMinerals;

            string pos      = $"({entry.X},{entry.Y})";
            string batt     = $"{entry.Battery:F0}%";
            string phase    = entry.Phase[..Math.Min(6, entry.Phase.Length)];
            string daymark  = entry.IsDay ? "☀" : "🌑";
            string time     = $"{daymark}{entry.HourOfSol:F1}h";
            string minerals = $"B{entry.MineralsB}/Y{entry.MineralsY}/G{entry.MineralsG}";

            if (newMineral)
                System.Console.ForegroundColor = ConsoleColor.Green;
            else if (entry.Battery < 10)
                System.Console.ForegroundColor = ConsoleColor.Red;
            else if (entry.Battery < 25)
                System.Console.ForegroundColor = ConsoleColor.Yellow;
            else
                System.Console.ForegroundColor = ConsoleColor.Gray;

            System.Console.WriteLine(
                $"  {entry.Tick,-5} {entry.Sol,-4} {time,-8} {phase,-6} {pos,-10} {batt,-7} {minerals,-9} {entry.EventNote}");
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
            var last  = log[^1];
            bool home  = last.X == map.StartX && last.Y == map.StartY;
            bool alive = last.Battery > 0;

            System.Console.WriteLine(
                $"  Total minerals:  {last.TotalMinerals}  " +
                $"(Blue={last.MineralsB}, Yellow={last.MineralsY}, Green={last.MineralsG})");
            System.Console.WriteLine($"  Final battery:   {last.Battery:F1}%");
            System.Console.WriteLine($"  Ticks used:      {last.Tick}/{_cfg.Hours * 2}");
            System.Console.WriteLine($"  Returned home:   {(home  ? "✓ YES" : "✗ NO")}");
            System.Console.WriteLine($"  Rover survived:  {(alive ? "✓ YES" : "✗ NO")}");
            System.Console.WriteLine($"  Distance trav.:  {last.DistanceTraveled:F0} cells");
            System.Console.WriteLine();

            if (home && alive)
                WriteColored($"  ✅ MISSION SUCCESS — {last.TotalMinerals} minerals collected",
                             ConsoleColor.Green);
            else if (!alive)
                WriteColored("  ❌ MISSION FAILED — battery died", ConsoleColor.Red);
            else
                WriteColored("  ⚠  MISSION INCOMPLETE — rover did not return home",
                             ConsoleColor.Yellow);
        }

        WriteSeparator('═');

        // ── Auto-save run log ──────────────────────────────────────────────────
        var savedPath = RunLogger.Save(log, _cfg, map);
        if (savedPath != null)
        {
            System.Console.WriteLine();
            WriteColored($"  💾 Run log saved → {savedPath}", ConsoleColor.DarkGray);
        }
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

    private static string FormatDate(string iso)
    {
        if (string.IsNullOrEmpty(iso)) return "unknown";
        return DateTime.TryParse(iso, out var dt)
               ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
               : iso;
    }
}
