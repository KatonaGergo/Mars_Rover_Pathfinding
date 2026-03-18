using MarsRover.Console;
using MarsRover.Core.Algorithm;
using MarsRover.Core.Simulation;
using System.Diagnostics;

// ── Usage ─────────────────────────────────────────────────────────────────────
//
//   dotnet run                                  → use config.json defaults
//   dotnet run -- --map path.csv                → override map only
//   dotnet run -- --episodes 1000               → override episodes only
//   dotnet run -- --map path.csv --episodes 200 --hours 24 --model mymodel
//   dotnet run -- --info                        → print saved model info, exit
//   dotnet run -- --verify                      → run exact verifier for configured hours
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

bool wantsInfo = Array.Exists(args, a => a == "--info");
bool wantsVerify = Array.Exists(args, a => a == "--verify");
bool wantsBenchmark = Array.Exists(args, a => a == "--benchmark");
bool wantsResetModel = Array.Exists(args, a => a == "--reset-model");
bool resetDelete = Array.Exists(args, a => a == "--reset-model-delete");

if (wantsResetModel)
{
    PrintResetModelResult(config.ModelPath, archive: !resetDelete);
}

// ── --info mode ───────────────────────────────────────────────────────────────
if (wantsInfo)
{
    ConsoleRunner.PrintModelInfo(config.ModelPath);
    return 0;
}

if (!config.Validate(out string validationError))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"ERROR: {validationError}");
    Console.ResetColor();
    return 1;
}

// ── --verify mode ────────────────────────────────────────────────────────────
if (wantsVerify)
{
    if (!TryReadIntArg(args, "--verify-timeout", 300, min: 1, max: 86_400, out int timeoutSec, out string timeoutErr))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {timeoutErr}");
        Console.ResetColor();
        return 1;
    }

    if (!TryReadIntArg(args, "--verify-lb", 0, min: 0, max: 390, out int lowerBound, out string lbErr))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {lbErr}");
        Console.ResetColor();
        return 1;
    }

    if (!TryReadOptionalIntArg(args, "--verify-target", min: 0, max: 390, out int? target, out string targetErr))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {targetErr}");
        Console.ResetColor();
        return 1;
    }

    if (!TryReadIntArg(args, "--verify-parallel", 1, min: 1, max: 64, out int verifyParallel, out string parallelErr))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {parallelErr}");
        Console.ResetColor();
        return 1;
    }

    if (!TryReadIntArg(args, "--verify-partitions", verifyParallel, min: 1, max: 512, out int verifyPartitions, out string partitionsErr))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {partitionsErr}");
        Console.ResetColor();
        return 1;
    }

    if (!TryReadIntArg(args, "--verify-transposition-cap", 300_000, min: 0, max: 5_000_000, out int transpositionCap, out string transErr))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {transErr}");
        Console.ResetColor();
        return 1;
    }

    bool adaptivePartitionProof = Array.Exists(args, a => a == "--verify-adaptive");
    bool useDominance = Array.Exists(args, a => a == "--verify-use-dominance");
    return RunVerifier(
        config,
        timeoutSec,
        lowerBound,
        target,
        useDominance,
        verifyParallel,
        verifyPartitions,
        adaptivePartitionProof,
        transpositionCap);
}

