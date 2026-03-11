using MarsRover.Core.Models;
namespace MarsRover.Core.Simulation;

/// <summary>
/// The authoritative simulation engine. Each call to Step() advances the world
/// by exactly one half-hour tick and returns the resulting state + log entry.
///
/// This class is intentionally UI-agnostic. The UI/ViewModel drives it via a timer.
/// </summary>
public class SimulationEngine
{
    private readonly GameMap _map;
    private readonly int     _totalTicks; // derived from hour input * 2

    // Mutable simulation state
    private int    _x;
    private int    _y;
    private double _battery;
    private int    _tick;
    private int    _mineralsB;
    private int    _mineralsY;
    private int    _mineralsG;
    private double _distanceTraveled;
    private bool   _isMining;

    public bool   IsFinished    => _tick >= _totalTicks;
    public bool   BatteryDead   => _battery <= 0;
    public int    CurrentTick   => _tick;
    public int    TotalTicks    => _totalTicks;
    public GameMap Map          => _map;
    public double Progress      => (double)_tick / _totalTicks;

    public SimulationEngine(GameMap map, int durationHours)
    {
        if (durationHours < 24)
            throw new ArgumentException("Duration must be at least 24 hours.", nameof(durationHours));

        _map         = map;
        _totalTicks  = durationHours * 2; // 2 ticks per hour (1 tick is a half an hour e.g an action)
        _x           = map.StartX;
        _y           = map.StartY;
        _battery     = EnergyCalculator.MaxBattery;
        _tick        = 0;
    }

    // ── State snapshot ───────────────────────────────────────────────────────

    public RoverState GetState() => new(
        _x, _y, _battery, _tick,
        _mineralsB, _mineralsY, _mineralsG,
        _isMining, RoverAction.StandbyAction
    );

    // ── Core tick ────────────────────────────────────────────────────────────

    /// <summary>
    /// Advances the simulation by one half-hour tick.
    /// Returns null if already finished.
    /// </summary>
    public SimulationLogEntry? Step(RoverAction action, string phase = "Collection")
    {
        if (IsFinished || BatteryDead) return null;

        bool isDay = IsDay(_tick);
        string eventNote = string.Empty;
        _isMining = false;

        switch (action.Type)
        {
            case RoverActionType.Move:
                eventNote = ExecuteMove(action.Directions ?? Array.Empty<Direction>(),
                                        action.Speed, isDay);
                break;

            case RoverActionType.Mine:
                eventNote = ExecuteMine(isDay);
                break;

            case RoverActionType.Standby:
                _battery = EnergyCalculator.Apply(_battery, RoverActionType.Standby,
                                                   RoverSpeed.Slow, isDay);
                eventNote = "Standby";
                break;
        }

        _tick++;

        return new SimulationLogEntry(
            Tick:             _tick,
            Sol:              _tick / RoverState.TicksPerSol,
            HourOfSol:        (_tick % RoverState.TicksPerSol) * 0.5,
            IsDay:            isDay,
            X:                _x,
            Y:                _y,
            Battery:          _battery,
            Action:           action,
            MineralsB:        _mineralsB,
            MineralsY:        _mineralsY,
            MineralsG:        _mineralsG,
            TotalMinerals:    _mineralsB + _mineralsY + _mineralsG,
            DistanceTraveled: _distanceTraveled,
            EventNote:        eventNote,
            Phase:            phase
        );
    }

    // ── Action executors ─────────────────────────────────────────────────────

    private string ExecuteMove(IReadOnlyList<Direction> dirs, RoverSpeed speed, bool isDay)
    {
        // Downgrade speed if battery cant afford it
        if (!EnergyCalculator.CanAfford(_battery, RoverActionType.Move, speed, isDay))
            speed = RoverSpeed.Slow;

        // Energy is charged for the tick regardless of how many steps complete
        _battery = EnergyCalculator.Apply(_battery, RoverActionType.Move, speed, isDay);

        if (dirs.Count == 0)
            return "Move — no directions";

        // Execute each direction step in sequence.
        // FREE MOVEMENT: each step can be a different direction.
        int moved  = 0;
        int finalX = _x;
        int finalY = _y;

        foreach (var dir in dirs)
        {
            var (nx, ny) = _map.ApplyDirection(finalX, finalY, dir);
            if (!_map.IsPassable(nx, ny)) break; // its an obstacle, so stop here
            finalX = nx;
            finalY = ny;
            moved++;
        }

        if (moved == 0)
            return "Blocked — standby";

        _distanceTraveled += moved;
        _x = finalX;
        _y = finalY;

        // Build a compact log, for example:"Move N→E→NE @ Fast ×3 → (12,7)"
        var dirStr = string.Join("→", dirs.Take(moved)
                         .Select(d => d.ToString()[..1])); // use the first letter: N E S W etc.
        return $"Move {dirStr} @ {speed} ×{moved} → ({_x},{_y})";
    }

    private string ExecuteMine(bool isDay)
    {
        if (!_map.HasMineral(_x, _y))
        {
            // Nothing to mine here, so treat as standby
            _battery = EnergyCalculator.Apply(_battery, RoverActionType.Standby,
                                               RoverSpeed.Slow, isDay);
            return "Mine attempted — no mineral here";
        }

        _isMining = true;
        _battery  = EnergyCalculator.Apply(_battery, RoverActionType.Mine,
                                            RoverSpeed.Slow, isDay);

        var tile = _map.GetTile(_x, _y);
        _map.CollectMineral(_x, _y);

        switch (tile)
        {
            case TileType.MineralB: _mineralsB++; return "⛏ Collected Blue mineral";
            case TileType.MineralY: _mineralsY++; return "⛏ Collected Yellow mineral";
            case TileType.MineralG: _mineralsG++; return "⛏ Collected Green mineral";
            default:                              return "⛏ Collected mineral";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public static bool IsDay(int tick)
    {
        int tickInSol = tick % RoverState.TicksPerSol;
        return tickInSol < RoverState.DayTicksPerSol;
    }

    /// <summary>Resets the engine to initial state (for replay/retraining).</summary>
    public void Reset()
    {
        _x                = _map.StartX;
        _y                = _map.StartY;
        _battery          = EnergyCalculator.MaxBattery;
        _tick             = 0;
        _mineralsB        = 0;
        _mineralsY        = 0;
        _mineralsG        = 0;
        _distanceTraveled = 0;
        _isMining         = false;
    }
}
