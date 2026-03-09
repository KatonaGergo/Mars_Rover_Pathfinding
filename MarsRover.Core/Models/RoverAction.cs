namespace MarsRover.Core.Models;

/// <summary>
/// All actions the rover can perform in one half-hour tick.
///
/// FREE MOVEMENT MODEL:
///   A Move action carries a list of 1–3 individual direction steps.
///   The number of steps equals the speed value (Slow=1, Normal=2, Fast=3).
///   Each step moves the rover one cell in any of the 8 directions — they
///   do NOT have to be the same direction. This means a Fast rover can go
///   North, North, East in a single tick, following a curved A* path optimally.
///
///   Energy cost is based on speed (the number of steps), applied once per tick:
///     Slow (1 step)  — k×1² = 2%
///     Normal (2 steps) — k×2² = 8%
///     Fast (3 steps) — k×3² = 18%
/// </summary>
public enum RoverSpeed
{
    Slow   = 1, // 1 step/tick,  E = k×1² = 2%
    Normal = 2, // 2 steps/tick, E = k×2² = 8%
    Fast   = 3  // 3 steps/tick, E = k×3² = 18%
}

public enum Direction
{
    North     = 0,
    NorthEast = 1,
    East      = 2,
    SouthEast = 3,
    South     = 4,
    SouthWest = 5,
    West      = 6,
    NorthWest = 7
}

public enum RoverActionType
{
    Move,
    Mine,
    Standby
}

public record RoverAction(
    RoverActionType         Type,
    IReadOnlyList<Direction>? Directions = null,   // ordered list of steps for this tick
    RoverSpeed              Speed = RoverSpeed.Slow)
{
    // ── Convenience properties ────────────────────────────────────────────────

    /// First direction in the list — for backward-compat code that only needs one.
    public Direction? Dir => Directions is { Count: > 0 } d ? d[0] : null;

    // ── Factory methods ───────────────────────────────────────────────────────

    public static readonly RoverAction MineAction    = new(RoverActionType.Mine);
    public static readonly RoverAction StandbyAction = new(RoverActionType.Standby);

    /// <summary>Single-step move (Slow, or any speed where path is straight).</summary>
    public static RoverAction Move(Direction dir, RoverSpeed speed) =>
        new(RoverActionType.Move, new[] { dir }, speed);

    /// <summary>
    /// Multi-step free-movement action.
    /// <paramref name="dirs"/> contains one direction per movement point — up to
    /// (int)speed entries. If fewer steps are available (near goal), pass fewer.
    /// </summary>
    public static RoverAction Move(IReadOnlyList<Direction> dirs, RoverSpeed speed) =>
        new(RoverActionType.Move, dirs, speed);

    /// <summary>
    /// All single-direction actions — used by legacy QLearningAgent and AllActions().
    /// </summary>
    public static IEnumerable<RoverAction> AllActions()
    {
        foreach (Direction d in Enum.GetValues<Direction>())
            foreach (RoverSpeed s in Enum.GetValues<RoverSpeed>())
                yield return Move(d, s);

        yield return MineAction;
        yield return StandbyAction;
    }

    public override string ToString() => Type switch
    {
        RoverActionType.Move =>
            Directions is { Count: > 1 }
                ? $"Move [{string.Join("→", Directions)}] @ {Speed}"
                : $"Move {Dir} @ {Speed}",
        RoverActionType.Mine    => "Mine",
        RoverActionType.Standby => "Standby",
        _                       => "Unknown"
    };
}