if (wantsBenchmark)
{
    return RunBenchmark(config, args);
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
try
{
    runner.Run();
    return 0;
}
catch (InvalidOperationException ex) when (IsSchemaMismatchError(ex))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine();
    Console.WriteLine("ERROR: Saved model is incompatible with current policy schema.");
    Console.WriteLine(ex.Message);
    Console.WriteLine();
    Console.WriteLine("Recovery:");
    Console.WriteLine($"  dotnet run --project MarsRover.Console -- --model {config.ModelPath} --reset-model");
    Console.WriteLine("Then run training again.");
    Console.ResetColor();
    return 2;
}

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
    Console.WriteLine("    --reset-model        Archive/delete current model files before continuing");
    Console.WriteLine("    --reset-model-delete Delete model files directly instead of archiving");
    Console.WriteLine("    --info               Print saved model info and exit");
    Console.WriteLine("    --benchmark          Run deterministic benchmark and save JSON/CSV to results/");
    Console.WriteLine("    --benchmark-preset <48h|100h>  Official benchmark preset");
    Console.WriteLine("    --benchmark-seeds <a,b,c>      Fixed seed list (default preset seeds)");
    Console.WriteLine("    --benchmark-episodes <int>     Episodes per benchmark seed");
    Console.WriteLine("    --benchmark-output <name>      Output base name (without extension)");
    Console.WriteLine("    --verify             Run exact branch-and-bound verifier");
    Console.WriteLine("    --verify-timeout <s> Verifier timeout in seconds (default: 300)");
    Console.WriteLine("    --verify-lb <int>    Initial lower bound to speed pruning (default: 0)");
    Console.WriteLine("    --verify-target <n>  Decision mode: check if >= n minerals is feasible");
    Console.WriteLine("    --verify-parallel <n>  Parallel worker count (target mode, proof-safe only)");
    Console.WriteLine("    --verify-partitions <n>  Total proof partitions (default: --verify-parallel)");
    Console.WriteLine("    --verify-adaptive  Multi-stage timeout escalation for unresolved partitions");
    Console.WriteLine("    --verify-transposition-cap <n>  Exact transposition table size per verifier (default: 300000)");
    Console.WriteLine("    --verify-use-dominance  Enable heuristic dominance pruning (faster, not proof-safe)");
    Console.WriteLine("    --help               Print this message and exit");
    Console.WriteLine();
    Console.WriteLine("  CLI flags override config.json. config.json is created on first run.");
    Console.WriteLine();
}

