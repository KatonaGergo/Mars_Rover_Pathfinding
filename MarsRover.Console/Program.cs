using MarsRover.Console;

// ── Usage ─────────────────────────────────────────────────────────────────────
//
//   dotnet run                                  → use config.json defaults
//   dotnet run -- --map path.csv                → override map only
//   dotnet run -- --episodes 1000               → override episodes only
//   dotnet run -- --map path.csv --episodes 200 --hours 24 --model mymodel
//   dotnet run -- --info                        → print saved model info, exit
//
// CLI flags always override config.json. Missing flags keep config.json values.
// config.json is created with defaults on first run if it does not exist.

// ── Special flags handled before config load ──────────────────────────────────
bool wantsHelp = Array.Exists(args, a => a is "--help" or "-h");
if (wantsHelp)
{
    PrintUsage();
    return 0;
}

// ── Load config ───────────────────────────────────────────────────────────────
var config = AppConfig.Load("config.json");

if (!config.ApplyArgs(args, out string argError))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"ERROR: {argError}");
    Console.ResetColor();
    PrintUsage();
    return 1;
}

if (!config.Validate(out string validationError))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"ERROR: {validationError}");
    Console.ResetColor();
    return 1;
}

// ── --info mode ───────────────────────────────────────────────────────────────
bool wantsInfo = Array.Exists(args, a => a == "--info");
if (wantsInfo)
{
    ConsoleRunner.PrintModelInfo(config.ModelPath);
    return 0;
}

// ── Boot screen ───────────────────────────────────────────────────────────────
TerminalUI.RunLoadingScreen();

// ── Main menu loop ────────────────────────────────────────────────────────────
// SystemStatus loops back to the menu; StartMission or Exit break out.
while (true)
{
    var choice = TerminalUI.RunMainMenu();

    if (choice == MainMenuResult.Exit)
        return 0;

    if (choice == MainMenuResult.SystemStatus)
    {
        Console.BackgroundColor = ConsoleColor.Black;
        Console.Clear();
        Console.CursorVisible = true;
        ConsoleRunner.PrintModelInfo(config.ModelPath);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("\n   Press any key to return to mission terminal...");
        Console.ResetColor();
        Console.ReadKey(intercept: true);
        continue;
    }

    break;  // StartMission → fall through to ConsoleRunner
}

// ── Mission run ───────────────────────────────────────────────────────────────
var runner = new ConsoleRunner(config);
runner.Run();
return 0;

// ── Helpers ───────────────────────────────────────────────────────────────────
static void PrintUsage()
{
    Console.WriteLine();
    Console.WriteLine("  USAGE:");
    Console.WriteLine("    dotnet run --project MarsRover.Console -- [options]");
    Console.WriteLine();
    Console.WriteLine("  OPTIONS:");
    Console.WriteLine("    --map      <path>    Path to map CSV            (default: mars_map_50x50.csv)");
    Console.WriteLine("    --hours    <int>     Mission duration in hours  (default: 24)");
    Console.WriteLine("    --episodes <int>     Training episode count     (default: 500)");
    Console.WriteLine("    --model    <name>    Model file base name       (default: model)");
    Console.WriteLine("    --info               Print saved model info and exit");
    Console.WriteLine("    --help               Print this message and exit");
    Console.WriteLine();
    Console.WriteLine("  CLI flags override config.json. config.json is created on first run.");
    Console.WriteLine();
}
