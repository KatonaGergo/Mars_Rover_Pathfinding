using MarsRover.Core.Models;
using MarsRover.Core.Simulation;

namespace MarsRover.Core.Utils;

/// <summary>
/// Writes a human-readable mission run log to results/run_TIMESTAMP.txt.
///
/// Lives in MarsRover.Core so it can be called from both MarsRover.Console
/// and MarsRover.UI without any circular dependencies.
///
/// Output path: results/run_YYYYMMDD_HHmmss.txt
/// The results/ directory is git-ignored — files are local only.
/// </summary>
public static class MissionLogger
{
    private const string ResultsDir = "results";
    private const int    W          = 80;

    /// <param name="log">Full tick-by-tick simulation log.</param>
    /// <param name="mapPath">Path to the map file (for display only).</param>
    /// <param name="hours">Mission duration in hours.</param>
    /// <param name="episodes">Training episodes used.</param>
    /// <param name="modelPath">Base model path (without extension).</param>
    /// <param name="map">Loaded GameMap (for start position and mineral counts).</param>
    /// <returns>Path of the file written, or null on failure.</returns>
    public static string? Save(
        List<SimulationLogEntry> log,
        string   mapPath,
        int      hours,
        int      episodes,
        string   modelPath,
        GameMap  map)
    {
        try
        {
            Directory.CreateDirectory(ResultsDir);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path      = Path.Combine(ResultsDir, $"run_{timestamp}.txt");

            using var w = new StreamWriter(path);
            Write(w, log, mapPath, hours, episodes, modelPath, map);
            return path;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"   WARNING: Could not save run log: {ex.Message}");
            Console.ResetColor();
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    private static void Write(
        StreamWriter             w,
        List<SimulationLogEntry> log,
        string   mapPath,
        int      hours,
        int      episodes,
        string   modelPath,
        GameMap  map)
    {
        // ── Title block ───────────────────────────────────────────────────────
        Sep(w, '═');
        w.WriteLine(Center("MARS ROVER  ·  MISSION RUN LOG"));
        w.WriteLine(Center("Q-Learning Agent  ·  Hybrid A* Navigation"));
        Sep(w, '═');
        w.WriteLine();

        // ── Mission parameters ────────────────────────────────────────────────
        Section(w, "MISSION PARAMETERS");
        Field(w, "Date",     DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss"));
        Field(w, "Map file", mapPath);
        Field(w, "Duration", $"{hours} hours  =  {hours * 2} ticks  (1 Martian sol)");
        Field(w, "Episodes", $"{episodes}");
        Field(w, "Model",    $"{modelPath}.qtable.json");
        w.WriteLine();

        if (log.Count == 0)
        {
            w.WriteLine("  No simulation data was recorded.");
            return;
        }

        var  last  = log[^1];
        bool home  = last.X == map.StartX && last.Y == map.StartY;
        bool alive = last.Battery > 0;

        // ── Quick result banner ───────────────────────────────────────────────
        Section(w, "RESULT");
        string resultLine = (home && alive)
            ? $"MISSION SUCCESS  —  {last.TotalMinerals} minerals collected"
            : (!alive ? "MISSION FAILED  —  battery died"
                      : "MISSION INCOMPLETE  —  rover did not return home");

        w.WriteLine($"  ★  {resultLine}");
        w.WriteLine();
        w.WriteLine($"  Minerals    {last.TotalMinerals,3}  {Bar(last.TotalMinerals, 23, 23)}  of 23 theoretical max");
        w.WriteLine($"  Battery     {last.Battery,5:F1}%  {Bar((int)last.Battery, 100, 23)}");
        w.WriteLine($"  Ticks used  {last.Tick,3} / {hours * 2}");
        w.WriteLine($"  Distance    {last.DistanceTraveled:F0} cells traversed");
        w.WriteLine($"  Home        {(home  ? "YES" : "NO")}");
        w.WriteLine($"  Survived    {(alive ? "YES" : "NO")}");
        w.WriteLine();

        // ── Mineral breakdown ─────────────────────────────────────────────────
        Section(w, "MINERAL BREAKDOWN");
        w.WriteLine($"  {"Type",-10} {"Count",6}   {"Bar",25}");
        w.WriteLine($"  {new string('─', 46)}");
        int total = Math.Max(last.TotalMinerals, 1);
        w.WriteLine($"  {"Blue",-10} {last.MineralsB,6}   {Bar(last.MineralsB, total, 25)}");
        w.WriteLine($"  {"Yellow",-10} {last.MineralsY,6}   {Bar(last.MineralsY, total, 25)}");
        w.WriteLine($"  {"Green",-10} {last.MineralsG,6}   {Bar(last.MineralsG, total, 25)}");
        w.WriteLine($"  {new string('─', 46)}");
        w.WriteLine($"  {"TOTAL",-10} {last.TotalMinerals,6}");
        w.WriteLine();

        // ── Phase statistics ──────────────────────────────────────────────────
        Section(w, "PHASE STATISTICS");
        int dayTicks     = log.Count(e => e.IsDay);
        int nightTicks   = log.Count(e => !e.IsDay);
        int dayMoves     = log.Count(e =>  e.IsDay && e.Action.Type == RoverActionType.Move);
        int nightMoves   = log.Count(e => !e.IsDay && e.Action.Type == RoverActionType.Move);
        int mineCount    = log.Count(e =>  e.Action.Type == RoverActionType.Mine
                                        && e.EventNote.Contains("Collected"));
        int standbyTicks = log.Count(e =>  e.Action.Type == RoverActionType.Standby);

        w.WriteLine($"  Day ticks      :  {dayTicks}  ({dayTicks * 0.5:F1}h)");
        w.WriteLine($"  Night ticks    :  {nightTicks}  ({nightTicks * 0.5:F1}h)");
        w.WriteLine($"  Move ticks day :  {dayMoves}");
        w.WriteLine($"  Move ticks ngt :  {nightMoves}");
        w.WriteLine($"  Mine ticks     :  {mineCount}  (minerals collected)");
        w.WriteLine($"  Standby ticks  :  {standbyTicks}");
        w.WriteLine();

        // ── Battery timeline ──────────────────────────────────────────────────
        Section(w, "BATTERY TIMELINE  (every 4 ticks)");
        w.WriteLine($"  {"Tick",-5} {"Phase",-4} {"Battery",8}   Chart");
        w.WriteLine($"  {new string('─', 60)}");
        for (int i = 0; i < log.Count; i += 4)
        {
            var e   = log[i];
            string phase = e.IsDay ? "DAY" : "NGT";
            w.WriteLine($"  {e.Tick,-5} {phase,-4} {e.Battery,6:F1}%   {Bar((int)e.Battery, 100, 30)}");
        }
        if (log.Count % 4 != 1)
        {
            var e = log[^1];
            w.WriteLine($"  {e.Tick,-5} {(e.IsDay ? "DAY" : "NGT"),-4} {e.Battery,6:F1}%   {Bar((int)e.Battery, 100, 30)}");
        }
        w.WriteLine();

        // ── Full tick-by-tick log ─────────────────────────────────────────────
        Section(w, "FULL TICK LOG");
        w.WriteLine($"  {"TICK",-5} {"TIME",-10} {"PHASE",-12} {"POSITION",-10} {"BATTERY",-8} {"MINERALS",-13} EVENT");
        w.WriteLine($"  {new string('─', W - 2)}");

        int prevMinerals = 0;
        foreach (var e in log)
        {
            bool newMin      = e.TotalMinerals > prevMinerals;
            prevMinerals     = e.TotalMinerals;
            string dayStr    = e.IsDay ? "DAY" : "NGT";
            string time      = $"{dayStr} {e.HourOfSol,4:F1}h";
            string pos       = $"({e.X,2},{e.Y,2})";
            string batt      = $"{e.Battery,5:F1}%";
            string minerals  = $"B{e.MineralsB} Y{e.MineralsY} G{e.MineralsG} ={e.TotalMinerals}";
            string marker    = newMin ? " <<< NEW MINERAL" : "";

            w.WriteLine(
                $"  {e.Tick,-5} {time,-10} {e.Phase,-12} {pos,-10} {batt,-8} {minerals,-13} {e.EventNote}{marker}");
        }

        w.WriteLine();
        Sep(w, '═');
        w.WriteLine(Center("END OF LOG"));
        Sep(w, '═');
    }

    // ── Formatting helpers ────────────────────────────────────────────────────

    private static string Bar(int value, int max, int len)
    {
        if (max <= 0) return "[" + new string('░', len) + "]";
        int filled = (int)Math.Round((double)value / max * len);
        filled     = Math.Clamp(filled, 0, len);
        return "[" + new string('█', filled) + new string('░', len - filled) + "]";
    }

    private static void Sep(StreamWriter w, char ch)
        => w.WriteLine(new string(ch, W));

    private static void Section(StreamWriter w, string title)
    {
        w.WriteLine($"  ┌─ {title} {"".PadRight(W - title.Length - 6, '─')}┐");
        w.WriteLine();
    }

    private static void Field(StreamWriter w, string label, string value)
        => w.WriteLine($"  {label,-12}:  {value}");

    private static string Center(string text)
    {
        int pad = Math.Max(0, (W - text.Length) / 2);
        return new string(' ', pad) + text;
    }
}
