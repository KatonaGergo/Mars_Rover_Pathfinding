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

// ── --verify mode ────────────────────────────────────────────────────────────
bool wantsVerify = Array.Exists(args, a => a == "--verify");
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