static int RunVerifier(
    AppConfig config,
    int timeoutSec,
    int lowerBound,
    int? targetMinerals,
    bool useDominance,
    int verifyParallel,
    int verifyPartitions,
    bool adaptivePartitionProof,
    int transpositionCap)
{
    GameMap map;
    try
    {
        map = GameMap.LoadFromFile(config.MapPath);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: Could not load map: {ex.Message}");
        Console.ResetColor();
        return 1;
    }

    int totalTicks = config.Hours * 2;

    if (verifyParallel > 1 && targetMinerals.HasValue && !useDominance)
    {
        if (adaptivePartitionProof)
        {
            return RunAdaptivePartitionedTargetVerifier(
                map,
                totalTicks,
                lowerBound,
                targetMinerals.Value,
                verifyParallel,
                verifyPartitions,
                transpositionCap,
                config);
        }

        return RunPartitionedTargetVerifier(
            map,
            totalTicks,
            timeoutSec,
            lowerBound,
            targetMinerals.Value,
            verifyParallel,
            verifyPartitions,
            transpositionCap,
            config);
    }

    if (verifyParallel > 1)
    {
        Console.WriteLine("Note: --verify-parallel is only used in target mode with proof-safe dominance OFF.");
    }

    var verifier = new OptimalMissionVerifier(
        map.Clone(),
        totalTicks,
        timeoutSeconds: timeoutSec,
        progressIntervalNodes: 200_000,
        useHeuristicDominance: useDominance,
        maxTranspositionEntries: transpositionCap);

    Console.WriteLine();
    Console.WriteLine("MARS ROVER OPTIMALITY VERIFIER");
    Console.WriteLine($"Map           : {config.MapPath}");
    Console.WriteLine($"Duration      : {config.Hours}h ({totalTicks} ticks)");
    Console.WriteLine($"Timeout       : {timeoutSec}s");
    Console.WriteLine($"Initial LB    : {lowerBound}");
    Console.WriteLine($"Mode          : {(targetMinerals.HasValue ? $"Target >= {targetMinerals.Value}" : "Maximize")}");
    Console.WriteLine($"Dominance     : {(useDominance ? "ON (heuristic)" : "OFF (proof-safe)")}");
    Console.WriteLine($"Transposition : {(useDominance ? "OFF (heuristic mode)" : $"{transpositionCap:N0} entries")}");
    Console.WriteLine();

    var result = verifier.Verify(
        initialLowerBound: lowerBound,
        targetMinerals: targetMinerals,
        onProgress: p =>
        {
            Console.Write(
                $"\rNodes: {p.NodesVisited:N0}  Best: {p.BestMinerals}  " +
                $"Pruned(U/D/T): {p.PrunedByUpperBound:N0}/{p.PrunedByDominance:N0}/{p.PrunedByTransposition:N0}  " +
                $"Elapsed: {p.ElapsedSeconds:F1}s    ");
        });

    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine("VERIFIER RESULT");
    Console.WriteLine($"Best minerals : {result.BestMinerals}");
    if (result.TargetMinerals.HasValue)
    {
        Console.WriteLine($"Target        : >= {result.TargetMinerals.Value}");
        Console.WriteLine($"Target reached: {(result.TargetReached ? "YES" : "NO")}");
        Console.WriteLine($"Target proven feasible: {(result.TargetProvenFeasible ? "YES" : "NO")}");
        Console.WriteLine($"Target proven infeasible: {(result.TargetProvenInfeasible ? "YES" : "NO")}");
    }
    else
    {
        Console.WriteLine($"Proven optimal: {(result.ProvenOptimal ? "YES" : "NO")}");
    }
    Console.WriteLine($"Proof-safe mode: {(result.UsedHeuristicDominance ? "NO" : "YES")}");
    Console.WriteLine($"Timed out     : {(result.TimedOut ? "YES" : "NO")}");
    Console.WriteLine($"Nodes visited : {result.NodesVisited:N0}");
    Console.WriteLine($"Pruned (UB)   : {result.PrunedByUpperBound:N0}");
    Console.WriteLine($"Pruned (DOM)  : {result.PrunedByDominance:N0}");
    Console.WriteLine($"Pruned (TR)   : {result.PrunedByTransposition:N0}");
    Console.WriteLine($"Elapsed       : {result.ElapsedSeconds:F2}s");

    if (result.BestRoute.Count > 0)
    {
        Console.WriteLine("Best route (mineral sequence):");
        Console.WriteLine(string.Join(" -> ", result.BestRoute.Select(p => $"({p.x},{p.y})")));
    }

    Console.WriteLine();
    if (result.TargetMinerals.HasValue)
    {
        if (result.TargetProvenInfeasible)
            Console.WriteLine($"No feasible route with >= {result.TargetMinerals.Value} minerals exists (proven).");
        else if (result.TargetReached)
            Console.WriteLine($"A feasible route with >= {result.TargetMinerals.Value} minerals exists.");
        else
            Console.WriteLine("Target result is inconclusive (timeout).");
    }
    else
    {
        Console.WriteLine(result.ProvenOptimal
            ? "Optimality is proven for this duration."
            : "Search did not finish proof within limits; best-found value shown.");
    }

    return 0;
}

