using Google.OrTools.LinearSolver;

namespace HWChargeOptimizer.Calculations;

public static class OptimizeSchedule
{
    // Use a slightly increased factor to favor discharging on financially interesting moments
    private const double DischargeFactor = 1.01;
    
    public static Solver.ResultStatus Calculate(Solver solver, ScheduleVariables scheduleVariables)
    {
        // Input validation
        if (scheduleVariables.Tariffs.Count == 0 || scheduleVariables.CombinedBatteryCapacity <= 0 || scheduleVariables.CurrentStateOfCharge < 0)
        {
            throw new ArgumentException("Invalid input parameters for optimization");
        }

        // Objective: minimize cost (negative discharge value = maximize profit)
        var objective = solver.Objective();

        // Create constraints and objective coefficients
        foreach (var tariff in scheduleVariables.Tariffs)
        {
            scheduleVariables.ChargeAmount[tariff.Date] = solver.MakeNumVar(0.0, scheduleVariables.MaxChargingRate, $"charge_{tariff.Date}");
            scheduleVariables.DischargeAmount[tariff.Date] = solver.MakeNumVar(0.0, scheduleVariables.MaxDischargingRate, $"discharge_{tariff.Date}");
            scheduleVariables.StateOfCharge[tariff.Date] = solver.MakeNumVar(0.0, scheduleVariables.CombinedBatteryCapacity, $"soc_{tariff.Date}");
            scheduleVariables.IsCharging[tariff.Date] = solver.MakeIntVar(0, 1, $"isCharging_{tariff.Date}");

            // Always charge at maximum rate during negative tariffs
            if (tariff.Price < 0)
            {
                solver.Add(scheduleVariables.ChargeAmount[tariff.Date] == scheduleVariables.MaxChargingRate);
            }

            // Prevent charging and discharging at the same time
            solver.Add(scheduleVariables.ChargeAmount[tariff.Date] <= scheduleVariables.MaxChargingRate * scheduleVariables.IsCharging[tariff.Date]);
            solver.Add(scheduleVariables.DischargeAmount[tariff.Date] <= scheduleVariables.MaxDischargingRate * (1 - scheduleVariables.IsCharging[tariff.Date]));

            // Prevent discharging if SoC is 0
            solver.Add(scheduleVariables.DischargeAmount[tariff.Date] <= scheduleVariables.StateOfCharge[tariff.Date]);

            // Cost of charging and value of discharging
            objective.SetCoefficient(scheduleVariables.ChargeAmount[tariff.Date], tariff.Price);
            objective.SetCoefficient(scheduleVariables.DischargeAmount[tariff.Date], -tariff.Price * DischargeFactor);
        }

        // Set initial state of charge
        solver.Add(scheduleVariables.StateOfCharge[scheduleVariables.Tariffs[0].Date] == scheduleVariables.CurrentStateOfCharge);

        // Battery state evolution
        for (var i = 1; i < scheduleVariables.Tariffs.Count; i++)
        {
            var prevHour = scheduleVariables.Tariffs[i - 1].Date;
            var currHour = scheduleVariables.Tariffs[i].Date;

            solver.Add(scheduleVariables.StateOfCharge[currHour] == scheduleVariables.StateOfCharge[prevHour]
                + scheduleVariables.ChargeAmount[prevHour] * scheduleVariables.ChargingEfficiency
                - scheduleVariables.DischargeAmount[prevHour] / scheduleVariables.DischargingEfficiency);
        }

        objective.SetMinimization();

        return solver.Solve();
    }
}