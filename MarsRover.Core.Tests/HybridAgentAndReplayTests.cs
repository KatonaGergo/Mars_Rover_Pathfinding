using System.Reflection;
using MarsRover.Core.Algorithm;
using MarsRover.Core.Models;
using MarsRover.Core.Simulation;
using Xunit;

namespace MarsRover.Core.Tests;

public class HybridAgentAndReplayTests
{
    [Theory]
    [InlineData(95, true)]  // margin = 0 -> must return now
    [InlineData(94, false)] // margin = 1 -> still safe
    [InlineData(90, false)] // margin = 5 -> safe
    public void MustReturnNow_Boundaries_AreCorrect(int tick, bool expected)
    {
        var map = CreateMap(startX: 10, startY: 10);
        var agent = new HybridAgent(map, totalTicks: 96, existingTable: null, seed: 1);
        var state = BuildState(10, 10, battery: 100, tick: tick);

        var method = typeof(HybridAgent).GetMethod(
            "MustReturnNow",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        bool actual = (bool)method!.Invoke(agent, [state, map])!;
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(10, 18, 10, 70.0)] // day-start return leg
    [InlineData(40, 18, 10, 70.0)] // night-start return leg
    public void ReturnLegParity_RuntimeMatchesAggressiveSimulator(
        int startTick, int startX, int startY, double startBattery)
    {
        var map = CreateMap(startX: 10, startY: 10);
        var agent = new HybridAgent(map, totalTicks: 200, existingTable: null, seed: 1);
        ForceReturningPhase(agent);

        int stepsHome = Math.Abs(startX - map.StartX) + Math.Abs(startY - map.StartY);
        var simMethod = typeof(HybridAgent).GetMethod(
            "SimulateReturnLegAggressive",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(simMethod);
        object? simPlan = simMethod!.Invoke(agent, [startTick, stepsHome, startBattery]);
        Assert.NotNull(simPlan);

        bool expectedFeasible = (bool)simPlan!.GetType().GetProperty("IsFeasible")!.GetValue(simPlan)!;
        int expectedTicks = (int)simPlan.GetType().GetProperty("Ticks")!.GetValue(simPlan)!;
        double expectedFinalBattery = (double)simPlan.GetType().GetProperty("FinalBattery")!.GetValue(simPlan)!;

        int x = startX;
        int y = startY;
        int tick = startTick;
        int ticksUsed = 0;
        double battery = startBattery;
        bool feasible = true;

        while (x != map.StartX || y != map.StartY)
        {
            var state = BuildState(x, y, battery, tick);
            var speed = agent.ChooseSpeed(state, map);
            bool isDay = SimulationEngine.IsDay(tick);
            double nextBattery = EnergyCalculator.Apply(
                battery, RoverActionType.Move, speed, isDay);
            if (nextBattery < 3.0)
            {
                feasible = false;
                break;
            }

            int moveBudget = (int)speed;
            while (moveBudget-- > 0 && (x != map.StartX || y != map.StartY))
            {
                if (x > map.StartX) x--;
                else if (x < map.StartX) x++;
                else if (y > map.StartY) y--;
                else if (y < map.StartY) y++;
            }

            battery = nextBattery;
            tick++;
            ticksUsed++;
        }

        Assert.Equal(expectedFeasible, feasible);
        if (!expectedFeasible) return;

        Assert.Equal(expectedTicks, ticksUsed);
        Assert.Equal(expectedFinalBattery, battery, precision: 8);
    }

    [Theory]
    [InlineData(95, 0)] // <=0
    [InlineData(94, 1)] // 1-2
    [InlineData(91, 2)] // 3-5
    [InlineData(80, 3)] // 6+
    public void ReturnMarginBucket_IsComputedAsExpected(int tick, int expectedBucket)
    {
        var map = CreateMap(startX: 10, startY: 10);
        var agent = new HybridAgent(map, totalTicks: 96, existingTable: null, seed: 1);
        var state = BuildState(10, 10, battery: 100, tick: tick);

        var q = agent.BuildQLearningState(state, map);
        Assert.Equal(expectedBucket, q.ReturnMarginBucket);
    }

    [Fact]
    public void ContinueUntilDeadline_DoesNotStopAtFirstReturn()
    {
        var map = CreateMap(startX: 10, startY: 10, minerals: [(11, 10, 'B')]);
        var runner = new SimulationRunner(map, durationHours: 24, modelPath: "test-runtime");

        var log = runner.RunSimulation(missionEndMode: MissionEndMode.ContinueUntilDeadline);

        Assert.NotEmpty(log);
        Assert.Equal(48, log[^1].Tick);
    }

    [Fact]
    public void ReplayDiversity_StrataAppearInBatch()
    {
        var per = new PrioritizedReplayBuffer(capacity: 200, seed: 7);
        string[] keys =
        [
            "0,0,1,0,2,0,1",
            "1,1,2,1,2,1,1",
            "2,2,2,1,2,2,1",
            "3,0,2,1,2,3,1"
        ];

        for (int i = 0; i < 120; i++)
        {
            string k = keys[i % keys.Length];
            per.Push(k, actionIdx: i % 6, reward: 1.0, nextStateKey: k, isTerminal: false, initialPriority: 1.0 + (i % 5));
        }

        var (batch, _, _) = per.SampleWithDiversity(batchSize: 40, beta: 0.4, stratifiedFraction: 0.5);
        var groups = batch
            .Select(t => t.StateKey)
            .Select(StateGroup)
            .Distinct()
            .Count();

        Assert.True(groups >= 3);
    }

    [Fact]
    public void ReplayDiversityActivation_ReflectsEnabledFlag()
    {
        var on = new TrainingOptions(
            ReplayDiversity: new ReplayDiversityOptions(Enabled: true, StratifiedFraction: 0.4));
        var off = new TrainingOptions(
            ReplayDiversity: new ReplayDiversityOptions(Enabled: false, StratifiedFraction: 0.4));

        Assert.True(SimulationRunner.ShouldUseDiversityReplay(on));
        Assert.False(SimulationRunner.ShouldUseDiversityReplay(off));
    }

    [Fact]
    public void SchemaMismatchResetFlow_ThrowsThenResetsModel()
    {
        string modelName = $"schema-reset-{Guid.NewGuid():N}";
        string resolved = SimulationRunner.ResolveModelPath(modelName);
        string qtablePath = resolved + ".qtable.json";
        string metaPath = resolved + ".meta.json";
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);

        try
        {
            new QTable().Save(qtablePath);
            File.WriteAllText(metaPath,
                "{\"EpisodesCompleted\":1,\"BestMinerals\":1,\"SavedAt\":\"2026-01-01T00:00:00Z\",\"PolicySchemaVersion\":1,\"TrainingProfile\":\"legacy\"}");

            var map = CreateMap(startX: 10, startY: 10, minerals: [(11, 10, 'B')]);
            var runner = new SimulationRunner(map, durationHours: 24, modelPath: modelName);

            var ex = Assert.Throws<InvalidOperationException>(() => runner.RunSimulation());
            Assert.Contains("schema mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);

            var reset = SimulationRunner.ResetSavedModel(modelName, archive: false);
            Assert.True(reset.HadModel);
            Assert.False(File.Exists(qtablePath));
            Assert.False(File.Exists(metaPath));
        }
        finally
        {
            if (File.Exists(qtablePath)) File.Delete(qtablePath);
            if (File.Exists(metaPath)) File.Delete(metaPath);
        }
    }

    [Fact]
    public void Benchmarking_BuildSummary_IsDeterministic()
    {
        int[] seeds = [101, 202, 303];
        BenchmarkRunResult[] runs =
        [
            new(101, 42, true, 95, 78.0),
            new(202, 44, true, 94, 76.0),
            new(303, 43, true, 96, 77.0)
        ];

        var s1 = Benchmarking.BuildSummary("Map/mars_map_50x50.csv", 48, 500, "model", seeds, runs);
        var s2 = Benchmarking.BuildSummary("Map/mars_map_50x50.csv", 48, 500, "model", seeds, runs);

        Assert.Equal(s1.MineralsMean, s2.MineralsMean, 8);
        Assert.Equal(s1.MineralsMedian, s2.MineralsMedian, 8);
        Assert.Equal(s1.MineralsStd, s2.MineralsStd, 8);
        Assert.Equal(s1.ReturnHomeRate, s2.ReturnHomeRate, 8);
        Assert.Equal(s1.TicksUsedMedian, s2.TicksUsedMedian, 8);
        Assert.Equal(s1.BatteryAtEndMedian, s2.BatteryAtEndMedian, 8);
    }

    [Fact]
    public void Benchmarking_SaveSummary_WritesExpectedFiles()
    {
        int[] seeds = [11, 22, 33];
        BenchmarkRunResult[] runs =
        [
            new(11, 41, true, 94, 70.0),
            new(22, 42, true, 95, 72.0),
            new(33, 43, false, 96, 65.0)
        ];

        var summary = Benchmarking.BuildSummary(
            mapPath: "Map/mars_map_50x50.csv",
            hours: 48,
            episodes: 300,
            modelPath: "model",
            seeds: seeds,
            runs: runs);

        string dir = Path.Combine(Path.GetTempPath(), "mars-rover-bench-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var (jsonPath, csvPath) = Benchmarking.SaveSummary(summary, "bench-shape-test", dir);

            Assert.True(File.Exists(jsonPath));
            Assert.True(File.Exists(csvPath));

            string json = File.ReadAllText(jsonPath);
            string csv = File.ReadAllText(csvPath);
            Assert.Contains("\"Runs\": 3", json);
            Assert.Contains("metric,value", csv);
            Assert.Contains("seed,minerals,returnedHome,ticksUsed,batteryAtEnd", csv);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static string StateGroup(string stateKey)
    {
        var parts = stateKey.Split(',');
        string battery = parts[0];
        string sol = parts[1];
        string margin = parts.Length > 5 ? parts[5] : "0";
        return $"{sol}:{battery}:{margin}";
    }

    private static RoverState BuildState(int x, int y, double battery, int tick)
        => new(
            X: x,
            Y: y,
            Battery: battery,
            Tick: tick,
            MineralsB: 0,
            MineralsY: 0,
            MineralsG: 0,
            IsMining: false,
            LastAction: RoverAction.StandbyAction);

    private static void ForceReturningPhase(HybridAgent agent)
    {
        var field = typeof(HybridAgent).GetField(
            "_phase",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(agent, MissionPhase.Returning);
    }

    private static GameMap CreateMap(
        int startX,
        int startY,
        IEnumerable<(int x, int y, char c)>? minerals = null)
    {
        var rows = new string[GameMap.Height];
        for (int y = 0; y < GameMap.Height; y++)
            rows[y] = new string('.', GameMap.Width);

        char[][] grid = rows.Select(r => r.ToCharArray()).ToArray();
        grid[startY][startX] = 'S';
        if (minerals != null)
        {
            foreach (var (x, y, c) in minerals)
                grid[y][x] = c;
        }

        string[] lines = grid.Select(r => new string(r)).ToArray();
        return GameMap.Parse(lines);
    }
}