static int RunPartitionedTargetVerifier(
    GameMap map,
    int totalTicks,
    int timeoutSec,
    int lowerBound,
    int targetMinerals,
    int requestedWorkers,
    int requestedPartitions,
    int transpositionCap,
    AppConfig config)
{
    int partitions = Math.Min(requestedPartitions, Math.Max(1, map.RemainingMinerals.Count));
    int workers = Math.Min(requestedWorkers, partitions);
    var results = new VerificationResult[partitions];
    var ranPartition = new bool[partitions];
    var sw = Stopwatch.StartNew();

    Console.WriteLine();
    Console.WriteLine("MARS ROVER OPTIMALITY VERIFIER (PARTITIONED TARGET MODE)");
    Console.WriteLine($"Map           : {config.MapPath}");
    Console.WriteLine($"Duration      : {config.Hours}h ({totalTicks} ticks)");
    Console.WriteLine($"Timeout       : {timeoutSec}s per partition");
    Console.WriteLine($"Initial LB    : {lowerBound}");
    Console.WriteLine($"Mode          : Target >= {targetMinerals}");
    Console.WriteLine($"Dominance     : OFF (proof-safe)");
    Console.WriteLine($"Transposition : {transpositionCap:N0} entries per partition");
    Console.WriteLine($"Workers       : {workers}");
    Console.WriteLine($"Partitions    : {partitions}");
    Console.WriteLine("Partition rule: first collected mineral index % partitions == partitionId");
    Console.WriteLine();

    bool targetReachedEarly = false;
    for (int batchStart = 0; batchStart < partitions && !targetReachedEarly; batchStart += workers)
    {
        int batchSize = Math.Min(workers, partitions - batchStart);
        var tasks = new Task[batchSize];

        for (int i = 0; i < batchSize; i++)
        {
            int partitionId = batchStart + i;
            tasks[i] = Task.Run(() =>
            {
                var verifier = new OptimalMissionVerifier(
                    map.Clone(),
                    totalTicks,
                    timeoutSeconds: timeoutSec,
                    progressIntervalNodes: 500_000,
                    useHeuristicDominance: false,
                    maxTranspositionEntries: transpositionCap,
                    firstMineralPartitions: partitions,
                    firstMineralRemainder: partitionId);

                results[partitionId] = verifier.Verify(
                    initialLowerBound: lowerBound,
                    targetMinerals: targetMinerals);
                ranPartition[partitionId] = true;
            });
        }

        Task.WaitAll(tasks);
        targetReachedEarly = results.Any(r => r.TargetReached);
    }

    sw.Stop();

    var ranResults = Enumerable.Range(0, partitions)
        .Where(i => ranPartition[i])
        .Select(i => results[i])
        .ToList();

    bool anyReached = ranResults.Any(r => r.TargetReached);
    bool allProvenInfeasible = !anyReached
        && ranResults.Count == partitions
        && ranResults.All(r => r.TargetProvenInfeasible);
    bool anyTimedOut = ranResults.Any(r => r.TimedOut);

    var best = ranResults
        .OrderByDescending(r => r.BestMinerals)
        .ThenBy(r => r.ElapsedSeconds)
        .First();

    long totalNodes = ranResults.Sum(r => r.NodesVisited);
    long totalPrunedUb = ranResults.Sum(r => r.PrunedByUpperBound);
    long totalPrunedDom = ranResults.Sum(r => r.PrunedByDominance);
    long totalPrunedTr = ranResults.Sum(r => r.PrunedByTransposition);
    int timedOutPartitions = ranResults.Count(r => r.TimedOut);

    Console.WriteLine("VERIFIER RESULT (AGGREGATED)");
    Console.WriteLine($"Best minerals : {best.BestMinerals}");
    Console.WriteLine($"Target        : >= {targetMinerals}");
    Console.WriteLine($"Target reached: {(anyReached ? "YES" : "NO")}");
    Console.WriteLine($"Target proven feasible: {(anyReached ? "YES" : "NO")}");
    Console.WriteLine($"Target proven infeasible: {(allProvenInfeasible ? "YES" : "NO")}");
    Console.WriteLine("Proof-safe mode: YES");
    Console.WriteLine($"Any timeout   : {(anyTimedOut ? "YES" : "NO")} ({timedOutPartitions}/{ranResults.Count})");
    Console.WriteLine($"Partitions run: {ranResults.Count}/{partitions}");
    Console.WriteLine($"Nodes visited : {totalNodes:N0}");
    Console.WriteLine($"Pruned (UB)   : {totalPrunedUb:N0}");
    Console.WriteLine($"Pruned (DOM)  : {totalPrunedDom:N0}");
    Console.WriteLine($"Pruned (TR)   : {totalPrunedTr:N0}");
    Console.WriteLine($"Elapsed       : {sw.Elapsed.TotalSeconds:F2}s");

    if (best.BestRoute.Count > 0)
    {
        Console.WriteLine("Best route (mineral sequence):");
        Console.WriteLine(string.Join(" -> ", best.BestRoute.Select(p => $"({p.x},{p.y})")));
    }

    Console.WriteLine();
    if (allProvenInfeasible)
        Console.WriteLine($"No feasible route with >= {targetMinerals} minerals exists (proven across all partitions).");
    else if (anyReached)
        Console.WriteLine($"A feasible route with >= {targetMinerals} minerals exists.");
    else
        Console.WriteLine("Target result is inconclusive (at least one partition timed out).");

    return 0;
}

