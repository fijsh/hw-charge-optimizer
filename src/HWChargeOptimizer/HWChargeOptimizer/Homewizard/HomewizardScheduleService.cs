using Google.OrTools.LinearSolver;
using HWChargeOptimizer.Configuration;
using HWChargeOptimizer.Homewizard;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomewizardBatteryOptimization.Homewizard;

public class HomewizardScheduleService(ILogger<HomewizardScheduleService> logger, IOptionsMonitor<HWChargeOptimizerConfig> config, IHomeWizardBatteryController batteryController, ConfigWriter configWriter) : BackgroundService
{
    private const string SystemTimeZone = "W. Europe Standard Time";
    
    // Use a slightly increased factor to avoid floating point precision issues in the solver
    private const double RoundingFactor = 0.01;
    // Use a slightly increased factor to favor discharging on financially interesting moments
    private const double DischargeFactor = 1.01;
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(SystemTimeZone);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // retrieve the combined battery status from the battery controller and update the config
                await RetrieveAndStoreBatteriesStatusInConfigAsync();

                var currentHousePowerUsage = await batteryController.GetLatestPowerMeasurementAsync();
                var socList = await batteryController.GetBatteryStateOfChargeAsync();

                // Cache config values
                var cfg = config.CurrentValue;
                var batteryCfg = cfg.Homewizard.BatteryConfiguration;
                var p1 = cfg.Homewizard.P1;

                var currentUtcDateTime = DateTimeOffset.UtcNow;
                currentUtcDateTime = new DateTimeOffset(currentUtcDateTime.Year, currentUtcDateTime.Month, currentUtcDateTime.Day, currentUtcDateTime.Hour, 0, 0, currentUtcDateTime.Offset);

                // Filter tariffs, only retain those from current hour onwards
                var tariffs = cfg.Zonneplan.Tariffs?.Where(t => t.Date >= currentUtcDateTime).ToList() ?? [];
                if (tariffs.Count == 0)
                {
                    logger.LogError("No current tariffs available in the Zonneplan tariff list.");
                    continue;
                }

                var combinedBatteryCapacity = batteryCfg.Batteries.Sum(s => s.CapacityKWh);
                var currentStateOfCharge = socList.Select((soc, index) => soc.StateOfChargePercentage * batteryCfg.Batteries[index].CapacityKWh / 100.0).Sum();
                
                var maxChargingRate = batteryCfg.MaxChargeRateKWh;
                var maxDischargingRate = batteryCfg.MaxDischargeRateKWh;
                
                var chargingEfficiency = batteryCfg.ChargingEfficiency;
                var dischargingEfficiency = batteryCfg.DischargingEfficiency;
                
                var currentBatteryMode = p1.BatteryMode;
                
                var currentTariff = tariffs.SingleOrDefault(s => s.Date == currentUtcDateTime);
                if (currentTariff == null)
                {
                    logger.LogError("No current tariff found for the current hour {currentHour}. This should never happen.", currentUtcDateTime);
                    continue;
                }

                // lowest and highest tariff today
                var todayTariffs = tariffs.Where(t => TimeZoneInfo.ConvertTime(t.Date, timeZone).Date == TimeZoneInfo.ConvertTime(currentUtcDateTime.Date, timeZone)).ToList();
                var lowestTariff = todayTariffs.Min(t => t.Price);
                var highestTariff = todayTariffs.Max(t => t.Price);

