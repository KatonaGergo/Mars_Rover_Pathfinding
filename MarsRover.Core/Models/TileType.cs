namespace MarsRover.Core.Models;

/// <summary>
/// Represents every possible tile type on the Mars map.
/// </summary>
public enum TileType
{
    Surface,    // '.' - passable ground
    Obstacle,   // '#' - impassable rock
    MineralB,   // 'B' - blue mineral (water ice)
    MineralY,   // 'Y' - yellow mineral (rare gold)
    MineralG,   // 'G' - green mineral (rare)
    Start       // 'S' - rover starting position
}