static int RunAdaptivePartitionedTargetVerifier(
    GameMap map,
    int totalTicks,
    int lowerBound,
    int targetMinerals,
    int requestedWorkers,
    int requestedPartitions,
    int transpositionCap,
    AppConfig config)
{
    int partitions = Math.Min(requestedPartitions, Math.Max(1, map.RemainingMinerals.Count));
    int workers = Math.Min(requestedWorkers, partitions);
    int[] stages = [30, 120, 300, 900, 1800];
    var unresolved = Enumerable.Range(0, partitions).ToList();
    var latest = new Dictionary<int, VerificationResult>(partitions);
    var wall = Stopwatch.StartNew();

    long totalNodes = 0;
    long totalPrunedUb = 0;
    long totalPrunedDom = 0;
    long totalPrunedTr = 0;

    Console.WriteLine();
    Console.WriteLine("MARS ROVER OPTIMALITY VERIFIER (ADAPTIVE PARTITIONED TARGET MODE)");
    Console.WriteLine($"Map           : {config.MapPath}");
    Console.WriteLine($"Duration      : {config.Hours}h ({totalTicks} ticks)");
    Console.WriteLine($"Initial LB    : {lowerBound}");
    Console.WriteLine($"Mode          : Target >= {targetMinerals}");
    Console.WriteLine("Dominance     : OFF (proof-safe)");
    Console.WriteLine($"Transposition : {transpositionCap:N0} entries per partition");
    Console.WriteLine($"Workers       : {workers}");
    Console.WriteLine($"Partitions    : {partitions}");
    Console.WriteLine($"Stages (sec)  : {string.Join(", ", stages)}");
    Console.WriteLine("Partition rule: first collected mineral index % partitions == partitionId");
    Console.WriteLine();

    bool anyReached = false;

    foreach (int stageTimeout in stages)
    {
        if (unresolved.Count == 0 || anyReached) break;

        Console.WriteLine($"Stage timeout : {stageTimeout}s");
        Console.WriteLine($"Unresolved    : {unresolved.Count}");

        var stageResults = ExecutePartitionSet(
            map,
            totalTicks,
            stageTimeout,
            lowerBound,
            targetMinerals,
            workers,
            partitions,
            transpositionCap,
            unresolved);

        var nextUnresolved = new List<int>(stageResults.Count);
        foreach (var entry in stageResults)
        {
            latest[entry.PartitionId] = entry.Result;

            totalNodes += entry.Result.NodesVisited;
            totalPrunedUb += entry.Result.PrunedByUpperBound;
            totalPrunedDom += entry.Result.PrunedByDominance;
            totalPrunedTr += entry.Result.PrunedByTransposition;

            if (entry.Result.TargetReached)
                anyReached = true;
            if (entry.Result.TimedOut)
                nextUnresolved.Add(entry.PartitionId);
        }

        unresolved = nextUnresolved;
        Console.WriteLine($"Stage done    : {stageResults.Count - unresolved.Count} settled, {unresolved.Count} still unresolved");
        Console.WriteLine();
    }

    wall.Stop();

    int ranPartitions = latest.Count;
    int timedOutPartitions = unresolved.Count;
    bool allProvenInfeasible = !anyReached
        && ranPartitions == partitions
        && timedOutPartitions == 0
        && latest.Values.All(r => r.TargetProvenInfeasible);

    VerificationResult best = latest.Values
        .OrderByDescending(r => r.BestMinerals)
        .ThenBy(r => r.ElapsedSeconds)
        .First();

    Console.WriteLine("VERIFIER RESULT (ADAPTIVE AGGREGATED)");
    Console.WriteLine($"Best minerals : {best.BestMinerals}");
    Console.WriteLine($"Target        : >= {targetMinerals}");
    Console.WriteLine($"Target reached: {(anyReached ? "YES" : "NO")}");
    Console.WriteLine($"Target proven feasible: {(anyReached ? "YES" : "NO")}");
    Console.WriteLine($"Target proven infeasible: {(allProvenInfeasible ? "YES" : "NO")}");
    Console.WriteLine("Proof-safe mode: YES");
    Console.WriteLine($"Any timeout   : {(timedOutPartitions > 0 ? "YES" : "NO")} ({timedOutPartitions}/{partitions})");
    Console.WriteLine($"Partitions run: {ranPartitions}/{partitions}");
    Console.WriteLine($"Nodes visited : {totalNodes:N0}");
    Console.WriteLine($"Pruned (UB)   : {totalPrunedUb:N0}");
    Console.WriteLine($"Pruned (DOM)  : {totalPrunedDom:N0}");
    Console.WriteLine($"Pruned (TR)   : {totalPrunedTr:N0}");
    Console.WriteLine($"Elapsed       : {wall.Elapsed.TotalSeconds:F2}s");

    if (best.BestRoute.Count > 0)
    {
        Console.WriteLine("Best route (mineral sequence):");
        Console.WriteLine(string.Join(" -> ", best.BestRoute.Select(p => $"({p.x},{p.y})")));
    }

    if (timedOutPartitions > 0)
    {
        string ids = string.Join(", ", unresolved.Take(80));
        if (unresolved.Count > 80) ids += ", ...";
        Console.WriteLine("Unresolved partition ids:");
        Console.WriteLine(ids);
    }

    Console.WriteLine();
    if (allProvenInfeasible)
        Console.WriteLine($"No feasible route with >= {targetMinerals} minerals exists (proven across all partitions).");
    else if (anyReached)
        Console.WriteLine($"A feasible route with >= {targetMinerals} minerals exists.");
    else
        Console.WriteLine("Target result is inconclusive (some partitions remained unresolved after all stages).");

    return 0;
}

