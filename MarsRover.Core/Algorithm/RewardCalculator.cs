using MarsRover.Core.Models;
using MarsRover.Core.Simulation;

namespace MarsRover.Core.Algorithm;

public static class HybridRewardCalculator
{
    // Reward values for various events and conditions. These are tunable hyperparameters.
    public const double MineralCollected  =  150.0;
    public const double ReturnedToBase    =  300.0;
    public const double FailedToReturn    = -200.0;
    public const double BatteryDied       = -300.0;
    public const double LowBatteryWarning =  -15.0;
    public const double CriticalBattery   =  -50.0;
    public const double NightMovePenalty  =   -0.5;
    public const double IdleDuringDay     =   -2.0;

    public static double Calculate(
        RoverState         prevState,
        RoverAction        action,
        SimulationLogEntry result,
        GameMap            map,
        bool               collectedMineral,
        bool               isTerminal,
        bool               returnedHome = false)
    {
        double reward = 0;

        if (isTerminal)
        {
            if (result.Battery <= 0)    reward += BatteryDied;
            if (returnedHome)           reward += ReturnedToBase;
            else if (result.Battery > 0) reward += FailedToReturn;
            reward += result.TotalMinerals * 10.0;
            return reward;
        }

        if (collectedMineral)           reward += MineralCollected;
        if (result.Battery < 5)         reward += CriticalBattery;
        else if (result.Battery < 10)   reward += LowBatteryWarning;
        if (!result.IsDay && action.Type == RoverActionType.Move)
                                        reward += NightMovePenalty;
        if (result.IsDay && action.Type == RoverActionType.Standby)
                                        reward += IdleDuringDay;

        return reward;
    }
}

public static class RewardCalculator
{
    public static double Calculate(
        RoverState prevState, RoverAction action, SimulationLogEntry result,
        GameMap map, bool collectedMineral, bool hitObstacle, bool isTerminal)
        => HybridRewardCalculator.Calculate(prevState, action, result, map,
                                            collectedMineral, isTerminal);
}