                logger.LogInformation("-----------------------------------------------------------");
                logger.LogInformation("Current battery mode:                         {currentBatteryMode}", currentBatteryMode);
                logger.LogInformation("Total battery capacity:                       {combinedBatteryCapacity} kWh", combinedBatteryCapacity);
                logger.LogInformation("Current state of charge (combined):           {currentStateOfCharge:F4} kWh", currentStateOfCharge);
                logger.LogInformation("Current house power consumption / production: {currentPowerUsage} Watt", currentHousePowerUsage);
                logger.LogInformation("Current tariff:                               {currentTariff:F4} / kWh", currentTariff.Price);
                logger.LogInformation("Lowest tariff today:                          {lowestTariff:F4} / kWh", lowestTariff);
                logger.LogInformation("Highest tariff today:                         {highestTariff:F4} / kWh", highestTariff);
                logger.LogInformation("Charging efficiency:                          {chargingEfficiency} %", chargingEfficiency * 100);
                logger.LogInformation("Discharging efficiency:                       {dischargingEfficiency} %", dischargingEfficiency * 100);
                logger.LogInformation("-----------------------------------------------------------");

                logger.LogInformation("Starting calculation of optimal charging schedule...");

                var scheduleVariables = new ScheduleVariables
                {
                    Tariffs = tariffs,
                    MaxChargingRate = maxChargingRate,
                    MaxDischargingRate = maxDischargingRate,
                    CombinedBatteryCapacity = combinedBatteryCapacity,
                    CurrentStateOfCharge = currentStateOfCharge,
                    ChargingEfficiency = chargingEfficiency,
                    DischargingEfficiency = dischargingEfficiency
                };
                
                // Create the solver that will calculate the most efficient charging
                using var solver = Solver.CreateSolver("SCIP");
                if (solver == null)
                {
                    throw new InvalidOperationException("Failed to create SCIP solver");
                }

                var resultStatus = Calculate(solver, scheduleVariables);