static List<(int PartitionId, VerificationResult Result)> ExecutePartitionSet(
    GameMap map,
    int totalTicks,
    int timeoutSec,
    int lowerBound,
    int targetMinerals,
    int workers,
    int totalPartitions,
    int transpositionCap,
    IReadOnlyList<int> partitionIds)
{
    int[] ids = partitionIds.ToArray();
    var results = new VerificationResult[ids.Length];

    for (int batchStart = 0; batchStart < ids.Length; batchStart += workers)
    {
        int batchSize = Math.Min(workers, ids.Length - batchStart);
        var tasks = new Task[batchSize];

        for (int i = 0; i < batchSize; i++)
        {
            int localIndex = batchStart + i;
            int partitionId = ids[localIndex];
            tasks[i] = Task.Run(() =>
            {
                var verifier = new OptimalMissionVerifier(
                    map.Clone(),
                    totalTicks,
                    timeoutSeconds: timeoutSec,
                    progressIntervalNodes: 500_000,
                    useHeuristicDominance: false,
                    maxTranspositionEntries: transpositionCap,
                    firstMineralPartitions: totalPartitions,
                    firstMineralRemainder: partitionId);

                results[localIndex] = verifier.Verify(
                    initialLowerBound: lowerBound,
                    targetMinerals: targetMinerals);
            });
        }

        Task.WaitAll(tasks);
    }

    var list = new List<(int PartitionId, VerificationResult Result)>(ids.Length);
    for (int i = 0; i < ids.Length; i++)
        list.Add((ids[i], results[i]));
    return list;
}

static bool TryReadIntArg(
    string[] args,
    string flag,
    int defaultValue,
    int min,
    int max,
    out int value,
    out string error)
{
    value = defaultValue;
    error = string.Empty;

    int i = Array.IndexOf(args, flag);
    if (i < 0) return true;

    if (i + 1 >= args.Length)
    {
        error = $"{flag} requires a value.";
        return false;
    }

    if (!int.TryParse(args[i + 1], out int parsed) || parsed < min || parsed > max)
    {
        error = $"{flag} must be an integer in [{min}, {max}] (got '{args[i + 1]}').";
        return false;
    }

    value = parsed;
    return true;
}

