using System.Globalization;
using System.Reflection;
using System.Text;
using Google.OrTools.LinearSolver;
using HWChargeOptimizer.Calculations;
using HWChargeOptimizer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScottPlot;


namespace HWChargeOptimizer.HomeWizard;

public class HomeWizardScheduleService(ILogger<HomeWizardScheduleService> logger, IOptionsMonitor<HWChargeOptimizerConfig> config, IHomeWizardBatteryController batteryController, ConfigWriter configWriter) : BackgroundService
{
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var timeZone = TimeZoneInfo.FindSystemTimeZoneById(Constants.SystemTimeZone);

                // retrieve the combined battery status from the battery controller and update the config
                await RetrieveAndStoreBatteriesStatusInConfigAsync();

                var currentHousePowerUsage = await batteryController.GetLatestPowerMeasurementAsync();
                var socList = await RetrieveAndStoreBatteriesSoCAsync();

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
                var todayTariffs = tariffs.Where(t => 
                    TimeZoneInfo.ConvertTime(t.Date, timeZone).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) 
                    == TimeZoneInfo.ConvertTime(currentUtcDateTime.Date, timeZone).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();
                
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
                using var solver = Solver.CreateSolver(Constants.SolverType);
                if (solver == null)
                {
                    throw new InvalidOperationException($"Failed to create {Constants.SolverType} solver");
                }

                var resultStatus = OptimizeSchedule.Calculate(solver, scheduleVariables);

