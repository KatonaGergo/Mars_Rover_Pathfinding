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