static bool TryReadOptionalIntArg(
    string[] args,
    string flag,
    int min,
    int max,
    out int? value,
    out string error)
{
    value = null;
    error = string.Empty;

    int i = Array.IndexOf(args, flag);
    if (i < 0) return true;
    if (i + 1 >= args.Length)
    {
        error = $"{flag} requires a value.";
        return false;
    }

    if (!int.TryParse(args[i + 1], out int parsed) || parsed < min || parsed > max)
    {
        error = $"{flag} must be an integer in [{min}, {max}] (got '{args[i + 1]}').";
        return false;
    }

    value = parsed;
    return true;
}

static int RunBenchmark(AppConfig config, string[] args)
{
    string? preset = GetArg(args, "--benchmark-preset");
    int hours = config.Hours;
    string presetLabel = $"{hours}h";
    int[] seeds = [101, 202, 303];

    if (!string.IsNullOrWhiteSpace(preset))
    {
        switch (preset.Trim().ToLowerInvariant())
        {
            case "48h":
                hours = 48;
                presetLabel = "official48h";
                seeds = [4_801, 4_802, 4_803];
                break;
            case "100h":
                hours = 100;
                presetLabel = "official100h";
                seeds = [10_001, 10_002, 10_003];
                break;
            default:
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR: Unsupported --benchmark-preset '{preset}'. Use 48h or 100h.");
                Console.ResetColor();
                return 1;
        }
    }

    if (!TryReadIntArg(
            args,
            "--benchmark-episodes",
            config.Episodes,
            min: 1,
            max: 100_000,
            out int episodes,
            out string episodesErr))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {episodesErr}");
        Console.ResetColor();
        return 1;
    }

    string? seedArg = GetArg(args, "--benchmark-seeds");
    if (!string.IsNullOrWhiteSpace(seedArg)
        && !TryParseSeedList(seedArg, out seeds, out string seedErr))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {seedErr}");
        Console.ResetColor();
        return 1;
    }

    string outputBase = GetArg(args, "--benchmark-output")
        ?? $"benchmark-{presetLabel}-{DateTime.Now:yyyyMMdd_HHmmss}";

    GameMap map;
    try
    {
        map = GameMap.LoadFromFile(config.MapPath);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: Could not load map: {ex.Message}");
        Console.ResetColor();
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine("MARS ROVER DETERMINISTIC BENCHMARK");
    Console.WriteLine($"Map         : {config.MapPath}");
    Console.WriteLine($"Duration    : {hours}h");
    Console.WriteLine($"Episodes    : {episodes}");
    Console.WriteLine($"Seeds       : {string.Join(", ", seeds)}");
    Console.WriteLine($"Output base : results/{outputBase}.[json|csv]");
    Console.WriteLine();

    var perRun = new List<BenchmarkRunResult>(seeds.Length);

    foreach (int seed in seeds)
    {
        string benchModelPath = $"{config.ModelPath}_bench_{hours}h_seed{seed}";
        SimulationRunner.ResetSavedModel(benchModelPath, archive: false);

        var runner = new SimulationRunner(map.Clone(), hours, benchModelPath);
        var options = TrainingProfileFactory.CreateOptions(TrainingProfileFactory.DefaultProfile) with
        {
            EpisodeSeedOffset = seed * 10_000,
            ProfileName = $"BenchmarkSeed{seed}",
            Curriculum = new CurriculumOptions(false),
            SeedSweep = new SeedSweepOptions(false)
        };

        try
        {
            var (_, log) = runner.TrainAndRun(
                episodes,
                onProgress: null,
                options: options);

            if (log.Count == 0)
            {
                perRun.Add(new BenchmarkRunResult(
                    Seed: seed,
                    Minerals: 0,
                    ReturnedHome: false,
                    TicksUsed: 0,
                    BatteryAtEnd: 0.0));
                Console.WriteLine($"seed {seed}: no log entries");
            }
            else
            {
                var final = log[^1];
                bool returnedHome = final.X == map.StartX && final.Y == map.StartY;
                perRun.Add(new BenchmarkRunResult(
                    Seed: seed,
                    Minerals: final.TotalMinerals,
                    ReturnedHome: returnedHome,
                    TicksUsed: final.Tick,
                    BatteryAtEnd: final.Battery));

                Console.WriteLine(
                    $"seed {seed}: minerals={final.TotalMinerals} " +
                    $"returnedHome={(returnedHome ? "yes" : "no")} " +
                    $"ticks={final.Tick} batteryEnd={final.Battery:F1}%");
            }
        }
        catch (InvalidOperationException ex) when (IsSchemaMismatchError(ex))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"seed {seed}: schema mismatch: {ex.Message}");
            Console.ResetColor();
            return 2;
        }
        finally
        {
            SimulationRunner.ResetSavedModel(benchModelPath, archive: false);
        }
    }

    var summary = Benchmarking.BuildSummary(
        mapPath: config.MapPath,
        hours: hours,
        episodes: episodes,
        modelPath: config.ModelPath,
        seeds: seeds,
        runs: perRun);

    var (jsonPath, csvPath) = Benchmarking.SaveSummary(summary, outputBase);

    Console.WriteLine();
    Console.WriteLine("BENCHMARK SUMMARY");
    Console.WriteLine($"minerals mean/median/std : {summary.MineralsMean:F2} / {summary.MineralsMedian:F2} / {summary.MineralsStd:F2}");
    Console.WriteLine($"return-home rate         : {summary.ReturnHomeRate:P1}");
    Console.WriteLine($"ticks-used median        : {summary.TicksUsedMedian:F2}");
    Console.WriteLine($"battery-end median       : {summary.BatteryAtEndMedian:F2}%");
    Console.WriteLine($"saved JSON               : {jsonPath}");
    Console.WriteLine($"saved CSV                : {csvPath}");
    Console.WriteLine();

    return 0;
}

