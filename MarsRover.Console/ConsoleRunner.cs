using MarsRover.Core.Algorithm;
using MarsRover.Core.Models;
using MarsRover.Core.Simulation;

namespace MarsRover.Console;

/// <summary>
/// Terminal dashboard for the Mars Rover Q-learning agent.
///
/// FLOW:
///   1. ASCII art banner + mission card
///   2. Main menu — button-style keyboard navigation
///   3. Training with live progress panel
///   4. Deployment simulation with colour-coded tick log
///   5. Mission summary card
///   6. Auto-save run log to results/
/// </summary>
public class ConsoleRunner
{
    private readonly AppConfig _cfg;
    private const int W = 72; // dashboard width

    public ConsoleRunner(AppConfig cfg) => _cfg = cfg;

    // ═════════════════════════════════════════════════════════════════════════
    // --info entry point
    // ═════════════════════════════════════════════════════════════════════════

    public static void PrintModelInfo(string modelPath)
    {
        PrintBanner();
        var info = SimulationRunner.GetModelInfo(modelPath);

        BoxTop("  SAVED MODEL INFO");
        if (info == null)
        {
            BoxLine($"  No saved model at: {SimulationRunner.ResolveModelPath(modelPath)}.qtable.json", ConsoleColor.Yellow);
        }
        else
        {
            BoxLine($"  Path     :  {SimulationRunner.ResolveModelPath(info.ModelPath)}.qtable.json");
            BoxLine($"  Saved    :  {FormatDate(info.SavedAt)}");
            BoxLine($"  Episodes :  {info.EpisodesCompleted:N0}");
            BoxLine($"  Best     :  {info.BestMinerals} minerals  {MineralBar(info.BestMinerals, 23)}");
            BoxLine($"  States   :  {info.StatesKnown:N0}");
            BoxLine($"  Schema   :  v{info.PolicySchemaVersion}");
            BoxLine($"  Profile  :  {info.TrainingProfile}");
        }
        BoxBottom();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Main run
    // ═════════════════════════════════════════════════════════════════════════

    public void Run()
    {
        PrintBanner();

        // ── Load map ──────────────────────────────────────────────────────────
        GameMap map;
        try
        {
            map = GameMap.LoadFromFile(_cfg.MapPath);
        }
        catch (Exception ex)
        {
            BoxTop("  ERROR");
            BoxLine($"  Could not load map: {ex.Message}", ConsoleColor.Red);
            BoxBottom();
            return;
        }

        // ── Mission card ──────────────────────────────────────────────────────
        PrintMissionCard(map);

        // ── Check for saved model ─────────────────────────────────────────────
        var simRunner  = new SimulationRunner(map, _cfg.Hours, _cfg.ModelPath);
        var savedModel = simRunner.GetModelInfo();

        // ── Main menu ─────────────────────────────────────────────────────────
        var choice = ShowMainMenu(savedModel);
        if (choice == MenuChoice.Quit) return;

        bool skipTrain = (choice == MenuChoice.RunOnly);

        // ── Training ──────────────────────────────────────────────────────────
        if (!skipTrain)
        {
            RunTraining(simRunner, savedModel);
        }

        // ── Deployment ────────────────────────────────────────────────────────
        Ln();
        BoxTop("  MISSION DEPLOYMENT  —  epsilon = 0  (pure exploitation)");
        BoxLine("  Launching rover with best learned policy...", ConsoleColor.Yellow);
        BoxBottom();
        Ln();

        var log = simRunner.RunSimulation();

        PrintTickLog(log);

        // ── Summary ───────────────────────────────────────────────────────────
        PrintMissionSummary(log, map);

        // ── Save log ──────────────────────────────────────────────────────────
        var savedPath = RunLogger.Save(log, _cfg, map);
        if (savedPath != null)
        {
            Ln();
            Col(ConsoleColor.DarkCyan);
            System.Console.WriteLine($"   ──  Run log saved  →  {savedPath}");
            Reset();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Banner
    // ═════════════════════════════════════════════════════════════════════════

    private static void PrintBanner()
    {
        System.Console.Clear();
        Ln();
        Col(ConsoleColor.DarkRed);
        System.Console.WriteLine(@"   ██████   ██████  ██    ██ ███████ ██████");
        System.Console.WriteLine(@"   ██   ██ ██    ██ ██    ██ ██      ██   ██");
        System.Console.WriteLine(@"   ██████  ██    ██ ██    ██ █████   ██████");
        System.Console.WriteLine(@"   ██   ██ ██    ██  ██  ██  ██      ██   ██");
        System.Console.WriteLine(@"   ██   ██  ██████    ████   ███████ ██   ██");
        Reset();
        Ln();
        Col(ConsoleColor.Red);
        System.Console.WriteLine(@"   ·· D A S H B O A R D  ·  Q - L E A R N I N G  A G E N T ··");
        Reset();
        System.Console.WriteLine($"   {"".PadRight(W - 4, '─')}");
        Ln();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Mission card
    // ═════════════════════════════════════════════════════════════════════════

    private void PrintMissionCard(GameMap map)
    {
        BoxTop("  MISSION PARAMETERS");
        BoxLine($"  Map        :  {_cfg.MapPath}");
        BoxLine($"  Grid       :  {GameMap.Width} × {GameMap.Height}");
        BoxLine($"  Start pos  :  ({map.StartX}, {map.StartY})");
        BoxLine($"  Minerals   :  {map.RemainingMinerals.Count}  (Blue / Yellow / Green)");
        BoxLine($"  Duration   :  {_cfg.Hours} hours  =  {_cfg.Hours * 2} ticks  (1 Martian sol)");
        BoxLine($"  Episodes   :  {_cfg.Episodes}");
        BoxLine($"  Model      :  {SimulationRunner.ResolveModelPath(_cfg.ModelPath)}.qtable.json");
        BoxBottom();
        Ln();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Main menu
    // ═════════════════════════════════════════════════════════════════════════

    private enum MenuChoice { Train, RunOnly, Quit }

    // ── Button definitions ────────────────────────────────────────────────────

    private record Button(string Label, string Description, MenuChoice Choice, ConsoleColor Color);

    private static MenuChoice ShowMainMenu(ModelInfo? savedModel)
    {
        if (savedModel != null)
        {
            BoxTop("  SAVED MODEL FOUND");
            BoxLine($"  Episodes   :  {savedModel.EpisodesCompleted:N0}");
            BoxLine($"  Best score :  {savedModel.BestMinerals} minerals  {MineralBar(savedModel.BestMinerals, 23)}");
            BoxLine($"  Epsilon    :  {savedModel.Epsilon:F4}");
            BoxLine($"  States     :  {savedModel.StatesKnown:N0}");
            BoxLine($"  Schema     :  v{savedModel.PolicySchemaVersion}");
            BoxLine($"  Profile    :  {savedModel.TrainingProfile}");
            BoxLine($"  Saved at   :  {FormatDate(savedModel.SavedAt)}");
            BoxBottom();
            Ln();
        }

        // Build button list based on whether a saved model exists
        var buttons = savedModel != null
            ? new[]
            {
                new Button("  TRAIN MORE  ", "Train more episodes and improve the model",   MenuChoice.Train,   ConsoleColor.Yellow),
                new Button("  RUN ONLY    ", "Deploy the saved policy now (skip training)", MenuChoice.RunOnly, ConsoleColor.Green),
                new Button("  QUIT        ", "Exit the application",                        MenuChoice.Quit,    ConsoleColor.DarkGray),
            }
            : new[]
            {
                new Button("  TRAIN & RUN ", "Train the agent then deploy the best policy", MenuChoice.Train,   ConsoleColor.Yellow),
                new Button("  QUIT        ", "Exit the application",                        MenuChoice.Quit,    ConsoleColor.DarkGray),
            };

        // Instruction header
        System.Console.WriteLine($"   {"".PadRight(W - 4, '─')}");
        Col(ConsoleColor.DarkGray);
        System.Console.WriteLine("   SELECT AN ACTION   ←/→ or A/D to move   Enter to confirm");
        Reset();
        System.Console.WriteLine($"   {"".PadRight(W - 4, '─')}");
        Ln();

        // Remember where the buttons start so we can redraw on navigation
        int menuRow = System.Console.CursorTop;
        int selected = 0;

        while (true)
        {
            // Redraw all buttons at the fixed row
            System.Console.SetCursorPosition(0, menuRow);
            DrawButtonRow(buttons, selected);

            var key = System.Console.ReadKey(intercept: true).Key;

            switch (key)
            {
                case ConsoleKey.LeftArrow:
                case ConsoleKey.A:
                    selected = (selected - 1 + buttons.Length) % buttons.Length;
                    break;

                case ConsoleKey.RightArrow:
                case ConsoleKey.D:
                    selected = (selected + 1) % buttons.Length;
                    break;

                case ConsoleKey.Enter:
                case ConsoleKey.Spacebar:
                    Ln(); Ln();
                    Col(ConsoleColor.White);
                    System.Console.WriteLine($"   ▶  {buttons[selected].Label.Trim()}");
                    Reset();
                    Ln();
                    return buttons[selected].Choice;

                // Also keep letter shortcuts as fallback
                case ConsoleKey.T:
                    Ln(); Ln();
                    Col(ConsoleColor.White);
                    System.Console.WriteLine($"   ▶  {buttons[0].Label.Trim()}");
                    Reset(); Ln();
                    return MenuChoice.Train;

                case ConsoleKey.R when savedModel != null:
                    Ln(); Ln();
                    Col(ConsoleColor.White);
                    System.Console.WriteLine("   ▶  RUN ONLY");
                    Reset(); Ln();
                    return MenuChoice.RunOnly;

                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    Ln(); Ln();
                    Col(ConsoleColor.White);
                    System.Console.WriteLine("   ▶  QUIT");
                    Reset(); Ln();
                    return MenuChoice.Quit;
            }
        }
    }

    /// <summary>
    /// Draws all buttons on one row. The selected button is highlighted with a
    /// filled background-style border; others are dimmed.
    /// </summary>
    private static void DrawButtonRow(Button[] buttons, int selected)
    {
        // Row 1: top border
        System.Console.Write("   ");
        for (int i = 0; i < buttons.Length; i++)
        {
            bool active = i == selected;
            Col(active ? buttons[i].Color : ConsoleColor.DarkGray);
            int w = buttons[i].Label.Length + 2;
            System.Console.Write("╔" + new string('═', w) + "╗");
            Reset();
            System.Console.Write("   ");
        }
        Ln();

        // Row 2: label
        System.Console.Write("   ");
        for (int i = 0; i < buttons.Length; i++)
        {
            bool active = i == selected;
            Col(active ? buttons[i].Color : ConsoleColor.DarkGray);
            System.Console.Write("║ ");
            if (active)
            {
                Col(ConsoleColor.White);
                System.Console.Write(buttons[i].Label);
                Col(buttons[i].Color);
            }
            else
            {
                System.Console.Write(buttons[i].Label);
            }
            System.Console.Write(" ║");
            Reset();
            System.Console.Write("   ");
        }
        Ln();

        // Row 3: bottom border
        System.Console.Write("   ");
        for (int i = 0; i < buttons.Length; i++)
        {
            bool active = i == selected;
            Col(active ? buttons[i].Color : ConsoleColor.DarkGray);
            int w = buttons[i].Label.Length + 2;
            System.Console.Write("╚" + new string('═', w) + "╝");
            Reset();
            System.Console.Write("   ");
        }
        Ln();
        Ln();

        // Row 4: description of selected button
        Col(ConsoleColor.DarkGray);
        string desc = buttons[selected].Description;
        System.Console.WriteLine("   " + desc.PadRight(W - 4));
        Reset();
        Ln();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Training
    // ═════════════════════════════════════════════════════════════════════════

    private void RunTraining(SimulationRunner simRunner, ModelInfo? savedModel)
    {
        BoxTop(savedModel != null ? "  RESUMING TRAINING" : "  TRAINING");
        BoxLine($"  Running {_cfg.Episodes} episodes on {System.Environment.ProcessorCount} threads...",
                ConsoleColor.Yellow);
        BoxBottom();
        Ln();

        int    bestSoFar  = savedModel?.BestMinerals ?? 0;
        var    trainStart = DateTime.Now;
        const int barW    = 36;

        simRunner.Train(_cfg.Episodes, progress =>
        {
            double elapsed = (DateTime.Now - trainStart).TotalSeconds;
            double eta     = progress.Episode > 0
                             ? elapsed / progress.Episode * (progress.TotalEps - progress.Episode)
                             : 0;

            if (progress.BestMinerals > bestSoFar)
            {
                bestSoFar = progress.BestMinerals;
                // Print new-best on its own permanent line
                System.Console.Write("\r" + new string(' ', W) + "\r");
                Col(ConsoleColor.Green);
                System.Console.WriteLine(
                    $"   ★  New best: {bestSoFar} minerals   {MineralBar(bestSoFar, 23)}" +
                    $"   ep {progress.Episode}");
                Reset();
            }

            int    filled  = (int)((double)progress.Episode / progress.TotalEps * barW);
            string bar     = new string('█', filled) + new string('░', barW - filled);
            string line    =
                $"\r   [{bar}]  {progress.Episode}/{progress.TotalEps}" +
                $"   ε {progress.Epsilon:F3}" +
                $"   ⛏ {progress.BestMinerals}" +
                $"   ETA {eta:F0}s   ";

            System.Console.Write(line);
        });

        System.Console.Write("\r" + new string(' ', W) + "\r");
        double secs = (DateTime.Now - trainStart).TotalSeconds;

        BoxTop("  TRAINING COMPLETE");
        BoxLine($"  Time elapsed  :  {secs:F1}s");
        BoxLine($"  Best result   :  {bestSoFar} minerals   {MineralBar(bestSoFar, 23)}",
                ConsoleColor.Green);
        BoxBottom();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Tick log
    // ═════════════════════════════════════════════════════════════════════════

    private static void PrintTickLog(List<SimulationLogEntry> log)
    {
        Col(ConsoleColor.DarkGray);
        System.Console.WriteLine(
            $"   {"TICK",-5} {"TIME",-9} {"PHASE",-11} {"POS",-10} {"BATTERY",-9} {"TOTAL",-6} EVENT");
        System.Console.WriteLine($"   {"".PadRight(W - 4, '─')}");
        Reset();

        int lastMinerals = 0;
        foreach (var e in log)
        {
            bool newMineral = e.TotalMinerals > lastMinerals;
            lastMinerals    = e.TotalMinerals;

            string tick    = $"{e.Tick}";
            string day     = e.IsDay ? "☀ DAY " : "🌑 NGT";
            string time    = $"{day} {e.HourOfSol:F1}h";
            string phase   = e.Phase.PadRight(10);
            string pos     = $"({e.X,2},{e.Y,2})";
            string batt    = $"{e.Battery,5:F1}%  {BatteryBar(e.Battery, 10)}";
            string total   = $"⛏ {e.TotalMinerals}";

            ConsoleColor col =
                newMineral        ? ConsoleColor.Green  :
                e.Battery < 10    ? ConsoleColor.Red    :
                e.Battery < 25    ? ConsoleColor.Yellow :
                                    ConsoleColor.Gray;

            Col(col);
            System.Console.WriteLine(
                $"   {tick,-5} {time,-9} {phase,-11} {pos,-10} {batt,-18} {total,-6} {e.EventNote}");
            Reset();
        }

        Ln();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Mission summary
    // ═════════════════════════════════════════════════════════════════════════

    private void PrintMissionSummary(List<SimulationLogEntry> log, GameMap map)
    {
        BoxTop("  MISSION SUMMARY");

        if (log.Count == 0)
        {
            BoxLine("  No simulation data.", ConsoleColor.Red);
            BoxBottom();
            return;
        }

        var  last  = log[^1];
        bool home  = last.X == map.StartX && last.Y == map.StartY;
        bool alive = last.Battery > 0;

        BoxLine($"  Minerals      :  {last.TotalMinerals}   " +
                $"Blue {last.MineralsB}  ·  Yellow {last.MineralsY}  ·  Green {last.MineralsG}");
        BoxLine($"  Battery left  :  {last.Battery:F1}%   {BatteryBar(last.Battery, 20)}");
        BoxLine($"  Ticks used    :  {last.Tick} / {_cfg.Hours * 2}");
        BoxLine($"  Distance      :  {last.DistanceTraveled:F0} cells");
        BoxLine($"  Returned home :  {(home  ? "YES ✓" : "NO  ✗")}",
                home  ? ConsoleColor.Green : ConsoleColor.Red);
        BoxLine($"  Survived      :  {(alive ? "YES ✓" : "NO  ✗")}",
                alive ? ConsoleColor.Green : ConsoleColor.Red);

        Ln();
        if (home && alive)
        {
            BoxLine($"  ✅  MISSION SUCCESS  —  {last.TotalMinerals} minerals collected",
                    ConsoleColor.Green);
            BoxLine($"      {MineralBar(last.TotalMinerals, 23, wide: true)}",
                    ConsoleColor.Green);
        }
        else if (!alive)
            BoxLine("  ❌  MISSION FAILED  —  battery died", ConsoleColor.Red);
        else
            BoxLine("  ⚠   MISSION INCOMPLETE  —  rover did not return home", ConsoleColor.Yellow);

        BoxBottom();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Box drawing helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static void BoxTop(string title)
    {
        Col(ConsoleColor.DarkCyan);
        System.Console.WriteLine($"   ╔{"".PadRight(W - 5, '═')}╗");
        System.Console.Write($"   ║");
        Reset();
        Col(ConsoleColor.Cyan);
        System.Console.Write(title.PadRight(W - 5));
        Col(ConsoleColor.DarkCyan);
        System.Console.WriteLine("║");
        System.Console.WriteLine($"   ╠{"".PadRight(W - 5, '═')}╣");
        Reset();
    }

    private static void BoxLine(string text, ConsoleColor textColor = ConsoleColor.Gray)
    {
        Col(ConsoleColor.DarkCyan);
        System.Console.Write("   ║ ");
        Col(textColor);
        System.Console.Write(text.PadRight(W - 7));
        Col(ConsoleColor.DarkCyan);
        System.Console.WriteLine(" ║");
        Reset();
    }

    private static void BoxBottom()
    {
        Col(ConsoleColor.DarkCyan);
        System.Console.WriteLine($"   ╚{"".PadRight(W - 5, '═')}╝");
        Reset();
        Ln();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Visual bar helpers
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Small mineral progress bar: ████░░░░ (max 23 minerals)</summary>
    private static string MineralBar(int minerals, int max, bool wide = false)
    {
        int len    = wide ? 23 : 12;
        int filled = (int)Math.Round((double)minerals / max * len);
        filled     = Math.Clamp(filled, 0, len);
        return "[" + new string('█', filled) + new string('░', len - filled) + "]";
    }

    /// <summary>Compact battery bar.</summary>
    private static string BatteryBar(double pct, int len)
    {
        int filled = (int)Math.Round(pct / 100.0 * len);
        filled     = Math.Clamp(filled, 0, len);
        return "[" + new string('█', filled) + new string('░', len - filled) + "]";
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Console helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static void Col(ConsoleColor c) => System.Console.ForegroundColor = c;
    private static void Reset()             => System.Console.ResetColor();
    private static void Ln()               => System.Console.WriteLine();

    private static string FormatDate(string iso)
    {
        if (string.IsNullOrEmpty(iso)) return "unknown";
        return DateTime.TryParse(iso, out var dt)
               ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
               : iso;
    }
}