                // Display results
                if (resultStatus is Solver.ResultStatus.OPTIMAL or Solver.ResultStatus.FEASIBLE)
                {
                    var startCharging = false;
                    var startDischarging = false;

                    logger.LogInformation("-----------------------------------------------------------");
                    logger.LogInformation("Local time  | C | D |  CQ   |  DQ   |  SoC | Tariff");
                    
                    foreach (var tariff in tariffs)
                    {
                        var charge = Math.Round(scheduleVariables.ChargeAmount[tariff.Date].SolutionValue(), 2);
                        var discharge = Math.Round(scheduleVariables.DischargeAmount[tariff.Date].SolutionValue(), 2);
                        var soc = Math.Round(scheduleVariables.StateOfCharge[tariff.Date].SolutionValue(), 2);

                        var chargingStatus = charge > RoundingFactor ? "Y" : "n";
                        var dischargingStatus = discharge > RoundingFactor ? "Y" : "n";

                        logger.LogInformation(
                            "{DateTimeOffset:dd/MM HH:mm} | {ChargingStatus} | {DischargingStatus} | {Charge,5:F2} | {Discharge,5:F2} | {Soc,3:F2} | {TariffPrice:F5}", 
                            TimeZoneInfo.ConvertTime(tariff.Date, timeZone), chargingStatus, dischargingStatus, charge, discharge, soc, tariff.Price);

                        if (tariff.Date == currentUtcDateTime && chargingStatus == "Y")
                            startCharging = true;
                        if (tariff.Date == currentUtcDateTime && dischargingStatus == "Y")
                            startDischarging = true;
                    }

                    // Calculate net cost
                    var totalCost = tariffs.Sum(t => t.Price * scheduleVariables.ChargeAmount[t.Date].SolutionValue()) / 100;
                    var totalValue = tariffs.Sum(t => t.Price * scheduleVariables.DischargeAmount[t.Date].SolutionValue()) / 100;

                    logger.LogInformation("-----------------------------------------------------------");
                    logger.LogInformation("Total charging cost:   € {TotalCost,5:F2}", totalCost);
                    logger.LogInformation("Total discharge cost:  € {TotalValue,5:F2}", totalValue);
                    logger.LogInformation("Net cost:              € {NetCost,5:F2}", totalCost - totalValue);
                    logger.LogInformation("-----------------------------------------------------------");

                    if (startCharging)
                    {
                        logger.LogInformation("Setting battery to full charging mode.");
                        await SetBatteryModeAsync(BatteryMode.ToFull);
                    }
                    else if (startDischarging)
                    {
                        logger.LogInformation("Setting battery to zero charging mode.");
                        await SetBatteryModeAsync(BatteryMode.Zero);
                    }
                    else
                    {
                        logger.LogInformation("Setting battery to standby mode.");
                        await SetBatteryModeAsync(BatteryMode.Standby);
                    }
                }
                else
                {
                    logger.LogWarning("No solution found. Setting battery to zero charging mode.");
                    await SetBatteryModeAsync(BatteryMode.Zero);
                }
            }
            catch (Exception ex)
            {
                logger.LogError("An error occurred while executing the Homewizard schedule service: {message}", ex.Message);
            }
            finally
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    logger.LogInformation("Homewizard schedule service is stopping, because cancellation was requested.");
                }
                else
                {
                    // wait for the configured refresh interval or until the next full hour, whichever is shorter
                    var refreshIntervalSeconds = config.CurrentValue.Homewizard.RefreshIntervalSeconds;
                    var now = DateTime.Now;
                    var nextHour = now.AddHours(1).Date.AddHours(now.Hour + 1);
                    var secondsUntilNextHour = (int)(nextHour - now).TotalSeconds;
                    var waitSeconds = Math.Min(refreshIntervalSeconds, secondsUntilNextHour);

                    logger.LogInformation("The Homewizard schedule service will wait for {refreshInterval} seconds before the next execution.",
                        waitSeconds);

                    await Task.Delay(waitSeconds * 1000, stoppingToken);
                }
            }
        }
    }

    private static Solver.ResultStatus Calculate(Solver solver, ScheduleVariables scheduleVariables)
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

    /// <summary>
    /// Sets the battery mode to the specified mode and updates the configuration.
    /// </summary>
    /// <param name="requestedBatteryMode">The battery mode to request</param>
    private async Task SetBatteryModeAsync(string requestedBatteryMode)
    {
        if (config.CurrentValue.Homewizard.P1.BatteryMode != requestedBatteryMode)
        {
            await batteryController.SetBatteryModeAsync(requestedBatteryMode);
            config.CurrentValue.Homewizard.P1.BatteryMode = requestedBatteryMode;
            config.CurrentValue.Homewizard.P1.LastUpdated = DateTimeOffset.UtcNow;
            await configWriter.WriteAsync(config.CurrentValue);
        }
    }

    /// <summary>
    /// Gets the current battery status from the battery controller and updates the configuration.
    /// </summary>
    /// <returns></returns>
    private async Task RetrieveAndStoreBatteriesStatusInConfigAsync()
    {
        var currentDateTime = DateTimeOffset.UtcNow;

        // retrieve the current battery status from the battery controller
        var batteryResponse = await batteryController.GetBatteriesStatusAsync();

        logger.LogInformation("Battery: Mode={mode}, P={powerW}, MaxIn={maxConsumptionW}, MaxOut={maxProductionW}, Target={targetPowerW}",
            batteryResponse.Mode,
            batteryResponse.PowerW,
            batteryResponse.MaxConsumptionW,
            batteryResponse.MaxProductionW,
            batteryResponse.TargetPowerW);

        config.CurrentValue.Homewizard.P1.BatteryMode = batteryResponse.Mode;
        config.CurrentValue.Homewizard.P1.LastUpdated = currentDateTime;
        config.CurrentValue.Homewizard.P1.PowerW = batteryResponse.PowerW;
        config.CurrentValue.Homewizard.P1.MaxConsumptionW = batteryResponse.MaxConsumptionW;
        config.CurrentValue.Homewizard.P1.MaxProductionW = batteryResponse.MaxProductionW;
        config.CurrentValue.Homewizard.P1.TargetPowerW = batteryResponse.TargetPowerW;

        await configWriter.WriteAsync(config.CurrentValue);
    }
}

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