static bool TryParseSeedList(string raw, out int[] seeds, out string error)
{
    error = string.Empty;
    seeds = Array.Empty<int>();

    var parts = raw.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
    {
        error = "--benchmark-seeds requires at least one integer seed.";
        return false;
    }

    var parsed = new List<int>(parts.Length);
    foreach (string part in parts)
    {
        if (!int.TryParse(part.Trim(), out int seed))
        {
            error = $"Invalid seed '{part}' in --benchmark-seeds.";
            return false;
        }
        parsed.Add(seed);
    }

    seeds = parsed.Distinct().ToArray();
    return true;
}

static string? GetArg(string[] args, string flag)
{
    int i = Array.IndexOf(args, flag);
    if (i < 0 || i + 1 >= args.Length) return null;
    return args[i + 1];
}

static bool IsSchemaMismatchError(InvalidOperationException ex)
    => ex.Message.Contains("schema mismatch", StringComparison.OrdinalIgnoreCase)
       || ex.Message.Contains("Retraining is required", StringComparison.OrdinalIgnoreCase);

static void PrintResetModelResult(string modelPath, bool archive)
{
    var result = SimulationRunner.ResetSavedModel(modelPath, archive);

    Console.WriteLine();
    Console.WriteLine("MODEL RESET");
    if (!result.HadModel)
    {
        Console.WriteLine($"No saved model found at {SimulationRunner.ResolveModelPath(modelPath)}.qtable.json");
        Console.WriteLine();
        return;
    }

    if (result.Archived)
    {
        Console.WriteLine("Saved model archived.");
        Console.WriteLine($"Archive directory: {result.ArchiveDirectory}");
    }
    else
    {
        Console.WriteLine("Saved model deleted.");
    }

    foreach (string file in result.ProcessedFiles)
        Console.WriteLine($"  - {file}");
    Console.WriteLine();
}
