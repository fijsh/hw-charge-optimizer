using System.Globalization;
using System.Text;
using Google.OrTools.LinearSolver;
using HWChargeOptimizer.Calculations;
using HWChargeOptimizer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScottPlot;

namespace HWChargeOptimizer.HomeWizard;

public class HomeWizardScheduleService : BackgroundService
{
    
    private readonly ILogger<HomeWizardScheduleService> _logger;
    private readonly IOptionsMonitor<HWChargeOptimizerConfig> _config;
    private readonly IHomeWizardBatteryController _batteryController;
    private readonly ConfigWriter _configWriter;

    public HomeWizardScheduleService(
        ILogger<HomeWizardScheduleService> logger,
        IOptionsMonitor<HWChargeOptimizerConfig> config,
        IHomeWizardBatteryController batteryController,
        ConfigWriter configWriter)
    {
        _logger = logger;
        _config = config;
        _batteryController = batteryController;
        _configWriter = configWriter;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var timeZone = TimeZoneInfo.FindSystemTimeZoneById(Constants.SystemTimeZone);

                // retrieve the combined battery status from the battery controller and update the config
                await RetrieveAndStoreBatteriesStatusInConfigAsync();

                var currentHousePowerUsage = await _batteryController.GetLatestPowerMeasurementAsync();
                var socList = await RetrieveAndStoreBatteriesSoCAsync();

                // Cache config values
                var cfg = _config.CurrentValue;
                var batteryCfg = cfg.Homewizard.BatteryConfiguration;
                var p1 = cfg.Homewizard.P1;

                // Tariffs are hourly-based, so compare on hour boundaries only.
                var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
                var currentLocalDateTimeHour = TruncateToHour(localNow);

                _logger.LogDebug("Current local date time hour: {currentLocalDateTimeHour}", currentLocalDateTimeHour);

                // Filter tariffs, only retain those from current hour onwards (hour comparisons, not .Date)
                var tariffs = (cfg.Zonneplan.Tariffs ?? [])
                    .Where(t => TruncateToHour(TimeZoneInfo.ConvertTime(t.Date, timeZone)) >= currentLocalDateTimeHour)
                    .OrderBy(t => t.Date)
                    .ToList();

                if (tariffs.Count == 0)
                {
                    _logger.LogError("No present or future tariffs available in the Zonneplan tariff list. This should never happen.");
                    continue;
                }
                
                var combinedBatteryCapacity = batteryCfg.Batteries.Sum(s => s.CapacityKWh);
                var currentStateOfCharge = socList
                    .Select((soc, index) => soc.StateOfChargePercentage * batteryCfg.Batteries[index].CapacityKWh / 100.0)
                    .Sum();

                var maxChargingRate = batteryCfg.MaxChargeRateKWh;
                var maxDischargingRate = batteryCfg.MaxDischargeRateKWh;

                var chargingEfficiency = batteryCfg.ChargingEfficiency;
                var dischargingEfficiency = batteryCfg.DischargingEfficiency;

                var currentBatteryMode = p1.BatteryMode;

                // Correct way: pick the tariff for the current hour (or if missing, the next future one).
                var currentTariff = tariffs.FirstOrDefault(t =>
                        TruncateToHour(TimeZoneInfo.ConvertTime(t.Date, timeZone)) >= currentLocalDateTimeHour)
                    ?? tariffs.First();

                var lowestTariff = tariffs.Min(t => t.Price);
                var highestTariff = tariffs.Max(t => t.Price);

                _logger.LogInformation("-----------------------------------------------------------");
                _logger.LogInformation("Current battery mode:                         {currentBatteryMode}", currentBatteryMode);
                _logger.LogInformation("Total battery capacity:                       {combinedBatteryCapacity} kWh", combinedBatteryCapacity);
                _logger.LogInformation("Current state of charge (combined):           {currentStateOfCharge:F4} kWh", currentStateOfCharge);
                _logger.LogInformation("Current house power consumption / production: {currentPowerUsage} Watt", currentHousePowerUsage);
                _logger.LogInformation("Current tariff:                               {currentTariff:F4} / kWh", currentTariff.Price);
                _logger.LogInformation("Lowest available tariff:                      {lowestTariff:F4} / kWh", lowestTariff);
                _logger.LogInformation("Highest available tariff:                     {highestTariff:F4} / kWh", highestTariff);
                _logger.LogInformation("Charging efficiency:                          {chargingEfficiency} %", chargingEfficiency * 100);
                _logger.LogInformation("Discharging efficiency:                       {dischargingEfficiency} %", dischargingEfficiency * 100);
                _logger.LogInformation("-----------------------------------------------------------");

                _logger.LogInformation("Starting calculation of optimal charging schedule...");

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

                        var tariffLocalHour = TruncateToHour(TimeZoneInfo.ConvertTime(tariff.Date, timeZone));
                        if (tariffLocalHour == currentLocalDateTimeHour && chargingStatus == "Y")
                            startCharging = true;
                        if (tariffLocalHour == currentLocalDateTimeHour && dischargingStatus == "Y")
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
                        _logger.LogInformation("Setting battery to full charging mode.");
                        await SetBatteryModeAsync(BatteryMode.FullCharge);
                    }
                    else if (startDischarging)
                    {
                        _logger.LogInformation("Setting battery to discharge only mode.");
                        await SetBatteryModeAsync(BatteryMode.ZeroDischargeOnly);
                    }
                    else
                    {
                        _logger.LogInformation("Setting battery to standby mode.");
                        await SetBatteryModeAsync(BatteryMode.Standby);
                    }
                    
