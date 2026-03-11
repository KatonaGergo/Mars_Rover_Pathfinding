using MarsRover.Core.Models;

namespace MarsRover.Core.Simulation;

/// <summary>
/// 8-directional A* pathfinder for the Mars map.
///
/// UNIFORM MOVEMENT COST:
///   All moves (cardinal and diagonal) cost 1 movement point.
///   This matches the free-movement model: Fast=3 points/tick can be spent
///   in any combination of directions (e.g. North, North, East).
///   Diagonal steps are not penalised — they cost the same as cardinal steps.
///
/// Heuristic: Chebyshev distance — exact for uniform 8-directional cost.
///   h(n) = max(|dx|, |dy|) = minimum steps needed in any direction
///   This is both admissible and consistent, guaranteeing optimal paths.
/// </summary>
public static class AStarPathfinder
{
    private const double StepCost = 1.0;

    private static readonly bool[] IsCardinal = [true, false, true, false, true, false, true, false];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the shortest path from (startX,startY) to (goalX,goalY).
    /// Returns an ordered list of (x, y, direction) steps, or empty list if no path exists.
    /// </summary>
    public static List<PathStep> FindPath(GameMap map, int startX, int startY, int goalX, int goalY)
    {
        if (startX == goalX && startY == goalY)
            return [];

        var openSet  = new PriorityQueue<Node, double>();
        var cameFrom = new Dictionary<(int, int), (int px, int py, Direction dir)>();
        var gScore   = new Dictionary<(int, int), double>();

        var startKey = (startX, startY);
        gScore[startKey] = 0;
        openSet.Enqueue(new Node(startX, startY), Heuristic(startX, startY, goalX, goalY));

        while (openSet.Count > 0)
        {
            var current = openSet.Dequeue();

            if (current.X == goalX && current.Y == goalY)
                return ReconstructPath(cameFrom, goalX, goalY);

            foreach (Direction dir in Enum.GetValues<Direction>())
            {
                var (dx, dy) = GameMap.DirectionDelta(dir);
                int nx = current.X + dx;
                int ny = current.Y + dy;

                if (!map.IsPassable(nx, ny)) continue;

                if (!IsCardinal[(int)dir])
                {
                    if (!map.IsPassable(current.X + dx, current.Y) ||
                        !map.IsPassable(current.X, current.Y + dy))
                        continue;
                }

                double stepCost   = StepCost; // uniform, all directions cost 1
                double tentativeG = gScore.GetValueOrDefault((current.X, current.Y), double.MaxValue)
                                    + stepCost;

                var neighborKey = (nx, ny);
                if (tentativeG < gScore.GetValueOrDefault(neighborKey, double.MaxValue))
                {
                    cameFrom[neighborKey] = (current.X, current.Y, dir);
                    gScore[neighborKey]   = tentativeG;
                    double f = tentativeG + Heuristic(nx, ny, goalX, goalY);
                    openSet.Enqueue(new Node(nx, ny), f);
                }
            }
        }

        return []; // If no path found
    }

    /// <summary>
    /// Returns the actual path cost (in step units) from start to goal,
    /// without reconstructing the full step list. Used for reward shaping.
    /// Returns double.MaxValue if unreachable.
    /// </summary>
    public static double PathCost(GameMap map, int startX, int startY, int goalX, int goalY)
    {
        var path = FindPath(map, startX, startY, goalX, goalY);
        if (path.Count == 0 && !(startX == goalX && startY == goalY))
            return double.MaxValue;

        return path.Count; // uniform cost — path length = number of movement points
    }

    // ── Heuristic ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Chebyshev distance — admissible heuristic for 8-directional movement.
    /// h = max(|Δx|, |Δy|)
    /// </summary>
    public static double Heuristic(int x1, int y1, int x2, int y2)
        => Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));

    // ── Path reconstruction ───────────────────────────────────────────────────

    private static List<PathStep> ReconstructPath(
        Dictionary<(int, int), (int px, int py, Direction dir)> cameFrom,
        int goalX, int goalY)
    {
        var steps = new List<PathStep>();
        var key   = (goalX, goalY);

        while (cameFrom.TryGetValue(key, out var prev))
        {
            steps.Add(new PathStep(key.Item1, key.Item2, prev.dir));
            key = (prev.px, prev.py);
        }

        steps.Reverse();
        return steps;
    }

    // ── Private types ─────────────────────────────────────────────────────────

    private record struct Node(int X, int Y);
}

/// <summary>One step in an A* path.</summary>
public record struct PathStep(int X, int Y, Direction Direction);
