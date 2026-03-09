using MarsRover.Core.Simulation;

namespace MarsRover.Core.Utils;

/// <summary>
/// Generates random 50×50 maps for testing and development.
/// Produces a text file in the expected format.
/// </summary>
public static class MapGenerator
{
    public static string GenerateMap(
        int seed         = 42,
        double obstaclePct = 0.12,
        double mineralBPct = 0.04,
        double mineralYPct = 0.02,
        double mineralGPct = 0.01)
    {
        var rng  = new Random(seed);
        var grid = new char[GameMap.Height, GameMap.Width];

        // Fill with surface
        for (int y = 0; y < GameMap.Height; y++)
            for (int x = 0; x < GameMap.Width; x++)
                grid[y, x] = '.';

        // Place start
        int startX = GameMap.Width  / 2;
        int startY = GameMap.Height / 2;
        grid[startY, startX] = 'S';

        // Scatter obstacles, minerals
        for (int y = 0; y < GameMap.Height; y++)
        {
            for (int x = 0; x < GameMap.Width; x++)
            {
                if (x == startX && y == startY) continue;

                double r = rng.NextDouble();
                if      (r < obstaclePct)                                    grid[y, x] = '#';
                else if (r < obstaclePct + mineralGPct)                      grid[y, x] = 'G';
                else if (r < obstaclePct + mineralGPct + mineralYPct)        grid[y, x] = 'Y';
                else if (r < obstaclePct + mineralGPct + mineralYPct + mineralBPct) grid[y, x] = 'B';
            }
        }

        // Build string
        var sb = new System.Text.StringBuilder();
        for (int y = 0; y < GameMap.Height; y++)
        {
            for (int x = 0; x < GameMap.Width; x++)
                sb.Append(grid[y, x]);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Writes a generated map to disk and returns the path.</summary>
    public static string WriteMapToFile(string path, int seed = 42)
    {
        var content = GenerateMap(seed);
        File.WriteAllText(path, content);
        return path;
    }
}
