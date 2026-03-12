using MarsRover.Core.Models;
using MarsRover.Core.Simulation;

namespace MarsRover.Console;

/// <summary>
/// Writes the full simulation run to a clean, human-readable .txt file.
/// Output path: results/run_YYYYMMDD_HHmmss.txt
///
/// The results/ folder is git-ignored, files are local only.
/// </summary>
public static class RunLogger
{
    private const string ResultsDir = "results";
    private const int    W          = 80; // file line width

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
            Write(w, log, cfg, map);
            return path;
        }
        catch (Exception ex)
        {
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine($"   WARNING: Could not save run log: {ex.Message}");
            System.Console.ResetColor();
            return null;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // File writer
    // ═════════════════════════════════════════════════════════════════════════

    private static void Write(StreamWriter w,
                              List<SimulationLogEntry> log,
                              AppConfig cfg, GameMap map)
    {
        // ── Title block ───────────────────────────────────────────────────────
        Sep(w, '═');
        w.WriteLine(Center("MARS ROVER  ·  MISSION RUN LOG"));
        w.WriteLine(Center("Q-Learning Agent  ·  Hybrid A* Navigation"));
        Sep(w, '═');
        w.WriteLine();

        // ── Mission parameters ────────────────────────────────────────────────
        Section(w, "MISSION PARAMETERS");
        Field(w, "Date",       DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss"));
        Field(w, "Map file",   cfg.MapPath);
        Field(w, "Duration",   $"{cfg.Hours} hours  =  {cfg.Hours * 2} ticks  (1 Martian sol)");
        Field(w, "Episodes",   $"{cfg.Episodes}");
        Field(w, "Model",      $"{cfg.ModelPath}.qtable.json");
        w.WriteLine();

        if (log.Count == 0)
        {
            w.WriteLine("  No simulation data was recorded.");
            return;
        }

        var last   = log[^1];
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
        w.WriteLine($"  Ticks used  {last.Tick,3} / {cfg.Hours * 2}");
        w.WriteLine($"  Distance    {last.DistanceTraveled:F0} cells traversed");
        w.WriteLine($"  Home        {(home  ? "YES" : "NO")}");
        w.WriteLine($"  Survived    {(alive ? "YES" : "NO")}");
        w.WriteLine();

        // ── Mineral breakdown ─────────────────────────────────────────────────
        Section(w, "MINERAL BREAKDOWN");
        w.WriteLine($"  {"Type",-10} {"Count",6}   {"Bar",25}");
        w.WriteLine($"  {new string('─', 46)}");
        w.WriteLine($"  {"Blue",-10} {last.MineralsB,6}   {Bar(last.MineralsB, Math.Max(last.TotalMinerals,1), 25)}");
        w.WriteLine($"  {"Yellow",-10} {last.MineralsY,6}   {Bar(last.MineralsY, Math.Max(last.TotalMinerals,1), 25)}");
        w.WriteLine($"  {"Green",-10} {last.MineralsG,6}   {Bar(last.MineralsG, Math.Max(last.TotalMinerals,1), 25)}");
        w.WriteLine($"  {new string('─', 46)}");
        w.WriteLine($"  {"TOTAL",-10} {last.TotalMinerals,6}");
        w.WriteLine();

        // ── Day / night split ─────────────────────────────────────────────────
        Section(w, "PHASE STATISTICS");
        int dayTicks    = log.Count(e => e.IsDay);
        int nightTicks  = log.Count(e => !e.IsDay);
        int dayMoves    = log.Count(e => e.IsDay  && e.Action.Type == RoverActionType.Move);
        int nightMoves  = log.Count(e => !e.IsDay && e.Action.Type == RoverActionType.Move);
        int mineCount   = log.Count(e => e.Action.Type == RoverActionType.Mine
                                      && e.EventNote.Contains("Collected"));
        int standbyTicks = log.Count(e => e.Action.Type == RoverActionType.Standby);

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
        // Always show last tick
        if (log.Count % 4 != 1)
        {
            var e = log[^1];
            string phase = e.IsDay ? "DAY" : "NGT";
            w.WriteLine($"  {e.Tick,-5} {phase,-4} {e.Battery,6:F1}%   {Bar((int)e.Battery, 100, 30)}");
        }
        w.WriteLine();

        // ── Full tick-by-tick log ─────────────────────────────────────────────
        Section(w, "FULL TICK LOG");
        w.WriteLine($"  {"TICK",-5} {"TIME",-10} {"PHASE",-12} {"POSITION",-10} {"BATTERY",-8} {"MINERALS",-13} EVENT");
        w.WriteLine($"  {new string('─', W - 2)}");

        int prevMinerals = 0;
        foreach (var e in log)
        {
            bool newMin = e.TotalMinerals > prevMinerals;
            prevMinerals = e.TotalMinerals;

            string dayStr   = e.IsDay ? "DAY" : "NGT";
            string time     = $"{dayStr} {e.HourOfSol,4:F1}h";
            string pos      = $"({e.X,2},{e.Y,2})";
            string batt     = $"{e.Battery,5:F1}%";
            string minerals = $"B{e.MineralsB} Y{e.MineralsY} G{e.MineralsG} ={e.TotalMinerals}";
            string marker   = newMin ? " <<< NEW MINERAL" : "";

            w.WriteLine(
                $"  {e.Tick,-5} {time,-10} {e.Phase,-12} {pos,-10} {batt,-8} {minerals,-13} {e.EventNote}{marker}");
        }

        w.WriteLine();
        Sep(w, '═');
        w.WriteLine(Center("END OF LOG"));
        Sep(w, '═');
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Formatting helpers
    // ═════════════════════════════════════════════════════════════════════════

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