                // Display results
                StringBuilder chargingSchedule = new();
                if (resultStatus is Solver.ResultStatus.OPTIMAL or Solver.ResultStatus.FEASIBLE)
                {
                    var startCharging = false;
                    var startDischarging = false;
                    
                    chargingSchedule.AppendLine("Local time  | C | D |  CQ   |  DQ   |  SoC | Tariff");
                    chargingSchedule.AppendLine("-----------------------------------------------------------");
                    
                    foreach (var tariff in tariffs)
                    {
                        var charge = Math.Round(scheduleVariables.ChargeAmount[tariff.Date].SolutionValue(), 2);
                        var discharge = Math.Round(scheduleVariables.DischargeAmount[tariff.Date].SolutionValue(), 2);
                        var soc = Math.Round(scheduleVariables.StateOfCharge[tariff.Date].SolutionValue(), 2);

                        var chargingStatus = charge > Constants.RoundingFactor ? "Y" : " ";
                        var dischargingStatus = discharge > Constants.RoundingFactor ? "Y" : " ";

                        chargingSchedule.AppendLine(
                            $"{TimeZoneInfo.ConvertTime(tariff.Date, timeZone):dd/MM HH:mm} | {chargingStatus} | {dischargingStatus} | {charge,5:F2} | {discharge,5:F2} | {soc,3:F2} | {tariff.Price:F5}");

                        if (tariff.Date == currentUtcDateTime && chargingStatus == "Y")
                            startCharging = true;
                        if (tariff.Date == currentUtcDateTime && dischargingStatus == "Y")
                            startDischarging = true;
                    }

                    // Calculate net cost
                    var totalCost = tariffs.Sum(t => t.Price * scheduleVariables.ChargeAmount[t.Date].SolutionValue()) / 100;
                    var totalValue = tariffs.Sum(t => t.Price * scheduleVariables.DischargeAmount[t.Date].SolutionValue()) / 100;

                    chargingSchedule.AppendLine("-----------------------------------------------------------");
                    chargingSchedule.AppendLine($"Total charging cost:   € {totalCost,5:F2}");
                    chargingSchedule.AppendLine($"Total discharge cost:  € {totalValue,5:F2}");
                    chargingSchedule.AppendLine($"Net cost:              € {totalCost - totalValue,5:F2}");
                    chargingSchedule.AppendLine("-----------------------------------------------------------");
                    
                    // write schedule to file
                    await File.WriteAllTextAsync(Constants.ChargingScheduleFileName, chargingSchedule.ToString(), stoppingToken);
                    
                    if (startCharging)
                    {
                        logger.LogInformation("Setting battery to full charging mode.");
                        await SetBatteryModeAsync(BatteryMode.FullCharge);
                    }
                    else if (startDischarging)
                    {
                        logger.LogInformation("Setting battery to discharge only mode.");
                        await SetBatteryModeAsync(BatteryMode.ZeroDischargeOnly);
                    }
                    else
                    {
                        logger.LogInformation("Setting battery to standby mode.");
                        await SetBatteryModeAsync(BatteryMode.Standby);
                    }
                    
                    // create chart file
                    CreateBatterySchedulePlot(tariffs, scheduleVariables.ChargeAmount, scheduleVariables.DischargeAmount, scheduleVariables.StateOfCharge);
                }
                else
                {
                    logger.LogWarning("No solution found. Setting battery to zero mode.");
                    await SetBatteryModeAsync(BatteryMode.Zero); // set to zero mode as a fallback
                }
            }
            catch (Exception ex)
            {
                logger.LogError("An error occurred while executing the HomeWizard schedule service: {message}", ex.ToString());
            }
            finally
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    logger.LogInformation("HomeWizard schedule service is stopping, because cancellation was requested.");
                }
                else
                {
                    // wait for the configured refresh interval or until the next full hour, whichever is shorter
                    var refreshIntervalSeconds = config.CurrentValue.Homewizard.RefreshIntervalSeconds;
                    var now = DateTime.Now;
                    var nextHour = now.AddHours(1).Date.AddHours(now.Hour + 1);
                    var secondsUntilNextHour = (int)(nextHour - now).TotalSeconds;
                    var waitSeconds = Math.Min(refreshIntervalSeconds, secondsUntilNextHour);

                    logger.LogInformation("The HomeWizard schedule service will wait for {refreshInterval} seconds before the next execution.",
                        waitSeconds);

                    await Task.Delay(waitSeconds * 1000, stoppingToken);
                }
            }
        }
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
    
    /// <summary>
    /// Gets the current battery status from the battery controller and updates the configuration.
    /// </summary>
    /// <returns></returns>
    private async Task<List<BatteryStateOfCharge>> RetrieveAndStoreBatteriesSoCAsync()
    {
        var currentDateTime = DateTimeOffset.UtcNow;
        
        var socList = await batteryController.GetBatteryStateOfChargeAsync();
        
        // log socList
        foreach (var soc in socList)
        {
            logger.LogInformation("Battery '{BatteryName}' (IP: {BatteryIp}) state of charge: {StateOfCharge}%", soc.Name, soc.Ip, soc.StateOfChargePercentage);
        }
        
        foreach (var soc in socList)
        {
            config.CurrentValue.Homewizard.BatteryConfiguration.Batteries.Single(b => b.Ip == soc.Ip).StateOfChargePercentage = soc.StateOfChargePercentage;
            config.CurrentValue.Homewizard.BatteryConfiguration.Batteries.Single(b => b.Ip == soc.Ip).LastUpdated = currentDateTime;
        }
        
        await configWriter.WriteAsync(config.CurrentValue);

        return socList;
    }
    
    private static void CreateBatterySchedulePlot(List<Tariff> tariffs,
        Dictionary<DateTimeOffset, Variable> chargeAmount,
        Dictionary<DateTimeOffset, Variable> dischargeAmount,
        Dictionary<DateTimeOffset, Variable> stateOfCharge)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(Constants.SystemTimeZone);
        
        // convert tariff times to local time zone for plotting
        foreach (var tariff in tariffs)
        {
            tariff.Date = TimeZoneInfo.ConvertTime(tariff.Date, timeZone);
        }
        
        // Create arrays for plotting
        var times = new double[tariffs.Count];
        var socValues = new double[tariffs.Count];
        var chargeValues = new double[tariffs.Count];
        var dischargeValues = new double[tariffs.Count];
        var tariffValues = new double[tariffs.Count];
        var timeLabels = new string[tariffs.Count];

        // Fill arrays with data
        for (var i = 0; i < tariffs.Count; i++)
        {
            var tariff = tariffs[i];
            times[i] = i;
            socValues[i] = stateOfCharge[tariff.Date].SolutionValue();
            chargeValues[i] = chargeAmount[tariff.Date].SolutionValue();
            dischargeValues[i] = dischargeAmount[tariff.Date].SolutionValue();
            tariffValues[i] = tariff.Price;
            timeLabels[i] = tariff.Date.ToString("HH", CultureInfo.InvariantCulture);
        }

        // Create plot
        var plot = new Plot();

        var fontLocation = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? throw new InvalidOperationException("Cannot find executing assembly location."), @"Fonts/Roboto-VariableFont.ttf");
        if (!File.Exists(fontLocation))
            throw new InvalidOperationException($"Font file not found at {fontLocation}. Cannot create chart.");
        
        // Add a font file to use its typeface for fonts with a given name
        Fonts.AddFontFile(
            name: "Roboto",
            path: Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? throw new InvalidOperationException("Cannot find fonts."), @"Fonts/Roboto-VariableFont.ttf"));
        
        plot.Font.Set("Roboto");
        
        // change figure colors for dark mode
        plot.FigureBackground.Color = Color.FromHex("#181818");
        plot.DataBackground.Color = Color.FromHex("#1f1f1f");

        // change axis and grid colors for dark mode
        plot.Axes.Color(Color.FromHex("#d7d7d7"));
        plot.Grid.MajorLineColor = Color.FromHex("#404040");

        // change legend colors for dark mode
        plot.Legend.BackgroundColor = Color.FromHex("#404040").WithAlpha(0.7);;
        plot.Legend.FontColor = Color.FromHex("#d7d7d7");
        plot.Legend.OutlineColor = Color.FromHex("#d7d7d7");

        // Add state of charge as a line with markers
        var socPlot = plot.Add.Scatter(times, socValues);
        socPlot.MarkerShape = MarkerShape.FilledCircle;
        socPlot.MarkerSize = 5;
        socPlot.LineWidth = 2;
        socPlot.LineColor = Colors.Blue;
        socPlot.MarkerColor = Colors.Blue;
        socPlot.LegendText = "State of Charge";

        for (var i = 0; i < times.Length; i++)
        {
            Bar chargeBar = new()
            {
                Value = chargeValues[i],
                Position = times[i],
                Size = 0.3,
                FillColor = Colors.Purple.WithAlpha(0.7),
                LineColor = Colors.Purple,
            };

            var barPlot = plot.Add.Bar(chargeBar);
            if (i == 0)
                barPlot.LegendText = "Charge";
        }

        for (var i = 0; i < times.Length; i++)
        {
            Bar dischargeBar = new()
            {
                Value = dischargeValues[i] * -1,
                Position = times[i],
                Size = 0.3,
                FillColor = Colors.Green.WithAlpha(0.7),
                LineColor = Colors.Green,
            };

            var barPlot = plot.Add.Bar(dischargeBar);
            if (i == 0)
                barPlot.LegendText = "Discharge";
        }

        // Create a second y-axis for tariff values
        var rightAxis = plot.Axes.Right;
        rightAxis.Label.Text = "Tariff (cents)";
        rightAxis.IsVisible = true;

        // Add tariff values on the right axis
        var tariffPlot = plot.Add.Scatter(times, tariffValues);
        tariffPlot.LineWidth = 2;
        tariffPlot.LineColor = Colors.Orange;
        tariffPlot.MarkerShape = MarkerShape.FilledDiamond;
        tariffPlot.MarkerSize = 5;
        tariffPlot.MarkerColor = Colors.Orange;
        tariffPlot.LegendText = "Tariff";
        tariffPlot.Axes.YAxis = plot.Axes.Right;
        
        // Configure axes
        plot.Axes.Bottom.Label.Text = "Time";
        plot.Axes.Left.Label.Text = "Energy (kWh)";

        // Add custom tick labels
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
            positions: times,
            labels: timeLabels);
        plot.Axes.Bottom.MajorTickStyle.Length = 0;

        // Add legend
        plot.Legend.IsVisible = true;
        plot.Legend.Alignment = Alignment.UpperLeft;

        // Add title
        plot.Title("Battery Charge/Discharge Planned Schedule");
        
        // set the color palette used when coloring new items added to the plot
        plot.Add.Palette = new ScottPlot.Palettes.Penumbra();
        
        // Save the plot
        if (File.Exists(Constants.PlotFileName))
        {
            File.Delete(Constants.PlotFileName);
        }
        
        plot.SavePng(Constants.PlotFileName, 1200, 600);
    }
}