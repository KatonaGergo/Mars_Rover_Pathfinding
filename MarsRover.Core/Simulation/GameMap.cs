using MarsRover.Core.Models;

namespace MarsRover.Core.Simulation;

/// <summary>
/// Parses and stores the 50×50 Mars map.
/// Provides obstacle checking, neighbor lookup, mineral tracking, and cloning.
/// </summary>
public class GameMap
{
    public const int Width  = 50;
    public const int Height = 50;

    private readonly TileType[,]          _tiles;
    private readonly HashSet<(int x, int y)>  _originalMinerals; // never mutated — used for Clone()
    private readonly HashSet<(int x, int y)>  _remainingMinerals; // mutated as minerals are collected

    public int StartX { get; private set; }
    public int StartY { get; private set; }

    public IReadOnlySet<(int x, int y)> RemainingMinerals => _remainingMinerals;

    // Direction deltas for all 8 compass directions (matches Direction enum order)
    private static readonly (int dx, int dy)[] DirectionDeltas =
    [
        ( 0, -1),  // North
        ( 1, -1),  // NorthEast
        ( 1,  0),  // East
        ( 1,  1),  // SouthEast
        ( 0,  1),  // South
        (-1,  1),  // SouthWest
        (-1,  0),  // West
        (-1, -1),  // NorthWest
    ];

