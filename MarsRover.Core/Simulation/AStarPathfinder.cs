using MarsRover.Core.Models;

namespace MarsRover.Core.Simulation;

/// <summary>
/// 8-directional A* pathfinder for the Mars map.
///
/// UNIFORM MOVEMENT COST:
///   All moves (cardinal and diagonal) cost 1 movement point.
///   This matches the free-movement model: Fast=3 points/tick can be spent
///   in any combination of directions (e.g. North, North, East).
///
/// Heuristic: Chebyshev distance — exact for uniform 8-directional cost.
///   h(n) = max(|dx|, |dy|) = minimum steps needed in any direction guaranteeing optimal paths.
/// </summary>
public static class AStarPathfinder
{
    private const double StepCost = 1.0;

    private static readonly Direction[] Directions = Enum.GetValues<Direction>();
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
        var gScore   = new double[GameMap.Width, GameMap.Height];
        Fill(gScore, double.MaxValue);

        gScore[startX, startY] = 0;
        openSet.Enqueue(new Node(startX, startY, 0), Heuristic(startX, startY, goalX, goalY));

        while (openSet.Count > 0)
        {
            var current = openSet.Dequeue();
            if (current.G > gScore[current.X, current.Y]) continue; // stale queue entry

            if (current.X == goalX && current.Y == goalY)
                return ReconstructPath(cameFrom, goalX, goalY);

            foreach (Direction dir in Directions)
            {
                if (!TryStep(map, current.X, current.Y, dir, out int nx, out int ny))
                    continue;

                double tentativeG = current.G + StepCost;
                if (tentativeG < gScore[nx, ny])
                {
                    cameFrom[(nx, ny)] = (current.X, current.Y, dir);
                    gScore[nx, ny]     = tentativeG;
                    double f = tentativeG + Heuristic(nx, ny, goalX, goalY);
                    openSet.Enqueue(new Node(nx, ny, tentativeG), f);
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
        if (startX == goalX && startY == goalY) return 0;
        if (!map.IsPassable(startX, startY) || !map.IsPassable(goalX, goalY))
            return double.MaxValue;

        var openSet = new PriorityQueue<Node, double>();
        var gScore  = new double[GameMap.Width, GameMap.Height];
        Fill(gScore, double.MaxValue);

        gScore[startX, startY] = 0;
        openSet.Enqueue(new Node(startX, startY, 0), Heuristic(startX, startY, goalX, goalY));

        while (openSet.Count > 0)
        {
            var current = openSet.Dequeue();
            if (current.G > gScore[current.X, current.Y]) continue; // stale queue entry

            if (current.X == goalX && current.Y == goalY)
                return current.G;

            foreach (Direction dir in Directions)
            {
                if (!TryStep(map, current.X, current.Y, dir, out int nx, out int ny))
                    continue;

                double tentativeG = current.G + StepCost;
                if (tentativeG < gScore[nx, ny])
                {
                    gScore[nx, ny] = tentativeG;
                    double f = tentativeG + Heuristic(nx, ny, goalX, goalY);
                    openSet.Enqueue(new Node(nx, ny, tentativeG), f);
                }
            }
        }

        return double.MaxValue;
    }

    /// <summary>
    /// Returns exact shortest-path distances (in movement points) from one source
    /// tile to every passable tile. Unreachable cells are int.MaxValue.
    /// Uses uniform-cost BFS with the same 8-way corner-cutting rules as A*.
    /// </summary>
    public static int[,] BuildDistanceField(GameMap map, int startX, int startY)
    {
        var dist = new int[GameMap.Width, GameMap.Height];
        Fill(dist, int.MaxValue);

        if (!map.IsPassable(startX, startY))
            return dist;

        var queue = new Queue<(int x, int y)>();
        dist[startX, startY] = 0;
        queue.Enqueue((startX, startY));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            int nextDist = dist[current.x, current.y] + 1;

            foreach (Direction dir in Directions)
            {
                if (!TryStep(map, current.x, current.y, dir, out int nx, out int ny))
                    continue;

                if (nextDist < dist[nx, ny])
                {
                    dist[nx, ny] = nextDist;
                    queue.Enqueue((nx, ny));
                }
            }
        }

        return dist;
    }

    // ── Heuristic ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Chebyshev distance for 8-directional movement.
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

    private static bool TryStep(GameMap map, int x, int y, Direction dir, out int nx, out int ny)
    {
        var (dx, dy) = GameMap.DirectionDelta(dir);
        nx = x + dx;
        ny = y + dy;

        if (!map.IsPassable(nx, ny)) return false;

        if (!IsCardinal[(int)dir])
        {
            if (!map.IsPassable(x + dx, y) || !map.IsPassable(x, y + dy))
                return false;
        }

        return true;
    }

    private static void Fill(double[,] matrix, double value)
    {
        for (int x = 0; x < matrix.GetLength(0); x++)
            for (int y = 0; y < matrix.GetLength(1); y++)
                matrix[x, y] = value;
    }

    private static void Fill(int[,] matrix, int value)
    {
        for (int x = 0; x < matrix.GetLength(0); x++)
            for (int y = 0; y < matrix.GetLength(1); y++)
                matrix[x, y] = value;
    }

    private record struct Node(int X, int Y, double G);
}

/// <summary>One step in an A* path.</summary>
public record struct PathStep(int X, int Y, Direction Direction);
