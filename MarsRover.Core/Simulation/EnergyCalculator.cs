using MarsRover.Core.Simulation;
using MarsRover.Core.Models;

namespace MarsRover.Core.Simulation;

/// <summary>
/// Single source of truth for all battery/energy calculations.
/// All values are per half-hour tick.
/// </summary>
public static class EnergyCalculator
{
    public const double MaxBattery       = 100.0;
    public const double MinBattery       = 0.0;
    public const double K                = 2.0;    // energy constant
    public const double DayChargePerTick = 10.0;   // +10 per half-hour during day
    public const double StandbyDrain     = 1.0;    // idle, not mining
    public const double MiningDrain      = 2.0;    // while mining

    /// <summary>Energy cost for moving at the given speed (k * v²).</summary>
    public static double MoveCost(RoverSpeed speed) => K * (int)speed * (int)speed;

    /// <summary>
    /// Computes net battery delta for one tick given the action and time of day.
    /// Negative = net drain, Positive = net gain.
    /// </summary>
    public static double NetDelta(RoverActionType actionType, RoverSpeed speed, bool isDay)
    {
        double cost = actionType switch
        {
            RoverActionType.Move    => MoveCost(speed),
            RoverActionType.Mine    => MiningDrain,
            RoverActionType.Standby => StandbyDrain,
            _                       => StandbyDrain
        };

        double charge = isDay ? DayChargePerTick : 0.0;
        return charge - cost;
    }

    /// <summary>
    /// Applies one tick's energy change, clamped to [0, 100].
    /// Returns the new battery level.
    /// </summary>
    public static double Apply(double currentBattery, RoverActionType actionType,
                               RoverSpeed speed, bool isDay)
    {
        double next = currentBattery + NetDelta(actionType, speed, isDay);
        return Math.Clamp(next, MinBattery, MaxBattery);
    }

    /// <summary>
    /// Returns true if the rover has enough battery to safely perform the action.
    /// We use a safety margin of 1 unit above 0.
    /// </summary>
    public static bool CanAfford(double currentBattery, RoverActionType actionType,
                                  RoverSpeed speed, bool isDay)
    {
        double delta = NetDelta(actionType, speed, isDay);
        return currentBattery + delta >= MinBattery;
    }

    /// <summary>
    /// Calculates the exact net battery delta for a trip of <paramref name="pathSteps"/>
    /// A* cells at the given speed, starting at tick <paramref name="startTick"/>.
    ///
    /// Because Fast=3 cells/tick, Normal=2, Slow=1, the number of ticks consumed
    /// differs by speed — and day/night changes during the trip affect charging.
    /// This is therefore the only correct way to compare speed choices.
    ///
    /// Returns the net battery CHANGE (negative = drain, positive = net charge).
    /// To get the battery REQUIRED: Math.Max(0, -ExactTripDelta(...))
    /// </summary>
    public static double ExactTripDelta(int startTick, int pathSteps, RoverSpeed speed,
                                         int totalTicks)
    {
        int cellsPerTick = (int)speed;                                // 1, 2, or 3
        int ticks        = (int)Math.Ceiling(pathSteps / (double)cellsPerTick);
        double delta     = 0;

        for (int t = 0; t < ticks; t++)
        {
            int  futureTick = Math.Min(startTick + t, totalTicks - 1);
            bool isDay      = SimulationEngine.IsDay(futureTick);
            delta          += NetDelta(RoverActionType.Move, speed, isDay);
        }

        return delta; // negative = net battery drain over the trip
    }

    /// <summary>
    /// How many ticks does a trip of <paramref name="pathSteps"/> A* cells take
    /// at the given speed?
    /// </summary>
    public static int TripTicks(int pathSteps, RoverSpeed speed)
        => (int)Math.Ceiling(pathSteps / (double)(int)speed);
}