    private GameMap(TileType[,] tiles, int startX, int startY,
                    HashSet<(int x, int y)> originalMinerals, HashSet<(int x, int y)>? remainingMinerals = null)
    {
        _tiles             = tiles;
        StartX             = startX;
        StartY             = startY;
        _originalMinerals  = originalMinerals;
        _remainingMinerals = remainingMinerals ?? new HashSet<(int x, int y)>(originalMinerals);
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    public static GameMap LoadFromFile(string path)
    {
        var lines = File.ReadAllLines(path);
        bool isCsv = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                  || (lines.Length > 0 && lines[0].Contains(','));
        return isCsv ? ParseCsv(lines) : Parse(lines);
    }

    /// <summary>
    /// Parses a plain .txt map where each row is a string of tile chars.
    /// Example row: "....#B..S..#.."
    /// </summary>
    public static GameMap Parse(string[] lines)
    {
        var tiles    = new TileType[Width, Height];
        var minerals = new HashSet<(int x, int y)>();
        int startX = 0, startY = 0;

        for (int y = 0; y < Height && y < lines.Length; y++)
        {
            for (int x = 0; x < Width && x < lines[y].Length; x++)
            {
                var tile = ParseChar(lines[y][x]);
                tiles[x, y] = tile;

                if (tile is TileType.MineralB or TileType.MineralY or TileType.MineralG)
                    minerals.Add((x, y));

                if (tile == TileType.Start) { startX = x; startY = y; }
            }
        }

        return new GameMap(tiles, startX, startY, minerals);
    }

    /// <summary>
    /// Parses a .csv map where tile tokens are separated by commas.
    /// Example row: ".,.,.,.,#,B,.,S,.,#"
    /// Each token's first non-whitespace character is the tile code.
    /// </summary>
    public static GameMap ParseCsv(string[] lines)
    {
        var tiles    = new TileType[Width, Height];
        var minerals = new HashSet<(int x, int y)>();
        int startX = 0, startY = 0;

        for (int y = 0; y < Height && y < lines.Length; y++)
        {
            // Skip blank lines (e.g. trailing newline at end of file)
            if (string.IsNullOrWhiteSpace(lines[y])) continue;

            var tokens = lines[y].Split(',');

            for (int x = 0; x < Width && x < tokens.Length; x++)
            {
                // Trim whitespace, take first char — handles "B", " B", "B " equally
                var token = tokens[x].Trim();
                if (token.Length == 0) continue;

                var tile = ParseChar(token[0]);
                tiles[x, y] = tile;

                if (tile is TileType.MineralB or TileType.MineralY or TileType.MineralG)
                    minerals.Add((x, y));

                if (tile == TileType.Start) { startX = x; startY = y; }
            }
        }

        return new GameMap(tiles, startX, startY, minerals);
    }

    /// <summary>
    /// Returns a fresh deep copy with all minerals restored.
    /// Call this at the start of every training episode so the map is never
    /// permanently depleted across episodes.
    /// </summary>
    public GameMap Clone()
    {
        var newTiles    = (TileType[,])_tiles.Clone();
        var newOriginal = new HashSet<(int x, int y)>(_originalMinerals);
        var newRemaining = new HashSet<(int x, int y)>(_originalMinerals); // fully restored
        return new GameMap(newTiles, StartX, StartY, newOriginal, newRemaining);
    }

    // ── Tile access ───────────────────────────────────────────────────────────

    public TileType GetTile(int x, int y)
    {
        if (!InBounds(x, y)) return TileType.Obstacle;
        return _tiles[x, y];
    }

    public bool IsPassable(int x, int y)
    {
        if (!InBounds(x, y)) return false;
        return _tiles[x, y] != TileType.Obstacle;
    }

    public bool HasMineral(int x, int y)    => _remainingMinerals.Contains((x, y));
    public bool CollectMineral(int x, int y) => _remainingMinerals.Remove((x, y));
    public bool InBounds(int x, int y)       => x >= 0 && x < Width && y >= 0 && y < Height;

    // ── Neighbor helpers ──────────────────────────────────────────────────────

    public IEnumerable<(int x, int y, Direction dir)> GetPassableNeighbors(int x, int y)
    {
        foreach (Direction dir in Enum.GetValues<Direction>())
        {
            var (dx, dy) = DirectionDeltas[(int)dir];
            int nx = x + dx, ny = y + dy;
            if (IsPassable(nx, ny)) yield return (nx, ny, dir);
        }
    }

    public (int x, int y) ApplyDirection(int x, int y, Direction dir)
    {
        var (dx, dy) = DirectionDeltas[(int)dir];
        return (x + dx, y + dy);
    }

    public (int dx, int dy) GetDelta(Direction dir) => DirectionDeltas[(int)dir];

    /// <summary>Static accessor for direction deltas — used by AStarPathfinder.</summary>
    public static (int dx, int dy) DirectionDelta(Direction dir) => DirectionDeltas[(int)dir];

    // ── Distance helpers ──────────────────────────────────────────────────────

    public int ManhattanDistance(int x1, int y1, int x2, int y2)
        => Math.Abs(x1 - x2) + Math.Abs(y1 - y2);

    public double ChebyshevDistance(int x1, int y1, int x2, int y2)
        => Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));

    /// <summary>
    /// Finds the nearest remaining mineral.
    /// Uses Chebyshev distance as a fast approximation — avoids running full A*
    /// on every mineral every tick (which would be millions of calls during training).
    /// A* is only used for actual path navigation, not target selection.
    /// </summary>
    public (int x, int y)? NearestMineral(int fromX, int fromY)
    {
        (int x, int y)? nearest = null;
        double bestDist = double.MaxValue;

        foreach (var pos in _remainingMinerals)
        {
            double dist = ChebyshevDistance(fromX, fromY, pos.x, pos.y);
            if (dist < bestDist)
            {
                bestDist = dist;
                nearest  = pos;
            }
        }
        return nearest;
    }

    /// <summary>
    /// Returns all minerals sorted by Chebyshev distance (fast approximation).
    /// A* is only invoked when actually navigating, not during target ranking.
    /// </summary>
    public List<((int x, int y) pos, double cost)> MineralsByPathCost(int fromX, int fromY)
    {
        var results = new List<((int x, int y), double)>();
        foreach (var pos in _remainingMinerals)
        {
            double dist = ChebyshevDistance(fromX, fromY, pos.x, pos.y);
            results.Add((pos, dist));
        }
        results.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return results;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static TileType ParseChar(char c) => c switch
    {
        '.' => TileType.Surface,
        '#' => TileType.Obstacle,
        'B' => TileType.MineralB,
        'Y' => TileType.MineralY,
        'G' => TileType.MineralG,
        'S' => TileType.Start,
        _   => TileType.Surface
    };
}
