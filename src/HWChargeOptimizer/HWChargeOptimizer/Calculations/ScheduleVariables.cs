using Google.OrTools.LinearSolver;
using HWChargeOptimizer.Configuration;

namespace HWChargeOptimizer.Calculations;

public class ScheduleVariables
{
    // Solver variables
    public Dictionary<DateTimeOffset, Variable> ChargeAmount { get; } = new();
    public Dictionary<DateTimeOffset, Variable> DischargeAmount { get; } = new();
    public Dictionary<DateTimeOffset, Variable> StateOfCharge { get; } = new();
    public Dictionary<DateTimeOffset, Variable> IsCharging { get; } = new();

    // Input parameters
    public List<Tariff> Tariffs { get; init; } = [];
    public double MaxChargingRate { get; init; }
    public double MaxDischargingRate { get; init; }
    public double CombinedBatteryCapacity { get; init; }
    public double CurrentStateOfCharge { get; init; }
    public double ChargingEfficiency { get; init; }
    public double DischargingEfficiency { get; init; }
}