                    // create chart file
                    CreateBatterySchedulePlot(tariffs, scheduleVariables.ChargeAmount, scheduleVariables.DischargeAmount, scheduleVariables.StateOfCharge);
                }
                else
                {
                    _logger.LogWarning("No solution found. Setting battery to zero mode.");
                    await SetBatteryModeAsync(BatteryMode.Zero); // set to zero mode as a fallback
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("An error occurred while executing the HomeWizard schedule service: {message}", ex.ToString());
            }
            finally
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("HomeWizard schedule service is stopping, because cancellation was requested.");
                }
                else
                {
                    // wait for the configured refresh interval or until the next full hour, whichever is shorter
                    var refreshIntervalSeconds = _config.CurrentValue.Homewizard.RefreshIntervalSeconds;
                    var now = DateTime.Now;
                    var nextHour = now.AddHours(1).Date.AddHours(now.Hour + 1);
                    var secondsUntilNextHour = (int)(nextHour - now).TotalSeconds;
                    var waitSeconds = Math.Min(refreshIntervalSeconds, secondsUntilNextHour);

                    _logger.LogInformation("The HomeWizard schedule service will wait for {refreshInterval} seconds before the next execution.",
                        waitSeconds);

                    await Task.Delay(TimeSpan.FromSeconds(waitSeconds), stoppingToken);
                }
            }
        }
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, 0, 0, value.Offset);

    /// <summary>
    /// Sets the battery mode to the specified mode and updates the configuration.
    /// </summary>
    /// <param name="requestedBatteryMode">The battery mode to request</param>
    private async Task SetBatteryModeAsync(string requestedBatteryMode)
    {
        if (_config.CurrentValue.Homewizard.P1.BatteryMode != requestedBatteryMode)
        {
            await _batteryController.SetBatteryModeAsync(requestedBatteryMode);
            _config.CurrentValue.Homewizard.P1.BatteryMode = requestedBatteryMode;
            _config.CurrentValue.Homewizard.P1.LastUpdated = DateTimeOffset.UtcNow;
            await _configWriter.WriteAsync(_config.CurrentValue);
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
        var batteryResponse = await _batteryController.GetBatteriesStatusAsync();

        _logger.LogInformation("Battery: Mode={mode}, P={powerW}, MaxIn={maxConsumptionW}, MaxOut={maxProductionW}, Target={targetPowerW}",
            batteryResponse.Mode,
            batteryResponse.PowerW,
            batteryResponse.MaxConsumptionW,
            batteryResponse.MaxProductionW,
            batteryResponse.TargetPowerW);

        _config.CurrentValue.Homewizard.P1.BatteryMode = batteryResponse.Mode;
        _config.CurrentValue.Homewizard.P1.LastUpdated = currentDateTime;
        _config.CurrentValue.Homewizard.P1.PowerW = batteryResponse.PowerW;
        _config.CurrentValue.Homewizard.P1.MaxConsumptionW = batteryResponse.MaxConsumptionW;
        _config.CurrentValue.Homewizard.P1.MaxProductionW = batteryResponse.MaxProductionW;
        _config.CurrentValue.Homewizard.P1.TargetPowerW = batteryResponse.TargetPowerW;

        await _configWriter.WriteAsync(_config.CurrentValue);
    }
    
    /// <summary>
    /// Gets the current battery status from the battery controller and updates the configuration.
    /// </summary>
    /// <returns></returns>
    private async Task<List<BatteryStateOfCharge>> RetrieveAndStoreBatteriesSoCAsync()
    {
        var currentDateTime = DateTimeOffset.UtcNow;

        var socList = await _batteryController.GetBatteryStateOfChargeAsync();

        // log socList
        foreach (var soc in socList)
        {
            _logger.LogInformation("Battery '{BatteryName}' (IP: {BatteryIp}) state of charge: {StateOfCharge}%", soc.Name, soc.Ip, soc.StateOfChargePercentage);
        }

        foreach (var soc in socList)
        {
            var battery = _config.CurrentValue.Homewizard.BatteryConfiguration.Batteries.SingleOrDefault(b => b.Ip == soc.Ip);
            if (battery == null)
            {
                _logger.LogWarning("Battery with IP {ip} not found in config. Skipping SoC update.", soc.Ip);
                continue;
            }

            battery.StateOfChargePercentage = soc.StateOfChargePercentage;
            battery.LastUpdated = currentDateTime;
        }

        await _configWriter.WriteAsync(_config.CurrentValue);

        return socList;
    }
    
    private static void CreateBatterySchedulePlot(List<Tariff> tariffs,
        Dictionary<DateTimeOffset, Variable> chargeAmount,
        Dictionary<DateTimeOffset, Variable> dischargeAmount,
        Dictionary<DateTimeOffset, Variable> stateOfCharge)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(Constants.SystemTimeZone);

        // Do not mutate `tariffs` (they are used as dictionary keys elsewhere). Create a local-time copy for plotting.
        var plotTariffs = tariffs
            .Select(t => new Tariff { Date = TimeZoneInfo.ConvertTime(t.Date, timeZone), Price = t.Price })
            .ToList();

        // Create arrays for plotting
        var times = new double[plotTariffs.Count];
        var socValues = new double[plotTariffs.Count];
        var chargeValues = new double[plotTariffs.Count];
        var dischargeValues = new double[plotTariffs.Count];
        var tariffValues = new double[plotTariffs.Count];
        var timeLabels = new string[plotTariffs.Count];

        // Fill arrays with data
        for (var i = 0; i < plotTariffs.Count; i++)
        {
            times[i] = i;

            // Dictionaries are keyed by the original tariff DateTimeOffset values.
            var key = tariffs[i].Date;

            socValues[i] = stateOfCharge.TryGetValue(key, out var soc) ? soc.SolutionValue() : 0.0;
            chargeValues[i] = chargeAmount.TryGetValue(key, out var ch) ? ch.SolutionValue() : 0.0;
            dischargeValues[i] = dischargeAmount.TryGetValue(key, out var dis) ? dis.SolutionValue() : 0.0;

            tariffValues[i] = plotTariffs[i].Price;
            timeLabels[i] = plotTariffs[i].Date.ToString("HH", CultureInfo.InvariantCulture);
        }

        // Create plot
        var plot = new Plot();

        var fontLocation = Path.Combine(AppContext.BaseDirectory, "Fonts", "Roboto-VariableFont.ttf");
        if (!File.Exists(fontLocation))
            throw new InvalidOperationException($"Font file not found at {fontLocation}. Cannot create chart.");

        // Add a font file to use its typeface for fonts with a given name
        Fonts.AddFontFile(name: "Roboto", path: fontLocation);

        plot.Font.Set("Roboto");
        
        // change figure colors for dark mode
        plot.FigureBackground.Color = Color.FromHex("#181818");
        plot.DataBackground.Color = Color.FromHex("#1f1f1f");

        // change axis and grid colors for dark mode
        plot.Axes.Color(Color.FromHex("#d7d7d7"));
        plot.Grid.MajorLineColor = Color.FromHex("#404040");

        // change legend colors for dark mode
        plot.Legend.BackgroundColor = Color.FromHex("#404040").WithAlpha(0.7);
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


