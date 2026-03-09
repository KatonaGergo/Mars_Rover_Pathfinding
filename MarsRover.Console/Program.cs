using MarsRover.Console;

// ── Argument parsing ──────────────────────────────────────────────────────────
// Usage:
//   dotnet run -- --map <path.csv> [--hours 24] [--episodes 500] [--model model]
//
// Defaults:
//   --hours    24      (one Martian sol)
//   --episodes 500
//   --model    model   (base name — saves model.qtable.json + model.meta.json)

string  mapPath   = GetArg(args, "--map",      "Map/mars_map_50x50.csv");
int     hours     = int.Parse(GetArg(args, "--hours",    "24"));
int     episodes  = int.Parse(GetArg(args, "--episodes", "500"));
string  modelPath = GetArg(args, "--model",    "model");

if (!File.Exists(mapPath))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"ERROR: Map file not found: {mapPath}");
    Console.WriteLine("Usage: dotnet run -- --map <path.csv> [--hours 24] [--episodes 500] [--model model]");
    Console.ResetColor();
    return 1;
}

var runner = new ConsoleRunner(mapPath, hours, episodes, modelPath);
runner.Run();
return 0;

// ── Helpers ───────────────────────────────────────────────────────────────────
static string GetArg(string[] args, string flag, string defaultVal)
{
    int i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : defaultVal;
}
